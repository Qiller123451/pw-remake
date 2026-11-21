// The light of the models in the game's world light (WorldRenderer: sky, sun, fog): a check that a double-sided
// material on a mirrored model is lit like a single-sided one (ModelMaterials.TwoSided - every model's root mirrors),
// and screenshots of buildings, units and animals of the tribes from the game's camera angle, to compare before /
// after a change of the materials or lights (docs/VIEW.md "Light").
//
// Run (rendering on llvmpipe in the container):
//   xvfb-run -a -s "-screen 0 1600x900x24" godot --path game res://src/View/Tests/LightingTest.tscn -- --shots=<dir> [--data=<folder>] [--only=buildings|units|seas]
//   --only=seas: SEAS units, a vehicle and buildings of a blue and a red player, in their original colours (top) and in
//   the player colours (bottom, their paint recoloured: ModelMaterials.SeasPainted), one picture seas_colours.png
//   --only=seasall: every SEAS unit, vehicle, robot, ship and building of the data, each scaled to one grid cell, a
//   blue and a red copy side by side (labelled), orthographic from the game's angle; original colours above, player
//   colours (white key + SeasMarkings) below: seas_all.png. Best with a larger window (--resolution 2400x1400 and
//   an xvfb screen of that size).
//   --only=seasunits: the same for the SEAS units, vehicles, robots and ships: seas_units.png.
//   --only=seasmirage: the same for the SEAS classes only MIRAGE adds (--data=<export>@MIRAGE): seas_mirage.png.
//   --only=seastents: the two tents in the proposal PW_SEAS_TENTS picks (SeasMarkings): seas_tents_<small><big>.png.
//   --only=seasbldg: the same for the SEAS buildings only, 5 pairs a row, labels above the pairs: seas_buildings.png.
//   --only=seasclose [--models=a,b] [--gap=1.6]: a few SEAS models close up, a row each: original | blue | red
//   (seas_close.png; --gap spreads the cells). --setting=icewaste: the models in a setting's textures (MIRAGE's snowy
//   seas_building_a / seas_tent, SettingMaterials; --data must hold assets/tex/settings with its index.json, as the
//   prepare tool writes them); --colors=yellow,green: the two player colours.
// Exit code 0 = all checks passed.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.View.Materials;
using PwRemake.View.Models;
using PwRemake.View.Parts;
using PwRemake.View.Tools;
using PwRemake.View.World;

namespace PwRemake.View.Tests;

public partial class LightingTest : Node3D
{
    int _fails, _checks;
    string _shots = "", _only = "";
    ModelLibrary _lib = null!;
    WorldRenderer _r = null!;
    readonly List<Node> _temp = new();

    void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) { _fails++; GD.PrintErr("FAIL " + what); }
        else GD.Print("ok   " + what);
    }

    public override async void _Ready()
    {
        Log.AddSink(new GodotPrintSink());
        foreach (var a in OS.GetCmdlineUserArgs()) { if (a.StartsWith("--shots=")) _shots = a.Substring(8); if (a.StartsWith("--only=")) _only = a.Substring(7); }
        try
        {
            _r = WorldRenderer.Create();
            AddChild(_r);
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(600, 600) }, Name = "Ground",
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.Color8(0x8a, 0x84, 0x52), DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert, SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled, Roughness = 1 },
            });
            await MirroredTwoSided();
            _lib = new ModelLibrary(new AssetSource(ViewerStage.TestConfig()));
            await Shots();
        }
        catch (Exception ex) { _fails++; GD.PrintErr("EXCEPTION " + ex); }
        GD.Print($"lighting tests: {_checks - _fails}/{_checks} passed");
        GetTree().Quit(_fails == 0 ? 0 : 1);
    }

    async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    void Look(Vector3 from, Vector3 at)
    {
        _r.Camera.Position = from; _r.Camera.LookAt(at);
        _r.FollowSun(at);
    }

    // a white quad facing the sun under a mirroring parent (as the models' roots), once single-sided, once double-sided
    // (ToLambert of an imported material with cull disabled); both must be lit alike (before the fix: ambient only)
    async Task MirroredTwoSided()
    {
        var sunDir = WorldRenderer.SunOffset.Normalized();
        Material Mat(bool twoSided) => ModelMaterials.ToLambert(new StandardMaterial3D
        {
            ResourceName = "test", AlbedoColor = Colors.White,
            CullMode = twoSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
        }, null, "Bldg", _ => null);
        var quads = new List<Node3D>();
        foreach (var (x, dbl) in new[] { (-3f, false), (3f, true) })
        {
            var holder = new Node3D { Position = new Vector3(x, 30, 0), Scale = new Vector3(-1, 1, 1) };
            var q = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(4, 4) }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            q.SetSurfaceOverrideMaterial(0, Mat(dbl));
            holder.AddChild(q);
            AddChild(holder);
            // (the quad's front, +z, towards the sun; under the mirror: the holder turned so that the world normal is sunDir)
            var n = new Vector3(-sunDir.X, sunDir.Y, sunDir.Z);           // the local normal the mirror turns into sunDir
            q.Basis = Basis.LookingAt(-n, Mathf.Abs(n.Y) > 0.9f ? Vector3.Forward : Vector3.Up);
            quads.Add(holder);
        }
        // the camera on the sun's side, looking at both
        Look(new Vector3(0, 30, 0) + sunDir * 25, new Vector3(0, 30, 0));
        await Frames(4);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = GetViewport().GetTexture().GetImage();
        float Lum(Vector3 p)
        {
            var s = _r.Camera.UnprojectPosition(p);
            var c = img.GetPixel((int)s.X, (int)s.Y);
            return (c.R + c.G + c.B) / 3;
        }
        float one = Lum(new Vector3(-3, 30, 0)), two = Lum(new Vector3(3, 30, 0));
        Check(one > 0.6f && Mathf.Abs(two - one) < 0.05f, $"mirrored double-sided material lit like a single-sided one (single {one:0.00}, double {two:0.00})");
        foreach (var q in quads) { RemoveChild(q); q.QueueFree(); }
    }

    ModelInstance? Place(string name, Vector3 pos, float headingDeg, Color? party = null)
    {
        if (_lib.Manifest.Info(name) == null || _lib.Source.Find($"assets/models/{name}.glb") == null) { GD.Print($"     ({name}: not in the data)"); return null; }
        var m = _lib.Instantiate(name, party)!;
        AddChild(m);
        m.Position = pos;
        m.Rotation = new Vector3(0, Mathf.DegToRad(headingDeg), 0);
        _temp.Add(m);
        return m;
    }

    void Clear() { foreach (var n in _temp) { RemoveChild(n); n.QueueFree(); } _temp.Clear(); }

    async Task Shot(string file)
    {
        await Frames(4);
        if (_shots == "") return;
        System.IO.Directory.CreateDirectory(_shots);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_shots, file));
        GD.Print("     shot " + file);
    }

    // the game camera: about 55 degrees down, from the south-east (renderer.js / RtsCamera defaults)
    void GameView(Vector3 at, float dist) => Look(at + new Vector3(0.35f, 1.45f, 1f).Normalized() * dist, at);

    async Task Shots()
    {
        var red = PartyColors.ById("red")!.Light; var blue = PartyColors.ById("blue")!.Light;
        // buildings of the tribes (whatever of them the data has; the test data: Norse, Dragon Clan, SEAS)
        if (_only == "seas") { await Seas(red, blue); return; }
        if (_only == "seasall") { await SeasAll(red, blue); return; }
        if (_only == "seasclose") { await SeasClose(red, blue); return; }
        if (_only == "seasunits") { await SeasAll(red, blue, SeasUnitModels, 5, 1.9f, "seas_units.png", 0); return; }
        if (_only == "seastents")
        {
            // the tent proposals of PW_SEAS_TENTS (SeasMarkings.TentChoice): seas_tents_<small><big>.png
            var (sm, bg) = SeasMarkings.TentChoice;
            await SeasAll(red, blue, new[] { "seas_small_tent", "seas_big_tent" }, 2, 1f, $"seas_tents_{(char)('A' + sm)}{(char)('A' + bg)}.png", 2);
            return;
        }
        if (_only == "seasmirage") { await SeasAll(red, blue, SeasMirageModels, 5, 1.9f, "seas_mirage.png", 12); return; }
        if (_only == "seasbldg") { await SeasAll(red, blue, SeasBuildingModels, 5, 1.9f, "seas_buildings.png", SeasBuildingModels.Length); return; }
        if (_only != "units") await Buildings(red, blue);
        if (_only != "buildings") await Units(red, blue);
    }

    async Task Buildings(Color red, Color blue)
    {
        Place("hu_tavern", new Vector3(-30, 0, -6), 20, red)?.SetBuildingState(4, 0, 1);
        Place("hu_warehouse", new Vector3(-8, 0, -14), 30, red)?.SetBuildingState(4, 0, 1);
        Place("ninigi_temple", new Vector3(18, 0, -10), -20, blue);
        Place("ninigi_teahouse", new Vector3(-12, 0, 14), -30, blue);
        Place("seas_headquarters", new Vector3(22, 0, 18), 10, red);
        Place("hu_palisade", new Vector3(2, 0, 4), 0, red);
        GameView(new Vector3(0, 0, 2), 85);
        await Shot("buildings.png");
        GameView(new Vector3(-20, 0, 4), 45);
        await Shot("buildings_close.png");
        Clear();
    }

    async Task Seas(Color red, Color blue)
    {
        var all = new List<ModelInstance>();
        foreach (var (side, party) in new[] { (-1f, blue), (1f, red) })
        {
            float x0 = side * 15;
            void Add(ModelInstance? m) { if (m != null) all.Add(m); }
            var hq = Place("seas_headquarters", new Vector3(x0 - side * 4, 0, -16), 200, party); hq?.SetBuildingState(4, 0, 1); Add(hq);
            var tw = Place("seas_turret_tower", new Vector3(side * 5, 0, 0), 200, party); tw?.SetBuildingState(4, 0, 1); Add(tw);
            Add(Place("seas_carrier", new Vector3(x0 + side * 12, 0, 2), 180, party));
            float x = x0 - 4;
            foreach (var n in new[] { "seas_warrior_s1", "seas_flamethrower_s2", "seas_rider_b" })
            {
                var m = Place(n, new Vector3(x, 0, 14), 160, party);
                if (m?.Anim != null) { m.Play(m.Anim.Pick("standanim", "idle_0", "stand")); m.Advance(0.5); }
                Add(m); x += 3f;
            }
        }
        GameView(new Vector3(0, 2, 0), 72);
        ModelMaterials.SetOriginalColours(true);
        foreach (var m in all) m.SetPartyColor(m.Party);
        await Frames(4);
        var before = GetViewport().GetTexture().GetImage();
        ModelMaterials.SetOriginalColours(false);
        foreach (var m in all) m.SetPartyColor(m.Party);
        await Frames(4);
        var after = GetViewport().GetTexture().GetImage();
        int painted = 0;
        foreach (var m in all) foreach (var mi in m.FindChildren("*", "MeshInstance3D", true, false))
            for (int s = 0; s < ((MeshInstance3D)mi).GetSurfaceOverrideMaterialCount(); s++)
                if (((MeshInstance3D)mi).GetSurfaceOverrideMaterial(s) is ShaderMaterial sm && sm.Shader?.Code.Contains("seas_paint") == true) painted++;
        Check(painted > 0, $"SEAS paint on the blue player's models ({painted} surfaces)");
        if (_shots == "") { Clear(); return; }
        int w = before.GetWidth(), h = before.GetHeight();
        var both = Image.CreateEmpty(w, h * 2, false, before.GetFormat());
        both.BlitRect(before, new Rect2I(0, 0, w, h), Vector2I.Zero);
        both.BlitRect(after, new Rect2I(0, 0, w, h), new Vector2I(0, h));
        both.Resize(w * 3 / 4, h * 3 / 2);
        System.IO.Directory.CreateDirectory(_shots);
        both.SavePng(System.IO.Path.Combine(_shots, "seas_colours.png"));
        GD.Print("     shot seas_colours.png (top: original colours, bottom: player colours)");
        Clear();
    }

    // the SEAS models of --only=seasall (buildings, defences, vehicles, robots, ships, soldiers)
    static readonly string[] SeasAllModels =
    {
        "seas_headquarters", "seas_barracks", "seas_steelwork", "seas_greenhouse", "seas_garage", "seas_laboratory",
        "seas_gate", "seas_fence", "seas_turret", "seas_turret_tower", "seas_healspring", "seas_crane_01",
        "seas_small_tent", "seas_big_tent", "seas_hq_bunker", "seas_hq_defense_turret_top", "seas_hq_machinegun_nest_top", "seas_hq_big_cannon_cannon",
        "seas_carrier", "seas_carrier_turret", "seas_submarine", "seas_hovercraft", "seas_helicopter", "seas_wehrspinne",
        "seas_wehrspinne_top", "seas_mechanical_walker", "seas_mobile_suit", "seas_mobile_suit_flamethrower", "seas_lumberjack", "seas_lumberjack_minigun",
        "seas_triceratops", "seas_worker_s1", "seas_warrior_s1", "seas_gunner_s2", "seas_rocketman_s2", "seas_flamethrower_s2",
        "seas_marksman_s2", "seas_medic_s3", "seas_rider_a", "seas_rider_b",
    };

    // one cell each: the largest horizontal extent at 80 % of the cell (a soldier as large as the headquarters)
    // (a skinned mesh's vertices are in the model's frame - its node's own transform, e.g. an armature scale, does not
    // apply to them; the box is then in the GSF frame, z up, and in the bind pose: its longest side, shortened)
    static void FitCell(ModelInstance m, float cell)
    {
        var box = new Aabb(); bool any = false, skinned = false;
        var inv = m.GlobalTransform.AffineInverse();
        foreach (var c in m.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)c;
            var b = mi.Skin != null ? mi.GetAabb() : inv * mi.GlobalTransform * mi.GetAabb();
            box = any ? box.Merge(b) : b; any = true;
            skinned |= mi.Skin != null;
        }
        float ext = skinned ? Math.Max(box.Size.X, Math.Max(box.Size.Y, box.Size.Z)) * 0.6f : Math.Max(box.Size.X, box.Size.Z);
        if (any && ext > 0.01f) { float k = cell * 0.8f / ext; m.Scale = Vector3.One * k; if (!skinned) m.Position -= new Vector3(0, box.Position.Y * k, 0); }
    }

    // --only=seasclose [--models=a,b,..]: a few SEAS models close up, one row each: original colours, blue, red
    async Task SeasClose(Color red, Color blue)
    {
        string[] names = { "seas_hq_bunker", "seas_hq_defense_turret_top", "seas_turret_tower", "seas_lumberjack" };
        // (--gap=<factor>: the cells further apart - tall robots and the widow's legs reach into the next row)
        float gap = 1;
        string? setting = null; string cols = "blue | red";
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--models=")) names = a.Substring(9).Split(',');
            if (a.StartsWith("--gap=")) float.TryParse(a.Substring(6), System.Globalization.CultureInfo.InvariantCulture, out gap);
            if (a.StartsWith("--setting=")) setting = a.Substring(10);
            if (a.StartsWith("--colors=") && a.Substring(9).Split(',') is { Length: 2 } cs
                && PartyColors.ById(cs[0]) is { } c0 && PartyColors.ById(cs[1]) is { } c1) { blue = c0.Light; red = c1.Light; cols = cs[0] + " | " + cs[1]; }
        }
        const float CELL = 10f;
        for (int i = 0; i < names.Length; i++)
            for (int k = 0; k < 3; k++)
            {
                var pos = new Vector3((k - 1) * CELL * 1.1f * gap, 0, (i - (names.Length - 1) / 2f) * CELL * 1.1f * gap);
                var m = Place(names[i], pos, 200, k == 0 ? null : k == 1 ? blue : red);
                if (m == null) continue;
                if (m.Anim != null) { m.Play(m.Anim.Pick("standanim", "idle_0", "stand")); m.Advance(0.5); }
                m.SetBuildingState(4, 0, 1);
                FitCell(m, CELL);
                if (setting != null) m.SetSetting(setting);
                m.SetPartyColor(k == 0 ? null : k == 1 ? blue : red);
                if (k == 0)
                {
                    var l = new Label3D { Text = names[i].Substring(5) + $"   (original | {cols}){(setting != null ? "  " + setting : "")}", PixelSize = 0.02f, FontSize = 40, OutlineSize = 10, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Position = pos + new Vector3(-CELL * 0.5f, 0.2f, CELL * 0.5f) };
                    AddChild(l); _temp.Add(l);
                }
            }
        var cam = _r.Camera;
        var (proj, size) = (cam.Projection, cam.Size);
        cam.Projection = Camera3D.ProjectionType.Orthogonal; cam.Size = names.Length * CELL * 1.15f * gap;
        GameView(Vector3.Zero, 300);
        await Shot("seas_close.png");
        cam.Projection = proj; cam.Size = size;
        Clear();
    }

    // the SEAS units, vehicles, robots and ships of --only=seasunits (seas_units.png, labels above the pairs)
    static readonly string[] SeasUnitModels =
    {
        "seas_submarine", "seas_hovercraft", "seas_helicopter", "seas_wehrspinne", "seas_wehrspinne_top",
        "seas_mechanical_walker", "seas_mobile_suit", "seas_mobile_suit_flamethrower", "seas_lumberjack", "seas_lumberjack_minigun",
        "seas_triceratops", "seas_worker_s1", "seas_warrior_s1", "seas_gunner_s2", "seas_rocketman_s2",
        "seas_flamethrower_s2", "seas_marksman_s2", "seas_medic_s3", "seas_rider_a", "seas_rider_b",
    };

    // MIRAGE's SEAS of --only=seasmirage (the classes only the MIRAGE configuration adds; the first 12 buildings),
    // run with the MIRAGE data (--data=<export>@MIRAGE)
    static readonly string[] SeasMirageModels =
    {
        "scorpion_controller", "seq_gate_machine_extended", "hs_warpgate", "hs_kennel", "hs_re_enforced_wall",
        "seas_hq_speaker", "plane_parachute_box_ingame", "seas_hq_house01", "seas_hq_house04", "seas_hq_house_addon_30",
        "seas_endboss_gate", "flying_trader_destroyed",
        "babbagx_mobile_suit", "ninigx_marksman_x3", "pirates_ninigx_archer_x1", "pirates_ninigx_spearman_x2", "scorpion",
        "flyingtrader", "seas_triceratops_transporter", "seas_triceratops_resource_collector", "macrolemyd_water", "mosasaurus",
        "muraenosaurus", "carnotaurus", "eusmilus", "gigantosaurus", "arena_tred",
        "polakanthus", "panoplosaurus", "stygimoloch",
    };

    // the SEAS buildings of --only=seasbldg (everything painted with the buildings' markings, Scope.Buildings)
    static readonly string[] SeasBuildingModels =
    {
        "seas_headquarters", "seas_barracks", "seas_steelwork", "seas_greenhouse", "seas_garage",
        "seas_laboratory", "seas_gate", "seas_fence", "seas_turret", "seas_turret_tower",
        "seas_healspring", "seas_crane_01", "seas_small_tent", "seas_big_tent", "seas_hq_bunker",
        "seas_hq_defense_turret_top", "seas_hq_machinegun_nest_top", "seas_hq_big_cannon", "seas_carrier", "seas_carrier_turret",
    };

    // models: blue and red copy side by side, PAIRS pairs a row; rowGap: row distance in cells, the label in the gap
    // above its pair (> 1.5: the labels clear of the models)
    async Task SeasAll(Color red, Color blue, string[]? models = null, int PAIRS = 6, float rowGap = 1f, string file = "seas_all.png", int buildings = 18)
    {
        models ??= SeasAllModels;
        const float CELL = 10f;
        var all = new List<ModelInstance>();
        int rows = (models.Length + PAIRS - 1) / PAIRS;
        for (int i = 0; i < models.Length; i++)
        {
            string n = models[i];
            int col = i % PAIRS, row = i / PAIRS;
            for (int side = 0; side < 2; side++)
            {
                var pos = new Vector3((col * 2 + side - PAIRS + 0.5f) * CELL, 0, (row - rows / 2f + 0.5f) * CELL * rowGap);
                var m = Place(n, pos, 200, side == 0 ? blue : red);
                if (m == null) continue;
                if (m.Anim != null) { m.Play(m.Anim.Pick("standanim", "idle_0", "stand")); m.Advance(0.5); }
                if (i < buildings) m.SetBuildingState(4, 0, 1);
                // an owned animal as in the game: its saddle and party parts shown, those tinted (EntityViews)
                if (m.Template.Fourcc is "Anim" or "Vehi")
                {
                    m.SetHideMask(PartBits.AnimalMask(new AnimalLook { Owned = true }));
                    m.SetAnimalTint(side == 0 ? blue : red);
                }
                FitCell(m, CELL);
                all.Add(m);
                if (side == 0)
                {
                    var l = new Label3D { Text = n.StartsWith("seas_") ? n.Substring(5) : n, PixelSize = 0.025f, FontSize = 48, OutlineSize = 12, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Position = pos + new Vector3(CELL / 2, 0.2f, rowGap > 1.5f ? -CELL * 1.15f : CELL * 0.5f) };
                    AddChild(l); _temp.Add(l);
                }
            }
        }
        var cam = _r.Camera;
        var (proj, size) = (cam.Projection, cam.Size);
        cam.Projection = Camera3D.ProjectionType.Orthogonal; cam.Size = rows * CELL * (rowGap > 1.5f ? 1.15f : 0.98f) * rowGap;
        // (wide enough for a row too - few rows: the tents)
        var vs = GetViewport().GetVisibleRect().Size;
        cam.Size = Math.Max(cam.Size, PAIRS * 2 * CELL * 1.05f * vs.Y / vs.X);
        GameView(new Vector3(0, 0, 0), 300);
        ModelMaterials.SetOriginalColours(true);
        foreach (var m in all) m.SetPartyColor(m.Party);
        await Frames(6);
        var before = GetViewport().GetTexture().GetImage();
        ModelMaterials.SetOriginalColours(false);
        foreach (var m in all) m.SetPartyColor(m.Party);
        await Frames(6);
        var after = GetViewport().GetTexture().GetImage();
        cam.Projection = proj; cam.Size = size;
        Check(all.Count > 0, $"SEAS models placed ({all.Count / 2} of {models.Length})");
        if (_shots != "")
        {
            int w = before.GetWidth(), h = before.GetHeight();
            var both = Image.CreateEmpty(w, h * 2, false, before.GetFormat());
            both.BlitRect(before, new Rect2I(0, 0, w, h), Vector2I.Zero);
            both.BlitRect(after, new Rect2I(0, 0, w, h), new Vector2I(0, h));
            System.IO.Directory.CreateDirectory(_shots);
            both.SavePng(System.IO.Path.Combine(_shots, file));
            GD.Print($"     shot {file} (top: original colours, bottom: player colours; blue left, red right of each pair)");
        }
        Clear();
    }

    async Task Units(Color red, Color blue)
    {
        // units and animals (cloth, fur fringes, feathers are double-sided too)
        float x = -12;
        foreach (var (n, party) in new (string, Color?)[] { ("aje_warrior_s2", red), ("aje_archer_s2", red), ("hu_warrior_s1", blue), ("ninigi_warrior_s2", blue), ("seas_warrior_s1", red), ("bela_s1", red), ("stina_s1", blue) })
        {
            var m = Place(n, new Vector3(x, 0, 6), 160, party);
            if (m?.Anim != null) { m.Play(m.Anim.Pick("standanim", "idle_0", "stand")); m.Advance(0.5); }
            x += 3.5f;
        }
        Place("triceratops", new Vector3(-8, 0, -6), 140);
        Place("allosaurus", new Vector3(6, 0, -6), 200);
        Place("mammoth", new Vector3(18, 0, -2), 220);
        GameView(new Vector3(0, 1, 2), 32);
        await Shot("units.png");
        Clear();
    }
}
