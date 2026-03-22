using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace JumpSolver;

// ──────────────────────────────────────────────────────────────────────────────
// Playback state machine
//
// STATES
//   Idle      – not running
//   Aligning  – walking to / snapping the start point
//   Playing   – executing steps in sequence
//
// PER STEP EXECUTION
//   1. Set player facing to step.FacingAngle (instant rotation write)
//   2. Enable MoveHook forward injection (player moves forward at full speed)
//   3. After step.JumpDelayMs, fire jump via ActionManager
//   4. After step.MoveDurationMs, disable injection → advance to next step
//
// ALIGNMENT
//   If the player is within StartPoint.SnapRadius yalms of the start, we:
//     a. Set rotation to StartPoint.Facing
//     b. (TODO) Drive vnavmesh to walk to the exact position
//   For now, if the player is already close enough we snap and go.
// ──────────────────────────────────────────────────────────────────────────────

public enum PlayState { Idle, Aligning, Playing }

public sealed unsafe class JumpPlayer
{
    public PlayState State     { get; private set; } = PlayState.Idle;
    public int       StepIndex { get; private set; }
    public float     StepTimer { get; private set; }   // ms elapsed in current step

    private JumpPuzzle?   puzzle;
    private MoveHook      mover;
    private readonly IPluginLog     log;
    private readonly IObjectTable   objects;
    private readonly IChatGui       chat;

    // Per-step state
    private bool jumpFired;
    private bool facingSet;

    // ─────────────────────────────────────────────────────────────────────────

    public JumpPlayer(MoveHook mover, IObjectTable objects, IChatGui chat, IPluginLog log)
    {
        this.mover   = mover;
        this.objects = objects;
        this.chat    = chat;
        this.log     = log;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public bool TryStart(JumpPuzzle puzzle, out string error)
    {
        error = string.Empty;

        if (puzzle.Start == null)          { error = "No start point set.";              return false; }
        if (puzzle.Steps.Count == 0)       { error = "No steps — add or record some.";   return false; }

        var player = objects.LocalPlayer;
        if (player == null)                { error = "Not logged in.";                   return false; }

        if (!mover.IsAvailable)
            chat.Print("[JumpSolver] WARNING: Movement hook unavailable — steps without working injection.");

        float dist = Vector3.Distance(player.Position, puzzle.Start.Position);
        if (dist > puzzle.Start.SnapRadius)
        {
            error = $"Too far from start ({dist:F1} yalms). Get within {puzzle.Start.SnapRadius} yalms.";
            return false;
        }

        this.puzzle = puzzle;
        StepIndex   = 0;
        StepTimer   = 0f;
        jumpFired   = false;
        facingSet   = false;
        State       = PlayState.Aligning;

        log.Info($"JumpPlayer: starting '{puzzle.Name}', {puzzle.Steps.Count} steps.");
        return true;
    }

    public void Stop(string reason)
    {
        mover.ClearInjection();
        State = PlayState.Idle;
        log.Info($"JumpPlayer: stopped — {reason}");
    }

    /// <summary>
    /// Called every framework frame while playing or aligning.
    /// </summary>
    public void Tick(float deltaMs, nint playerAddress, Vector3 playerPos)
    {
        switch (State)
        {
            case PlayState.Aligning:
                TickAlign(playerAddress, playerPos);
                break;

            case PlayState.Playing:
                TickStep(deltaMs, playerAddress);
                break;
        }
    }

    // ── State handlers ────────────────────────────────────────────────────────

    private void TickAlign(nint playerAddress, Vector3 playerPos)
    {
        // TODO: If distance > 0.5 yalms, issue vnavmesh.Path.MoveTo IPC call
        //   var move = PluginInterface.GetIpcSubscriber<float, float, float, bool, object>("vnavmesh.Path.MoveTo");
        //   move.InvokeAction(start.Position.X, start.Position.Y, start.Position.Z, false);
        // For now, just snap facing and start immediately if close enough.

        mover.SetPlayerFacing(playerAddress, puzzle!.Start!.Facing);

        State     = PlayState.Playing;
        StepIndex = 0;
        StepTimer = 0f;
        jumpFired = false;
        facingSet = false;

        chat.Print($"[JumpSolver] Running '{puzzle.Name}' — {puzzle.Steps.Count} steps.");
        log.Info("JumpPlayer: alignment complete, starting steps.");
    }

    private void TickStep(float deltaMs, nint playerAddress)
    {
        if (StepIndex >= puzzle!.Steps.Count)
        {
            mover.ClearInjection();
            State = PlayState.Idle;
            chat.Print("[JumpSolver] Done!");
            log.Info("JumpPlayer: playback complete.");
            return;
        }

        var step = puzzle.Steps[StepIndex];

        // 1. Snap facing (once per step)
        if (!facingSet)
        {
            mover.SetPlayerFacing(playerAddress, step.FacingAngle);
            facingSet = true;
            log.Debug($"JumpPlayer: step {StepIndex + 1} — facing={step.FacingAngle:F2}");
        }

        // 2. Hold forward movement
        mover.MoveForward();

        // 3. Fire jump after delay
        if (step.Jump && !jumpFired && StepTimer >= step.JumpDelayMs)
        {
            FireJump(playerAddress);
            jumpFired = true;
        }

        StepTimer += deltaMs;

        // 4. Advance when move duration elapsed
        if (StepTimer >= step.MoveDurationMs)
        {
            mover.ClearInjection();

            log.Debug($"JumpPlayer: step {StepIndex + 1} done " +
                      $"(elapsed={StepTimer:F0}ms, jump={jumpFired}).");

            StepIndex++;
            StepTimer = 0f;
            jumpFired = false;
            facingSet = false;
        }
    }

    // ── Jump ──────────────────────────────────────────────────────────────────

    private void FireJump(nint playerAddress)
    {
        try
        {
            // FFXIV General action 2 = Jump. Targeting self (0xE0000000 is the
            // self sentinel for General actions — confirmed valid for non-targeted actions).
            var am = ActionManager.Instance();
            if (am == null) return;
            am->UseAction(ActionType.General, Signatures.JumpActionId, 0xE0000000);
            log.Debug($"JumpPlayer: jump fired at step {StepIndex + 1}, t={StepTimer:F0}ms.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "JumpPlayer: exception firing jump.");
        }
    }
}
