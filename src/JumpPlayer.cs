using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace JumpSolver;

public enum PlayState { Idle, WalkingToStart, Playing }

internal sealed unsafe class JumpPlayer
{
    public PlayState State      { get; private set; } = PlayState.Idle;
    public int       FrameIndex { get; private set; }
    public bool      LastJumpFired { get; private set; }

    private JumpPuzzle?         puzzle;
    private List<RecordedFrame> playbackFrames = new();
    private float               frameTimer     = 0f;

    private readonly MoveHook    mover;
    private readonly IObjectTable objects;
    private readonly IChatGui    chat;
    private readonly IPluginLog  log;

    internal JumpPlayer(MoveHook mover, IObjectTable objects, IChatGui chat, IPluginLog log)
    {
        this.mover   = mover;
        this.objects = objects;
        this.chat    = chat;
        this.log     = log;
    }

    public int PlaybackFrameCount => playbackFrames.Count;

    // ── Public API ────────────────────────────────────────────────────────────

    public bool TryStart(JumpPuzzle puzzle, out string error)
    {
        error = string.Empty;
        if (puzzle.Start == null)          { error = "No start point set."; return false; }
        if (!puzzle.HasRecording)          { error = "No recording to play."; return false; }
        if (!mover.IsAvailable)            { error = "Movement hook unavailable."; return false; }

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
        State          = PlayState.Playing;

        mover.SetPlayerFacing(lp.Address, puzzle.Start.Facing);

        int jumps = playbackFrames.Count(f => f.Jump);
        chat.Print($"[JumpSolver] Playing '{puzzle.Name}' — {puzzle.Segments.Count} segment(s), {playbackFrames.Count} frames, {jumps} jump(s).");
        log.Info("JumpPlayer: playback started.");
        return true;
    }

    public bool TryWalkToStart(JumpPuzzle puzzle, out string error)
    {
        error = string.Empty;
        if (puzzle.Start == null) { error = "No start point set."; return false; }
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
        mover.ClearInjection();
        State = PlayState.Idle;
        log.Info($"JumpPlayer: stopped — {reason}");
    }

    public void Tick(float deltaMs, nint playerAddress, Vector3 playerPos)
    {
        LastJumpFired = false;
        switch (State)
        {
            case PlayState.WalkingToStart: TickWalkToStart(playerAddress, playerPos); break;
            case PlayState.Playing:        TickPlayback(deltaMs, playerAddress);       break;
        }
    }

    // ── Walk to start ─────────────────────────────────────────────────────────
    // Two phases:
    //   Coarse (dist > 0.6f) — face toward target, inject full forward.
    //   Fine   (dist ≤ 0.6f) — lock to recorded start facing, project the
    //                           remaining XZ offset into forward/strafe components
    //                           and inject proportionally (ramps to zero at target).
    // No position writes — pure RMI injection throughout.

    private const float WalkDoneThreshold = 0.05f;
    private const float WalkSlowDist     = 1.0f;   // start slowing down at this distance

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

            // Always face toward target — no phase switch, no jitter.
            // Speed ramps from 1.0 (full) down to 0 as we approach.
            // Starts slowing at WalkSlowDist, arrives smoothly at WalkDoneThreshold.
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

    private void TickPlayback(float deltaMs, nint playerAddress)
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

        // Prefer exact recorded RMI floats; fall back to binary Moving for old saves.
        if (frame.SumForward != 0f || frame.SumLeft != 0f)
        {
            mover.InjectedForward = frame.SumForward;
            mover.InjectedLeft    = frame.SumLeft;
        }
        else if (frame.Moving) mover.MoveForward();
        else                   mover.ClearInjection();

        if (frame.Jump && FireJump()) LastJumpFired = true;

        frameTimer += deltaMs;
        while (frameTimer >= playbackFrames[FrameIndex].DeltaMs)
        {
            frameTimer -= playbackFrames[FrameIndex].DeltaMs;
            FrameIndex++;
            if (FrameIndex >= playbackFrames.Count) break;
        }
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
