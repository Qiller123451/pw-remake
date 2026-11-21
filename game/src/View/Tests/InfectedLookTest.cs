// The infected look (docs/INFECTED_LOOK.md): the three Norse buildings that matter most (the headquarters
// hu_fireplace, the arena, the temple) and five wild animals (velociraptor, smilodon, triceratops, allosaurus,
// mammoth) side by side - normal | stage 2 | stage 3 (fully overrun) - from the game's camera (RtsCamera at its default
// zoom, 70 m), by day and by night, and a closer look. Checks: the infected materials are on, the animals draw with
// their bind positions (CUSTOM0), the fires turn green, the effects are made and go again at stage 0.
//
// And (Kacper 23:45 "also add other buildings units too") every Norse building of both configurations with its ruins
// and construction stages (--only=norse_buildings), the Norse units - characters, heroes, riders on their mounts, the
// undead, statues, vehicles and ships (--only=norse_units: the game's camera and a close-up of each) - and every wild
// animal (--only=animals_all): pairs normal | stage 3, each cropped to the pair (infected_<group>_<model>.png), put
// together into docs/images/infected_norse_buildings.jpg, infected_norse_units.jpg (+ _close), infected_animals_all.jpg.
//
// Run (rendering on llvmpipe in the container):
//   xvfb-run -a -s "-screen 0 1600x900x24" godot --path game res://src/View/Tests/InfectedLookTest.tscn -- --shots=<dir> [--only=buildings|animals|close|stages|eyes|norse_buildings|norse_units|animals_all] [--markers]
//   (add --rendering-driver opengl3 before res:// for the Compatibility renderer)
// Screenshots (each row: normal | stage 2 | stage 3): <dir>/infected_<group>_<day|night>_<model>.png and, put together
// by tools in the docs, docs/images/infected_*.jpg. --party=<colour id> / --damage=<0..2>: the close look's player
// colour and destruction level (the Outbreak's infected are green; a building under attack). --markers: small spheres at the animals' wound centres (to place
// the wound table). Exit code 0 = all checks passed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.View.Fx;
using PwRemake.View.Materials;
using PwRemake.View.Models;
using PwRemake.View.Tools;
using PwRemake.View.World;

namespace PwRemake.View.Tests;

public partial class InfectedLookTest : Node3D
{
    int _fails, _checks;
    string _shots = "", _only = "";
    bool _markers;
    string _party = "red";     // --party=<colour id>: the close look's player colour (the Outbreak's infected: green)
    int _damage;               // --damage=<0..2>: the close look's destruction level (a building under attack)
    string[]? _pick;    // --pick=a,b: only the entries containing one of these (Pairs)
    ModelLibrary _lib = null!;
    WorldRenderer _r = null!;
    Particles _p = null!;
    Vector3 _target;
    readonly List<Node3D> _temp = new();
    readonly List<ModelInstance> _anims = new();
    CanvasLayer _labels = null!;
    const float DT = 1 / 30f;

    public static readonly string[] BUILDINGS = { "hu_fireplace", "hu_arena", "hu_temple" };
    public static readonly string[] ANIMALS = { "velociraptor", "smilodon", "allosaurus", "triceratops", "mammoth" };
    static readonly int[] COLUMNS = { 0, 2, 3 };

    // the Norse buildings of both tech trees (base + MIRAGE: classgfx of the Hu BLDG classes), their ruins (_dest),
    // upgrades and attached parts; name@level: a construction stage (SetBuildingState level 0..3)
    public static readonly string[] NORSE_BUILDINGS =
    {
        "hu_fireplace", "hu_lumberjack_cottage", "hu_stone_quarry", "hu_stone_cottage", "hu_small_animal_farm", "hu_corn_field",
        "hu_warehouse", "hu_tavern", "hu_temple", "hu_arena", "hu_kennel", "hu_weapons_smith", "hu_machine_maker",
        "hu_magic_cauldron", "hu_harbour", "hu_bunker", "hu_bunker_upgrade", "hu_small_tower", "hu_large_tower",
        "hu_large_tower_upgrade", "hu_palisade", "hu_palisade_gate", "hu_small_wall",
        "hu_small_wall_gate", "hu_re_enforced_wall", "hu_re_enforced_wall_gate", "hu_ladder", "hu_rally_point",
        "hu_rally_point_harbour", "hu_warpgate",
        // MIRAGE's (launch pads, the mine, the special box, the big horn, the mud pit, the punching bag: other models)
        "hs_large_tower", "flying_trader_destroyed", "tutorial_vikingpark_mainentrance", "plane_parachute_box_ingame", "horn",
        "hs_mud_pit", "tutorial_vikingpark_fairground_ride_02", "tutorial_vikingpark_gate",
        // construction stages and ruins
        "hu_fireplace@1", "hu_stone_cottage@2", "hu_large_tower@2", "hu_fireplace_dest", "hu_temple_dest", "hu_tavern_dest",
        "hu_stone_cottage_dest", "hu_large_tower_dest", "hu_small_wall_gate_dest", "hu_warehouse_dest",
    };

    // the Norse units (both tech trees: Hu CHTR / ANML / VHCL / SHIP, the Norse heroes Bela and Stina): one level each
    // (a higher one where it looks different); "mount+rider": a rider at the mount's "Ride" link; "char+weapon:link"
    public static readonly string[] NORSE_UNITS =
    {
        "hu_worker_s1", "hu_warrior_s1+hu_axe_a:HndR", "hu_warrior_s4", "hu_spearman_s1", "hu_pikeman_s2", "hu_archer_s2",
        "hu_marksman_s2", "hu_druid_s3", "hu_jetpack_warrior_s2", "hu_berserker_s3", "hu_killer_s3", "seas_flamethrower_s2",
        "bela_s0", "stina_s0", "stina_eusmilus_s1", "hu_undead_warrior", "hu_undead_killer", "megaloceros+hu_rider_a:Ride",
        "woolly_rhino+hu_rider_b:Ride", "mammoth", "hu_berserk_statue_01", "hu_steam_tank", "hu_steam_boat", "flyingtrader",
        "hu_cart", "hu_mobile_suit", "hu_steam_ram", "hu_dragon_boat", "hu_transport_ship", "hu_fishing_boat", "hu_ram_ship",
    };

    // the wild animals of both tech trees (World ANML classgfx) and the Norse mounts
    public static readonly string[] ANIMALS_ALL =
    {
        "achelousaurus", "allosaurud", "allosaurus", "amargasaurus", "ankylosaurus", "apatosaurus", "archaeopteryx",
        "atroxosaurus", "baryonyx", "bothriolepis", "brachiosaurus", "carcharodontosaurus", "carnotaurus",
        "corythosaurus", "deinonychus", "dilophosaurus", "diplodocus", "dunkleosteus", "edmontosaurus",
        "euoplocephalus", "eusmilus", "gallimimus", "gigantopithecus", "gigantosaurus", "henodus", "heterodontosaurus",
        "iguanodon", "kentrosaurus", "kronosaurus", "lambeosaurus", "lemur", "liopleurodon", "macrolemys_land",
        "maiasauria", "mammoth", "megaloceros", "mosasaurus", "muraenosaurud", "muraenosaurus", "oviraptor",
        "panoplosaurus", "parasaurolophus", "pentaceratops", "placohelys", "polakanthus", "psittacosaurus", "pteranodon",
        "saltasaurus", "sloth", "smilodon", "spinosaurus", "stegosaurus", "stygimoloch", "styracosaurus", "tarbosaurus",
        "titan_triceratops", "trex", "triceratops", "tsintaosaurus", "velociraptor", "wild_boar", "woolly_rhino",
    };

    void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) { _fails++; GD.PrintErr("FAIL " + what); }
        else GD.Print("ok   " + what);
    }

    public override async void _Ready()
    {
        Log.AddSink(new GodotPrintSink());
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--shots=")) _shots = a.Substring(8);
            if (a.StartsWith("--only=")) _only = a.Substring(7);
            if (a == "--markers") _markers = true;
            if (a.StartsWith("--party=")) _party = a.Substring(8);
            if (a.StartsWith("--damage=")) _damage = int.Parse(a.Substring(9));
            if (a.StartsWith("--pick=")) _pick = a.Substring(7).Split(',');
        }
        try
        {
            _r = WorldRenderer.Create();
            AddChild(_r);
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(900, 900) }, Name = "Ground",
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.Color8(0x6a, 0x74, 0x42), DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert, SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled, Roughness = 1 },
            });
            _lib = new ModelLibrary(new AssetSource(ViewerStage.TestConfig()));
            _p = new Particles(_lib.Textures);
            AddChild(_p);
            _lib.OnInstance = _p.Track;
            _lib.OnEmitter = _p.TrackNode;
            _labels = new CanvasLayer();
            AddChild(_labels);
            await Logic();
            if (_only is "" or "buildings") await Rows(BUILDINGS, "buildings", 80, 36);
            if (_only is "" or "animals") await Rows(ANIMALS, "animals", 70, 0);
            if (_only is "" or "close") await Close();
            if (_only is "stages") await Stages();
            if (_only is "eyes") await Eyes();
            if (_only is "norse_buildings") await Pairs(NORSE_BUILDINGS, "norse_buildings", false);
            if (_only is "norse_units") { await Pairs(NORSE_UNITS, "norse_units", false); await Pairs(NORSE_UNITS, "norse_units_close", true); }
            if (_only is "animals_all") await Pairs(ANIMALS_ALL, "animals_all", false);
        }
        catch (Exception ex) { _fails++; GD.PrintErr("EXCEPTION " + ex); }
        GD.Print($"infected look tests: {_checks - _fails}/{_checks} passed");
        GetTree().Quit(_fails == 0 ? 0 : 1);
    }

    double _time;
    public override void _Process(double delta)
    {
        if (_p == null || _r == null) return;
        _time += DT;
        foreach (var a in _anims) if (GodotObject.IsInstanceValid(a)) a.Advance(DT);
        _p.Update(DT, _r.Camera, _target);
    }

    async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    void Look(Vector3 from, Vector3 at)
    {
        _target = at;
        _r.Camera.Position = from; _r.Camera.LookAt(at);
        _r.FollowSun(at);
    }

    // the game's camera (RtsCamera: yaw 0, pitch 0.72 + 0.36 x the zoom's share between 25 and 150 m)
    void GameView(Vector3 at, float dist)
    {
        double t = (dist - 25) / 125.0, pitch = 0.72 + 0.36 * t;
        Look(at + new Vector3(0, (float)Math.Sin(pitch), (float)Math.Cos(pitch)) * dist, at);
    }

    ModelInstance? Place(string name, Vector3 pos, float headingDeg, Color? party = null)
    {
        if (_lib.Manifest.Info(name) == null || _lib.Source.Find($"assets/models/{name}.glb") == null) { GD.Print($"     ({name}: not in the data)"); return null; }
        var m = _lib.Instantiate(name, party)!;
        AddChild(m);
        m.Position = pos;
        m.Rotation = new Vector3(0, Mathf.DegToRad(headingDeg), 0);
        _temp.Add(m);
        if (m.Template.Fourcc == "Anim") _anims.Add(m);
        return m;
    }

    void Clear()
    {
        foreach (var n in _temp) { RemoveChild(n); n.QueueFree(); }
        _temp.Clear(); _anims.Clear();
        _p.Clear();     // (the smoke of the models just freed: not into the next picture)
        foreach (var c in _labels.GetChildren()) { _labels.RemoveChild(c); c.QueueFree(); }
    }

    void Label(string text, Vector3 at)
    {
        var s = _r.Camera.UnprojectPosition(at);
        var l = new Label { Text = text, Position = s - new Vector2(text.Length * 5.5f, 0) };
        l.AddThemeFontSizeOverride("font_size", 20);
        l.AddThemeColorOverride("font_color", Colors.White);
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 6);
        _labels.AddChild(l);
    }

    async Task Shot(string file, int frames = 45, Rect2I? crop = null)
    {
        await Frames(frames);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (_shots == "") return;
        System.IO.Directory.CreateDirectory(_shots);
        var img = GetViewport().GetTexture().GetImage();
        if (crop is { } c) img = img.GetRegion(c.Intersection(new Rect2I(0, 0, img.GetWidth(), img.GetHeight())));
        img.SavePng(System.IO.Path.Combine(_shots, file));
        GD.Print("     shot " + file);
    }

    void SetNight(bool night)
    {
        _r.SetNight(night ? (0.22f, Color.FromHtml("8aa8ff"), 0.36f, Color.FromHtml("101828")) : null);
        foreach (var n in _temp) if (n is ModelInstance m && m.Template.Fourcc != "Anim") m.SetNight(night);
    }

    static string StageName(int s) => s switch { 0 => "normal", 1 => "stage 1 (fresh)", 2 => "stage 2 (spreading)", _ => "stage 3 (overrun)" };

    // ---- the logic: materials, meshes, effects, green fire, back to normal
    async Task Logic()
    {
        var b = Place("hu_fireplace", Vector3.Zero, 0, PartyColors.ById("red")!.Light);
        var a = Place("allosaurus", new Vector3(40, 0, 0), 0);
        if (b == null || a == null) { Check(false, "hu_fireplace and allosaurus in the data"); return; }
        b.SetBuildingState(4, 0, 1);
        await Frames(2);
        static IEnumerable<Material?> Overrides(ModelInstance m) => m.Parts.Select(p => p.node).OfType<MeshInstance3D>()
            .SelectMany(mi => Enumerable.Range(0, mi.GetSurfaceOverrideMaterialCount()).Select(mi.GetSurfaceOverrideMaterial));
        static bool IsInfected(Material? x) => x is ShaderMaterial s && s.Shader.Code.Contains("InfectedLook");
        Check(!Overrides(b).Any(IsInfected), "a healthy building has no infected material");
        b.SetInfected(2);
        Check(Overrides(b).Count(IsInfected) > 3, $"stage 2: the building's surfaces infected ({Overrides(b).Count(IsInfected)})");
        Check(b.GreenFire, "stage 2: the fireplace's fires are green");
        Check(b.InfectionFx.OfType<ModelInstance>().Count() == 3, $"stage 2: three poison clouds ({b.InfectionFx.OfType<ModelInstance>().Count()})");
        Check(b.InfectionFx.OfType<InfectionBlight>().Count() == 1, "stage 2: the blight");
        Check(_lib.Template("fx_poison_big", null, true) == null || b.InfectionFx.OfType<ModelInstance>().All(m => m.Template.Name == "fx_poison_big"), "stage 2: the clouds are the game's fx_poison_big");
        Check(InfectedLook.FliesEmitter(_lib) != null, "the original's flies emitter found (hu_lumberjack_cottage)");
        Check(b.InfectionFx.Any(n => n.Name.ToString().StartsWith("infection_flies")), "stage 2: flies about the building");
        b.SetInfected(1);
        Check(!b.GreenFire && b.InfectionFx.OfType<ModelInstance>().Count() == 1, "stage 1: fires as they are, one cloud");
        a.SetInfected(3);
        var skinned = a.Parts.Select(p => p.node).OfType<MeshInstance3D>().Where(mi => mi.Skin != null && mi.Mesh is ArrayMesh).ToList();
        Check(skinned.Count > 0 && skinned.All(mi => (((ArrayMesh)mi.Mesh).SurfaceGetFormat(0) & Mesh.ArrayFormat.FormatCustom0) != 0),
            $"stage 3: the animal's skinned meshes carry their bind positions ({skinned.Count})");
        Check(Overrides(a).Any(m => m is ShaderMaterial s && s.Shader.Code.Contains("wound_c")), "stage 3: the animal's wound material");
        a.SetInfected(0); b.SetInfected(0);
        Check(!Overrides(b).Any(IsInfected) && !Overrides(a).Any(IsInfected), "stage 0: the normal materials again");
        Check(b.InfectionFx.Count == 0 && !b.GreenFire, "stage 0: no effects, fires as they are");
        Check(a.Parts.Select(p => p.node).OfType<MeshInstance3D>().Where(mi => mi.Skin != null && mi.Mesh is ArrayMesh)
            .All(mi => (((ArrayMesh)mi.Mesh).SurfaceGetFormat(0) & Mesh.ArrayFormat.FormatCustom0) == 0), "stage 0: the animal's own meshes again");
        // ---- the Norse units: characters (their weapons: items), riders on their mounts, passengers, the undead, vehicles
        static bool Rest(ModelInstance m) => m.Parts.Select(p => p.node).OfType<MeshInstance3D>().Any(mi => mi.Mesh is ArrayMesh am && am.GetSurfaceCount() > 0 && (am.SurfaceGetFormat(0) & Mesh.ArrayFormat.FormatCustom0) != 0);
        static IEnumerable<ShaderMaterial> Inf(ModelInstance m) => Overrides(m).OfType<ShaderMaterial>().Where(x => x.Shader.Code.Contains("InfectedLook"));
        var u = Place("hu_warrior_s1", new Vector3(-40, 0, 0), 0);
        if (u != null)
        {
            var axe = u.Attach("hu_axe_a", "HndR");
            u.SetInfected(2);
            Check(u.InfectionKind == LookKind.Character && Inf(u).Any(x => x.Shader.Code.Contains("infected character")), "a warrior: the character look");
            Check(Rest(u), "a warrior draws from its bind positions");
            Check(Inf(u).Any(x => x.GetShaderParameter("eyes").AsVector4().W > 0 && x.GetShaderParameter("wound_n").AsInt32() == 3), "a warrior at stage 2: green eyes, 3 wounds");
            var w = InfectedLook.WoundsOf(u.Template, LookKind.Character);
            Check(w != null && w.Wounds.Length == 5 && w.Eyes != null, "the warrior's wounds from the Norse skeleton (5, eyes)");
            if (axe != null) Check(axe.Infection == 2 && axe.InfectionKind == LookKind.Item && !Rest(axe), "the warrior's axe: an item (rot only)");
            u.SetInfected(0);
            Check(!Inf(u).Any() && !Rest(u) && (axe == null || axe.Infection == 0), "the warrior healthy again");
        }
        var mount = Place("megaloceros", new Vector3(-60, 0, 0), 0);
        var rider = mount?.Attach("hu_rider_a", "Ride");
        if (mount != null && rider != null)
        {
            mount.SetInfected(3);
            Check(mount.InfectionKind == LookKind.Animal && rider.Infection == 3 && rider.InfectionKind == LookKind.Character, "a mount's rider follows it (the character look)");
            var later = mount.Attach("hu_rider_b", "Ride");
            Check(later == null || later.Infection == 3, "a rider attached later is infected at once");
            var pax = mount.Attach("hu_worker_s1", "Ride");
            pax?.MarkPassenger();
            mount.SetInfected(2);
            Check(pax == null || pax.Infection == 0, "a passenger keeps its own look");
        }
        else Check(false, "megaloceros with hu_rider_a at Ride in the data");
        var ud = Place("hu_undead_warrior", new Vector3(-80, 0, 0), 0);
        if (ud != null)
        {
            ud.SetInfected(1);
            Check(ud.InfectionKind == LookKind.Undead && Inf(ud).All(x => x.GetShaderParameter("wound_n").AsInt32() == 0 && x.GetShaderParameter("infect_skin").AsSingle() == 0)
                && Inf(ud).Any(x => x.GetShaderParameter("eyes").AsVector4().W > 0), "the undead: no second rot, no wounds, green eyes from stage 1");
        }
        var v = Place("hu_steam_tank", new Vector3(-100, 0, 0), 0);
        if (v != null)
        {
            v.SetInfected(3);
            Check(v.InfectionKind == LookKind.Vehicle && Rest(v) && Inf(v).Any(x => x.Shader.Code.Contains("infected vehicle")), "a vehicle: the building look from its bind positions");
            Check(!v.InfectionFx.OfType<InfectionBlight>().Any() && v.InfectionFx.OfType<ModelInstance>().Count() == 2, "a vehicle: no blight, two clouds");
        }
        var wall = Place("hu_palisade_gate", new Vector3(-120, 0, 0), 0);
        if (wall != null)
        {
            wall.SetBuildingState(4, 0, 1);
            wall.SetInfected(3);
            var bl = wall.InfectionFx.OfType<InfectionBlight>().FirstOrDefault();
            Check(bl != null && Mathf.Abs(bl.RadiusX - bl.RadiusZ) > 0.5f, $"a palisade gate: the blight along the wall ({bl?.RadiusX:0.0} x {bl?.RadiusZ:0.0} m)");
            Check(wall.InfectionFx.OfType<ModelInstance>().All(m => m.Template.Name == "fx_poison") && wall.InfectionFx.OfType<ModelInstance>().Count() == 2, "a palisade gate: two small clouds");
        }
        Check(InfectedLook.TurnsGreen("hu_fireplace/particles_DEST_02_feuer_Gross_01") && InfectedLook.TurnsGreen("hu_weapons_smith/particles_hu_waffenmacher_schornstein_qualm")
            && !InfectedLook.TurnsGreen("hu_lumberjack_cottage/particles_aje_Schlachthaus_lecker_Fleischfliegen_01") && !InfectedLook.TurnsGreen("hu_arena/particles_fx_dust_01")
            && !InfectedLook.TurnsGreen("hu_harbour/particles_wasser_01"), "fires and smoke turn green; flies, dust and the harbour's water do not");
        Clear();
        await Frames(2);
    }

    // ---- rows: each model normal | stage 2 | stage 3 at the game's camera, by day and by night
    async Task Rows(string[] models, string group, float dist, float gap)
    {
        foreach (var night in new[] { false, true })
            foreach (var name in models)
            {
                if (_lib.Template(name, null, true) is not { } t) { GD.Print($"     ({name}: not in the data)"); continue; }
                float w = gap > 0 ? gap : Mathf.Clamp(Mathf.Max(t.Bounds.Size.X, t.Bounds.Size.Z) * 1.25f, 12f, 20f);
                var row = new List<ModelInstance>();
                for (int i = 0; i < 3; i++)
                {
                    // (animals turned three-quarter to the camera, their left flank towards it; buildings as placed)
                    var m = Place(name, new Vector3((i - 1) * w, 0, 0), t.Fourcc == "Anim" ? 120 : 0, PartyColors.ById("red")!.Light);
                    if (m == null) continue;
                    if (m.Template.Fourcc != "Anim") m.SetBuildingState(4, 0, 1, night);
                    else if (m.Anim != null) m.Play(m.Anim.Pick("standanim", "idle_0", "stand", "walk_1"));
                    m.SetInfected(COLUMNS[i]);
                    row.Add(m);
                    if (_markers) Markers(m);
                }
                SetNight(night);
                float h = t.Bounds.Size.Y;
                GameView(new Vector3(0, h * 0.3f, 0), dist);
                await Frames(2);
                for (int i = 0; i < row.Count; i++) Label(StageName(COLUMNS[i]), row[i].Position + new Vector3(0, h + 1.5f, 0));
                await Shot($"infected_{group}_{(night ? "night" : "day")}_{name}.png", 60);
                Clear();
                SetNight(false);
            }
    }

    // ---- a closer look: the fireplace and a few animals fully overrun, a lower camera
    async Task Close()
    {
        foreach (var night in new[] { false, true })
        {
            var b = Place("hu_fireplace", new Vector3(0, 0, 0), 0, PartyColors.ById(_party)!.Light);
            b?.SetBuildingState(4, _damage, 1, night);
            b?.SetInfected(3);
            SetNight(night);
            Look(new Vector3(-6, 14, 32), new Vector3(0, 5, 0));
            await Shot($"infected_close_{(night ? "night" : "day")}_hu_fireplace.png", 60);
            Clear();
            SetNight(false);
        }
        foreach (var name in ANIMALS)
        {
            if (_lib.Template(name, null, true) is not { } t) continue;
            float s = Mathf.Max(t.Bounds.Size.X, Mathf.Max(t.Bounds.Size.Y, t.Bounds.Size.Z));
            var a = Place(name, new Vector3(-s * 0.55f, 0, 0), 120);
            var c = Place(name, new Vector3(s * 0.55f, 0, 0), 300);
            foreach (var m in new[] { a, c })
            {
                if (m == null) continue;
                if (m.Anim != null) m.Play(m.Anim.Pick("walk_1", "standanim"));
                m.SetInfected(3);
                if (_markers) Markers(m);
            }
            Look(new Vector3(0, s * 0.9f, s * 1.9f), new Vector3(0, t.Bounds.Size.Y * 0.45f, 0));
            await Shot($"infected_close_{name}.png", 40);
            Clear();
        }
    }

    // ---- the stages of the headquarters one after the other (0 .. 3)
    async Task Stages()
    {
        for (int s = 0; s <= 3; s++)
        {
            var b = Place("hu_fireplace", new Vector3((s - 1.5f) * 26, 0, 0), 0, PartyColors.ById("red")!.Light);
            b?.SetBuildingState(4, 0, 1);
            b?.SetInfected(s);
        }
        GameView(new Vector3(0, 4, 0), 95);
        await Frames(2);
        for (int s = 0; s <= 3; s++) Label(StageName(s), new Vector3((s - 1.5f) * 26, 20, 0));
        await Shot("infected_stages_hu_fireplace.png", 60);
        Clear();
    }

    // ---- pairs: each model normal | stage 3 from the game's camera (close: a closer look), cropped to the pair.
    // Entries: "model", "model@level" (a construction stage), "model+part:link" (a rider on a mount, a weapon in a hand)
    async Task Pairs(string[] list, string group, bool close)
    {
        foreach (var entry in list)
        {
            if (_pick != null && !_pick.Any(k => entry.Contains(k))) continue;
            string spec = entry, file = entry.Replace("@", "_lvl").Replace("+", "_").Replace(":", "_");
            int level = 4;
            string? part = null, link = null;
            int at = spec.IndexOf('@');
            if (at > 0) { level = int.Parse(spec.Substring(at + 1)); spec = spec.Substring(0, at); }
            int plus = spec.IndexOf('+');
            if (plus > 0) { var c = spec.Substring(plus + 1).Split(':'); spec = spec.Substring(0, plus); part = c[0]; link = c.Length > 1 ? c[1] : null; }
            if (_lib.Template(spec, null, true) is not { } t) { GD.Print($"     ({spec}: not in the data)"); continue; }
            var b = t.Bounds;
            bool body = t.Fourcc is "Anim" or "Char";
            float size = Mathf.Max(b.Size.X, b.Size.Z), w = Mathf.Max(size * (body ? 1.15f : 1.2f), body ? 4.5f : 8f);
            var row = new List<ModelInstance>();
            for (int i = 0; i < 2; i++)
            {
                // (animals and characters turned three-quarter to the camera; buildings as placed)
                var m = Place(spec, new Vector3((i - 0.5f) * w - b.GetCenter().X, 0, -b.GetCenter().Z), body ? 150 : t.Fourcc is "Vehi" or "Ship" or "Misc" ? 120 : 0, PartyColors.ById("red")!.Light);
                if (m == null) continue;
                if (!body) m.SetBuildingState(level, 0, 1);
                if (m.Anim != null) m.Play(m.Anim.Pick("standanim", "idle_0", "stand", "walk_1"));
                if (part != null && m.Attach(part, link, PartyColors.ById("red")!.Light) is { } pt && pt.Anim != null)
                    pt.Anim.Play(pt.Anim.Pick("ride_idle_0", "standanim", "idle_0"));
                m.SetInfected(i == 0 ? 0 : 3);
                row.Add(m);
                if (_markers) Markers(m);
            }
            if (row.Count == 0) { Clear(); continue; }
            float h = b.Size.Y;
            var centre = new Vector3(0, h * 0.4f, 0);
            if (close) Look(centre + new Vector3(0.25f, 0.5f, 1f).Normalized() * Mathf.Max(w * 1.9f, h * 2.1f), centre);
            else GameView(centre, body && h < 8 ? 60 : 80);
            await Frames(2);
            // the pair's rectangle on the screen (the boxes' corners) and some room for the labels
            var r = new Rect2();
            bool first = true;
            foreach (var m in row)
                for (int k = 0; k < 8; k++)
                {
                    var p = _r.Camera.UnprojectPosition(m.GlobalTransform * b.GetEndpoint(k));
                    if (first) { r = new Rect2(p, Vector2.Zero); first = false; } else r = r.Expand(p);
                }
            // (no labels: the sheet names the columns - left normal, right stage 3)
            r = r.Grow(24);
            float minW = close ? 520 : 300, minH = close ? 380 : 170;
            if (r.Size.X < minW) r = r.GrowIndividual((minW - r.Size.X) / 2, 0, (minW - r.Size.X) / 2, 0);
            if (r.Size.Y < minH) r = r.GrowIndividual(0, (minH - r.Size.Y) / 2, 0, (minH - r.Size.Y) / 2);
            await Shot($"infected_{group}_{file}.png", 32, new Rect2I((Vector2I)r.Position, (Vector2I)r.Size));
            Clear();
        }
    }

    // ---- the heads from the side and the front, orthographic (to place the eyes of the wound table): side view screen
    // x = GSF y, screen y = GSF z; front view screen x = -GSF x
    async Task Eyes()
    {
        foreach (var a in InfectedLookData.Table)
        {
            if (a.Eyes is not { } e) continue;
            var m = Place(a.Model, Vector3.Zero, 0);
            if (m == null) continue;
            if (_markers) Markers(m);
            _r.Camera.Projection = Camera3D.ProjectionType.Orthogonal;
            _r.Camera.Size = (float)Math.Max(e.R * 16, 1.4);
            // (GSF (x, y, z) -> the instance's (-x, z, -y))
            var at = new Vector3((float)-e.Cx, (float)e.Z, (float)-e.Y);
            Look(at + new Vector3(40, 0, 0), at);
            await Shot($"infected_eyes_{a.Model}_side.png", 8);
            Look(at + new Vector3(0, 0, -40), at);
            await Shot($"infected_eyes_{a.Model}_front.png", 4);
            Clear();
        }
        _r.Camera.Projection = Camera3D.ProjectionType.Perspective;
    }

    // small spheres at the wound centres and the eyes (the table's GSF frame -> the instance's: (-x, z, -y))
    void Markers(ModelInstance m)
    {
        var a = InfectedLook.WoundsOf(m.Template, m.InfectionKind) ?? InfectedLook.WoundsOf(m.Template, InfectedLook.KindOf(m.Template));
        if (a == null) return;
        void Dot(double x, double y, double z, Color col)
        {
            var s = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = col, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true } };
            s.Position = new Vector3((float)-x, (float)z, (float)-y);
            m.AddChild(s);
        }
        foreach (var w in a.Wounds) Dot(w.X, w.Y, w.Z, Colors.Magenta);
        if (a.Eyes is { } e) { Dot(e.Cx + e.Dx, e.Y, e.Z, Colors.Cyan); Dot(e.Cx - e.Dx, e.Y, e.Z, Colors.Cyan); }
    }
}
