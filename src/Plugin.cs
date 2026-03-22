using System;
using System.Numerics;

using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace JumpSolver;

// ──────────────────────────────────────────────────────────────────────────────
// Plugin entry point
//
// Two authoring paths:
//   A) Manual  — add steps in the UI, set FacingAngle from current player facing,
//                tune move duration and jump delay with sliders
//   B) Record  — toggle record, walk the puzzle yourself; each jump becomes a step
//
// Playback: /js play (or button) — aligns to start, runs all steps.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class Plugin : IDalamudPlugin
{
    // ── Dalamud services ──────────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static ICommandManager        CommandManager  { get; private set; } = null!;
    [PluginService] internal static IChatGui               ChatGui         { get; private set; } = null!;
    [PluginService] internal static IClientState           ClientState     { get; private set; } = null!;
    [PluginService] internal static IObjectTable           ObjectTable     { get; private set; } = null!;
    [PluginService] internal static IPlayerState           PlayerState     { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework       { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider   GameInterop     { get; private set; } = null!;

    // ── Systems ───────────────────────────────────────────────────────────────
    private readonly MoveHook     mover;
    private readonly JumpRecorder recorder;
    private readonly JumpPlayer   player;

    // ── Active puzzle ─────────────────────────────────────────────────────────
    private readonly JumpPuzzle puzzle = new();

    // ── UI state ──────────────────────────────────────────────────────────────
    private bool showWindow   = false;
    private int  selectedStep = -1;
    private int  activeTab    = 0;   // 0 = Manual, 1 = Recorded

    // ── Commands ──────────────────────────────────────────────────────────────
    private const string CmdMain = "/jumpsolver";
    private const string CmdJs   = "/js";

    // ─────────────────────────────────────────────────────────────────────────

    public Plugin()
    {
        mover    = new MoveHook(GameInterop, Log);
        recorder = new JumpRecorder(Log);
        player   = new JumpPlayer(mover, ObjectTable, ChatGui, Log);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "JumpSolver:\n" +
                "  /js              – Toggle window\n" +
                "  /js setstart     – Set start point to current position\n" +
                "  /js record       – Start recording (walk puzzle yourself)\n" +
                "  /js stop         – Stop recording / abort playback\n" +
                "  /js play         – Run from start point\n" +
                "  /js clear        – Wipe all recorded steps",
        });
        CommandManager.AddHandler(CmdJs, new CommandInfo(OnCommand) { ShowInHelp = false });

        Framework.Update                     += OnUpdate;
        PluginInterface.UiBuilder.Draw       += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += () => showWindow = true;

        Log.Info("JumpSolver loaded. /js for help.");
    }

    public void Dispose()
    {
        if (recorder.State != RecordState.Idle)   recorder.StopRecording();
        if (player.State   != PlayState.Idle)     player.Stop("plugin unloaded");

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
            var localPlayer = ObjectTable.LocalPlayer;
            if (localPlayer == null) return;

            float dt = (float)fw.UpdateDelta.TotalMilliseconds;

            // Recording tick
            if (recorder.State != RecordState.Idle)
                recorder.Tick(dt, localPlayer.Position, localPlayer.Rotation);

            // Playback tick
            if (player.State != PlayState.Idle)
                player.Tick(dt, localPlayer.Address, localPlayer.Position);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "JumpSolver: exception in framework update.");
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private void OnCommand(string cmd, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":         showWindow = !showWindow;  break;
            case "setstart": CmdSetStart();             break;
            case "record":   CmdRecord();               break;
            case "stop":     CmdStop();                 break;
            case "play":     CmdPlay();                 break;
            case "clear":    CmdClear();                break;
            default:
                ChatGui.Print($"[JumpSolver] Unknown: {args}. Try /js for help.");
                break;
        }
    }

    private void CmdSetStart()
    {
        var p = ObjectTable.LocalPlayer;
        if (p == null) { ChatGui.Print("[JumpSolver] Not logged in."); return; }
        puzzle.Start = new StartPoint { Position = p.Position, Facing = p.Rotation };
        ChatGui.Print($"[JumpSolver] Start set at ({p.Position.X:F1}, {p.Position.Y:F1}, {p.Position.Z:F1}).");
    }

    private void CmdRecord()
    {
        if (player.State != PlayState.Idle) { ChatGui.Print("[JumpSolver] Stop playback first."); return; }
        var p = ObjectTable.LocalPlayer;
        if (p == null) { ChatGui.Print("[JumpSolver] Not logged in."); return; }

        recorder.StartRecording(p.Position, p.Rotation);
        puzzle.Start = recorder.CapturedStart;
        puzzle.Steps.Clear();
        activeTab = 1;
        ChatGui.Print("[JumpSolver] Recording — walk your puzzle now. /js stop when done.");
    }

    private void CmdStop()
    {
        if (recorder.State != RecordState.Idle)
        {
            recorder.StopRecording();
            // Merge recorded steps into puzzle
            puzzle.Steps.Clear();
            puzzle.Steps.AddRange(recorder.CompletedSteps);
            ChatGui.Print($"[JumpSolver] Recorded {puzzle.Steps.Count} steps.");
        }
        else if (player.State != PlayState.Idle)
        {
            player.Stop("aborted by user");
            ChatGui.Print("[JumpSolver] Playback aborted.");
        }
        else
        {
            ChatGui.Print("[JumpSolver] Nothing to stop.");
        }
    }

    private void CmdPlay()
    {
        if (recorder.State != RecordState.Idle) { ChatGui.Print("[JumpSolver] Stop recording first."); return; }
        if (player.TryStart(puzzle, out var error)) return;
        ChatGui.Print($"[JumpSolver] {error}");
    }

    private void CmdClear()
    {
        CmdStop();
        puzzle.Steps.Clear();
        puzzle.Start = null;
        recorder.ClearAll();
        selectedStep = -1;
        ChatGui.Print("[JumpSolver] Cleared.");
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    private void DrawUI()
    {
        if (!showWindow) return;

        ImGui.SetNextWindowSize(new Vector2(500, 580), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("JumpSolver", ref showWindow, ImGuiWindowFlags.None))
        { ImGui.End(); return; }

        DrawHeader();
        ImGui.Separator();
        DrawStartSection();
        ImGui.Separator();
        DrawTabBar();
        ImGui.Separator();
        DrawPlaybackBar();

        ImGui.End();
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        ImGui.TextColored(new Vector4(0.18f, 0.80f, 0.44f, 1f), "JumpSolver");
        ImGui.SameLine();

        // Status badge
        var (stateText, stateColor) = GetStateDisplay();
        ImGui.TextColored(stateColor, $"[{stateText}]");

        if (!mover.IsAvailable)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), "⚠ hook failed");
        }

        ImGui.Spacing();
        var name = puzzle.Name;
        ImGui.SetNextItemWidth(250);
        if (ImGui.InputText("##puzzlename", ref name, 80))
            puzzle.Name = name;
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "puzzle name");
    }

    private (string text, Vector4 color) GetStateDisplay()
    {
        if (player.State == PlayState.Playing)
            return ($"PLAYING {player.StepIndex + 1}/{puzzle.Steps.Count}", new Vector4(0.3f, 0.8f, 1f, 1f));
        if (player.State == PlayState.Aligning)
            return ("ALIGNING", new Vector4(1f, 0.8f, 0.2f, 1f));
        if (recorder.State != RecordState.Idle)
            return ($"REC {recorder.State} +{recorder.CompletedSteps.Count}", new Vector4(1f, 0.3f, 0.3f, 1f));
        return ("idle", new Vector4(0.5f, 0.5f, 0.5f, 1f));
    }

    // ── Start point ───────────────────────────────────────────────────────────

    private void DrawStartSection()
    {
        ImGui.Text("Start Point");
        ImGui.SameLine();
        if (ImGui.SmallButton("Set Here##start"))
            CmdSetStart();

        if (puzzle.Start != null)
        {
            var s = puzzle.Start;
            ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.65f, 1f),
                $"  ({s.Position.X:F1}, {s.Position.Y:F1}, {s.Position.Z:F1})   facing {s.Facing:F2} rad");
            ImGui.SetNextItemWidth(160);
            float snap = s.SnapRadius;
            if (ImGui.SliderFloat("snap radius (yalms)##snap", ref snap, 1f, 20f))
                s.SnapRadius = snap;
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.65f, 0.2f, 1f),
                "  Not set — stand at puzzle start and click Set Here.");
        }
    }

    // ── Tab bar: Manual / Recorded ────────────────────────────────────────────

    private void DrawTabBar()
    {
        if (ImGui.BeginTabBar("##modes"))
        {
            if (ImGui.BeginTabItem("Manual"))
            {
                activeTab = 0;
                DrawManualTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Recorded"))
            {
                activeTab = 1;
                DrawRecordedTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    // ── Manual tab ────────────────────────────────────────────────────────────

    private void DrawManualTab()
    {
        ImGui.Spacing();

        // Step list
        ImGui.Text($"Steps ({puzzle.Steps.Count})");
        ImGui.SameLine();
        if (ImGui.SmallButton("+ Add##manual"))
        {
            var facing = ObjectTable.LocalPlayer?.Rotation ?? 0f;
            puzzle.Steps.Add(new JumpStep(facing, 700f, true, 0f));
            selectedStep = puzzle.Steps.Count - 1;
        }

        ImGui.BeginChild("##manuallist", new Vector2(0, 160), true);
        for (int i = 0; i < puzzle.Steps.Count; i++)
            DrawStepRow(i);
        ImGui.EndChild();

        // Editor
        if (selectedStep >= 0 && selectedStep < puzzle.Steps.Count)
            DrawStepEditor(puzzle.Steps[selectedStep]);
    }

    private void DrawStepRow(int i)
    {
        var step = puzzle.Steps[i];
        bool isCurrent = player.State == PlayState.Playing && i == player.StepIndex;
        bool isSelected = i == selectedStep;

        var color = isCurrent  ? new Vector4(0.18f, 0.80f, 0.44f, 1f)
                  : isSelected ? new Vector4(0.85f, 0.85f, 1f, 1f)
                  :              new Vector4(0.70f, 0.70f, 0.70f, 1f);

        ImGui.TextColored(color,
            $"{i + 1:D2}. facing {step.FacingAngle,6:F2}r   {step.MoveDurationMs,5:F0}ms" +
            (step.Jump ? $"  J+{step.JumpDelayMs:F0}ms" : "  (no jump)"));

        if (ImGui.IsItemClicked()) selectedStep = i;
    }

    private void DrawStepEditor(JumpStep step)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 1f, 1f), $"Step {selectedStep + 1}");

        // Facing angle
        float facing = step.FacingAngle;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderFloat("Facing (rad)##f", ref facing, -MathF.PI, MathF.PI))
            step.FacingAngle = facing;
        ImGui.SameLine();
        if (ImGui.SmallButton("← Player"))
        {
            var p = ObjectTable.LocalPlayer;
            if (p != null) step.FacingAngle = p.Rotation;
        }

        // Move duration
        float dur = step.MoveDurationMs;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderFloat("Move (ms)##d", ref dur, 50f, 3000f))
            step.MoveDurationMs = dur;

        // Jump
        bool doJump = step.Jump;
        if (ImGui.Checkbox("Jump##j", ref doJump)) step.Jump = doJump;

        if (step.Jump)
        {
            ImGui.SameLine();
            float jdelay = step.JumpDelayMs;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderFloat("Jump delay (ms)##jd", ref jdelay, 0f, step.MoveDurationMs))
                step.JumpDelayMs = jdelay;
        }

        // Actions
        ImGui.Spacing();
        if (ImGui.SmallButton("Remove##rm"))
        {
            puzzle.Steps.RemoveAt(selectedStep);
            selectedStep = Math.Clamp(selectedStep - 1, -1, puzzle.Steps.Count - 1);
        }
        ImGui.SameLine();
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

    // ── Recorded tab ──────────────────────────────────────────────────────────

    private void DrawRecordedTab()
    {
        ImGui.Spacing();

        // Recording controls
        if (recorder.State == RecordState.Idle)
        {
            if (ImGui.Button("Start Recording"))   CmdRecord();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "Walk your puzzle — each jump is captured.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f),
                $"● REC  [{recorder.State}]  {recorder.CompletedSteps.Count} steps");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##recstop")) CmdStop();
        }

        ImGui.Spacing();

        // Live step list from recorder
        var steps = recorder.State != RecordState.Idle ? recorder.CompletedSteps : puzzle.Steps;
        ImGui.Text($"Captured steps: {steps.Count}");
        ImGui.BeginChild("##reclist", new Vector2(0, 200), true);
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            bool isCurrent = player.State == PlayState.Playing && i == player.StepIndex;
            var color = isCurrent ? new Vector4(0.18f, 0.80f, 0.44f, 1f)
                                  : new Vector4(0.70f, 0.70f, 0.70f, 1f);
            ImGui.TextColored(color,
                $"{i + 1:D2}. facing {step.FacingAngle,6:F2}r   " +
                $"move {step.MoveDurationMs,5:F0}ms   J+{step.JumpDelayMs:F0}ms");
        }
        ImGui.EndChild();

        if (recorder.State == RecordState.Idle && puzzle.Steps.Count > 0)
        {
            ImGui.Spacing();
            if (ImGui.SmallButton("Re-record"))
            {
                puzzle.Steps.Clear();
                CmdRecord();
            }
        }
    }

    // ── Playback bar ──────────────────────────────────────────────────────────

    private void DrawPlaybackBar()
    {
        ImGui.Spacing();

        bool isPlaying = player.State != PlayState.Idle;
        bool isRecording = recorder.State != RecordState.Idle;

        if (!isPlaying && !isRecording)
        {
            bool canPlay = puzzle.Start != null && puzzle.Steps.Count > 0;
            if (!canPlay) ImGui.BeginDisabled();
            if (ImGui.Button("▶ Play"))  CmdPlay();
            if (!canPlay) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("✕ Clear")) CmdClear();
        }
        else
        {
            if (ImGui.Button("■ Stop"))  CmdStop();

            if (player.State == PlayState.Playing)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.18f, 0.80f, 0.44f, 1f),
                    $"Step {player.StepIndex + 1} / {puzzle.Steps.Count}   t={player.StepTimer:F0}ms");
            }
        }
    }
}
