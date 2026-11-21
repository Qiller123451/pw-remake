// Map viewer: an original ParaWorld map (.ula) of a configuration drawn as the game draws it - terrain, water,
// forests, decoration and the map's objects as static models (no simulation) - with the RTS camera.
//
// Scene: res://scenes/MapViewer.tscn. Controls: the game's camera (arrows / screen edge / middle drag pan, wheel and
// PageUp / PageDown zoom, Alt + left drag or middle + right drag turn, End resets); F1 shows the info line, F2 the
// fog of war test (a sight disc around the camera target), G cycles the grass (off / on / dense), F12 saves a
// screenshot to the user folder; B the biome brush (MapViewer.Biomes.cs: paint biome weights, Ctrl+S saves them).
// Command line (after "--"):
//   --map=<name or .ula path>     single_11, multi_2_jun_001, Cpn_single_001/single_01 ... (default multi_2_jun_001)
//   --data=<converted data>       (default: PW_DATA or the staged test data)   --install=<ParaWorld folder>
//   --config=<id>                 a prepared configuration of PW_CONTENT (or content/): its data and its overrides
//                                 (the setting textures, the effect models)
//   --settex=0                    the option "Setting textures on buildings" off (plain textures, no snow)
//   --cam=<x>,<z>[,<yaw deg>[,<distance>]]   or --spot=start|centre (default: the first start location)
//   --mapcam=<x>,<z>[,<yaw deg>[,<distance>]]   the camera's target in map coordinates (the co-op definitions')
//   --shadows=off|medium|high  --grass=off|on|dense  --res=<scale>  --aa=0|1  --objects=0|1  --weather=snow|rain[:<intensity>]
//   --fog=1 (fog of war test)  --adapt=<building model> (levels the ground under the camera target, as a building)
//   --shot=<png> [--frames=<n>]   renders n frames (default 30), saves the picture and quits
//   --coop=<config>:<mission>[:<seats>]   the map extended by the mission's co-op definition (game/coop, docs/COOP.md
//                                 "Maps") for 2 or 3 seats (default 3), with the seats' starts marked (and the enemy
//                                 bases red) in the overview; the seats' main buildings and armies and the enemy bases
//                                 as a headless co-op start places them (PW_CONTENT's configuration)
//   --overview                    a top-down orthographic view of the whole map (no depth fog, no shadows)
//   --biomes=<x.biomes.json>      draw the map with this biome layer (docs/BIOMES.md); --brush=<biome> starts the brush
//   --biomes-out=<file>           where Ctrl+S saves the painted layer (default: the map's own extension file)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.View.Fx;
using PwRemake.View.Models;
using PwRemake.View.Terrain;

namespace PwRemake.View.World;

public partial class MapViewer : Node3D
{
    static readonly PwRemake.Sim.Core.Logger L = Log.For("View.MapViewer");
    readonly Dictionary<string, string> _args = new();
    WorldRenderer _r = null!;
    WorldView _view = null!;
    RtsCamera _cam = null!;
    RtsCameraInput _input = null!;
    Label _info = null!;
    WeatherState? _weather;
    PwRemake.Sim.World.FogGrid? _fogTest;
    int _frames;
    string? _shot;
    int _shotFrames = 30;
    double _fpsAcc; int _fpsN; double _fps;
    bool _overview;

    /// <summary>--coop=config:mission[:seats]: the map extended by the mission's co-op definition (as the co-op map hook
    /// does it in a game) - returns the seats' starts to mark: seat 1 green, seat 2 orange, seat 3 magenta</summary>
    static List<(double X, double Z, Color C)> ApplyCoop(MapSource S, string arg, string? data = null)
    {
        var p = arg.Split(':');
        var marks = new List<(double, double, Color)>();
        int mission = (int)Json.ParseNum(p.Length > 1 ? p[1] : "0", 0), seats = p.Length > 2 ? (int)Json.ParseNum(p[2], 3) : 3;
        var def = PwRemake.Sim.Coop.CoopDefinition.Load(p[0], mission);
        if (def == null || S.Md == null || S.Rebuild == null) { L.Warn($"--coop: no co-op definition {arg}"); return marks; }
        var plan = CoopMapPlan.FromJson(def.Raw, seats);
        S.CopyFrom(S.Rebuild(MapExtension.Apply(S.Md, plan)));
        // seat 1: the mission's human player's start location (map.lua's mission of the configuration's mission folder,
        // PW_CONTENT or content/), else the map's start location of slot 0
        (double X, double Z)? s1 = null;
        try
        {
            var content = System.Environment.GetEnvironmentVariable("PW_CONTENT") is { Length: > 0 } pc ? pc : Path.Combine(PwRemake.App.AppPaths.RootDir, "content");
            var dir = Path.Combine(content, "configs", p[0], "missions");
            var entry = PwRemake.Sim.Campaign.MissionEntry.LoadIndex(dir).FirstOrDefault(e => e.Id == mission);
            var mj = entry != null ? PwMapReader.MissionOf(Path.Combine(dir, entry.Folder)) : null;
            if (mj?["players"]?[0]?["start_location"] is { } sl) s1 = S.ToGame(sl["x"].Num(), sl["y"].Num());
        }
        catch (Exception e) { L.Warn($"--coop: the mission data: {e.Message}"); }
        int k0 = S.StartSlots.IndexOf(0);
        if (s1 == null && k0 >= 0) s1 = (S.Starts[k0][0], S.Starts[k0][1]);
        if (s1 is { } a1) marks.Add((a1.X, a1.Z, new Color(0.2f, 1f, 0.2f)));
        foreach (var site in plan.Sites)
        {
            var (x, z) = S.ToGame(site.X, site.Z);
            marks.Add((x, z, site.Enemy ? new Color(0.9f, 0.1f, 0.1f) : site.Seat == 2 ? new Color(1f, 0.55f, 0f) : new Color(0.95f, 0.2f, 0.95f)));
        }
        // the seats' main buildings and start armies and the enemy bases of the definition, as the game places them (a
        // headless co-op start of the mission: PW_CONTENT's configuration), drawn as the map's own objects
        try
        {
            var content = System.Environment.GetEnvironmentVariable("PW_CONTENT") is { Length: > 0 } pc2 ? pc2 : Path.Combine(PwRemake.App.AppPaths.RootDir, "content");
            var gc = GameConfiguration.Load(Path.Combine(content, "configs", p[0]));
            if (gc != null && Directory.Exists(gc.MissionsDir) && data != null)
            {
                var start = new PwRemake.Sim.Game.GameStart { Configuration = p[0], Mission = mission, Difficulty = 1, Seed = 1 };
                var folder = PwRemake.Sim.Campaign.MissionSession.MissionFolderOf(start, gc);
                int i = 0;
                var cs = new[] { "human", "human", "ai" }.Take(seats).Select(k => new PwRemake.Sim.Coop.CoopSeat { Seat = ++i, Kind = k, Name = "Seat " + i }).ToList();
                start.Coop = PwRemake.Sim.Coop.CoopPlan.Make(def, folder!, cs, 0);
                var ms = PwRemake.Sim.Campaign.MissionSession.Create(start, gc, new PwRemake.Sim.Campaign.SessionOptions { DataDir = data, NoBrains = true, NoTriggers = true });
                var co = ms.Campaign.Coop!;
                // the ground as the co-op game has it (the extension and the definition's gates' pavement, CoopMapHook)
                if (ms.Map.Md is { } gmd) S.CopyFrom(S.Rebuild(gmd));
                var ents = new List<(PwRemake.Sim.World.Entity E, int Slot)>();
                // the definition's gates (CoopRuntime.Gates): the wall pieces they replace go, the gates come
                foreach (var g in co.Def.Gates.Where(g => g.Seats <= seats))
                {
                    foreach (var n in g.Replaces)
                        if (S.Md.Objects.FirstOrDefault(o => o.Name == n) is { } mo)
                        {
                            var (wx, wz) = S.ToGame(mo.X, mo.Y);
                            S.Owned.RemoveAll(o => o.Cls == mo.Cls && Math.Abs(o.X - wx) < 0.1 && Math.Abs(o.Z - wz) < 0.1);
                        }
                    if (ms.Campaign.Objects.Get(g.Replaces[0])?.Entity is PwRemake.Sim.World.Building gb && gb.Alive) ents.Add((gb, -1));
                }
                foreach (var seat in co.Seats.Where(x => x.Seat >= 2))
                    foreach (var e in ms.World.Buildings.Cast<PwRemake.Sim.World.Entity>().Concat(ms.World.Units).Where(e => e.Alive && e.Owner == seat.Player)) ents.Add((e, seat.Seat == 2 ? 1 : 5));
                foreach (var en in co.Enemies.List) foreach (var e in en.Placed.Where(e => e.Alive)) ents.Add((e, en.Slot));
                foreach (var (e, slot) in ents)
                    // (a building by its model's origin and turn, as the map's own: Building.Origin / Rot)
                    S.Owned.Add(e is PwRemake.Sim.World.Building b
                        ? new MapOwned { Type = "BLDG", Cls = e.Name, X = b.Origin.X, Z = b.Origin.Z, Rot = b.Rot, Owner = slot < 0 ? null : slot, Level = 1 }
                        : new MapOwned { Type = "CHTR", Cls = e.Name, X = e.Pos.X, Z = e.Pos.Z, Rot = e.Heading, Owner = slot, Level = e is PwRemake.Sim.World.Unit u ? u.Level : 1 });
                L.Info($"co-op map {arg}: {ents.Count} objects of the seats and the enemy additions placed");
            }
        }
        catch (Exception e) { L.Warn($"--coop: the co-op start: {e.Message}"); }
        L.Info($"co-op map {arg}: {S.Md.Hx * 2} x {S.Md.Hy * 2} m, {plan.Sites.Count} seat sites");
        return marks;
    }

    public override void _Ready()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--") && a.Contains('=')) _args[a[2..a.IndexOf('=')]] = a[(a.IndexOf('=') + 1)..];
            else if (a.StartsWith("--")) _args[a[2..]] = "1";
        if (_args.GetValueOrDefault("settex", "1") == "0") PwRemake.View.Materials.SettingMaterials.SetEnabled(false);
        string data = System.Environment.GetEnvironmentVariable("PW_DATA") ?? "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a";
        string install = System.Environment.GetEnvironmentVariable("PW_INSTALL") ?? "/mnt/user-data/uploads/Paraworld";
        if (_args.TryGetValue("data", out var d)) data = d;
        if (_args.TryGetValue("install", out var ins)) install = ins;
        var cfg = new GameConfiguration { Id = "map-viewer", Title = "Map viewer", Data = data, Install = install };
        // a prepared configuration (its data and its overrides: the setting textures, the effect models ...)
        if (_args.TryGetValue("config", out var cid))
        {
            var content = System.Environment.GetEnvironmentVariable("PW_CONTENT") is { Length: > 0 } pc0 ? pc0 : Path.Combine(PwRemake.App.AppPaths.RootDir, "content");
            if (GameConfiguration.Load(Path.Combine(content, "configs", cid)) is { } gc0) { cfg = gc0; data = gc0.Data; install = gc0.Install; }
            else L.Warn($"--config={cid}: no configuration in {content}");
        }
        var sw = Stopwatch.StartNew();
        var lib = new ModelLibrary(new AssetSource(cfg));
        var rules = PwRemake.Sim.Rules.Rules.Load(data);
        string mapName = _args.GetValueOrDefault("map", "multi_2_jun_001");
        var path = MapLoader.FindMap(cfg, mapName);
        if (path == null) { L.Error($"map not found: {mapName}"); GD.PushError($"map not found: {mapName}"); GetTree().Quit(1); return; }
        var S = MapLoader.Load(path, rules, data, lib.Has);
        if (_args.TryGetValue("biomes", out var bf)) S.UseBiomes(BiomeLayer.Read(bf));
        _mapPath = path; _lib = lib;
        var marks = new List<(double X, double Z, Color C)>();
        if (_args.TryGetValue("coop", out var coop)) marks = ApplyCoop(S, coop, data);
        double tRead = sw.Elapsed.TotalMilliseconds;

        var rs = new RenderSettings
        {
            Shadows = _args.GetValueOrDefault("shadows", "medium"), Grass = _args.GetValueOrDefault("grass", "on"),
            ResScale = Json.ParseNum(_args.GetValueOrDefault("res", "1"), 1), Antialias = _args.GetValueOrDefault("aa", "1") != "0",
        };
        _r = WorldRenderer.Create(rs);
        AddChild(_r);
        var opts = new WorldViewOptions
        {
            Render = rs, MapObjects = _args.GetValueOrDefault("objects", "1") != "0", Fog = true,
            ClassModel = (cls, level) => rules.Stats(cls, level, null)?.Gfx is { Length: > 0 } g && lib.Has(g) ? g : rules.ClassGfx(cls),
            // (the map's walk grid stands in for the game's blocked cells: no grass under rocks and in rivers)
            GrassBlocked = S.Walkable is { } wk ? (x, z) => !wk(x, z) : null,
        };
        _opts = opts;
        _view = WorldView.Load(S, lib, opts);
        AddChild(_view);
        _view.Fog.RevealAll = true;
        _biomes = new BiomeAmbience(_view, _r);
        L.Info($"{Path.GetFileName(path)}: read {tRead:0} ms, view {sw.Elapsed.TotalMilliseconds - tRead:0} ms");

        foreach (var (mx, mz, mc) in _args.ContainsKey("overview") ? marks : new())   // (seat marks in the overview only)
        {
            var mat = new StandardMaterial3D { AlbedoColor = mc, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            var ring = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 11, BottomRadius = 11, Height = 4, Material = mat } };
            ring.Position = new Vector3((float)mx, (float)_view.Ground.At(mx, mz) + 30, (float)mz);
            _view.Objects.AddChild(ring);
        }
        if (_args.ContainsKey("overview"))
        {
            // the whole map from above: orthographic, north up, no depth fog and no shadows (they need a near camera)
            _overview = true;
            _r.Env.Environment.FogEnabled = false;
            rs.Shadows = "off"; _r.ApplySettings();
            var c = _r.Camera;
            double w2 = S.Half != null ? S.Half[0] * 2 : S.Size, h2 = S.Half != null ? S.Half[1] * 2 : S.Size;
            var vp = GetViewport().GetVisibleRect().Size;
            c.Projection = Camera3D.ProjectionType.Orthogonal; c.Far = 4000;
            if (w2 / h2 > vp.X / vp.Y) { c.KeepAspect = Camera3D.KeepAspectEnum.Width; c.Size = (float)w2; } else c.Size = (float)h2;
        }
        _cam = new RtsCamera(_r.Camera, (x, z) => _view.Ground.At(x, z), S.Size / 2 - 8);
        // the camera starts at the first start location, looking along the level's default camera angle
        var spot = S.Starts.Count > 0 ? S.Starts[0] : new[] { 0.0, 0.0 };
        if (_args.GetValueOrDefault("spot", "start") is "centre" or "center") spot = new[] { 0.0, 0.0 };
        _cam.X = spot[0]; _cam.Z = spot[1];
        _cam.Yaw = Math.PI / 2 - S.DefaultCamera;
        if (_args.TryGetValue("cam", out var cs))
        {
            var p = cs.Split(',').Select(s => Json.ParseNum(s.Trim(), double.NaN)).ToArray();
            if (p.Length >= 2) { _cam.X = p[0]; _cam.Z = p[1]; }
            if (p.Length >= 3 && double.IsFinite(p[2])) _cam.Yaw = p[2] * Math.PI / 180;
            if (p.Length >= 4 && double.IsFinite(p[3])) _cam.Dist = _cam.TDist = Math.Clamp(p[3], 10, 400);
        }
        // (--mapcam: the same in the original map's map coordinates - the co-op definitions' - turned into game ones)
        if (_args.TryGetValue("mapcam", out var mcs))
        {
            var p = mcs.Split(',').Select(s => Json.ParseNum(s.Trim(), double.NaN)).ToArray();
            if (p.Length >= 2) (_cam.X, _cam.Z) = S.ToGame(p[0], p[1]);
            if (p.Length >= 3 && double.IsFinite(p[2])) _cam.Yaw = p[2] * Math.PI / 180;
            if (p.Length >= 4 && double.IsFinite(p[3])) _cam.Dist = _cam.TDist = Math.Clamp(p[3], 10, 400);
        }
        _cam.Update(0.016);
        _input = new RtsCameraInput { Cam = _cam, Name = "CameraInput" };
        AddChild(_input);

        if (_args.TryGetValue("weather", out var wa))
        {
            var parts = wa.Split(':');
            _weather = new WeatherState(parts[0] == "rain" ? 2 : 1, parts.Length > 1 ? Json.ParseNum(parts[1], 4) : 4, 270, 30);
        }
        else if (S.Weather is { Tracks.Count: > 0 } wp)
        {
            var t = wp.Tracks.FirstOrDefault(t => t.Type > 0 && t.Intensity > 0);
            if (t != null) _weather = new WeatherState((int)t.Type, t.Intensity, t.Wind, t.Strength);
        }
        if (_args.ContainsKey("fog")) StartFogTest();
        if (_args.TryGetValue("adapt", out var gfx))
        {
            bool ok = _view.AdaptTerrain(gfx, _cam.X, _cam.Z, 0);
            _view.ClearVegetation(_cam.X, _cam.Z, 14);
            L.Info($"adapt terrain {gfx} at {_cam.X:0}, {_cam.Z:0}: {ok}");
            // the building itself on the levelled ground
            if (lib.Instantiate(gfx) is ModelInstance b)
            {
                b.Position = new Vector3((float)_cam.X, (float)_view.Ground.At(_cam.X, _cam.Z), (float)_cam.Z);
                _view.Objects.AddChild(b);
            }
        }

        if (_args.ContainsKey("check")) { int bad = Check(); GD.Print($"CHECK {(bad == 0 ? "ok" : bad + " failed")}"); if (_shot == null && !_args.ContainsKey("shot")) { GetTree().Quit(bad); return; } }

        var layer = new CanvasLayer();
        AddChild(layer);
        _info = new Label { Position = new Vector2(8, 8), Visible = !_args.ContainsKey("shot") || _args.ContainsKey("info") };
        _info.AddThemeColorOverride("font_color", Colors.White);
        _info.AddThemeColorOverride("font_outline_color", Colors.Black);
        _info.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_info);
        _shot = _args.GetValueOrDefault("shot");
        if (_args.TryGetValue("frames", out var fr)) _shotFrames = Math.Max(1, (int)Json.ParseNum(fr, 30));
        BrushInit();
    }

    /// <summary>--check: the API the game session calls, on the loaded map (printed as CHECK lines; returns the failures)</summary>
    int Check()
    {
        int bad = 0;
        void T(bool ok, string what) { GD.Print($"CHECK {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) bad++; }
        var v = _view; var S = v.Source; var hf = v.Ground;
        T(v.Terrain.ChunkCount > 0 && hf.N == (int)Math.Round(S.Size / S.MeshCell) + 1, $"terrain {hf.N}x{hf.N} samples in {v.Terrain.ChunkCount} chunks");
        // the mesh's weights are the map source's (MapSource.Splat at the vertex)
        int i = hf.N / 3, j = hf.N / 2;
        var w = v.Terrain.VertexWeights(i, j);
        var sw = S.Splat(hf)(i * hf.Cell - S.Size / 2, j * hf.Cell - S.Size / 2);
        T(Enumerable.Range(0, Math.Min(8, sw.Length)).All(k => Math.Abs(w[k] - sw[k]) < 1e-5), "vertex weights = MapSource.Splat");
        T((S.Water == null) == (v.Water == null), $"water {(S.Water?.ToString() ?? "none")}");
        T(v.Trees.Count == S.Trees.Count && v.Trees.Count(t => t.Prop != null) > 0 || S.Trees.Count == 0, $"{v.Trees.Count(t => t.Prop != null)} of {S.Trees.Count} trees drawn");
        T(v.Props.Kinds.Count > 0 && v.Foliage.SpriteCount > 0, $"{v.Props.Kinds.Count} prop kinds, {v.Foliage.SpriteCount} foliage sprites");
        // AdaptTerrain: levels the samples under the brush and paints its materials
        double x = _cam.X, z = _cam.Z;
        var before = (float[])hf.H.Clone();
        var splatBefore = v.Splat(x, z);
        bool ok = v.AdaptTerrain("hu_fireplace", x, z, 0.3);
        int changed = 0; for (int k = 0; k < before.Length; k++) if (before[k] != hf.H[k]) changed++;
        var splatAfter = v.Splat(x, z);
        T(ok && v.GroundLog.Count == 1, "AdaptTerrain(hu_fireplace) has a brush");
        T(changed > 0 || before.Max() == before.Min(), $"{changed} height samples levelled");
        T(!splatBefore.SequenceEqual(splatAfter), "the brush painted the ground materials");
        T(!v.AdaptTerrain("no_such_building", x, z, 0), "no brush: false");
        // ClearVegetation: the trees and plants around go
        int near = v.Trees.Count(t => !t.Gone && Math.Sqrt((t.Src.X - x) * (t.Src.X - x) + (t.Src.Z - z) * (t.Src.Z - z)) <= 40);
        v.ClearVegetation(x, z, 40);
        int left = v.Trees.Count(t => !t.Gone && Math.Sqrt((t.Src.X - x) * (t.Src.X - x) + (t.Src.Z - z) * (t.Src.Z - z)) <= 40);
        T(left == 0, $"ClearVegetation took {near} trees");
        // the fog grid (terrain.js FogOfWar)
        var g = new PwRemake.Sim.World.FogGrid(S.Size, 4);
        g.Update(new[] { (x, z, 20.0) });
        T(g.Visible(x, z) && g.Visible(x + 18, z) && !g.Visible(x + 30, z) && g.Explored(x, z), "fog grid: a 20 m sight disc");
        g.Update(Array.Empty<(double, double, double)>());
        T(!g.Visible(x, z) && g.Explored(x, z) && g.TextureBytes()[g.N * (int)Math.Floor((z + S.Size / 2) / 4) + (int)Math.Floor((x + S.Size / 2) / 4)] == 115, "fog grid: explored stays (115)");
        v.SetFog(g);
        return bad;
    }

    void StartFogTest()
    {
        _fogTest = new PwRemake.Sim.World.FogGrid(_view.Source.Size, 4);
        _view.Fog.RevealAll = false;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (BrushKey(k)) return;
        switch (k.Keycode)
        {
            case Key.F1: _info.Visible = !_info.Visible; break;
            case Key.F2: if (_fogTest == null) StartFogTest(); else { _fogTest = null; _view.Fog.RevealAll = true; } break;
            case Key.G: _view.SetGrass(_view.Options.Render.Grass switch { "off" => "on", "on" => "dense", _ => "off" }); break;
            case Key.F12: SaveShot(Path.Combine(OS.GetUserDataDir(), $"map_{DateTime.Now:yyyyMMdd_HHmmss}.png")); break;
        }
    }

    void SaveShot(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(path);
        L.Info($"screenshot {path}");
    }

    public override void _Process(double delta)
    {
        if (_view == null) return;
        double dt = _shot != null ? 1 / 30.0 : Math.Min(0.1, delta);
        _input.Step(dt);
        _cam.Update(dt);
        _r.FollowSun(_cam.Target);
        if (_overview) _r.Camera.LookAtFromPosition(new Vector3(0, 1500, 0), Vector3.Zero, new Vector3(0, 0, -1));
        if (_fogTest != null)
        {
            _fogTest.Update(new[] { ((double)_cam.Target.X, (double)_cam.Target.Z, 60.0), ((double)_cam.Target.X + 90, (double)_cam.Target.Z, 30.0) });
            _view.SetFog(_fogTest);
        }
        _biomes.Update(_cam.Target);
        BrushStep(dt);
        _view.Step(dt, _r.Camera, _cam.Target, _biomes.Weather(_weather));
        _fpsAcc += delta; _fpsN++;
        if (_fpsAcc >= 0.5) { _fps = _fpsN / _fpsAcc; _fpsAcc = 0; _fpsN = 0; }
        if (_info.Visible)
        {
            var S = _view.Source;
            _info.Text = $"{S.Name} ({S.Setting}, {S.Size} m)  {_fps:0} fps  draws {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}  " +
                $"props {_view.Props.Drawn} in {_view.Props.Calls} draws  grass {_view.GrassCount}  sprites {_view.Foliage.SpriteCount}\n" +
                $"camera {_cam.X:0}, {_cam.Z:0}  yaw {_cam.Yaw * 180 / Math.PI:0}  distance {_cam.Dist:0}   [F1 info, F2 fog test, G grass, F12 screenshot, B biome brush]" + BrushInfo();
        }
        if (_shot != null && ++_frames >= _shotFrames)
        {
            SaveShot(_shot);
            GD.Print($"map viewer: shadows {_r.Sun.ShadowEnabled} (range {_r.Sun.DirectionalShadowMaxDistance} m), props drawn {_view.Props.Drawn}, load ms: {string.Join(", ", _view.LoadMs.Select(kv => $"{kv.Key} {kv.Value:0}"))}");
            GetTree().Quit();
        }
    }
}
