using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

using PanacheUI.Components;
using PanacheUI.Core;
using PanacheUI.Layout;
using PanacheUI.Rendering;

using ImTextureID = Dalamud.Bindings.ImGui.ImTextureID;

namespace JumpSolver;

// ─────────────────────────────────────────────────────────────────────────────
// FrameEditorWindow
//
// Jump-focused editor. Instead of showing every field for every frame, it:
//   • Shows a draggable SumLeft bar chart for the run-up window
//   • Shows a draggable jump-timing marker on the same graph
//   • Shows a facing angle slider for the jump frame
//   • Hides per-frame noise: DeltaMs, SumForward, Moving, CameraYaw, CameraPitch
//     (those only appear in the collapsed "Advanced" panel for power users)
//
// Fields that DON'T need to show every frame (kept in Advanced / hidden):
//   • DeltaMs      — timing delta, ~7ms at 140fps, rarely changed
//   • SumForward   — almost always 0 (standstill) or 1 (running), not strafe-critical
//   • Moving       — legacy backward-compat bool, never useful in editor
//   • CameraYaw    — not editable per-frame in practice
//   • CameraPitch  — not editable per-frame in practice
// ─────────────────────────────────────────────────────────────────────────────
internal sealed class FrameEditorWindow : IDisposable
{
    // ── PanacheUI infrastructure ──────────────────────────────────────────────
    private readonly LayoutEngine  _layout;
    private readonly SkiaRenderer  _renderer;
    private readonly TextureManager _textures;
    private RenderSurface          _surface;
    private ImTextureID?           _texHandle;
    private int                    _surfaceW, _surfaceH;
    private Vector2?               _windowPos;

    // ── Node references (for layout-box lookup) ───────────────────────────────
    private Node? _graphNode;
    private Node? _facingNode;
    private Node? _advancedNode;
    private Node? _btnSave;
    private Node? _btnPrevJump, _btnNextJump;

    private Dictionary<Node, LayoutBox> _lastLayout = new();

    // ── Editor state ──────────────────────────────────────────────────────────
    public  bool IsVisible;
    private int  _jumpIdx;
    private int  _lastJumpIdx = -1;
    private bool _showAdvanced;

    // Graph drag state
    private bool _draggingJump;
    private bool _draggingSumLeft;
    private int  _dragFrame;           // frame index being dragged

    // Callbacks
    public Action? OnSave;

    // Accent colour (green, matching plugin theme)
    private static readonly PColor Accent      = PColor.FromHex("#2ECC70");
    private static readonly PColor AccentDim   = PColor.FromHex("#1A7A44");
    private static readonly PColor AccentWarm  = PColor.FromHex("#E6C84A");   // run-up highlight
    private static readonly PColor AccentWarmD = PColor.FromHex("#7A6010");

    // ─────────────────────────────────────────────────────────────────────────

    public FrameEditorWindow(ITextureProvider texProvider)
    {
        _layout   = new LayoutEngine();
        _renderer = new SkiaRenderer();
        _surfaceW = 820;
        _surfaceH = 76;   // header-only surface; graph drawn with ImDrawList
        _surface  = new RenderSurface(_surfaceW, _surfaceH);
        _textures = new TextureManager(texProvider);
    }

    // ─────────────────────────────────────────────────────────────────────────

    public void Draw(JumpPuzzle puzzle)
    {
        if (!IsVisible || !puzzle.HasRecording) return;

        var allFrames  = puzzle.Segments.SelectMany(s => s.Frames).ToList();
        int total      = allFrames.Count;
        var jumpIdxs   = allFrames.Select((f, i) => (f, i)).Where(x => x.f.Jump).Select(x => x.i).ToList();
        int jumpCount  = jumpIdxs.Count;
        if (jumpCount == 0) return;

        _jumpIdx = Math.Clamp(_jumpIdx, 0, jumpCount - 1);

        int jumpFrameIdx = jumpIdxs[_jumpIdx];
        var jf           = allFrames[jumpFrameIdx];

        // Run-up: walk backward from jump frame to find first frame with movement
        int runUpStart = jumpFrameIdx;
        while (runUpStart > 0 &&
               (allFrames[runUpStart - 1].SumForward != 0f || allFrames[runUpStart - 1].SumLeft != 0f))
            runUpStart--;

        bool hasRunUp      = runUpStart < jumpFrameIdx;
        int  runUpLen      = jumpFrameIdx - runUpStart;
        int  prevEnd       = _jumpIdx > 0 ? jumpIdxs[_jumpIdx - 1] + 1 : 0;
        int  standstillLen = hasRunUp ? runUpStart - prevEnd : 0;
        int  contextBefore = hasRunUp ? 5 : 20;
        int  rangeStart    = Math.Max(prevEnd, Math.Max(0, runUpStart - contextBefore));
        int  rangeEnd      = Math.Min(total - 1, jumpFrameIdx + 15);

        // ── ImGui window ──────────────────────────────────────────────────────
        ImGui.SetNextWindowSize(new Vector2(820, 520), ImGuiCond.FirstUseEver);
        if (_windowPos.HasValue)
            ImGui.SetNextWindowPos(_windowPos.Value, ImGuiCond.Always);

        const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoTitleBar
                                     | ImGuiWindowFlags.NoScrollbar
                                     | ImGuiWindowFlags.NoScrollWithMouse;
        bool vis = IsVisible;
        if (!ImGui.Begin("##jsfe", ref vis, Flags)) { IsVisible = vis; ImGui.End(); return; }
        IsVisible = vis;

        if (!_windowPos.HasValue)
            _windowPos = ImGui.GetWindowPos();

        var avail = ImGui.GetContentRegionAvail();
        int newW  = Math.Max(400, (int)avail.X);

        // Header surface width can change; height is fixed 76px
        if (newW != _surfaceW)
        {
            _surfaceW = newW;
            _surface.Dispose();
            _surface = new RenderSurface(_surfaceW, _surfaceH);
        }

        // Build & render PanacheUI header strip
        var root    = BuildHeaderTree(_surfaceW, _surfaceH, _jumpIdx, jumpCount, jf, hasRunUp, standstillLen, runUpLen, rangeStart, rangeEnd);
        _lastLayout = _layout.Compute(root, _surfaceW, _surfaceH);
        _renderer.Render(_surface.Canvas, root, _lastLayout, 0f);
        _texHandle = _textures.Upload(_surface);

        Vector2 imagePos = default;
        if (_texHandle.HasValue)
        {
            imagePos = ImGui.GetCursorScreenPos();
            ImGui.Image(_texHandle.Value, new Vector2(_surfaceW, _surfaceH));
        }

        // ── Close button overlay ───────────────────────────────────────────────
        const float BtnSz = 22f, BtnPd = 8f;
        var closePos = new Vector2(imagePos.X + _surfaceW - BtnSz - BtnPd, imagePos.Y + BtnPd);
        ImGui.SetCursorScreenPos(closePos);
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f,    0f,    0f,    0.20f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.85f, 0.20f, 0.20f, 0.80f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.85f, 0.10f, 0.10f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(1f, 1f, 1f, 0.90f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        if (ImGui.Button("X##close_fe", new Vector2(BtnSz, BtnSz))) IsVisible = false;
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();

        // ── Header pill hit-test (Prev / Next jump) ───────────────────────────
        {
            var mouse  = ImGui.GetMousePos();
            float mx   = mouse.X - imagePos.X;
            float my   = mouse.Y - imagePos.Y;
            bool hov   = ImGui.IsItemHovered() || (ImGui.GetIO().MousePos.X >= imagePos.X
                         && ImGui.GetIO().MousePos.X < imagePos.X + _surfaceW
                         && ImGui.GetIO().MousePos.Y >= imagePos.Y
                         && ImGui.GetIO().MousePos.Y < imagePos.Y + _surfaceH);

            bool clicked = hov && ImGui.IsMouseClicked(ImGuiMouseButton.Left);

            if (clicked && _btnPrevJump != null && _lastLayout.TryGetValue(_btnPrevJump, out var pb)
                && mx >= pb.X && mx <= pb.Right && my >= pb.Y && my <= pb.Bottom)
            {
                if (_jumpIdx > 0) _jumpIdx--;
            }
            if (clicked && _btnNextJump != null && _lastLayout.TryGetValue(_btnNextJump, out var nb)
                && mx >= nb.X && mx <= nb.Right && my >= nb.Y && my <= nb.Bottom)
            {
                if (_jumpIdx < jumpCount - 1) _jumpIdx++;
            }
        }

        // ── Window drag (on header strip) ─────────────────────────────────────
        bool mouseOnHeader = ImGui.GetIO().MousePos.Y >= imagePos.Y
                          && ImGui.GetIO().MousePos.Y <  imagePos.Y + _surfaceH;
        if (mouseOnHeader && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta  = ImGui.GetIO().MouseDelta;
            _windowPos = (_windowPos ?? ImGui.GetWindowPos()) + delta;
        }

        // ═════════════════════════════════════════════════════════════════════
        // GRAPH SECTION  (pure ImGui draw list — no PanacheUI nodes)
        // ═════════════════════════════════════════════════════════════════════

        ImGui.Spacing();

        float winX     = ImGui.GetWindowPos().X;
        float winY     = ImGui.GetWindowPos().Y + ImGui.GetScrollY();
        float padLeft  = 10f;
        float padRight = 10f;
        float gLeft    = winX + padLeft;
        float gTop     = ImGui.GetCursorScreenPos().Y;
        float gWidth   = ImGui.GetContentRegionAvail().X - padLeft - padRight;
        float gHeight  = 170f;
        float gRight   = gLeft + gWidth;
        float gBottom  = gTop  + gHeight;

        int   numFrames = rangeEnd - rangeStart + 1;
        float colW      = gWidth / Math.Max(numFrames, 1);
        float zeroY     = gTop + gHeight * 0.5f;

        var dl = ImGui.GetWindowDrawList();

        // Background
        dl.AddRectFilled(new Vector2(gLeft, gTop), new Vector2(gRight, gBottom),
                         0xFF0E0E1E, 4f);
        dl.AddRect(new Vector2(gLeft, gTop), new Vector2(gRight, gBottom),
                   ImColor(Accent.WithOpacity(0.35f)), 4f);

        // Grid lines
        for (int g = -2; g <= 2; g++)
        {
            float y = zeroY - g * gHeight * 0.22f;
            uint gridCol = g == 0 ? ImColor(0xFFFFFF, 0.18f) : ImColor(0xFFFFFF, 0.07f);
            dl.AddLine(new Vector2(gLeft, y), new Vector2(gRight, y), gridCol);
            if (g != 0)
            {
                float label = g * 0.44f;
                dl.AddText(new Vector2(gLeft + 2f, y - 9f), ImColor(0xFFFFFF, 0.25f),
                           $"{label:+0.00;-0.00}");
            }
        }
        dl.AddText(new Vector2(gLeft + 2f, zeroY - 9f), ImColor(0xFFFFFF, 0.30f), " 0.00");

        // ── Bar chart ─────────────────────────────────────────────────────────
        var mouse2    = ImGui.GetIO().MousePos;
        bool inGraph  = mouse2.X >= gLeft && mouse2.X <= gRight
                     && mouse2.Y >= gTop  && mouse2.Y <= gBottom;
        int  hoverFr  = inGraph ? (int)((mouse2.X - gLeft) / colW) + rangeStart : -1;
        hoverFr = Math.Clamp(hoverFr, rangeStart, rangeEnd);

        for (int i = rangeStart; i <= rangeEnd; i++)
        {
            var   fr       = allFrames[i];
            int   col      = i - rangeStart;
            float cx       = gLeft + (col + 0.5f) * colW;
            float barHalf  = colW * 0.42f;

            float sl       = fr.SumLeft;
            float barH     = sl * gHeight * 0.44f;   // pixels above/below zero

            bool isJump    = i == jumpFrameIdx;
            bool isRunUp   = i >= runUpStart && i < jumpFrameIdx;
            bool isHover   = i == hoverFr && inGraph;

            uint barColor  = isJump   ? ImColor(Accent)
                           : isRunUp  ? ImColor(AccentWarm)
                           :            ImColor(0xFFFFFF, 0.15f);

            if (isHover && !isJump) barColor = ImColor(0xFFFFFF, 0.28f);

            // Bar: from zeroY upward if SumLeft>0, downward if <0
            float barTop    = barH >= 0 ? zeroY - barH : zeroY;
            float barBottom = barH >= 0 ? zeroY        : zeroY - barH;
            if (Math.Abs(barH) < 1f) { barTop = zeroY - 1f; barBottom = zeroY + 1f; }

            dl.AddRectFilled(new Vector2(cx - barHalf, barTop),
                             new Vector2(cx + barHalf, barBottom),
                             barColor, 2f);

            // Jump marker (green vertical line)
            if (isJump)
            {
                dl.AddLine(new Vector2(cx, gTop + 4f), new Vector2(cx, gBottom - 4f),
                           ImColor(Accent.WithOpacity(0.90f)), 2.5f);
                dl.AddText(new Vector2(cx - 10f, gTop + 4f), ImColor(Accent), "J");
            }
        }

        // ── Hover tooltip ─────────────────────────────────────────────────────
        if (inGraph && hoverFr >= rangeStart && hoverFr <= rangeEnd)
        {
            var hf = allFrames[hoverFr];
            ImGui.SetTooltip($"Frame {hoverFr}  SumLeft={hf.SumLeft:+0.000;-0.000}  {(hf.Jump ? "[JUMP]" : "")}");
        }

        // ── Graph interaction ─────────────────────────────────────────────────
        // Start drag
        if (inGraph && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            // Are we clicking near the jump line?
            float jumpCx = gLeft + (jumpFrameIdx - rangeStart + 0.5f) * colW;
            if (Math.Abs(mouse2.X - jumpCx) <= 8f)
            {
                _draggingJump    = true;
                _draggingSumLeft = false;
            }
            else
            {
                _draggingSumLeft = true;
                _draggingJump    = false;
                _dragFrame       = hoverFr;
            }
        }

        // Continue SumLeft drag while button held
        if (_draggingSumLeft && ImGui.IsMouseDown(ImGuiMouseButton.Left) && _dragFrame >= rangeStart && _dragFrame <= rangeEnd)
        {
            // Y maps to SumLeft: gTop = +1.0, gBottom = -1.0
            float rawSL   = (zeroY - mouse2.Y) / (gHeight * 0.44f);
            float snapped = MathF.Round(rawSL * 20f) / 20f;  // snap to 0.05 increments
            allFrames[_dragFrame].SumLeft = Math.Clamp(snapped, -1f, 1f);
            if (inGraph) _dragFrame = hoverFr;  // follow mouse across columns
        }

        // Continue jump drag while button held
        if (_draggingJump && ImGui.IsMouseDown(ImGuiMouseButton.Left) && inGraph)
        {
            int targetFr = hoverFr;
            if (targetFr != jumpFrameIdx && targetFr >= rangeStart && targetFr <= rangeEnd
                && !allFrames[targetFr].Jump)
            {
                allFrames[jumpFrameIdx].Jump = false;
                allFrames[targetFr].Jump     = true;
                // jumpFrameIdx will update next frame via the fresh scan
            }
        }

        // Release
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _draggingJump    = false;
            _draggingSumLeft = false;
        }

        // Advance the cursor past the graph
        ImGui.SetCursorScreenPos(new Vector2(winX + padLeft, gBottom + 8f));

        // ═════════════════════════════════════════════════════════════════════
        // STEER CONTROL  — adjusts SumLeft across all run-up frames
        // SumForward/SumLeft are the actual movement inputs; this is what
        // genuinely changes where the character lands.
        // ═════════════════════════════════════════════════════════════════════

        ImGui.PushStyleColor(ImGuiCol.Text,       new Vector4(0.80f, 0.80f, 0.85f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg,    new Vector4(0.08f, 0.18f, 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, ToImVec(AccentWarm));

        // Average SumLeft across run-up frames as the "current" value for display.
        float avgLeft = hasRunUp
            ? allFrames.Skip(runUpStart).Take(jumpFrameIdx - runUpStart + 1)
                       .Average(f => f.SumLeft)
            : jf.SumLeft;
        float avgLeftOld = avgLeft;

        ImGui.Text($"Steer left/right  ({(hasRunUp ? runUpLen : 1)} frame(s))");
        ImGui.SameLine(200f);
        ImGui.TextColored(ToImVec(AccentWarm),
            avgLeft > 0.005f  ? $"← left  {avgLeft:+0.000}" :
            avgLeft < -0.005f ? $"→ right {avgLeft:+0.000}" : "center  0.000");

        ImGui.SetNextItemWidth(gWidth - 80f);
        if (ImGui.SliderFloat("##steer", ref avgLeft, -1f, 1f, ""))
        {
            float delta = avgLeft - avgLeftOld;
            // Add the same delta to every run-up frame so the approach steers uniformly.
            int steerEnd = jumpFrameIdx;
            int steerStart = hasRunUp ? runUpStart : jumpFrameIdx;
            for (int i = steerStart; i <= steerEnd; i++)
                allFrames[i].SumLeft = Math.Clamp(allFrames[i].SumLeft + delta, -1f, 1f);
        }

        ImGui.PopStyleColor(3);

        ImGui.Spacing();

        // ═════════════════════════════════════════════════════════════════════
        // ADVANCED PANEL (collapsible — DeltaMs / SumForward / raw data)
        // ═════════════════════════════════════════════════════════════════════

        ImGui.PushStyleColor(ImGuiCol.Header,        new Vector4(0.10f, 0.22f, 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.14f, 0.32f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.60f, 0.90f, 0.65f, 1f));
        _showAdvanced = ImGui.CollapsingHeader("Advanced  (DeltaMs · SumForward · raw frames)", _showAdvanced ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        ImGui.PopStyleColor(3);

        if (_showAdvanced)
        {
            var dimText    = new Vector4(0.40f, 0.40f, 0.45f, 1f);
            var green      = ToImVec(Accent);
            var runUpColor = ToImVec(AccentWarm);
            var jumpRowBg  = new Vector4(0.08f, 0.24f, 0.12f, 1f);
            float rowH     = ImGui.GetTextLineHeightWithSpacing();

            ImGui.BeginChild("##feadv", new Vector2(0, Math.Min(200f, (rangeEnd - rangeStart + 1) * rowH + 10f)), true);

            ImGui.TextColored(dimText, $"  {"#",-6}  {"ΔT ms",-9}  {"Fwd",-10}  {"Left",-10}  {"Pos XYZ",-28}  Jump");
            ImGui.Separator();

            for (int i = rangeStart; i <= rangeEnd; i++)
            {
                var  f      = allFrames[i];
                bool isJmp  = i == jumpFrameIdx;
                bool isRUp  = i >= runUpStart && i < jumpFrameIdx;

                ImGui.PushID(i);
                if (isJmp) ImGui.PushStyleColor(ImGuiCol.FrameBg, jumpRowBg);

                ImGui.TextColored(isJmp ? green : isRUp ? runUpColor : dimText, $"{i,6} ");
                ImGui.SameLine();

                ImGui.SetNextItemWidth(60);
                float dt = f.DeltaMs;
                if (ImGui.DragFloat("##dt", ref dt, 0.1f, 1f, 200f, "%.1f")) f.DeltaMs = dt;
                ImGui.SameLine();

                ImGui.SetNextItemWidth(68);
                float fwd = f.SumForward;
                if (ImGui.DragFloat("##fwd", ref fwd, 0.005f, -1f, 1f, "%.3f")) f.SumForward = fwd;
                ImGui.SameLine();

                ImGui.SetNextItemWidth(68);
                float lft = f.SumLeft;
                if (ImGui.DragFloat("##lft", ref lft, 0.005f, -1f, 1f, "%.3f")) f.SumLeft = lft;
                ImGui.SameLine();

                ImGui.TextColored(dimText, $"  ({f.PosX,6:F1},{f.PosY,6:F1},{f.PosZ,6:F1})");
                ImGui.SameLine();

                if (f.Jump)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.10f, 0.55f, 0.25f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.15f, 0.75f, 0.35f, 1f));
                    if (ImGui.SmallButton("[J]")) f.Jump = false;
                    ImGui.PopStyleColor(2);
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.18f, 0.18f, 0.22f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.30f, 0.36f, 1f));
                    if (ImGui.SmallButton("[ ]")) f.Jump = true;
                    ImGui.PopStyleColor(2);
                }

                if (isJmp) ImGui.PopStyleColor();
                ImGui.PopID();
            }

            ImGui.EndChild();
        }

        // ═════════════════════════════════════════════════════════════════════
        // SAVE BAR
        // ═════════════════════════════════════════════════════════════════════

        ImGui.Spacing();

        // Trim after current jump
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.45f, 0.22f, 0.10f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.65f, 0.30f, 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.85f, 0.40f, 0.20f, 1f));
        if (ImGui.Button($"Trim after Jump {_jumpIdx + 1}##trim", new Vector2(180f, 0)))
        {
            var kept = allFrames.Take(jumpFrameIdx + 1).ToList();
            puzzle.Segments.Clear();
            puzzle.Segments.Add(new PuzzleSegment { Name = "Segment 1", Frames = kept });
            _jumpIdx = 0;   // reset to first jump since the list shrank
        }
        ImGui.PopStyleColor(3);

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.10f, 0.45f, 0.22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.14f, 0.60f, 0.30f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.20f, 0.80f, 0.40f, 1f));
        if (ImGui.Button("Save to Config", new Vector2(160f, 0))) OnSave?.Invoke();
        ImGui.PopStyleColor(3);

        ImGui.End();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PanacheUI header tree (76px strip)
    // ─────────────────────────────────────────────────────────────────────────

    private Node BuildHeaderTree(int w, int h,
        int jumpIdx, int jumpCount,
        RecordedFrame jf, bool hasRunUp,
        int standstillLen, int runUpLen,
        int rangeStart, int rangeEnd)
    {
        var root = new Node().WithId("root").WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width  = w;
            s.HeightMode      = SizeMode.Fixed; s.Height = h;
            s.Flow            = Flow.Vertical;
            s.BackgroundColor = Theme.Base;
        });

        root.AppendChild(BuildHeader(w, jumpIdx, jumpCount, jf, hasRunUp, standstillLen, runUpLen));
        root.AppendChild(PUI.SectionDivider(Accent.WithOpacity(0.25f)));

        return root;
    }

    private Node BuildHeader(int w, int jumpIdx, int jumpCount,
        RecordedFrame jf, bool hasRunUp, int standstillLen, int runUpLen)
    {
        var header = new Node().WithStyle(s =>
        {
            s.Flow                  = Flow.Vertical;
            s.WidthMode             = SizeMode.Fill;
            s.HeightMode            = SizeMode.Fit;
            s.BackgroundColor       = PColor.FromHex("#0C1F14");
            s.BackgroundGradientEnd = Theme.Panel;
            s.Padding               = new EdgeSize(10, 18, 6, 18);
            s.Gap                   = 4;
        });

        // Title row
        var titleRow = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Horizontal; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit; s.Gap = 10;
        });

        titleRow.AppendChild(new Node().WithText("Frame Editor").WithStyle(s =>
        {
            s.FontSize   = 16f; s.Bold = true;
            s.Color      = Accent;
            s.WidthMode  = SizeMode.Fit; s.HeightMode = SizeMode.Fit;
        }));

        // Spacer
        titleRow.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
        }));

        // Jump navigation pills
        var prevPill = PUI.PillButton("prev-jump", "◄", Accent);
        prevPill.Style.Opacity = jumpIdx > 0 ? 1f : 0.35f;
        _btnPrevJump = prevPill;
        titleRow.AppendChild(prevPill);

        titleRow.AppendChild(new Node().WithText($"Jump {jumpIdx + 1} / {jumpCount}").WithStyle(s =>
        {
            s.FontSize   = 12f; s.Bold = true;
            s.Color      = PColor.White;
            s.WidthMode  = SizeMode.Fit; s.HeightMode = SizeMode.Fit;
            s.Padding    = new EdgeSize(5, 10);
        }));

        var nextPill = PUI.PillButton("next-jump", "►", Accent);
        nextPill.Style.Opacity = jumpIdx < jumpCount - 1 ? 1f : 0.35f;
        _btnNextJump = nextPill;
        titleRow.AppendChild(nextPill);

        header.AppendChild(titleRow);

        // Info row
        string posStr  = $"frame #{jumpCount}  pos ({jf.PosX:F2}, {jf.PosY:F2}, {jf.PosZ:F2})";
        string infoStr = hasRunUp
            ? $"standstill: {standstillLen} fr  run-up: {runUpLen} fr"
            : "no run-up detected";

        var infoRow = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Horizontal; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit; s.Gap = 16;
        });

        infoRow.AppendChild(new Node().WithText(posStr).WithStyle(s =>
        {
            s.FontSize   = 10f; s.Color = Theme.TextMuted;
            s.WidthMode  = SizeMode.Fit; s.HeightMode = SizeMode.Fit;
        }));
        infoRow.AppendChild(new Node().WithText(infoStr).WithStyle(s =>
        {
            s.FontSize   = 10f; s.Color = Accent.WithOpacity(0.70f);
            s.WidthMode  = SizeMode.Fit; s.HeightMode = SizeMode.Fit;
        }));

        header.AppendChild(infoRow);
        return header;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static uint ImColor(PColor c) =>
        ((uint)c.A << 24) | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R;

    private static uint ImColor(uint rgb, float alpha) =>
        ((uint)(alpha * 255f) << 24) | (rgb & 0xFF0000) | (rgb & 0x00FF00) | (rgb & 0x0000FF);

    private static Vector4 ToImVec(PColor c) =>
        new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    // ─────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _surface.Dispose();
        _textures.Dispose();
        _layout.Dispose();
        _renderer.Dispose();
    }
}
