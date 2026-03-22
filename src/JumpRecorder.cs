using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Plugin.Services;

namespace JumpSolver;

// ──────────────────────────────────────────────────────────────────────────────
// JumpRecorder  —  converts raw player movement into JumpStep sequences
//
// STATE MACHINE
//   Idle             – not recording
//   WaitingForMove   – recording active; player is standing still or turning
//   Moving           – player is walking; accumulating step duration
//   InAir            – player jumped; waiting for landing
//
// STEP BOUNDARIES  (events that finalize the current step and start a new one)
//   • Direction change: player's rotation drifts > DirChangeThreshold rad from
//     the facing at the START of the current walk. Finalizes the current step
//     (no-jump), starts a new step with the new facing.
//   • Jump: Y-velocity spikes positive. Records jump=true + jumpDelayMs.
//     Transitions to InAir; step is finalized on landing.
//   • Stop without jump: XZ velocity drops to near-zero for > IdleDebounceMs.
//     Finalizes as a no-jump step, returns to WaitingForMove.
//   • Landing: Y returns to stable after being airborne.
//     Finalizes the step with the total elapsed time.
//
// TIMING
//   All times are in milliseconds. The timer starts when XZ movement is first
//   detected and runs until the step is finalized. This captures the full
//   duration the player held a direction (including through the air).
// ──────────────────────────────────────────────────────────────────────────────

public enum RecordState { Idle, WaitingForMove, Moving, InAir }

internal sealed class JumpRecorder
{
    // ── Thresholds ────────────────────────────────────────────────────────────
    private const float MoveXZThreshold    = 0.02f;   // yalms/frame to count as "moving"
    private const float DirChangeThreshold = 0.12f;   // radians — direction change splits step
    private const float AirborneYDelta     = 0.05f;   // y delta/frame to count as "rising (jumped)"
    private const float GroundedYDelta     = 0.025f;  // |y delta|/frame to count as "grounded"
    private const float LandingDebounceMs  = 80f;     // stay grounded this long before finalising
    private const float IdleDebounceMs     = 350f;    // idle this long to finalise a no-jump step
    private const float MinStepMs          = 80f;     // discard steps shorter than this

    // ── Public state ──────────────────────────────────────────────────────────
    public RecordState    State          { get; private set; } = RecordState.Idle;
    public List<JumpStep> CompletedSteps { get; private set; } = new();
    public StartPoint?    CapturedStart  { get; private set; }

    // ── Per-step tracking ──────────────────────────────────────────────────────
    private float stepTimer      = 0f;   // ms since step start (movement first detected)
    private float jumpDelayMs    = 0f;   // ms from step start to jump input
    private float facingAtStart  = 0f;   // player facing when step began
    private bool  jumpDetected   = false;

    // Landing / idle debounce
    private float landDebounce   = 0f;
    private float idleDebounce   = 0f;

    // Y tracking
    private float prevY          = 0f;
    private Vector2 prevXZ       = Vector2.Zero;

    private readonly IPluginLog log;

    // ─────────────────────────────────────────────────────────────────────────

    internal JumpRecorder(IPluginLog log) { this.log = log; }

    // ── Public API ────────────────────────────────────────────────────────────

    public void StartRecording(Vector3 playerPos, float playerFacing)
    {
        CompletedSteps.Clear();
        CapturedStart = new StartPoint { Position = playerPos, Facing = playerFacing };
        ResetStep(playerFacing);
        prevXZ = new Vector2(playerPos.X, playerPos.Z);
        prevY  = playerPos.Y;
        State  = RecordState.WaitingForMove;
        log.Info("JumpRecorder: started.");
    }

    public void StopRecording()
    {
        // If mid-step, try to salvage what we have
        if (State == RecordState.Moving && stepTimer > MinStepMs)
            FinalizeStep(jump: false);

        State = RecordState.Idle;
        log.Info($"JumpRecorder: stopped — {CompletedSteps.Count} steps.");
    }

    public void ClearAll()
    {
        State = RecordState.Idle;
        CompletedSteps.Clear();
        CapturedStart = null;
    }

    // Called every framework frame while recording is active.
    public void Tick(float deltaMs, Vector3 pos, float facing)
    {
        if (State == RecordState.Idle) return;

        float xzDelta = Vector2.Distance(new Vector2(pos.X, pos.Z), prevXZ);
        float yDelta  = pos.Y - prevY;
        bool  moving  = xzDelta > MoveXZThreshold;
        bool  rising  = yDelta  > AirborneYDelta;
        bool  grounded = MathF.Abs(yDelta) < GroundedYDelta;

        switch (State)
        {
            case RecordState.WaitingForMove:
                TickWaiting(deltaMs, pos, facing, moving, rising);
                break;

            case RecordState.Moving:
                TickMoving(deltaMs, pos, facing, moving, rising);
                break;

            case RecordState.InAir:
                TickInAir(deltaMs, grounded);
                break;
        }

        prevXZ = new Vector2(pos.X, pos.Z);
        prevY  = pos.Y;
    }

    // ── State handlers ────────────────────────────────────────────────────────

    private void TickWaiting(float deltaMs, Vector3 pos, float facing, bool moving, bool rising)
    {
        if (moving || rising)
        {
            // Movement or standing-jump detected — start a new step
            State         = RecordState.Moving;
            facingAtStart = facing;
            stepTimer     = 0f;
            jumpDetected  = false;
            idleDebounce  = 0f;

            if (rising)
            {
                // Jumped from standstill — jump delay = 0
                jumpDetected = true;
                jumpDelayMs  = 0f;
                State        = RecordState.InAir;
                log.Debug("JumpRecorder: standing jump.");
            }
            else
            {
                log.Debug($"JumpRecorder: step started, facing={facing:F2} rad.");
            }
        }
    }

    private void TickMoving(float deltaMs, Vector3 pos, float facing, bool moving, bool rising)
    {
        stepTimer += deltaMs;

        // Direction change → split step
        float rotDiff = AngleDiff(facing, facingAtStart);
        if (MathF.Abs(rotDiff) > DirChangeThreshold)
        {
            if (stepTimer > MinStepMs)
            {
                log.Debug($"JumpRecorder: direction change ({rotDiff:F2} rad) — splitting step.");
                FinalizeStep(jump: false);
            }

            // Start fresh with new facing
            facingAtStart = facing;
            stepTimer     = 0f;
            jumpDetected  = false;
            idleDebounce  = 0f;
            State         = RecordState.Moving;
            return;
        }

        // Jump detected
        if (!jumpDetected && rising)
        {
            jumpDetected = true;
            jumpDelayMs  = stepTimer;
            State        = RecordState.InAir;
            log.Debug($"JumpRecorder: jump at {jumpDelayMs:F0}ms, facing={facingAtStart:F2} rad.");
            return;
        }

        // Player stopped without jumping
        if (!moving)
        {
            idleDebounce += deltaMs;
            if (idleDebounce >= IdleDebounceMs && stepTimer > MinStepMs)
            {
                log.Debug($"JumpRecorder: stop without jump at {stepTimer:F0}ms.");
                FinalizeStep(jump: false);
                State = RecordState.WaitingForMove;
            }
        }
        else
        {
            idleDebounce = 0f;
        }
    }

    private void TickInAir(float deltaMs, bool grounded)
    {
        stepTimer += deltaMs;

        if (grounded)
        {
            landDebounce += deltaMs;
            if (landDebounce >= LandingDebounceMs)
            {
                FinalizeStep(jump: true);
                State = RecordState.WaitingForMove;
            }
        }
        else
        {
            landDebounce = 0f;
        }
    }

    // ── Finalization ──────────────────────────────────────────────────────────

    private void FinalizeStep(bool jump)
    {
        float duration = MathF.Max(stepTimer - (jump ? LandingDebounceMs : IdleDebounceMs), MinStepMs);

        var step = new JumpStep
        {
            FacingAngle    = facingAtStart,
            MoveDurationMs = duration,
            Jump           = jump,
            JumpDelayMs    = jump ? jumpDelayMs : 0f,
        };

        CompletedSteps.Add(step);
        log.Info($"JumpRecorder: step {CompletedSteps.Count} — " +
                 $"facing={step.FacingAngle:F2}r  move={step.MoveDurationMs:F0}ms  " +
                 $"jump={step.Jump}  jDelay={step.JumpDelayMs:F0}ms");

        ResetStep(0f);
        landDebounce = 0f;
        idleDebounce = 0f;
    }

    private void ResetStep(float facing)
    {
        stepTimer    = 0f;
        jumpDelayMs  = 0f;
        facingAtStart = facing;
        jumpDetected = false;
        landDebounce = 0f;
        idleDebounce = 0f;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Shortest signed difference between two angles (-π..π)
    private static float AngleDiff(float a, float b)
    {
        float d = a - b;
        while (d >  MathF.PI) d -= MathF.PI * 2f;
        while (d < -MathF.PI) d += MathF.PI * 2f;
        return d;
    }
}
