// The effects layer (View/Fx, docs/EFFECTS.md): every kind of effect is made, drawn for a while and checked to make
// and free its nodes - the models' particle emitters (a campfire, the Norse fireplace with its state-switched fires,
// a building's damage fires, the emitter-only waterfalls as landscape objects), the game's effect models for the
// world's events (hits of every kind, melee blood, level-up, spawn, placed / built / invented pings, deaths, the
// vehicle explosion, a building at 50 % / 25 %, poison, ice, heal and item glows, the MIRAGE moves' fx / fxAt / fxFly,
// blasts), the drawn effects (explosions, dust, blood, muzzle smoke, glows, scorch marks; the damage smoke of a
// damaged building), the healing areas (their rays standing up), a mission's effect (EFCT), question marks, a
// cutscene's effect item, the "Particles" setting (off / low / on / high), and the cost of many emitters. Screenshots
// of each group with --shots; --only=<group,...> runs only those groups (e.g. --only=HealAreas).
//
// Run (rendering on llvmpipe in the container):
//   xvfb-run -a -s "-screen 0 1600x900x24" godot --path game --rendering-driver opengl3 res://src/View/Tests/FxTest.tscn -- --shots=<dir> [--data=<folder>]
// Exit code 0 = all checks passed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.View.Entities;
using PwRemake.View.Fx;
using PwRemake.View.Models;
using PwRemake.View.Tools;
using PwRemake.View.World;
using GameWorld = PwRemake.Sim.World.World;
using SimManifest = PwRemake.Sim.Content.ModelManifest;

namespace PwRemake.View.Tests;

public partial class FxTest : Node3D
{
    int _fails, _checks;
    string _shots = "";
    ModelLibrary _lib = null!;
    WorldRenderer _r = null!;
    GameWorld _w = null!;
    WorldFx _fx = null!;
    Particles _p = null!;
    PropField _props = null!;
    readonly Dictionary<Entity, ModelInstance> _models = new();
    Vector3 _target;
    const float DT = 1 / 30f;

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
            _r = WorldRenderer.Create();
            AddChild(_r);
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(800, 800) }, Name = "Ground",
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.Color8(0x6a, 0x74, 0x42), DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert, SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled, Roughness = 1 },
            });
            var cfg = ViewerStage.TestConfig();
            _lib = new ModelLibrary(new AssetSource(cfg));
            var R = PwRemake.Sim.Rules.Rules.Load(cfg.Data);
            var SM = SimManifest.Load(cfg.Data);
            _w = new GameWorld(R, SM, new FlatMap(600) { Setting = "Northland" }, new WorldOptions { Seed = 3 });
            foreach (var (id, tribe, col) in new[] { (1, "Hu", 0xd02020), (2, "Aje", 0x2050e0) })
                _w.Players.Add(new Player(id, tribe, new PlayerOpts { Rules = R }) { PartyColor = col });
            _p = new Particles(_lib.Textures);
            AddChild(_p);
            _lib.OnInstance = _p.Track;
            _props = new PropField { Particles = _p };
            AddChild(_props);
            _fx = new WorldFx(() => _w, _lib, _p, e => _models.GetValueOrDefault(e), e => V(e.Pos), (x, z) => 0);
            AddChild(_fx);
            if (OS.GetCmdlineUserArgs().Contains("--debug")) { await Debug(); GetTree().Quit(0); return; }
            // (--only=A,B: just these groups, e.g. --only=HealAreas)
            var only = OS.GetCmdlineUserArgs().FirstOrDefault(x => x.StartsWith("--only="))?.Substring(7).Split(',');
            foreach (var (name, run) in new (string, Func<Task>)[]
            {
                ("EmitterData", EmitterData), ("ModelEmitters", ModelEmitters), ("StaticEmitters", StaticEmitters),
                ("EffectModels", EffectModels), ("GameEvents", GameEvents), ("OriginalEffects", OriginalEffects),
                ("HealAreas", HealAreas), ("Drawn", Drawn), ("MissionAndMarks", MissionAndMarks), ("Settings", Settings),
                ("OutOfView", OutOfView), ("Performance", Performance), ("VisualFixes", VisualFixes),
            })
                if (only == null || only.Contains(name)) await run();
        }
        catch (Exception ex) { _fails++; GD.PrintErr("EXCEPTION " + ex); }
        GD.Print($"effects tests: {_checks - _fails}/{_checks} passed");
        GetTree().Quit(_fails == 0 ? 0 : 1);
    }

    readonly List<ModelInstance> _anim = new();   // (models whose clips Run advances)
    static Vector3 V(V3 p) => new((float)p.X, (float)p.Y, (float)p.Z);

    // frames of the game: the effects and the particles step with a fixed time, the screen draws in between
    async Task Run(double seconds)
    {
        // (a frame drawn every 15 steps and at the end: software rendering is slow)
        int n = (int)Math.Ceiling(seconds / DT);
        for (int i = 0; i < n; i++)
        {
            _fx.Step(DT, DT, DT);
            foreach (var m in _models.Values) if (GodotObject.IsInstanceValid(m)) m.Advance(DT);
            foreach (var m in _anim) if (GodotObject.IsInstanceValid(m)) m.Advance(DT);
            _props.UpdateView(_r.Camera);
            _p.Update(DT, _r.Camera, _target);
            if (i % 15 == 14 || i == n - 1) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    void Look(Vector3 at, float dist = 40, float yaw = 0.6f, float pitch = 0.55f)
    {
        _target = at;
        var off = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch)) * dist;
        _r.Camera.Position = at + off;
        _r.Camera.LookAt(at);
        _r.FollowSun(at);
    }

    async Task Shot(string file)
    {
        if (_shots == "") return;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        System.IO.Directory.CreateDirectory(_shots);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_shots, file));
        GD.Print("shot " + file);
    }

    ModelInstance? Place(string gfx, Vector3 pos, float yaw = 0)
    {
        var m = _lib.Instantiate(gfx);
        if (m == null) return null;
        m.Position = pos; m.Rotation = new Vector3(0, yaw, 0);
        AddChild(m);
        return m;
    }

    int Sprites => _p.Batches.Sum(b => b.Multimesh.VisibleInstanceCount);

    async Task Debug()
    {
        var only = OS.GetCmdlineUserArgs().FirstOrDefault(x => x.StartsWith("--models="));
        if (only != null)
        {
            foreach (var name in only.Substring(9).Split(','))
            {
                var dArg = OS.GetCmdlineUserArgs().FirstOrDefault(x => x.StartsWith("--dist="));
                float dist = dArg != null ? float.Parse(dArg[7..], System.Globalization.CultureInfo.InvariantCulture) : 30;
                Look(new Vector3(0, 2 + (dist - 30) * 0.4f, 0), dist, 0.5f, 0.4f * 30 / dist);
                var atArg = OS.GetCmdlineUserArgs().FirstOrDefault(x => x.StartsWith("--at="));
                double at = atArg != null ? double.Parse(atArg[5..], System.Globalization.CultureInfo.InvariantCulture) : 0.5;
                var e = _fx.Mfx.At(name, Vector3.Zero, Math.Max(3, at + 1), party: DrawnFx.Hex(0xd02020), loop: true);
                if (name.EndsWith("_dest")) e?.Obj.Play("destroy", false);
                await Run(at);
                foreach (var b2 in _p.Batches)
                {
                    if (b2.LastN == 0) continue;
                    var d = b2.Data;
                    float ymax = float.MinValue, ymin = float.MaxValue;
                    for (int i = 0; i < b2.LastN; i++) { float y = d[i * ParticleBatch.FLOATS + 7]; ymax = Math.Max(ymax, y); ymin = Math.Min(ymin, y); }
                    GD.Print($"batch {b2.Name} {b2.GridW}x{b2.GridH}: {b2.LastN}, y {ymin:0.0} .. {ymax:0.0}");
                    for (int i = 0; i < Math.Min(4, b2.LastN); i++) { int o = i * ParticleBatch.FLOATS; GD.Print($"  pos ({d[o + 3]:0.00}, {d[o + 7]:0.00}, {d[o + 11]:0.00}) col ({d[o + 12]:0.00} {d[o + 13]:0.00} {d[o + 14]:0.00}) a {d[o + 15]:0.00} size {d[o + 16]:0.00} tile {d[o + 18]}"); }
                }
                await Shot($"debug_{name}.png");
                await Run(4);
            }
            return;
        }
        if (OS.GetCmdlineUserArgs().Contains("--waterfall"))
        {
            _props.AddKind("jng_waterfall_01", _lib.Template("jng_waterfall_01"), false);
            var h = _props.Add("jng_waterfall_01", 0, 0, 0, 0, 1);
            foreach (var x in h!.Emit!) GD.Print($"emitter {x.Name}: {x.Matrix} det {x.Matrix.Basis.Determinant()}");
            Look(new Vector3(0, 20, 0), 110, 0.2f, 0.25f);
            await Run(3.0);
            foreach (var b2 in _p.Batches)
            {
                GD.Print($"batch {b2.Name}: {b2.LastN}");
                var d = b2.Data;
                for (int i = 0; i < Math.Min(8, b2.LastN); i++) { int o = i * ParticleBatch.FLOATS; GD.Print($"  pos ({d[o + 3]:0.00}, {d[o + 7]:0.00}, {d[o + 11]:0.00}) a {d[o + 15]:0.00} size {d[o + 16]:0.00}"); }
            }
            await Shot("debug_waterfall.png");
            return;
        }
        var camp = Place("campfire", Vector3.Zero)!;
        Look(new Vector3(0, 1, 0), 12, 0.6f, 0.3f);
        await Run(1.0);
        foreach (var (n, p) in camp.Parts) if (p.Kind == PartKind.Particles) GD.Print($"emitter {n.Name}: global {n.GlobalTransform} det {n.GlobalTransform.Basis.Determinant()} local {n.Transform}");
        foreach (var b in _p.Batches)
        {
            GD.Print($"batch {b.Name}: {b.LastN} grid {b.GridW}x{b.GridH} tex {b.Tex.GetWidth()}x{b.Tex.GetHeight()}");
            var d = b.Data;
            for (int i = 0; i < Math.Min(6, b.LastN); i++) { int o = i * ParticleBatch.FLOATS; GD.Print($"  pos ({d[o + 3]:0.00}, {d[o + 7]:0.00}, {d[o + 11]:0.00}) col ({d[o + 12]:0.00} {d[o + 13]:0.00} {d[o + 14]:0.00}) a {d[o + 15]:0.00} size {d[o + 16]:0.00} rot {d[o + 17]:0.00} tile {d[o + 18]} piv {d[o + 19]}"); }
        }
        await Shot("debug_campfire.png");
        _p.Level = "off";
        await Run(0.2);
        await Shot("debug_campfire_off.png");
        camp.QueueFree();
        var sb = new ParticleBatch(_lib.Textures.Get("gore.png")!, 8, 8, true) { TopLevel = true };
        AddChild(sb);
        for (int f = 0; f < 4; f++)
        {
            sb.Push(new Vector3(-3 + 2 * f, 1.5f, 0), 2.3f, 1, 1, 1, 1, 16 + 4 * f, 0, ParticleBatch.PackPivot(0, 0), false);
            sb.Flush(3);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        for (int f = 0; f < 4; f++) sb.Push(new Vector3(-3 + 2 * f, 1.5f, 0), 2.3f, 1, 1, 1, 1, 16 + 4 * f, 0, ParticleBatch.PackPivot(0, 0), false);
        sb.Flush(3);
        await Shot("debug_sprites.png");
        _p.Level = "on";
    }

    // ------------------------------------------------------------------------------------------- data
    async Task EmitterData()
    {
        var camp = _lib.Template("campfire");
        Check(camp != null && camp.Emitters.Count == 2, $"campfire: 2 emitters read ({camp?.Emitters.Count})");
        var e = camp?.Emitters.FirstOrDefault(x => x.Path.ToString().Contains("feuer"))?.Emitter;
        Check(e != null && e.Tex.EndsWith("gore.png") && e.Additive && e.Max == 30 && Math.Abs(e.Size - 8.5f) < 1e-4 && e.Flags == 655616 && e.Count == 16 && e.TileStart == 16,
            "campfire flame: texture, blending, max, size, flags, tiles as in the file");
        var wf = _lib.Template("jng_waterfall_01");
        Check(wf != null && wf.Emitters.Count == 6 && wf.MeshCount == 0, $"jng_waterfall_01: emitter-only model, 6 emitters ({wf?.Emitters.Count}, {wf?.MeshCount} meshes)");
        var fp = _lib.Template("hu_fireplace");
        Check(fp != null && fp.Emitters.Count > 10 && fp.Emitters.Any(x => x.Sig >= 0), $"hu_fireplace: emitters with part states ({fp?.Emitters.Count})");
        var lv = _lib.Template("levelup_char_fx");
        Check(lv != null && lv.Emitters.All(x => x.Emitter!.Anims?.Contains("anim") == true) && lv.HasClips, "levelup_char_fx: emitters driven by its clip 'anim'");
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------- model emitters
    async Task ModelEmitters()
    {
        var at = new Vector3(0, 0, 0);
        int before = _p.Active;
        var camp = Place("campfire", at + new Vector3(-6, 0, 0));
        var fire = Place("hu_fireplace", at + new Vector3(8, 0, 2), 0.4f);
        Check(_p.Active >= before + 2, $"instances tracked on Instantiate ({_p.Active - before} emitters)");
        Look(at + new Vector3(0, 2, 0), 26, 0.7f, 0.35f);
        await Run(1.5);
        Check(_p.Drawn > 20 && Sprites > 20, $"campfire and fireplace spray ({_p.Drawn} sprites, {_p.Batches.Count()} batches)");
        await Shot("fx_emitters_fire.png");
        // a building's damage fires: the emitters of the damage stages show with the stage (part states)
        var wh = Place("hu_warehouse", at + new Vector3(0, 0, -30));
        if (wh != null)
        {
            Look(at + new Vector3(0, 4, -30), 45, 0.5f, 0.45f);
            await Run(1.0);
            int intact = Sprites;
            wh.SetBuildingState(4, 2, 1);
            await Run(2.0);
            int burning = Sprites;
            Check(burning > intact, $"warehouse at damage stage 2: its fires spray ({intact} -> {burning} sprites)");
            await Shot("fx_emitters_damage.png");
            wh.SetBuildingState(4, 0, 1);
            await Run(3.0);
        }
        // the emitters of a removed model: dropped
        camp?.QueueFree(); fire?.QueueFree(); wh?.QueueFree();
        await Run(12.0);
        Check(_p.Active + _p.DormantCount <= before + 0, $"emitters of freed models dropped ({_p.Active} active, {_p.DormantCount} dormant)");
        // a preview sprays nothing
        var ghost = Place("campfire", at);
        ghost!.Sprays = false;
        await Run(0.5);
        Check(Sprites == 0, $"a placement preview (Sprays false) sprays nothing ({Sprites})");
        ghost.QueueFree();
        await Run(0.2);
    }

    // ------------------------------------------------------------------------------------------- reported looks
    // Kacper's reports (2026-11): flamethrowers flaming at rest, mission 10's treehouse without its tree and its
    // allied allosaurus in harness, the holy city's lantern halos as black squares (screenshots fx_flamethrower_*,
    // fx_treehouse_allosaurus).
    async Task VisualFixes()
    {
        Particles.WeaponClips = _w.Data.WeaponClips();
        var at = new Vector3(0, 0, 60);
        // 1. weapons that spray fire, through the game's own path: units of the simulation fight (Combat picks the
        // weapon's clip - "seas_flamethrower_0", "attack_front"), the models follow their clips as in a game
        // (EntityView.SyncAnim -> AnimController.Start). The SEAS flamethrower (flame stream and smoke on the model's
        // root, the flamer's drip in its hand), the mobile suit (the flame on a bone only attack_front moves) and the
        // Dragon Clan's flamethrower trike (its emitters name their clips): no fire at rest, fire while they attack,
        // none again once the fight is over. (The first version played the clip by hand and passed while no fire
        // showed in a game: Start left CurName empty.)
        var hu = _w.Players.First(p => p.Tribe == "Hu");
        var aje = _w.Players.First(p => p.Tribe == "Aje");
        var fighters = new List<(Unit u, ModelInstance m, int serial)>();
        foreach (var (cls, dx) in new[] { ("seas_flamethrower", -10.0), ("seas_mobile_suit_flamethrower", 0.0), ("ninigi_flamethrower_trike", 10.0) })
        {
            var u = _w.SpawnUnit(cls, hu, 300 + dx, 300);
            if (u?.Gfx == null || _lib.Instantiate(u.Gfx.ToLowerInvariant()) is not ModelInstance m) { GD.Print($"(no model for {cls})"); continue; }
            AddChild(m);
            if (u.HeldWeapon != null)
                foreach (var wp in u.HeldWeapon.Parts) if (wp.Gfx.Count > 0) m.Attach(wp.Gfx[0].ToLowerInvariant(), wp.Link);
            fighters.Add((u, m, -1));
        }
        var fireNodes = new[] { "particles_feuer", "particles_rauch", "particles_feuer_tropfen" };
        int Fire(ModelInstance m, bool trike) => trike ? _p.LiveOf(m, "particles_feuer") + _p.LiveOf(m, "particles_rauch")
            : fireNodes.Sum(n => _p.LiveOf(m, n) + m.FindChildren("*", "ModelInstance", true, false).OfType<ModelInstance>().Sum(c => _p.LiveOf(c, n)));
        int FireAll() => fighters.Sum(f => Fire(f.m, f.u.Name.Contains("trike")));
        async Task Game(double seconds)
        {
            double acc = 0;
            int n = (int)Math.Ceiling(seconds / DT);
            for (int i = 0; i < n; i++)
            {
                for (acc += DT; acc >= GameWorld.STEP; acc -= GameWorld.STEP) _w.Step();
                for (int k = 0; k < fighters.Count; k++)
                {
                    var (u, m, serial) = fighters[k];
                    m.Position = new Vector3((float)u.Pos.X, (float)u.Pos.Y, (float)u.Pos.Z) - new Vector3(300, 0, 300) + at;
                    m.Rotation = new Vector3(0, (float)u.Heading, 0);
                    if (u.Anim != null) EntityView.SyncAnim(m, u.Anim, ref serial, DT, true);
                    fighters[k] = (u, m, serial);
                }
                _p.Update(DT, _r.Camera, _target);
                if (i % 15 == 14 || i == n - 1) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        if (fighters.Count > 0)
        {
            Look(at + new Vector3(0, 2, 6), 40, 0.6f, 0.55f);
            await Game(3.0);
            int rest = FireAll();
            Check(rest == 0, $"flame weapons at rest: no fire ({rest} particles; clips {string.Join(", ", fighters.Select(f => f.u.Anim?.CurName))})");
            // foes in front of them: they attack (the simulation's own target choice and clips)
            var foes = new List<Unit>();
            for (int i = 0; i < 6; i++) if (_w.SpawnUnit("aje_warrior", aje, 290 + i * 4, 306) is Unit f) foes.Add(f);
            int peak = 0; var seen = new HashSet<string>();
            int spraying = 0, gaps = 0; var gapOf = new HashSet<string>();
            for (int t = 0; t < 30 && foes.Any(f => f.Alive); t++)
            {
                await Game(0.2);
                foreach (var f in fighters) if (f.u.Anim?.CurName is string c) seen.Add(f.u.Name + ":" + c);
                peak = Math.Max(peak, FireAll());
                // between the strikes too: a unit that sprays has its flame on (Unit.Spraying; it went on and off)
                foreach (var f in fighters) if (f.u.Spraying) { spraying++; if (Fire(f.m, f.u.Name.Contains("trike")) == 0) { gaps++; gapOf.Add(f.u.Name + ":" + f.u.Anim?.CurName); } }
                if (t == 6) await Shot("fx_flamethrower_fight.png");
            }
            GD.Print("clips in the fight: " + string.Join(", ", seen));
            Check(peak > 40, $"flame weapons in a fight: fire while they attack (peak {peak} particles)");
            Check(spraying > 10 && gaps <= 1, $"flame weapons in a fight: a steady flame from strike to strike ({gaps} of {spraying} looks without fire: {string.Join(", ", gapOf)})");
            foreach (var f in foes) if (f.Alive) _w.Kill(f, null);
            await Game(8.0);
            int after = FireAll();
            Check(after == 0, $"flame weapons after the fight: no fire ({after} particles)");
            foreach (var f in fighters) f.m.QueueFree();
            await Run(1.0);
        }
        // 2. mission 10: Darwin's treehouse as a game object (a Deko model: its tree's crown is foliage) and the arena
        // allosaurus of a player (wild look) beside the tribe's tamed one
        var tpl = _lib.Template("dh_darwins_treehouse");
        if (tpl != null)
        {
            Check(EntityViews.NeedsTreeSprites(tpl.Fourcc, tpl.Extras), "the treehouse (Deko with foliage) gets its own leaves as a game object");
            var th = _lib.Instantiate(_lib.Template("dh_darwins_treehouse", new ModelLoadOptions { TreeSprites = true })!)!;
            AddChild(th); th.Position = at + new Vector3(0, 0, -10);
            int leaves = th.FindChildren("*_foliage", "MultiMeshInstance3D", true, false).Count;
            Check(leaves > 5, $"the treehouse's crown: {leaves} foliage sprite sets");
            // its trunk and branches (mesh37: opaque jng_large_b on the bark band, alpha 0 there) are not cut out
            bool trunk = th.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Any(mi =>
                Enumerable.Range(0, mi.Mesh?.GetSurfaceCount() ?? 0).Any(s => mi.Mesh!.SurfaceGetMaterial(s) is StandardMaterial3D sm
                    && sm.ResourceName == "jng_large_b" && sm.Transparency == BaseMaterial3D.TransparencyEnum.Disabled));
            Check(trunk, "the treehouse's tree: its bark surfaces drawn opaque (not cut out)");
            var arena = _w.SpawnUnit("arena_allosaurus", aje, 520, 540)!;
            var tamed = _w.SpawnUnit("aje_allosaurus", aje, 520, 500)!;
            var a1 = Place("allosaurus", at + new Vector3(-14, 0, 8), 2.4f);
            var a2 = Place("allosaurus", at + new Vector3(14, 0, 8), 2.4f);
            a1?.SetHideMask((uint)arena.HideMask); a2?.SetHideMask((uint)tamed.HideMask);
            Look(at + new Vector3(0, 8, 0), 58, 0.0f, 0.3f);
            await Run(0.2);
            await Shot("fx_treehouse_allosaurus.png");
            th.QueueFree(); a1?.QueueFree(); a2?.QueueFree();
        }
        // 3. a lantern of the holy city as a map decoration: its halo (GSF flag 0x4, night only) is no cut-out leaf of the
        // foliage field - by day nothing, at night the model's additive sprite (WorldView.AddGlowSprites)
        var lan = _lib.Template("hc_lantern_01");
        if (lan != null)
        {
            int n = FoliageField.TreeSprites(lan.Extras, 0, 0, 0, 0, 1, false, new Random(1)).Values.Sum(l => l.Count);
            Check(n == 0 && lan.Extras["foliage"]![0]!["glow"].Bool(), $"hc_lantern_01: the halo marked glow, not in the foliage field ({n} sprites)");
        }
        await Run(0.2);
    }

    // ------------------------------------------------------------------------------------------- landscape objects
    async Task StaticEmitters()
    {
        var at = new Vector3(120, 0, 0);
        var k1 = _props.AddKind("jng_waterfall_01", _lib.Template("jng_waterfall_01"), false);
        var k2 = _props.AddKind("northland_waterfall_01_top", _lib.Template("northland_waterfall_01_top"), false);
        var h1 = _props.Add("jng_waterfall_01", at.X, 0, at.Z, 0, 1);
        var h2 = k2 != null ? _props.Add("northland_waterfall_01_top", at.X + 40, 0, at.Z, 0.5, 1) : null;
        _props.Build();
        Check(h1?.Emit?.Count == 6, $"waterfall as a landscape object: 6 emitters placed ({h1?.Emit?.Count})");
        Look(at + new Vector3(15, 25, 0), 110, 0.2f, 0.25f);
        await Run(3.0);
        Check(Sprites > 50, $"the waterfalls' spray ({Sprites} sprites)");
        await Shot("fx_waterfalls.png");
        _props.Remove(h1); _props.Remove(h2);
        await Run(6.0);
        Check(Sprites == 0, $"removed landscape objects: their spray ends ({Sprites})");
    }

    // ------------------------------------------------------------------------------------------- effect models
    async Task EffectModels()
    {
        var M = _fx.Mfx;
        int made = M.Made, freed = M.FreedCount;
        var at = new Vector3(-120, 0, 0);
        Look(at + new Vector3(0, 2, 0), 34, 0.5f, 0.4f);
        // the hits of every kind in a row (impact types, creature / object / land / water)
        var names = new[] { "hit_creature_sharp_sml", "hit_creature_explo_big", "hit_creature_poison_big", "hit_object_hu_blunt_big", "hit_object_seas_explo_big", "hit_land_nor_explo_big", "hit_land_nor_blunt_big", "hit_water_explo_big", "hit_creature_gatling", "fx_impact_proj_01" };
        int i = 0;
        foreach (var n in names) { Check(M.At(n, at + new Vector3(-22 + 5 * i, 0.5f, 0), 4.0) != null, $"effect model {n}"); i++; }
        await Run(0.6);
        Check(Sprites > 30, $"the hits spray ({Sprites})");
        await Shot("fx_hits.png");
        // pings with their clips, owner-coloured
        var red = DrawnFx.Hex(0xd02020);
        var lv = M.At("levelup_char_fx", at + new Vector3(-10, 0, 14), 2.0, party: red);
        M.At("spawnping_char_tec_fx", at + new Vector3(-4, 0, 14), 1.2, party: red);
        M.At("placeping_building_fx", at + new Vector3(2, 0, 14), 1.55);
        M.At("readyping_building_fx", at + new Vector3(10, 0, 14), 1.8, party: red);
        M.At("deathping_animal_med_fx", at + new Vector3(18, 0, 14), 3.0, party: red);
        M.At("vehicle_explosion", at + new Vector3(26, 0, 14), 2.5);
        Check(lv?.Obj.Anim?.CurName == "anim", $"levelup_char_fx plays its clip 'anim' ({lv?.Obj.Anim?.CurName})");
        Look(at + new Vector3(8, 2, 14), 36, 0.3f, 0.35f);
        await Run(0.5);
        await Shot("fx_pings.png");
        // follow, fly, keyed stop
        var follow = new Node3D { Position = at + new Vector3(0, 0, 30) };
        AddChild(follow);
        var f = M.Attach(() => GodotObject.IsInstanceValid(follow) ? new ModelFx.FollowPose(follow.Position + new Vector3(0, 1, 0), true) : null, "fx_poison", 100, "poison:1");
        var fly = M.FlyTo("rolling_stone", at + new Vector3(-20, 15, 30), at + new Vector3(0, 0, 30), 1.2);
        follow.Position += new Vector3(5, 0, 0);
        await Run(0.3);
        Check(f != null && f.Obj.Position.DistanceTo(follow.Position + new Vector3(0, 1, 0)) < 0.01f, "an attached effect follows (poison)");
        Check(fly == null || fly.Obj.Position.DistanceTo(at + new Vector3(-20, 15, 30)) > 1, "a flying effect moves (rolling_stone)");
        M.Stop("poison:1");
        follow.QueueFree();
        await Run(5.0);
        Check(M.Count == 0 && M.FreedCount - freed == M.Made - made, $"effect models all freed ({M.Made - made} made, {M.FreedCount - freed} freed, {M.Count} left)");
        Check(M.GetChildCount() == 0, $"no effect nodes left ({M.GetChildCount()})");
    }

    // ------------------------------------------------------------------------------------------- the world's events
    async Task GameEvents()
    {
        var W = _w;
        var hu = W.Players[0];
        var at = new Vector3(0, 0, 120);
        Look(at + new Vector3(0, 2, 0), 40, 0.4f, 0.45f);
        var war = W.SpawnUnit("hu_warrior", hu, at.X - 10, at.Z)!;
        var wh = W.PlaceBuilding("hu_warehouse", hu, at.X + 14, at.Z - 4);
        foreach (var e in new Entity?[] { war, wh })
            if (e?.Gfx is { } g && Place(g.ToLowerInvariant(), V(e.Pos)) is { } m) _models[e] = m;
        Check(_models.Count == 2, $"a warrior and a warehouse placed ({_models.Count})");
        var w = new Weapon { Id = "cannon", Projectile = "hu_cannonball", Splash = 8 };
        var arrow = new Weapon { Id = "arrow", Projectile = "hu_arrow" };
        int made = _fx.Mfx.Made;
        var evs = new List<WorldEvent>
        {
            new("impact", new Args { ["pos"] = new V3(at.X, 0, at.Z + 6), ["weapon"] = w, ["splash"] = true }),
            new("impact", new Args { ["pos"] = new V3(at.X - 10, 1, at.Z), ["weapon"] = arrow, ["target"] = war }),
            new("impact", new Args { ["pos"] = new V3(at.X + 14, 3, at.Z - 4), ["weapon"] = w, ["target"] = wh }),
            new("hit", new Args { ["attacker"] = war, ["target"] = war, ["melee"] = true }),
            new("levelup", new Args { ["unit"] = war }),
            new("trained", new Args { ["unit"] = war }),
            new("placed", new Args { ["building"] = wh }),
            new("built", new Args { ["building"] = wh }),
            new("upgrade", new Args { ["entity"] = wh, ["player"] = hu }),
            new("damagestage", new Args { ["entity"] = wh, ["stage"] = 1 }),
            new("healfx", new Args { ["to"] = war }),
            new("itemTaken", new Args { ["unit"] = war }),
            new("fxAt", new Args { ["pos"] = new V3(at.X + 4, 0, at.Z + 14), ["gfx"] = "fx_dust_twister", ["until"] = W.Time + 3, ["loop"] = true }),
            new("blast", new Args { ["pos"] = new V3(at.X - 14, 0, at.Z + 14) }),
            new("dust", new Args { ["pos"] = new V3(at.X + 14, 0, at.Z + 14), ["r"] = 6.0, ["kind"] = "tornado" }),
            new("glow", new Args { ["kind"] = "firework", ["unit"] = war, ["i"] = 0.0, ["pos"] = new V3(at.X, 0, at.Z - 20), ["size"] = 6.0, ["size1"] = 16.0, ["life"] = 1.2, ["color"] = 0x40ff60 }),
            new("camshake", new Args { ["cls"] = "CPlaceBuildingFX", ["pos"] = new V3(at.X, 0, at.Z) }),
        };
        int shakes = 0;
        _fx.Gfx.Shake = (_, _, _, _, _) => shakes++;
        _fx.HandleEvents(evs);
        Check(_fx.ImpactPlayed(evs[0]) && _fx.ImpactPlayed(evs[1]) && _fx.ImpactPlayed(evs[2]), "impacts on the ground, a unit and a building play the game's hit models");
        Check(_fx.Mfx.Keyed.ContainsKey("heal:" + war.Id) && _fx.Mfx.Keyed.ContainsKey("item:" + war.Id), "heal and item glows on the unit");
        Check(_fx.Mfx.Made - made >= 14, $"effect models of the events ({_fx.Mfx.Made - made})");
        Check(shakes >= 3, $"camera shakes: placed, damage stage, blast, camshake ({shakes})");
        Check(_fx.Gfx.ImpactName("hu_arrow", war, Vector3.Zero).Names[0] == "hit_creature_sharp_sml" && _fx.Gfx.ImpactName("seas_turret_bullet", wh, Vector3.Zero).Names[0] == "hit_object_gatling"
              && _fx.Gfx.ImpactName("hu_cannonball", null, Vector3.Zero).Names[0] == "hit_land_nor_blunt_big", "hit names (E:27-162)");
        // a damaged building: its own damage-stage emitters smoke and burn (ModelEmitters) - nothing drawn
        wh!.DmgStage = 2; wh.Built = true;
        int spawned = _fx.Fx.Spawned;
        await Run(1.0);
        Check(_fx.Fx.Spawned == spawned, $"no drawn sprites for the events and a damaged building ({_fx.Fx.Spawned - spawned})");
        await Shot("fx_events.png");
        // poison and ice (Status, a few times a second)
        war.St.Poison = new PoisonState();
        war.St.TrapWhy = "iced"; war.St.T["trapped"] = W.Time + 100;
        await Run(0.6);
        Check(_fx.Mfx.HasKey("poison:" + war.Id) && _fx.Mfx.HasKey("ice:" + war.Id), "poison and ice on the unit");
        war.St.Poison = null; war.St.T.Remove("trapped");
        _fx.HandleEvents(new List<WorldEvent> { new("itemDropped", new Args { ["unit"] = war }) });
        wh.DmgStage = 0;
        // the unit dies: its effects go with it
        war.Alive = false;
        _fx.HandleEvents(new List<WorldEvent> { new("died", new Args { ["entity"] = war }), new("destroyed", new Args { ["entity"] = wh }) });
        await Run(10.5);       // (the fireworks last 10 s)
        Check(_fx.Mfx.Count == 0, $"the events' effect models are gone ({_fx.Mfx.Count})");
        foreach (var m in _models.Values) m.QueueFree();
        _models.Clear();
        await Run(0.1);
    }

    // ------------------------------------------------------------------------------------------- the game's own effects
    // where the JS drew its own: ruins, rocket smoke, fireworks, tornado, quicksand, spirits, the clips' effect events
    async Task OriginalEffects()
    {
        var W = _w;
        var hu = W.Players[0];
        var at = new Vector3(-120, 0, -120);
        var M = _fx.Mfx;
        int drawn = _fx.Fx.Spawned;
        Look(at + new Vector3(0, 4, 0), 55, 0.5f, 0.45f);
        // ruins: "<gfx>_dest" plays "destroy" (EntityViews), its emitters are the explosion; the temple's adds its dust
        var ruins = new List<ModelInstance>();
        foreach (var (n, dx) in new[] { ("hu_temple_dest", -16f), ("hu_warehouse_dest", 16f) })
            if (Place(n, at + new Vector3(dx, 0, 0)) is { } m) { m.SyncAnimation("destroy", false, 1, 0, 0); ruins.Add(m); _anim.Add(m); }
        Check(ruins.Count == 2 && ruins.All(r => r.Anim?.CurName == "destroy"), $"ruins play their clip 'destroy' ({ruins.Count})");
        var b = W.PlaceBuilding("hu_warehouse", hu, at.X - 16, at.Z)!;
        b.Ruin = "hu_temple_dest";
        _fx.HandleEvents(new List<WorldEvent> { new("destroyed", new Args { ["entity"] = b }) });
        Check(M.List.Any(e => e.Obj.Template.Name == "hu_temple_dem_fx"), "the temple ruin's dust (Hu_Temple_Dem_Fx)");
        await Run(0.5);
        int boom = Sprites;
        Check(boom > 100, $"the ruins' explosions spray ({boom} sprites)");
        await Shot("fx_ruins.png");
        await Run(2.0);
        await Shot("fx_ruins_smoke.png");
        foreach (var m in ruins) m.QueueFree();
        _anim.Clear();
        await Run(3.0);              // (the ruins' last particles)
        Look(at + new Vector3(0, 3, 25), 42, 0.35f, 0.5f);
        // the burning resin field's model (EntityViews shows it while Burning)
        var resin = Place("ninigi_resin_field_fire", at + new Vector3(0, 0, 20));
        // rocket smoke, fireworks, tornado, quicksand
        var rk = W.SpawnUnit("seas_rocketman", hu, at.X - 20, at.Z + 20);
        var fw = W.SpawnUnit("hu_warrior", hu, at.X + 20, at.Z + 20)!;
        var evs = new List<WorldEvent>
        {
            new("shoot", new Args { ["attacker"] = rk, ["pos"] = new V3(at.X - 20, 2, at.Z + 20) }),
            new("shoot", new Args { ["attacker"] = fw, ["pos"] = new V3(at.X + 20, 2, at.Z + 20) }),
            new("glow", new Args { ["kind"] = "firework", ["unit"] = fw, ["i"] = 0.0, ["pos"] = fw.Pos }),
            new("glow", new Args { ["kind"] = "firework", ["unit"] = fw, ["i"] = 1.0, ["pos"] = fw.Pos }),
            new("dust", new Args { ["pos"] = new V3(at.X - 10, 0, at.Z + 40), ["r"] = 6.0, ["kind"] = "tornado" }),
            new("dust", new Args { ["pos"] = new V3(at.X + 10, 0, at.Z + 40), ["r"] = 8.0, ["kind"] = "quicksand", ["life"] = 30.0 }),
        };
        _fx.HandleEvents(evs);
        bool Has(string n) => M.List.Any(e => !e.Freed && e.Obj.Template.Name == n);
        Check(rk == null || Has("fx_seas_rpg"), "a rocket launcher's shot smokes (fx_seas_rpg)");
        Check(M.List.Count(e => e.Obj.Template.Name == "fx_seas_rpg") <= 1, "other guns: no scripted muzzle effect");
        Check(Has("ninigi_fireworks") && M.List.Count(e => e.Obj.Template.Name == "ninigi_fireworks") == 1, "fireworks: the model ninigi_fireworks, once per move");
        Check(Has("aje_tornado") && Has("aje_quicksand_trap"), "the tornado and the quicksand trap as the game's models");
        _fx.HandleEvents(new List<WorldEvent> { new("dust", new Args { ["pos"] = new V3(at.X - 10, 0, at.Z + 40), ["kind"] = "tornado" }) });
        Check(M.List.Count(e => !e.Freed && e.Obj.Template.Name == "aje_tornado") == 1, "the tornado's ticks keep one model up");
        // the spirits: fx_ghost_big while an own shaman is selected
        W.Spirits.Add(new Spirit { Owner = hu, Pos = new V3(at.X + 24, 0, at.Z + 30), Until = W.Time + 60, Name = "hu_warrior" });
        bool sel = true;
        _fx.ShowSpirits = () => sel;
        for (int i = 0; i < 3; i++)
        {
            await Run(0.5);
            _fx.HandleEvents(new List<WorldEvent> { new("dust", new Args { ["pos"] = new V3(at.X - 10, 0, at.Z + 40), ["kind"] = "tornado" }) });
        }
        Check(_fx.SpiritCount == 1 && Has("fx_ghost_big"), $"a spirit shows fx_ghost_big while a shaman is selected ({_fx.SpiritCount})");
        await Shot("fx_original.png");
        sel = false;
        await Run(0.3);
        Check(_fx.SpiritCount == 0 && !Has("fx_ghost_big"), "spirits hidden without a shaman selected");
        // the clips' effect events (Sim AnimFx): shakes and the stomp's ping
        var fxEv = PwRemake.Sim.World.AnimFx.With(null, "brachiosaurus");
        Check(fxEv != null && fxEv["walk_1"].Count == 4 && fxEv["walk_1"][0].Wav == PwRemake.Sim.World.AnimFx.FX && PwRemake.Sim.World.AnimFx.With(null, "hu_warrior") == null,
              "the brachiosaurus' footfalls are clip events (AnimFx)");
        int shakes = 0;
        _fx.Gfx.Shake = (_, _, _, _, _) => shakes++;
        _fx.AnimEffect("CBrachioWalking", fw);
        _fx.AnimEffect("CPlaceBuildingFX", fw);
        Check(shakes == 2 && Has("placeping_building_fx"), $"footfall and stomp shake the camera, the stomp pings ({shakes})");
        Check(_fx.Fx.Spawned == drawn, $"none of these drew sprites ({_fx.Fx.Spawned - drawn})");
        W.Spirits.Clear();
        _fx.ShowSpirits = null;
        resin?.QueueFree();
        M.Clear();
        await Run(0.2);
    }

    // ------------------------------------------------------------------------------------------- healing areas
    // The temples' and the SEAS fortress' healing area fx_heal_area (Building.usl CTemple / Mirage.usl CSeasFortress
    // SetReady: AddFX; WorldFx.ReadyFx), the healing wells' own emitters (seas_healspring - MIRAGE's SEAS healing well -
    // and the maps' healing_well_01), the level-up's rays and a water hit's splashes: sprites that point the way they fly
    // (flag 0x20000000) with "rotate" pi and the pivot at their cell's foot. They rise from the ground; until 2026-11-05
    // they hung from their foot into it (user report: the SEAS fortress' healing area "should have yellow vertical
    // lines"; SpriteAngle). Models the data lacks are left out.
    async Task HealAreas()
    {
        var W = _w;
        var hu = W.Players[0];
        var at = new Vector3(-120, 0, 120);
        // the sprites of the rays sheet (rays.png: 16 x 2 cells) drawn in the last frame within 30 m of the place (not
        // the effects of the other groups still in range), and how many stand up
        (int N, int Up) Rays()
        {
            int n = 0, up = 0;
            foreach (var b in _p.Batches)
            {
                if (b.GridW != 16 || b.GridH != 2) continue;
                var d = b.Data;
                for (int i = 0; i < b.LastN; i++)
                {
                    int o = i * ParticleBatch.FLOATS;
                    if (new Vector2(d[o + 3] - at.X, d[o + 11] - at.Z).Length() > 30) continue;
                    n++;
                    if (SpriteAngle.Top(d[o + 17]).Y > 0.7f) up++;
                }
            }
            return (n, up);
        }
        async Task Show(string what, string shot, Func<List<Node3D>> make, bool rays = true, string? top = null)
        {
            var made = make();
            if (made.Count == 0) { GD.Print($"skip {what}: not in the data"); return; }
            Look(at + new Vector3(0, 4, 0), 50, 0.6f, 0.38f);
            await Run(2.5);
            var (n, up) = Rays();
            if (rays) Check(n > 0 && up >= n * 0.9, $"{what}: its rays stand up from the ground ({up} of {n})");
            await Shot(shot);
            if (top != null)
            {
                // the original's game camera (Kacper's reference shot of a Norse temple's healing area): high, about
                // 50 degrees down, the whole 24 m disk of the rays in the view
                Look(at, 85, 0.6f, 0.87f);
                await Run(0.5);
                await Shot(top);
            }
            foreach (var m in made) if (GodotObject.IsInstanceValid(m)) m.QueueFree();
            await Run(3.5);           // (the last particles)
        }
        // a temple as the game makes it: finished, the effect shown by WorldFx.ReadyFx
        await Show("hu_temple (CTemple)", "fx_heal_temple.png", top: "fx_heal_temple_top.png", make: () =>
        {
            var b = W.PlaceBuilding("hu_temple", hu, at.X, at.Z);
            if (b == null || Place(b.Gfx!.ToLowerInvariant(), V(b.Pos)) is not { } m) return new();
            _models[b] = m;
            b.UpdateVisual(true);
            return new() { m };
        });
        foreach (var b in W.Buildings) if (b.Name == "hu_temple") b.Alive = false;
        await Run(0.5);
        Check(_fx.ReadyFxCount == 0, $"the temple's effect goes with it ({_fx.ReadyFxCount})");
        _models.Clear();
        // the SEAS fortress (MIRAGE seas_fortress: the model seas_hq_bunker, CSeasFortress) with the effect in its frame
        await Show("seas_hq_bunker + fx_heal_area (CSeasFortress)", "fx_heal_fortress.png", () =>
        {
            if (Place("seas_hq_bunker", at) is not { } m) return new();
            if (_lib.Instantiate("fx_heal_area") is { } fx) { m.AddChild(fx); fx.Play(fx.Anim?.Pick("anim", "standanim")); }
            return new() { m };
        });
        // the healing wells' own emitters
        await Show("seas_healspring (CSeasWell)", "fx_heal_well_seas.png", () => Place("seas_healspring", at) is { } m ? new() { m } : new());
        await Show("healing_well_01 (CFountain)", "fx_heal_well.png", () => Place("healing_well_01", at) is { } m ? new() { m } : new());
        // the level-up's rays and the splashes of a water hit (the same kind of emitter)
        Look(at + new Vector3(0, 4, 0), 30, 0.6f, 0.3f);
        var lv = _fx.Mfx.At("levelup_char_fx", at + new Vector3(-6, 0, 0), 3, party: DrawnFx.Hex(0xd02020), anim: "anim");
        _fx.Mfx.At("hit_water_explo_big", at + new Vector3(6, 0, 0), 3);
        await Run(0.6);
        var (ln, lu) = Rays();
        Check(lv == null || (ln > 0 && lu >= ln * 0.6), $"levelup_char_fx: its rays point up ({lu} of {ln})");
        await Shot("fx_rays_levelup_splash.png");
        await Run(3.5);
    }

    // ------------------------------------------------------------------------------------------- drawn effects
    async Task Drawn()
    {
        var F = _fx.Fx;
        var at = new Vector3(-120, 0, 120);
        Look(at + new Vector3(0, 3, 0), 40, 0.5f, 0.4f);
        F.Explosion(at, 1.5f);
        F.Dust(at + new Vector3(12, 0, 0), 4);
        F.Blood(at + new Vector3(-10, 1.5f, 0), 12);
        F.Muzzle(at + new Vector3(0, 2, 10));
        F.Spawn(DrawnFx.Kind.Glow, at + new Vector3(-10, 6, -8), 6, 16, 1.2f, color: DrawnFx.Hex(0xff5030));
        await Run(0.35);
        Check(F.Live > 40 && F.Decals >= 1, $"drawn effects: explosion, dust, blood, muzzle, glow ({F.Live} sprites, {F.Decals} scorch)");
        await Shot("fx_drawn.png");
        await Run(4.0);
        Check(F.Live == 0, $"drawn sprites fade out ({F.Live})");
    }

    // ------------------------------------------------------------------------------------------- missions, marks, cutscenes
    async Task MissionAndMarks()
    {
        var at = new Vector3(120, 0, 120);
        Look(at + new Vector3(0, 3, 0), 40, 0.5f, 0.4f);
        var list = new List<MissionEffect> { new() { Gfx = "fx_groundfog", X = at.X - 8, Z = at.Z, Until = _w.Time + 1e9 }, new() { Gfx = "fx_heal_area", X = at.X + 8, Z = at.Z, Until = _w.Time + 1e9 } };
        _fx.MissionEffects = list;
        var marks = new List<(string, string, double, double)> { ("q1", "questionmark_red", at.X - 6, at.Z + 10), ("q2", "exclamation_yellow", at.X + 6, at.Z + 10) };
        _fx.QuestionMarks = () => marks;
        _fx.CutsceneEffect("Data/Base/Gfx/Effects/hit_land_nor_explo_big", at + new Vector3(0, 0, -8), 2);
        await Run(1.0);
        Check(_fx.MissionFxCount == 2, $"a mission's effects (EFCT) placed ({_fx.MissionFxCount})");
        Check(_fx.MarkCount == 2, $"question marks as the game's models ({_fx.MarkCount})");
        await Shot("fx_mission.png");
        list.Clear();
        marks.Clear();
        await Run(3.0);
        Check(_fx.MissionFxCount == 0 && _fx.MarkCount == 0 && _fx.Mfx.Count == 0, $"mission effects, marks and the cutscene item gone ({_fx.MissionFxCount}, {_fx.MarkCount}, {_fx.Mfx.Count})");
        _fx.MissionEffects = null; _fx.QuestionMarks = null;
    }

    // ------------------------------------------------------------------------------------------- the setting
    async Task Settings()
    {
        var at = new Vector3(0, 0, -120);
        var fires = new List<ModelInstance>();
        for (int i = 0; i < 6; i++) if (Place("campfire", at + new Vector3(-15 + 6 * i, 0, 0)) is { } m) fires.Add(m);
        Look(at + new Vector3(0, 1, 0), 30, 0.3f, 0.35f);
        await Run(1.0);
        int on = Sprites;
        _fx.Level = "off";
        await Run(0.2);
        Check(Sprites == 0 && _p.Drawn == 0, $"Particles off: nothing drawn ({Sprites})");
        _fx.Fx.Explosion(at);
        Check(_fx.Fx.Live == 0, "Particles off: no drawn effects");
        _fx.Level = "low";
        await Run(1.0);
        int low = _p.Drawn;
        _fx.Level = "high";
        await Run(0.5);
        Check(on > 0 && low > 0, $"Particles low / on spray ({low} / {on})");
        _fx.Level = "on";
        foreach (var m in fires) m.QueueFree();
        await Run(0.5);
    }

    // the emitters in the view are drawn steadily however many spray out of it (user report: the fires, smoke and steam
    // of a SEAS base on a lava map came and went - the emitters around the camera target out of the view, behind and
    // beside the camera, took the sprite limit, and the ones in the view were cut from frame to frame). A frame rate
    // of 165 Hz as on the user's monitor; 300 campfires and teahouses all around, four teahouses in the view.
    async Task OutOfView()
    {
        var at = new Vector3(400, 0, 0);
        var ms = new List<ModelInstance>();
        for (int i = 0; i < 300; i++)
        {
            float a = i * 2.4f, r = 50 + (i * 37 % 100);
            if (Place(i % 2 == 0 ? "campfire" : "ninigi_teahouse", at + new Vector3(r * Mathf.Cos(a), 0, r * Mathf.Sin(a))) is { } m) ms.Add(m);
        }
        // (the ones in the view last: before, the emitters later in the list were the ones cut)
        for (int i = 0; i < 4; i++) if (Place("ninigi_teahouse", at + new Vector3(-9 + 6 * i, 0, -4 + 3 * (i % 2))) is { } m) ms.Add(m);
        Look(at, 70, 0.3f, 0.9f);
        await Run(1.0);
        var planes = _r.Camera.GetFrustum();
        int min = int.MaxValue, max = 0, outside = 0, frames = 330;
        for (int f = 0; f < frames; f++)
        {
            _p.Update(1 / 165.0, _r.Camera, _target);
            int near = 0;
            foreach (var b in _p.Batches)
            {
                var d = b.Data;
                for (int i = 0; i < b.LastN; i++)
                {
                    int o = i * ParticleBatch.FLOATS;
                    var w = new Vector3(d[o + 3], d[o + 7], d[o + 11]);
                    if (new Vector2(w.X - at.X, w.Z - at.Z).Length() < 25) near++;
                    foreach (var pl in planes) if (pl.DistanceTo(w) > d[o + 16] * 3) { outside++; break; }
                }
            }
            min = Math.Min(min, near); max = Math.Max(max, near);
            if (f % 55 == 0) await Shot($"fx_out_of_view_{f / 55}.png");          // (a sequence over the 2 s)
            else if (f % 60 == 59) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        GD.Print($"out of view: {_p.Active} emitters active, {_p.Drawn} sprites drawn, the teahouses in the view {min} .. {max} sprites per frame");
        Check(outside == 0, $"only sprites in the view are drawn ({outside} outside in {frames} frames)");
        Check(max > 0 && min >= max * 0.6, $"the emitters in the view drawn steadily at 165 Hz with 300 sprayers around ({min} .. {max} sprites per frame)");
        Check(_p.Load > 0.99f, $"the load counts what is drawn: full detail ({_p.Load:0.00})");
        foreach (var m in ms) m.QueueFree();
        await Run(0.5);
    }

    // ------------------------------------------------------------------------------------------- cost
    async Task Performance()
    {
        var at = new Vector3(0, 0, -250);
        var fires = new List<ModelInstance>();
        for (int i = 0; i < 100; i++) if (Place("hu_fireplace", at + new Vector3((i % 10) * 9 - 45, 0, (i / 10) * 9 - 45)) is { } m) fires.Add(m);
        Look(at + new Vector3(0, 2, 0), 90, 0.3f, 0.6f);
        await Run(1.0);
        var sw = Stopwatch.StartNew();
        int frames = 30;
        for (int i = 0; i < frames; i++) { _p.Update(DT, _r.Camera, _target); }
        double ms = sw.Elapsed.TotalMilliseconds / frames;
        GD.Print($"perf: 100 fireplaces, {_p.Active} active emitters, {_p.DormantCount} dormant, {_p.Drawn} sprites: {ms:0.00} ms per update (CPU)");
        Check(ms < 40, $"particle update of 100 fireplaces under 40 ms ({ms:0.0} ms, {_p.Drawn} sprites)");
        Check(_p.Drawn <= 3000, $"sprites capped by the setting ({_p.Drawn} <= 3000)");
        await Shot("fx_many.png");
        foreach (var m in fires) m.QueueFree();
        await Run(0.2);
    }
}
