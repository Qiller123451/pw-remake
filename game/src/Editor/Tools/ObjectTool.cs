// The map editor's object tool (docs/MAP_EDITOR.md "Objects", plan phase 3) - the "Objects" palette group and the
// start locations of the "Players" group:
//   place     a class chosen in the palette follows the mouse as a ghost (the model, a green ring); the wheel or Q / E
//             turn it (15 degrees, Shift 45), a click puts it down - Shift + click puts down more of it -, Esc or a
//             right click ends; options: owner and level of new objects, random heading, snap to a grid
//   select    click (Ctrl: add / remove), drag a box (Ctrl: add), Ctrl+A all; Esc clears
//   edit      drag the selection over the ground (it sits on the terrain; decoration keeps its height above it), Q / E
//             turn it, Delete, Ctrl+C / Ctrl+V (at the mouse) / Ctrl+D (duplicate); the properties panel: name for the
//             triggers, owner, level, hit points %, amount, a nest's species, heading
// Every edit is a command of the history (ObjectsEdit, ObjectsAddRemove: undo / redo). The objects are drawn by the
// ObjectLayer; the palette and the panels are in ObjectPanels.cs, the players in PlayerPanels.cs. Placing buildings
// does not touch the map's heights: Test map levels the ground under them as the game does (on its snapshot).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Maps;

namespace PwRemake.Editor;

/// <summary>what the object tool keeps while the editor screen is away (Test map): the palette, the class being placed,
/// the options, the selection, the clipboard</summary>
public static class ObjectToolState
{
    public static List<PaletteEntry>? Palette;
    static string? s_cfg;
    /// <summary>the class being placed (null: select / edit)</summary>
    public static PaletteEntry? Placing;
    public static string Category = ObjectPalette.ALL, Query = "";
    /// <summary>new objects: the player slot (null: nobody's) and the level (1-based)</summary>
    public static int? NewOwner = 0;
    public static int NewLevel = 1;
    public static bool RandomRot;
    /// <summary>snap to a grid of this many metres (0: off)</summary>
    public static double Snap;
    /// <summary>the heading of the ghost (map rot)</summary>
    public static double GhostRot;
    public static readonly ObjectClipboard Clipboard = new();
    public static readonly List<MapObject> Selection = new();
    /// <summary>the player slot shown in the Players panel</summary>
    public static int Slot;
    static uint s_rnd = 0x9e3779b9;
    /// <summary>a random heading (an LCG of its own: the editor needs no determinism, the simulation's generators stay untouched)</summary>
    public static double RandomHeading() { s_rnd = s_rnd * 1664525 + 1013904223; return (s_rnd >> 8) / (double)(1 << 24) * 2 * Math.PI - Math.PI; }

    /// <summary>the palette of the configuration (built once): its classes; a mod's are marked against the base game's</summary>
    public static List<PaletteEntry> PaletteOf()
    {
        var cfg = EditorSession.Config;
        if (Palette != null && s_cfg == cfg.Id) return Palette;
        s_cfg = cfg.Id;
        HashSet<string>? baseNames = null;
        if (cfg.IsMod && GameApp.Configurations.FirstOrDefault(c => !c.IsMod) is { } bc)
        {
            var n = ObjectPalette.ClassNamesOf(bc.Data);
            if (n.Count > 0) baseNames = n;
        }
        var lib = EditorSession.Models;
        Palette = ObjectPalette.Build(EditorSession.Rules, m => lib.Has(m), baseNames);
        return Palette;
    }

    public static PaletteEntry? EntryOf(string cls) => PaletteOf().FirstOrDefault(e => string.Equals(e.Cls, cls, StringComparison.OrdinalIgnoreCase));
}

/// <summary>the object and player tools on the editor's tool framework (EditorTools.All): their settings are the
/// editor's panels (ObjectPanels.cs, PlayerPanels.cs); their mouse and keys go through MapEditor.ObjectsInput (clicks,
/// drags, boxes - not a held stroke)</summary>
public static class ObjectToolSet
{
    public static IEnumerable<EditorTool> Create() => new EditorTool[] { new ObjectEditTool(), new PlayersEditTool() };
}

public sealed class ObjectEditTool : EditorTool
{
    public override string Group => "Objects";
    public override string Label => "Place / select";
    public override string Tip => "A class of the palette: click to place it (Shift: more). Click, Ctrl + click or a box to select; drag to move; Q / E turn; Delete; Ctrl+C / V / D";
    public override void BuildProps(ToolUi ui, ToolContext? c) { if (c != null && MapEditor.Live is { } ed && GodotObject.IsInstanceValid(ed)) ed.BuildObjectProps(); else ui.Note("Loading the map …"); }
    public override void Reset() => ObjectToolState.Placing = null;
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p) => default;
    public override string Status(ToolContext c) => ObjectToolState.Placing is { } e ? $"placing {e.Name} (heading {ObjectToolState.GhostRot * 180 / Math.PI:0}°)" : $"objects: {ObjectToolState.Selection.Count} selected of {c.Doc.Objects.Count}";
}

public sealed class PlayersEditTool : EditorTool
{
    public override string Group => "Players";
    public override string Label => "Players and map settings";
    public override string Tip => "The player slots (tribes, teams, colours, start), their start locations (drag them on the map), the map's settings";
    public override void BuildProps(ToolUi ui, ToolContext? c) { if (c != null && MapEditor.Live is { } ed && GodotObject.IsInstanceValid(ed)) ed.BuildPlayersProps(); else ui.Note("Loading the map …"); }
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p) => default;
    public override string Status(ToolContext c) => $"players: {c.Doc.MaxPlayers} start locations · slot {ObjectToolState.Slot + 1}";
}

public partial class MapEditor
{
    /// <summary>the editor screen on show (the object and player tools build their panels with it)</summary>
    internal static MapEditor? Live;
    ObjectLayer? _objLayer;
    MapObject? _hoverObj, _ghostObj;
    bool _objPress, _objDrag, _boxSel;
    Vector2 _pressAt;
    (double X, double Y) _dragFrom;
    Dictionary<MapObject, (double X, double Y)> _dragOrig = new();
    Panel? _selBox;
    bool _objPropsStale;

    static List<MapObject> Sel => ObjectToolState.Selection;
    bool ObjectTools => EditorSession.ToolGroup is "Objects" or "Players";
    /// <summary>the Players group edits only the start locations</summary>
    bool Pickable(MapObject o) => EditorSession.ToolGroup != "Players" || o.Type == "SLOC";

    // ================================================================================ hooks of the editor screen

    /// <summary>the view was (re)built: the objects drawn on it</summary>
    void ObjectsViewLoaded()
    {
        Live = this;
        // (command line: --editor-objects-demo puts a few objects down around the camera and selects one - screenshots;
        // with --editor-tool "Place / select" the Objects group is open)
        if (!s_argsDone)
        {
            s_argsDone = true;
            if (_args.ContainsKey("editor-objects-demo")) CallDeferred(nameof(ObjectsDemo));
        }
        var rules = EditorSession.Rules;
        var lib = EditorSession.Models;
        _objLayer = new ObjectLayer(_view!, _src!, (cls, level) => rules.Exists(cls) ? rules.StatsAt(cls, Math.Max(1, level), null)?.Gfx is { Length: > 0 } g && lib.Has(g.ToLowerInvariant()) ? g : rules.ClassGfx(cls) : rules.ClassGfx(cls.ToLowerInvariant()) ?? cls.ToLowerInvariant())
        {
            SlotColor = SlotColorOf,
        };
        _view!.AddChild(_objLayer);
        var live = Doc.Objects.ToHashSet();
        Sel.RemoveAll(o => !live.Contains(o));
        _objLayer.Sync(Doc.Objects);
        UpdateMinimapStarts();
        L.Info($"editor objects: {_objLayer.Count} drawn");
        if (ObjectTools) { BuildPalette(); BuildProperties(); }
    }

    Color SlotColorOf(int slot)
    {
        var p = _playersCache ??= Doc.GetPlayers();
        var id = slot >= 0 && slot < p.Slots.Count ? p.Slots[slot].Color : null;
        return (PwRemake.View.Materials.PartyColors.ById(id) ?? PwRemake.View.Materials.PartyColors.All[Math.Abs(slot) % PwRemake.View.Materials.PartyColors.All.Length]).Light;
    }
    MapPlayerSettings? _playersCache;

    /// <summary>the document's objects changed (an edit, undo, redo)</summary>
    void ObjectsDocChanged()
    {
        if (_objLayer == null) return;
        _objLayer.Sync(Doc.Objects);
        var live = Doc.Objects.ToHashSet();
        Sel.RemoveAll(o => !live.Contains(o));
        UpdateMinimapStarts();
        if (ObjectTools) { if (_objDrag) _objPropsStale = true; else BuildProperties(); }
        RefreshUi();
    }

    /// <summary>the map's info changed (players, settings): the players' colours, the panels</summary>
    void ObjectsInfoChanged()
    {
        var before = _playersCache;
        _playersCache = Doc.GetPlayers();
        if (_objLayer != null && (before == null || !before.Slots.Select(s => s.Color).SequenceEqual(_playersCache.Slots.Select(s => s.Color)))) _objLayer.Rebuild(Doc.Objects);
        if (EditorSession.ToolGroup == "Players" && !_playersUiBusy) { BuildPalette(); BuildProperties(); }
    }

    /// <summary>height samples changed (a terrain tool, undo): the objects there re-seated by the document (Z) drawn again</summary>
    void ObjectsHeightsChanged(GridRect r)
    {
        if (_objLayer == null) return;
        var d = Doc;
        _objLayer.PlaceIn(d.Objects, d.OriginX + (r.I0 - 1) * d.Cell, d.OriginY + (r.J0 - 1) * d.Cell, d.OriginX + (r.I1 + 1) * d.Cell, d.OriginY + (r.J1 + 1) * d.Cell);
    }

    void UpdateMinimapStarts()
    {
        if (_src == null) return;
        _minimap.Starts = Doc.Objects.Where(o => o.Type == "SLOC").Select(o => _src.ToGame(o.X, o.Y)).Select(p => (p.x, p.z)).ToList();
        _minimap.Invalidate();
    }

    /// <summary>input before the camera sees it (the camera takes the wheel and Q / E): turning the ghost or the selection;
    /// true = taken</summary>
    bool ObjectsPreInput(InputEvent e)
    {
        if (!ObjectTools || _view == null || _dialogs.GetChildCount() > 0) return false;
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wb && ObjectToolState.Placing != null && !wb.CtrlPressed && !_overUi)
        {
            ObjectToolState.GhostRot = ObjectOps.Norm(ObjectToolState.GhostRot + (wb.ButtonIndex == MouseButton.WheelUp ? 1 : -1) * Math.PI / 12);
            return true;
        }
        if (e is InputEventKey { Pressed: true, Keycode: Key.Q or Key.E } k && !k.CtrlPressed && !Typing())
        {
            double step = (k.ShiftPressed ? Math.PI / 4 : Math.PI / 12) * (k.Keycode == Key.Q ? 1 : -1);
            if (ObjectToolState.Placing != null) { ObjectToolState.GhostRot = ObjectOps.Norm(ObjectToolState.GhostRot + step); return true; }
            var turn = Sel.Where(Pickable).ToList();
            if (turn.Count > 0)
            {
                if (ObjectsEdit.Make("Turn objects", turn, o => ObjectOps.Turn(o, step)) is { } ed) History.Execute(ed);
                return true;
            }
        }
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && ObjectToolState.Placing != null && !_overUi)
        {
            ObjectToolState.Placing = null; BuildPalette(); BuildProperties();
            return false;          // (the camera may still turn)
        }
        return false;
    }

    bool Typing() => GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit or SpinBox;

    /// <summary>the object tool's clicks and keys (from _UnhandledInput); true = taken</summary>
    bool ObjectsInput(InputEvent e)
    {
        if (!ObjectTools || _view == null) return false;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb && !mb.AltPressed)
        {
            if (mb.Pressed && OverView(mb.Position))
            {
                if (ObjectToolState.Placing != null) { PlaceAtCursor(mb.ShiftPressed); return true; }
                _objPress = true; _pressAt = mb.Position; _objDrag = false; _boxSel = false;
                var hit = PickAt(mb.Position);
                if (hit != null)
                {
                    if (mb.CtrlPressed) { if (!Sel.Remove(hit)) Sel.Add(hit); SelectionChanged(); _objPress = false; return true; }
                    if (!Sel.Contains(hit)) { Sel.Clear(); Sel.Add(hit); SelectionChanged(); }
                    if (_hover is Vector3 g) { var (mx, my) = _src!.ToMap(g.X, g.Z); _dragFrom = (mx, my); }
                    _dragOrig = Sel.ToDictionary(o => o, o => (o.X, o.Y));
                }
                else { _boxSel = true; _boxCtrl = mb.CtrlPressed; }
                return true;
            }
            if (!mb.Pressed && _objPress) { EndObjectPress(mb.Position); return true; }
            return false;
        }
        if (e is not InputEventKey { Pressed: true } k || Typing()) return false;
        switch (k.Keycode)
        {
            case Key.Delete or Key.Backspace when Sel.Count > 0: DeleteSelection(); return true;
            case Key.Escape when ObjectToolState.Placing != null: ObjectToolState.Placing = null; BuildPalette(); BuildProperties(); return true;
            case Key.Escape when Sel.Count > 0: Sel.Clear(); SelectionChanged(); return true;
            case Key.C when k.CtrlPressed && Sel.Count > 0: CopySelection(); return true;
            case Key.V when k.CtrlPressed && !k.Echo: PasteAtCursor(); return true;
            case Key.D when k.CtrlPressed && !k.Echo && Sel.Count > 0: DuplicateSelection(); return true;
            case Key.A when k.CtrlPressed && !k.Echo: Sel.Clear(); Sel.AddRange(Doc.Objects.Where(Pickable)); SelectionChanged(); return true;
        }
        return false;
    }
    bool _boxCtrl;

    MapObject? PickAt(Vector2 screen)
    {
        if (_objLayer == null) return null;
        Vector3 ro = _r.Camera.ProjectRayOrigin(screen), rd = _r.Camera.ProjectRayNormal(screen);
        var hit = _objLayer.Pick(ro, rd);
        return hit != null && Pickable(hit) ? hit : null;
    }

    void EndObjectPress(Vector2 at)
    {
        _objPress = false;
        if (_objDrag)
        {
            _objDrag = false;
            History.EndMerge();
            if (_objPropsStale) { _objPropsStale = false; BuildProperties(); }
        }
        else if (_boxSel)
        {
            _boxSel = false;
            if (_selBox != null) _selBox.Visible = false;
            var r = new Rect2(_pressAt, at - _pressAt).Abs();
            if (!_boxCtrl) Sel.Clear();
            if (r.Size.X > 4 || r.Size.Y > 4)
                foreach (var o in _objLayer!.InRect(_r.Camera, r).Where(Pickable)) if (!Sel.Contains(o)) Sel.Add(o);
            SelectionChanged();
        }
    }

    /// <summary>every frame: the ghost under the mouse, the drag of the selection, the box, the rings</summary>
    void ObjectsProcess(double dt)
    {
        if (_objLayer == null) return;
        _objLayer.Advance(dt);
        bool tools = ObjectTools;
        var mouse = GetViewport().GetMousePosition();
        // the ghost
        var placing = tools ? ObjectToolState.Placing : null;
        Vector3? ghostAt = null;
        if (placing != null && _hover is Vector3 h)
        {
            var (mx, my) = _src!.ToMap(h.X, h.Z);
            (mx, my) = ObjectOps.Snap(mx, my, ObjectToolState.Snap);
            _ghostObj = ObjectOps.Create(placing, Doc, mx, my, ObjectToolState.GhostRot, ObjectToolState.NewOwner, ObjectToolState.NewLevel, EditorSession.Rules);
            if (placing.Type == "SLOC" && EditorSession.ToolGroup == "Players") _ghostObj.Owner = ObjectToolState.Slot;
            _objLayer.Ghost(_ghostObj);
            var (gx, gz) = _src.ToGame(mx, my);
            ghostAt = new Vector3((float)gx, h.Y, (float)gz);
        }
        else { _objLayer.Ghost(null); _ghostObj = null; }
        // a press on a selected object: dragging it once the mouse has moved a little
        if (_objPress && !Input.IsMouseButtonPressed(MouseButton.Left)) EndObjectPress(mouse);
        if (_objPress && !_boxSel && _dragOrig.Count > 0 && !_objDrag && (mouse - _pressAt).Length() > 4) _objDrag = true;
        if (_objDrag && _hover is Vector3 hv && !_camIn.Dragging)
        {
            var (mx, my) = _src!.ToMap(hv.X, hv.Z);
            double dx = mx - _dragFrom.X, dy = my - _dragFrom.Y;
            // (snap: the grabbed object's - the first of the selection - place on the grid, the others keep their offsets)
            if (ObjectToolState.Snap > 0 && _dragOrig.Count > 0)
            {
                var first = _dragOrig.First().Value;
                var (sx, sy) = ObjectOps.Snap(first.X + dx, first.Y + dy, ObjectToolState.Snap);
                dx = sx - first.X; dy = sy - first.Y;
            }
            var orig = _dragOrig;
            if (ObjectsEdit.Make("Move objects", orig.Keys, o => ObjectOps.MoveTo(o, Doc, orig[o].X + dx, orig[o].Y + dy), mergeable: true) is { } ed) History.Execute(ed, merge: true);
        }
        // the box
        if (_boxSel)
        {
            _selBox ??= MakeSelBox();
            var r = new Rect2(_pressAt, mouse - _pressAt).Abs();
            _selBox.Visible = r.Size.X > 4 || r.Size.Y > 4;
            _selBox.Position = r.Position; _selBox.Size = r.Size;
        }
        _hoverObj = tools && placing == null && !_objDrag && OverView(mouse) ? PickAt(mouse) : null;
        _objLayer.DrawRings(tools ? Sel : Enumerable.Empty<MapObject>(), _hoverObj, ghostAt);
    }

    Panel MakeSelBox()
    {
        var p = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        p.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(1f, 0.85f, 0.4f, 0.12f), BorderColor = new Color(1f, 0.85f, 0.4f, 0.9f), BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1 });
        _dialogs.GetParent().AddChild(p);
        return p;
    }

    void SelectionChanged() => BuildProperties();

    // ================================================================================ the edits

    /// <summary>puts the class being placed down at the ghost; more: keep placing (Shift)</summary>
    void PlaceAtCursor(bool more)
    {
        if (_ghostObj == null || ObjectToolState.Placing == null) return;
        var o = _ghostObj;
        _ghostObj = null;
        if (o.Type == "SLOC" && Doc.Objects.Any(x => x.Type == "SLOC" && x.Owner == o.Owner))
        {
            SetStatusNote($"Player {o.Owner + 1} has a start location: drag it, or delete it first");
            return;
        }
        History.Execute(ObjectsAddRemove.Add(Doc, new[] { o }, $"Place {ObjectToolState.Placing.Name}"));
        if (ObjectToolState.RandomRot) ObjectToolState.GhostRot = ObjectToolState.RandomHeading();
        if (!more)
        {
            ObjectToolState.Placing = null;
            Sel.Clear(); Sel.Add(o);
            BuildPalette();
            BuildProperties();
        }
    }

    void DeleteSelection()
    {
        var del = Sel.Where(Pickable).ToList();
        if (ObjectsAddRemove.Remove(Doc, del, del.Count == 1 ? $"Delete {del[0].Cls}" : $"Delete {del.Count} objects") is { } c) History.Execute(c);
        Sel.Clear();
        BuildProperties();
    }

    void CopySelection()
    {
        ObjectToolState.Clipboard.Copy(Sel);
        SetStatusNote($"{Sel.Count} objects copied");
    }

    void PasteAtCursor()
    {
        if (ObjectToolState.Clipboard.Count == 0) { SetStatusNote("Nothing to paste"); return; }
        double mx, my;
        if (_hover is Vector3 h) (mx, my) = _src!.ToMap(h.X, h.Z);
        else (mx, my) = _src!.ToMap(_cam!.Target.X, _cam.Target.Z);
        var made = ObjectToolState.Clipboard.PasteAt(Doc, mx, my);
        History.Execute(ObjectsAddRemove.Add(Doc, made, $"Paste {made.Count} objects"));
        Sel.Clear(); Sel.AddRange(made);
        BuildProperties();
    }

    void DuplicateSelection()
    {
        var src = Sel.ToList();
        var clip = new ObjectClipboard();
        clip.Copy(src);
        var (cx, cy) = clip.Centre;
        var made = clip.PasteAt(Doc, cx + 4, cy + 4);
        History.Execute(ObjectsAddRemove.Add(Doc, made, $"Duplicate {made.Count} objects"));
        Sel.Clear(); Sel.AddRange(made);
        BuildProperties();
    }

    /// <summary>changes the selected objects (one undo step)</summary>
    void EditSelection(string name, Action<MapObject> f)
    {
        if (ObjectsEdit.Make(name, Sel.ToList(), f) is { } e) History.Execute(e);
    }

    /// <summary>starts placing a class (the palette's click)</summary>
    void StartPlacing(PaletteEntry e)
    {
        ObjectToolState.Placing = e;
        if (ObjectToolState.RandomRot) ObjectToolState.GhostRot = ObjectToolState.RandomHeading();
        Sel.Clear();
        BuildProperties();
    }

    // ================================================================================ the self-test (--editor-selftest)

    /// <summary>the object tools through the editor's own path: place (the ghost's way), drag, turn, owner, delete, undo,
    /// copy / paste, the players; "EDITOR objects selftest ok"</summary>
    void ObjectsSelfTest()
    {
        int bad = 0;
        void T(bool ok, string what) { L.Info($"EDITOR {(ok ? "ok  " : "FAIL")} objects: {what}"); if (!ok) bad++; }
        var pal = ObjectToolState.PaletteOf();
        T(pal.Count > 100 && ObjectPalette.CATEGORIES.All(c => pal.Any(e => e.Category == c)), $"the palette: {pal.Count} classes in every category");
        string group0 = EditorSession.ToolGroup;
        EditorSession.ToolGroup = "Objects";
        int n0 = Doc.Objects.Count, undo0 = History.UndoCount;
        // (beside the camera's target: the first start location's marker stands on it)
        var at = _cam!.Target + new Vector3(24, 0, 10);
        at.Y = (float)_view!.Ground.At(at.X, at.Z);
        var bld = pal.FirstOrDefault(e => e.Category == "Buildings" && e.Model != null && e.Group == (EditorSession.Config.PlayableTribes.FirstOrDefault() ?? "Hu"));
        T(bld != null, $"a building of the palette: {bld?.Cls}");
        if (bld == null) { EditorSession.ToolGroup = group0; L.Info("EDITOR objects selftest failed"); return; }
        // place: the ghost under the mouse, a click
        StartPlacing(bld);
        _hover = at;
        ObjectsProcess(0.016);
        T(_ghostObj != null, "the ghost follows the mouse");
        PlaceAtCursor(false);
        var o = Doc.Objects.LastOrDefault();
        T(Doc.Objects.Count == n0 + 1 && o?.Cls == bld.Cls && o.Owner == ObjectToolState.NewOwner, "placed for the owner of new objects");
        T(o != null && _objLayer!.Has(o) && _objLayer.NodeOf(o) is Node3D nd && Math.Abs(nd.Position.X - at.X) < 0.5, "drawn by the game's model where it was placed");
        T(Sel.Count == 1 && Sel[0] == o && ObjectToolState.Placing == null, "selected after placing");
        // pick it with a ray from the camera
        var scr = _r.Camera.UnprojectPosition(_objLayer!.NodeOf(o!)!.Position + new Vector3(0, 1, 0));
        T(PickAt(scr) == o, "picked under the mouse");
        // drag 10 m east: many steps, one undo step
        _dragFrom = _src!.ToMap(at.X, at.Z); _dragOrig = Sel.ToDictionary(x => x, x => (x.X, x.Y));
        _objPress = false; _objDrag = true;             // (the drag without a held button: headless)
        for (int k = 1; k <= 10; k++) { _hover = at + new Vector3(k, 0, 0); ObjectsProcess(0.016); }
        _objDrag = false; History.EndMerge();
        var (ox, _) = _src.ToMap(at.X, at.Z);
        T(Math.Abs(o!.X - (ox + 10)) < 0.01 && History.UndoCount == undo0 + 2, $"dragged 10 m in one undo step ({o.X - ox:0.00} m)");
        T(Math.Abs(_objLayer.NodeOf(o)!.Position.X - (at.X + 10)) < 0.05, "the model follows");
        EditSelection("Owner", x => x.Owner = 1);
        T(o.Owner == 1, "owner 2");
        double rot0 = o.Rot;
        ObjectsPreInput(new InputEventKey { Keycode = Key.Q, Pressed = true });
        T(Math.Abs(ObjectOps.Norm(o.Rot - rot0 - Math.PI / 12)) < 1e-9, "Q turns the selection by 15 degrees");
        CopySelection(); _hover = at + new Vector3(0, 0, 20); PasteAtCursor();
        T(Doc.Objects.Count == n0 + 2 && Sel.Count == 1 && Sel[0] != o && Sel[0].Cls == o.Cls, "pasted a copy at the mouse");
        DeleteSelection();
        T(Doc.Objects.Count == n0 + 1, "deleted");
        History.Undo();
        T(Doc.Objects.Count == n0 + 2, "undo: back");
        History.Redo();
        while (History.UndoCount > undo0) History.Undo();
        T(Doc.Objects.Count == n0 && !_objLayer.Has(o), "everything undone: the model gone");
        // the players: a slot's tribe and Test map's setup
        _playersUiBusy = true;
        History.Execute(PlayersEdit.Make(Doc, p => { p.Slots[1].Tribe = "Aje"; p.Slots[1].Control = "computer"; }));
        _playersUiBusy = false;
        var setup = EditorSession.TestSetup("editor:selftest", Doc.Name);
        T(setup.Slots.Count(s => s.Type != "closed") == Math.Max(2, PlayersSetup.ActiveSlots(Doc).Count) && setup.Slots[0].Type == "human", "Test map's setup from the players");
        T(setup.Slots.Any(s => s.Type == "ai" && s.Hq == 1 && s.Tribe == "Aje"), "slot 2: a computer player of the Dustriders");
        History.Undo();
        // a stone and a fruit bush follow a height edit: the document's Z and the drawn model on the new ground
        foreach (var cat in new[] { "Stones", "Fruit bushes" })
        {
            var cands = pal.Where(x => x.Category == "Resources" && x.Group == cat && x.Model != null && !x.Cls.StartsWith("fishshoal", StringComparison.OrdinalIgnoreCase)).ToList();
            string set3 = Doc.Setting.Length >= 3 ? Doc.Setting[..3] : Doc.Setting;          // (the map's setting's own: resource_stone_jun, jungle_fruit_bush)
            var e = cands.FirstOrDefault(x => x.Cls.Contains(set3, StringComparison.OrdinalIgnoreCase)) ?? cands.FirstOrDefault();
            if (e == null) { L.Info($"EDITOR skip objects: no {cat} with a model"); continue; }
            var (rx, ry) = _src!.ToMap(at.X - 30, at.Z + 30);
            var r = ObjectOps.Create(e, Doc, rx, ry, 0, null, 0, EditorSession.Rules);
            int u0 = History.UndoCount;
            History.Execute(ObjectsAddRemove.Add(Doc, new[] { r }, "Place (test)"));
            double z0 = r.Z;
            var brush = EditorSession.Brush;
            bool lower = brush.Lower; brush.Lower = false;
            for (int k = 0; k < 6; k++) History.Execute(brush.Step(Doc, rx, ry, 0.2)!, merge: true);
            History.EndMerge();
            brush.Lower = lower;
            var rn = _objLayer!.NodeOf(r);
            double g = _view.Ground.At(rn?.Position.X ?? 0, rn?.Position.Z ?? 0);
            T(r.Z > z0 + 0.5 && rn != null && Math.Abs(rn.Position.Y - g) < 0.05, $"{cat} ({e.Cls}): follows the raised ground (z {z0:0.00} -> {r.Z:0.00} m, model at {rn?.Position.Y:0.00}, ground {g:0.00})");
            while (History.UndoCount > u0) History.Undo();
            T(!Doc.Objects.Contains(r), $"{cat}: undone");
        }
        EditorSession.ToolGroup = group0;
        _hover = null;
        BuildPalette(); BuildProperties();
        L.Info($"EDITOR objects selftest {(bad == 0 ? "ok" : bad + " failed")}");
    }

    static bool s_argsDone;

    void ObjectsDemo()
    {
        var pal = ObjectToolState.PaletteOf();
        var tribe = EditorSession.Config.PlayableTribes.FirstOrDefault() ?? "Hu";
        var (cx, cy) = _src!.ToMap(_cam!.Target.X, _cam.Target.Z);
        var made = new List<MapObject>();
        var blds = pal.Where(e => e.Category == "Buildings" && e.Group == tribe && e.Model != null && !e.Cls.Contains("wall") && !e.Cls.Contains("palisade")).Take(3).ToList();
        for (int k = 0; k < blds.Count; k++) made.Add(ObjectOps.Create(blds[k], Doc, cx + 22 + k * 16, cy - 10, 0.6, 0, 1, EditorSession.Rules));
        var units = pal.Where(e => e.Category == "Units" && e.Group == tribe && e.Model != null).Take(4).ToList();
        for (int k = 0; k < units.Count; k++) made.Add(ObjectOps.Create(units[k], Doc, cx + 20 + k * 4, cy + 12, 2.4, 1, 2, EditorSession.Rules));
        if (pal.FirstOrDefault(e => e.Category == "Wild animals" && e.Model != null) is { } an) made.Add(ObjectOps.Create(an, Doc, cx + 46, cy + 18, 1, null, 1, EditorSession.Rules));
        History.Execute(ObjectsAddRemove.Add(Doc, made, "Demo objects"));
        Sel.Clear(); Sel.Add(made[0]);
        if (pal.FirstOrDefault(e => e.Group == "Rocks and cliffs") is { } rock) StartPlacing(rock);
        _hover = new Vector3(_cam.Target.X + 10, _cam.Target.Y, _cam.Target.Z + 22);
        _cam.X += 30; _cam.Dist = _cam.TDist = 70;
        BuildPalette(); BuildProperties();
    }

    /// <summary>--editor-test-map: a building of slot 0 put down before the test (checked in the running game)</summary>
    MapObject? ObjectsBeforeTestMap()
    {
        var pal = ObjectToolState.PaletteOf();
        var e = pal.FirstOrDefault(x => x.Category == "Buildings" && x.Model != null && x.Group == (EditorSession.Config.PlayableTribes.FirstOrDefault() ?? "Hu") && !x.Cls.EndsWith("fireplace", StringComparison.Ordinal) && !x.Cls.Contains("wall") && !x.Cls.Contains("palisade"));
        var st = Doc.Objects.FirstOrDefault(o => o.Type == "SLOC" && o.Owner == 0);
        if (e == null || st == null) return null;
        var o = ObjectOps.Create(e, Doc, st.X + 28, st.Y, 0, 0, 1, EditorSession.Rules);
        History.Execute(ObjectsAddRemove.Add(Doc, new[] { o }, "Place (test)"));
        return o;
    }
}
