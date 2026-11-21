// The map editor (docs/MAP_EDITOR.md; plan: docs/MAP_EDITOR_PLAN.md phase 1): a screen of the game (the main menu's
// "Map Editor", `--editor`). The map is drawn with the game's own view (WorldView: terrain, water, forests, plants,
// grass, the map's objects), looked at with the editor camera (EditorCamera: the RTS camera with free zoom, turn and
// tilt), with a cursor on the ground (the 2 m height grid and the brush's circle). Around it, in the game's look:
//   top bar      New, Open, Save, Save as, Export / Import .pwmap, Undo, Redo, Test map, Exit - and the map's name
//                (* = unsaved changes)
//   left         the tool palette: Terrain, Paint, Objects, Players, Regions, Triggers (the frames; tools come with the
//                later phases - now: Terrain's raise / lower brush)
//   right        the properties of the tool and of the map, Test map's players, the minimap
//   bottom       the status line: the cursor's map coordinates, height, ground material, walkable
// Every edit is a command of the document's EditHistory (undo / redo, Ctrl+Z / Ctrl+Y); the document (IEditorMapDoc,
// EditorSession) outlives the screen, so Test map (a skirmish on the edited map from memory) comes back to it unchanged.
//
// Saving (docs/MAP_EDITOR.md "Saving and sharing"): the remake's map format into the user's maps folder ("My maps":
// user://maps/<name>/, MapFiles.UserDir); a map opened from the installation (content/configs) or a campaign mission is
// saved as a copy there (Save asks a name) - the installation's maps are never written.
//
// Keys: Ctrl+Z undo, Ctrl+Y / Ctrl+Shift+Z redo, Ctrl+N new, Ctrl+O open, Ctrl+S save (Ctrl+Shift+S save as),
// Ctrl+E export .pwmap, F5 Test map, Esc exit;
// [ / ] brush radius, - / = brush strength, left mouse: the chosen tool (Tools/*: Ctrl its other way); the camera's keys:
// EditorCamera.cs.
// Command line (docs/DEBUG.md): --editor [--map <name or path>] [--editor-selftest] [--editor-test-map <game s>].
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Editor.Triggers;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.UI;
using PwRemake.View.World;

namespace PwRemake.Editor;

public partial class MapEditor : Node3D
{
    static readonly Logger L = Log.For("Editor");
    readonly Dictionary<string, string> _args = GameApp.UserArgs();
    WorldRenderer _r = null!;
    WorldView? _view;
    MapSource? _src;
    EditorCamera? _cam;
    EditorCameraInput _camIn = null!;
    EditorCursor _cursor = null!;
    BiomeAmbience? _amb;
    IEditorMapDoc? _doc;
    int _frames, _loadAt = -1;
    Vector3? _hover;
    bool _stroke, _waterStale;
    double _waterT;

    IEditorMapDoc Doc => _doc!;
    EditHistory History => EditorSession.History!;

    public MapEditor() { Name = "MapEditor"; }

    public override void _Ready()
    {
        EditorSession.Testing = false;
        _r = WorldRenderer.Create(new RenderSettings
        {
            Shadows = GameApp.Settings.Shadows is "off" or "high" ? GameApp.Settings.Shadows : "medium", Grass = GameApp.Settings.Grass is "off" or "dense" ? GameApp.Settings.Grass : "on",
            ResScale = Math.Clamp(GameApp.Settings.RenderScale, 0.25, 2), Antialias = GameApp.Settings.Antialias,
        });
        AddChild(_r);
        _cursor = new EditorCursor();
        AddChild(_cursor);
        _camIn = new EditorCameraInput { Name = "CameraInput" };
        AddChild(_camIn);
        BuildUi();
        if (EditorSession.Doc == null)
        {
            // the first start: the map of --map, else a new flat map
            IEditorMapDoc? d = null;
            if (_args.TryGetValue("map", out var m) && m.Length > 0)
            {
                var path = MapLoader.FindMap(EditorSession.Config, m);
                if (path != null) d = TryOpen(path); else L.Warn($"--map {m}: not found");
            }
            EditorSession.Use(d ?? MapDataDoc.CreateFlat(new NewMapSpec { Size = 512 }));
        }
        SetStatus("Loading the map …");
        _loadAt = 2;              // (two frames: the interface shows "Loading" first)
    }

    public override void _ExitTree()
    {
        if (_doc != null) _doc.Changed -= OnDocChanged;
        if (EditorSession.History != null) EditorSession.History.Changed -= RefreshUi;
        if (_cam != null) EditorSession.Camera = (_cam.X, _cam.Z, _cam.Yaw, _cam.Pitch, _cam.TDist);
    }

    static IEditorMapDoc? TryOpen(string path)
    {
        try { return MapDataDoc.Open(path); }
        catch (Exception e) { L.Error($"the map could not be opened: {path}", e); return null; }
    }

    // ================================================================================ the view of the document

    /// <summary>draws the session's document (again): the world view, the camera, the minimap</summary>
    void LoadView()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (_doc != null) _doc.Changed -= OnDocChanged;
        EditorSession.History!.Changed -= RefreshUi;
        if (_view != null) { RemoveChild(_view); _view.QueueFree(); _view = null; }
        // the same map drawn again (a biome added, water turned on or off): the camera stays where it is
        (double X, double Z, double Yaw, double Pitch, double Dist)? keep = _cam != null && ReferenceEquals(_doc, EditorSession.Doc) ? (_cam.X, _cam.Z, _cam.Yaw, _cam.Pitch, _cam.TDist) : null;
        _doc = EditorSession.Doc!;
        var S = _src = EditorSession.SourceOf(_doc);
        var rules = EditorSession.Rules;
        var lib = EditorSession.Models;
        var d = _doc;
        var opts = new WorldViewOptions
        {
            Render = _r.Settings, MapObjects = true, Fog = true,
            ClassModel = (cls, level) => rules.Stats(cls, level, null)?.Gfx is { Length: > 0 } g && lib.Has(g) ? g : rules.ClassGfx(cls),
            // (no grass where land units cannot walk: rocks, rivers - the walk grid as the map viewer)
            GrassBlocked = (x, z) => { var (mx, my) = S.ToMap(x, z); return !d.WalkableAt(mx, my); },
        };
        try
        {
            _view = WorldView.Load(S, lib, opts);
        }
        catch (Exception e)
        {
            L.Error("the map could not be drawn", e);
            ShowDialog(EditorDialog.Message("Map Editor", "The map could not be drawn: " + e.Message));
            return;
        }
        AddChild(_view);
        _view.Fog.RevealAll = true;
        _amb = new BiomeAmbience(_view, _r);
        _cam = new EditorCamera(_r.Camera, (x, z) => _view.Ground.At(x, z), S.Size / 2 - 8);
        if (keep is { } k) { _cam.X = k.X; _cam.Z = k.Z; _cam.Yaw = k.Yaw; _cam.Pitch = k.Pitch; _cam.Dist = _cam.TDist = k.Dist; }
        else if (EditorSession.Camera is { } c) { _cam.X = c.X; _cam.Z = c.Z; _cam.Yaw = c.Yaw; _cam.Pitch = c.Pitch; _cam.Dist = _cam.TDist = c.Dist; }
        else
        {
            var spot = S.Starts.Count > 0 ? S.Starts[0] : new[] { 0.0, 0.0 };
            _cam.X = spot[0]; _cam.Z = spot[1]; _cam.Yaw = Math.PI / 2 - S.DefaultCamera;
        }
        _cam.Update(1);
        _camIn.Cam = _cam;
        var o = S.Origin ?? new[] { 0.0, 0.0 };
        _cursor.HeightAt = (x, z) => _view.Ground.At(x, z);
        _cursor.Cell = d.Cell; _cursor.GridX0 = d.OriginX - o[0]; _cursor.GridZ0 = d.OriginY - o[1];
        _minimap.Ox = o[0]; _minimap.Oy = o[1];
        _minimap.Starts = S.Starts.Select(s => (s[0], s[1])).ToList();
        _minimap.SetDoc(d);
        ObjectsViewLoaded();                                  // (the objects: Tools/ObjectTool.cs)
        MissionViewLoaded();                                  // (regions, mission data, triggers: Tools/RegionTools.cs)
        d.Changed += OnDocChanged;
        EditorSession.History.Changed += RefreshUi;
        RefreshUi();
        L.Info($"editor view of {d.Name}: {S.Size} m, {_view.Terrain.ChunkCount} terrain chunks, {_view.Trees.Count} trees, {_view.MapModels.Count} objects - {sw.ElapsedMilliseconds} ms");
        TerrainViewLoaded();
        if (_args.ContainsKey("editor-selftest") && !_selftestDone) { _selftestDone = true; SelfTest(); }
        else if (_args.TryGetValue("editor-test-map", out var tm) && !_testStarted) StartScriptedTest(tm);
        else if (_args.ContainsKey("editor-walkthrough") && s_walk < 2) WalkthroughStep();     // (MapEditor.Walkthrough.cs)
    }

    /// <summary>the document changed: the view follows (heights: only the touched chunks of the mesh)</summary>
    void OnDocChanged(DocChange c)
    {
        if (_view == null || _src == null) return;
        switch (c.Kind)
        {
            case DocChangeKind.Heights: RefreshHeights(c.Rect); QueueVegetation(c.Rect); ObjectsHeightsChanged(c.Rect); break;
            case DocChangeKind.Info: RefreshUi(); ObjectsInfoChanged(); break;
            case DocChangeKind.Objects: ObjectsDocChanged(); break;       // (phase 3: Tools/ObjectTool.cs)
            case DocChangeKind.Materials or DocChangeKind.Walk or DocChangeKind.Biomes or DocChangeKind.Forest or DocChangeKind.Plants or DocChangeKind.Water:
                OnTerrainChanged(c); break;                // (phase 2: MapEditor.Terrain.cs)
            default: _loadAt = _frames + 1; break;       // (anything else: drawn again)
        }
        _minimap.Invalidate();
    }

    /// <summary>the samples of the document in r changed: the view's height field there sampled again from the map
    /// source (it reads the document's samples), the terrain chunks over them rebuilt, the grass there renewed; the
    /// water's depth texture a moment later (it is the whole map's)</summary>
    void RefreshHeights(GridRect r)
    {
        var hf = _view!.Ground; var S = _src!; var d = Doc;
        var o = S.Origin ?? new[] { 0.0, 0.0 };
        double half = hf.Size / 2;
        // the document's rectangle (and one sample around: the bilinear lookup) in game coordinates -> the field's samples
        double x0 = d.OriginX + (r.I0 - 1) * d.Cell - o[0], x1 = d.OriginX + (r.I1 + 1) * d.Cell - o[0];
        double z0 = d.OriginY + (r.J0 - 1) * d.Cell - o[1], z1 = d.OriginY + (r.J1 + 1) * d.Cell - o[1];
        int i0 = Math.Max(0, (int)Math.Floor((x0 + half) / hf.Cell)), i1 = Math.Min(hf.N - 1, (int)Math.Ceiling((x1 + half) / hf.Cell));
        int j0 = Math.Max(0, (int)Math.Floor((z0 + half) / hf.Cell)), j1 = Math.Min(hf.N - 1, (int)Math.Ceiling((z1 + half) / hf.Cell));
        if (i1 < i0 || j1 < j0) return;
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                hf.H[j * hf.N + i] = (float)S.Height(i * hf.Cell - half, j * hf.Cell - half);
        _view.Terrain.RefreshHeights(new SampleRect(i0, j0, i1, j1));
        double cx = (x0 + x1) / 2, cz = (z0 + z1) / 2, rr = Math.Max(x1 - x0, z1 - z0) / 2 + 4;
        _view.InvalidateGrass(cx, cz, rr);
        _waterStale = true;
    }

    // ================================================================================ the frame

    public override void _Process(double delta)
    {
        _frames++;
        if (_frames == _loadAt) { _loadAt = -1; LoadView(); }
        if (_view == null || _cam == null || !IsInsideTree()) return;      // (not yet drawn; or Test map took the screen)
        double dt = Math.Min(0.1, delta);
        _camIn.Step(dt);
        _cam.Update(dt);
        _r.FollowSun(_cam.Target);
        _amb?.Update(_cam.Target);
        // the ground under the mouse (not over the panels, not while a dialog is open)
        _hover = null;
        var mp = GetViewport().GetMousePosition();
        if (OverView(mp))
        {
            Vector3 ro = _r.Camera.ProjectRayOrigin(mp), rd = _r.Camera.ProjectRayNormal(mp);
            var hf = _view.Ground;
            if (GroundPick.Ray(ro.X, ro.Y, ro.Z, rd.X, rd.Y, rd.Z, hf.At) is { } p) _hover = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
        }
        // the tool: while the left button is held (the stroke began over the map) a step every frame (EditorTool)
        var tool = EditorTools.Current;
        if (_stroke)
        {
            if (!Input.IsMouseButtonPressed(MouseButton.Left)) EndStroke();
            else tool?.Step(Ctx(), _camIn.Dragging ? null : PointOf(_hover), dt);
        }
        var look = tool?.Cursor(Ctx(), PointOf(_hover)) ?? default;
        _cursor.Show(_hover, look.Radius, look.Other, look.Square, look.Lines);
        TerrainFrame(dt);
        MissionFrame();
        ObjectsProcess(dt);
        if (_waterStale && (_waterT -= dt) <= 0 && !_stroke) { _view.Water?.RefreshDepth(); _waterStale = false; _waterT = 0.3; }
        _view.Step(dt, _r.Camera, _cam.Target, _amb?.Weather(null));
        UpdateStatus();
    }

    /// <summary>is the mouse over the map (not over a panel or a dialog): a motion no control took reached
    /// _UnhandledInput (the panels stop the mouse's events)</summary>
    bool OverView(Vector2 p) => _dialogs.GetChildCount() == 0 && !_overUi && GetViewport().GetVisibleRect().HasPoint(p);
    bool _overUi;

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseMotion) _overUi = true;          // (until _UnhandledInput sees it: no control took it)
        else if (ObjectsPreInput(e)) GetViewport().SetInputAsHandled();     // (the wheel, Q / E turn objects: before the camera)
    }

    void BrushStep(Vector3 at, double dt)
    {
        var b = EditorSession.Brush;
        bool flip = Input.IsKeyPressed(Key.Ctrl);
        if (flip) b.Lower = !b.Lower;
        var (mx, my) = _src!.ToMap(at.X, at.Z);
        var e = b.Step(Doc, mx, my, dt);
        if (flip) b.Lower = !b.Lower;
        if (e != null) History.Execute(e, merge: true);
    }

    void EndStroke()
    {
        if (!_stroke) return;
        _stroke = false;
        if (_view != null && _doc != null) EditorTools.Current?.End(Ctx(), PointOf(_hover));
        History.EndMerge();
        L.Debug($"stroke done: {History.UndoCount} undo steps");
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion) _overUi = false;
        if (_dialogs.GetChildCount() > 0) return;
        if (MissionInput(e)) { GetViewport().SetInputAsHandled(); return; }
        if (ObjectsInput(e)) { GetViewport().SetInputAsHandled(); return; }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb && !mb.AltPressed && OverView(mb.Position) && _view != null)
        {
            if (EditorTools.Current is { } tool && PointOf(_hover) is { } at) { _stroke = true; tool.Begin(Ctx(), at); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (e is not InputEventKey { Pressed: true } k || GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit or SpinBox) return;
        bool handled = true;
        if (k.CtrlPressed)
            switch (k.Keycode)
            {
                case Key.Z when k.ShiftPressed: Redo(); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.N when !k.Echo: NewMap(); break;
                case Key.O when !k.Echo: OpenMap(); break;
                case Key.S when !k.Echo: Save(k.ShiftPressed); break;
                case Key.E when !k.Echo: ExportPwmap(); break;
                default: handled = false; break;
            }
        else
            switch (k.Keycode)
            {
                case Key.F5 when !k.Echo: TestMap(); break;
                case Key.Escape when !k.Echo: Exit(); break;
                case Key.Bracketleft: EditorSession.Brush.Radius = Math.Max(2, EditorSession.Brush.Radius / 1.15); SyncBrushUi(); break;
                case Key.Bracketright: EditorSession.Brush.Radius = Math.Min(120, EditorSession.Brush.Radius * 1.15); SyncBrushUi(); break;
                case Key.Minus: EditorSession.Brush.Strength = Math.Max(0.5, EditorSession.Brush.Strength / 1.2); SyncBrushUi(); break;
                case Key.Equal: EditorSession.Brush.Strength = Math.Min(40, EditorSession.Brush.Strength * 1.2); SyncBrushUi(); break;
                default: handled = false; break;
            }
        if (handled) GetViewport().SetInputAsHandled();
    }

    // ================================================================================ the commands of the top bar

    void Undo() { EndStroke(); if (!History.Undo()) SetStatusNote("Nothing to undo"); }
    void Redo() { EndStroke(); if (!History.Redo()) SetStatusNote("Nothing to redo"); }

    /// <summary>runs go after asking, when the map has unsaved changes (Save first, then go; Discard; Cancel)</summary>
    void IfSaved(string what, Action go)
    {
        if (History.Dirty) ShowDialog(EditorDialog.Unsaved(what, go, () => Save(false, go))); else go();
    }

    void NewMap() => IfSaved("Make a new map", () => ShowDialog(EditorDialog.NewMap(spec =>
    {
        EditorSession.Use(MapDataDoc.CreateFlat(spec));
        SetStatus("Making the map …");
        _loadAt = _frames + 2;
    })));

    void OpenMap() => IfSaved("Open another map", () => ShowDialog(EditorDialog.OpenMap(OpenPath, ImportPwmap)));

    void OpenPath(string path)
    {
        var d = TryOpen(path);
        if (d == null) { ShowDialog(EditorDialog.Message("Open map", $"The map could not be read: {path} (the log has the reason).")); return; }
        EditorSession.Use(d);
        SetStatus("Loading the map …");
        _loadAt = _frames + 2;
    }

    /// <summary>Save: into the map's own place in My maps; a new map, one of the installation or a mission: Save as (a
    /// name; a copy in My maps). then: after a successful save.</summary>
    void Save(bool saveAs, Action? then = null)
    {
        if (_doc == null) return;
        EndStroke();
        if (!saveAs && EditorSession.SavesInPlace(Doc)) { SaveTo(Doc.SourcePath!, then); return; }
        ShowDialog(EditorDialog.SaveAs(Doc.Name, path => SaveTo(path, then), ShowDialog));
    }

    /// <summary>writes the map (a folder, or a .pwmap file it came from) - the history marks it saved</summary>
    void SaveTo(string path, Action? then = null)
    {
        try
        {
            EditorSession.Save(Doc, path);
            History.MarkSaved();
            SetStatusNote($"Saved: {path}{(Doc.Requires.Length > 0 ? $" (needs {Doc.Requires}: the other configurations' lists leave it out)" : "")}");
            RefreshUi();
            then?.Invoke();
        }
        catch (Exception e)
        {
            L.Error($"the map could not be saved to {path}", e);
            ShowDialog(EditorDialog.Message("Save", $"The map could not be saved: {e.Message}"));
        }
    }

    /// <summary>Export .pwmap: the map as one file to share (PwMapWriter.WriteZip); the document stays where it is</summary>
    void ExportPwmap()
    {
        if (_doc == null) return;
        EndStroke();
        var name = EditorSession.SafeName(Doc.Name);
        _dialogs.AddChild(EditorDialog.PwmapFile(true, EditorSession.UserMapsDir, name + PwMapPaths.Ext, ExportTo));
        if (_dialogs.GetChild(_dialogs.GetChildCount() - 1) is FileDialog fd) fd.PopupCentered();
    }

    void ExportTo(string path)
    {
        if (!path.EndsWith(PwMapPaths.Ext, StringComparison.OrdinalIgnoreCase)) path += PwMapPaths.Ext;
        try
        {
            EditorSession.StampRequirement(Doc);
            PwMapWriter.WriteZip(Doc.ToMap(), path);
            var foreign = WorkshopClasses();
            SetStatusNote($"Exported: {path}");
            if (foreign.Count > 0 || Doc.Requires.Length > 0)
                ShowDialog(EditorDialog.Message("Export .pwmap", $"Exported to {path}.\n\n" +
                    (foreign.Count > 0 ? $"The map uses classes the official game does not have ({string.Join(", ", foreign.Take(8))}{(foreign.Count > 8 ? " …" : "")}): " +
                        $"players need the same configuration ({EditorSession.Config.Title}) - a .pwmap does not carry units of a mod or of the Workshop." : "") +
                    (Doc.Requires.Length > 0 ? $"{(foreign.Count > 0 ? "\n\n" : "")}map.lua says it requires {Doc.Requires} ({string.Join("; ", MapRequirement.Reasons(Doc, EditorSession.Requirement(), 3))}): " +
                        "the official game's lists leave it out." : "")));
        }
        catch (Exception e)
        {
            L.Error($"the map could not be exported to {path}", e);
            ShowDialog(EditorDialog.Message("Export .pwmap", $"The map could not be exported: {e.Message}"));
        }
    }

    /// <summary>the map's object classes the base game's class lists lack (a mod's: MIRAGE, the Workshop's)</summary>
    List<string> WorkshopClasses()
    {
        try
        {
            var baseCfg = GameApp.Configurations.FirstOrDefault(c => string.IsNullOrEmpty(c.Mod) && c.Id != EditorSession.Config.Id);
            if (baseCfg == null) return new();
            var names = PwRemake.Sim.Editor.ObjectPalette.ClassNamesOf(baseCfg.Data);
            if (names.Count == 0) return new();
            return Doc.Objects.Where(o => o.Type != "SLOC" && !names.Contains(o.Cls)).Select(o => o.Cls).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        }
        catch (Exception e) { L.Debug("classes of the map: " + e.Message); return new(); }
    }

    /// <summary>Import .pwmap: a map file copied into My maps (listed in the skirmish setup from now on) and opened</summary>
    void ImportPwmap() => IfSaved("Open another map", () =>
    {
        _dialogs.AddChild(EditorDialog.PwmapFile(false, System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "", ImportFrom));
        if (_dialogs.GetChild(_dialogs.GetChildCount() - 1) is FileDialog fd) fd.PopupCentered();
    });

    void ImportFrom(string file)
    {
        try
        {
            var target = ImportInto(file, EditorSession.UserMapsDir);
            L.Info($"imported {file} -> {target}");
            OpenPath(target);
        }
        catch (Exception e)
        {
            L.Error($"the map could not be imported: {file}", e);
            ShowDialog(EditorDialog.Message("Import .pwmap", $"The map could not be imported: {e.Message}"));
        }
    }

    /// <summary>copies a .pwmap into the folder (a free name: "x.pwmap", "x (2).pwmap" ...) after reading it once (a damaged
    /// file is refused before it is copied); the copy's path</summary>
    public static string ImportInto(string file, string dir)
    {
        PwMapReader.ReadHeader(file);
        System.IO.Directory.CreateDirectory(dir);
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        if (System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(file)!) == System.IO.Path.GetFullPath(dir)) return file;      // (already in My maps)
        var target = System.IO.Path.Combine(dir, name + PwMapPaths.Ext);
        for (int k = 2; System.IO.File.Exists(target) || System.IO.Directory.Exists(target); k++) target = System.IO.Path.Combine(dir, $"{name} ({k}){PwMapPaths.Ext}");
        System.IO.File.Copy(file, target);
        return target;
    }

    /// <summary>Test map: a skirmish on the map as it is now (from memory, no save needed); its end comes back here</summary>
    void TestMap()
    {
        if (_doc == null || _view == null) return;
        EndStroke();
        if (MissionTestMap()) return;                     // (a map with triggers: played as a mission - Tools/TriggerEditor.cs)
        var key = EditorPlaytest.Register(Doc);
        var setup = EditorSession.TestSetup(key, Doc.Name);
        var start = new GameStart { Configuration = EditorSession.Config.Id, Mode = GameMode.Skirmish, Skirmish = setup, Seed = setup.Seed };
        L.Info($"Test map: {start}");
        EditorSession.Testing = true;
        Boot.Instance.Show(PwRemake.UI.LoadingScreen.ForSkirmish(start));
    }

    void Exit() => IfSaved("Leave the editor", () =>
    {
        L.Info("leaving the map editor");
        if (History.Dirty) History.MarkSaved();          // (discarded: quitting the game does not ask again; the map stays in memory)
        TestMission.Clean();                             // (Test map's mission folders of this run)
        Boot.Instance.Show(new TitleScreen());
    });

    // ================================================================================ test runs (command line)

    bool _testStarted;
    static bool _selftestDone;
    static bool s_testReturned;

    /// <summary>--editor-selftest: a stroke of the raise brush at the camera's target through the editor's own path (the
    /// history, the document, the view's refresh), checked, undone, checked; then --editor-test-map if given. EDITOR
    /// lines in the log; "EDITOR selftest ok" or "... FAIL".</summary>
    void SelfTest()
    {
        int bad = 0;
        void T(bool ok, string what) { L.Info($"EDITOR {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) bad++; }
        var hf = _view!.Ground;
        var at = _cam!.Target;
        var before = (float[])hf.H.Clone();
        double h0 = hf.At(at.X, at.Z);
        int undo0 = History.UndoCount;
        _stroke = true;
        for (int k = 0; k < 10; k++) BrushStep(new Vector3(at.X + k * 0.3f, at.Y, at.Z), 0.1);
        EndStroke();
        double h1 = hf.At(at.X, at.Z);
        T(h1 > h0 + 1, $"the stroke raised the view's ground at the target: {h0:0.00} -> {h1:0.00} m");
        T(History.UndoCount == undo0 + 1, "one undo step for the stroke");
        T(History.Dirty, "unsaved changes");
        var (mx, my) = _src!.ToMap(at.X, at.Z);
        T(Math.Abs(Doc.HeightAt(mx, my) - _src.Height(at.X, at.Z)) < 1e-4, "the document and the map source agree");
        History.Undo();
        T(hf.H.SequenceEqual(before), "undo: every height sample of the view as before");
        T(!History.Dirty, "undo: no unsaved changes");
        History.Redo();
        T(hf.At(at.X, at.Z) > h0 + 1, "redo: raised again");
        History.Undo();
        TerrainSelfTest(T);                                // (phase 2: a stroke of every terrain and paint tool)
        MissionSelfTest(T);                                // (phases 5, 6: a region and a trigger - Tools/TriggerEditor.cs)
        L.Info($"EDITOR selftest {(bad == 0 ? "ok" : bad + " failed")}");
        ObjectsSelfTest();
        if (_args.TryGetValue("editor-test-map", out var tm)) StartScriptedTest(tm);
    }

    /// <summary>--editor-test-map &lt;game s&gt;: a stroke, Test map, and back to the editor at that game time (the pause
    /// menu's quit); the editor then checks that its map is as it was ("EDITOR test map ok")</summary>
    void StartScriptedTest(string arg)
    {
        if (s_testReturned)
        {
            bool same = s_heights != null && EditorSession.Doc is MapDataDoc md && md.Md.Heights.SequenceEqual(s_heights);
            L.Info($"EDITOR test map {(same ? "ok" : "FAIL")}: back in the editor, the map {(same ? "unchanged" : "CHANGED")}");
            return;
        }
        _testStarted = true;
        var at = _cam!.Target;
        _stroke = true;
        for (int k = 0; k < 8; k++) BrushStep(at, 0.1);
        EndStroke();
        var placed = ObjectsBeforeTestMap();
        s_heights = EditorSession.Doc is MapDataDoc d ? (float[])d.Md.Heights.Clone() : null;
        double t = Json.ParseNum(arg, 10);
        var tree = GetTree();
        void Check()
        {
            if (GameSession.Current is not { } s || !s.EditorTest || s.World.Time < t) return;
            tree.ProcessFrame -= Check;
            L.Info($"EDITOR test map running: {s.Sim.Title}, {s.World.Players.Count} players, game time {s.World.Time:0.0} s, ground at the hill {s.Sim.Built.Map.Hf.At(at.X, at.Z):0.00} m");
            if (placed != null) L.Info($"EDITOR test map objects {(s.World.Buildings.Any(b => b.Name == placed.Cls && b.Owner?.Hq == 0) ? "ok" : "FAIL")}: the placed {placed.Cls} stands for player 1");
            s_testReturned = true;
            s.EndAction("menu");
        }
        tree.ProcessFrame += Check;
        TestMap();
    }
    static float[]? s_heights;
}
