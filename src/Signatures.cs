namespace JumpSolver;

/// <summary>
/// Signature strings for game function hooks.
///
/// HOW TO UPDATE A SIGNATURE:
///   1. Install the "SigHelper" Dalamud plugin (or use Dalamud's built-in
///      /xlsig command if available).
///   2. Search for the function by name in the CS-framework dump or
///      IDA/Ghidra, then re-scan with SigMaker.
///   3. Update the string below and rebuild.
///
/// The plugin will log a WARNING and disable movement injection (safely) if
/// a signature fails — it will not crash.
/// </summary>
internal static class Signatures
{
    /// <summary>
    /// Client::Game::Character::CharacterMoveController.HandleMoveInput
    ///
    /// Called every frame for the local player to translate raw keyboard/
    /// gamepad input into the movement direction fed to the physics engine.
    /// Hooking here and overriding the output parameters is the standard
    /// approach used by movement-injecting plugins (vnavmesh, BossMod, etc.).
    ///
    /// Delegate:
    ///   bool RMIWalk(void* self,
    ///                float* sumLeft,     // positive = strafe left
    ///                float* sumForward,  // positive = move forward
    ///                float* sumTurnLeft, // positive = turn left
    ///                byte*  haveBackwardOrStrafe,
    ///                byte*  a6,
    ///                byte   a7)
    ///
    /// Accurate as of Dawntrail ~7.1x. Verify with SigHelper on game update.
    /// </summary>
    internal const string RMIWalk = "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D";

    /// <summary>
    /// General action ID for Jump.
    /// ActionManager->UseAction(ActionType.General, JumpActionId, selfEntityId)
    /// This is stable across patches — general action IDs don't change.
    /// </summary>
    internal const uint JumpActionId = 2;
}
