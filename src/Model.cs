using System.Collections.Generic;
using System.Numerics;

namespace JumpSolver;

// ──────────────────────────────────────────────────────────────────────────────
// JumpStep
//
// One authored step. Playback:
//   1. Snap player facing to FacingAngle
//   2. Hold "move forward" for MoveDurationMs milliseconds
//   3. If Jump == true, fire jump after JumpDelayMs milliseconds
//
// FacingAngle is an absolute world-space angle in radians (-π..π).
// During recording it is captured from player.Rotation at the moment
// the jump input is detected. During manual authoring it is set to the
// player's current facing when the user clicks "Set Facing Here".
// ──────────────────────────────────────────────────────────────────────────────
public sealed class JumpStep
{
    public float FacingAngle   { get; set; }        // radians, world-space
    public float MoveDurationMs { get; set; } = 700f; // how long to hold forward
    public bool  Jump          { get; set; } = true;
    public float JumpDelayMs   { get; set; } = 0f;   // ms after move starts before jump fires

    public JumpStep() { }

    public JumpStep(float facingAngle, float moveDurationMs, bool jump = true, float jumpDelayMs = 0f)
    {
        FacingAngle    = facingAngle;
        MoveDurationMs = moveDurationMs;
        Jump           = jump;
        JumpDelayMs    = jumpDelayMs;
    }

    public JumpStep Clone() => new(FacingAngle, MoveDurationMs, Jump, JumpDelayMs);
}

// ──────────────────────────────────────────────────────────────────────────────
// StartPoint
//
// The exact position and facing the player must be at before playback begins.
// If the player is within SnapRadius yalms, the plugin walks them to the
// exact position and snaps their rotation before executing steps.
// ──────────────────────────────────────────────────────────────────────────────
public sealed class StartPoint
{
    public Vector3 Position   { get; set; }
    public float   Facing     { get; set; }   // radians, world-space
    public float   SnapRadius { get; set; } = 5f;
}

// ──────────────────────────────────────────────────────────────────────────────
// JumpPuzzle
//
// A named, shareable puzzle solution. Serialised to pluginConfigs/JumpSolver.json.
// ──────────────────────────────────────────────────────────────────────────────
public sealed class JumpPuzzle
{
    public string         Name  { get; set; } = "New Puzzle";
    public StartPoint?    Start { get; set; }
    public List<JumpStep> Steps { get; set; } = new();
}
