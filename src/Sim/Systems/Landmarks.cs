// The map's special places: healing wells (CFountain), lakes of healing and of damage (CLakeOfHealing /
// CLakeOfDamage) and the level's weather program (the client's CWeatherMgr).
//
// Reference: MiscObj.usl CFountain (2115-2260), CLakeOfDamage / CLakeOfHealing (153-260), task/FountainHeal.usl,
// FightingObj.usl FountainHeal / HealMe / TakeDirectMeleeDmg, GameInputController.usl (the "healing" cursor and
// the right click on a well), Game/mgr/WeatherMgr.usl, ActionFactory.usl CActionWeather (trigger WTHR).
//
// A well (W.Wells: an entity of kind "well" around a Prop - the model the view draws) holds `maxhitpoints` of healing
// ("hitpoints" is what it holds now). A unit sent to it (the right click of characters, animals, vehicles, ships:
// order "fountain") walks there at its walking pace; within 30 m of it when the walk ends it gets what it lacks - or
// what is left - and the well refills: every second refill_dur-th of its store, until it is full.
// A lake (W.Lakes: {name, region, heal, amount, types}) every 3 s gives each fighting object in its region (a map
// region by name) the amount: damage (protection counts, not in a transport) or healing; `types` limits it to object
// types ("|CHTR|ANML|..."; empty: all).
// The weather (W.Weather: {tracks [{dur, type, wind, strength, intensity}], loop, i, t0, over}): the tracks of the
// level's Root/WeatherData one after the other, each `Duration` minutes long; looped or, at the end, no weather.
// Type 0 none, 1 snow, 2 rain (PWClient.exe "Disabled Snow Rain"). The view draws it (WeatherNow).
//
// Ported from remake/src/game/systems/landmarks.js (pw-exporter 98c7703). C#: a well is an Entity (orders target
// entities); its prop is World.Props' (updated by the world's step - the JS updated the props of wells here).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.World;

/// <summary>a healing well: the prop of the map and its store (landmarks.js prop.well)</summary>
public sealed partial class Well : Entity
{
    public readonly Prop Prop;
    public double Max, Fill, Refill;
    public double? RefillT;
    public string WellId = "";
    public Well(World w, Prop prop) : base(w, "well", null, prop.Pos.X, prop.Pos.Z) { Prop = prop; Pos = prop.Pos; Gfx = prop.Model; }
    /// <summary>the well can be used (its prop there and shown)</summary>
    public bool Usable => Alive && Prop.Alive && Prop.Visible;
}

/// <summary>a lake of healing or damage (landmarks.js addLake)</summary>
public sealed partial class Lake
{
    public string Name = "", Region = "";
    public bool Heal;
    public double Amount;
    public string Types = "";
    public double T;
}

/// <summary>a track of the weather program</summary>
public sealed record WeatherTrack(double Dur, int Type, double Intensity, double Wind, double Strength);

public sealed partial class WeatherProgram
{
    public string Name = "";
    public bool Loop;
    public List<WeatherTrack> Tracks = new();
    public int I;
    public double T0;
    public bool Over;

    /// <summary>the level's weather program from the level info's description tree (Root/WeatherData: Name, Loop,
    /// Tracks/Entry {Name, Duration (minutes), WindDir 0..7 (north, north-east ... clockwise: WeatherMgr's 0, 315, 270 ...
    /// degrees), WindStrength, Type, Intensity}); null: none</summary>
    public static WeatherProgram? FromTree(InfoNode? tree)
    {
        static InfoNode? Sub(InfoNode? n, string name) => n?.Subs.FirstOrDefault(s => s.Name == name);
        var wd = Sub(tree, "WeatherData");
        if (wd == null) return null;
        static string Val(InfoNode? n, string k) => Sub(n, k)?.Value ?? "";
        static double Num(string s) => Json.ParseNum(s) is var v && double.IsFinite(v) ? v : 0;
        var DIRS = new double[] { 0, 315, 270, 225, 180, 135, 90, 45 };
        var tracks = (Sub(wd, "Tracks")?.Subs ?? new List<InfoNode>()).Select(e =>
        {
            int wdir = (int)Num(Val(e, "WindDir"));
            return new WeatherTrack(Num(Val(e, "Duration")) * 60, (int)Num(Val(e, "Type")), Num(Val(e, "Intensity")), wdir >= 0 && wdir < 8 ? DIRS[wdir] : 0, Num(Val(e, "WindStrength")));
        }).ToList();
        return new WeatherProgram { Name = Val(wd, "Name"), Loop = Val(wd, "Loop") == "1", Tracks = tracks };
    }
}

public sealed partial class World
{
    /// <summary>FountainHeal: arrived when within 30 m (Abs2S &gt; 900: walk again)</summary>
    const double WELL_REACH = 30;
    /// <summary>the lakes' TIMER_DAMAGE</summary>
    const double LAKE_TICK = 3;
    static readonly System.Text.RegularExpressions.Regex WellUsers = new("^(CHTR|ANML|VHCL|SHIP)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ---------------------------------------------------------------- healing wells
    /// <summary>a map's well: its model, class, position, rotation, attributes (maps/source.js) -> the well, null when the
    /// model is missing</summary>
    public Well? PlaceWell(string model, string cls, double x, double z, double? y, double rot, double[]? q, IReadOnlyDictionary<string, string>? attr)
    {
        if (!Models.Has(model)) return null;
        var p = AddProp(new PropSpec { Model = model, X = x, Z = z, Y = y, Rot = rot, Q = q, Block = true, BlockRadius = 1.5 });
        return AddWell(p, attr, cls);
    }

    /// <summary>a Prop becomes a well; a = the object's attributes (hitpoints, maxhitpoints, refill_dur - missing ones have
    /// the defaults of attribs_def.txt: 100, 100, 120), cls its class</summary>
    public Well AddWell(Prop prop, IReadOnlyDictionary<string, string>? a, string cls = "")
    {
        double V(string k, double d) => a != null && a.TryGetValue(k, out var s) && s != "" ? (Json.ParseNum(s) is var n && double.IsFinite(n) ? n : 0) : d;
        double max = V("maxhitpoints", 100);
        var w = new Well(this, prop)
        {
            Max = max, Fill = Math.Min(max, V("hitpoints", 100)), Refill = V("refill_dur", 120), RefillT = null,
            WellId = "well" + Wells.Count,
        };
        w.Name = (cls != "" ? cls : prop.Model).ToLowerInvariant();
        w.Radius = PropRadius(prop);
        Wells.Add(w);
        return w;
    }

    public double PropRadius(Prop p)
    {
        if (Models.Box(p.Model) is not var (mn, mx)) return 2;
        return Math.Max(1, Math.Max(mx.X - mn.X, mx.Z - mn.Z) / 2);
    }

    /// <summary>who may be sent to a well (GameInputController GetAction "healing": characters, animals, vehicles, ships)</summary>
    public bool CanUseWell(Unit u, Entity? t) => u != null && u.Alive && (t is Well w ? w.Usable && WellUsers.IsMatch(u.Cls) : t is Building b && CanUseSeasWell(u, b));

    // task 'fountain' (CFountainHeal): walk to the well, at 30 m or less when the walk ends: drink
    partial void FountainUpdate(Unit u, double dt)
    {
        var t = u.Task;
        if (t.Target is Building sw) { SeasWellUpdate(u, sw, dt); return; }        // MIRAGE's SEAS well (MirageBuildings.cs)
        var w = t.Target as Well;
        if (w == null || !w.Usable) { FountainDone(u); return; }
        if (!t.Walking)
        {
            if (t.Tries > 4) { FountainDone(u); return; }
            t.Tries++;
            var (x, z) = ApproachWell(u, w);
            u.SetPath(x, z);
            t.Walking = true;
        }
        bool near = Math.Sqrt((w.Pos.X - u.Pos.X) * (w.Pos.X - u.Pos.X) + (w.Pos.Z - u.Pos.Z) * (w.Pos.Z - u.Pos.Z)) <= w.Radius + 3;
        if (u.Path.Count > 0 && !near) { u.MoveAnim(u.Steer(dt, u.Speed), false); return; }
        u.Path = new NavPath(); u.Vel = V3.Zero;
        if (Math.Sqrt((w.Pos.X - u.Pos.X) * (w.Pos.X - u.Pos.X) + (w.Pos.Z - u.Pos.Z) * (w.Pos.Z - u.Pos.Z)) > WELL_REACH) { t.Walking = false; return; }
        DrinkAt(w, u);
        u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z;
        FountainDone(u);
    }

    /// <summary>the heal walk is over (healed, the place gone or empty, no way there): idle, or the order queued behind it
    /// (UnitTask.Then: the computer player's walk back - MIRAGE AiGoalUseFountains "Q_Action /Walk")</summary>
    void FountainDone(Unit u)
    {
        var next = u.Task.Then;
        u.Task = UnitTask.Idle();
        if (next != null) Order(u, next);
    }

    /// <summary>a free place next to the well (ObjMgr.GetFreePos on the well's position)</summary>
    (double X, double Z) ApproachWell(Unit u, Well w)
    {
        var nav = NavFor(u); double dx = u.Pos.X - w.Pos.X, dz = u.Pos.Z - w.Pos.Z, d = Math.Sqrt(dx * dx + dz * dz);
        if (d == 0) d = 1;
        double x = w.Pos.X + dx / d * (w.Radius + 1.5), z = w.Pos.Z + dz / d * (w.Radius + 1.5);
        if (nav.IsFree(x, z)) return (x, z);
        int c = nav.NearestFree(nav.Idx(x, z));
        return c >= 0 ? nav.Center(c) : (x, z);
    }

    /// <summary>CFountain.ObjArrived: the unit gets what it lacks (or what is left); the refill starts; "fill_anim" plays</summary>
    public void DrinkAt(Well w, Unit u)
    {
        double need = u.MaxHp - u.Hp;
        if (w.Fill - need >= 0) { SetHp(u, u.MaxHp); w.Fill -= need; } else { SetHp(u, u.Hp + w.Fill); w.Fill = 0; }
        w.RefillT ??= 1;
        if (w.Prop.Anim != null && w.Prop.Anim.Has("fill_anim")) w.Prop.PlayAnim("fill_anim", 1);       // (Game.usl event 23: CHealingWellState)
        Emit("drink", new Args { ["unit"] = u, ["well"] = w });
    }

    partial void WellsUpdate(double dt)
    {
        // (the wells' props are World.Props: the world's step advances their animations)
        foreach (var w in Wells)
        {
            if (!w.Prop.Alive) w.Alive = false;
            if (!w.Alive || w.RefillT == null) continue;
            if ((w.RefillT -= dt) > 0) continue;
            w.RefillT += 1;
            w.Fill += w.Max / Math.Max(1, w.Refill);                                // Refill(): every second 1 / refill_dur of the store
            if (w.Fill >= w.Max) { w.Fill = w.Max; w.RefillT = null; }
        }
        if (Wells.Any(w => !w.Alive)) Wells = Wells.Where(w => w.Alive).ToList();
    }

    // ---------------------------------------------------------------- lakes of healing / damage
    public void AddLake(Lake l) { l.T = LAKE_TICK; Lakes.Add(l); }
    /// <summary>a lake of the map source / a campaign's DMGL helper</summary>
    public void AddLake(Maps.MapLake l) => AddLake(new Lake { Name = l.Name, Region = l.Region, Heal = l.Heal, Amount = l.Amount, Types = l.Types });

    partial void LakesUpdate(double dt)
    {
        foreach (var l in Lakes)
        {
            if ((l.T -= dt) > 0) continue;
            l.T += LAKE_TICK;
            if (RegionOf?.Invoke(l.Region) is not IMapRegion r || !r.Enabled) continue;
            if (r.Bounds() is not var (bx, bz, br)) continue;
            var types = l.Types ?? "";
            // (passengers are where their transport is: the healing reaches them, the damage does not)
            void Hit(Entity e)
            {
                var p = e.Pos;          // (a passenger's position is its transport's: transport.js passengerUpdate)
                if (!e.Alive || e.Parked || Math.Sqrt((p.X - bx) * (p.X - bx) + (p.Z - bz) * (p.Z - bz)) > br + e.Radius || !r.Contains(p.X, p.Z)) return;
                if (types != "" && !types.Contains(e.Cls)) return;
                if (l.Heal) HealMe(e, l.Amount);
                else if (!e.Inside) TakeDirectDmg(e, l.Amount, 0, null, false);
            }
            foreach (var u in Units.ToList()) Hit(u);
            foreach (var e in Buildings.ToList()) Hit(e);
        }
    }

    /// <summary>FightingObj.HealMe: up to the full hit points; a unit glows for 2.5 s ("fx_heal_passive")</summary>
    public void HealMe(Entity e, double amount)
    {
        if (e.Hp >= e.MaxHp) return;
        SetHp(e, Math.Min(e.MaxHp, e.Hp + amount));
        if (e is Unit u) { u.HealedT = Time; Emit("healfx", new Args { ["from"] = null, ["to"] = e }); }
    }

    // ---------------------------------------------------------------- weather (CWeatherMgr)
    /// <summary>the program of the level (Root/WeatherData) or of a file (WTHR with chgtype 0: progpath - not in the game's data)</summary>
    public void StartWeather(WeatherProgram? prog)
    {
        var tracks = (prog?.Tracks ?? new List<WeatherTrack>()).Where(t => t.Dur > 0).ToList();
        Weather = tracks.Count > 0 ? new WeatherProgram { Name = prog!.Name, Tracks = tracks, Loop = prog.Loop, I = 0, T0 = Time, Over = false } : null;
    }
    /// <summary>the map source's program (maps/source.js weather: landmarks.js weatherProgram)</summary>
    public void StartWeather(Maps.WeatherProgram? prog) =>
        StartWeather(prog == null ? null : new WeatherProgram { Name = prog.Name, Loop = prog.Loop, Tracks = prog.Tracks.Select(t => new WeatherTrack(t.Dur, (int)t.Type, t.Intensity, t.Wind, t.Strength)).ToList() });
    /// <summary>WTHR with chgtype 1: the next track now</summary>
    public void NextWeatherTrack()
    {
        var w = Weather;
        if (w == null) return;
        if (w.I + 1 < w.Tracks.Count) { w.I++; w.T0 = Time; }
        else if (w.Loop) { w.I = 0; w.T0 = Time; w.Over = false; }
        else w.Over = true;
    }
    partial void WeatherUpdate()
    {
        var w = Weather;
        if (w == null || w.Over) return;
        if (Time - w.T0 >= w.Tracks[w.I].Dur) NextWeatherTrack();
    }
    /// <summary>what to draw now: type 0 none | 1 snow | 2 rain, intensity, wind (degrees, 0 = north), strength; null: none</summary>
    public WeatherTrack? WeatherNow()
    {
        var w = Weather;
        if (w == null || w.Over) return null;
        var t = w.Tracks[w.I];
        return t.Type != 0 ? t : null;
    }
}

public sealed partial class UnitTask
{
}
