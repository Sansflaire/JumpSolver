using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Plugin.Services;

namespace JumpSolver;

public enum RecordState { Idle, Recording }

internal sealed class JumpRecorder
{
    // ── Detection thresholds ──────────────────────────────────────────────────
    private const float AirborneYDelta    = 0.05f;   // Y rise/sample = jumped
    private const float PlantedThreshold  = 0.004f;
    private const int   MinGroundedFrames = 4;
    private const int   PostJumpLockoutTicks = 20;

    // ── Public state ──────────────────────────────────────────────────────────
    public RecordState         State          { get; private set; } = RecordState.Idle;
    public List<RecordedFrame> CapturedFrames { get; private set; } = new();

    // ── Frame tracking ────────────────────────────────────────────────────────
    private bool  prevRising        = false;
    private float prevY             = 0f;
    private bool  jumpInAir         = false;
    private int   groundedFrameCount = 0;
    private int   postJumpLockout   = 0;

    private readonly IPluginLog log;

    internal JumpRecorder(IPluginLog log) { this.log = log; }

    // ── Public API ────────────────────────────────────────────────────────────

    public void StartRecording(Vector3 pos, float facing)
    {
        CapturedFrames.Clear();
        prevY              = pos.Y;
        prevRising         = false;
        jumpInAir          = false;
        groundedFrameCount = 0;
        postJumpLockout    = 0;
        State              = RecordState.Recording;
        log.Info("JumpRecorder: recording started.");
    }

    public void StopRecording()
    {
        State = RecordState.Idle;
        int jumps = CapturedFrames.Count(f => f.Jump);
        log.Info($"JumpRecorder: stopped — {CapturedFrames.Count} frames, {jumps} jump(s).");
    }

    public void ClearAll()
    {
        State = RecordState.Idle;
        CapturedFrames.Clear();
    }

    public void Tick(float deltaMs, Vector3 pos, float facing, float sumForward = 0f, float sumLeft = 0f)
    {
        if (State != RecordState.Recording) return;

        float yDelta  = pos.Y - prevY;
        bool  rising  = yDelta > AirborneYDelta;

        if (jumpInAir)
        {
            if (MathF.Abs(yDelta) < PlantedThreshold) groundedFrameCount++;
            else                                       groundedFrameCount = 0;
            if (groundedFrameCount >= MinGroundedFrames) { jumpInAir = false; groundedFrameCount = 0; }
        }
        if (postJumpLockout > 0) postJumpLockout--;

        bool jumpFire = rising && !prevRising && !jumpInAir && postJumpLockout == 0;
        if (jumpFire) { jumpInAir = true; groundedFrameCount = 0; postJumpLockout = PostJumpLockoutTicks; }

        CapturedFrames.Add(new RecordedFrame
        {
            DeltaMs    = deltaMs,
            Facing     = facing,
            PosX       = pos.X,
            PosZ       = pos.Z,
            Moving     = sumForward != 0f || sumLeft != 0f,
            Jump       = jumpFire,
            SumForward = sumForward,
            SumLeft    = sumLeft,
        });

        prevY      = pos.Y;
        prevRising = rising;
    }
}
