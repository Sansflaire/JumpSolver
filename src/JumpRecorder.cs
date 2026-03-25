using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Plugin.Services;

namespace JumpSolver;

public enum RecordState { Idle, Recording }

internal sealed class JumpRecorder
{
    // ── Public state ──────────────────────────────────────────────────────────
    public RecordState         State          { get; private set; } = RecordState.Idle;
    public List<RecordedFrame> CapturedFrames { get; private set; } = new();

    // ── Frame tracking ────────────────────────────────────────────────────────
    // Jump detection uses spacebar edge (pressed this frame, not last frame).
    // This is exact — Y-delta inference fires 1–3 frames late.
    private bool prevKeySpace = false;

    private readonly IPluginLog log;

    internal JumpRecorder(IPluginLog log) { this.log = log; }

    // ── Public API ────────────────────────────────────────────────────────────

    public void StartRecording(Vector3 pos, float facing)
    {
        CapturedFrames.Clear();
        prevKeySpace = false;
        State        = RecordState.Recording;
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

    public void Tick(float deltaMs, Vector3 pos, float facing,
                     float sumForward = 0f, float sumLeft = 0f, bool keySpace = false,
                     float cameraYaw = 0f, float cameraPitch = 0f)
    {
        if (State != RecordState.Recording) return;

        // Fire jump on the exact frame Space is first pressed (edge detection).
        // Y-delta inference fires 1–3 frames late and can double-trigger.
        bool jumpFire = keySpace && !prevKeySpace;

        CapturedFrames.Add(new RecordedFrame
        {
            DeltaMs     = deltaMs,
            Facing      = facing,
            PosX        = pos.X,
            PosY        = pos.Y,
            PosZ        = pos.Z,
            Moving      = sumForward != 0f || sumLeft != 0f,
            Jump        = jumpFire,
            SumForward  = sumForward,
            SumLeft     = sumLeft,
            CameraYaw   = cameraYaw,
            CameraPitch = cameraPitch,
        });

        prevKeySpace = keySpace;
    }
}
