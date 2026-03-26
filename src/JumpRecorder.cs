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

    // ── Single-jump mode — auto-stop after one jump fires and lands ───────────
    // Used by the "Re-record Jump N" flow in FrameEditorWindow.
    // After landing is detected, OnSingleJumpComplete is fired with the captured
    // frames and recording stops automatically.
    private bool  _singleJumpMode   = false;
    private bool  _jumpFiredInMode  = false;
    private int   _landMinFrames    = 0;
    private int   _landStableCount  = 0;
    private float _lastY            = 0f;
    private bool  _hasDescended     = false;

    public bool IsSingleJumpMode => _singleJumpMode;
    public Action<List<RecordedFrame>>? OnSingleJumpComplete;

    private const int   LandMinWait   = 30;
    private const int   LandStableReq = 5;
    private const float LandYEps      = 0.005f;

    private readonly IPluginLog log;

    internal JumpRecorder(IPluginLog log) { this.log = log; }

    // ── Public API ────────────────────────────────────────────────────────────

    public void StartRecording(Vector3 pos, float facing)
    {
        CapturedFrames.Clear();
        prevKeySpace    = false;
        _singleJumpMode = false;
        State           = RecordState.Recording;
        log.Info("JumpRecorder: recording started.");
    }

    /// <summary>
    /// Starts recording a single jump. Recording stops automatically once the
    /// jump fires and the character lands. <see cref="OnSingleJumpComplete"/>
    /// is invoked with the captured frames.
    /// </summary>
    public void StartSingleJumpRecording()
    {
        CapturedFrames.Clear();
        prevKeySpace      = false;
        _singleJumpMode   = true;
        _jumpFiredInMode  = false;
        _landMinFrames    = 0;
        _landStableCount  = 0;
        _hasDescended     = false;
        State             = RecordState.Recording;
        log.Info("JumpRecorder: single-jump recording started.");
    }

    public void StopRecording()
    {
        State           = RecordState.Idle;
        _singleJumpMode = false;
        int jumps = CapturedFrames.Count(f => f.Jump);
        log.Info($"JumpRecorder: stopped — {CapturedFrames.Count} frames, {jumps} jump(s).");
    }

    public void ClearAll()
    {
        State           = RecordState.Idle;
        _singleJumpMode = false;
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

        // ── Single-jump mode: auto-stop after jump fires and landing detected ─
        if (_singleJumpMode)
        {
            if (!_jumpFiredInMode && jumpFire)
            {
                _jumpFiredInMode = true;
                _lastY           = pos.Y;
                _landMinFrames   = LandMinWait;
                _landStableCount = 0;
                _hasDescended    = false;
            }

            if (_jumpFiredInMode)
            {
                float yDelta = MathF.Abs(pos.Y - _lastY);
                bool  descFr = pos.Y < _lastY - LandYEps;
                _lastY = pos.Y;

                if (_landMinFrames > 0) { _landMinFrames--; }
                else
                {
                    if (descFr) _hasDescended = true;
                    if (_hasDescended)
                    {
                        if (yDelta < LandYEps) _landStableCount++;
                        else                   _landStableCount = 0;
                    }
                    if (_landStableCount >= LandStableReq)
                    {
                        var captured = new List<RecordedFrame>(CapturedFrames);
                        StopRecording();
                        OnSingleJumpComplete?.Invoke(captured);
                        return;
                    }
                }
            }
        }
    }
}
