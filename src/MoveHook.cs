using System;

using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace JumpSolver;

/// <summary>
/// Owns the RMIWalk hook for movement injection and the player rotation write.
///
/// Usage:
///   • Set <see cref="InjectedForward"/> (0..1) and <see cref="InjectedLeft"/> (-1..1)
///     before each frame when you want movement. The hook will apply them.
///   • Call <see cref="ClearInjection"/> when the step is done to stop moving.
///   • Call <see cref="SetPlayerFacing"/> to snap the character's rotation instantly.
///   • <see cref="IsAvailable"/> is false if the sig scan failed — all ops become no-ops.
/// </summary>
internal sealed unsafe class MoveHook : IDisposable
{
    // ── Injected movement, written by JumpPlayer / control commands each frame ─
    public float InjectedForward  { get; set; }   //  1 = full forward,  -1 = backward
    public float InjectedLeft     { get; set; }   //  1 = full left,     -1 = right (strafe)
    public float InjectedTurnLeft { get; set; }   //  1 = turn left,     -1 = turn right
    public bool  Injecting        => InjectedForward != 0f || InjectedLeft != 0f || InjectedTurnLeft != 0f;

    // ── Natural (pre-injection) values from last RMI frame ───────────────────
    // HandleMoveInput is called multiple times per Framework.Update tick.
    // LastNatural* = value from the most recent call (may be a spurious zero).
    // PeakNatural* = highest-magnitude value seen since ResetNaturalPeaks() was last called.
    // Use PeakNatural* for display/recording; call ResetNaturalPeaks() at the start of each tick.
    public float LastNaturalForward  { get; private set; }
    public float LastNaturalLeft     { get; private set; }
    public float LastNaturalTurnLeft { get; private set; }

    public float PeakNaturalForward  { get; private set; }
    public float PeakNaturalLeft     { get; private set; }
    public float PeakNaturalTurnLeft { get; private set; }

    public void ResetNaturalPeaks()
    {
        PeakNaturalForward  = 0f;
        PeakNaturalLeft     = 0f;
        PeakNaturalTurnLeft = 0f;
    }

    // ── Whether the hook was installed successfully ───────────────────────────
    public bool IsAvailable => rmiWalkHook != null;

    // ── RMIWalk delegate ─────────────────────────────────────────────────────
    // bool HandleMoveInput(void* self,
    //   float* sumLeft, float* sumForward, float* sumTurnLeft,
    //   byte* haveBackwardOrStrafe, byte* a6, byte a7)
    private delegate bool RMIWalkDelegate(
        void* self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte*  haveBackwardOrStrafe,
        byte*  a6,
        byte   a7);

    private Hook<RMIWalkDelegate>? rmiWalkHook;
    private readonly IPluginLog log;

    // ─────────────────────────────────────────────────────────────────────────

    public MoveHook(IGameInteropProvider gameInterop, IPluginLog log)
    {
        this.log = log;

        try
        {
            rmiWalkHook = gameInterop.HookFromSignature<RMIWalkDelegate>(
                Signatures.RMIWalk,
                RMIWalkDetour);
            rmiWalkHook.Enable();
            log.Info("JumpSolver: RMIWalk hook installed.");
        }
        catch (Exception ex)
        {
            log.Warning(ex,
                "JumpSolver: RMIWalk signature scan failed — movement injection disabled. " +
                "Update Signatures.RMIWalk and rebuild.");
            rmiWalkHook = null;
        }
    }

    public void Dispose()
    {
        rmiWalkHook?.Dispose();
    }

    // ── Hook detour ───────────────────────────────────────────────────────────

    private bool RMIWalkDetour(
        void* self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte*  haveBackwardOrStrafe,
        byte*  a6,
        byte   a7)
    {
        try
        {
            // Always call original first so the rest of the game logic runs normally.
            bool result = rmiWalkHook!.Original(
                self, sumLeft, sumForward, sumTurnLeft,
                haveBackwardOrStrafe, a6, a7);

            LastNaturalForward  = *sumForward;
            LastNaturalLeft     = *sumLeft;
            LastNaturalTurnLeft = *sumTurnLeft;

            // Track highest-magnitude values seen this tick for stable display/recording.
            if (MathF.Abs(*sumForward)   > MathF.Abs(PeakNaturalForward))  PeakNaturalForward  = *sumForward;
            if (MathF.Abs(*sumLeft)      > MathF.Abs(PeakNaturalLeft))     PeakNaturalLeft     = *sumLeft;
            if (MathF.Abs(*sumTurnLeft)  > MathF.Abs(PeakNaturalTurnLeft)) PeakNaturalTurnLeft = *sumTurnLeft;

            if (Injecting)
            {
                *sumForward           = InjectedForward;
                *sumLeft              = InjectedLeft;
                *sumTurnLeft          = InjectedTurnLeft;
                *haveBackwardOrStrafe = (byte)(InjectedLeft != 0f ? 1 : 0);
            }

            return result;
        }
        catch (Exception ex)
        {
            log.Error(ex, "JumpSolver: exception in RMIWalk detour.");
            return rmiWalkHook!.Original(self, sumLeft, sumForward, sumTurnLeft,
                haveBackwardOrStrafe, a6, a7);
        }
    }

    // ── Movement control ──────────────────────────────────────────────────────

    /// <summary>Hold forward at full speed.</summary>
    public void MoveForward() { InjectedForward = 1f; InjectedLeft = 0f; }

    /// <summary>Stop all injected movement.</summary>
    public void ClearInjection() { InjectedForward = 0f; InjectedLeft = 0f; InjectedTurnLeft = 0f; }

    // ── Rotation / facing ─────────────────────────────────────────────────────

    /// <summary>
    /// Instantly snaps the local player's facing to <paramref name="radians"/>.
    /// Must be called on the framework thread.
    /// </summary>
    public void SetPlayerFacing(nint playerAddress, float radians)
    {
        if (playerAddress == nint.Zero) return;
        try
        {
            var obj = (GameObject*)playerAddress;
            obj->Rotation = radians;
        }
        catch (Exception ex)
        {
            log.Error(ex, "JumpSolver: failed to set player rotation.");
        }
    }

    // ── Camera ────────────────────────────────────────────────────────────────

    private bool _cameraLoggedOnce = false;

    public (float yaw, float pitch) GetCameraAngles()
    {
        try
        {
            var mgr = CameraManager.Instance();
            if (mgr == null)
            {
                if (!_cameraLoggedOnce) { log.Warning("JumpSolver: CameraManager.Instance() is null"); _cameraLoggedOnce = true; }
                return (0f, 0f);
            }
            var cam = mgr->Cameras[0];
            if (cam.Value == null)
            {
                if (!_cameraLoggedOnce) { log.Warning("JumpSolver: Cameras[0].Value is null"); _cameraLoggedOnce = true; }
                return (0f, 0f);
            }
            _cameraLoggedOnce = false;
            return (cam.Value->DirH, cam.Value->DirV);
        }
        catch (Exception ex)
        {
            if (!_cameraLoggedOnce) { log.Error(ex, "JumpSolver: GetCameraAngles failed"); _cameraLoggedOnce = true; }
            return (0f, 0f);
        }
    }

    public void SetCameraAngles(float yaw, float pitch)
    {
        try
        {
            var mgr = CameraManager.Instance();
            if (mgr == null) return;
            var cam = mgr->Cameras[0];
            if (cam.Value == null) return;
            cam.Value->DirH = yaw;
            cam.Value->DirV = Math.Clamp(pitch, cam.Value->DirVMin, cam.Value->DirVMax);
        }
        catch (Exception ex) { log.Error(ex, "JumpSolver: SetCameraAngles failed"); }
    }

    // ── Position correction ────────────────────────────────────────────────────

    /// <summary>
    /// Nudges the player's XZ position toward (<paramref name="targetX"/>, <paramref name="targetZ"/>)
    /// by at most <paramref name="maxDelta"/> yalms per call.
    /// No-ops if the offset is negligible (&lt;0.001) or too large (&gt;0.5 — something went wrong).
    /// Must be called on the framework thread.
    /// </summary>
    public void NudgeXZ(nint playerAddress, float targetX, float targetZ, float maxDelta = 0.05f)
    {
        if (playerAddress == nint.Zero) return;
        try
        {
            var   obj  = (GameObject*)playerAddress;
            float dx   = targetX - obj->Position.X;
            float dz   = targetZ - obj->Position.Z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < 0.001f || dist > 0.5f) return;
            float scale = MathF.Min(maxDelta, dist) / dist;
            obj->Position.X += dx * scale;
            obj->Position.Z += dz * scale;
        }
        catch (Exception ex)
        {
            log.Error(ex, "JumpSolver: failed to nudge player position.");
        }
    }
}
