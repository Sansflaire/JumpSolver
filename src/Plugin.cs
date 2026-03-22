using System;
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
    [PluginService] internal static IClientState           ClientState     { get; private set; } = null!;
    [PluginService] internal static IObjectTable           ObjectTable     { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework       { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider   GameInterop     { get; private set; } = null!;
    [PluginService] internal static IGameGui               GameGui         { get; private set; } = null!;

    // ── Systems ───────────────────────────────────────────────────────────────
    private readonly MoveHook     mover;
    private readonly JumpRecorder recorder;
    private readonly JumpPlayer   player;
    private readonly Configuration config;

    // ── Active puzzle ─────────────────────────────────────────────────────────
    private JumpPuzzle puzzle = new();

    // ── UI ────────────────────────────────────────────────────────────────────
    private bool showWindow   = false;
    private int  selectedStep = -1;

    // Save/load panel
    private int    loadIndex   = -1;
    private string saveName    = "";

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

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "JumpSolver:\n" +
                "  /js              – Toggle window\n" +
                "  /js setstart     – Mark current position as start\n" +
                "  /js gotostart    – Walk to start point\n" +
                "  /js record       – Start recording\n" +
                "  /js stop         – Stop recording / abort playback\n" +
                "  /js play         – Run from start point",
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

            if (recorder.State != RecordState.Idle)
                recorder.Tick(dt, lp.Position, lp.Rotation);

            if (player.State != PlayState.Idle)
                player.Tick(dt, lp.Address, lp.Position);
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
            case "":           showWindow = !showWindow;  break;
            case "setstart":   CmdSetStart();             break;
            case "gotostart":  CmdGotoStart();            break;
            case "record":     CmdRecord();               break;
            case "stop":       CmdStop();                 break;
            case "play":       CmdPlay();                 break;
            default: ChatGui.Print($"[JumpSolver] Unknown: {args}"); break;
        }
    }

    private void CmdSetStart()
    {
        var p = ObjectTable.LocalPlayer;
        if (p == null) { ChatGui.Print("[JumpSolver] Not logged in."); return; }
        puzzle.Start = new StartPoint { Position = p.Position, Facing = p.Rotation };
        ChatGui.Print($"[JumpSolver] Start set at ({p.Position.X:F1}, {p.Position.Y:F1}, {p.Position.Z:F1}).");
    }

    private void CmdGotoStart()
    {
        if (player.TryWalkToStart(puzzle, out var err)) return;
        ChatGui.Print($"[JumpSolver] {err}");
    }

    private void CmdRecord()
    {
        if (player.State != PlayState.Idle) { ChatGui.Print("[JumpSolver] Stop playback first."); return; }
        var p = ObjectTable.LocalPlayer;
        if (p == null) { ChatGui.Print("[JumpSolver] Not logged in."); return; }

        recorder.StartRecording(p.Position, p.Rotation);
        puzzle.Start = recorder.CapturedStart;
        puzzle.Steps.Clear();
        ChatGui.Print("[JumpSolver] Recording — walk your puzzle. /js stop when done.");
    }

    private void CmdStop()
    {
        if (recorder.State != RecordState.Idle)
        {
            recorder.StopRecording();
            puzzle.Steps.Clear();
            puzzle.Steps.AddRange(recorder.CompletedSteps);
            ChatGui.Print($"[JumpSolver] Recorded {puzzle.Steps.Count} steps.");
        }
        else if (player.State != PlayState.Idle)
        {
            player.Stop("aborted");
            ChatGui.Print("[JumpSolver] Stopped.");
        }
    }

    private void CmdPlay()
    {
        if (recorder.State != RecordState.Idle) { ChatGui.Print("[JumpSolver] Stop recording first."); return; }
        if (player.TryStart(puzzle, out var err)) return;
        ChatGui.Print($"[JumpSolver] {err}");
    }

    // ── Save / Load ───────────────────────────────────────────────────────────

    private void SavePuzzle()
    {
        string name = string.IsNullOrWhiteSpace(saveName) ? puzzle.Name : saveName;
        puzzle.Name = name;

        // Replace existing or add new
        int idx = config.SavedPuzzles.FindIndex(p => p.Name == name);
        if (idx >= 0)
            config.SavedPuzzles[idx] = puzzle.DeepCopy();
        else
            config.SavedPuzzles.Add(puzzle.DeepCopy());

        PluginInterface.SavePluginConfig(config);
        ChatGui.Print($"[JumpSolver] Saved '{name}'.");
    }

    private void LoadPuzzle(int idx)
    {
        if (idx < 0 || idx >= config.SavedPuzzles.Count) return;
        puzzle = config.SavedPuzzles[idx].DeepCopy();
        selectedStep = -1;
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

    // ── UI ────────────────────────────────────────────────────────────────────

    private void DrawUI()
    {
        // Always draw world marker regardless of window state
        DrawWorldMarker();

        if (!showWindow) return;

        ImGui.SetNextWindowSize(new Vector2(520, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("JumpSolver", ref showWindow)) { ImGui.End(); return; }

        DrawHeader();
        ImGui.Separator();
        DrawStartSection();
        ImGui.Separator();
        DrawStepList();
        ImGui.Separator();
        DrawPlaybackBar();
        ImGui.Separator();
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
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), "⚠ hook unavailable");
        }

        var name = puzzle.Name;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputText("##pname", ref name, 80)) puzzle.Name = name;
    }

    private (string, Vector4) GetStateLabel()
    {
        if (player.State == PlayState.Playing)
            return ($"PLAYING {player.StepIndex + 1}/{puzzle.Steps.Count}", new Vector4(0.3f, 0.8f, 1f, 1f));
        if (player.State == PlayState.WalkingToStart)
            return ("WALKING TO START", new Vector4(1f, 0.85f, 0.2f, 1f));
        if (recorder.State != RecordState.Idle)
            return ($"REC [{recorder.State}] +{recorder.CompletedSteps.Count}", new Vector4(1f, 0.3f, 0.3f, 1f));
        return ("idle", new Vector4(0.5f, 0.5f, 0.5f, 1f));
    }

    // ── Start section ─────────────────────────────────────────────────────────

    private void DrawStartSection()
    {
        ImGui.Text("Start Point");
        ImGui.SameLine();
        if (ImGui.SmallButton("Set Here")) CmdSetStart();
        ImGui.SameLine();

        bool canGo = puzzle.Start != null && player.State == PlayState.Idle && mover.IsAvailable;
        if (!canGo) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Go To Start")) CmdGotoStart();
        if (!canGo) ImGui.EndDisabled();

        if (puzzle.Start != null)
        {
            var s = puzzle.Start;
            var lp = ObjectTable.LocalPlayer;
            float dist = lp != null ? Vector3.Distance(lp.Position, s.Position) : float.MaxValue;

            var distCol = dist < 0.5f   ? new Vector4(0.3f, 1f, 0.3f, 1f)
                        : dist < s.SnapRadius ? new Vector4(1f, 1f, 0.3f, 1f)
                        :                       new Vector4(0.65f, 0.65f, 0.65f, 1f);

            ImGui.TextColored(distCol,
                $"  ({s.Position.X:F1}, {s.Position.Y:F1}, {s.Position.Z:F1})  " +
                $"facing {s.Facing:F2}r   dist {dist:F1}y");

            float snap = s.SnapRadius;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("snap radius##snap", ref snap, 1f, 30f)) s.SnapRadius = snap;
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.65f, 0.2f, 1f), "  Not set.");
        }
    }

    // ── Step list ─────────────────────────────────────────────────────────────

    private void DrawStepList()
    {
        bool isRec = recorder.State != RecordState.Idle;

        ImGui.Text($"Steps ({puzzle.Steps.Count})");
        ImGui.SameLine();

        if (!isRec)
        {
            if (ImGui.SmallButton("+ Add"))
            {
                float facing = ObjectTable.LocalPlayer?.Rotation ?? 0f;
                puzzle.Steps.Add(new JumpStep(facing, 700f, false, 0f));
                selectedStep = puzzle.Steps.Count - 1;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Record"))  CmdRecord();
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f),
                $"● REC  {recorder.CompletedSteps.Count} steps");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop Rec")) CmdStop();
        }

        // Step rows
        ImGui.BeginChild("##steps", new Vector2(0, 170), true);

        var displaySteps = isRec ? recorder.CompletedSteps : puzzle.Steps;
        for (int i = 0; i < displaySteps.Count; i++)
        {
            var step = displaySteps[i];
            bool isCurrent = player.State == PlayState.Playing && i == player.StepIndex;
            bool isSel     = !isRec && i == selectedStep;

            var col = isCurrent ? new Vector4(0.18f, 0.80f, 0.44f, 1f)
                    : isSel     ? new Vector4(0.85f, 0.85f, 1f, 1f)
                    :             new Vector4(0.70f, 0.70f, 0.70f, 1f);

            ImGui.TextColored(col,
                $"{i + 1:D2}. {step.FacingAngle,6:F2}r  {step.MoveDurationMs,6:F0}ms" +
                (step.Jump ? $"  J+{step.JumpDelayMs:F0}ms" : ""));

            if (!isRec && ImGui.IsItemClicked()) selectedStep = i;

            // Delete button per row
            if (!isRec)
            {
                ImGui.SameLine();
                ImGui.PushID(i);
                if (ImGui.SmallButton("✕"))
                {
                    puzzle.Steps.RemoveAt(i);
                    if (selectedStep >= puzzle.Steps.Count)
                        selectedStep = puzzle.Steps.Count - 1;
                    ImGui.PopID();
                    break; // avoid iterating invalidated list
                }
                ImGui.PopID();
            }
        }

        ImGui.EndChild();

        // Editor for selected step
        if (!isRec && selectedStep >= 0 && selectedStep < puzzle.Steps.Count)
            DrawStepEditor(puzzle.Steps[selectedStep]);
    }

    private void DrawStepEditor(JumpStep step)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 1f, 1f), $"Step {selectedStep + 1}");

        float f = step.FacingAngle;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderFloat("Facing (rad)##f", ref f, -MathF.PI, MathF.PI)) step.FacingAngle = f;
        ImGui.SameLine();
        if (ImGui.SmallButton("← From Player"))
        {
            var p = ObjectTable.LocalPlayer;
            if (p != null) step.FacingAngle = p.Rotation;
        }

        float d = step.MoveDurationMs;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderFloat("Move (ms)##d", ref d, 50f, 4000f)) step.MoveDurationMs = d;

        bool j = step.Jump;
        if (ImGui.Checkbox("Jump##j", ref j)) step.Jump = j;
        if (step.Jump)
        {
            ImGui.SameLine();
            float jd = step.JumpDelayMs;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderFloat("Delay (ms)##jd", ref jd, 0f, step.MoveDurationMs)) step.JumpDelayMs = jd;
        }

        ImGui.Spacing();
        if (ImGui.SmallButton("Duplicate##dup"))
        {
            puzzle.Steps.Insert(selectedStep + 1, step.Clone());
            selectedStep++;
        }
        ImGui.SameLine();
        if (selectedStep > 0 && ImGui.SmallButton("↑##up"))
        {
            (puzzle.Steps[selectedStep], puzzle.Steps[selectedStep - 1]) =
                (puzzle.Steps[selectedStep - 1], puzzle.Steps[selectedStep]);
            selectedStep--;
        }
        ImGui.SameLine();
        if (selectedStep < puzzle.Steps.Count - 1 && ImGui.SmallButton("↓##dn"))
        {
            (puzzle.Steps[selectedStep], puzzle.Steps[selectedStep + 1]) =
                (puzzle.Steps[selectedStep + 1], puzzle.Steps[selectedStep]);
            selectedStep++;
        }
    }

    // ── Playback bar ──────────────────────────────────────────────────────────

    private void DrawPlaybackBar()
    {
        ImGui.Spacing();
        bool active = player.State != PlayState.Idle;
        bool isRec  = recorder.State != RecordState.Idle;

        if (!active && !isRec)
        {
            bool canPlay = puzzle.Start != null && puzzle.Steps.Count > 0;
            if (!canPlay) ImGui.BeginDisabled();
            if (ImGui.Button("▶ Play"))  CmdPlay();
            if (!canPlay) ImGui.EndDisabled();
        }
        else if (!isRec)
        {
            if (ImGui.Button("■ Stop")) CmdStop();
            if (player.State == PlayState.Playing)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.18f, 0.80f, 0.44f, 1f),
                    $"Step {player.StepIndex + 1}/{puzzle.Steps.Count}  {player.StepTimer:F0}ms");
            }
        }
        ImGui.Spacing();
    }

    // ── Save / Load section ───────────────────────────────────────────────────

    private void DrawSaveLoadSection()
    {
        ImGui.Text("Puzzles");
        ImGui.Spacing();

        // Save current puzzle
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

        ImGui.BeginChild("##savedlist", new Vector2(0, 120), true);
        for (int i = 0; i < config.SavedPuzzles.Count; i++)
        {
            var p    = config.SavedPuzzles[i];
            bool sel = i == loadIndex;

            if (ImGui.Selectable($"{p.Name}  ({p.Steps.Count} steps)##sl{i}", sel))
                loadIndex = i;

            ImGui.SameLine();
            ImGui.PushID(10000 + i);
            if (ImGui.SmallButton("Load"))  { LoadPuzzle(i); loadIndex = i; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Del"))   DeleteSavedPuzzle(i);
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    // ── World-space start marker ──────────────────────────────────────────────

    private void DrawWorldMarker()
    {
        if (puzzle.Start == null) return;

        var lp = ObjectTable.LocalPlayer;
        var s  = puzzle.Start;

        float dist = lp != null ? Vector3.Distance(lp.Position, s.Position) : float.MaxValue;
        bool onPoint = dist < 0.35f;
        bool inRange = dist <= s.SnapRadius;

        // Facing match (only relevant when on-point)
        bool correctFacing = onPoint && lp != null &&
                             MathF.Abs(AngleDiff(lp.Rotation, s.Facing)) < 0.15f;

        uint colFill, colRing;
        if (correctFacing)      { colFill = Color(0.1f, 1f, 0.1f, 0.85f); colRing = Color(0.1f, 1f, 0.1f, 0.5f); }
        else if (inRange)       { colFill = Color(1f, 1f, 0.1f, 0.85f);   colRing = Color(1f, 1f, 0.1f, 0.4f); }
        else                    { colFill = Color(1f, 0.2f, 0.2f, 0.85f); colRing = Color(1f, 0.2f, 0.2f, 0.3f); }

        var dl = ImGui.GetForegroundDrawList();

        // Centre dot
        if (GameGui.WorldToScreen(s.Position, out var centre))
        {
            dl.AddCircleFilled(centre, 7f, colFill);
            dl.AddCircle(centre, 7f, Color(0f, 0f, 0f, 0.6f), 16, 1.5f);

            // Facing direction line
            var facingDir = new Vector3(MathF.Sin(s.Facing), 0f, MathF.Cos(s.Facing));
            var facingTip = s.Position + facingDir * 1.5f;
            if (GameGui.WorldToScreen(facingTip, out var tipScreen))
                dl.AddLine(centre, tipScreen, colFill, 2f);
        }

        // Snap radius ring (32 segments on the XZ plane)
        const int Seg = 32;
        Vector2?  prev = null;
        for (int i = 0; i <= Seg; i++)
        {
            float ang = i * MathF.PI * 2f / Seg;
            var   wp  = s.Position + new Vector3(MathF.Sin(ang) * s.SnapRadius, 0f,
                                                  MathF.Cos(ang) * s.SnapRadius);
            if (GameGui.WorldToScreen(wp, out var sp))
            {
                if (prev.HasValue)
                    dl.AddLine(prev.Value, sp, colRing, 1.5f);
                prev = sp;
            }
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
