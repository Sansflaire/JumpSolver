using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using Dalamud.Plugin.Services;

namespace JumpSolver;

/// <summary>
/// Diagnostic capture — records RMI input values, facing, and player position every framework tick.
/// Two captures are intended: one during natural recording, one during playback.
/// Writes a CSV to pluginConfigs so the two files can be compared side-by-side.
///
/// Columns:
///   ElapsedMs    — ms since capture started
///   PosX/Y/Z     — player world position
///   SpeedXZ      — XZ speed derived from position delta (yalms/s)
///   Facing       — player facing in radians (obj->Rotation)
///   NatFwd       — sumForward from real keyboard input (pre-injection)
///   NatLeft      — sumLeft from real keyboard input (pre-injection)
///   NatTurnLeft  — sumTurnLeft from real keyboard input (A/D in Standard mode)
///   InjFwd       — sumForward sent to physics engine
///   InjLeft      — sumLeft sent to physics engine
///   Injecting    — 1 if plugin was overriding input, 0 if natural
///   JumpFired    — 1 on the frame a jump UseAction was accepted by the game
/// </summary>
internal sealed class DiagCapture
{
    private struct Frame
    {
        public float ElapsedMs;
        public float PosX, PosY, PosZ;
        public float SpeedXZ;
        public float Facing;
        public float NatFwd, NatLeft, NatTurnLeft;
        public float InjFwd, InjLeft;
        public bool  Injecting;
        public bool  JumpFired;
    }

    private readonly List<Frame> frames  = new();
    private readonly IPluginLog  log;

    private float   elapsed  = 0f;
    private Vector3 prevPos  = Vector3.Zero;
    private bool    hasPrev  = false;
    private string  label    = "diag";

    public bool IsActive { get; private set; }

    internal DiagCapture(IPluginLog log) { this.log = log; }

    // ── Control ───────────────────────────────────────────────────────────────

    public void Start(string label)
    {
        frames.Clear();
        elapsed     = 0f;
        hasPrev     = false;
        this.label  = label;
        IsActive    = true;
        log.Info($"DiagCapture: started ({label}).");
    }

    /// <summary>
    /// Add one frame. Call from Plugin.OnUpdate after player.Tick.
    /// </summary>
    public void AddFrame(float deltaMs, Vector3 pos, float facing,
                         float natFwd, float natLeft, float natTurnLeft,
                         float injFwd, float injLeft,
                         bool  injecting, bool jumpFired)
    {
        if (!IsActive) return;

        float speedXZ = 0f;
        if (hasPrev)
        {
            float dx   = pos.X - prevPos.X;
            float dz   = pos.Z - prevPos.Z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            speedXZ = deltaMs > 0f ? dist / (deltaMs / 1000f) : 0f;
        }

        frames.Add(new Frame
        {
            ElapsedMs   = elapsed,
            PosX        = pos.X,
            PosY        = pos.Y,
            PosZ        = pos.Z,
            SpeedXZ     = speedXZ,
            Facing      = facing,
            NatFwd      = natFwd,
            NatLeft     = natLeft,
            NatTurnLeft = natTurnLeft,
            InjFwd      = injFwd,
            InjLeft     = injLeft,
            Injecting   = injecting,
            JumpFired   = jumpFired,
        });

        elapsed += deltaMs;
        prevPos  = pos;
        hasPrev  = true;
    }

    /// <summary>Stop and write CSV. Returns the path written, or empty string on failure.</summary>
    public string Stop()
    {
        IsActive = false;
        if (frames.Count == 0)
        {
            log.Info("DiagCapture: stopped with no frames.");
            return string.Empty;
        }

        try
        {
            string dir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "pluginConfigs");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir,
                $"JumpSolver_diag_{label}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            using var w = new StreamWriter(path);
            w.WriteLine("ElapsedMs,PosX,PosY,PosZ,SpeedXZ,Facing,NatFwd,NatLeft,NatTurnLeft,InjFwd,InjLeft,Injecting,JumpFired");
            foreach (var f in frames)
                w.WriteLine(
                    $"{f.ElapsedMs:F1},{f.PosX:F4},{f.PosY:F4},{f.PosZ:F4}," +
                    $"{f.SpeedXZ:F4},{f.Facing:F4}," +
                    $"{f.NatFwd:F4},{f.NatLeft:F4},{f.NatTurnLeft:F4}," +
                    $"{f.InjFwd:F4},{f.InjLeft:F4}," +
                    $"{(f.Injecting ? 1 : 0)},{(f.JumpFired ? 1 : 0)}");

            log.Info($"DiagCapture: wrote {frames.Count} frames → {path}");
            return path;
        }
        catch (Exception ex)
        {
            log.Error(ex, "DiagCapture: failed to write CSV.");
            return string.Empty;
        }
    }
}
