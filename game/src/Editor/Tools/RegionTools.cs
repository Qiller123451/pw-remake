// The map editor's "Regions" group (docs/MAP_EDITOR.md "Regions and mission data", plan phase 5): the mission data a
// map's triggers work with, edited on the map and in the properties panel. Every change is a command of the history
// (MissionEdits: undo / redo).
//   Regions        draw a rectangle or an oval (a drag; "add to the chosen region": another shape of it), click a
//                  region to choose it, drag it to move it (Shift: the whole region), drag a handle to resize the
//                  chosen shape; the panel: the regions, rename, each shape's form and "on at the start", delete
//   Groups         named sets of placed objects: click an object on the map to put it into the chosen group or take
//                  it out (the object gets a GUID of its own, world.lua's id), "add the selection" of the object tool
//   Variables      name, value at the start (renaming one renames it in the triggers)
//   Quests         name, headline, description, group title (texts of the map's language), main / optional, hidden at
//                  the start, accomplished / failed at the start, bonus points per difficulty
//   Question marks click on the map to put the chosen one there (or a new one), its state at the start
//   Cameras        camera points from the editor's view, the start camera (where the mission's camera begins)
// The mission data is drawn on the map by the RegionLayer while this group or the Triggers group is open.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Maps;
using PwRemake.UI;

namespace PwRemake.Editor;

/// <summary>what the region tools keep while the editor screen is away: the chosen entries, the drawing mode</summary>
public static class RegionState
{
    public enum Mode { Select, Rectangle, Oval }
    public static Mode DrawMode = Mode.Select;
    /// <summary>the chosen region and shape (-1: none)</summary>
    public static int Region = -1, Shape = -1;
    public static int Group = -1, Quest = -1, QuestionMark = -1, Camera = -1;
    /// <summary>a drawn shape becomes another shape of the chosen region</summary>
    public static bool AddToRegion;
    /// <summary>the regions drawn whatever group is open</summary>
    public static bool ShowAlways;

    public static void Clear() { Region = Shape = Group = Quest = QuestionMark = Camera = -1; }
}

/// <summary>the tools of the Regions and Triggers groups (EditorTools.All)</summary>
public static class MissionToolSet
{
    public static IEnumerable<EditorTool> Create() => new EditorTool[]
    {
        new RegionTool(), new GroupTool(), new MissionPanelTool("Variables", "Variables of the triggers: name and value at the start", ed => ed.BuildVariablesProps()),
        new MissionPanelTool("Quests", "The quests: texts, state at the start, bonus", ed => ed.BuildQuestsProps()),
        new QuestionMarkTool(), new MissionPanelTool("Cameras", "Camera points and the start camera", ed => ed.BuildCamerasProps()),
        new TriggerTool(),
    };
}

/// <summary>small controls of the mission panels in the game's look (on top of ToolUi)</summary>
public static class MissionUi
{
    /// <summary>a text field: set gets the text when Enter is pressed or the field is left (and it changed)</summary>
    public static LineEdit Field(Control box, string label, string text, Action<string> set, string tip = "", float labelW = 96)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        if (label.Length > 0) row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(labelW, 0), TooltipText = tip });
        var e = new LineEdit { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = tip, CustomMinimumSize = new Vector2(80, 0) };
        string last = text;
        void Apply() { if (e.Text != last) { last = e.Text; set(e.Text); } }
        e.TextSubmitted += _ => Apply();
        e.FocusExited += Apply;
        row.AddChild(e);
        box.AddChild(row);
        return e;
    }

    /// <summary>a list to choose from (one line per entry); pick gets the index</summary>
    public static ItemList List(Control box, IEnumerable<string> items, int chosen, Action<int> pick, float height = 150)
    {
        var l = new ItemList { CustomMinimumSize = new Vector2(240, height), SelectMode = ItemList.SelectModeEnum.Single, FocusMode = Control.FocusModeEnum.None };
        foreach (var s in items) l.AddItem(s);
        if (chosen >= 0 && chosen < l.ItemCount) { l.Select(chosen); l.EnsureCurrentIsVisible(); }
        l.ItemSelected += i => pick((int)i);
        box.AddChild(l);
        return l;
    }

    public static HBoxContainer Buttons(Control box, params (string Text, Action Go, string Tip)[] bs)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 4);
        foreach (var (t, go, tip) in bs) { var b = PwTheme.Btn(t, go, 0); b.TooltipText = tip; b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; h.AddChild(b); }
        box.AddChild(h);
        return h;
    }

    /// <summary>the panel's content in a scroll box of at most that height (long lists)</summary>
    public static VBoxContainer Scroll(ToolUi ui, float height = 560)
    {
        var sc = new ScrollContainer { CustomMinimumSize = new Vector2(250, height), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var v = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 6);
        sc.AddChild(v);
        ui.Box.AddChild(sc);
        return v;
    }

    public static string F(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}

// ==================================================================================================== Regions
public sealed class RegionTool : EditorTool
{
    public override string Group => "Regions";
    public override string Label => "Regions";
    public override string Tip => "Draw rectangles and ovals (a drag), click a region to choose it, drag it to move (Shift: the whole region), drag a handle to resize; Delete removes the chosen shape";

    enum Drag { None, Draw, Move, Resize }
    Drag _drag;
    double _x0, _y0, _x1, _y1, _lastX, _lastY;
    int _handle;
    EdShape? _orig;

    public override void Reset() { _drag = Drag.None; }

    public override void Begin(ToolContext c, ToolPoint p)
    {
        var m = c.Doc.Mission;
        _x0 = _x1 = _lastX = p.Mx; _y0 = _y1 = _lastY = p.My;
        if (RegionState.DrawMode != RegionState.Mode.Select) { _drag = Drag.Draw; return; }
        // a handle of the chosen shape
        if (RegionState.Region >= 0 && RegionState.Region < m.Regions.Count && RegionState.Shape >= 0 && RegionState.Shape < m.Regions[RegionState.Region].Shapes.Count)
        {
            var s = m.Regions[RegionState.Region].Shapes[RegionState.Shape];
            int k = 0;
            foreach (var (hx, hy) in RegionLayer.Handles(s))
            {
                if (Math.Abs(hx - p.Mx) <= RegionLayer.Handle && Math.Abs(hy - p.My) <= RegionLayer.Handle) { _drag = Drag.Resize; _handle = k; _orig = s.Clone(); return; }
                k++;
            }
        }
        // the smallest shape under the mouse (the chosen region's first)
        (int R, int S, double A)? best = null;
        for (int i = 0; i < m.Regions.Count; i++)
            for (int k = 0; k < m.Regions[i].Shapes.Count; k++)
            {
                var s = m.Regions[i].Shapes[k];
                if (!s.Contains(p.Mx, p.My)) continue;
                double a = s.W * s.H * (i == RegionState.Region ? 0.5 : 1);
                if (best == null || a < best.Value.A) best = (i, k, a);
            }
        if (best is { } b)
        {
            bool changed = RegionState.Region != b.R || RegionState.Shape != b.S;
            RegionState.Region = b.R; RegionState.Shape = b.S;
            _drag = Drag.Move;
            if (changed) MapEditor.MissionSelectionChanged();
        }
        else if (RegionState.Region >= 0) { RegionState.Region = RegionState.Shape = -1; _drag = Drag.None; MapEditor.MissionSelectionChanged(); }
    }

    public override void Step(ToolContext c, ToolPoint? p, double dt)
    {
        if (p is not { } q) return;
        var m = c.Doc.Mission;
        switch (_drag)
        {
            case Drag.Draw: _x1 = q.Mx; _y1 = q.My; break;
            case Drag.Move:
                {
                    double dx = q.Mx - _lastX, dy = q.My - _lastY;
                    if (Math.Abs(dx) + Math.Abs(dy) < 0.05 || RegionState.Region < 0) return;
                    c.Run(MissionEdits.MoveShape(m, RegionState.Region, c.Shift ? -1 : RegionState.Shape, dx, dy));
                    _lastX = q.Mx; _lastY = q.My;
                    break;
                }
            case Drag.Resize when _orig != null:
                {
                    double x0 = _orig.X, y0 = _orig.Y, x1 = _orig.X + _orig.W, y1 = _orig.Y + _orig.H;
                    // handles 0..7 clockwise from the north-west corner (RegionLayer.Handles)
                    if (_handle is 0 or 6 or 7) x0 = Math.Min(q.Mx, x1 - 1);
                    if (_handle is 2 or 3 or 4) x1 = Math.Max(q.Mx, x0 + 1);
                    if (_handle is 0 or 1 or 2) y0 = Math.Min(q.My, y1 - 1);
                    if (_handle is 4 or 5 or 6) y1 = Math.Max(q.My, y0 + 1);
                    c.Run(MissionEdits.SetShapeBox(m, RegionState.Region, RegionState.Shape, x0, y0, x1 - x0, y1 - y0));
                    break;
                }
        }
    }

    public override void End(ToolContext c, ToolPoint? p)
    {
        var m = c.Doc.Mission;
        if (_drag == Drag.Draw)
        {
            if (p is { } q) { _x1 = q.Mx; _y1 = q.My; }
            double x = Math.Min(_x0, _x1), y = Math.Min(_y0, _y1), w = Math.Abs(_x1 - _x0), h = Math.Abs(_y1 - _y0);
            if (w >= 2 && h >= 2)
            {
                bool oval = RegionState.DrawMode == RegionState.Mode.Oval;
                double z = c.Doc.HeightAt(x + w / 2, y + h / 2);
                if (RegionState.AddToRegion && RegionState.Region >= 0 && RegionState.Region < m.Regions.Count)
                {
                    c.Run(MissionEdits.AddShape(m, RegionState.Region, oval, x, y, w, h, z), merge: false);
                    RegionState.Shape = m.Regions[RegionState.Region].Shapes.Count - 1;
                }
                else
                {
                    c.Run(MissionEdits.NewRegion(m, oval, x, y, w, h, z), merge: false);
                    RegionState.Region = m.Regions.Count - 1; RegionState.Shape = 0;
                }
                c.Note($"{(oval ? "oval" : "rectangle")} {w:0} x {h:0} m");
                MapEditor.MissionSelectionChanged();
            }
            else c.Note("Drag to draw a region (at least 2 x 2 m)");
        }
        _drag = Drag.None; _orig = null;
    }

    public override ToolCursor Cursor(ToolContext c, ToolPoint? p)
    {
        var cur = new ToolCursor();
        if (_drag != Drag.Draw) return cur;
        var col = new Color(1, 1, 0.5f);
        double x = Math.Min(_x0, _x1), y = Math.Min(_y0, _y1), w = Math.Abs(_x1 - _x0), h = Math.Abs(_y1 - _y0);
        var pts = new List<(double, double)>();
        if (RegionState.DrawMode == RegionState.Mode.Oval)
            for (int k = 0; k <= 48; k++) { double a = 2 * Math.PI * k / 48; pts.Add((x + w / 2 + w / 2 * Math.Cos(a), y + h / 2 + h / 2 * Math.Sin(a))); }
        else pts.AddRange(new[] { (x, y), (x + w, y), (x + w, y + h), (x, y + h), (x, y) });
        cur.Lines = new();
        for (int k = 0; k + 1 < pts.Count; k++)
        {
            var (ax, az) = c.Source.ToGame(pts[k].Item1, pts[k].Item2); var (bx, bz) = c.Source.ToGame(pts[k + 1].Item1, pts[k + 1].Item2);
            cur.Lines.Add((ax, az, bx, bz, col));
        }
        return cur;
    }

    public override string Status(ToolContext c)
    {
        var m = c.Doc.Mission;
        string chosen = RegionState.Region >= 0 && RegionState.Region < m.Regions.Count ? $" · {m.Regions[RegionState.Region].Name} (shape {RegionState.Shape + 1})" : "";
        return $"regions: {m.Regions.Count}{chosen} · {RegionState.DrawMode.ToString().ToLowerInvariant()}";
    }

    public override void BuildProps(ToolUi ui, ToolContext? c)
    {
        if (c == null || MapEditor.Live is not { } ed) { ui.Note("Loading the map …"); return; }
        ed.BuildRegionProps(ui, c);
    }
}

public sealed class GroupTool : EditorTool
{
    public override string Group => "Regions";
    public override string Label => "Groups";
    public override string Tip => "Named sets of objects: click an object on the map to put it into the chosen group (or take it out)";
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p) => default;

    public override void Begin(ToolContext c, ToolPoint p)
    {
        var m = c.Doc.Mission;
        if (RegionState.Group < 0 || RegionState.Group >= m.Groups.Count) { c.Note("Choose or make a group first"); return; }
        var o = MapEditor.NearestObject(c.Doc, p.Mx, p.My, 4);
        if (o == null) { c.Note("No object there"); return; }
        var (gc, guid) = MissionEdits.EnsureGuid(o);
        bool member = m.Groups[RegionState.Group].Members.Contains(guid);
        var change = MissionEdits.Change<EdGroup>(m, RegionState.Group, g => { if (member) g.Members.Remove(guid); else g.Members.Add(guid); }, member ? "Take out of the group" : "Put into the group");
        c.Run(gc == null ? change : new CompositeEdit(change.Name, new[] { gc, change }), merge: false);
        c.Note($"{o.Name} {(member ? "taken out of" : "put into")} {m.Groups[RegionState.Group].Name}");
    }

    public override string Status(ToolContext c)
    {
        var m = c.Doc.Mission;
        return RegionState.Group >= 0 && RegionState.Group < m.Groups.Count ? $"group {m.Groups[RegionState.Group].Name}: {m.Groups[RegionState.Group].Members.Count} objects" : $"groups: {m.Groups.Count}";
    }

    public override void BuildProps(ToolUi ui, ToolContext? c) { if (c != null && MapEditor.Live is { } ed) ed.BuildGroupProps(ui, c); else ui.Note("Loading the map …"); }
}

public sealed class QuestionMarkTool : EditorTool
{
    public override string Group => "Regions";
    public override string Label => "Question marks";
    public override string Tip => "Click on the map: the chosen question mark goes there (none chosen: a new one)";
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p) => default;

    public override void Begin(ToolContext c, ToolPoint p)
    {
        var m = c.Doc.Mission;
        if (RegionState.QuestionMark >= 0 && RegionState.QuestionMark < m.QuestionMarks.Count)
            c.Run(MissionEdits.Change<EdQuestionMark>(m, RegionState.QuestionMark, q => { q.X = Math.Round(p.Mx, 2); q.Y = Math.Round(p.My, 2); q.Z = Math.Round(p.Y, 2); }, "Move question mark"), merge: false);
        else
        {
            c.Run(MissionEdits.Add(m, new EdQuestionMark { Guid = EdGuid.New(), Name = MissionDoc.FreeName("questionmark", m.QuestionMarks.Select(q => q.Name)), X = Math.Round(p.Mx, 2), Y = Math.Round(p.My, 2), Z = Math.Round(p.Y, 2) }), merge: false);
            RegionState.QuestionMark = m.QuestionMarks.Count - 1;
        }
        MapEditor.MissionSelectionChanged();
    }

    public override string Status(ToolContext c) => $"question marks: {c.Doc.Mission.QuestionMarks.Count}";
    public override void BuildProps(ToolUi ui, ToolContext? c) { if (c != null && MapEditor.Live is { } ed) ed.BuildQuestionMarkProps(ui, c); else ui.Note("Loading the map …"); }
}

/// <summary>a tool of the group that is only a panel (variables, quests, cameras)</summary>
public sealed class MissionPanelTool : EditorTool
{
    readonly string _label, _tip;
    readonly Action<MapEditor> _build;
    public MissionPanelTool(string label, string tip, Action<MapEditor> build) { _label = label; _tip = tip; _build = build; }
    public override string Group => "Regions";
    public override string Label => _label;
    public override string Tip => _tip;
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p) => default;
    public override string Status(ToolContext c) => _label.ToLowerInvariant();
    public override void BuildProps(ToolUi ui, ToolContext? c) { if (c != null && MapEditor.Live is { } ed) { ed.PanelUi = ui; _build(ed); } else ui.Note("Loading the map …"); }
}

// ==================================================================================================== the screen's part
public partial class MapEditor
{
    RegionLayer? _regionLayer;
    MissionDoc? _missionSub;
    internal ToolUi? PanelUi;
    bool _missionPropsQueued;

    MissionDoc Mis => Doc.Mission;
    void Run(IEditCommand c) => History.Execute(c);
    internal void RunCommand(IEditCommand c) => History.Execute(c);

    /// <summary>the view was (re)built: the mission data drawn on it, its changes followed</summary>
    void MissionViewLoaded()
    {
        if (_missionSub != null) _missionSub.Changed -= OnMissionChanged;
        _missionSub = Doc.Mission;
        _missionSub.Changed += OnMissionChanged;
        _regionLayer = new RegionLayer(_view!, _src!, Doc);
        _view!.AddChild(_regionLayer);
        var m = Doc.Mission;
        if (RegionState.Region >= m.Regions.Count) RegionState.Clear();
        _trigWin = null;
        L.Info($"editor mission data: {m.Regions.Count} regions, {m.Groups.Count} groups, {m.Variables.Count} variables, {m.Quests.Count} quests, {m.Triggers.Triggers.Count()} triggers");
        if (_args.ContainsKey("editor-triggers-demo") && !s_trigDemoDone) { s_trigDemoDone = true; CallDeferred(nameof(TriggersDemo)); }
    }

    /// <summary>every frame: the layer shown with the Regions and Triggers groups</summary>
    void MissionFrame() => _regionLayer?.Update(RegionState.ShowAlways || EditorSession.ToolGroup is "Regions" or "Triggers");

    void OnMissionChanged(MissionPart p)
    {
        _regionLayer?.Invalidate();
        _trigWin?.OnMissionChanged(p);
        // the panel follows (deferred: not while a field is being typed in)
        if (EditorSession.ToolGroup == "Regions" && !_missionPropsQueued) { _missionPropsQueued = true; CallDeferred(nameof(RebuildMissionProps)); }
    }

    void RebuildMissionProps()
    {
        _missionPropsQueued = false;
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit && !_forceRebuild) return;
        _forceRebuild = false;
        BuildProperties();
    }
    bool _forceRebuild;

    /// <summary>the chosen region / group ... changed: drawn and shown anew</summary>
    internal static void MissionSelectionChanged()
    {
        if (Live is not { } ed || !IsInstanceValid(ed)) return;
        ed._regionLayer?.Invalidate();
        ed._forceRebuild = true;
        if (!ed._missionPropsQueued) { ed._missionPropsQueued = true; ed.CallDeferred(nameof(RebuildMissionProps)); }
    }

    /// <summary>the placed object nearest to map (mx, my) within r metres (its radius counts a little)</summary>
    internal static MapObject? NearestObject(IEditorMapDoc d, double mx, double my, double r)
    {
        MapObject? best = null; double bd = r * r;
        foreach (var o in d.Objects)
        {
            double dd = (o.X - mx) * (o.X - mx) + (o.Y - my) * (o.Y - my);
            if (dd < bd) { bd = dd; best = o; }
        }
        return best;
    }

    /// <summary>keys of the mission tools (before the editor's own): Delete removes the chosen shape; Esc closes the
    /// trigger editor or ends a pick; true = taken</summary>
    bool MissionInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return false;
        if (k.Keycode == Key.Escape && _trigWin != null && (_trigWin.Picking || _trigWin.Visible)) { _trigWin.Escape(); return true; }
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit or SpinBox) return false;
        if (k.Keycode == Key.Delete && EditorTools.Current is RegionTool && RegionState.Region >= 0 && RegionState.Region < Mis.Regions.Count)
        {
            DeleteShape();
            return true;
        }
        return false;
    }

    void DeleteShape()
    {
        var r = Mis.Regions[RegionState.Region];
        if (r.Shapes.Count > 1 && RegionState.Shape >= 0) { Run(MissionEdits.RemoveShape(Mis, RegionState.Region, RegionState.Shape)); RegionState.Shape = 0; }
        else { Run(MissionEdits.Remove<EdRegion>(Mis, RegionState.Region)); RegionState.Region = RegionState.Shape = -1; }
        MissionSelectionChanged();
    }

    // ---------------------------------------------------------------------------------------------- the panels
    internal void BuildRegionProps(ToolUi ui, ToolContext c)
    {
        var m = Mis;
        ui.Caption("Regions");
        ui.Choice(new[] { "Select", "Rectangle", "Oval" }, (int)RegionState.DrawMode, i => { RegionState.DrawMode = (RegionState.Mode)i; }, 3,
            new[] { "Click a region to choose it; drag to move it, drag a handle to resize", "Drag to draw a rectangle", "Drag to draw an oval" });
        ui.Check("Add to the chosen region", RegionState.AddToRegion, on => RegionState.AddToRegion = on, "A drawn shape becomes another shape of the chosen region");
        ui.Check("Show the regions always", RegionState.ShowAlways, on => RegionState.ShowAlways = on);
        var box = MissionUi.Scroll(ui, 470);
        MissionUi.List(box, m.Regions.Select(r => $"{r.Name}  ({r.Shapes.Count})"), RegionState.Region, i => { RegionState.Region = i; RegionState.Shape = 0; MissionSelectionChanged(); LookAtRegion(i); }, 170);
        if (RegionState.Region < 0 || RegionState.Region >= m.Regions.Count) { ui.Note($"{m.Regions.Count} regions. Draw one on the map, or click one to choose it."); return; }
        int ri = RegionState.Region;
        var r = m.Regions[ri];
        MissionUi.Field(box, "Name", r.Name, s => { if (s.Length > 0) Run(MissionEdits.Rename<EdRegion>(m, ri, s)); }, "The region's name (the triggers name it by its GUID; text references follow a rename)");
        box.AddChild(PwTheme.Para($"GUID {r.Guid}", 11, PwTheme.Dim));
        for (int k = 0; k < r.Shapes.Count; k++)
        {
            var s = r.Shapes[k]; int kk = k;
            var row = new HBoxContainer();
            var pick = new CheckBox { Text = $"{k + 1}", ButtonPressed = k == RegionState.Shape, FocusMode = Control.FocusModeEnum.None, TooltipText = "Choose this shape (its handles resize it)" };
            pick.Toggled += on => { if (on) { RegionState.Shape = kk; MissionSelectionChanged(); } };
            row.AddChild(pick);
            var form = new OptionButton { FocusMode = Control.FocusModeEnum.None };
            form.AddItem("rectangle"); form.AddItem("oval"); form.Select(s.Oval ? 1 : 0);
            form.ItemSelected += i => Run(MissionEdits.Change<EdRegion>(m, ri, x => x.Shapes[kk].Oval = i == 1, "Shape form"));
            row.AddChild(form);
            var on = new CheckBox { Text = "on", ButtonPressed = s.Enabled, FocusMode = Control.FocusModeEnum.None, TooltipText = "On at the start (off: ActivateRegion switches it on)" };
            on.Toggled += v => Run(MissionEdits.Change<EdRegion>(m, ri, x => x.Shapes[kk].Enabled = v, v ? "Shape on" : "Shape off"));
            row.AddChild(on);
            row.AddChild(new Label { Text = $"{MissionUi.F(s.W)} x {MissionUi.F(s.H)} m", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            var del = PwTheme.Btn("✕", () => Run(MissionEdits.RemoveShape(m, ri, kk)), 30); del.TooltipText = "Delete this shape"; del.Disabled = r.Shapes.Count < 2;
            row.AddChild(del);
            box.AddChild(row);
        }
        if (RegionState.Shape >= 0 && RegionState.Shape < r.Shapes.Count)
        {
            var s = r.Shapes[RegionState.Shape]; int si = RegionState.Shape;
            void Box(string what, Func<EdShape, double> get, Action<EdShape, double> set)
                => MissionUi.Field(box, what, MissionUi.F(get(s)), t => { if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Run(MissionEdits.Change<EdRegion>(m, ri, x => set(x.Shapes[si], v), "Region box")); }, "", 50);
            Box("x", q => q.X, (q, v) => q.X = v); Box("y", q => q.Y, (q, v) => q.Y = v);
            Box("width", q => q.W, (q, v) => q.W = Math.Max(1, v)); Box("height", q => q.H, (q, v) => q.H = Math.Max(1, v));
        }
        MissionUi.Buttons(box, ("Delete region", () => { Run(MissionEdits.Remove<EdRegion>(m, ri)); RegionState.Region = RegionState.Shape = -1; MissionSelectionChanged(); }, "Delete the region (the triggers that name it are listed by the trigger editor's check)"),
            ("Look at it", () => LookAtRegion(ri), "Move the camera to it"));
    }

    void LookAtRegion(int i)
    {
        if (_cam == null || _src == null || i < 0 || i >= Mis.Regions.Count || Mis.Regions[i].Box() is not { } b) return;
        var (x, z) = _src.ToGame((b[0] + b[2]) / 2, (b[1] + b[3]) / 2);
        _cam.X = x; _cam.Z = z;
    }

    internal void BuildGroupProps(ToolUi ui, ToolContext c)
    {
        var m = Mis;
        ui.Caption("Groups");
        ui.Note("Click an object on the map: into the chosen group, or out of it.");
        var box = MissionUi.Scroll(ui, 480);
        MissionUi.List(box, m.Groups.Select(g => $"{g.Name}  ({g.Members.Count})"), RegionState.Group, i => { RegionState.Group = i; MissionSelectionChanged(); }, 160);
        MissionUi.Buttons(box, ("New group", () =>
        {
            Run(MissionEdits.Add(m, new EdGroup { Guid = EdGuid.New(), Name = MissionDoc.FreeName("Group", m.Groups.Select(g => g.Name)) }));
            RegionState.Group = m.Groups.Count - 1; MissionSelectionChanged();
        }, "A new empty group"));
        if (RegionState.Group < 0 || RegionState.Group >= m.Groups.Count) return;
        int gi = RegionState.Group;
        var g = m.Groups[gi];
        MissionUi.Field(box, "Name", g.Name, s => { if (s.Length > 0) Run(MissionEdits.Rename<EdGroup>(m, gi, s)); });
        var objs = m.Objects().ToDictionary(o => o.Guid, o => o, StringComparer.Ordinal);
        box.AddChild(PwTheme.Para(g.Members.Count == 0 ? "No objects yet." : string.Join(", ", g.Members.Take(40).Select(x => objs.TryGetValue(x, out var o) ? o.Name : x + " (not on the map)")) + (g.Members.Count > 40 ? " …" : ""), 12, PwTheme.Text));
        MissionUi.Buttons(box,
            ("Add the selection", () =>
            {
                var cs = new List<IEditCommand>(); var add = new List<string>();
                foreach (var o in ObjectToolState.Selection) { var (gc, guid) = MissionEdits.EnsureGuid(o); if (gc != null) cs.Add(gc); if (!g.Members.Contains(guid)) add.Add(guid); }
                cs.Add(MissionEdits.Change<EdGroup>(m, gi, x => x.Members.AddRange(add), "Add to the group"));
                Run(new CompositeEdit("Add to the group", cs));
            }, "The objects chosen with the object tool"),
            ("Clear", () => Run(MissionEdits.Change<EdGroup>(m, gi, x => x.Members.Clear(), "Empty the group")), "Take every object out"),
            ("Delete", () => { Run(MissionEdits.Remove<EdGroup>(m, gi)); RegionState.Group = -1; MissionSelectionChanged(); }, "Delete the group"));
    }

    internal void BuildVariablesProps()
    {
        var ui = PanelUi!; var m = Mis;
        ui.Caption("Variables");
        ui.Note("The level variables the triggers test (Var) and change (SetVar), with their value at the start. A rename renames them in the triggers.");
        var box = MissionUi.Scroll(ui, 480);
        for (int i = 0; i < m.Variables.Count; i++)
        {
            int ii = i; var v = m.Variables[i];
            var row = new HBoxContainer();
            box.AddChild(row);
            MissionUi.Field(row, "", v.Name, s => { if (s.Length > 0 && s != v.Name) Run(MissionEdits.Rename<EdVariable>(m, ii, s)); }, "name");
            MissionUi.Field(row, "=", v.Value, s => Run(MissionEdits.Change<EdVariable>(m, ii, x => x.Value = s, "Variable value")), "the value at the start", 14);
            var del = PwTheme.Btn("✕", () => Run(MissionEdits.Remove<EdVariable>(m, ii)), 30); del.TooltipText = "Delete"; row.AddChild(del);
        }
        MissionUi.Buttons(box, ("New variable", () => Run(MissionEdits.Add(m, new EdVariable { Name = MissionDoc.FreeName("var", m.Variables.Select(x => x.Name)), Value = "0" })), ""));
    }

    internal void BuildQuestsProps()
    {
        var ui = PanelUi!; var m = Mis;
        ui.Caption("Quests");
        var box = MissionUi.Scroll(ui, 520);
        MissionUi.List(box, m.Quests.Select(q => $"{q.Name}  {(q.Main ? "main" : "optional")}{(q.Visible ? "" : ", hidden")}"), RegionState.Quest, i => { RegionState.Quest = i; MissionSelectionChanged(); }, 130);
        MissionUi.Buttons(box, ("New quest", () => { Run(MissionEdits.NewQuest(m)); RegionState.Quest = m.Quests.Count - 1; MissionSelectionChanged(); }, "A new quest, hidden at the start (a Quest action shows it)"));
        if (RegionState.Quest < 0 || RegionState.Quest >= m.Quests.Count) return;
        int qi = RegionState.Quest; var q = m.Quests[qi];
        MissionUi.Field(box, "Name", q.Name, s => { if (s.Length > 0) Run(MissionEdits.Rename<EdQuest>(m, qi, s)); }, "the id the triggers name (quest_name)");
        MissionUi.Field(box, "Headline", q.Headline, s => Run(MissionEdits.SetQuestText(m, qi, "headline", s)), $"text {q.KeyHeadline}");
        box.AddChild(new Label { Text = "Description" });
        var desc = new TextEdit { Text = q.Description, CustomMinimumSize = new Vector2(240, 90), WrapMode = TextEdit.LineWrappingMode.Boundary };
        desc.FocusExited += () => { if (desc.Text != m.Quests[qi].Description) Run(MissionEdits.SetQuestText(m, qi, "description", desc.Text)); };
        box.AddChild(desc);
        MissionUi.Field(box, "Group title", q.GroupTitle, s => Run(MissionEdits.SetQuestText(m, qi, "group", s)), $"text {q.KeyGroup}");
        void Flag(string text, bool v, Action<EdQuest, bool> set, string tip)
        {
            var cb = new CheckBox { Text = text, ButtonPressed = v, FocusMode = Control.FocusModeEnum.None, TooltipText = tip };
            cb.Toggled += on => Run(MissionEdits.Change<EdQuest>(m, qi, x => set(x, on), "Quest " + text));
            box.AddChild(cb);
        }
        Flag("Main quest", q.Main, (x, v) => x.Main = v, "Main quest (else optional)");
        Flag("Hidden at the start", !q.Visible, (x, v) => x.Visible = !v, "Hidden until a Quest action shows it");
        Flag("Accomplished at the start", q.Accomplished, (x, v) => x.Accomplished = v, "");
        Flag("Failed at the start", q.Unaccomplishable, (x, v) => x.Unaccomplishable = v, "");
        var row = new HBoxContainer(); box.AddChild(row);
        row.AddChild(new Label { Text = "Bonus" });
        string[] names = { "easy", "medium", "hard" };
        for (int k = 0; k < 3; k++)
        {
            int kk = k;
            MissionUi.Field(row, "", MissionUi.F(q.Bonus[k]), s => { if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Run(MissionEdits.Change<EdQuest>(m, qi, x => x.Bonus[kk] = v, "Quest bonus")); }, "bonus points on " + names[k]);
        }
        MissionUi.Buttons(box, ("Delete quest", () => { Run(MissionEdits.Remove<EdQuest>(m, qi)); RegionState.Quest = -1; MissionSelectionChanged(); }, ""));
    }

    internal void BuildQuestionMarkProps(ToolUi ui, ToolContext c)
    {
        var m = Mis;
        ui.Caption("Question marks");
        ui.Note("Click on the map: the chosen one goes there (none chosen: a new one). QuestionMark actions change them.");
        var box = MissionUi.Scroll(ui, 420);
        MissionUi.List(box, m.QuestionMarks.Select(q => $"{q.Name}  {q.State}"), RegionState.QuestionMark, i => { RegionState.QuestionMark = i; MissionSelectionChanged(); }, 140);
        MissionUi.Buttons(box, ("None chosen", () => { RegionState.QuestionMark = -1; MissionSelectionChanged(); }, "The next click makes a new one"));
        if (RegionState.QuestionMark < 0 || RegionState.QuestionMark >= m.QuestionMarks.Count) return;
        int qi = RegionState.QuestionMark; var q = m.QuestionMarks[qi];
        MissionUi.Field(box, "Name", q.Name, s => { if (s.Length > 0) Run(MissionEdits.Rename<EdQuestionMark>(m, qi, s)); });
        var states = new[] { "STATE_INVISIBLE", "QM_STATE_YELLOW", "QM_STATE_GREEN", "QM_STATE_RED", "EC_STATE_YELLOW" };
        var o = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        foreach (var s in states) o.AddItem(s);
        o.Select(Math.Max(0, Array.IndexOf(states, q.State)));
        o.ItemSelected += i => Run(MissionEdits.Change<EdQuestionMark>(m, qi, x => x.State = states[i], "Question mark state"));
        box.AddChild(o);
        box.AddChild(PwTheme.Para($"at {MissionUi.F(q.X)}, {MissionUi.F(q.Y)}", 12, PwTheme.Dim));
        MissionUi.Buttons(box, ("Delete", () => { Run(MissionEdits.Remove<EdQuestionMark>(m, qi)); RegionState.QuestionMark = -1; MissionSelectionChanged(); }, ""));
    }

    internal void BuildCamerasProps()
    {
        var ui = PanelUi!; var m = Mis;
        ui.Caption("Cameras");
        ui.Note("Camera points from the editor's view (for the triggers' positions and the scripts: world.lua cameras), and the start camera.");
        var box = MissionUi.Scroll(ui, 460);
        MissionUi.List(box, m.Cameras.Select(c => c.Name), RegionState.Camera, i => { RegionState.Camera = i; MissionSelectionChanged(); }, 120);
        (double X, double Y, double Angle) View()
        {
            var (mx, my) = _src!.ToMap(_cam!.X, _cam.Z);
            return (Math.Round(mx, 1), Math.Round(my, 1), Math.Round(Math.PI / 2 - _cam.Yaw, 4));
        }
        MissionUi.Buttons(box, ("Add this view", () =>
        {
            var (x, y, a) = View();
            Run(MissionEdits.Add(m, new EdCamera { Name = MissionDoc.FreeName("camera", m.Cameras.Select(c => c.Name)), X = x, Y = y, Angle = a, Pitch = Math.Round(_cam!.Pitch, 3), Dist = Math.Round(_cam.TDist, 1) }));
            RegionState.Camera = m.Cameras.Count - 1; MissionSelectionChanged();
        }, "A camera point where the editor's camera looks now"));
        if (RegionState.Camera >= 0 && RegionState.Camera < m.Cameras.Count)
        {
            int ci = RegionState.Camera; var cp = m.Cameras[ci];
            MissionUi.Field(box, "Name", cp.Name, s => { if (s.Length > 0) Run(MissionEdits.Rename<EdCamera>(m, ci, s)); });
            MissionUi.Buttons(box,
                ("Look from it", () => { var (x, z) = _src!.ToGame(cp.X, cp.Y); _cam!.X = x; _cam.Z = z; _cam.Yaw = Math.PI / 2 - cp.Angle; _cam.Pitch = cp.Pitch; _cam.TDist = cp.Dist; }, ""),
                ("Delete", () => { Run(MissionEdits.Remove<EdCamera>(m, ci)); RegionState.Camera = -1; MissionSelectionChanged(); }, ""));
        }
        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "Start camera" });
        box.AddChild(PwTheme.Para(m.StartX is double sx ? $"at {MissionUi.F(sx)}, {MissionUi.F(m.StartY ?? 0)}, angle {m.StartAngle:0.###}" : $"the human's start location, angle {m.StartAngle:0.###}", 12, PwTheme.Text));
        MissionUi.Buttons(box,
            ("From this view", () => { var (x, y, a) = View(); Run(MissionEdits.SetStartCamera(m, a, x, y)); }, "The mission's camera starts here, turned as the editor's"),
            ("Angle only", () => { var (_, _, a) = View(); Run(MissionEdits.SetStartCamera(m, a, null, null)); }, "The camera starts at the human's start location, turned as the editor's now"));
    }
}
