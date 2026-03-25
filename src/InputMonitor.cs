using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace JumpSolver;

// ─────────────────────────────────────────────────────────────────────────────
// InputMonitor  —  reads all relevant inputs every Framework.Update tick.
//
// Sources:
//   RMI hook  : SumForward / SumLeft / SumTurnLeft (what the game actually sees)
//   GetAsyncKeyState : raw OS-level key state (W A S D Q E Space LMB RMB)
//   GetCursorPos     : screen-space mouse position → delta per frame
// ─────────────────────────────────────────────────────────────────────────────
internal sealed class InputMonitor
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_W       = 0x57;
    private const int VK_A       = 0x41;
    private const int VK_S       = 0x53;
    private const int VK_D       = 0x44;
    private const int VK_Q       = 0x51;
    private const int VK_E       = 0x45;
    private const int VK_SPACE   = 0x20;

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // ── RMI-level (what the game actually processes after the hook) ───────────
    public float NatForward  { get; private set; }   // W/S aggregate
    public float NatLeft     { get; private set; }   // Q/E aggregate
    public float NatTurnLeft { get; private set; }   // A/D + right-mouse-drag aggregate

    // ── Injection state (what JumpPlayer is injecting) ────────────────────────
    public float InjForward  { get; private set; }
    public float InjLeft     { get; private set; }
    public float InjTurnLeft { get; private set; }
    public bool  Injecting   { get; private set; }

    // ── Raw OS keyboard ───────────────────────────────────────────────────────
    public bool KeyW     { get; private set; }
    public bool KeyA     { get; private set; }
    public bool KeyS     { get; private set; }
    public bool KeyD     { get; private set; }
    public bool KeyQ     { get; private set; }
    public bool KeyE     { get; private set; }
    public bool KeySpace { get; private set; }

    // ── Raw OS mouse ──────────────────────────────────────────────────────────
    public bool LMB        { get; private set; }
    public bool RMB        { get; private set; }
    // Mouse delta — NOTE: will read 0 while FFXIV locks the cursor (right-click held).
    // In that mode, rotation comes through as NatTurnLeft from the RMI hook instead.
    public int MouseDeltaX { get; private set; }
    public int MouseDeltaY { get; private set; }

    // ── Character state ───────────────────────────────────────────────────────
    public float   CharFacing { get; private set; }   // obj->Rotation (radians, -π..π)
    public float   SpeedXZ   { get; private set; }   // yalms/second in the XZ plane
    public Vector3 Position  { get; private set; }

    // ── Jump ─────────────────────────────────────────────────────────────────
    public bool JumpFired { get; private set; }

    // ── Tracking ──────────────────────────────────────────────────────────────
    private int     prevMouseX, prevMouseY;
    private bool    firstFrame = true;

    // Rolling speed window — only real position changes are enqueued.
    // segDist stored per entry = distance from the PREVIOUS entry to this one.
    // windowDistSum tracks the running total path length inside the window.
    // Evicting the oldest entry subtracts its segment, keeping the sum accurate.
    private readonly Queue<(long ms, float x, float z, float segDist)> speedQueue = new();
    private float lastEnqueuedX   = float.NaN;
    private float lastEnqueuedZ   = float.NaN;
    private float windowDistSum   = 0f;
    private const long SpeedWindowMs = 750;


    // ── Update — called once per Framework.Update tick ────────────────────────

    public void Update(float deltaMs, Vector3 pos, float facing, MoveHook mover, bool jumpFired)
    {
        // Use peak values seen across all RMI calls this tick — stable and accurate.
        NatForward  = mover.PeakNaturalForward;
        NatLeft     = mover.PeakNaturalLeft;
        NatTurnLeft = mover.PeakNaturalTurnLeft;

        InjForward  = mover.InjectedForward;
        InjLeft     = mover.InjectedLeft;
        InjTurnLeft = mover.InjectedTurnLeft;
        Injecting   = mover.Injecting;

        // Raw OS key state
        KeyW     = IsDown(VK_W);
        KeyA     = IsDown(VK_A);
        KeyS     = IsDown(VK_S);
        KeyD     = IsDown(VK_D);
        KeyQ     = IsDown(VK_Q);
        KeyE     = IsDown(VK_E);
        KeySpace = IsDown(VK_SPACE);
        LMB      = IsDown(VK_LBUTTON);
        RMB      = IsDown(VK_RBUTTON);

        // Mouse delta (screen cursor — locked to 0 when FFXIV captures mouse)
        if (GetCursorPos(out var cur))
        {
            if (firstFrame)
            {
                prevMouseX = cur.X;
                prevMouseY = cur.Y;
                firstFrame = false;
            }
            MouseDeltaX = cur.X - prevMouseX;
            MouseDeltaY = cur.Y - prevMouseY;
            prevMouseX  = cur.X;
            prevMouseY  = cur.Y;
        }

        // Character state
        CharFacing = facing;
        Position   = pos;

        // Only enqueue when position actually changed — duplicate frames skew window boundaries.
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (pos.X != lastEnqueuedX || pos.Z != lastEnqueuedZ)
        {
            float seg = float.IsNaN(lastEnqueuedX) ? 0f :
                MathF.Sqrt((pos.X - lastEnqueuedX) * (pos.X - lastEnqueuedX) +
                           (pos.Z - lastEnqueuedZ) * (pos.Z - lastEnqueuedZ));
            speedQueue.Enqueue((nowMs, pos.X, pos.Z, seg));
            windowDistSum += seg;
            lastEnqueuedX  = pos.X;
            lastEnqueuedZ  = pos.Z;
        }

        while (speedQueue.Count > 1 && nowMs - speedQueue.Peek().ms > SpeedWindowMs)
            windowDistSum -= speedQueue.Dequeue().segDist;

        if (speedQueue.Count >= 2)
        {
            float elapsed = nowMs - speedQueue.Peek().ms;
            SpeedXZ = elapsed > 0f ? windowDistSum / elapsed * 1000f : 0f;
        }

        JumpFired = jumpFired;
    }

    // ── Snapshot for IPC / API ────────────────────────────────────────────────

    public InputSnapshot Snapshot => new()
    {
        NatForward  = NatForward,
        NatLeft     = NatLeft,
        NatTurnLeft = NatTurnLeft,
        InjForward  = InjForward,
        InjLeft     = InjLeft,
        InjTurnLeft = InjTurnLeft,
        Injecting   = Injecting,
        KeyW        = KeyW,
        KeyA        = KeyA,
        KeyS        = KeyS,
        KeyD        = KeyD,
        KeyQ        = KeyQ,
        KeyE        = KeyE,
        KeySpace    = KeySpace,
        LMB         = LMB,
        RMB         = RMB,
        MouseDeltaX = MouseDeltaX,
        MouseDeltaY = MouseDeltaY,
        CharFacing  = CharFacing,
        FacingDeg   = CharFacing * (180f / MathF.PI),
        SpeedXZ     = SpeedXZ,
        PosX        = Position.X,
        PosY        = Position.Y,
        PosZ        = Position.Z,
        JumpFired   = JumpFired,
    };
}

// Published via IPC "JumpSolver.GetInputState" as JSON.
// Also used by ClaudeAccessXIV GET /player/inputs.
public sealed class InputSnapshot
{
    // RMI-level floats (what the game sees after the hook processes all inputs)
    public float NatForward  { get; set; }
    public float NatLeft     { get; set; }
    public float NatTurnLeft { get; set; }

    // Currently injected values (from JumpPlayer / /js control)
    public float InjForward  { get; set; }
    public float InjLeft     { get; set; }
    public float InjTurnLeft { get; set; }
    public bool  Injecting   { get; set; }

    // Raw OS key state
    public bool KeyW     { get; set; }
    public bool KeyA     { get; set; }
    public bool KeyS     { get; set; }
    public bool KeyD     { get; set; }
    public bool KeyQ     { get; set; }
    public bool KeyE     { get; set; }
    public bool KeySpace { get; set; }
    public bool LMB      { get; set; }
    public bool RMB      { get; set; }

    // Mouse delta (0 while FFXIV locks cursor during right-click steering)
    public int MouseDeltaX { get; set; }
    public int MouseDeltaY { get; set; }

    // Character state
    public float CharFacing { get; set; }   // radians
    public float FacingDeg  { get; set; }   // degrees (convenience)
    public float SpeedXZ    { get; set; }   // yalms/sec
    public float PosX       { get; set; }
    public float PosY       { get; set; }
    public float PosZ       { get; set; }

    public bool JumpFired { get; set; }
}
