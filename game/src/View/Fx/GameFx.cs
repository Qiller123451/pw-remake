// The original game's own effects for world events: projectile and melee hits, deaths, spawns, level-ups, building
// placement / completion / damage, inventions, poison and ice, felled trees - and the effects of the MIRAGE moves
// (moves_mirage). They are effect models (ModelFx) chosen the way the original client scripts choose them.
//
// Reference: the browser remake's docs/GAME_EFFECTS.md, from Data/Base/Scripts/Game/misc/Effects.usl (E), Game.usl
// (G) and the server's FightingObj.usl. The particle database (sequencer/particles.txt) is only for mission scripts
// (EFCT: WorldFx.MissionEffects).
//
// Ported from remake/src/ui/gamefx.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.View.Models;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.View.Fx;

public sealed class GameFx
{
    // map setting -> the key in the effect names (E:49-62; anything else is Savanna)
    static readonly Dictionary<string, string> LAND = new() { ["Northland"] = "nor", ["Savanna"] = "sav", ["Jungle"] = "jng", ["Icewaste"] = "ice", ["Ashvalley"] = "ash" };
    // EvaluateProjectile (E:164-274): the first prefix of the projectile's model name that matches gives the hit type
    static readonly (string Prefix, string Type)[] PROJ_TYPE =
    {
        ("ninigi_muraeno_submarine_arrow", "sharp_med"), ("tesla_revolvercrossbow_arrow", "sharp_sml"), ("tesla_crazy_machine_arrow", "explo_sml"),
        ("ninigi_rocket_boat_arrow", "explo_big"), ("seas_wehrspinne_bullet", "explo_big"), ("ninigi_shuriken_arrow", "sharp_sml"),
        ("ninigi_spear_b_arrow", "sharp_med"), ("ninigi_spear_c_arrow", "sharp_med"), ("ninigi_spear_d_arrow", "sharp_med"), ("ninigi_spear_e_arrow", "sharp_med"),
        ("babbage_pistol_arrow", "sharp_sml"), ("seas_carrier_bullet", "sharp_med"), ("ninigi_mortar_arrow", "explo_sml"), ("hu_slingshot_stone", "blunt_sml"),
        ("druid_staff_arrow", "sharp_med"), ("aje_spear_a_arrow", "sharp_med"), ("aje_spear_b_arrow", "sharp_med"), ("aje_spear_c_arrow", "sharp_med"),
        ("aje_spear_d_arrow", "sharp_med"), ("aje_spear_e_arrow", "sharp_med"), ("hu_spear_b_arrow", "sharp_med"), ("hu_spear_c_arrow", "sharp_med"),
        ("hu_spear_d_arrow", "sharp_med"), ("hu_spear_e_arrow", "sharp_med"), ("hu_cannonball", "blunt_big"), ("ninigi_bullet", "gatling"),
        ("ninigi_arrow", "sharp_sml"), ("aje_molotov", "poison_med"), ("seas_rocket", "explo_med"), ("hu_arrow", "sharp_sml"), ("hu_bolt", "sharp_sml"),
        ("seas_hq_big_cannon_projectile", "big_bertha"), ("schliemann_flintlock_arrow", "sharp_sml"), ("bela_knickknackbow_arrow", "sharp_sml"),
        ("ninigi_icespear_c_arrow", "sharp_med"), ("ninigi_icespear_d_arrow", "sharp_med"), ("ninigi_icespear_e_arrow", "sharp_med"),
        ("aje_ammo_dilo_spittle", "poison_sml"), ("lovelace_musket_arrow", "sharp_sml"), ("hu_dragon_boat_arrow", "sharp_big"),
        ("ninigi_gatling_arrow", "gatling"), ("ninigi_rb_projektil", "explo_big"), ("ninigi_small_rocket", "explo_med"), ("seas_turret_bullet", "gatling"),
        ("hu_ballista_arrow", "sharp_big"), ("ninigi_cannonball", "explo_big"), ("aje_ammo_stone", "poison_big"), ("aje_ammo_dino", "blunt_sml"),
        ("ninigi_rocket", "explo_big"), ("seas_torpedo", "explo_big"), ("seas_bullet", "sharp_sml"), ("aje_arrow", "sharp_sml"), ("hu_trunk", "blunt_big"),
    };

    public static string ProjectileType(string? gfx)
    {
        var n = (gfx ?? "").ToLowerInvariant();
        foreach (var (p, t) in PROJ_TYPE) if (n.StartsWith(p, StringComparison.Ordinal)) return t;
        return "blunt_sml";
    }

    static readonly HashSet<string> TRIBES = new() { "hu", "aje", "ninigi", "seas" };
    /// <summary>camera shakes (Effects.usl ShakeCameraTheGundlachStyle calls): duration s, strength, fade-out %, range m</summary>
    public static readonly Dictionary<string, (double Dur, double Strength, double Fade, double Range)> SHAKES = new()
    {
        ["CPlaceBuildingFX"] = (1.0, 5, 100, 60),        // a building placed (Building.usl:174 -> Game.usl:972); the mammoth's stomp
        ["CBrachioDying"] = (1.0, 40, 75, 60),          // the brachiosaurus falls (dying, frame 58)
        ["CBrachioWalking"] = (0.5, 5, 75, 30),         // its footfalls (walk_1 / walk_2 / walk_left / walk_right)
        ["CVehicleExplosion"] = (0.5, 5, 100, 60),      // the torpedo turtle explodes (Ship.usl:1540)
        ["CDestructionHit"] = (0.5, 5, 100, 30),        // an object drops below 50 % and 25 % (FightingObj.usl:3054)
        ["CCamShakeHarvestTrees"] = (1.0, 5, 100, 20),  // a felled tree hits the ground
        ["CBigBuildingExplosion"] = (1.0, 40, 75, 60),
    };
    static readonly Dictionary<string, string> CASTE_SPAWN = new() { ["res"] = "spawnping_char_res_fx", ["nat"] = "spawnping_char_nat_fx", ["tec"] = "spawnping_char_tec_fx" };
    /// <summary>the effect models used here (the session loads them with the map: main.js GAME_FX_MODEL)</summary>
    public static readonly System.Text.RegularExpressions.Regex GAME_FX_MODEL = new(@"^(hit_|fx_impact_|spawnping_|readyping_|placeping_|deathping_animal_|levelup_char_fx$|leave_blast$|vehicle_explosion$|explo_logcannon_building$|fx_poison|fx_ice_freeze$|hu_wallstones$"
        // and those of WorldFx (the original effect models tools/prepare/effect_models.py adds to the data)
        + @"|hu_temple_dem_fx$|hu_wall_dem_fx$|fx_seas_rpg$|fx_ghost_big$|ninigi_fireworks$|aje_tornado$|aje_quicksand_trap$)");

    readonly Func<GameWorld> _w;
    public readonly ModelFx M;
    readonly Func<Entity, ModelInstance?> _model;
    readonly Func<Entity, Vector3> _pos;
    /// <summary>a camera shake (RtsCamera.AddShake: duration, strength, fade %, position, range)</summary>
    public Action<double, double, double, Vector3, double>? Shake;
    /// <summary>the sound of an effect model that played (its "FX_&lt;model&gt;" event: Effects_snd.txt)</summary>
    public Action<string, Vector3>? Sound;
    double _statusT;
    readonly Random _rnd = new();

    /// <summary>world; mfx: the effect models; model: an entity's drawn model; pos: where it is drawn</summary>
    public GameFx(Func<GameWorld> world, ModelLibrary lib, ModelFx mfx, Func<Entity, ModelInstance?> model, Func<Entity, Vector3> pos)
    {
        _w = world; Lib = lib; M = mfx; _model = model; _pos = pos;
    }

    GameWorld W => _w();
    string Land() => LAND.TryGetValue(W.Setting ?? "", out var k) ? k : "sav";
    static string TribeOf(Entity e)
    {
        var t = (e.Def?.Tribe is { Length: > 0 } dt ? dt : e.Owner?.Tribe ?? "").ToLowerInvariant();
        return TRIBES.Contains(t) ? t : "";
    }
    static Vector3 V(PwRemake.Sim.Core.V3 p) => new((float)p.X, (float)p.Y, (float)p.Z);
    static Color? Party(Entity e) => e.Owner?.PartyColor is int c ? DrawnFx.Hex(c) : null;
    static float Height(Entity e) => e is Unit u ? (float)u.Height : e is Building b ? (float)b.Ht : 2;

    /// <summary>OnProjectileImpact (E:27-162): "hit_" + where + [tribe_] + type at the projectile, 4 s; a flash on units</summary>
    public (string[] Names, bool Flash) ImpactName(string? gfx, Entity? target, Vector3 pos)
    {
        string type = ProjectileType(gfx);
        bool gat = type == "gatling";
        if (target == null)
        {
            bool water = !gat && W.WaterLevel is double wl && W.Height(pos.X, pos.Z) < wl;
            return (new[] { water ? $"hit_water_{type}" : $"hit_land_{Land()}_{type}", $"hit_land_ash_{type}", "hit_water_blunt_big" }, false);
        }
        var c = target.Cls;
        if (c == "CHTR" || c == "ANML")
        {
            var t = c == "CHTR" && !gat && type != "big_bertha" ? type[..^3] + "sml" : type;
            return (new[] { $"hit_creature_{t}" }, true);
        }
        if (c == "VHCL" || c == "SHIP" || c == "BLDG")
        {
            var tr = gat ? "" : TribeOf(target);
            return (new[] { tr.Length > 0 ? $"hit_object_{tr}_{type}" : $"hit_object_{type}", $"hit_object_{type}", $"hit_object_hu_{type}" }, c == "VHCL");
        }
        return (new[] { $"hit_land_{Land()}_{type}" }, false);
    }

    public string? First(IEnumerable<string> names) { foreach (var n in names) if (M.Has(n)) return n; return null; }

    /// <summary>a projectile's impact (false: no effect model - the drawn effects stand in)</summary>
    public bool Impact(Weapon? weapon, Entity? target, Vector3 pos)
    {
        var gfx = weapon?.Projectile;
        if (string.IsNullOrEmpty(gfx)) return false;
        var (names, flash) = ImpactName(gfx, target, pos);
        var n = First(names);
        if (n == null) return false;
        M.At(n, pos, 4.0);
        if (flash) M.At(_rnd.NextDouble() < 0.5 ? "fx_impact_proj_01" : "fx_impact_proj_02", pos, 2.5);
        Sound?.Invoke(n, pos);
        return true;
    }

    /// <summary>a melee hit on a character or animal (EID_HitReactionOVLAnim -> CBloodHit + CHitFlash, E:422-538)</summary>
    public bool Melee(Entity? t, Vector3? from)
    {
        if (t is not Unit u || u.Cls == "VHCL" || u.Cls == "SHIP") return false;
        var m = _model(u);
        if (m == null) return false;
        if (u.St.T.GetValueOrDefault("trapped") > W.Time || u.Knock != null) return true;
        Vector3 p;
        var l = u.Cls == "CHTR" ? m.FindLink("Blfa") : null;
        var pos = _pos(u);
        if (l != null && l.IsInsideTree()) p = l.GlobalPosition;
        else
        {
            // others: half the body's width to the side the blow came from, 3 m up
            double a = from is Vector3 f ? Math.Atan2(f.X - pos.X, f.Z - pos.Z) : 0;
            p = new Vector3(pos.X + (float)(Math.Sin(a) * u.Radius * 0.5), pos.Y + Math.Min(3, (float)(u.Height > 0 ? u.Height : 2) * 0.6f), pos.Z + (float)(Math.Cos(a) * u.Radius * 0.5));
        }
        M.At(u.Cls == "CHTR" ? "hit_creature_sharp_sml" : "hit_creature_sharp_med", p, 2.0);
        M.At("fx_impact_0" + (1 + _rnd.Next(6)), p, 2.5);
        return true;
    }

    /// <summary>DoLevelUpAnim -> CLevelUpFX_Transport / CLevelUpFX (E:792-807): the owner-coloured ring, its clip</summary>
    public bool LevelUp(Unit u)
    {
        var p = _pos(u);
        if (u.Cls != "CHTR") p.Y += 0.75f * (float)(u.Height > 0 ? u.Height : 2);
        return M.At("levelup_char_fx", p, ClipLen("levelup_char_fx", 2.0), party: Party(u)) != null;
    }

    /// <summary>a unit leaves its building (CTheLite: by caste for characters, Spawnping_Animal_Fx for animals)</summary>
    public bool Spawned(Unit u)
    {
        if (u.Cls == "CHTR")
        {
            var caste = W.Data.Column(u.Name, u.RulesOwner());
            return M.At(CASTE_SPAWN.GetValueOrDefault(caste) ?? CASTE_SPAWN["tec"], _pos(u), 1.2, party: Party(u)) != null;
        }
        if (u.Cls == "ANML") return M.At("spawnping_animal_fx", _pos(u), 2.85) != null;
        return false;
    }

    public bool Placed(Building b) { bool ok = M.At("placeping_building_fx", V(b.Pos), 1.55) != null; CamShake("CPlaceBuildingFX", V(b.Pos)); return ok; }
    public bool Built(Building b) => M.At("readyping_building_fx", V(b.Pos), 1.8, party: Party(b)) != null;
    public bool Invented(Entity? b) => b != null && M.At("readyping_invent_fx", V(b.Pos), 1.5, party: Party(b)) != null;

    /// <summary>death pings (character.usl:2281, Animal.usl:945): characters small, animals by radius; vehicles explode</summary>
    public bool Died(Entity u)
    {
        var pos = _pos(u);
        if (u.Cls == "VHCL") return M.At("vehicle_explosion", pos, 2.5) != null;
        if (u.Cls != "CHTR" && u.Cls != "ANML") return false;
        double r = u.Cls == "CHTR" ? 0 : u.Radius;
        return M.At(r <= 3.5 ? "deathping_animal_small_fx" : r <= 5 ? "deathping_animal_med_fx" : "deathping_animal_big_fx", pos, 3.0, party: Party(u)) != null;
    }

    /// <summary>a building or ship crossed 50 % / 25 % hit points (UpdateDestructionFlags -> CDestructionHit, E:1372)</summary>
    public bool Damaged(Entity b)
    {
        var p = V(b.Pos); p.Y += (b is Building bd && bd.Ht > 0 ? (float)bd.Ht : 4) * 0.5f;
        var n = b.Cls == "BLDG" ? "explo_logcannon_building" : $"hit_object_{(TribeOf(b) is { Length: > 0 } t ? t : "hu")}_blunt_big";
        bool ok = M.At(n, p, 2.5) != null;
        CamShake("CDestructionHit", V(b.Pos));
        return ok;
    }

    /// <summary>a felled tree's crown hits the ground (CCamShakeHarvestTrees, ui/logs.js): a cloud of leaves and the shake</summary>
    public bool LeafBlast(Vector3 p)
    {
        CamShake("CCamShakeHarvestTrees", p);
        return M.At("leave_blast", p, 2.4) != null;
    }

    /// <summary>an explosion on the ground (MIRAGE's meteor and walking bomb)</summary>
    public bool Blast(Vector3 pos)
    {
        var n = First(ImpactName("ninigi_rocket", null, pos).Names);
        if (n == null) return false;
        M.At(n, pos, 4.0);
        CamShake("CBigBuildingExplosion", pos);       // (the MIRAGE blasts: a guess - no client script of MIRAGE)
        return true;
    }

    /// <summary>the camera shakes of the original's effect classes (Effects.usl: duration s, strength, fade-out %, range m)</summary>
    public void CamShake(string? cls, Vector3 pos)
    {
        if (cls != null && SHAKES.TryGetValue(cls, out var k)) Shake?.Invoke(k.Dur, k.Strength, k.Fade, pos, k.Range);
    }

    double ClipLen(string gfx, double d)
    {
        var tpl = Lib.Template(gfx, null, true);
        if (tpl?.Clips != null && tpl.ClipNames.TryGetValue("anim", out var n) && tpl.Clips.GetAnimation(n) is Animation a) return a.Length;
        return d;
    }
    /// <summary>(the clip lengths of the effect models)</summary>
    public readonly ModelLibrary Lib;

    /// <summary>a follow function for an entity's link (or its position) - the effect goes when the entity does</summary>
    public Func<ModelFx.FollowPose?> Follow(Entity u, string? link = null)
    {
        return () =>
        {
            if (!u.Alive || u.Removed || u.Inside) return null;
            var m = _model(u);
            if (m == null || !GodotObject.IsInstanceValid(m)) return null;
            var l = link != null ? m.FindLink(link) : null;
            var p = l != null && l.IsInsideTree() ? l.GlobalPosition : _pos(u);
            return new ModelFx.FollowPose(p, m.Visible);
        };
    }

    /// <summary>poison (CPoisonEffect, linked while poisoned) and ice (CIcedEffect, 5 s) - checked a few times a second</summary>
    public void Status(double dt)
    {
        _statusT -= dt;
        if (_statusT > 0) return;
        _statusT = 0.25;
        foreach (var u in W.Units)
        {
            if (!u.Alive || u.Inside) continue;
            var m = _model(u);
            if (m == null || !m.Visible) continue;
            if (u.St.Poison != null)
            {
                var k = "poison:" + u.Id;
                if (!M.Extend(k, M.Time + 0.5)) M.Attach(Follow(u), u.Radius < 8 ? "fx_poison" : "fx_poison_big", 0.5, k);
            }
            if (u.St.TrapWhy == "iced" && u.St.T.GetValueOrDefault("trapped") > W.Time && !M.HasKey("ice:" + u.Id)) M.Attach(Follow(u), "fx_ice_freeze", 5.0, "ice:" + u.Id);
        }
    }

    /// <summary>the heal glow of a unit (feedback.js healGlow): 2.5 s, longer while it is healed</summary>
    public void HealGlow(Unit u)
    {
        var k = "heal:" + u.Id;
        if (!M.Extend(k, M.Time + 2.5)) M.Attach(Follow(u), "fx_heal_passive", 2.5, k);
    }

    /// <summary>an item taken: the glow on its carrier while it carries it (FightingObj.AddItem: "fx_unique_pickup"); dropped: gone</summary>
    public void ItemGlow(Unit u, bool on)
    {
        if (on) M.Attach(Follow(u), "fx_unique_pickup", double.PositiveInfinity, "item:" + u.Id);
        else M.Stop("item:" + u.Id);
    }
}
