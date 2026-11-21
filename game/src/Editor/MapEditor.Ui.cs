// The map editor's interface (MapEditor.cs): the top bar, the tool palette, the properties panel with the minimap, the
// status line - built in code in the game's look (PwTheme's buttons, fields and sliders; PwWindow's frames). The
// middle stays open: the map is seen and edited there (mouse input that no panel takes goes to the editor).
using System;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Editor;
using PwRemake.UI;

namespace PwRemake.Editor;

public partial class MapEditor
{
    Control _dialogs = null!;
    EditorMinimap _minimap = null!;
    Label _status = null!, _title = null!, _note = null!;
    Button _undo = null!, _redo = null!, _save = null!, _saveAs = null!, _test = null!;
    VBoxContainer _palette = null!, _props = null!;
    double _noteT;

    /// <summary>the editor's panels: top bar / (palette | map | properties) / status line</summary>
    void BuildUi()
    {
        var layer = new CanvasLayer { Name = "EditorUi", Layer = 10 };
        AddChild(layer);
        var root = new Control { Theme = PwTheme.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(root);
        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        col.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        col.AddThemeConstantOverride("separation", 0);
        root.AddChild(col);

        // ---- top bar
        var top = Bar();
        col.AddChild(top);
        var tb = new HBoxContainer();
        tb.AddThemeConstantOverride("separation", 6);
        top.AddChild(tb);
        Button B(string text, Action go, string tip, float w = 92) { var b = PwTheme.Btn(text, go, w); b.TooltipText = tip; tb.AddChild(b); return b; }
        B("New", NewMap, "A new flat map (Ctrl+N)");
        B("Open", OpenMap, "Open one of your maps, a map of the installation or a campaign mission (Ctrl+O)");
        _save = B("Save", () => Save(false), "Save the map into My maps (Ctrl+S)");
        _saveAs = B("Save as", () => Save(true), "Save the map under another name in My maps (Ctrl+Shift+S)", 104);
        B("Export", ExportPwmap, "Export the map as one .pwmap file to share (Ctrl+E)", 86);
        B("Import", ImportPwmap, "Import a .pwmap file into My maps and open it", 86);
        tb.AddChild(new Control { CustomMinimumSize = new Vector2(10, 0) });
        _undo = B("Undo", Undo, "Undo (Ctrl+Z)");
        _redo = B("Redo", Redo, "Redo (Ctrl+Y)");
        tb.AddChild(new Control { CustomMinimumSize = new Vector2(10, 0) });
        _test = B("Test map", TestMap, "Play a skirmish on the map as it is now - no saving needed; its end comes back here (F5)", 120);
        B("Exit", Exit, "Back to the main menu (Esc)", 80);
        _title = new Label { Text = "Map Editor", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _title.AddThemeColorOverride("font_color", PwTheme.Hi);
        _title.AddThemeFontSizeOverride("font_size", 17);
        tb.AddChild(_title);

        // ---- the middle: palette | map | properties
        var mid = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        mid.AddThemeConstantOverride("separation", 0);
        col.AddChild(mid);
        var left = new PwWindow("Tools", 236) { SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };
        _palette = left.Body;
        mid.AddChild(left);
        mid.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });   // (the map)
        var rightCol = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        rightCol.AddThemeConstantOverride("separation", 0);
        mid.AddChild(rightCol);
        var right = new PwWindow("Properties", 300) { SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };
        _props = right.Body;
        rightCol.AddChild(right);
        rightCol.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        var mapWin = new PwWindow("Map", 300);
        _minimap = new EditorMinimap
        {
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            CameraPose = () => _cam == null ? (0, 0, 0) : (_cam.X, _cam.Z, _cam.Yaw),
            LookAt = (x, z) => { if (_cam != null) { _cam.X = x; _cam.Z = z; } },
        };
        mapWin.Body.AddChild(_minimap);
        rightCol.AddChild(mapWin);

        // ---- status line
        var bottom = Bar();
        col.AddChild(bottom);
        var sb = new HBoxContainer();
        bottom.AddChild(sb);
        _status = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        _status.AddThemeFontSizeOverride("font_size", 14);
        sb.AddChild(_status);
        _note = new Label();
        _note.AddThemeColorOverride("font_color", PwTheme.Hi);
        _note.AddThemeFontSizeOverride("font_size", 14);
        sb.AddChild(_note);

        _dialogs = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _dialogs.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_dialogs);
        BuildPalette();
        BuildProperties();
    }

    /// <summary>a bar across the screen: the original's dark brown with a thin gold edge</summary>
    static PanelContainer Bar()
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        var box = new StyleBoxFlat { BgColor = new Color(0.1f, 0.07f, 0.035f, 0.94f), BorderColor = new Color(PwTheme.Dim, 0.7f), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        box.BorderWidthTop = box.BorderWidthBottom = 1;
        p.AddThemeStyleboxOverride("panel", box);
        return p;
    }

    static Label Caption(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", PwTheme.Hi);
        l.AddThemeFontSizeOverride("font_size", 15);
        return l;
    }

    // the palette's groups: the tools of each (the later phases fill them: docs/MAP_EDITOR_PLAN.md)
    static readonly (string Group, string Tip, string[] Tools, string Later)[] GROUPS =
    {
        ("Terrain", "Shape the ground; the water level", Array.Empty<string>(), ""),          // (phase 2: Tools/TerrainTools.cs)
        ("Paint", "Ground materials, biomes, walkability, forests, plants", Array.Empty<string>(), ""),   // (Tools/PaintToolSet.cs)
        ("Objects", "Buildings, units, animals, resources, decoration", Array.Empty<string>(), ""),       // (phase 3: Tools/ObjectTool.cs)
        ("Players", "Slots, tribes, teams, start locations; the map's settings", Array.Empty<string>(), ""),   // (phase 4: Tools/PlayerPanels.cs)
        ("Regions", "Regions, groups, quests, camera points", Array.Empty<string>(), "Phase 5"),
        ("Triggers", "The map's triggers (Lua)", Array.Empty<string>(), "Phase 6"),
    };

    void BuildPalette()
    {
        foreach (var c in _palette.GetChildren()) { _palette.RemoveChild(c); c.QueueFree(); }
        _palette.AddThemeConstantOverride("separation", 4);
        foreach (var g in GROUPS)
        {
            bool open = EditorSession.ToolGroup == g.Group;
            var head = new Button { Text = (open ? "▾ " : "▸ ") + g.Group, ToggleMode = true, ButtonPressed = open, TooltipText = g.Tip, FocusMode = Control.FocusModeEnum.None, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(168, 28) };
            head.ThemeTypeVariation = "PwTab";
            var gg = g.Group;
            head.Pressed += () => { EditorSession.ToolGroup = gg; BuildPalette(); BuildProperties(); };
            _palette.AddChild(head);
            if (!open) continue;
            // the group's tools (EditorTools: one chosen per group)
            var tools = EditorTools.In(g.Group).ToList();
            if (tools.Count > 0)
            {
                var tg = new ButtonGroup();
                foreach (var tl in tools)
                {
                    var b = new CheckBox { Text = tl.Label, ButtonGroup = tg, ButtonPressed = EditorTools.Chosen(g.Group) == tl, FocusMode = Control.FocusModeEnum.None, TooltipText = tl.Tip };
                    var tt = tl;
                    b.Toggled += on => { if (on) { EditorTools.Choose(tt); BuildProperties(); } };
                    _palette.AddChild(b);
                }
                if (g.Group == "Objects" && _doc != null) BuildObjectPalette();          // (the classes: Tools/ObjectPanels.cs)
                if (g.Group == "Players" && _doc != null) BuildPlayersPalette();         // (the map's settings: Tools/PlayerPanels.cs)
                continue;
            }
            foreach (var t in g.Tools)
            {
                var tool = new CheckBox { Text = t, ButtonPressed = true, FocusMode = Control.FocusModeEnum.None, TooltipText = "Left mouse: raise; Ctrl + left mouse: lower" };
                _palette.AddChild(tool);
            }
            var later = PwTheme.Para(g.Tools.Length > 0 ? "Coming: " + g.Later : $"Coming in {g.Later.ToLowerInvariant()} of the map editor.", 12);
            later.CustomMinimumSize = new Vector2(168, 0);
            _palette.AddChild(later);
        }
    }

    HSlider Slider(double min, double max, double step, double value, Action<double> set)
    {
        var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(150, 22), FocusMode = Control.FocusModeEnum.None };
        s.ValueChanged += v => set(v);
        return s;
    }

    void BuildProperties()
    {
        foreach (var c in _props.GetChildren()) { _props.RemoveChild(c); c.QueueFree(); }
        // the chosen tool's settings (EditorTool.BuildProps)
        if (EditorTools.Current is { } tool) tool.BuildProps(new ToolUi(_props), _view != null && _doc != null && _src != null ? Ctx() : null);
        else _props.AddChild(PwTheme.Para("No tools in this group yet.", 13));

        // the map
        _props.AddChild(new HSeparator());
        _props.AddChild(Caption("Map"));
        _mapInfo = PwTheme.Para("", 13, PwTheme.Text);
        _props.AddChild(_mapInfo);

        // Test map's players
        _props.AddChild(new HSeparator());
        _props.AddChild(Caption("Test map"));
        BuildTestMapInfo();                                   // (the map's players: Tools/PlayerPanels.cs)
        RefreshUi();
    }
    Label? _mapInfo;

    /// <summary>the brush's settings changed by a key ([ ] - =): the panel shows them</summary>
    void SyncBrushUi() => BuildProperties();

    /// <summary>the buttons, the title and the map's info after a change</summary>
    void RefreshUi()
    {
        var h = EditorSession.History;
        var d = EditorSession.Doc;
        if (h == null || d == null) return;
        _undo.Disabled = !h.CanUndo; _undo.TooltipText = h.CanUndo ? $"Undo {h.UndoName} (Ctrl+Z)" : "Nothing to undo";
        _redo.Disabled = !h.CanRedo; _redo.TooltipText = h.CanRedo ? $"Redo {h.RedoName} (Ctrl+Y)" : "Nothing to redo";
        _save.TooltipText = EditorSession.SavesInPlace(d) ? $"Save the map: {d.SourcePath} (Ctrl+S)" : "Save the map into My maps - a name is asked (Ctrl+S)";
        _title.Text = $"{d.Name}{(h.Dirty ? " *" : "")}  ·  Map Editor";
        if (_mapInfo != null)
            _mapInfo.Text = $"{d.Name}\n{d.HeightsW * d.Cell:0} x {d.HeightsH * d.Cell:0} m · {d.Setting}\nwater {(d.Water is double w ? $"{w:0.#} m" : "none")} · {d.MaxPlayers} players" +
                            (d.SourcePath != null ? $"\n{(EditorSession.SavesInPlace(d) ? "My maps: " : "")}{System.IO.Path.GetFileName(d.SourcePath)}" : "\nnew map (not saved yet)");
    }

    void SetStatus(string s) => _status.Text = s;

    /// <summary>a short note at the right of the status line (a few seconds)</summary>
    void SetStatusNote(string s) { _note.Text = s; _noteT = 3; }

    void UpdateStatus()
    {
        if ((_noteT -= GetProcessDeltaTime()) <= 0 && _note.Text.Length > 0) _note.Text = "";
        var d = _doc;
        if (d == null || _src == null) return;
        string where;
        if (_hover is { } p)
        {
            var (mx, my) = _src.ToMap(p.X, p.Z);
            where = $"x {mx:0.0}  y {my:0.0}   height {d.HeightAt(mx, my):0.00} m   material {d.MaterialAt(mx, my)}   walkable {(d.WalkableAt(mx, my) ? "yes" : "no")}";
        }
        else where = "the cursor is not over the map";
        string tool = _view != null && EditorTools.Current is { } t ? t.Status(Ctx()) : EditorSession.ToolGroup;
        _status.Text = $"{where}     |     {tool}     |     {History.UndoCount} undo steps";
    }

    void ShowDialog(EditorDialog d) => _dialogs.AddChild(d);
}
