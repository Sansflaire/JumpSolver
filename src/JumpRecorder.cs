using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Plugin.Services;

namespace JumpSolver;

// ──────────────────────────────────────────────────────────────────────────────
// Recording state machine
//
// STATES
//   Idle          – not recording
//   WaitingForMove– recording is active; player is standing still
//                   (waiting for them to begin a run toward a jump)
//   Moving        – player is running; tracking direction + elapsed time
//   InAir         – player jumped; waiting for landing
//
// STEP DETECTION
//   A step is produced every time the player leaves the ground (jumps).
//   The step captures:
//     • FacingAngle   – player.Rotation at the moment the jump was detected
//     • JumpDelayMs   – time between first movement and the jump input
//     • MoveDurationMs– time between first movement and landing (set on land)
//     • Jump          – always true for detected jumps
//
//   Standing-still jumps (JumpDelayMs ≈ 0, MoveDurationMs short) are also
//   captured correctly — the player faces a direction and presses jump while
//   stationary, which produces a short step.
//
// HOW TO DETECT JUMP / LANDING WITHOUT A GAME HOOK
//   We track Y position over time.
//   Jump detected:  pos.Y - prevPos.Y > AirborneRiseThreshold
//   Landing:        wasAirborne && |pos.Y - prevPos.Y| < GroundedDeltaThreshold
//                   AND Y velocity has gone from + to ~0 / negative
// ──────────────────────────────────────────────────────────────────────────────

public enum RecordState { Idle, WaitingForMove, Moving, InAir }

public sealed class JumpRecorder
{
    // ── Tunable thresholds ────────────────────────────────────────────────────

    /// Minimum XZ displacement per frame (yalms) to count as "moving"
    private const float MoveXZThreshold = 0.03f;

    /// Minimum Y rise per frame to count as "airborne / jumped"
    private const float AirborneRiseThreshold = 0.04f;

    /// Max |Y delta| per frame while still counting as "grounded"
    private const float GroundedDeltaThreshold = 0.02f;

    /// After landing, how many ms of ground contact before we consider the
    /// step truly finished (debounce for uneven terrain)
    private const float LandingDebounceMs = 120f;

    // ─────────────────────────────────────────────────────────────────────────

    public RecordState          State          { get; private set; } = RecordState.Idle;
    public List<JumpStep>       CompletedSteps { get; private set; } = new();
    public StartPoint?          CapturedStart  { get; private set; }

    private readonly IPluginLog log;

    // Per-step tracking
    private float   stepTimer    = 0f;   // ms since move started
    private float   jumpTimer    = 0f;   // ms from move start until jump detected
    private float   facingAtJump = 0f;   // player.Rotation when jump was detected
    private bool    jumpDetected = false;

    // Y tracking (for jump/land detection)
    private float prevY          = 0f;
    private bool  wasRising      = false;
    private float landingDebounce = 0f;

    // ─────────────────────────────────────────────────────────────────────────

    public JumpRecorder(IPluginLog log) { this.log = log; }

    // ── Public API ────────────────────────────────────────────────────────────

    public void StartRecording(Vector3 playerPos, float playerFacing)
    {
        CompletedSteps.Clear();
        CapturedStart = new StartPoint
        {
            Position = playerPos,
            Facing   = playerFacing,
        };
        ResetStep();
        State = RecordState.WaitingForMove;
        log.Info("JumpRecorder: recording started.");
    }

    public void StopRecording()
    {
        State = RecordState.Idle;
        log.Info($"JumpRecorder: recording stopped. {CompletedSteps.Count} steps captured.");
    }

    public void ClearAll()
    {
        StopRecording();
        CompletedSteps.Clear();
        CapturedStart = null;
    }

    /// <summary>
    /// Called every framework frame while recording is active.
    /// </summary>
    public void Tick(float deltaMs, Vector3 pos, float facing)
    {
        if (State == RecordState.Idle) return;

        float yDelta = pos.Y - prevY;
        bool  isRising  = yDelta > AirborneRiseThreshold;
        bool  isGrounded = MathF.Abs(yDelta) < GroundedDeltaThreshold;

        switch (State)
        {
            case RecordState.WaitingForMove:
                TickWaiting(deltaMs, pos, facing, yDelta, isRising);
                break;

            case RecordState.Moving:
                TickMoving(deltaMs, pos, facing, yDelta, isRising);
                break;

            case RecordState.InAir:
                TickInAir(deltaMs, pos, facing, isGrounded);
                break;
        }

        prevY    = pos.Y;
        wasRising = isRising;
    }

    // ── State handlers ────────────────────────────────────────────────────────

    private void TickWaiting(float deltaMs, Vector3 pos, float facing, float yDelta, bool isRising)
    {
        // Transition to Moving when XZ movement detected
        // Also handle the case where the player jumps from a standstill
        bool xzMoved = XZMoved(pos);

        if (xzMoved)
        {
            State     = RecordState.Moving;
            stepTimer = 0f;
            jumpTimer = 0f;
            jumpDetected = false;
            log.Debug("JumpRecorder: movement started.");
        }
        else if (isRising)
        {
            // Standing jump — treat move start and jump as simultaneous
            State         = RecordState.InAir;
            stepTimer     = 0f;
            jumpTimer     = 0f;
            facingAtJump  = facing;
            jumpDetected  = true;
            log.Debug("JumpRecorder: standing jump detected.");
        }

        prevXZ = new Vector2(pos.X, pos.Z);
    }

    private void TickMoving(float deltaMs, Vector3 pos, float facing, float yDelta, bool isRising)
    {
        stepTimer += deltaMs;

        if (!jumpDetected && isRising)
        {
            // Jump detected mid-run
            jumpTimer    = stepTimer;
            facingAtJump = facing;
            jumpDetected = true;
            State        = RecordState.InAir;
            log.Debug($"JumpRecorder: jump at {jumpTimer:F0}ms, facing {facingAtJump:F2} rad.");
        }

        // If player stops completely without jumping, reset (false start)
        if (!XZMoved(pos) && !isRising && stepTimer > 300f)
        {
            log.Debug("JumpRecorder: movement stopped without jump — resetting step.");
            ResetStep();
            State = RecordState.WaitingForMove;
        }

        prevXZ = new Vector2(pos.X, pos.Z);
    }

    private void TickInAir(float deltaMs, Vector3 pos, float facing, bool isGrounded)
    {
        stepTimer += deltaMs;

        if (isGrounded)
        {
            landingDebounce += deltaMs;
            if (landingDebounce >= LandingDebounceMs)
                FinalizeStep();
        }
        else
        {
            landingDebounce = 0f;
        }

        prevXZ = new Vector2(pos.X, pos.Z);
    }

    // ── Step finalization ─────────────────────────────────────────────────────

    private void FinalizeStep()
    {
        var step = new JumpStep
        {
            FacingAngle    = facingAtJump,
            MoveDurationMs = MathF.Max(stepTimer - LandingDebounceMs, 100f),
            Jump           = true,
            JumpDelayMs    = jumpTimer,
        };

        CompletedSteps.Add(step);
        log.Info($"JumpRecorder: step {CompletedSteps.Count} saved — " +
                 $"facing={step.FacingAngle:F2} move={step.MoveDurationMs:F0}ms " +
                 $"jumpDelay={step.JumpDelayMs:F0}ms");

        ResetStep();
        State = RecordState.WaitingForMove;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Vector2 prevXZ = Vector2.Zero;

    private bool XZMoved(Vector3 pos)
        => Vector2.Distance(new Vector2(pos.X, pos.Z), prevXZ) > MoveXZThreshold;

    private void ResetStep()
    {
        stepTimer      = 0f;
        jumpTimer      = 0f;
        facingAtJump   = 0f;
        jumpDetected   = false;
        landingDebounce = 0f;
        wasRising      = false;
    }
}
