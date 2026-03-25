using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Plugin.Ipc;
using Dalamud.Bindings.ImGui;


namespace JumpSolver;

public sealed class Plugin : IDalamudPlugin
{
    // ── Dalamud services ──────────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static ICommandManager        CommandManager  { get; private set; } = null!;
    [PluginService] internal static IChatGui               ChatGui         { get; private set; } = null!;
    [PluginService] internal static IObjectTable           ObjectTable     { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework       { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider   GameInterop     { get; private set; } = null!;
    [PluginService] internal static IGameGui               GameGui         { get; private set; } = null!;

    // ── Systems ───────────────────────────────────────────────────────────────
    private readonly MoveHook      mover;
    private readonly JumpRecorder  recorder;
    private readonly JumpPlayer    player;
    private readonly DiagCapture   diag;
    private readonly Configuration config;

    // ── Input monitoring ──────────────────────────────────────────────────────
    internal static InputMonitor InputMonitor { get; } = new();
    private ICallGateProvider<string>? ipcInputState;
    // Pre-serialized on the framework thread every tick — safe to read from any thread via IPC.
    private volatile string _lastInputJson = "{}";

    // ── Control injection timer ───────────────────────────────────────────────
    // Set by /js control — injection runs until this UTC-ms timestamp.
    private long controlUntilMs = 0L;

    // ── Active puzzle ─────────────────────────────────────────────────────────
    private JumpPuzzle puzzle = new();

    // ── UI state ──────────────────────────────────────────────────────────────
    private bool   showWindow  = false;
    private bool   showDiag    = false;
    private bool   showHud     = false;
    private int    loadIndex   = -1;
    private string saveName    = "";

    // Pending new segment — set when Stop is clicked during a recording that
    // should be appended as a new segment rather than replacing everything.
    private bool pendingNewSegment = false;

    // ── Commands ──────────────────────────────────────────────────────────────
    private const string CmdMain = "/jumpsolver";
    private const string CmdJs   = "/js";

    // ─────────────────────────────────────────────────────────────────────────

    public Plugin()
    {
        config   = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        mover    = new MoveHook(GameInterop, Log);
        recorder = new JumpRecorder(Log);
        player   = new JumpPlayer(mover, ObjectTable, ChatGui, Log);
        diag     = new DiagCapture(Log);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "JumpSolver — jump puzzle automation\n" +
                "  /js             Toggle window\n" +
                "  /js hud         Toggle input monitor overlay\n" +
                "  /js setstart    Mark current position as start\n" +
                "  /js gotostart   Walk character to start point\n" +
                "  /js rec         Start new recording (clears existing)\n" +
                "  /js seg         Append a new segment to the recording\n" +
                "  /js stop        Stop recording or playback\n" +
                "  /js play        Play back the recording from start\n" +
                "  /js control <fwd> <left> <turn> <ms>   Inject movement for N ms\n" +
                "  /js control stop   Stop injected movement immediately\n" +
                "  /js jump        Fire a single jump\n" +
                "  /js diag natural   Start diagnostic capture (natural input)\n" +
                "  /js diag play      Start diagnostic capture (playback input)\n" +
                "  /js diag stop      Stop diagnostic capture and write CSV",
        });
        CommandManager.AddHandler(CmdJs, new CommandInfo(OnCommand) { ShowInHelp = false });

        // Publish input state via IPC so ClaudeAccessXIV can read it.
        try
        {
            ipcInputState = PluginInterface.GetIpcProvider<string>("JumpSolver.GetInputState");
            ipcInputState.RegisterFunc(() => _lastInputJson);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "JumpSolver: IPC provider registration failed.");
        }

        Framework.Update                     += OnUpdate;
        PluginInterface.UiBuilder.Draw       += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += () => showWindow = true;

        Log.Info("JumpSolver loaded.");
    }

    public void Dispose()
    {
        if (recorder.State != RecordState.Idle) recorder.StopRecording();
        if (player.State   != PlayState.Idle)   player.Stop("unloaded");

        Framework.Update                     -= OnUpdate;
        PluginInterface.UiBuilder.Draw       -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= () => showWindow = true;

        CommandManager.RemoveHandler(CmdMain);
        CommandManager.RemoveHandler(CmdJs);

        try { ipcInputState?.UnregisterFunc(); } catch { }

        mover.Dispose();
    }

    // ── Framework update ──────────────────────────────────────────────────────

    private void OnUpdate(IFramework fw)
    {
        try
        {
            var lp = ObjectTable.LocalPlayer;
            if (lp == null) return;

            float dt = (float)fw.UpdateDelta.TotalMilliseconds;

            // Control timer — clear injection when /js control duration expires
            if (controlUntilMs > 0L && player.State == PlayState.Idle
                && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= controlUntilMs)
            {
                mover.ClearInjection();
                controlUntilMs = 0L;
            }

            if (player.State != PlayState.Idle)
                player.Tick(dt, lp.Address, lp.Position);

            // Input monitor runs before the recorder so KeySpace is fresh for jump detection.
            // Framework.Update fires BEFORE the RMI hook, so peaks here are from the previous
            // frame's RMI calls — accurate and one frame stale at most.
            InputMonitor.Update(dt, lp.Position, lp.Rotation, mover, player.LastJumpFired);
            _lastInputJson = JsonSerializer.Serialize(InputMonitor.Snapshot);

            if (recorder.State == RecordState.Recording)
            {
                var (camYaw, camPitch) = mover.GetCameraAngles();
                recorder.Tick(dt, lp.Position, lp.Rotation,
                              mover.PeakNaturalForward, mover.PeakNaturalLeft,
                              InputMonitor.KeySpace, camYaw, camPitch);
            }

            if (diag.IsActive)
            {
                float injFwd  = mover.Injecting ? mover.InjectedForward : mover.LastNaturalForward;
                float injLeft = mover.Injecting ? mover.InjectedLeft    : mover.LastNaturalLeft;
                diag.AddFrame(dt, lp.Position, lp.Rotation,
                    mover.PeakNaturalForward, mover.PeakNaturalLeft, mover.PeakNaturalTurnLeft,
                    injFwd, injLeft,
                    mover.Injecting, player.LastJumpFired);
            }

            mover.ResetNaturalPeaks();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "JumpSolver: framework update exception.");
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private void OnCommand(string cmd, string args)
    {
        var arg = args.Trim().ToLowerInvariant();
        switch (arg)
        {
            case "":               showWindow = !showWindow;         break;
            case "hud":            showHud    = !showHud;            break;
            case "setstart":       CmdSetStart();                    break;
            case "gotostart":      CmdGotoStart();                   break;
            case "rec":            CmdRec();                         break;
            case "seg":            CmdAddSegment();                  break;
            case "stop":           CmdStop();                        break;
            case "play":           CmdPlay();                        break;
            case "jump":           CmdJump();                        break;
            case "control stop":   mover.ClearInjection();
                                   controlUntilMs = 0L;              break;
            case "diag natural":   CmdDiag("natural");               break;
            case "diag play":      CmdDiag("play");                  break;
            case "diag stop":      CmdDiagStop();                    break;
            default:
                if (arg.StartsWith("control ")) CmdControl(args.Trim()[8..]);
                else ChatGui.Print($"[JumpSolver] Unknown: {args}");
                break;
        }
    }

    private void CmdSetStart()
    {
        var lp = ObjectTable.LocalPlayer;
        if (lp == null) { ChatGui.Print("[JumpSolver] Not logged in."); return; }
        var (yaw, pitch) = mover.GetCameraAngles();
        puzzle.Start = new StartPoint
        {
            Position    = lp.Position,
            Facing      = lp.Rotation,
            CameraYaw   = yaw,
            CameraPitch = pitch,
        };
        ChatGui.Print($"[JumpSolver] Start set at ({lp.Position.X:F1}, {lp.Position.Y:F1}, {lp.Position.Z:F1}), cam yaw={yaw:F2}.");
    }


    private void CmdGotoStart()
    {
        if (!player.TryWalkToStart(puzzle, out var err))
            ChatGui.Print($"[JumpSolver] {err}");
    }

    private void CmdRec()
    {
        if (player.State != PlayState.Idle) { ChatGui.Print("[JumpSolver] Stop playback first."); return; }
        if (recorder.State != RecordState.Idle) { ChatGui.Print("[JumpSolver] Already recording."); return; }
        var lp = ObjectTable.LocalPlayer;
        if (lp == null) { ChatGui.Print("[JumpSolver] Not logged in."); return; }

        puzzle.Start = new StartPoint { Position = lp.Position, Facing = lp.Rotation };
        puzzle.Segments.Clear();
        pendingNewSegment = false;
        recorder.StartRecording(lp.Position, lp.Rotation);
        ChatGui.Print("[JumpSolver] Recording — do your run, then Stop.");
    }

    private void CmdAddSegment()
    {
        if (player.State != PlayState.Idle) { ChatGui.Print("[JumpSolver] Stop playback first."); return; }
        if (recorder.State != RecordState.Idle) { ChatGui.Print("[JumpSolver] Already recording."); return; }
        if (!puzzle.HasRecording) { ChatGui.Print("[JumpSolver] Record a first segment first."); return; }
        var lp = ObjectTable.LocalPlayer;
        if (lp == null) { ChatGui.Print("[JumpSolver] Not logged in."); return; }

        pendingNewSegment = true;
        recorder.ClearAll();
        recorder.StartRecording(lp.Position, lp.Rotation);
        ChatGui.Print($"[JumpSolver] Recording segment {puzzle.Segments.Count + 1} — do the next section, then Stop.");
    }

    private void CmdStop()
    {
        if (recorder.State == RecordState.Recording)
        {
            recorder.StopRecording();
            if (pendingNewSegment)
            {
                int num = puzzle.Segments.Count + 1;
                puzzle.Segments.Add(new PuzzleSegment
                    { Name = $"Segment {num}", Frames = recorder.CapturedFrames.ToList() });
                ChatGui.Print($"[JumpSolver] Segment {num} saved — {recorder.CapturedFrames.Count} frames, {recorder.CapturedFrames.Count(f => f.Jump)} jump(s).");
            }
            else
            {
                puzzle.Segments.Clear();
                puzzle.Segments.Add(new PuzzleSegment
                    { Name = "Segment 1", Frames = recorder.CapturedFrames.ToList() });
                ChatGui.Print($"[JumpSolver] Saved — {recorder.CapturedFrames.Count} frames, {recorder.CapturedFrames.Count(f => f.Jump)} jump(s).");
            }
            pendingNewSegment = false;
            return;
        }

        if (player.State != PlayState.Idle)
        {
            player.Stop("aborted");
            ChatGui.Print("[JumpSolver] Stopped.");
        }
    }

    private void CmdPlay()
    {
        if (recorder.State != RecordState.Idle) { ChatGui.Print("[JumpSolver] Stop recording first."); return; }
        if (!player.TryStart(puzzle, out var err))
        {
            ChatGui.Print($"[JumpSolver] {err}");
            return;
        }
        // Snap camera to the angle it was at when the start point was recorded.
        if (puzzle.Start != null)
            mover.SetCameraAngles(puzzle.Start.CameraYaw, puzzle.Start.CameraPitch);
    }

    private void CmdDiag(string label)
    {
        if (diag.IsActive) diag.Stop();
        diag.Start(label);
        ChatGui.Print($"[JumpSolver] Diag capture started ({label}). Do your run, then /js diag stop.");
    }

    private void CmdDiagStop()
    {
        if (!diag.IsActive) { ChatGui.Print("[JumpSolver] Diag not running."); return; }
        string path = diag.Stop();
        ChatGui.Print(string.IsNullOrEmpty(path)
            ? "[JumpSolver] Diag: no frames captured."
            : $"[JumpSolver] Diag saved: {path}");
    }

    private void CmdControl(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            ChatGui.Print("[JumpSolver] Usage: /js control <fwd> <left> <turn> <ms>  (all floats)");
            return;
        }
        if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float fwd)  ||
            !float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float left) ||
            !float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float turn) ||
            !float.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float ms))
        {
            ChatGui.Print("[JumpSolver] /js control: all arguments must be numbers.");
            return;
        }

        mover.InjectedForward  = Math.Clamp(fwd,  -1f, 1f);
        mover.InjectedLeft     = Math.Clamp(left, -1f, 1f);
        mover.InjectedTurnLeft = Math.Clamp(turn, -1f, 1f);
        controlUntilMs         = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)ms;
        Log.Info($"JumpSolver: control injected — fwd={fwd:F2} left={left:F2} turn={turn:F2} for {ms:F0}ms.");
    }

    private static unsafe void CmdJump()
    {
        try
        {
            var am = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
            if (am == null) return;
            am->UseAction((FFXIVClientStructs.FFXIV.Client.Game.ActionType)5,
                          Signatures.JumpActionId, 0xE0000000);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "JumpSolver: /js jump exception.");
        }
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    private void DrawUI()
    {
        DrawWorldMarker();
        if (showHud) DrawInputHud();

        if (!showWindow) return;

        ImGui.SetNextWindowSize(new Vector2(480, 500), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("JumpSolver", ref showWindow)) { ImGui.End(); return; }

        DrawHeader();
        ImGui.Separator();
        DrawStartSection();
        ImGui.Separator();
        DrawControls();
        ImGui.Separator();
        DrawSegmentList();
        ImGui.Separator();
        if (showDiag) DrawDiagSection();
        DrawSaveLoadSection();

        ImGui.End();
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        ImGui.TextColored(new Vector4(0.18f, 0.80f, 0.44f, 1f), "JumpSolver");
        ImGui.SameLine();
        var (txt, col) = GetStateLabel();
        ImGui.TextColored(col, $"[{txt}]");

        if (!mover.IsAvailable)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), "hook unavailable");
        }

        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 35f);
        bool d = showDiag;
        if (ImGui.Checkbox("diag", ref d)) showDiag = d;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show diagnostic capture panel");

        var name = puzzle.Name;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputText("##pname", ref name, 80)) puzzle.Name = name;
    }

    private (string, Vector4) GetStateLabel()
    {
        if (player.State == PlayState.Playing)
            return ($"PLAYING {player.FrameIndex}/{player.PlaybackFrameCount}",
                    new Vector4(0.3f, 0.8f, 1f, 1f));
        if (player.State == PlayState.WpWaitLand)
            return ("IN AIR", new Vector4(0.6f, 0.9f, 1f, 1f));
        if (player.State == PlayState.WpNav)
            return ("MOVING TO NEXT", new Vector4(0.3f, 0.8f, 1f, 1f));
        if (player.State == PlayState.WalkingToStart)
            return ("WALKING", new Vector4(1f, 0.85f, 0.2f, 1f));
        if (recorder.State == RecordState.Recording)
            return ($"REC  {recorder.CapturedFrames.Count} frames", new Vector4(1f, 0.3f, 0.3f, 1f));
        return ("idle", new Vector4(0.5f, 0.5f, 0.5f, 1f));
    }

    // ── Start section ─────────────────────────────────────────────────────────

    private void DrawStartSection()
    {
        ImGui.Text("Start Point");
        ImGui.SameLine();

        bool idle = player.State == PlayState.Idle && recorder.State == RecordState.Idle;
        if (!idle) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Set Here")) CmdSetStart();
        ImGui.SameLine();
        bool canGo = puzzle.Start != null && idle && mover.IsAvailable;
        if (!canGo) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Go To Start")) CmdGotoStart();
        if (!canGo) ImGui.EndDisabled();
        if (!idle) ImGui.EndDisabled();

        if (puzzle.Start != null)
        {
            var  s    = puzzle.Start;
            var  lp   = ObjectTable.LocalPlayer;
            float dist = lp != null ? Vector3.Distance(lp.Position, s.Position) : float.MaxValue;

            var col = dist < 0.5f        ? new Vector4(0.3f, 1f, 0.3f, 1f)
                    : dist < s.SnapRadius ? new Vector4(1f, 1f, 0.3f, 1f)
                    :                       new Vector4(0.65f, 0.65f, 0.65f, 1f);

            ImGui.TextColored(col,
                $"  ({s.Position.X:F1}, {s.Position.Y:F1}, {s.Position.Z:F1})  " +
                $"facing {s.Facing:F2}  dist {dist:F1}y");

            float snap = s.SnapRadius;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("snap##snap", ref snap, 1f, 30f)) s.SnapRadius = snap;

            if (player.State == PlayState.WalkingToStart)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Stop Walking")) player.Stop("user cancelled walk");
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.65f, 0.2f, 1f), "  Not set.");
        }
    }

    // ── Controls ──────────────────────────────────────────────────────────────

    private void DrawControls()
    {
        ImGui.Spacing();
        bool isRec     = recorder.State == RecordState.Recording;
        bool isPlaying = player.State   == PlayState.Playing
                      || player.State   == PlayState.WpWaitLand
                      || player.State   == PlayState.WpNav;
        bool isWalking = player.State   == PlayState.WalkingToStart;

        if (isRec)
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f),
                $"● REC  {recorder.CapturedFrames.Count} frames");
            ImGui.SameLine();
            if (ImGui.Button("Stop##stoprec")) CmdStop();
        }
        else if (isPlaying)
        {
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f),
                $"PLAYING  {player.FrameIndex} / {player.PlaybackFrameCount}");
            ImGui.SameLine();
            if (ImGui.Button("Stop##stopplay")) CmdStop();
            ImGui.SameLine();

            // Trim at current frame
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.6f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.85f, 0.3f, 0.3f, 1f));
            if (ImGui.Button("Trim here"))
            {
                int trimAt = Math.Max(1, player.FrameIndex);
                player.Stop("trimmed");
                var trimmed = puzzle.Segments.SelectMany(s => s.Frames).Take(trimAt).ToList();
                puzzle.Segments.Clear();
                puzzle.Segments.Add(new PuzzleSegment { Name = "Segment 1", Frames = trimmed });
                ChatGui.Print($"[JumpSolver] Trimmed to {trimmed.Count} frames.");
            }
            ImGui.PopStyleColor(2);
        }
        else if (!isWalking)
        {
            bool hookOk = mover.IsAvailable;
            if (!hookOk) ImGui.BeginDisabled();

            if (ImGui.Button("Rec")) CmdRec();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Start a fresh recording (clears existing). Stand at start, then run and jump.");

            ImGui.SameLine();
            bool canSeg = puzzle.HasRecording;
            if (!canSeg) ImGui.BeginDisabled();
            if (ImGui.Button("+ Seg")) CmdAddSegment();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Record an additional segment appended to the end");
            if (!canSeg) ImGui.EndDisabled();

            ImGui.SameLine();
            bool canPlay = puzzle.Start != null && puzzle.HasRecording;
            if (!canPlay) ImGui.BeginDisabled();
            if (ImGui.Button("Play")) CmdPlay();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play back from start point (must be standing there first)");
            if (!canPlay) ImGui.EndDisabled();

            if (!hookOk) ImGui.EndDisabled();
        }
        ImGui.Spacing();
    }

    // ── Segment list ──────────────────────────────────────────────────────────

    private void DrawSegmentList()
    {
        if (puzzle.Segments.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                "No recording yet. Press Rec and do your puzzle run.");
            return;
        }

        ImGui.Text($"Recording  ({puzzle.TotalFrameCount} frames total, {puzzle.TotalJumpCount} jump(s))");

        ImGui.BeginChild("##segs", new Vector2(0, 90), true);
        for (int i = 0; i < puzzle.Segments.Count; i++)
        {
            var seg = puzzle.Segments[i];
            ImGui.PushID(i);

            bool playing = player.State == PlayState.Playing;
            int  offset  = puzzle.Segments.Take(i).Sum(s => s.Frames.Count);
            bool active  = playing && player.FrameIndex >= offset
                                   && player.FrameIndex < offset + seg.Frames.Count;

            var col = active ? new Vector4(0.3f, 1f, 0.3f, 1f)
                             : new Vector4(0.75f, 0.75f, 0.75f, 1f);
            ImGui.TextColored(col,
                $"{i + 1}. {seg.Name}   {seg.Frames.Count} frames   {seg.JumpCount} jump(s)");

            ImGui.SameLine();
            if (recorder.State == RecordState.Idle && player.State == PlayState.Idle)
            {
                if (ImGui.SmallButton("Del##d"))
                {
                    puzzle.Segments.RemoveAt(i);
                    ImGui.PopID();
                    break;
                }
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    // ── Diag section ──────────────────────────────────────────────────────────

    private void DrawDiagSection()
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Diagnostic Capture");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Captures raw RMI input + position every frame → writes CSV.\n" +
                "Natural: capture your own keyboard input.\n" +
                "Playback: capture what the plugin injects.\n" +
                "Files go to: XIVLauncher\\pluginConfigs\\");

        if (!diag.IsActive)
        {
            if (ImGui.SmallButton("Natural")) diag.Start("natural");
            ImGui.SameLine();
            if (ImGui.SmallButton("Playback")) diag.Start("playback");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "● CAPTURING");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##diagstop"))
            {
                string path = diag.Stop();
                ChatGui.Print(string.IsNullOrEmpty(path)
                    ? "[JumpSolver] Diag: no frames captured."
                    : $"[JumpSolver] Diag saved: {path}");
            }
        }
        ImGui.Spacing();
    }

    // ── Save / Load ───────────────────────────────────────────────────────────

    private void DrawSaveLoadSection()
    {
        ImGui.Text("Puzzles");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##savename", ref saveName, 80);
        ImGui.SameLine();
        if (ImGui.Button("Save"))
        {
            if (string.IsNullOrWhiteSpace(saveName)) saveName = puzzle.Name;
            SavePuzzle();
        }

        ImGui.Spacing();

        if (config.SavedPuzzles.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "No saved puzzles.");
            return;
        }

        ImGui.BeginChild("##savedlist", new Vector2(0, 100), true);
        for (int i = 0; i < config.SavedPuzzles.Count; i++)
        {
            var  p   = config.SavedPuzzles[i];
            bool sel = i == loadIndex;

            ImGui.PushID(10000 + i);
            if (ImGui.SmallButton("Load")) { LoadPuzzle(i); loadIndex = i; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Del")) { DeleteSavedPuzzle(i); ImGui.PopID(); break; }
            ImGui.PopID();

            ImGui.SameLine();
            if (ImGui.Selectable(
                $"{p.Name}  ({p.TotalFrameCount} frames, {p.Segments.Count} seg(s))##sl{i}",
                sel, ImGuiSelectableFlags.None, new Vector2(0, 0)))
                loadIndex = i;
        }
        ImGui.EndChild();
    }

    private void SavePuzzle()
    {
        string name = string.IsNullOrWhiteSpace(saveName) ? puzzle.Name : saveName;
        puzzle.Name = name;
        int idx = config.SavedPuzzles.FindIndex(p => p.Name == name);
        if (idx >= 0) config.SavedPuzzles[idx] = puzzle.DeepCopy();
        else          config.SavedPuzzles.Add(puzzle.DeepCopy());
        PluginInterface.SavePluginConfig(config);
        ChatGui.Print($"[JumpSolver] Saved '{name}'.");
    }

    private void LoadPuzzle(int idx)
    {
        if (idx < 0 || idx >= config.SavedPuzzles.Count) return;
        puzzle = config.SavedPuzzles[idx].DeepCopy();
        ChatGui.Print($"[JumpSolver] Loaded '{puzzle.Name}'.");
    }

    private void DeleteSavedPuzzle(int idx)
    {
        if (idx < 0 || idx >= config.SavedPuzzles.Count) return;
        string name = config.SavedPuzzles[idx].Name;
        config.SavedPuzzles.RemoveAt(idx);
        PluginInterface.SavePluginConfig(config);
        if (loadIndex >= config.SavedPuzzles.Count) loadIndex = config.SavedPuzzles.Count - 1;
        ChatGui.Print($"[JumpSolver] Deleted '{name}'.");
    }

    // ── Input HUD overlay ─────────────────────────────────────────────────────

    private void DrawInputHud()
    {
        var m = InputMonitor;

        ImGui.SetNextWindowSize(new Vector2(270, 0), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(10, 200), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.82f);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.05f, 0.05f, 0.10f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(4f, 3f));

        bool open = showHud;
        if (!ImGui.Begin("Input Monitor##jshud", ref open,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
            ImGui.End();
            showHud = open;
            return;
        }
        showHud = open;
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        // ── Header ────────────────────────────────────────────────────────────
        ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), "INPUT MONITOR");
        if (m.Injecting)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.1f, 1f), "[INJECTING]");
        }

        ImGui.Separator();

        // ── RMI float bars ────────────────────────────────────────────────────
        float activeForward  = m.Injecting ? m.InjForward  : m.NatForward;
        float activeLeft     = m.Injecting ? m.InjLeft     : m.NatLeft;
        float activeTurnLeft = m.Injecting ? m.InjTurnLeft : m.NatTurnLeft;

        DrawInputBar("Fwd  ", activeForward,  new Vector4(0.2f, 0.9f, 0.4f, 1f));
        DrawInputBar("Left ", activeLeft,     new Vector4(0.9f, 0.75f, 0.2f, 1f));
        DrawInputBar("Turn ", activeTurnLeft, new Vector4(0.7f, 0.4f, 0.95f, 1f));

        // Show both nat and inj if injecting
        if (m.Injecting)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                $"  nat: fwd={m.NatForward:F2} left={m.NatLeft:F2} turn={m.NatTurnLeft:F2}");
        }

        ImGui.Separator();

        // ── Raw key state ─────────────────────────────────────────────────────
        ImGui.Text("Keys ");
        ImGui.SameLine();
        DrawKeyDot("W", m.KeyW);  ImGui.SameLine();
        DrawKeyDot("A", m.KeyA);  ImGui.SameLine();
        DrawKeyDot("S", m.KeyS);  ImGui.SameLine();
        DrawKeyDot("D", m.KeyD);  ImGui.SameLine();
        DrawKeyDot("Q", m.KeyQ);  ImGui.SameLine();
        DrawKeyDot("E", m.KeyE);  ImGui.SameLine();
        DrawKeyDot("SPC", m.KeySpace);

        ImGui.Text("Mouse");
        ImGui.SameLine();
        DrawKeyDot("LMB", m.LMB); ImGui.SameLine();
        DrawKeyDot("RMB", m.RMB); ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.65f, 1f),
            $"  dX:{m.MouseDeltaX:+0;-0;+0}  dY:{m.MouseDeltaY:+0;-0;+0}");

        ImGui.Separator();

        // ── Character state ───────────────────────────────────────────────────
        float deg = m.CharFacing * (180f / MathF.PI);
        ImGui.Text($"Facing  {deg:F1}°");
        ImGui.SameLine(130);
        ImGui.Text($"Speed  {m.SpeedXZ:F2} y/s");

        ImGui.Text($"Pos  ({m.Position.X:F2}, {m.Position.Y:F2}, {m.Position.Z:F2})");

        if (m.JumpFired)
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "● JUMP FIRED");

        ImGui.End();
    }

    private static void DrawInputBar(string label, float value, Vector4 color)
    {
        const float BarW = 140f;

        ImGui.Text(label);
        ImGui.SameLine();

        var  dl  = ImGui.GetWindowDrawList();
        var  pos = ImGui.GetCursorScreenPos();
        float h  = ImGui.GetTextLineHeight();

        // Background track
        dl.AddRectFilled(pos, pos + new Vector2(BarW, h),
            ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.18f, 1f)));

        // Center line
        float cx = BarW * 0.5f;
        dl.AddLine(pos + new Vector2(cx, 0), pos + new Vector2(cx, h),
            ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.45f, 0.9f)), 1.5f);

        // Filled portion (center to value)
        float norm    = (value + 1f) * 0.5f;   // map -1..1 → 0..1
        float barFrom = MathF.Min(cx, norm * BarW);
        float barTo   = MathF.Max(cx, norm * BarW);
        if (barTo > barFrom + 0.5f)
            dl.AddRectFilled(pos + new Vector2(barFrom, 1f), pos + new Vector2(barTo, h - 1f),
                ImGui.GetColorU32(color));

        ImGui.Dummy(new Vector2(BarW, h));
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1f), $"{value:F2}");
    }

    private static void DrawKeyDot(string label, bool down)
    {
        var col = down
            ? new Vector4(0.25f, 1f, 0.25f, 1f)
            : new Vector4(0.28f, 0.28f, 0.30f, 1f);
        ImGui.TextColored(col, label);
    }

    // ── World-space markers ───────────────────────────────────────────────────

    private void DrawWorldMarker()
    {
        if (puzzle.Start == null) return;

        var  dl = ImGui.GetForegroundDrawList();
        var  lp = ObjectTable.LocalPlayer;
        var  s  = puzzle.Start;

        float dist = lp != null ? Vector3.Distance(lp.Position, s.Position) : float.MaxValue;
        bool onPoint      = dist < 0.35f;
        bool inRange      = dist <= s.SnapRadius;
        bool correctFacing = onPoint && lp != null
                          && MathF.Abs(AngleDiff(lp.Rotation, s.Facing)) < 0.15f;

        uint colFill = correctFacing ? Color(0.1f, 1f,  0.1f, 0.85f)
                     : inRange       ? Color(1f,   1f,  0.1f, 0.85f)
                     :                 Color(1f,  0.2f, 0.2f, 0.85f);
        uint colRing = correctFacing ? Color(0.1f, 1f,  0.1f, 0.5f)
                     : inRange       ? Color(1f,   1f,  0.1f, 0.4f)
                     :                 Color(1f,  0.2f, 0.2f, 0.3f);

        if (GameGui.WorldToScreen(s.Position, out var sc))
        {
            dl.AddCircleFilled(sc, 7f, colFill);
            dl.AddCircle(sc, 7f, Color(0f, 0f, 0f, 0.6f), 16, 1.5f);
            var dir = new Vector3(MathF.Sin(s.Facing), 0f, MathF.Cos(s.Facing));
            if (GameGui.WorldToScreen(s.Position + dir * 1.5f, out var tip))
                dl.AddLine(sc, tip, colFill, 2f);
        }

        // Snap radius ring
        const int Seg = 32;
        Vector2?  prev = null;
        for (int i = 0; i <= Seg; i++)
        {
            float ang = i * MathF.PI * 2f / Seg;
            var   wp  = s.Position + new Vector3(MathF.Sin(ang) * s.SnapRadius, 0f,
                                                  MathF.Cos(ang) * s.SnapRadius);
            if (GameGui.WorldToScreen(wp, out var sp))
            { if (prev.HasValue) dl.AddLine(prev.Value, sp, colRing, 1.5f); prev = sp; }
            else prev = null;
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static uint Color(float r, float g, float b, float a)
        => ImGui.GetColorU32(new Vector4(r, g, b, a));

    private static float AngleDiff(float a, float b)
    {
        float d = a - b;
        while (d >  MathF.PI) d -= MathF.PI * 2f;
        while (d < -MathF.PI) d += MathF.PI * 2f;
        return d;
    }
}
