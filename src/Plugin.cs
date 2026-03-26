using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Plugin.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;


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
    [PluginService] internal static ITextureProvider       TextureProvider { get; private set; } = null!;

    // ── Systems ───────────────────────────────────────────────────────────────
    private readonly MoveHook           mover;
    private readonly JumpRecorder       recorder;
    private readonly JumpPlayer         player;
    private readonly DiagCapture        diag;
    private readonly Configuration      config;
    private readonly JumpSolverWindow   mainWindow;
    private readonly FrameEditorWindow  editorWindow;

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

    // ── UI state (legacy — kept for /js hud command) ──────────────────────────
    private bool showHud = false;

    // Pending new segment — set when Stop is clicked during a recording that
    // should be appended as a new segment rather than replacing everything.
    private bool pendingNewSegment = false;

    // ── Commands ──────────────────────────────────────────────────────────────
    private const string CmdMain = "/jumpsolver";
    private const string CmdJs   = "/js";

    // ─────────────────────────────────────────────────────────────────────────

    public Plugin()
    {
        config       = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        mover        = new MoveHook(GameInterop, Log);
        recorder     = new JumpRecorder(Log);
        player       = new JumpPlayer(mover, ObjectTable, ChatGui, Log);
        diag         = new DiagCapture(Log);
        editorWindow = new FrameEditorWindow(TextureProvider)
        {
            OnSave     = SavePuzzle,
            OnTestJump = idx =>
            {
                if (!player.TryTestJump(puzzle, idx, out var err))
                    ChatGui.Print($"[JumpSolver] {err}");
            },
            OnReRecordJump = idx =>
            {
                // Set up the splice callback, then navigate to the jump's run-up start.
                // When navigation arrives, recording starts automatically.
                // When the re-recorded jump lands, OnSingleJumpComplete splices + saves.
                recorder.OnSingleJumpComplete = newFrames =>
                {
                    SpliceJumpFrames(puzzle, idx, newFrames);
                    SavePuzzle();
                    ChatGui.Print($"[JumpSolver] Jump {idx + 1} re-recorded and saved.");
                };
                if (!player.TryNavigateToJumpStart(puzzle, idx, () =>
                {
                    recorder.StartSingleJumpRecording();
                    ChatGui.Print($"[JumpSolver] At Jump {idx + 1} run-up — perform the jump now.");
                }, out var err))
                {
                    recorder.OnSingleJumpComplete = null;
                    ChatGui.Print($"[JumpSolver] {err}");
                }
            },
        };
        mainWindow = new JumpSolverWindow(TextureProvider, player, recorder, mover, diag, ObjectTable)
        {
            OnSetStart  = CmdSetStart,
            OnGoToStart = CmdGotoStart,
            OnRec       = CmdRec,
            OnAddSeg    = CmdAddSegment,
            OnPlay      = CmdPlay,
            OnStop      = CmdStop,
            OnTrim      = frameIdx =>
            {
                int trimAt = Math.Max(1, frameIdx);
                player.Stop("trimmed");
                var trimmed = puzzle.Segments.SelectMany(s => s.Frames).Take(trimAt).ToList();
                puzzle.Segments.Clear();
                puzzle.Segments.Add(new PuzzleSegment { Name = "Segment 1", Frames = trimmed });
                ChatGui.Print($"[JumpSolver] Trimmed to {trimmed.Count} frames.");
            },
            OnSave   = name =>
            {
                if (!string.IsNullOrWhiteSpace(name)) puzzle.Name = name;
                SavePuzzle();
            },
            OnLoad   = LoadPuzzle,
            OnDelete = DeleteSavedPuzzle,
        };

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
        PluginInterface.UiBuilder.OpenMainUi += () => mainWindow.IsVisible = true;

        Log.Info("JumpSolver loaded.");
    }

    public void Dispose()
    {
        if (recorder.State != RecordState.Idle) recorder.StopRecording();
        if (player.State   != PlayState.Idle)   player.Stop("unloaded");

        Framework.Update                     -= OnUpdate;
        PluginInterface.UiBuilder.Draw       -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= () => mainWindow.IsVisible = true;

        CommandManager.RemoveHandler(CmdMain);
        CommandManager.RemoveHandler(CmdJs);

        try { ipcInputState?.UnregisterFunc(); } catch { }

        mainWindow.Dispose();
        editorWindow.Dispose();
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
            case "":               mainWindow.IsVisible = !mainWindow.IsVisible; break;
            case "hud":            showHud = !showHud;               break;
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
            bool wasSingleJump = recorder.IsSingleJumpMode;
            recorder.StopRecording();

            if (wasSingleJump)
            {
                // Re-record was cancelled manually — don't splice partial frames.
                recorder.OnSingleJumpComplete = null;
                ChatGui.Print("[JumpSolver] Re-record cancelled.");
                return;
            }

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

    /// <summary>
    /// Replace the frames that belong to jump <paramref name="jumpIndex"/> (from the
    /// frame after the previous jump up to and including the jump frame itself) with
    /// <paramref name="newFrames"/>. All other jumps are kept intact.
    /// </summary>
    private static void SpliceJumpFrames(JumpPuzzle puzzle, int jumpIndex,
                                          List<RecordedFrame> newFrames)
    {
        var allFrames = puzzle.Segments.SelectMany(s => s.Frames).ToList();
        var jumpIdxs  = allFrames.Select((f, i) => (f, i))
                                 .Where(x => x.f.Jump)
                                 .Select(x => x.i)
                                 .ToList();
        if (jumpIndex < 0 || jumpIndex >= jumpIdxs.Count) return;

        int jumpFrameIdx = jumpIdxs[jumpIndex];
        int prevEnd      = jumpIndex > 0 ? jumpIdxs[jumpIndex - 1] + 1 : 0;

        var result = new List<RecordedFrame>();
        result.AddRange(allFrames.Take(prevEnd));
        result.AddRange(newFrames);
        result.AddRange(allFrames.Skip(jumpFrameIdx + 1));

        puzzle.Segments.Clear();
        puzzle.Segments.Add(new PuzzleSegment { Name = "Segment 1", Frames = result });
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
        mainWindow.Draw(puzzle, config);
        // Open editor when Edit Frames is clicked
        if (mainWindow.ShowEditor && puzzle.HasRecording && !editorWindow.IsVisible)
            editorWindow.IsVisible = true;
        bool isIdle = player.State == PlayState.Idle && recorder.State == RecordState.Idle;
        editorWindow.Draw(puzzle, isIdle);
        // Sync: if editor X button was clicked, reset ShowEditor so re-clicking Edit Frames works
        if (!editorWindow.IsVisible)
            mainWindow.ShowEditor = false;
    }

    private void SavePuzzle()
    {
        string name = puzzle.Name;
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
