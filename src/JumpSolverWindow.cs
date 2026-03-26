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
// JumpSolverWindow
//
// Full PanacheUI main window. Interactive widgets (InputText, DragFloat,
// scrollable puzzle list) are overlaid via ImGui.SetCursorScreenPos on top
// of the PanacheUI image surface.
// ─────────────────────────────────────────────────────────────────────────────
internal sealed class JumpSolverWindow : IDisposable
{
    // ── PanacheUI infrastructure ──────────────────────────────────────────────
    private readonly LayoutEngine   _layout;
    private readonly SkiaRenderer   _renderer;
    private readonly TextureManager _textures;
    private RenderSurface           _surface;
    private ImTextureID?            _texHandle;
    private int                     _surfaceW, _surfaceH;
    private Dictionary<Node, LayoutBox> _lastLayout = new();

    // ── External state ────────────────────────────────────────────────────────
    private readonly JumpPlayer    _player;
    private readonly JumpRecorder  _recorder;
    private readonly MoveHook      _mover;
    private readonly DiagCapture   _diag;
    private readonly IObjectTable  _objectTable;

    // ── UI state ──────────────────────────────────────────────────────────────
    public  bool   IsVisible;
    private bool   _showDiag;
    public  bool   ShowEditor    { get; set; }
    public  string SaveName      { get; private set; } = "";
    private int    _loadIndex    = -1;

    // ── Callbacks (Plugin orchestrates the actual actions) ────────────────────
    public Action?      OnSetStart, OnGoToStart, OnRec, OnAddSeg, OnPlay, OnStop;
    public Action<int>? OnTrim;
    public Action<string>? OnSave;
    public Action<int>? OnLoad, OnDelete;
    public Action?      OnToggleHud;

    // ── Named nodes for hit-test ──────────────────────────────────────────────
    private Node? _btnSetHere, _btnGoToStart;
    private Node? _btnRec, _btnAddSeg, _btnPlay, _btnStop, _btnTrim;
    private Node? _btnEditFrames, _btnSave;
    private Node? _btnDiagNat, _btnDiagPlay, _btnDiagStop;
    private Node? _btnDiagToggle;
    private Node? _nodeSnapRadius;     // ImGui overlay: DragFloat
    private Node? _nodePuzzleName;     // ImGui overlay: InputText
    private Node? _nodeSaveName;       // ImGui overlay: InputText
    private Node? _nodePuzzleList;     // ImGui overlay: BeginChild
    private List<(Node delNode, int idx)> _puzzleDelNodes = new();
    private List<(Node loadNode, int idx)> _puzzleLoadNodes = new();

    // Accent — green
    private static readonly PColor Accent    = PColor.FromHex("#2ECC70");
    private static readonly PColor AccentHdr = PColor.FromHex("#1A8844");

    // ─────────────────────────────────────────────────────────────────────────

    public JumpSolverWindow(ITextureProvider texProvider,
                            JumpPlayer player, JumpRecorder recorder,
                            MoveHook mover, DiagCapture diag,
                            IObjectTable objectTable)
    {
        _player      = player;
        _recorder    = recorder;
        _mover       = mover;
        _diag        = diag;
        _objectTable = objectTable;

        _layout   = new LayoutEngine();
        _renderer = new SkiaRenderer();
        _surfaceW = 480;
        _surfaceH = 520;
        _surface  = new RenderSurface(_surfaceW, _surfaceH);
        _textures = new TextureManager(texProvider);
    }

    // ─────────────────────────────────────────────────────────────────────────

    public void Draw(JumpPuzzle puzzle, Configuration config)
    {
        if (!IsVisible) return;

        const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoTitleBar
                                     | ImGuiWindowFlags.NoScrollbar
                                     | ImGuiWindowFlags.NoScrollWithMouse;

        ImGui.SetNextWindowSize(new Vector2(480, 520), ImGuiCond.FirstUseEver);

        bool vis = IsVisible;
        if (!ImGui.Begin("##jumpsolver_main", ref vis, Flags)) { IsVisible = vis; ImGui.End(); return; }
        IsVisible = vis;

        var avail = ImGui.GetContentRegionAvail();
        int newW  = Math.Max(200, (int)avail.X);
        int newH  = Math.Max(200, (int)avail.Y);

        if (newW != _surfaceW || newH != _surfaceH)
        {
            _surfaceW = newW;
            _surfaceH = newH;
            _surface.Dispose();
            _surface = new RenderSurface(_surfaceW, _surfaceH);
        }

        // Rebuild tree every frame — state changes frequently during recording/playback
        var root = BuildTree(_surfaceW, _surfaceH, puzzle, config);
        _lastLayout = _layout.Compute(root, _surfaceW, _surfaceH);
        _renderer.Render(_surface.Canvas, root, _lastLayout, 0f);
        _texHandle = _textures.Upload(_surface);

        if (!_texHandle.HasValue) { ImGui.End(); return; }

        var imagePos = ImGui.GetCursorScreenPos();
        ImGui.Image(_texHandle.Value, new Vector2(_surfaceW, _surfaceH));
        bool imageHovered = ImGui.IsItemHovered();

        // ── Close button overlay ──────────────────────────────────────────────
        const float BtnSz = 22f, BtnPd = 8f;
        var closePos = new Vector2(imagePos.X + _surfaceW - BtnSz - BtnPd, imagePos.Y + BtnPd);
        ImGui.SetCursorScreenPos(closePos);
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f,    0f,    0f,    0.20f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.85f, 0.20f, 0.20f, 0.80f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.85f, 0.10f, 0.10f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(1f, 1f, 1f, 0.90f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        if (ImGui.Button("X##close_js", new Vector2(BtnSz, BtnSz))) IsVisible = false;
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
        bool overClose = ImGui.IsItemHovered();

        // ── Puzzle name InputText overlay ─────────────────────────────────────
        if (_nodePuzzleName != null && _lastLayout.TryGetValue(_nodePuzzleName, out var nameBox))
        {
            ImGui.SetCursorScreenPos(new Vector2(imagePos.X + nameBox.X, imagePos.Y + nameBox.Y));
            ImGui.SetNextItemWidth(nameBox.Width);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,         new Vector4(0f,    0f,    0f,    0.50f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,  new Vector4(0.08f, 0.18f, 0.12f, 0.80f));
            ImGui.PushStyleColor(ImGuiCol.Text,            new Vector4(1f,    1f,    1f,    0.90f));
            var name = puzzle.Name;
            if (ImGui.InputText("##pname", ref name, 80)) puzzle.Name = name;
            ImGui.PopStyleColor(3);
        }

        // ── Snap radius DragFloat overlay ─────────────────────────────────────
        if (_nodeSnapRadius != null && puzzle.Start != null
            && _lastLayout.TryGetValue(_nodeSnapRadius, out var snapBox))
        {
            ImGui.SetCursorScreenPos(new Vector2(imagePos.X + snapBox.X, imagePos.Y + snapBox.Y));
            ImGui.SetNextItemWidth(snapBox.Width);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0f,    0f,    0f,    0.50f));
            ImGui.PushStyleColor(ImGuiCol.SliderGrab,     ToImVec(Accent));
            ImGui.PushStyleColor(ImGuiCol.Text,           new Vector4(0.65f, 0.65f, 0.65f, 1f));
            float snap = puzzle.Start.SnapRadius;
            if (ImGui.DragFloat("##snap", ref snap, 0.1f, 0.5f, 30f, "snap %.1fy")) puzzle.Start.SnapRadius = snap;
            ImGui.PopStyleColor(3);
        }

        // ── Save name InputText overlay ───────────────────────────────────────
        if (_nodeSaveName != null && _lastLayout.TryGetValue(_nodeSaveName, out var saveNameBox))
        {
            ImGui.SetCursorScreenPos(new Vector2(imagePos.X + saveNameBox.X, imagePos.Y + saveNameBox.Y));
            ImGui.SetNextItemWidth(saveNameBox.Width);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,  new Vector4(0f, 0f, 0f, 0.50f));
            ImGui.PushStyleColor(ImGuiCol.Text,      new Vector4(1f, 1f, 1f, 0.85f));
            string sn = SaveName;
            if (ImGui.InputText("##savename", ref sn, 80)) SaveName = sn;
            ImGui.PopStyleColor(2);
        }

        // ── Puzzle list BeginChild overlay ────────────────────────────────────
        if (_nodePuzzleList != null && _lastLayout.TryGetValue(_nodePuzzleList, out var listBox))
        {
            ImGui.SetCursorScreenPos(new Vector2(imagePos.X + listBox.X, imagePos.Y + listBox.Y));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.04f, 0.07f, 0.05f, 0.95f));
            ImGui.BeginChild("##savedlist", new Vector2(listBox.Width, listBox.Height), false);

            for (int i = 0; i < config.SavedPuzzles.Count; i++)
            {
                var p = config.SavedPuzzles[i];
                ImGui.PushID(i);

                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.10f, 0.38f, 0.22f, 0.85f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.15f, 0.55f, 0.30f, 0.95f));
                if (ImGui.SmallButton("Load")) { OnLoad?.Invoke(i); _loadIndex = i; }
                ImGui.PopStyleColor(2);

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.45f, 0.12f, 0.12f, 0.85f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.18f, 0.18f, 0.95f));
                if (ImGui.SmallButton("Del"))
                {
                    OnDelete?.Invoke(i);
                    ImGui.PopStyleColor(2);
                    ImGui.PopID();
                    break;
                }
                ImGui.PopStyleColor(2);

                ImGui.SameLine();
                bool sel = i == _loadIndex;
                ImGui.PushStyleColor(ImGuiCol.Header,        new Vector4(0.08f, 0.22f, 0.12f, 1f));
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.12f, 0.30f, 0.18f, 1f));
                if (ImGui.Selectable($"{p.Name}  ({p.TotalFrameCount} fr, {p.Segments.Count} seg)##sl{i}", sel))
                    _loadIndex = i;
                ImGui.PopStyleColor(2);

                ImGui.PopID();
            }

            if (config.SavedPuzzles.Count == 0)
                ImGui.TextColored(new Vector4(0.35f, 0.35f, 0.42f, 1f), "No saved puzzles.");

            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        // ── Hit-test PUI pill buttons ─────────────────────────────────────────
        var mouse   = ImGui.GetIO().MousePos;
        float mx    = mouse.X - imagePos.X;
        float my    = mouse.Y - imagePos.Y;
        bool clicked = imageHovered && !overClose && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                    && !ImGui.IsAnyItemActive();   // don't steal focus from overlaid inputs

        bool isIdle     = _player.State == PlayState.Idle && _recorder.State == RecordState.Idle;
        bool isRec      = _recorder.State == RecordState.Recording;
        bool isPlaying  = _player.State is PlayState.Playing or PlayState.WpWaitLand or PlayState.WpNav;

        if (clicked)
        {
            bool Over(Node? n) => n != null
                && _lastLayout.TryGetValue(n, out var b)
                && mx >= b.X && mx <= b.Right && my >= b.Y && my <= b.Bottom;

            if      (Over(_btnSetHere)   && isIdle)                                              OnSetStart?.Invoke();
            else if (Over(_btnGoToStart) && isIdle && puzzle.Start != null && _mover.IsAvailable) OnGoToStart?.Invoke();
            else if (Over(_btnRec)       && isIdle && _mover.IsAvailable)                         OnRec?.Invoke();
            else if (Over(_btnAddSeg)    && isIdle && puzzle.HasRecording && _mover.IsAvailable)  OnAddSeg?.Invoke();
            else if (Over(_btnPlay)      && isIdle && puzzle.Start != null && puzzle.HasRecording && _mover.IsAvailable) OnPlay?.Invoke();
            else if (Over(_btnStop)      && (isRec || isPlaying))                                OnStop?.Invoke();
            else if (Over(_btnTrim)      && isPlaying)                                           OnTrim?.Invoke(_player.FrameIndex);
            else if (Over(_btnEditFrames)&& isIdle && puzzle.HasRecording)                       ShowEditor = true;
            else if (Over(_btnSave))                                                             OnSave?.Invoke(string.IsNullOrWhiteSpace(SaveName) ? puzzle.Name : SaveName);
            else if (Over(_btnDiagNat)   && !_diag.IsActive)                                    _diag.Start("natural");
            else if (Over(_btnDiagPlay)  && !_diag.IsActive)                                    _diag.Start("playback");
            else if (Over(_btnDiagStop)  && _diag.IsActive)                                     { _diag.Stop(); }
            else if (Over(_btnDiagToggle))                                                       _showDiag = !_showDiag;
        }

        ImGui.End();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PanacheUI node tree
    // ─────────────────────────────────────────────────────────────────────────

    private Node BuildTree(int w, int h, JumpPuzzle puzzle, Configuration config)
    {
        // Reset node references
        _btnSetHere = _btnGoToStart = null;
        _btnRec = _btnAddSeg = _btnPlay = _btnStop = _btnTrim = null;
        _btnEditFrames = _btnSave = null;
        _btnDiagNat = _btnDiagPlay = _btnDiagStop = _btnDiagToggle = null;
        _nodeSnapRadius = _nodePuzzleName = _nodeSaveName = _nodePuzzleList = null;
        _puzzleDelNodes.Clear();
        _puzzleLoadNodes.Clear();

        bool isIdle    = _player.State == PlayState.Idle && _recorder.State == RecordState.Idle;
        bool isRec     = _recorder.State == RecordState.Recording;
        bool isPlaying = _player.State is PlayState.Playing or PlayState.WpWaitLand or PlayState.WpNav;
        bool hookOk    = _mover.IsAvailable;

        var root = PUI.RootNode(w, h);

        root.AppendChild(BuildHeader(puzzle, isRec, isPlaying));
        root.AppendChild(PUI.SectionDivider(Accent.WithOpacity(0.30f)));
        root.AppendChild(BuildStartSection(puzzle, isIdle));
        root.AppendChild(PUI.SectionDivider(Accent.WithOpacity(0.12f)));
        root.AppendChild(BuildControlsSection(puzzle, isIdle, isRec, isPlaying, hookOk));
        root.AppendChild(PUI.SectionDivider(Accent.WithOpacity(0.12f)));
        root.AppendChild(BuildRecordingSection(puzzle, isIdle));
        if (_showDiag)
        {
            root.AppendChild(PUI.SectionDivider(Accent.WithOpacity(0.12f)));
            root.AppendChild(BuildDiagSection());
        }
        root.AppendChild(PUI.SectionDivider(Accent.WithOpacity(0.12f)));
        root.AppendChild(BuildPuzzlesSection(config));

        return root;
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private Node BuildHeader(JumpPuzzle puzzle, bool isRec, bool isPlaying)
    {
        var header = new Node().WithStyle(s =>
        {
            s.Flow                  = Flow.Vertical;
            s.WidthMode             = SizeMode.Fill;
            s.HeightMode            = SizeMode.Fit;
            s.BackgroundColor       = PColor.FromHex("#0A1F10");
            s.BackgroundGradientEnd = Theme.Panel;
            s.Padding               = new EdgeSize(12, 18, 8, 18);
            s.Gap                   = 6;
        });

        // Title row: "JumpSolver" + state badge + "diag" toggle
        var titleRow = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Horizontal; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit; s.Gap = 8;
        });

        titleRow.AppendChild(new Node().WithText("JumpSolver").WithStyle(s =>
        {
            s.FontSize = 18f; s.Bold = true; s.Color = Accent;
            s.WidthMode = SizeMode.Fit; s.HeightMode = SizeMode.Fit;
        }));

        // State badge
        var (stateText, stateColor) = GetStateLabel();
        titleRow.AppendChild(new Node().WithText($"[{stateText}]").WithStyle(s =>
        {
            s.FontSize = 11f; s.Bold = true; s.Color = stateColor;
            s.WidthMode = SizeMode.Fit; s.HeightMode = SizeMode.Fit;
            s.Padding = new EdgeSize(5, 8);
        }));

        if (!_mover.IsAvailable)
        {
            titleRow.AppendChild(new Node().WithText("hook unavailable").WithStyle(s =>
            {
                s.FontSize = 10f; s.Color = PColor.FromHex("#FF6633");
                s.WidthMode = SizeMode.Fit; s.HeightMode = SizeMode.Fit;
                s.Padding = new EdgeSize(4, 6);
            }));
        }

        // Spacer
        titleRow.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
        }));

        // Diag toggle
        var diagToggle = new Node().WithText(_showDiag ? "[diag ✓]" : "[diag]").WithStyle(s =>
        {
            s.FontSize = 10f;
            s.Color = _showDiag ? Accent.WithOpacity(0.90f) : Theme.TextSubtle;
            s.WidthMode = SizeMode.Fit; s.HeightMode = SizeMode.Fit;
            s.Padding = new EdgeSize(4, 8);
            s.BorderRadius = 4;
            s.BorderColor = _showDiag ? Accent.WithOpacity(0.40f) : Theme.TextSubtle.WithOpacity(0.20f);
            s.BorderWidth = 1;
        });
        _btnDiagToggle = diagToggle;
        titleRow.AppendChild(diagToggle);

        header.AppendChild(titleRow);

        // Puzzle name input placeholder (ImGui InputText overlaid here)
        var nameRow = new Node().WithId("puzzle-name-row").WithStyle(s =>
        {
            s.Flow = Flow.Horizontal; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit; s.Gap = 6;
        });

        // Placeholder label
        nameRow.AppendChild(new Node().WithText("Puzzle:").WithStyle(s =>
        {
            s.FontSize = 10f; s.Color = Theme.TextSubtle;
            s.WidthMode = SizeMode.Fit; s.HeightMode = SizeMode.Fit;
            s.Padding = new EdgeSize(3, 0, 0, 0);
        }));

        // Invisible placeholder for InputText
        var nameInput = new Node().WithId("puzzle-name-input").WithStyle(s =>
        {
            s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fixed; s.Height = 20;
            s.BackgroundColor = PColor.Transparent;
        });
        _nodePuzzleName = nameInput;
        nameRow.AppendChild(nameInput);

        header.AppendChild(nameRow);
        return header;
    }

    // ── Start section ─────────────────────────────────────────────────────────

    private Node BuildStartSection(JumpPuzzle puzzle, bool isIdle)
    {
        var content = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Vertical; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
            s.Padding = new EdgeSize(8, 14); s.Gap = 6;
        });

        content.AppendChild(PUI.SectionLabel("START POINT", Accent));

        // Button row
        var btnRow = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Horizontal; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit; s.Gap = 8;
        });

        var setHere = PUI.PillButton("btn-sethere", "Set Here", Accent);
        setHere.Style.Opacity = isIdle ? 1f : 0.40f;
        _btnSetHere = setHere;
        btnRow.AppendChild(setHere);

        bool canGo = puzzle.Start != null && isIdle && _mover.IsAvailable;
        var goTo = PUI.PillButton("btn-gotostart", "Go To Start", Accent);
        goTo.Style.Opacity = canGo ? 1f : 0.40f;
        _btnGoToStart = goTo;
        btnRow.AppendChild(goTo);

        content.AppendChild(btnRow);

        // Position display
        if (puzzle.Start != null)
        {
            var lp   = _objectTable.LocalPlayer;
            float dist = lp != null ? Vector3.Distance(lp.Position, puzzle.Start.Position) : float.MaxValue;
            var s = puzzle.Start;

            PColor distColor = dist < 0.5f          ? PColor.FromHex("#44FF66")
                             : dist < s.SnapRadius   ? PColor.FromHex("#FFEE44")
                             :                         Theme.TextSubtle;

            content.AppendChild(new Node()
                .WithText($"({s.Position.X:F1}, {s.Position.Y:F1}, {s.Position.Z:F1})  {dist:F1}y")
                .WithStyle(st =>
                {
                    st.FontSize  = 10f; st.Color = distColor;
                    st.WidthMode = SizeMode.Fill; st.HeightMode = SizeMode.Fit;
                }));

            // Snap radius drag placeholder
            var snapNode = new Node().WithId("snap-radius").WithStyle(st =>
            {
                st.WidthMode = SizeMode.Fixed; st.Width = 160;
                st.HeightMode = SizeMode.Fixed; st.Height = 18;
                st.BackgroundColor = PColor.Transparent;
            });
            _nodeSnapRadius = snapNode;
            content.AppendChild(snapNode);
        }
        else
        {
            content.AppendChild(new Node().WithText("No start point set").WithStyle(s =>
            {
                s.FontSize = 10f; s.Color = Theme.TextSubtle;
                s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
            }));
        }

        return PUI.SectionWrap(Accent, content);
    }

    // ── Controls section ──────────────────────────────────────────────────────

    private Node BuildControlsSection(JumpPuzzle puzzle,
        bool isIdle, bool isRec, bool isPlaying, bool hookOk)
    {
        var content = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Horizontal; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
            s.Padding = new EdgeSize(8, 14); s.Gap = 8;
        });

        if (isRec)
        {
            content.AppendChild(new Node()
                .WithText($"● REC  {_recorder.CapturedFrames.Count} frames")
                .WithStyle(s =>
                {
                    s.FontSize = 12f; s.Bold = true;
                    s.Color = PColor.FromHex("#FF4444");
                    s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
                    s.Padding = new EdgeSize(3, 0);
                }));

            var stop = PUI.PillButton("btn-stop", "Stop", PColor.FromHex("#FF4444"));
            _btnStop = stop;
            content.AppendChild(stop);
        }
        else if (isPlaying)
        {
            string playLabel = _player.State switch
            {
                PlayState.WpWaitLand => "IN AIR",
                PlayState.WpNav      => "MOVING",
                _                    => $"PLAYING  {_player.FrameIndex}/{_player.PlaybackFrameCount}"
            };
            content.AppendChild(new Node().WithText(playLabel).WithStyle(s =>
            {
                s.FontSize = 12f; s.Bold = true;
                s.Color = PColor.FromHex("#44CCFF");
                s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
                s.Padding = new EdgeSize(3, 0);
            }));

            var stop = PUI.PillButton("btn-stop", "Stop", PColor.FromHex("#4488CC"));
            _btnStop = stop;
            content.AppendChild(stop);

            var trim = PUI.PillButton("btn-trim", "Trim here", PColor.FromHex("#CC3333"));
            _btnTrim = trim;
            content.AppendChild(trim);
        }
        else
        {
            var rec = PUI.PillButton("btn-rec", "Rec", Accent);
            rec.Style.Opacity = (isIdle && hookOk) ? 1f : 0.40f;
            _btnRec = rec;
            content.AppendChild(rec);

            bool canSeg = puzzle.HasRecording && isIdle && hookOk;
            var seg = PUI.PillButton("btn-addseg", "+ Seg", Accent);
            seg.Style.Opacity = canSeg ? 1f : 0.40f;
            _btnAddSeg = seg;
            content.AppendChild(seg);

            bool canPlay = puzzle.Start != null && puzzle.HasRecording && isIdle && hookOk;
            var play = PUI.PillButton("btn-play", "Play", PColor.FromHex("#44AAFF"));
            play.Style.Opacity = canPlay ? 1f : 0.40f;
            _btnPlay = play;
            content.AppendChild(play);
        }

        return PUI.SectionWrap(Accent, content);
    }

    // ── Recording section ─────────────────────────────────────────────────────

    private Node BuildRecordingSection(JumpPuzzle puzzle, bool isIdle)
    {
        var content = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Vertical; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
            s.Padding = new EdgeSize(8, 14); s.Gap = 6;
        });

        if (!puzzle.HasRecording)
        {
            content.AppendChild(new Node().WithText("No recording yet. Press Rec and run the puzzle.").WithStyle(s =>
            {
                s.FontSize = 10f; s.Color = Theme.TextSubtle;
                s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
            }));
            return PUI.SectionWrap(Accent, content);
        }

        // Summary row
        var summaryRow = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Horizontal; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit; s.Gap = 8;
        });

        summaryRow.AppendChild(new Node()
            .WithText($"{puzzle.TotalFrameCount} frames  •  {puzzle.TotalJumpCount} jump(s)  •  {puzzle.Segments.Count} segment(s)")
            .WithStyle(s =>
            {
                s.FontSize = 11f; s.Color = Theme.TextMuted;
                s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
            }));

        bool canEdit = isIdle;
        var editBtn = PUI.PillButton("btn-editframes", "Edit Frames", Accent);
        editBtn.Style.Opacity = canEdit ? 1f : 0.40f;
        _btnEditFrames = editBtn;
        summaryRow.AppendChild(editBtn);

        content.AppendChild(summaryRow);

        // Segment list
        for (int i = 0; i < puzzle.Segments.Count; i++)
        {
            var seg = puzzle.Segments[i];
            var segRow = new Node().WithStyle(s =>
            {
                s.Flow = Flow.Horizontal; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit; s.Gap = 6;
            });

            bool isActive = _player.State == PlayState.Playing;
            int  offset   = puzzle.Segments.Take(i).Sum(sg => sg.Frames.Count);
            bool playing  = isActive && _player.FrameIndex >= offset
                                     && _player.FrameIndex <  offset + seg.Frames.Count;

            PColor textColor = playing ? Accent : Theme.TextMuted;
            segRow.AppendChild(new Node()
                .WithText($"{i + 1}. {seg.Name}  •  {seg.Frames.Count} fr  •  {seg.JumpCount} jump(s)")
                .WithStyle(s =>
                {
                    s.FontSize = 10f; s.Color = textColor;
                    s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
                }));

            if (isIdle && puzzle.Segments.Count > 0)
            {
                var delSeg = new Node().WithText("Del").WithStyle(s =>
                {
                    s.FontSize = 9f; s.Color = PColor.FromHex("#FF6655");
                    s.WidthMode = SizeMode.Fit; s.HeightMode = SizeMode.Fit;
                    s.Padding = new EdgeSize(2, 6);
                    s.BorderRadius = 3;
                    s.BorderColor = PColor.FromHex("#FF6655").WithOpacity(0.40f);
                    s.BorderWidth = 1;
                });
                _puzzleDelNodes.Add((delSeg, i));
                segRow.AppendChild(delSeg);
            }

            content.AppendChild(segRow);
        }

        return PUI.SectionWrap(Accent, content);
    }

    // ── Diag section ──────────────────────────────────────────────────────────

    private Node BuildDiagSection()
    {
        var content = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Vertical; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
            s.Padding = new EdgeSize(8, 14); s.Gap = 6;
        });

        content.AppendChild(PUI.SectionLabel("DIAGNOSTIC CAPTURE", PColor.FromHex("#AAAAAA")));

        var btnRow = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Horizontal; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit; s.Gap = 8;
        });

        if (_diag.IsActive)
        {
            btnRow.AppendChild(new Node().WithText("● CAPTURING").WithStyle(s =>
            {
                s.FontSize = 11f; s.Bold = true; s.Color = PColor.FromHex("#FF4444");
                s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
            }));
            var stopDiag = PUI.PillButton("btn-diagstop", "Stop", PColor.FromHex("#FF4444"));
            _btnDiagStop = stopDiag;
            btnRow.AppendChild(stopDiag);
        }
        else
        {
            var nat = PUI.PillButton("btn-diagnat", "Natural", PColor.FromHex("#AAAAAA"));
            _btnDiagNat = nat;
            btnRow.AppendChild(nat);

            var play = PUI.PillButton("btn-diagplay", "Playback", PColor.FromHex("#AAAAAA"));
            _btnDiagPlay = play;
            btnRow.AppendChild(play);
        }

        content.AppendChild(btnRow);
        return PUI.SectionWrap(PColor.FromHex("#888888"), content);
    }

    // ── Puzzles section ───────────────────────────────────────────────────────

    private Node BuildPuzzlesSection(Configuration config)
    {
        var content = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Vertical; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit;
            s.Padding = new EdgeSize(8, 14); s.Gap = 6;
        });

        content.AppendChild(PUI.SectionLabel("PUZZLES", Accent));

        // Save name placeholder + Save button row
        var saveRow = new Node().WithStyle(s =>
        {
            s.Flow = Flow.Horizontal; s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fit; s.Gap = 8;
        });

        var saveNameNode = new Node().WithId("save-name-input").WithStyle(s =>
        {
            s.WidthMode = SizeMode.Fill; s.HeightMode = SizeMode.Fixed; s.Height = 20;
            s.BackgroundColor = PColor.Transparent;
        });
        _nodeSaveName = saveNameNode;
        saveRow.AppendChild(saveNameNode);

        var saveBtn = PUI.PillButton("btn-save", "Save", Accent);
        _btnSave = saveBtn;
        saveRow.AppendChild(saveBtn);

        content.AppendChild(saveRow);

        // Puzzle list placeholder (ImGui.BeginChild overlaid here)
        var listNode = new Node().WithId("puzzle-list").WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = 100;
            s.BackgroundColor = PColor.Transparent;
        });
        _nodePuzzleList = listNode;
        content.AppendChild(listNode);

        return PUI.SectionWrap(Accent, content);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private (string text, PColor color) GetStateLabel()
    {
        if (_player.State == PlayState.Playing)
            return ($"PLAYING {_player.FrameIndex}/{_player.PlaybackFrameCount}", PColor.FromHex("#44CCFF"));
        if (_player.State == PlayState.WpWaitLand)
            return ("IN AIR",         PColor.FromHex("#88DDFF"));
        if (_player.State == PlayState.WpNav)
            return ("MOVING",         PColor.FromHex("#44CCFF"));
        if (_player.State == PlayState.WalkingToStart)
            return ("WALKING",        PColor.FromHex("#FFDD44"));
        if (_recorder.State == RecordState.Recording)
            return ($"REC {_recorder.CapturedFrames.Count}", PColor.FromHex("#FF4444"));
        return ("idle", Theme.TextSubtle);
    }

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
