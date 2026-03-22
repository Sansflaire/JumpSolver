using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace JumpSolver;

// ─────────────────────────────────────────────────────────────────────────────
// RecordedFrame  —  one framework tick captured during recording
//
// SumForward / SumLeft = raw RMI floats from keyboard/gamepad — replayed exactly.
// Moving = backward-compat bool for old saves that predate SumForward.
// Jump   = first tick where rising Y was detected — fire jump during playback.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class RecordedFrame
{
    public float DeltaMs    { get; set; }
    public float Facing     { get; set; }
    public float PosX       { get; set; }
    public float PosZ       { get; set; }
    public bool  Moving     { get; set; }   // backward-compat with old saves
    public bool  Jump       { get; set; }
    public float SumForward { get; set; }   // raw RMI — replayed exactly
    public float SumLeft    { get; set; }   // raw RMI — replayed exactly
}

// ─────────────────────────────────────────────────────────────────────────────
// PuzzleSegment  —  a named chunk of recorded frames
// ─────────────────────────────────────────────────────────────────────────────
public sealed class PuzzleSegment
{
    public string              Name   { get; set; } = "Segment";
    public List<RecordedFrame> Frames { get; set; } = new();

    public int JumpCount => Frames.Count(f => f.Jump);

    public PuzzleSegment Clone()
    {
        var copy = new PuzzleSegment { Name = Name };
        foreach (var f in Frames)
            copy.Frames.Add(new RecordedFrame
            {
                DeltaMs    = f.DeltaMs, Facing  = f.Facing,
                PosX       = f.PosX,   PosZ    = f.PosZ,
                Moving     = f.Moving, Jump    = f.Jump,
                SumForward = f.SumForward, SumLeft = f.SumLeft,
            });
        return copy;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// StartPoint
// ─────────────────────────────────────────────────────────────────────────────
public sealed class StartPoint
{
    public Vector3 Position   { get; set; }
    public float   Facing     { get; set; }
    public float   SnapRadius { get; set; } = 5f;

    public StartPoint Clone() => new()
        { Position = Position, Facing = Facing, SnapRadius = SnapRadius };
}

// ─────────────────────────────────────────────────────────────────────────────
// JumpPuzzle  —  a named puzzle backed by one or more recorded segments
// ─────────────────────────────────────────────────────────────────────────────
public sealed class JumpPuzzle
{
    public string              Name     { get; set; } = "New Puzzle";
    public StartPoint?         Start    { get; set; }
    public List<PuzzleSegment> Segments { get; set; } = new();

    public bool HasRecording    => Segments.Any(s => s.Frames.Count > 0);
    public int  TotalFrameCount => Segments.Sum(s => s.Frames.Count);
    public int  TotalJumpCount  => Segments.Sum(s => s.JumpCount);

    public JumpPuzzle DeepCopy()
    {
        var copy = new JumpPuzzle { Name = Name, Start = Start?.Clone() };
        foreach (var seg in Segments) copy.Segments.Add(seg.Clone());
        return copy;
    }
}
