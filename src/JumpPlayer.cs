using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace JumpSolver;

public enum PlayState { Idle, WalkingToStart, Playing, WpWaitLand, WpNav }

internal sealed unsafe class JumpPlayer
{
    public PlayState State        { get; private set; } = PlayState.Idle;
    public int       FrameIndex   { get; private set; }
    public bool      LastJumpFired { get; private set; }

    private JumpPuzzle?         puzzle;
    private List<RecordedFrame> playbackFrames = new();
    private float               frameTimer     = 0f;

    // ── Waypoint state ────────────────────────────────────────────────────────
    // One waypoint per jump in the recording.
    // NavX/Z  = start-of-run-up position for this jump (where we navigate to after landing).
    // NavFacing = facing at that position.
    // ResumeFrameIndex = frame index to resume Playing from after nav.
    private readonly record struct JumpWaypoint(
        float NavX, float NavZ, float NavY,
        float NavFacing,
        int   ResumeFrameIndex);

    private List<JumpWaypoint> waypoints     = new();
    private int                waypointIndex = 0;   // index of the next waypoint to nav to

    // ── Landing detection ─────────────────────────────────────────────────────
    private int   landingMinFrames   = 0;
    private int   landingStableCount = 0;
    private float lastY              = 0f;
    private bool  hasDescended       = false;  // must have dropped after apex before counting stable frames
    private const int   LandingMinWait   = 30;      // frames after jump before checking stability
    private const int   LandingStableReq = 5;       // consecutive stable frames = landed
    private const float LandingYEps      = 0.005f;

    // ── Airborne injection — keep forward input active during jump flight ────────
    // Stored at jump time, injected every WpWaitLand tick, cleared on landing.
    private float airborneInjFwd  = 0f;
    private float airborneInjLeft = 0f;
    private float airborneFacing  = 0f;   // initial facing (camera-alignment only)

    // Frame-tracking for mid-air facing replay.
    // The recording often has Facing changes during the jump arc (player steers mid-air
    // by rotating the camera). We step through recording frames and replay those Facing
    // values so the injection direction matches the original trajectory.
    private int   airFrameIndex = 0;
    private float airFrameTimer = 0f;

    // ── Jump retry — hold frame and retry if UseAction is rejected ────────────
    private bool pendingJump      = false;
    private int  jumpRetryFrames  = 0;
    private const int JumpRetryMax = 120;  // give up after ~2s

    // ── Camera replay ─────────────────────────────────────────────────────────
    // Camera yaw is rate-limited everywhere to prevent rapid spinning.
    // 0.08 rad/frame ≈ 4.6°/frame ≈ 288°/s at 60fps — fast enough to track turns,
    // slow enough to be smooth to the eye.
    private const float MaxCamDelta = 0.08f;

    private static float ApplyCamAngle(float current, float target)
    {
        // Shortest-path delta, wrapped to (-π, π].
        float delta = target - current;
        while (delta >  MathF.PI) delta -= 2f * MathF.PI;
        while (delta < -MathF.PI) delta += 2f * MathF.PI;
        // Cap movement so wrap-boundary recordings can't snap.
        delta = Math.Clamp(delta, -MaxCamDelta, MaxCamDelta);
        return current + delta;
    }

    // Timeout guards — if we get stuck, abort rather than loop forever.
    private int stateTimeoutFrames = 0;
    private const int LandTimeout  = 600;   // ~12 s
    private const int NavTimeout   = 1500;  // ~30 s

    // ── Waypoint nav target ───────────────────────────────────────────────────
    private float navTargetX, navTargetZ, navTargetY, navFacing;
    private int   navResumeFrame;

    // ── vnavmesh IPC — optional terrain-following WpNav ───────────────────────
    // When available, vnavmesh pathfinds around platform edges instead of walking
    // a straight line that may clip the corner of a narrow ledge.
    // All InvokeFunc calls are wrapped in try/catch — vnavmesh may not be loaded.
    private readonly ICallGateSubscriber<bool>?                      _vnavIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, Task<bool>>? _vnavMoveTo;
    private readonly ICallGateSubscriber<object?>?                   _vnavStop;
    private bool _vnavActive = false;  // true while vnavmesh is driving WpNav movement


    private readonly MoveHook     mover;
    private readonly IObjectTable objects;
    private readonly IChatGui     chat;
    private readonly IPluginLog   log;

    internal JumpPlayer(MoveHook mover, IObjectTable objects, IChatGui chat, IPluginLog log)
    {
        this.mover   = mover;
        this.objects = objects;
        this.chat    = chat;
        this.log     = log;

        // vnavmesh IPC — GetIpcSubscriber never throws; InvokeFunc throws if provider is absent.
        _vnavIsReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _vnavMoveTo  = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, Task<bool>>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        _vnavStop    = Plugin.PluginInterface.GetIpcSubscriber<object?>("vnavmesh.Path.Stop");
    }

    public int PlaybackFrameCount => playbackFrames.Count;

    // ── Public API ────────────────────────────────────────────────────────────

    public bool TryStart(JumpPuzzle puzzle, out string error)
    {
        error = string.Empty;
        if (puzzle.Start == null)     { error = "No start point set.";        return false; }
        if (!puzzle.HasRecording)     { error = "No recording to play.";       return false; }
        if (!mover.IsAvailable)       { error = "Movement hook unavailable.";  return false; }

        var lp = objects.LocalPlayer;
        if (lp == null) { error = "Not logged in."; return false; }

        float dist = Vector3.Distance(lp.Position, puzzle.Start.Position);
        if (dist > puzzle.Start.SnapRadius)
        {
            error = $"Too far from start ({dist:F1} yalms, need ≤ {puzzle.Start.SnapRadius:F0}). Use Go To Start.";
            return false;
        }

        this.puzzle    = puzzle;
        playbackFrames = puzzle.Segments.SelectMany(s => s.Frames).ToList();
        FrameIndex     = 0;
        frameTimer     = 0f;
        waypointIndex  = 0;
        waypoints      = BuildWaypoints(playbackFrames);

        mover.SetPlayerFacing(lp.Address, puzzle.Start.Facing);

        if (waypoints.Count > 0)
        {
            // Navigate to the run-up start for the first jump (same path as subsequent jumps).
            // This ensures:
            //   (a) character arrives at the correct terrain level before the jump fires,
            //   (b) any step-up animation lock has time to clear during the short run-up replay.
            var wp0 = waypoints[0];
            navTargetX         = wp0.NavX;
            navTargetZ         = wp0.NavZ;
            navTargetY         = wp0.NavY;
            navFacing          = wp0.NavFacing;
            navResumeFrame     = wp0.ResumeFrameIndex;
            _vnavActive        = false;
            stateTimeoutFrames = NavTimeout;
            State              = PlayState.WpNav;
        }
        else
        {
            // No jumps — just replay directly from frame 0.
            State = PlayState.Playing;
        }

        int jumps = playbackFrames.Count(f => f.Jump);
        chat.Print(
            $"[JumpSolver] Playing '{puzzle.Name}' — {puzzle.Segments.Count} seg(s), " +
            $"{playbackFrames.Count} frames, {jumps} jump(s)" +
            (waypoints.Count > 0 ? " [waypoint mode]" : "") + ".");
        log.Info($"JumpPlayer: playback started — {waypoints.Count} waypoint(s).");
        return true;
    }

    public bool TryWalkToStart(JumpPuzzle puzzle, out string error)
    {
        error = string.Empty;
        if (puzzle.Start == null) { error = "No start point set.";       return false; }
        if (!mover.IsAvailable)   { error = "Movement hook unavailable."; return false; }

        var lp = objects.LocalPlayer;
        if (lp == null) { error = "Not logged in."; return false; }

        float dist = Vector3.Distance(lp.Position, puzzle.Start.Position);
        if (dist > 60f)
        {
            error = $"Too far from start ({dist:F0} yalms). Get within 60 yalms first.";
            return false;
        }

        this.puzzle = puzzle;
        State       = PlayState.WalkingToStart;
        log.Info($"JumpPlayer: walking to start ({dist:F1} yalms).");
        return true;
    }

    public void Stop(string reason)
    {
        StopVnavmesh();
        mover.ClearInjection();
        State = PlayState.Idle;
        log.Info($"JumpPlayer: stopped — {reason}");
    }

    private void StopVnavmesh()
    {
        if (!_vnavActive) return;
        _vnavActive = false;
        try { _vnavStop?.InvokeFunc(); }
        catch { /* vnavmesh not loaded or IPC unavailable */ }
    }

    public void Tick(float deltaMs, nint playerAddress, Vector3 playerPos)
    {
        LastJumpFired = false;
        switch (State)
        {
            case PlayState.WalkingToStart: TickWalkToStart(playerAddress, playerPos);       break;
            case PlayState.Playing:        TickPlayback(deltaMs, playerAddress, playerPos); break;
            case PlayState.WpWaitLand:     TickWpWaitLand(deltaMs, playerAddress, playerPos); break;
            case PlayState.WpNav:          TickWpNav(playerAddress, playerPos);             break;
        }
    }

    // ── Walk to start ─────────────────────────────────────────────────────────

    private const float WalkDoneThreshold = 0.05f;
    private const float WalkSlowDist      = 1.0f;

    private void TickWalkToStart(nint playerAddress, Vector3 playerPos)
    {
        try
        {
            var   target = puzzle!.Start!.Position;
            float dx     = target.X - playerPos.X;
            float dz     = target.Z - playerPos.Z;
            float dist   = MathF.Sqrt(dx * dx + dz * dz);

            if (dist < WalkDoneThreshold)
            {
                mover.ClearInjection();
                mover.SetPlayerFacing(playerAddress, puzzle.Start.Facing);
                State = PlayState.Idle;
                chat.Print("[JumpSolver] Aligned to start — ready to play.");
                log.Info("JumpPlayer: aligned at start.");
                return;
            }

            mover.SetPlayerFacing(playerAddress, MathF.Atan2(dx, dz));
            mover.InjectedForward = Math.Clamp(dist / WalkSlowDist, 0f, 1f);
            mover.InjectedLeft    = 0f;
        }
        catch (Exception ex)
        {
            log.Error(ex, "JumpPlayer: exception in TickWalkToStart.");
            Stop("walk error");
        }
    }

    // ── Frame playback ────────────────────────────────────────────────────────

    private void TickPlayback(float deltaMs, nint playerAddress, Vector3 playerPos)
    {
        if (FrameIndex >= playbackFrames.Count)
        {
            mover.ClearInjection();
            State = PlayState.Idle;
            chat.Print("[JumpSolver] Done!");
            log.Info("JumpPlayer: playback complete.");
            return;
        }

        var frame = playbackFrames[FrameIndex];

        mover.SetPlayerFacing(playerAddress, frame.Facing);

        // Camera yaw = character facing + π puts the camera BEHIND the character.
        // Rate-limited via ApplyCamAngle so transitions after WpNav arrival are smooth.
        {
            var (curYaw, curPitch) = mover.GetCameraAngles();
            float targetPitch = (frame.CameraPitch != 0f)
                ? ApplyCamAngle(curPitch, frame.CameraPitch)
                : curPitch;
            float behindYaw = frame.Facing + MathF.PI;
            if (behindYaw > MathF.PI) behindYaw -= 2f * MathF.PI;
            mover.SetCameraAngles(ApplyCamAngle(curYaw, behindYaw), targetPitch);
        }

        // Prefer exact recorded RMI floats; fall back to binary Moving for old saves.
        if (frame.SumForward != 0f || frame.SumLeft != 0f)
        {
            mover.InjectedForward = frame.SumForward;
            mover.InjectedLeft    = frame.SumLeft;
        }
        else if (frame.Moving) mover.MoveForward();
        else                   mover.ClearInjection();

        if (frame.Jump || pendingJump)
        {
            if (FireJump())
            {
                LastJumpFired = true;
                pendingJump   = false;
                jumpRetryFrames = 0;

                // Always enter WpWaitLand so airborne injection stays active throughout the
                // flight arc. FFXIV has no momentum — clearing injection mid-air stops the
                // character dead. For intermediate jumps, WpWaitLand also navigates to the
                // next run-up start. For the last jump it just waits for landing then resumes.
                waypointIndex++;
                // If either SumForward or SumLeft is non-zero, this is a modern recording with
                // exact RMI float data — trust SumForward directly even if it is zero (pure strafe).
                // Only fall back to the binary Moving flag for old recordings that have neither.
                airborneInjFwd  = (frame.SumForward != 0f || frame.SumLeft != 0f)
                                  ? frame.SumForward
                                  : (frame.Moving ? 1f : 0f);
                airborneInjLeft = frame.SumLeft;
                airborneFacing  = frame.Facing;
                airFrameIndex   = FrameIndex;   // step through frames to replay facing during flight
                airFrameTimer   = 0f;
                State              = PlayState.WpWaitLand;
                landingMinFrames   = LandingMinWait;
                landingStableCount = 0;
                hasDescended       = false;
                stateTimeoutFrames = LandTimeout;
                lastY              = playerPos.Y;
                log.Info($"JumpPlayer: jump {waypointIndex} fired — waiting for landing.");
                return;  // don't advance frame; FrameIndex will be set by WpNav on resume
            }
            else
            {
                // UseAction rejected — hold this frame and retry next tick.
                jumpRetryFrames++;
                if (jumpRetryFrames >= JumpRetryMax)
                {
                    log.Warning($"JumpPlayer: jump at frame {FrameIndex} rejected {JumpRetryMax} times — giving up.");
                    pendingJump     = false;
                    jumpRetryFrames = 0;
                }
                else
                {
                    pendingJump = true;
                    return;  // stay on this frame, retry next tick
                }
            }
        }

        // Advance frame timer.
        // IMPORTANT: Stop at jump frames so they are always read and processed on their
        // own game tick. If the recording fps > playback fps (e.g. recorded at 150fps,
        // replayed at 60fps), the while loop can consume multiple recording frames per
        // tick and would silently advance THROUGH a jump frame, skipping the jump entirely.
        frameTimer += deltaMs;
        while (FrameIndex < playbackFrames.Count && frameTimer >= playbackFrames[FrameIndex].DeltaMs)
        {
            frameTimer -= playbackFrames[FrameIndex].DeltaMs;
            FrameIndex++;
            // If we just landed on a jump frame, stop here — process it next tick.
            if (FrameIndex < playbackFrames.Count && playbackFrames[FrameIndex].Jump) break;
        }
    }

    // ── Wait for landing ──────────────────────────────────────────────────────

    private void TickWpWaitLand(float deltaMs, nint playerAddress, Vector3 playerPos)
    {
        try
        {
            // Advance the airborne frame pointer using the same DeltaMs-based timing as
            // TickPlayback. This lets us replay the Facing values from the recording during
            // the flight arc, replicating any mid-air steering the player performed.
            airFrameTimer += deltaMs;
            while (airFrameIndex + 1 < playbackFrames.Count)
            {
                float nextDms = playbackFrames[airFrameIndex + 1].DeltaMs;
                if (airFrameTimer < nextDms) break;
                airFrameTimer -= nextDms;
                airFrameIndex++;
            }
            float currentFacing = (airFrameIndex < playbackFrames.Count)
                ? playbackFrames[airFrameIndex].Facing
                : airborneFacing;

            // Keep injecting forward input throughout the flight.
            // FFXIV has no momentum — RMI input is the forward force every frame.
            // Clearing mid-air stops the character dead; we only clear on landing.
            // currentFacing tracks mid-air steering from the recording.
            mover.SetPlayerFacing(playerAddress, currentFacing);
            mover.InjectedForward = airborneInjFwd;
            mover.InjectedLeft    = airborneInjLeft;

            // Keep camera behind the character during flight so there's no snap
            // when WpNav later smoothly approaches navFacing + π.
            {
                var (_, curPitch) = mover.GetCameraAngles();
                float airYaw = airborneFacing + MathF.PI;
                if (airYaw > MathF.PI) airYaw -= 2f * MathF.PI;
                mover.SetCameraAngles(airYaw, curPitch);
            }

            if (--stateTimeoutFrames <= 0)
            {
                chat.Print("[JumpSolver] Landing detection timed out — aborted.");
                Stop("landing timeout");
                return;
            }

            float yDelta   = MathF.Abs(playerPos.Y - lastY);
            bool  descFrame = playerPos.Y < lastY - LandingYEps;  // Y dropped by more than eps
            lastY = playerPos.Y;

            if (landingMinFrames > 0)
            {
                landingMinFrames--;
                return;  // don't start stability check until minimum frames elapsed
                         // (don't set hasDescended here — post-jump Y jitter in this window
                         //  would prematurely arm descent detection before real apex descent)
            }

            if (descFrame) hasDescended = true;

            // Only accumulate stable frames after the character has clearly started descending.
            // At the jump apex, Y barely moves for several frames — without this guard those
            // frames would falsely trigger landing detection while the character is still mid-air.
            if (hasDescended)
            {
                if (yDelta < LandingYEps) landingStableCount++;
                else                      landingStableCount = 0;
            }

            if (landingStableCount >= LandingStableReq)
            {
                // Landed — clear injection now that we're on solid ground.
                mover.ClearInjection();

                if (waypointIndex >= waypoints.Count)
                {
                    // Last jump — no more run-ups to navigate to. Resume normal playback
                    // from the frame after the jump (FrameIndex is still at the jump frame).
                    FrameIndex++;
                    frameTimer = 0f;
                    State      = PlayState.Playing;
                    log.Info($"JumpPlayer: last jump landed, resuming playback at frame {FrameIndex}.");
                }
                else
                {
                    // Navigate directly to the run-up start for the next waypoint.
                    // (WpRotate was removed — its standing-still body spin was worse than the
                    // brief facing snap on WpNav frame 1. Camera alignment is handled smoothly
                    // by ApplyCamAngle in TickWpNav and TickPlayback.)
                    var wp         = waypoints[waypointIndex];
                    navTargetX     = wp.NavX;
                    navTargetZ     = wp.NavZ;
                    navTargetY     = wp.NavY;
                    navFacing      = wp.NavFacing;
                    navResumeFrame = wp.ResumeFrameIndex;
                    _vnavActive    = false;

                    stateTimeoutFrames = NavTimeout;
                    State = PlayState.WpNav;
                    log.Info($"JumpPlayer: landed wp {waypointIndex} — navigating to " +
                             $"({wp.NavX:F2}, {wp.NavZ:F2}), resume frame {wp.ResumeFrameIndex}.");
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "JumpPlayer: exception in TickWpWaitLand.");
            Stop("landing error");
        }
    }

    // ── Waypoint navigation ───────────────────────────────────────────────────

    private void TickWpNav(nint playerAddress, Vector3 playerPos)
    {
        try
        {
            if (--stateTimeoutFrames <= 0)
            {
                chat.Print("[JumpSolver] Waypoint navigation timed out — aborted.");
                Stop("nav timeout");
                return;
            }

            float dx   = navTargetX - playerPos.X;
            float dz   = navTargetZ - playerPos.Z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);

            if (dist < WalkDoneThreshold)
            {
                StopVnavmesh();
                mover.ClearInjection();
                mover.SetPlayerFacing(playerAddress, navFacing);
                // Don't hard-set camera here — let TickPlayback's ApplyCamAngle track it
                // smoothly from whatever angle it reached during WpNav.
                FrameIndex = navResumeFrame;
                frameTimer = 0f;
                State      = PlayState.Playing;
                log.Info($"JumpPlayer: reached wp {waypointIndex}, resuming at frame {navResumeFrame}.");
                return;
            }

            // Try to start vnavmesh terrain-following on the first tick of this WpNav phase.
            // vnavmesh pathfinds around platform edges; straight-line injection can clip ledge corners.
            // If vnavmesh is unavailable or not ready for this zone, fall through to straight-line.
            if (!_vnavActive)
                _vnavActive = TryStartVnavmesh();

            if (!_vnavActive)
            {
                // Fallback: straight-line injection toward nav target.
                mover.SetPlayerFacing(playerAddress, MathF.Atan2(dx, dz));
                mover.InjectedForward = Math.Clamp(dist / WalkSlowDist, 0f, 1f);
                mover.InjectedLeft    = 0f;
            }
            // else: vnavmesh is driving movement — JumpSolver injection already cleared.

            // Smoothly pre-align camera to navFacing + π so there is no DirH snap
            // when playback resumes. Character facing still snaps 1 frame at arrival
            // but Injecting=0 on that frame so the game can't steer the character.
            {
                var (curYaw, curPitch) = mover.GetCameraAngles();
                float targetYaw = navFacing + MathF.PI;
                if (targetYaw > MathF.PI) targetYaw -= 2f * MathF.PI;
                mover.SetCameraAngles(ApplyCamAngle(curYaw, targetYaw), curPitch);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "JumpPlayer: exception in TickWpNav.");
            Stop("waypoint nav error");
        }
    }

    // ── vnavmesh helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Starts vnavmesh terrain-following navigation toward the current nav target.
    /// Clears JumpSolver injection so vnavmesh drives freely.
    /// Returns true if vnavmesh accepted the request, false if unavailable/not ready.
    /// </summary>
    private bool TryStartVnavmesh()
    {
        try
        {
            if (_vnavIsReady == null || _vnavMoveTo == null) return false;
            if (!_vnavIsReady.InvokeFunc())                  return false;
            var dest = new Vector3(navTargetX, navTargetY, navTargetZ);
            _vnavMoveTo.InvokeFunc(dest, false);
            mover.ClearInjection();  // vnavmesh drives; don't fight it
            log.Debug($"JumpSolver: vnavmesh navigating to ({navTargetX:F2}, {navTargetY:F2}, {navTargetZ:F2}).");
            return true;
        }
        catch (Exception ex)
        {
            log.Debug($"JumpSolver: vnavmesh unavailable, falling back to straight-line nav: {ex.Message}");
            return false;
        }
    }

    // ── Waypoint pre-processing ───────────────────────────────────────────────
    // For each jump frame: find a "nav target" slightly before any terrain step in the
    // run-up so the character:
    //   (a) arrives at the correct terrain level (Y) before the jump fires, and
    //   (b) traverses the step naturally during a short run-up replay, giving any
    //       step-up animation lock time to clear before UseAction is called.
    //
    // Observation (Example4): navigating directly to the jump frame's XZ caused the
    // character to arrive at Y=-14.0 (ground) instead of Y=-13.5 (step platform)
    // because the straight-line WpNav walk stepped off the platform edge.  The game
    // then silently rejected UseAction for 2+ seconds (step-up animation lock), the
    // retry window expired, and the jump fired from the wrong position after Playing
    // advanced past the skipped frame.

    private const int   MaxRunUpSearch    = 90;   // frames to scan back for a Y step (~1.5 s)
    private const int   PreStepPad        = 8;    // frames before the step to use as nav target
    private const float RunUpStepYEps     = 0.3f; // min |ΔY| between consecutive frames = step
    private const int   MinStandstillFrames = 10;  // min consecutive zero-input frames to qualify as a standstill

    private static List<JumpWaypoint> BuildWaypoints(List<RecordedFrame> frames)
    {
        var result = new List<JumpWaypoint>();
        for (int i = 0; i < frames.Count; i++)
        {
            if (!frames[i].Jump) continue;

            // ── Step 1: initial navIdx — Y-step detection or 90-frame fallback ──────
            // Scan backward to find a terrain step-up in the run-up. If found, place
            // navIdx before the step so the character approaches from the lower level.
            // If not found, fall back to 90 frames before the jump.
            int searchFrom = Math.Max(1, i - MaxRunUpSearch);
            int navIdx     = Math.Max(0, i - MaxRunUpSearch);
            bool hasYStep  = false;

            for (int k = i - 1; k >= searchFrom; k--)
            {
                if (frames[k].PosY - frames[k - 1].PosY > RunUpStepYEps)   // upward steps only — landings are downward and must not suppress standstill detection
                {
                    navIdx   = Math.Max(0, k - 1 - PreStepPad);
                    hasYStep = true;
                    break;
                }
            }

            // ── Step 2: find the last pre-jump standstill block ──────────────────────
            // Players typically record: approach → standstill → short run-up → jump.
            // Scan backward from the jump to find the last contiguous block of frames
            // where the player had zero forward/left input (standing still).
            // stillEnd   = last standstill frame (just before run-up starts)
            // stillStart = first standstill frame (where player first stopped)
            int stillEnd   = -1;
            int stillStart = -1;
            for (int k = i - 1; k > navIdx; k--)
            {
                bool zero = frames[k].SumForward == 0f && frames[k].SumLeft == 0f;
                if (stillEnd < 0 && !zero)   continue;              // still in run-up, skip
                if (stillEnd < 0)            { stillEnd = k; continue; } // entered standstill boundary
                if (zero)                    continue;               // inside standstill
                stillStart = k + 1;                                  // found run→standstill transition
                break;
            }
            // If standstill extends all the way back to navIdx, clamp stillStart there.
            if (stillEnd >= 0 && stillStart < 0) stillStart = navIdx + 1;

            // ── Step 3: apply standstill-aware nav target and resume frame ───────────
            // If a long-enough standstill exists:
            //   • runUpStart = first real run-up frame after the standstill ends
            //   • navIdx    = standstill start position (stable resting point), UNLESS a
            //                  Y-step was found — in that case we must approach from below
            //                  so the character steps up naturally; keep existing navIdx.
            // This eliminates standstill replay (dead time) and routes WpNav to the
            // exact stable position the player stood at before each jump, rather than
            // an arbitrary mid-run point 90 frames before the jump.
            int runUpStart = navIdx;  // default: resume from navIdx as before
            if (!hasYStep &&
                stillStart >= 0 && stillEnd >= stillStart &&
                (stillEnd - stillStart + 1) >= MinStandstillFrames)
            {
                // No Y-step: navigate to the stable resting position and skip standstill replay.
                // The character walks straight into the short run-up after WpNav arrives.
                navIdx     = stillStart;
                runUpStart = stillEnd + 1;
                // With Y-step: do NOT apply this optimisation. The full replay from the pre-step
                // nav target must include the step traversal and standstill so the step-up
                // animation lock clears before UseAction fires.
            }

            result.Add(new JumpWaypoint(
                frames[navIdx].PosX,
                frames[navIdx].PosZ,
                frames[navIdx].PosY,
                frames[navIdx].Facing,
                runUpStart));
        }
        return result;
    }

    // ── Jump ──────────────────────────────────────────────────────────────────

    private bool FireJump()
    {
        try
        {
            var am = ActionManager.Instance();
            if (am == null) return false;
            bool ok = am->UseAction((ActionType)5, Signatures.JumpActionId, 0xE0000000);
            if (!ok) log.Warning($"JumpPlayer: jump rejected by game at frame {FrameIndex}.");
            return ok;
        }
        catch (Exception ex)
        {
            log.Error(ex, "JumpPlayer: exception firing jump.");
            return false;
        }
    }
}
