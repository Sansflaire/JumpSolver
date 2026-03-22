using System.Collections.Generic;
using System.Numerics;

namespace JumpSolver;

// ──────────────────────────────────────────────────────────────────────────────
// JumpStep
//
// One recorded or authored step. Playback sequence per step:
//   1. Snap player facing to FacingAngle
//   2. Hold "move forward" for MoveDurationMs milliseconds
//   3. If Jump == true, fire jump after JumpDelayMs milliseconds from move start
//
// MoveDurationMs covers the FULL step including time in the air — keep holding
// forward through the jump. The step ends when MoveDurationMs elapses, which
// should correspond to roughly when the player lands.
//
// During recording, each step boundary is one of:
//   • Direction change (rotation shifts > threshold while walking)
//   • Landing after a jump
//   • Player stops moving without jumping
// ──────────────────────────────────────────────────────────────────────────────
public sealed class JumpStep
{
    public float FacingAngle    { get; set; }          // radians, world-space (-π..π)
    public float MoveDurationMs { get; set; } = 700f;  // hold forward this long
    public bool  Jump           { get; set; } = false;
    public float JumpDelayMs    { get; set; } = 0f;    // ms after move starts before jump fires

    public JumpStep() { }

    public JumpStep(float facingAngle, float moveDurationMs, bool jump = false, float jumpDelayMs = 0f)
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
// ──────────────────────────────────────────────────────────────────────────────
public sealed class StartPoint
{
    public Vector3 Position   { get; set; }
    public float   Facing     { get; set; }   // radians
    public float   SnapRadius { get; set; } = 5f;

    public StartPoint Clone() => new()
    {
        Position   = Position,
        Facing     = Facing,
        SnapRadius = SnapRadius,
    };
}

// ──────────────────────────────────────────────────────────────────────────────
// JumpPuzzle
// ──────────────────────────────────────────────────────────────────────────────
public sealed class JumpPuzzle
{
    public string         Name  { get; set; } = "New Puzzle";
    public StartPoint?    Start { get; set; }
    public List<JumpStep> Steps { get; set; } = new();

    public JumpPuzzle DeepCopy()
    {
        var copy = new JumpPuzzle { Name = Name, Start = Start?.Clone() };
        foreach (var s in Steps) copy.Steps.Add(s.Clone());
        return copy;
    }
}
