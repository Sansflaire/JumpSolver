using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace JumpSolver;

public enum PlayState { Idle, WalkingToStart, Playing }

internal sealed unsafe class JumpPlayer
{
    public PlayState State     { get; private set; } = PlayState.Idle;
    public int       StepIndex { get; private set; }
    public float     StepTimer { get; private set; }

    private JumpPuzzle?  puzzle;
    private readonly MoveHook    mover;
    private readonly IObjectTable objects;
    private readonly IChatGui    chat;
    private readonly IPluginLog  log;

    private bool  jumpFired;
    private bool  facingSet;

    // Walk-to-start locking phase
    private bool  locking    = false;
    private float lockTimer  = 0f;
    private const float WalkSlowRadius  = 1.5f;   // begin slowing within this many yalms
    private const float WalkSnapRadius  = 0.04f;  // snap position when this close (~4cm)
    private const float LockDurationMs  = 350f;   // hold exact position this long (server acceptance)

    // ─────────────────────────────────────────────────────────────────────────

    internal JumpPlayer(MoveHook mover, IObjectTable objects, IChatGui chat, IPluginLog log)
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
        if (puzzle.Start == null)        { error = "No start point set.";            return false; }
        if (puzzle.Steps.Count == 0)     { error = "No steps to run.";               return false; }
        var player = objects.LocalPlayer;
        if (player == null)              { error = "Not logged in.";                 return false; }

        float dist = Vector3.Distance(player.Position, puzzle.Start.Position);
        if (dist > puzzle.Start.SnapRadius)
        {
            error = $"Too far from start ({dist:F1} yalms, need ≤ {puzzle.Start.SnapRadius:F0}).";
            return false;
        }

        this.puzzle = puzzle;
        BeginSteps();
        return true;
    }

    /// <summary>Walk beeline to the start point, snap facing on arrival.</summary>
    public bool TryWalkToStart(JumpPuzzle puzzle, out string error)
    {
        error = string.Empty;
        if (puzzle.Start == null)    { error = "No start point set.";  return false; }
        var player = objects.LocalPlayer;
        if (player == null)          { error = "Not logged in.";        return false; }

        float dist = Vector3.Distance(player.Position, puzzle.Start.Position);
        if (dist > puzzle.Start.SnapRadius)
        {
            error = $"Too far ({dist:F1} yalms). Get within {puzzle.Start.SnapRadius:F0} yalms first.";
            return false;
        }

        if (!mover.IsAvailable)
        {
            error = "Movement hook unavailable — update Signatures.RMIWalk.";
            return false;
        }

        this.puzzle = puzzle;
        locking     = false;
        lockTimer   = 0f;
        State       = PlayState.WalkingToStart;
        log.Info("JumpPlayer: walking to start point.");
        return true;
    }

    public void Stop(string reason)
    {
        mover.ClearInjection();
        locking   = false;
        lockTimer = 0f;
        State     = PlayState.Idle;
        log.Info($"JumpPlayer: stopped — {reason}");
    }

    public void Tick(float deltaMs, nint playerAddress, Vector3 playerPos)
    {
        switch (State)
        {
            case PlayState.WalkingToStart:
                TickWalkToStart(deltaMs, playerAddress, playerPos);
                break;

            case PlayState.Playing:
                TickStep(deltaMs, playerAddress);
                break;
        }
    }

    // ── Walk to start ─────────────────────────────────────────────────────────

    private void TickWalkToStart(float deltaMs, nint playerAddress, Vector3 playerPos)
    {
        var   target = puzzle!.Start!.Position;
        float dist   = Vector3.Distance(playerPos, target);

        // Locking phase: hold exact position every frame until server confirms it
        if (locking)
        {
            unsafe
            {
                var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)playerAddress;
                obj->Position = target;
                obj->Rotation = puzzle.Start.Facing;
            }
            lockTimer += deltaMs;
            if (lockTimer >= LockDurationMs)
            {
                locking = false;
                State   = PlayState.Idle;
                chat.Print("[JumpSolver] Locked to start point.");
                log.Info("JumpPlayer: lock complete.");
            }
            return;
        }

        // Close enough — snap and begin locking
        if (dist <= WalkSnapRadius)
        {
            mover.ClearInjection();
            locking   = true;
            lockTimer = 0f;
            unsafe
            {
                var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)playerAddress;
                obj->Position = target;
                obj->Rotation = puzzle.Start.Facing;
            }
            log.Info($"JumpPlayer: snapped at dist={dist:F3}y, locking for {LockDurationMs}ms.");
            return;
        }

        // Approach: scale speed proportionally so she decelerates smoothly into the target
        // Full speed at WalkSlowRadius, 0.1× speed at WalkSnapRadius
        float t     = Math.Clamp((dist - WalkSnapRadius) / (WalkSlowRadius - WalkSnapRadius), 0f, 1f);
        float speed = MathF.Max(0.1f, t);

        var   dir = Vector3.Normalize(target - playerPos);
        float ang = MathF.Atan2(dir.X, dir.Z);
        mover.SetPlayerFacing(playerAddress, ang);
        mover.InjectedForward = speed;
        mover.InjectedLeft    = 0f;
    }

    // ── Playback ──────────────────────────────────────────────────────────────

    private void BeginSteps()
    {
        // Snap facing then begin step 0
        var player = objects.LocalPlayer;
        if (player != null)
            mover.SetPlayerFacing(player.Address, puzzle!.Start!.Facing);

        StepIndex = 0;
        StepTimer = 0f;
        jumpFired = false;
        facingSet = false;
        State     = PlayState.Playing;

        chat.Print($"[JumpSolver] Running '{puzzle!.Name}' — {puzzle.Steps.Count} steps.");
        log.Info("JumpPlayer: playback started.");
    }

    private void TickStep(float deltaMs, nint playerAddress)
    {
        if (StepIndex >= puzzle!.Steps.Count)
        {
            mover.ClearInjection();
            State = PlayState.Idle;
            chat.Print("[JumpSolver] Done!");
            log.Info("JumpPlayer: complete.");
            return;
        }

        var step = puzzle.Steps[StepIndex];

        // 1. Snap facing once per step
        if (!facingSet)
        {
            mover.SetPlayerFacing(playerAddress, step.FacingAngle);
            facingSet = true;
        }

        // 2. Hold forward
        mover.MoveForward();

        // 3. Fire jump at correct delay
        if (step.Jump && !jumpFired && StepTimer >= step.JumpDelayMs)
        {
            FireJump();
            jumpFired = true;
        }

        StepTimer += deltaMs;

        // 4. Advance when duration elapsed
        if (StepTimer >= step.MoveDurationMs)
        {
            mover.ClearInjection();
            log.Debug($"JumpPlayer: step {StepIndex + 1} done ({StepTimer:F0}ms).");
            StepIndex++;
            StepTimer = 0f;
            jumpFired = false;
            facingSet = false;
        }
    }

    // ── Jump ──────────────────────────────────────────────────────────────────

    private void FireJump()
    {
        try
        {
            var am = ActionManager.Instance();
            if (am == null) return;
            // ActionType 5 = GeneralAction. Jump is General Action ID 2.
            am->UseAction((ActionType)5, Signatures.JumpActionId, 0xE0000000);
            log.Debug($"JumpPlayer: jump fired at step {StepIndex + 1}, t={StepTimer:F0}ms.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "JumpPlayer: exception firing jump.");
        }
    }
}
