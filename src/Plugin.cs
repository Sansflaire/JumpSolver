using System;
using System.Linq;
using System.Numerics;

using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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

    // ── Active puzzle ─────────────────────────────────────────────────────────
    private JumpPuzzle puzzle = new();

    // ── UI state ──────────────────────────────────────────────────────────────
    private bool   showWindow  = false;
    private bool   showDiag    = false;
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
                "  /js setstart    Mark current position as start\n" +
                "  /js gotostart   Walk character to start point\n" +
                "  /js rec         Start new recording (clears existing)\n" +
                "  /js seg         Append a new segment to the recording\n" +
                "  /js stop        Stop recording or playback\n" +
                "  /js play        Play back the recording from start",
        });
        CommandManager.AddHandler(CmdJs, new CommandInfo(OnCommand) { ShowInHelp = false });

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

            if (recorder.State == RecordState.Recording)
                recorder.Tick(dt, lp.Position, lp.Rotation,
                              mover.LastNaturalForward, mover.LastNaturalLeft);

            if (player.State != PlayState.Idle)
                player.Tick(dt, lp.Address, lp.Position);

            if (diag.IsActive)
            {
                float injFwd  = mover.Injecting ? mover.InjectedForward : mover.LastNaturalForward;
                float injLeft = mover.Injecting ? mover.InjectedLeft    : mover.LastNaturalLeft;
                diag.AddFrame(dt, lp.Position,
                    mover.LastNaturalForward, mover.LastNaturalLeft,
                    injFwd, injLeft,
                    mover.Injecting, player.LastJumpFired);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "JumpSolver: framework update exception.");
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private void OnCommand(string cmd, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":          showWindow = !showWindow; break;
            case "setstart":  CmdSetStart();            break;
            case "gotostart": CmdGotoStart();           break;
            case "rec":       CmdRec();                 break;
            case "seg":       CmdAddSegment();          break;
            case "stop":      CmdStop();                break;
            case "play":      CmdPlay();                break;
            default: ChatGui.Print($"[JumpSolver] Unknown: {args}"); break;
        }
    }

    private void CmdSetStart()
    {
        var lp = ObjectTable.LocalPlayer;
        if (lp == null) { ChatGui.Print("[JumpSolver] Not logged in."); return; }
        puzzle.Start = new StartPoint { Position = lp.Position, Facing = lp.Rotation };
        ChatGui.Print($"[JumpSolver] Start set at ({lp.Position.X:F1}, {lp.Position.Y:F1}, {lp.Position.Z:F1}).");
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
            ChatGui.Print($"[JumpSolver] {err}");
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    private void DrawUI()
    {
        DrawWorldMarker();

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
        bool isPlaying = player.State   == PlayState.Playing;
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
