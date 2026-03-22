using System;

using Dalamud.Hooking;
using Dalamud.Plugin.Services;
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
    // ── Injected movement, written by JumpPlayer each frame ──────────────────
    public float InjectedForward { get; set; }   //  1 = full forward,  -1 = backward
    public float InjectedLeft    { get; set; }   //  1 = full left,     -1 = right
    public bool  Injecting       => InjectedForward != 0f || InjectedLeft != 0f;

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

            if (Injecting)
            {
                *sumForward             = InjectedForward;
                *sumLeft                = InjectedLeft;
                *sumTurnLeft            = 0f;
                *haveBackwardOrStrafe   = (byte)(InjectedLeft != 0f ? 1 : 0);
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
    public void ClearInjection() { InjectedForward = 0f; InjectedLeft = 0f; }

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
}
