// The effects layer of a running game: the models' particle emitters (Particles), the game's effect models (ModelFx
// + GameFx) and what feeds them - the world's events (feedback.js handle: hits, impacts, deaths, destruction,
// level-ups, spawns, buildings placed / finished / damaged, inventions, heal and item glows, the rocket launchers'
// smoke, the MIRAGE moves' fx / fxAt / fxFly / blast / camshake, the Aje quicksand and tornado, the Ninigi
// fireworks), the clips' effect events (Sim AnimFx: camera shakes), the state the simulation keeps (the spirits, a
// finished temple's ready effect), the missions' effects (EFCT: the particle database's models,
// MissionHost.Effects), the question marks (the game's own models) and the cutscenes' effect items. Everything is
// the original game's own models and emitters; the browser remake's drawn effects (DrawnFx) only stand in when the
// data lacks a model (docs/EFFECTS.md lists what is drawn and why). View-only: it reads the simulation, never writes.
//
// The session calls HandleEvents with each frame's events (before its sounds: an impact whose effect model played
// has that model's sound - ImpactPlayed), Step once per frame after the entities moved.
//
// Ported from remake/src/ui/feedback.js (the effects of handle, itemGlow, healGlow), ui/logs.js leafBlast,
// game/world.js damageFx / spiritTick (the drawn part), game/systems/buildings.js resin (its fire),
// game/entities.js readyFx, game/campaign/engine.js effectTick, ui/mission.js questionMark (the model) and
// ui/cutscene.js (the fx items) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.View.Models;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.View.Fx;

public partial class WorldFx : Node3D
{
    static readonly PwRemake.Sim.Core.Logger L = Log.For("View.Fx");
    public readonly Particles Particles;
    public readonly ModelFx Mfx;
    public readonly DrawnFx Fx;
    public readonly GameFx Gfx;
    readonly Func<GameWorld> _w;
    readonly ModelLibrary _lib;
    readonly Func<Entity, ModelInstance?> _model;
    readonly Func<Entity, Vector3> _pos;
    /// <summary>is a place visible to the player now / explored (fog of war); null: everything</summary>
    public new Func<double, double, bool>? Visible;
    public Func<double, double, bool>? Explored;
    /// <summary>the effects a mission's triggers play (EFCT: MissionHost.Effects)</summary>
    public List<MissionEffect>? MissionEffects;
    /// <summary>the question marks to show: (key, model, x, z in game coordinates)</summary>
    public Func<IEnumerable<(string Key, string Model, double X, double Z)>>? QuestionMarks;
    readonly HashSet<WorldEvent> _impactsPlayed = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Spirit, ModelFx.Entry> _spirits = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<Unit> _fireworks = new(ReferenceEqualityComparer.Instance);
    /// <summary>are the spirits shown (MainController.UpdateSpirits: while one of the player's own aje_shaman is
    /// selected); null: never</summary>
    public Func<bool>? ShowSpirits;
    /// <summary>the dust effects of two ruins' "destroy" clips (the GSF event table, frame 1)</summary>
    public static readonly Dictionary<string, string> DEM_FX = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hu_temple_dest"] = "hu_temple_dem_fx", ["hu_small_wall_gate_dest"] = "hu_wall_dem_fx",
    };
    /// <summary>the shooters whose shot sends event 36 (CRocketLauncherSmoke: FightingObj.CreateProjectileAndShoot)</summary>
    public static readonly HashSet<string> ROCKET_SMOKE = new(StringComparer.OrdinalIgnoreCase) { "seas_rocketman", "ninigi_mortar" };
    readonly Dictionary<Building, (ModelInstance Host, ModelInstance Fx)> _ready = new();
    readonly Dictionary<MissionEffect, ModelInstance> _missionFx = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, (string Model, ModelFx.Entry E)> _marks = new();
    double _scanT;
    readonly Random _rnd = new();
    string _level = "on";

    GameWorld W => _w();

    /// <summary>world; lib: the models; particles: the models' emitters (WorldView.Particles: the map's waterfalls and
    /// every model instance's emitters - it updates them); model / pos: an entity's drawn model and where it is drawn</summary>
    public WorldFx(Func<GameWorld> world, ModelLibrary lib, Particles particles, Func<Entity, ModelInstance?> model, Func<Entity, Vector3> pos, Func<double, double, double> height)
    {
        Name = "Effects";
        _w = world; _lib = lib; _model = model; _pos = pos;
        Particles = particles;
        Mfx = new ModelFx(lib);
        Fx = new DrawnFx(height);
        Gfx = new GameFx(world, lib, Mfx, model, pos);
        AddChild(Mfx);
        AddChild(Fx);
    }

    /// <summary>the "Particles" setting: off | low | on | high (off: no emitters, no drawn sprites)</summary>
    public string Level
    {
        get => _level;
        set
        {
            _level = value is "off" or "low" or "on" or "high" ? value : "on";
            Particles.Level = _level;
            Fx.Enabled = _level != "off";
        }
    }

    static Vector3 V(V3 p) => new((float)p.X, (float)p.Y, (float)p.Z);
    bool Vis(V3 p) => Visible?.Invoke(p.X, p.Z) ?? true;
    bool Vis(Vector3 p) => Visible?.Invoke(p.X, p.Z) ?? true;

    /// <summary>did the game's effect model play for this impact event (its sound then is the model's)?</summary>
    public bool ImpactPlayed(WorldEvent e) => _impactsPlayed.Contains(e);

    // ======================================================================================== events
    /// <summary>the effects of a frame's world events (feedback.js handle)</summary>
    public void HandleEvents(IReadOnlyList<WorldEvent> events)
    {
        _impactsPlayed.Clear();
        foreach (var e in events)
        {
            try { Handle(e); }
            catch (Exception ex) { L.WarnOnce("ev:" + e.Type, $"effect of {e.Type} failed: {ex.Message}"); }
        }
    }

    void Handle(WorldEvent e)
    {
        var G = Gfx;
        switch (e.Type)
        {
            // an item taken: the glow on its carrier while it carries it (FightingObj.AddItem: "fx_unique_pickup")
            case "itemTaken": if (e.U("unit") is { } iu) G.ItemGlow(iu, true); break;
            case "itemDropped": if (e.U("unit") is { } du) G.ItemGlow(du, false); break;
            case "placed": if (e.B("building") is { } pb && Vis(pb.Pos)) G.Placed(pb); break;
            case "built": if (e.B("building") is { } bb && Vis(bb.Pos)) G.Built(bb); break;
            case "spawnfx": if (e.U("unit") is { } su && Vis(su.Pos)) G.Spawned(su); break;        // a mission's unit leaves its building (CTheLite)
            case "trained": if (e.U("unit") is { } tu && Vis(tu.Pos)) G.Spawned(tu); break;
            case "upgrade": if (e.E("entity") is { } ue && Vis(ue.Pos)) G.Invented(ue); break;
            case "levelup":
                if (e.U("unit") is { } lu && Vis(lu.Pos) && !G.LevelUp(lu))
                    Fx.Spawn(DrawnFx.Kind.Glow, _pos(lu) + new Vector3(0, (float)lu.Height * 0.6f, 0), 3, 7, 0.8f);
                break;
            case "died":
                {
                    if (e.E("entity") is not { } en || !Vis(en.Pos)) break;
                    var p = _pos(en); p.Y += (float)(en is Unit u ? u.Height : 2) * 0.4f;
                    if (!G.Died(en)) { if (en.Cls == "VHCL") Fx.Explosion(p, 1); else Fx.Blood(p, 12); }
                    break;
                }
            // a building destroyed: its ruin "<gfx>_dest" plays its clip "destroy" (EntityViews), whose emitters are the
            // explosion, debris, dust and smoke; two ruins also start a dust effect at frame 1 of the clip (the GSF
            // event table: CHu_Temple_Dem_Fx / CHu_Wall_Dem_Fx, 4 s)
            case "destroyed":
                {
                    if (e.E("entity") is not Building b || e.Data["silent"] is true || b.Ruin == null) break;
                    if (!(Explored?.Invoke(b.Pos.X, b.Pos.Z) ?? true)) break;
                    if (DEM_FX.TryGetValue(b.Ruin, out var dem))
                        Mfx.At(dem, new Vector3((float)b.Origin.X, (float)b.Origin.Y, (float)b.Origin.Z), 4.0, (float)b.Rot);
                    break;
                }
            // the smoke of a rocket launcher's shot (FightingObj CreateProjectileAndShoot: event 36 for seas_rocketman
            // and ninigi_mortar -> CRocketLauncherSmoke: fx_seas_rpg at the link "Proj", 2.5 s); other guns have no
            // scripted muzzle effect (the weapon models' own emitters fire while they shoot)
            case "shoot":
                {
                    if (e.E("attacker") is not Unit su2 || !ROCKET_SMOKE.Contains(su2.Name) || !Vis(su2.Pos)) break;
                    var m = _model(su2);
                    var l = m?.FindLink("Proj");
                    var at = l != null && l.IsInsideTree() ? l.GlobalPosition : e.Data["pos"] is V3 sp ? V(sp) : _pos(su2);
                    var fx = Mfx.At("fx_seas_rpg", at, 2.5);
                    if (fx != null && l != null && l.IsInsideTree()) fx.Obj.Basis = l.GlobalBasis.Orthonormalized();
                    break;
                }
            case "impact":
                {
                    if (e.Data["pos"] is not V3 p || !Vis(p)) break;
                    var w = e.Data["weapon"] as Weapon;
                    // the game's hit effect; without its models, the drawn ones below
                    if (G.Impact(w, e.E("target"), V(p))) { _impactsPlayed.Add(e); break; }
                    double sp = e.Data["big"] is true ? 10 : w?.Splash ?? 0;     // (big: mines / torpedo turtle blasts)
                    if (string.IsNullOrEmpty(w?.Projectile)) break;           // (no projectile: the original shows nothing)
                    if (e.Data["splash"] is true) Fx.Explosion(V(p), sp > 6 ? 1.2f : 0.6f);
                    else Fx.Dust(V(p), 0.6f);
                    break;
                }
            case "hit":
                {
                    if (e.E("target") is not { } t || !Vis(t.Pos)) break;
                    bool melee = e.Data["melee"] is true;
                    var from = e.E("attacker") is { } a ? _pos(a) : (Vector3?)null;
                    if (!melee || !G.Melee(t, from))
                        if (t is Unit tu2 && t.Cls != "VHCL" && melee) Fx.Blood(_pos(t) + new Vector3(0, (float)(tu2.Height > 0 ? tu2.Height : 2) * 0.5f, 0), 4);
                    break;
                }
            case "healfx": if (e.E("to") is Unit hu) G.HealGlow(hu); break;
            case "damagestage": if (e.E("entity") is { } de && Vis(de.Pos)) G.Damaged(de); break;
            // the MIRAGE moves (moves_mirage): effect models on units / at points / flying, blasts, shakes
            case "fx":
                {
                    if (e.E("entity") is not { } fe) break;
                    var key = (e.Str("key") ?? "") + ":" + fe.Id;
                    double until = e.Num("until");
                    if (until > W.Time) Mfx.Attach(G.Follow(fe, e.Str("link")), e.Str("gfx"), until - W.Time, key, e.Num("offset"));
                    else Mfx.Stop(key);
                    break;
                }
            case "fxAt":
                {
                    if (e.Data["pos"] is not V3 p || !Vis(p)) break;
                    Mfx.At(e.Str("gfx"), V(p), e.Num("until") - W.Time, e.Data["yaw"] is double yaw ? (float)yaw : null, loop: e.Data["loop"] is true);
                    break;
                }
            case "fxFly":
                {
                    if (e.Data["from"] is not V3 f || e.Data["to"] is not V3 to || !Vis(to)) break;
                    Mfx.FlyTo(e.Str("gfx"), V(f), V(to), e.Num("T", 1));
                    break;
                }
            case "camshake": if (e.Data["pos"] is V3 cp) G.CamShake(e.Str("cls"), V(cp)); break;
            case "blast": if (e.Data["pos"] is V3 bp && Vis(bp) && !G.Blast(V(bp))) Fx.Explosion(V(bp), 1.5f); break;
            // the Aje quicksand and tornado (moves.js W.fx.dust): the game's objects Aje_Quicksand_Trap (CQuicksand,
            // 30 s) and Aje_Tornado (CTornadoObj: "swirl" looping while it lasts; the tornado's ticks keep it up)
            case "dust":
                {
                    if (e.Data["pos"] is not V3 dp) break;
                    var kind = e.Str("kind");
                    var key = $"{kind}:{Math.Round(dp.X)}:{Math.Round(dp.Z)}";
                    var gfx = kind == "quicksand" ? "aje_quicksand_trap" : kind == "tornado" ? "aje_tornado" : null;
                    double life = kind == "quicksand" ? e.Num("life", 30) : 1.5;
                    if (gfx != null && Mfx.Extend(key, Mfx.Time + life)) break;
                    var made = gfx != null ? Mfx.At(gfx, new Vector3((float)dp.X, (float)W.Height(dp.X, dp.Z), (float)dp.Z), life, loop: true, key: key) : null;
                    if (made != null)
                    {
                        if (kind == "quicksand") made.Obj.SetBuildingState(4, 0, 1);    // (a finished, intact building: its damage fires off)
                        break;
                    }
                    if (Vis(dp)) Fx.Dust(V(dp), (float)e.Num("r", 2));            // (data without the model: the JS's dust)
                    break;
                }
            // the Ninigi fireworks (Fireworks.usl: the object ninigi_fireworks - CFireWorkObj plays "anim" once, gone
            // after 10 s) at the first of the move's six glow events
            case "glow":
                {
                    if (e.Data["pos"] is not V3 gp) break;
                    if (e.Str("kind") == "firework" && e.U("unit") is { } fu)
                    {
                        if (e.Num("i") == 0 && Mfx.At("ninigi_fireworks", _pos(fu), 10.0, (float)fu.Heading) != null) { _fireworks.Add(fu); break; }
                        if (_fireworks.Contains(fu)) { if (e.Num("i") >= 5) _fireworks.Remove(fu); break; }
                    }
                    var c = e.Data["color"] is int ci ? DrawnFx.Hex(ci) : e.Data["color"] is double cd ? DrawnFx.Hex((int)cd) : Godot.Colors.White;
                    Fx.Spawn(DrawnFx.Kind.Glow, V(gp) + new Vector3(0, 20 + (float)_rnd.NextDouble() * 20, 0), (float)e.Num("size", 6), (float)e.Num("size1", 16), (float)e.Num("life", 1.2), color: c);
                    break;
                }
        }
    }

    /// <summary>a felled tree's crown hit the ground (ui/logs.js leafBlast): 8 m from the trunk in the fall direction
    /// (the clip's digit x 45 degrees: CCamShakeHarvestTrees)</summary>
    public void LeafFall(ResNode n, string? clip)
    {
        int d = clip is { Length: > 0 } && char.IsDigit(clip[^1]) ? clip[^1] - '0' : 0;
        double a = d * Math.PI / 4;
        double x = n.Pos.X + Math.Cos(a) * 8, z = n.Pos.Z + Math.Sin(a) * 8;
        if (!(Visible?.Invoke(n.Pos.X, n.Pos.Z) ?? true)) return;
        Gfx.LeafBlast(new Vector3((float)x, (float)W.Height(x, z), (float)z));
    }

    /// <summary>a cutscene's effect item (ui/cutscene.js 'fx'): the model of its path at its place for its length</summary>
    public void CutsceneEffect(string path, Vector3 pos, double len)
    {
        var name = path.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (Mfx.At(name, pos, Math.Max(0.5, len)) == null) L.WarnOnce("cine:" + name, $"cutscene effect {name} has no model");
    }

    // ======================================================================================== per frame
    /// <summary>one frame (before the particles' update): dt the frame's time, gdt the game time of the frame (0 while
    /// paused), fxDt the effect models' time (the game's, or the frame's in a cutscene)</summary>
    public void Step(double dt, double gdt, double fxDt)
    {
        try
        {
            Gfx.Status(dt);
            _scanT -= dt;
            if (_scanT <= 0) { _scanT = 0.25; ReadyFx(); MissionFx(); Marks(); Spirits(); }
        }
        catch (Exception ex) { L.WarnOnce("step", $"effects: {ex.Message}"); }
        Mfx.Update(fxDt);   // (the particles: WorldView.Step / the caller, after this)
        // the clips of the ready effects (game time) and the missions' effects (the effects' time)
        foreach (var (_, r) in _ready) if (r.Fx.Anim != null && GodotObject.IsInstanceValid(r.Fx)) r.Fx.Advance(gdt);
        foreach (var (_, m) in _missionFx) if (m != null && m.Anim != null && GodotObject.IsInstanceValid(m)) m.Advance(fxDt);
        Fx.Update(dt);
    }

    // (the smoke and fire of damaged buildings are the building models' own emitters - their damage-stage parts,
    // ModelInstance.SetBuildingState; the burning resin field is its model ninigi_resin_field_fire: EntityViews)

    // the spirits of the dead (the client's spirit objects show fx_ghost_big - PWClient.exe names it next to the class
    // BSpiritObj / type SPRT): only while the player has one of their own aje_shaman selected
    // (MainController.UpdateSpirits -> ShowSpirits)
    void Spirits()
    {
        bool show = ShowSpirits?.Invoke() == true;
        HashSet<Spirit>? alive = null;
        if (show)
            foreach (var s in W.Spirits)
            {
                if (!s.Alive) continue;
                (alive ??= new(ReferenceEqualityComparer.Instance)).Add(s);
                if (_spirits.TryGetValue(s, out var have) && !have.Freed) { have.Obj.Visible = Vis(s.Pos); continue; }
                var e = Mfx.At("fx_ghost_big", V(s.Pos), double.PositiveInfinity, anim: "");
                if (e == null) { L.WarnOnce("ghost", "the spirits' effect model fx_ghost_big is missing"); break; }
                e.Obj.Visible = Vis(s.Pos);
                _spirits[s] = e;
            }
        if (_spirits.Count == 0) return;
        foreach (var s in new List<Spirit>(_spirits.Keys))
            if (alive == null || !alive.Contains(s)) { _spirits[s].Until = 0; _spirits.Remove(s); }
    }

    public int SpiritCount => _spirits.Count;

    /// <summary>an effect event of a clip (Sim AnimFx: the brachiosaurus' footfalls and fall, the mammoth's stomp):
    /// the camera shake of its class; CBrachioDying and CPlaceBuildingFX also show Placeping_Building_Fx (1.55 s)</summary>
    public void AnimEffect(string cls, Entity e)
    {
        if (!Vis(e.Pos)) return;
        var p = _pos(e);
        Gfx.CamShake(cls, p);
        if (cls is "CBrachioDying" or "CPlaceBuildingFX") Mfx.At("placeping_building_fx", p, 1.55, (float)e.Heading);
    }

    // the effect a finished building shows for good (entities.js readyFx: the temples' healing glow)
    void ReadyFx()
    {
        foreach (var b in W.Buildings)
        {
            // (an infected temple - stage 2 and 3, docs/INFECTED_LOOK.md - shows no healing area: the tall rays of light,
            // fx_heal_area, stood over the Outbreak's plague villages in daylight; its healing itself is the simulation's)
            bool want = b.Alive && b.ReadyFx != null && !(b.Infection >= 2 && b.ReadyFx == "fx_heal_area");
            _ready.TryGetValue(b, out var r);
            var host = want ? _model(b) : null;
            if (r.Fx != null && (!want || host != r.Host || !GodotObject.IsInstanceValid(r.Host)))
            {
                if (GodotObject.IsInstanceValid(r.Fx)) r.Fx.QueueFree();
                _ready.Remove(b);
                r = default;
            }
            if (!want || host == null || r.Fx != null) continue;
            var fx = _lib.Instantiate(b.ReadyFx!.ToLowerInvariant());
            if (fx == null) continue;
            host.AddChild(fx);         // (the model's own frame: as the JS adds it to the building's object)
            fx.Play(fx.Anim?.Pick("anim", "standanim"));
            _ready[b] = (host, fx);
        }
    }

    // a mission's effects (EFCT, engine.js effectTick): the model plays its clip "anim" (SetSLEAnim) - the emitters it
    // drives spray while it runs - until the effect's time is up
    void MissionFx()
    {
        var list = MissionEffects;
        if (list == null && _missionFx.Count == 0) return;
        if (list != null)
            foreach (var e in list)
            {
                if (_missionFx.ContainsKey(e) || e.Until <= W.Time) continue;
                var m = _lib.Instantiate(e.Gfx.ToLowerInvariant());
                if (m == null) { L.WarnOnce("efct:" + e.Gfx, $"effect model {e.Gfx} is missing"); _missionFx[e] = null!; continue; }
                m.Position = new Vector3((float)e.X, (float)W.Height(e.X, e.Z), (float)e.Z);
                AddChild(m);
                var a = m.Anim?.Pick("anim") ?? FirstClip(m.Template);
                if (a != null) m.Play(a);
                _missionFx[e] = m;
            }
        List<MissionEffect>? gone = null;
        foreach (var (e, m) in _missionFx)
        {
            if (e.Until > W.Time && list != null && list.Contains(e)) continue;
            if (m != null && GodotObject.IsInstanceValid(m)) m.QueueFree();
            (gone ??= new()).Add(e);
        }
        if (gone != null) foreach (var e in gone) _missionFx.Remove(e);
    }

    static string? FirstClip(ModelTemplate t) { foreach (var k in t.ClipNames.Keys) if (!k.Contains('#')) return k; return null; }

    // the question marks (QuestionMark.usl SetGFX: questionmark_red / _green / _yellow, exclamation_yellow - their
    // emitters glow, "anim" turns them), on the ground; a place the player has not seen yet does not show its mark
    void Marks()
    {
        var want = new HashSet<string>();
        if (QuestionMarks != null)
            foreach (var (key, model, x, z) in QuestionMarks())
            {
                if (string.IsNullOrEmpty(model)) continue;
                want.Add(key);
                if (_marks.TryGetValue(key, out var have) && (have.Model != model || have.E.Freed)) { have.E.Until = 0; _marks.Remove(key); }
                if (!_marks.ContainsKey(key))
                {
                    var e = Mfx.At(model, new Vector3((float)x, (float)W.Height(x, z), (float)z), 1e9, loop: true);
                    if (e == null) continue;
                    _marks[key] = (model, e);
                }
                _marks[key].E.Obj.Visible = Explored?.Invoke(x, z) ?? true;
            }
        foreach (var k in new List<string>(_marks.Keys)) if (!want.Contains(k)) { _marks[k].E.Until = 0; _marks.Remove(k); }
    }

    public int MarkCount => _marks.Count;
    public int MissionFxCount => _missionFx.Count;
    public int ReadyFxCount => _ready.Count;

    public void Clear()
    {
        Mfx.Clear(); Fx.Clear();
        foreach (var (_, r) in _ready) if (GodotObject.IsInstanceValid(r.Fx)) r.Fx.QueueFree();
        _ready.Clear();
        foreach (var (_, m) in _missionFx) if (m != null && GodotObject.IsInstanceValid(m)) m.QueueFree();
        _missionFx.Clear(); _marks.Clear(); _spirits.Clear(); _fireworks.Clear();
    }
}
