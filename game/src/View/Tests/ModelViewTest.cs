// View tests on the real converted models: the part flag logic, loading every model of the test set, the look of
// units, buildings (construction stages, damage, epochs), attached parts (riders, turrets, flags), party colours,
// animations; screenshots for docs/VIEW.md and performance numbers.
//
// Run (rendering on llvmpipe in the container):
//   xvfb-run -a -s "-screen 0 1600x900x24" godot --path game res://src/View/Tests/ModelViewTest.tscn -- --shots=<dir> [--data=<folder>]
// Exit code 0 = all checks passed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.View.Anim;
using PwRemake.View.Compose;
using PwRemake.View.Materials;
using PwRemake.View.Models;
using PwRemake.View.Parts;
using PwRemake.View.Tools;

namespace PwRemake.View.Tests;

public partial class ModelViewTest : Node3D
{
    int _fails, _checks;
    string _shots = "";
    ModelLibrary _lib = null!;
    Camera3D _cam = null!;
    readonly List<Node> _temp = new();

    static readonly string[] TestSet =
    {
        "hu_warrior_s1", "hu_worker_s1", "stina_s1", "seas_warrior_s1", "mammoth", "allosaurus", "seas_wehrspinne",
        "hu_small_tower", "jng_waterfall_01", "ninigi_gatling_arrow", "hu_warehouse", "seas_turret_tower", "ninigi_temple", "ninigi_teahouse", "jungle_tree_01",
        "hu_rider_b", "seas_rider_b", "hu_animal_flag_01", "seas_wehrspinne_top", "seas_turret", "hu_crane_01", "hu_axe_a",
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
        foreach (var a in OS.GetCmdlineUserArgs()) if (a.StartsWith("--shots=")) _shots = a.Substring(8);
        try
        {
            PartLogic();
            var cfg = ViewerStage.TestConfig();
            _lib = new ModelLibrary(new AssetSource(cfg)) { PartsOnLinks = CarrierParts };
            ViewerStage.Setup(this);
            _cam = new Camera3D { Fov = 40, Far = 2000 };
            AddChild(_cam);
            Loading();
            await Shots();
            await MirageOffsets();
            await Performance();
        }
        catch (Exception ex) { _fails++; GD.PrintErr("EXCEPTION " + ex); }
        GD.Print($"view tests: {_checks - _fails}/{_checks} passed");
        GetTree().Quit(_fails == 0 ? 0 : 1);
    }

    // ---- parts.js logic on known attributes of the converted models
    void PartLogic()
    {
        // hu_small_tower mesh40 attr 301989919 (0x1200001f): finished (bit 25) + damage stage 2 (bit 28)
        int sig = PartBits.StaticSig(301989919);
        Check(PartBits.SigVisible(sig, 4, 2, 1) && !PartBits.SigVisible(sig, 4, 0, 1) && !PartBits.SigVisible(sig, 2, 2, 1), "static signature: finished, damage stage 2");
        // construction level 2 part
        uint cl2 = (1u << 23) | 1;
        Check(PartBits.SigVisible(PartBits.StaticSig(cl2), 2, 0, 1) && !PartBits.SigVisible(PartBits.StaticSig(cl2), 4, 0, 1), "construction level part");
        // epoch III part
        uint e3 = (1u << 11) | 1;
        Check(PartBits.SigVisible(PartBits.StaticSig(e3), 4, 0, 3) && !PartBits.SigVisible(PartBits.StaticSig(e3), 4, 0, 1), "epoch part");
        Check(PartBits.HelperPart(1u << 18 | 1, "Bldg") == "pick" && PartBits.HelperPart(1u << 19 | 1, "Bldg") == "shadow" && PartBits.HelperPart(2, "Bldg") == "lod" && PartBits.HelperPart(1u << 19 | 1, "Char") == null, "helper parts");
        Check(PartBits.TodOf(1u << 20, "Bldg") == TimeOfDay.Night && PartBits.TodOf(1u << 20, "Anim") == TimeOfDay.Always && PartBits.TodOf(1u << 17, "Ship") == TimeOfDay.Day, "time of day parts");
        // mammoth: saddle (attr 95 = bit 6) and party colour parts (attr 63 = bit 5) hidden on a wild animal, shown when
        // owned; the body (attr 31: LOD bits only) always
        uint wild = PartBits.AnimalMask(new AnimalLook());
        uint owned = PartBits.AnimalMask(new AnimalLook { Owned = true });
        Check(!PartBits.MaskVisible(95, wild) && PartBits.MaskVisible(95, owned) && !PartBits.MaskVisible(63, wild) && PartBits.MaskVisible(63, owned) && PartBits.MaskVisible(31, wild), "animal saddle / party flags");
        Check(PartBits.WoundStage(1) == 0 && PartBits.WoundStage(0.5) == 4 && PartBits.WoundStage(0.1) == 7, "wound stages");
        Check(!PartBits.MaskVisible(1u << 20 | 7, PartBits.AnimalMask(new AnimalLook { Hp = 0.9 })) && PartBits.MaskVisible(1u << 20 | 7, PartBits.AnimalMask(new AnimalLook { Hp = 0.75 })), "wounds by hit points");
        Check(PartyColors.IsPartyFlags("00001001/00000000") && !PartyColors.IsPartyFlags("00000001/00000000") && ModelMaterials.GsfAdditive("00000104/0") == 0x104, "material flags");
        var g = Composite.GearFor("hu_mammoth", inv => inv == "hu_mammoth_armor");
        Check(g.gear.SequenceEqual(new[] { PartBits.ARMOR, PartBits.ARMORSADDLE }) && g.noSaddle, "compose GEAR: mammoth armour");
    }

    // ---- every model of the test set loads and is prepared
    void Loading()
    {
        foreach (var n in TestSet)
        {
            var t = _lib.Template(n);
            Check(t != null, $"load {n}");
            if (t == null) continue;
            GD.Print($"     {n,-22} {t.Fourcc} meshes {t.MeshCount,3} surfaces {t.SurfaceCount,3} clips {(t.Clips?.GetAnimationList().Count ?? 0),4} links {t.Links.Count,3} parts {t.Parts.Count,3} box {t.Bounds.Size} {t.LoadMs:0} ms ({t.LoadSteps})");
        }
        var w = _lib.Template("hu_warrior_s1")!;
        Check(w.Clips!.HasAnimation("walk_1#l") && w.Clips.HasAnimation("walk_1#s") && w.Clips.HasAnimation("walk_1"), "start / loop / end parts of walk_1");
        var stina = _lib.Template("stina_s1")!;
        Check(stina.HasClips && stina.Clips!.GetAnimationList().Count > 200, "borrowed clips (stina_s1 <- stina_s0)");
        var tower = _lib.Template("hu_small_tower")!;
        Check(tower.Parts.Any(p => p.Sig >= 0), "tower: construction / damage parts");
        Check(tower.Links.ContainsKey("Cr_1") && tower.Links.ContainsKey("Proj"), "tower: links");
        var mam = _lib.Template("mammoth")!;
        Check(mam.Links.ContainsKey("Ride") && mam.Links.ContainsKey("flag"), "mammoth: rider and flag links");
        Check(mam.Bounds.Size.Y > 8 && mam.Bounds.Size.Y < 12, $"mammoth height {mam.Bounds.Size.Y:0.0} (the original's units: a man is ~3.8 tall)");
        // a wall piece: parts by name (pwexport WALL_PARTS "0aB" -> component "0a"), only the ones the WallMap names shown
        var pal = _lib.Template("hu_palisade", new ModelLoadOptions { WallArms = true });
        if (pal != null)
        {
            Check(pal.Parts.Count(p => p.Comp != null) > 10, "palisade: wall parts named");
            var pi = _lib.Instantiate(pal);
            var st = new BuildingState(4, 0, 1) { WallParts = new HashSet<string> { "0a" } };
            pi.SetBuildingState(st);
            var vis = pi.Parts.Where(x => x.part.Comp != null && x.node.Visible).Select(x => x.part.Comp).Distinct().ToList();
            Check(vis.Count == 1 && vis[0] == "0a", $"palisade: only the named arm shown ({string.Join(",", vis)})");
            pi.Free();
        }
        var war = _lib.Template("hu_warrior_s1")!;
        Check(war.Bounds.Size.Y > 3.2 && war.Bounds.Size.Y < 4.5, $"warrior height {war.Bounds.Size.Y:0.00}");
    }

    ModelInstance Place(string name, Vector3 pos, float headingDeg = 0, Color? party = null)
    {
        var m = _lib.Instantiate(name, party)!;
        AddChild(m);
        m.Position = pos;
        m.Rotation = new Vector3(0, Mathf.DegToRad(headingDeg), 0);
        _temp.Add(m);
        return m;
    }

    void Clear() { foreach (var n in _temp) { RemoveChild(n); n.QueueFree(); } _temp.Clear(); }

    // the hit reaction's overlay (AnimController.HitReaction, docs/VIEW.md "Hit reactions"): over a walking worker it
    // turns bones without stopping the walk; on a held pose the bones come back exactly when it is over
    void HitReactionOverlay(ModelInstance m)
    {
        var A = m.Anim!;
        var sk = m.Root.FindChild("Skeleton3D", true, false) as Skeleton3D;
        if (sk == null || !A.Has("hit_reaction")) { Check(false, "hit reaction: a skeleton and the clip hit_reaction on " + m.Name); return; }
        Quaternion[] Pose() { var q = new Quaternion[sk.GetBoneCount()]; for (int i = 0; i < q.Length; i++) q[i] = sk.GetBonePoseRotation(i); return q; }
        static float Diff(Quaternion[] x, Quaternion[] y) { float d = 0; for (int i = 0; i < x.Length; i++) d = Math.Max(d, x[i].Normalized().AngleTo(y[i].Normalized())); return d; }     // (normalized: the player's blends are not quite unit length)
        string clip = A.CurName ?? "";
        A.OverlayRestore(); m.Advance(0.05);
        var basePose = Pose();
        Check(A.HitReaction('f'), "hit reaction started (" + clip + ")");
        A.OverlayApply(0.12);
        float turned = Diff(basePose, Pose());
        Check(turned > 0.02f, $"hit reaction turns bones over the running clip ({Mathf.RadToDeg(turned):0.0} deg)");
        Check(A.CurName == clip && A.Phase == "l" && A.Player.IsPlaying(), "hit reaction: the running clip goes on (" + A.CurName + " " + A.Phase + ")");
        A.Player.Pause();
        A.OverlayRestore();
        var held = Pose();
        for (int i = 0; i < 8; i++) { A.OverlayRestore(); A.OverlayApply(0.05); }
        A.OverlayRestore();
        // (by components: an angle of two equal quaternions is ~0.04 deg in single precision)
        static float Comp(Quaternion[] x, Quaternion[] y) { float d = 0; for (int i = 0; i < x.Length; i++) { var a = x[i] - y[i]; var b = x[i] + y[i]; d = Math.Max(d, Math.Min(Math.Max(Math.Max(Math.Abs(a.X), Math.Abs(a.Y)), Math.Max(Math.Abs(a.Z), Math.Abs(a.W))), Math.Max(Math.Max(Math.Abs(b.X), Math.Abs(b.Y)), Math.Max(Math.Abs(b.Z), Math.Abs(b.W))))); } return d; }
        Check(!A.Overlaying && Comp(held, Pose()) < 1e-6f, $"hit reaction over: a held pose as it was (largest difference {Comp(held, Pose()):0.0e0})");
        A.Player.Play();
    }

    async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    async Task Shot(string file)
    {
        await Frames(3);
        if (_shots == "") return;
        System.IO.Directory.CreateDirectory(_shots);
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(System.IO.Path.Combine(_shots, file));
        GD.Print("     shot " + file);
    }

    void Look(Vector3 from, Vector3 at) { _cam.Position = from; _cam.LookAt(at); }

    async Task Shots()
    {
        // 1. units: Norse warrior and worker, a hero (borrowed clips), SEAS warrior; party colours
        var red = PartyColors.ById("red")!.Light; var blue = PartyColors.ById("blue")!.Light;
        // (heading 0 faces -z: turned towards the camera at +z)
        var a = Place("hu_warrior_s1", new Vector3(-4.5f, 0, 0), 160, red);
        var b = Place("hu_worker_s1", new Vector3(-1.5f, 0, 0), 170, blue);
        var c = Place("stina_s1", new Vector3(1.5f, 0, 0), 190, red);
        var d = Place("seas_warrior_s1", new Vector3(4.5f, 0, 0), 200, blue);
        foreach (var m in new[] { a, b, c, d }) { m.Play(m.Anim!.Pick("standanim", "idle_0", "stand")); m.Advance(0.5); }
        var steps = new List<AnimEvent>();
        b.OnAnimEvents(ev => steps.Add(ev));
        Check(a.Anim!.CurName == "standanim", "warrior plays standanim");
        Look(new Vector3(0, 3.2f, 11f), new Vector3(0, 1.9f, 0));
        await Shot("units.png");
        // walking: the start part, then the loop
        a.Play("walk_1"); a.Advance(0.1);
        Check(a.Anim.Phase == "s", "walk_1: start part first");
        a.Advance(1.0);
        Check(a.Anim.Phase == "l", "walk_1: then its loop");
        b.Play("walk_2"); c.Play("walk_2"); d.Play("walk_2");
        foreach (var m in new[] { b, c, d }) m.Advance(0.4);
        for (int i = 0; i < 40; i++) b.Advance(0.05);
        Check(steps.Count > 0 && steps.All(e => e.Wav.EndsWith(".wav")), $"walk sound events ({steps.Count}: {steps.FirstOrDefault().Wav})");
        HitReactionOverlay(b);
        await Shot("units_walk.png");
        a.Play("standanim");
        Check(a.Anim.Phase == "e", "walk_1 -> standanim: its end part first");
        Clear();

        // 2. a mammoth with rider and level flag, an allosaurus (wild), the SEAS spider with turret and gunner
        var mam = Place("mammoth", new Vector3(-9, 0, 0), 150, red);
        mam.SetAnimalLook(new AnimalLook { Owned = true });
        mam.SetAnimalTint(red);
        var comp = new Composite(mam);
        comp.Build(new[]
        {
            new PartSpec { I = 0, Kind = "rider", Gfx = "hu_rider_b", Link = "Ride", Anim = "ride_idle_0", AttackAnim = "ride_attack_front" },
            new PartSpec { I = 1, Kind = "other", Gfx = "hu_animal_flag_01", Link = "flag" },
        });
        Check(comp.Rider != null && comp.Parts.Count == 2, "mammoth: rider and flag attached");
        mam.Play("walk_1"); mam.Advance(0.7);
        var allo = Place("allosaurus", new Vector3(3, 0, -8), 220);
        allo.Play("standanim"); allo.Advance(0.3);
        var spider = Place("seas_wehrspinne", new Vector3(11, 0, 2), 200, blue);
        spider.SetAnimalLook(new AnimalLook { Owned = true });
        var sc = new Composite(spider);
        sc.Build(new[]
        {
            new PartSpec { I = 0, Kind = "turret", Gfx = "seas_wehrspinne_top", Link = "we", Anim = "attack_front" },
            new PartSpec { I = 1, Kind = "rider", Gfx = "seas_rider_b", Link = "Dri1", Pi = 0, Anim = "balista_stand" },
        });
        Check(sc.Turrets.Count == 1 && sc.Parts.Count == 2, "wehrspinne: turret and gunner attached");
        spider.Play("standanim"); spider.Advance(0.2);
        await Frames(2);
        float err = sc.Aim(new Vector3(-20, 0, 20), 10);
        Check(err < 0.01f, $"turret turned to its target (error {err:0.000})");
        comp.Update(0.1, false); sc.Update(0.1, false);
        Look(new Vector3(0, 9, 32), new Vector3(0, 3, -1));
        await Shot("animals.png");
        Clear();

        // 3. construction stages of the Norse tower (levels 0..3, finished, damage 1, 2) with the cranes while built
        for (int i = 0; i < 6; i++)
        {
            var t = Place("hu_small_tower", new Vector3(-40 + i * 16, 0, 0), 180, red);
            int level = i < 4 ? i : 4, dmg = i >= 4 ? i - 3 : 0;
            t.SetBuildingState(level, dmg, 1);
            if (level < 4) { t.Attach("hu_crane_01", "Cr_1"); t.Attach("hu_crane_01", "Cr_2"); t.Attach("hu_crane_02", "Cr_3"); }
            foreach (var cr in t.Attached) { cr.Play(cr.Anim?.Pick("standanim", "work", "idle") ?? cr.Anim?.Names.FirstOrDefault()); cr.Advance(0.3); }
        }
        Look(new Vector3(0, 22, 68), new Vector3(0, 6, 0));
        await Shot("construction.png");
        Clear();

        // 4. buildings: warehouse (epochs I and III), Dragon Clan temple and tea house (sprites), SEAS turret tower, a tree
        var w1 = Place("hu_warehouse", new Vector3(-40, 0, 0), 20, red); w1.SetBuildingState(4, 0, 1);
        var w3 = Place("hu_warehouse", new Vector3(-12, 0, 0), 20, red); w3.SetBuildingState(4, 0, 3);
        var tem = Place("ninigi_temple", new Vector3(18, 0, -4), -20, blue);
        var tea = Place("ninigi_teahouse", new Vector3(45, 0, 5), -30, blue);
        var tt = Place("seas_turret_tower", new Vector3(-26, 0, 30), 0, blue);
        tt.Attach("seas_turret", "we");
        var tree = _lib.Instantiate("jungle_tree_01", null, new ModelLoadOptions { TreeSprites = true })!;
        AddChild(tree); tree.Position = new Vector3(5, 0, 28); _temp.Add(tree);
        Look(new Vector3(0, 45, 90), new Vector3(0, 4, 0));
        await Shot("buildings.png");
        Clear();

        // 5. a tree with its foliage sprites, the Dragon Clan tea house's lanterns (sprites), by day and by night
        var tree2 = _lib.Instantiate("jungle_tree_01", null, new ModelLoadOptions { TreeSprites = true })!;
        AddChild(tree2); tree2.Position = new Vector3(-12, 0, 0); _temp.Add(tree2);
        var tea2 = Place("ninigi_teahouse", new Vector3(10, 0, 0), 200, blue);
        Look(new Vector3(0, 12, 38), new Vector3(0, 6, 0));
        await Shot("tree_sprites.png");
        Check(tea2.SetNight(true), "tea house switched to night");
        ViewerStage.SetNight(this, true);
        Look(new Vector3(4, 9, 26), new Vector3(10, 5, 0));
        await Shot("night.png");
        ViewerStage.SetNight(this, false);
        Clear();

        // 6. the SEAS carrier (when its file is in the data): the hull carries its own copy of the turret it gets at "we"
        // (ModelLibrary.DropBakedParts) - left out, only the linked turret shows, turned away from the bow
        if (HasCarrierFiles())
        {
            var raw = new ModelLibrary(_lib.Source).Template("seas_carrier");
            var tpl = _lib.Template("seas_carrier");
            Check(raw != null && tpl != null && tpl.MeshCount == raw.MeshCount - 1, $"carrier: the hull's own turret left out ({raw?.MeshCount} -> {tpl?.MeshCount} meshes)");
            for (int k = 0; k < 2; k++)
            {
                var car = k == 0 ? _lib.Instantiate(raw!, blue) : Place("seas_carrier", new Vector3(30, 0, 0), 150, blue);
                if (k == 0) { AddChild(car); car.Position = new Vector3(-30, 0, 0); car.RotationDegrees = new Vector3(0, 150, 0); _temp.Add(car); }
                car.SetBuildingState(4, 0, 1);
                var cc = new Composite(car);
                cc.Build(new[] { new PartSpec { I = 0, Kind = "turret", Gfx = "seas_carrier_turret", Link = "we", Anim = "attack_front" } });
                await Frames(2);
                // the turret turned 90 degrees off the bow: the hull's copy (first carrier, loaded without the drop) stays
                float aimErr = cc.Aim(car.ToGlobal(new Vector3(-30, 6, 0)), 10);
                if (k == 1) Check(aimErr < 0.01f && Mathf.Abs(cc.Turrets[0].Obj.Rotation.Z) > 0.5f, $"carrier: the turret turns (error {aimErr:0.000}, angle {Mathf.RadToDeg(cc.Turrets[0].Obj.Rotation.Z):0})");
                var at = car.ToGlobal(new Vector3(0, 6, -4));
                Look(at + new Vector3(16, 26, 26), at);
                await Shot(k == 0 ? "carrier_turret_before.png" : "carrier_turret.png");
            }
            Clear();
        }
    }

    // 7. MIRAGE's parts linked with an offset (composites "offset": AddObjCustomized, metres in the link's frame) - when the
    // MIRAGE data (<data>@MIRAGE) has the models: the CBR titan's two machine guns left and right of its saddle, the shore
    // cannon's carrier turret set down onto its base, the ballista titan's log cannon over its two steam cannons
    async Task MirageOffsets()
    {
        var dir = _lib.Source.Config.Data.TrimEnd('/') + "@MIRAGE";
        if (!System.IO.Directory.Exists(dir)) return;
        var lib = new ModelLibrary(new AssetSource(new GameConfiguration { Id = "view-test-mirage", Title = "View test MIRAGE", Data = dir, Install = "" }));
        bool Has(params string[] n) => n.All(x => lib.Has(x) && lib.Source.Resolve($"assets/models/{x}.glb") != null);
        ModelInstance Put(string name, Vector3 pos, float deg)
        {
            var m = lib.Instantiate(name, new Color(0.2f, 0.4f, 1))!;
            AddChild(m); m.Position = pos; m.RotationDegrees = new Vector3(0, deg, 0); _temp.Add(m);
            return m;
        }
        bool any = false;
        if (Has("arena_tred", "seas_hq_machinegun_nest_top", "seas_triceratops_transporter"))
        {
            any = true;
            var rex = Put("arena_tred", new Vector3(-22, 0, 0), 200);
            var c = new Composite(rex);
            c.Build(new[]
            {
                new PartSpec { I = 0, Kind = "turret", Gfx = "seas_hq_machinegun_nest_top", Link = "Ride", WeaponClass = "seas_arena_mg", Offset = new Vector3(3.04f, -1.15f, -1.21f) },
                new PartSpec { I = 1, Kind = "turret", Gfx = "seas_hq_machinegun_nest_top", Link = "Ride", WeaponClass = "seas_arena_mg", Offset = new Vector3(-3.04f, -1.15f, -1.21f) },
                new PartSpec { I = 2, Kind = "buildup", Gfx = "seas_triceratops_transporter", Link = "Ride", Offset = new Vector3(0, 0, -1.61f) },
            });
            await Frames(1);
            float d = c.Parts.Count == 3 ? c.Parts[0].Obj.GlobalPosition.DistanceTo(c.Parts[1].Obj.GlobalPosition) : 0;
            Check(Mathf.Abs(d - 6.08f) < 0.05f, $"CBR titan: its two machine guns 6.08 m apart ({d:0.00})");
        }
        if (Has("seas_hq_defense_turret", "seas_carrier_turret"))
        {
            any = true;
            var sc = Put("seas_hq_defense_turret", new Vector3(4, 0, 6), 30);
            var c = new Composite(sc);
            c.Build(new[] { new PartSpec { I = 0, Kind = "turret", Gfx = "seas_carrier_turret", Link = "we", Offset = new Vector3(0, 0, -7.05f) } });
            await Frames(1);
            // the turret's meshes start 5.3 m above its origin: lowered by 7.05 m they sit on the 1.7 m high base
            float y = c.Parts.Count == 1 ? c.Parts[0].Obj.GlobalPosition.Y - sc.GlobalPosition.Y : 99;
            Check(Mathf.Abs(y - (1.1f - 7.05f)) < 0.05f, $"shore cannon: the carrier turret set down onto its base (origin {y:0.00} m)");
        }
        if (Has("titan_triceratops", "hu_titan_transporter_buildup", "hu_mammoth_log_cannon_buildup_top", "hs_steam_boat_cannon", "hu_rhino_ballista_buildup_top"))
        {
            any = true;
            var ti = Put("titan_triceratops", new Vector3(26, 0, -4), 160);
            var c = new Composite(ti);
            c.Build(new[]
            {
                new PartSpec { I = 0, Kind = "turret", Gfx = "hu_rhino_ballista_buildup_top", Link = "con2", WeaponClass = "hu_titan_ballista" },
                new PartSpec { I = 1, Kind = "turret", Gfx = "hu_rhino_ballista_buildup_top", Link = "con3", WeaponClass = "hu_titan_ballista" },
                new PartSpec { I = 2, Kind = "buildup", Gfx = "hu_titan_transporter_buildup", Link = "con" },
                new PartSpec { I = 4, Kind = "turret", Gfx = "hu_mammoth_log_cannon_buildup_top", Link = "Dri4", Pi = 2, WeaponClass = "hu_titan_logcannon", Offset = new Vector3(-0.075f, 0.36f, 1.59f) },
                new PartSpec { I = 5, Kind = "buildup", Gfx = "hs_steam_boat_cannon", Link = "Dri4", Pi = 2, Offset = new Vector3(-0.075f, 0.36f, 0.09f) },
                new PartSpec { I = 6, Kind = "buildup", Gfx = "hs_steam_boat_cannon", Link = "Dri4", Pi = 2, Offset = new Vector3(-0.075f, 0.36f, 1.49f) },
            });
            await Frames(1);
            var cannons = c.Parts.Where(p => p.Spec.Gfx == "hs_steam_boat_cannon").ToList();
            float d = cannons.Count == 2 ? cannons[0].Obj.GlobalPosition.DistanceTo(cannons[1].Obj.GlobalPosition) : 0;
            Check(Mathf.Abs(d - 1.4f) < 0.05f, $"ballista titan: its two steam cannons 1.4 m apart ({d:0.00})");
        }
        if (!any) return;
        Look(new Vector3(0, 26, 46), new Vector3(0, 5, 0));
        await Shot("mirage_offsets.png");
        Clear();
    }

    bool HasCarrierFiles() => _lib.Has("seas_carrier") && _lib.Source.Resolve("assets/models/seas_carrier.glb") != null
        && _lib.Source.Resolve("assets/models/seas_carrier_turret.glb") != null;

    // the composites the game hangs on the carrier (GameSession.PartsOnLinksOf gives them from the rules)
    static IEnumerable<(string Link, string Gfx)> CarrierParts(string model) =>
        model == "seas_carrier" ? new[] { ("we", "seas_carrier_turret") } : Enumerable.Empty<(string, string)>();

    async Task Performance()
    {
        // 400 animated warriors: instancing time, animation update time
        var sw = Stopwatch.StartNew();
        var probe = _lib.Template("hu_warrior_s1")!;
        for (int i = 0; i < 100; i++) probe.Packed.Instantiate<Node3D>().Free();
        GD.Print($"perf: PackedScene.Instantiate of a warrior {sw.Elapsed.TotalMilliseconds / 100:0.00} ms");
        sw.Restart();
        var list = new List<ModelInstance>();
        var tpl = _lib.Template("hu_warrior_s1")!;
        double tNew = 0, tAdd = 0, tPlay = 0, tTint = 0;
        var sw2 = new Stopwatch();
        for (int i = 0; i < 400; i++)
        {
            sw2.Restart();
            var m = _lib.Instantiate(tpl);
            tNew += sw2.Elapsed.TotalMilliseconds; sw2.Restart();
            m.SetPartyColor(PartyColors.All[i % 8].Light);
            tTint += sw2.Elapsed.TotalMilliseconds; sw2.Restart();
            AddChild(m);
            m.Position = new Vector3((i % 20) * 2.5f - 25, 0, (i / 20) * 2.5f - 25);
            tAdd += sw2.Elapsed.TotalMilliseconds; sw2.Restart();
            m.Play(i % 2 == 0 ? "walk_2" : "standanim", true, 1, null, 0);
            tPlay += sw2.Elapsed.TotalMilliseconds;
            list.Add(m);
        }
        GD.Print($"perf: per warrior: instance {tNew / 400:0.00} ms, party colour {tTint / 400:0.00} ms, into the tree {tAdd / 400:0.00} ms, first clip {tPlay / 400:0.00} ms");
        double instMs = sw.Elapsed.TotalMilliseconds;
        GD.Print($"perf: 400 warriors instanced in {instMs:0} ms ({instMs / 400:0.00} ms each)");
        await Frames(5);
        sw.Restart();
        for (int f = 0; f < 30; f++) foreach (var m in list) m.Advance(1 / 30.0);
        double animMs = sw.Elapsed.TotalMilliseconds / 30;
        GD.Print($"perf: animation update of 400 warriors {animMs:0.00} ms per frame");
        Look(new Vector3(0, 30, 45), new Vector3(0, 0, -5));
        sw.Restart();
        await Frames(10);
        double frameMs = sw.Elapsed.TotalMilliseconds / 10;
        GD.Print($"perf: frame time with 400 animated warriors {frameMs:0} ms (llvmpipe software rendering)");
        await Shot("army.png");
        foreach (var m in list) { RemoveChild(m); m.QueueFree(); }
        // static props: 300 trees
        sw.Restart();
        var trees = new List<ModelInstance>();
        for (int i = 0; i < 300; i++) { var t = _lib.Instantiate("jungle_tree_01")!; AddChild(t); t.Position = new Vector3((i % 20) * 8 - 80, 0, (i / 20) * 8 - 80); trees.Add(t); }
        GD.Print($"perf: 300 trees instanced in {sw.Elapsed.TotalMilliseconds:0} ms");
        foreach (var t in trees) { RemoveChild(t); t.QueueFree(); }
        GD.Print($"perf: textures {_lib.Textures.Count}, {_lib.Textures.Bytes / 1048576.0:0} MB with mipmaps");
    }
}

/// <summary>Log lines to Godot's output (tests).</summary>
sealed class GodotPrintSink : ILogSink
{
    public string Name => "godot-print";
    public void Write(in LogEntry e) { if (e.Level >= LogLevel.Warn) GD.PrintErr(e.Format()); else GD.Print(e.Format()); }
    public void Flush() { }
    public void Dispose() { }
}
