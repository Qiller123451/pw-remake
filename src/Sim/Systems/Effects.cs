// Status effects, auras and timed states of units and buildings.
//
// The original keeps these in several places (FightingObj.usl): ref-counted "effect flags" (warcry, warpaint,
// trumpet ...), flat "bonus buckets" (DAMAGE, DEFENSE, RANGEDDEFENSE, RANGE, BLDGDAMAGE), timers (trapped,
// invulnerable, animal immunity) and camouflage layers. Here every entity has one `St` object (EntityState.cs Status):
//
//   e.St.T[name]      game time until which a timed state lasts (trapped, invul, iced, trumpet, paw, doping, ...)
//   e.St.Aura         aura values recomputed every AURA_TICK seconds from all aura sources (warcry tier, ...)
//   e.St.Bonus        flat bonus buckets filled by auras (DAMAGE, MELEEDAMAGE, RANGEDAMAGE, DEFENSE, RANGEDDEFENSE,
//                     RANGE, BLDGDAMAGE)
//   e.St.Camo         set of camouflage layers: 'entr' (entrenched), 'disg' (ninja), 'aje' (shaman), 'hero' (vanish)
//   e.St.Poison       active poison slots (see Combat.cs)
//
// Aura sources are listed in AURAS below - add a row to give a unit or building a new aura. The heroes' auras come
// from the data (hero auras, HeroEffect): their RangeEffect ability and filter, so a mod's (MIRAGE's) heroes do what they do there.
// A copy of a class (the sequel's: Rules.Kind) has its original's aura.
//
// Ported from remake/src/game/systems/effects.js (pw-exporter 98c7703); SetCamo from systems/moves.js setCamo (the
// effects hook of docs/SIMULATION.md).
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>A row of the aura table (effects.js AURAS).</summary>
public sealed class AuraDef
{
    /// <summary>"friend" | "enemy": units of the source owner's team, or its enemies (wild animals count as enemies)</summary>
    public string Who = "friend";
    public Func<Entity, double> Radius = _ => 0;
    /// <summary>source condition (invention, level, finished building ...)</summary>
    public Func<Entity, World, bool> When = (_, _) => true;
    /// <summary>optional target filter</summary>
    public Func<Entity, bool>? Match;
    /// <summary>the aura also affects buildings</summary>
    public bool Bldg;
    /// <summary>writes into unit.St.Aura / unit.St.Bonus</summary>
    public Action<Entity, Entity> Apply = (_, _) => { };
}

/// <summary>A hero's aura effect read from its RangeEffect filter (effects.js heroEffect).</summary>
public sealed class HeroFx
{
    /// <summary>class -> [(bucket, key, value)]</summary>
    public readonly Dictionary<string, List<(string Bucket, string Key, double V)>> Bonus = new(StringComparer.Ordinal);
    /// <summary>"tribe/type" -> weapon duration factor (Ada), skulls factor (Schliemann)</summary>
    public readonly Dictionary<string, double> Dur = new(StringComparer.Ordinal), Skulls = new(StringComparer.Ordinal);
    public readonly HashSet<string> Invent = new(StringComparer.Ordinal);
}

public sealed partial class World
{
    public const double AURA_TICK = 1.0;
    const string BOAR_RAGE_FILTER = "Hu/Upgrades/xenage/wild_boar_rage";
    static readonly double[] WARCRY = { 1, 1, 1, 1.1, 1.15, 1.2 }, WARPAINT = { 1, 1, 1, 0.9, 0.85, 0.8 }, AURA_R = { 0, 0, 0, 10, 15, 20 };

    /// <summary>is a timed state active? (effects.js active)</summary>
    public static bool IsActive(Entity e, string key, double time) => e.St.Active(key, time);

    // CalcAttackBoni (FO:7812): multipliers first, then the flat DAMAGE bucket; MIRAGE also has buckets for melee and
    // ranged weapons only (CalcMeleeAttackBoni / CalcRangeAttackBoni)
    public double AttackBoni(Entity e, double d, Weapon? w = null)
    {
        var a = e.St.Aura; double t = Time;
        if (e.St.Active("trumpet", t)) d *= 0.8;
        if (e.St.Active("paw", t)) d *= 0.8;
        if (a.Warcry > 0) d *= WARCRY[Math.Min(5, a.Warcry)];
        if (a.Warpaint > 0) d *= WARPAINT[Math.Min(5, a.Warpaint)];
        if (a.Drums) d *= 1.2;
        if (a.NinigiCauldron) d *= 1.25;
        if (a.BoarRage) d *= 1.5;
        if (e.St.Active("cyborg", t)) d *= 1.5;              // MIRAGE cyborg roar (SetCyborgEffect, FO:10054)
        var b = e.St.Bonus;
        return d + b.DAMAGE + (w != null ? (w.Projectile != null ? b.RANGEDAMAGE : b.MELEEDAMAGE) : 0);
    }
    // AddTemporaryDefenseBoni (FO:7894) / ranged (FO:7952); MIRAGE's speaker + 20 each (MIRAGE_SPEAKER, MirageSeasBuildings.cs)
    public double TempDef(Entity e, double p) => p + (e.St.Aura.Pennant && e.Def != null && e.Def.Tribe != "Aje" ? 20 : 0) + e.St.Bonus.DEFENSE + SpeakerDef(e);
    /// <summary>(+ Defence_On_Wall for a unit on a wall's walkway: systems/wallwalk.js setOnWall, FO:7959)</summary>
    public double TempRangedDef(Entity e, double p) => p + e.St.Bonus.RANGEDDEFENSE + (e is Unit u && u.OnWall != null ? u.WallDef : 0) + SpeakerDef(e);
    public double RangeBonus(Entity e) => e.St.Bonus.RANGE;

    // ------------------------------------------------------------------------------------------------ aura table
    static bool Inv(Entity src, World w, string name) => src.Owner != null && w.Data.Invented(src.Owner, name, src.Owner.Tribe, src);
    static bool BuiltSrc(Entity src) => src is not Building b || b.Built;
    static bool Lvl(Entity src, int n) => src.Level >= n;
    static bool IsUnit(Entity u) => u.Kind == "unit";
    static bool IsWorkerUnit(Entity u) => u is Unit x && x.IsWorker;
    static readonly HashSet<string> ARCHERS = new(StringComparer.Ordinal) { "ninigi_archer", "ninigi_marksman", "aje_archer", "hu_archer", "hu_marksman", "Bela_s0" };
    static double AuraR(Entity s) => s.Level >= 0 && s.Level < AURA_R.Length ? AURA_R[s.Level] : 0;

    public static readonly Dictionary<string, AuraDef> AURAS = new(StringComparer.Ordinal)
    {
        // warcry (character.usl:769): friends get damage x1.10 / x1.15 / x1.20 by the warrior's level 3/4/5
        ["hu_warrior"] = new() { Who = "friend", Radius = AuraR, When = (s, w) => Lvl(s, 3) && Inv(s, w, "warcry"), Match = u => u.Kind == "unit", Apply = (u, s) => { u.St.Aura.Warcry = Math.Max(u.St.Aura.Warcry, s.Level); } },
        // warpaint: enemies deal x0.90 / x0.85 / x0.80
        ["aje_warrior"] = new() { Who = "enemy", Radius = AuraR, When = (s, w) => Lvl(s, 3) && Inv(s, w, "warpaint"), Match = IsUnit, Apply = (u, s) => { u.St.Aura.Warpaint = Math.Max(u.St.Aura.Warpaint, s.Level); } },
        ["mayor_s0"] = new() { Who = "enemy", Radius = _ => 20, When = (s, _) => Lvl(s, 2), Match = IsUnit, Apply = (u, _) => { u.St.Aura.Warpaint = 5; } },
        // buildings (Building.usl region buildings)
        ["hu_magic_cauldron"] = new() { Who = "friend", Radius = _ => 30, When = (s, _) => BuiltSrc(s), Match = IsUnit, Apply = (u, _) => { u.St.Bonus.DAMAGE += 0.1 * BaseDmg(u); } },
        ["aje_scarecrow"] = new() { Who = "enemy", Radius = _ => 50, When = (s, _) => BuiltSrc(s), Match = u => u.Cls == "ANML", Apply = (u, _) => { u.St.Bonus.DAMAGE -= 0.2 * BaseDmg(u); } },
        ["ninigi_cauldron"] = new() { Who = "friend", Radius = _ => 35, When = (s, _) => BuiltSrc(s), Match = u => ARCHERS.Contains(u.Name), Apply = (u, _) => { u.St.Aura.NinigiCauldron = true; } },
        ["aje_skull_protector"] = new() { Who = "friend", Bldg = true, Radius = _ => 30, When = (s, _) => BuiltSrc(s), Match = u => u.Name != "aje_skull_protector", Apply = (u, _) => { u.St.Aura.SkullProtect = true; } },
        ["ninigi_smoke_tower"] = new() { Who = "friend", Bldg = true, Radius = _ => 35, When = (s, _) => BuiltSrc(s), Match = u => u.Kind == "building" && u.Name != "ninigi_smoke_tower", Apply = (u, _) => { u.St.Aura.Invisible = true; } },
        ["ninigi_smokebomb_thrower"] = new() { Who = "friend", Radius = _ => 15, When = (_, _) => true, Match = IsUnit, Apply = (u, _) => { u.St.Aura.Invisible = true; } },
        ["ninigi_parasaurolophus_drums"] = new() { Who = "friend", Radius = _ => 50, When = (_, _) => true, Match = u => IsUnit(u) && !IsWorkerUnit(u), Apply = (u, _) => { u.St.Aura.Drums = true; } },
        ["hu_scout"] = new() { Who = "friend", Radius = _ => 20, When = (s, w) => Inv(s, w, "drums"), Match = u => IsUnit(u) && !IsWorkerUnit(u), Apply = (u, _) => { u.St.Aura.Drums = true; } },
        ["hu_rhino"] = new() { Who = "friend", Radius = _ => 20, When = (s, w) => Inv(s, w, "pennant"), Match = IsUnit, Apply = (u, _) => { u.St.Aura.Pennant = true; } },
    };

    /// <summary>e is an aura source of AURAS with its aura on (buffs.js: A.when(e, W) &amp;&amp; A.radius(e))</summary>
    public bool AuraOn(Entity e)
    {
        var k = Data.Kind(e.Name);
        if (!AURAS.TryGetValue(k, out var A) && !AURAS.TryGetValue(k.ToLowerInvariant(), out A)) return false;
        return e.Owner != null && A.When(e, this) && A.Radius(e) != 0;
    }

    // ------------------------------------------------------------------------------------------------ hero auras
    // A hero's aura (Hero.usl RangeEffect): its ability RangeEffect (switched on by its level-2 filter; radius, OnEnemy)
    // says how far and on whom, its filter Filters/Special/Upgrades/<hero>/RangeEffect what it does to every unit in it -
    // read here from the data (HeroEffect), so the official game's and a mod's heroes each do what they do there:
    //   /Objects/<tribe>/<type>/<class>/<x>_bonus = v   that class gets v in a bonus bucket (FightingObj OnTechTreeChange:
    //                                                  bldgdamage, rangedamage, meleedamage, damage, rangeddefense,
    //                                                  defense, range - checked in this order); one entry per name
    //   /Modifications/<tribe>/<type>/WeaponDuration   units of that tribe and type attack that much slower (Ada)
    //   /Modifications/<tribe>/<type>/Skulls           they are worth that many more skulls (Schliemann)
    //   /Objects/<tribe>/InventObjects/damage_babbage_bonus   Babbit: BUILDING_DAMAGE_BONUS % more against buildings
    // Scripted ones by class: Darwin (wild animals leave his people alone), Livingstone (drains life, not while vanished).
    static readonly (string K, string B)[] BUCKETS = { ("bldgdamage", "BLDGDAMAGE"), ("rangedamage", "RANGEDAMAGE"), ("meleedamage", "MELEEDAMAGE"), ("damage", "DAMAGE"),
        ("rangeddefense", "RANGEDDEFENSE"), ("defense", "DEFENSE"), ("range", "RANGE") };
    readonly Dictionary<string, HeroFx> _heroFx = new(StringComparer.Ordinal);

    /// <summary>parseFloat of a modificator's value (an object's text is "[object Object]": NaN)</summary>
    static double PF(TtNode? n) => n?.Leaf != null && TT.ParseFloat(n.Leaf, out var v) ? v : double.NaN;

    public HeroFx HeroEffect(string name)
    {
        if (_heroFx.TryGetValue(name, out var c)) return c;
        var f = Data.Base.Filter($"Special/Upgrades/{name}/RangeEffect") ?? Data.Base.Filter($"Special/Upgrades/{Data.Kind(name)}/RangeEffect");
        var fx = new HeroFx();
        foreach (var m in TT.ModsOf(f))
        {
            var p = m.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            double v = PF(m.Value);
            if (p.Length > 3 && p[0] == "Objects" && p[2] == "InventObjects") fx.Invent.Add(p[1] + "/" + p[3]);
            else if (p.Length == 5 && p[0] == "Objects" && p[4].EndsWith("_bonus", StringComparison.Ordinal))
            {
                var key = ReplaceFirst(p[4], "_bonus", "");
                int bi = Array.FindIndex(BUCKETS, b => key.Contains(b.K, StringComparison.Ordinal));
                if (bi >= 0 && v > 0) { if (!fx.Bonus.TryGetValue(p[3], out var l)) fx.Bonus[p[3]] = l = new(); l.Add((BUCKETS[bi].B, key, v)); }
            }
            else if (p.Length > 3 && p[0] == "Modifications" && p[3] == "WeaponDuration") { var k = p[1] + "/" + p[2]; fx.Dur[k] = Math.Max(Or1(fx.Dur, k), v); }
            else if (p.Length > 3 && p[0] == "Modifications" && p[3] == "Skulls") { var k = p[1] + "/" + p[2]; fx.Skulls[k] = Math.Max(Or1(fx.Skulls, k), v); }
        }
        _heroFx[name] = fx;
        return fx;
    }
    // (fx.dur.get(k) || 1)
    static double Or1(Dictionary<string, double> d, string k) => d.TryGetValue(k, out var x) && x != 0 && !double.IsNaN(x) ? x : 1;
    static string ReplaceFirst(string s, string a, string b) { int i = s.IndexOf(a, StringComparison.Ordinal); return i < 0 ? s : s.Substring(0, i) + b + s.Substring(i + a.Length); }

    void ApplyHero(HeroFx fx, string k, Unit s, Unit u)
    {
        var tribe = u.Owner?.Tribe ?? u.Def?.Tribe;          // (the player's tribe's modifications)
        var a = u.St.Aura;
        foreach (var cls in new[] { u.Name, Data.Kind(u.Name) })
            if (fx.Bonus.TryGetValue(cls, out var l)) foreach (var (b, key, v) in l) (a.Bon ??= new()).Set(key, (b, v));
        var t = tribe + "/" + u.Cls;
        if (fx.Dur.TryGetValue(t, out var dur)) a.Slowhand = Math.Max(a.Slowhand != 0 && !double.IsNaN(a.Slowhand) ? a.Slowhand : 1, dur);
        if (fx.Skulls.TryGetValue(t, out var sk)) a.Kleemann = Math.Max(a.Kleemann != 0 && !double.IsNaN(a.Kleemann) ? a.Kleemann : 1, sk);
        if (fx.Invent.Contains(tribe + "/damage_babbage_bonus")) (a.Bon ??= new()).Set("babbage", ("BLDGDAMAGE", Data.Extra.HeroConsts.TryGetValue("BUILDING_DAMAGE_BONUS", out var bb) ? bb : 20));
        if (k == "darwin_s0") a.NoAnimalAggro = true;
        if (k == "livingstone_s0") (a.DrainBy ??= new()).Add(s);
    }
    static double BaseDmg(Entity u) { var cs = u.Cs(); return cs != null ? cs.Dmg : 0; }

    // ------------------------------------------------------------------------------------------------ the updates
    double _auraT;

    /// <summary>every AURA_TICK: rebuild all auras from their sources; apply periodic effects (hero drain, building heal)</summary>
    public void AuraUpdate()
    {
        var all = new List<Entity>(Units.Count + Buildings.Count);
        all.AddRange(Units); all.AddRange(Buildings);
        foreach (var e in all) { e.St.Aura = new AuraState(); var b = e.St.Bonus; foreach (var k in BonusBuckets.Keys) b[k] = 0; }
        foreach (var s in all)
        {
            if (!s.Alive || s.Owner == null) continue;
            if (!AURAS.TryGetValue(Data.Kind(s.Name), out var A) || !A.When(s, this)) continue;
            double r = A.Radius(s);
            if (r == 0 || double.IsNaN(r)) continue;
            void Visit(Entity u)
            {
                if (!u.Alive || u == s) return;
                bool friend = u.Owner != null && s.Owner!.IsFriend(u.Owner);
                if (u.Owner != null && s.Owner!.IsNeutral(u.Owner)) return;       // auras help friends and harm enemies, not neutral players
                if ((A.Who == "friend") != friend) return;
                if (A.Match != null && !A.Match(u)) return;
                if (Hypot(u.Pos.X - s.Pos.X, u.Pos.Z - s.Pos.Z) - u.Radius > r) return;
                A.Apply(u, s);
            }
            UHash.Query(s.Pos.X, s.Pos.Z, r + 8, Visit);
            if (A.Bldg) BHash.Query(s.Pos.X, s.Pos.Z, r + 20, Visit);     // aura also affects buildings
        }
        // the heroes' auras (HeroEffect above)
        foreach (var s in Units)
        {
            var so = s.Owner;
            if (!s.Alive || so == null || s.Stats == null || !s.Stats.Abilities.TryGetValue("RangeEffect", out var ab) || !ab.On("enabled")) continue;
            var k = Data.Kind(s.Name);
            if (k == "livingstone_s0" && s.Active("vanish")) continue;
            var fx = HeroEffect(s.Name);
            double r = ab.Num("radius", 0); if (r == 0 || double.IsNaN(r)) r = 20;
            bool onEnemy = ab.On("OnEnemy");
            UHash.Query(s.Pos.X, s.Pos.Z, r + 8, u =>
            {
                if (!u.Alive || u == s) return;
                if (u.Owner != null && so.IsNeutral(u.Owner)) return;
                if (onEnemy == (u.Owner != null && so.IsFriend(u.Owner))) return;
                // wild animals count as enemies only when aggressive (Hero.usl OnObjEnterRangedEffect; Schliemann: all of them)
                if (u.Owner == null && onEnemy && k != "schliemann_s0" && !(u.Def != null && u.Def.Aggressive == 1)) return;
                if (Hypot(u.Pos.X - s.Pos.X, u.Pos.Z - s.Pos.Z) - u.Radius > r) return;
                ApplyHero(fx, k, s, u);
            });
        }
        // the bonus entries into the buckets (one entry per name: two Coles do not add up)
        foreach (var e in all) if (e.St.Aura.Bon != null) foreach (var (b, v) in e.St.Aura.Bon.Values) e.St.Bonus[b] += v;
        // wild boar rage (FightingObj.usl:5488): a boar at <= 25% hit points deals x1.5 damage once "wild_boar_rage" is
        // invented. MIRAGE (FO:7387 DoHPRelatedStuff) switches its own filter on at <= 3/7 of the hit points instead.
        var modRage = Data.Base.Filter(BOAR_RAGE_FILTER);
        foreach (var u in Units)
        {
            if (!u.Alive || u.Name != "hu_wild_boar" || u.Owner == null || !Data.Invented(u.Owner, "wild_boar_rage", u.Owner.Tribe)) continue;
            if (modRage == null) { if (u.Hp * 4 <= u.MaxHp && !(u.LocalTT != null && u.LocalTT.Has("Hu/Upgrades/hu_wild_boar/wild_boar_rage"))) u.St.Aura.BoarRage = true; continue; }   // (a mission's raging boar has it already)
            bool on = u.Hp * 7 / 3 <= u.MaxHp;
            if (on != (u.LocalTT != null && u.LocalTT.Has(BOAR_RAGE_FILTER))) LocalFilter(u, BOAR_RAGE_FILTER, on);
        }
    }

    /// <summary>timed effects: poison ticks, hero damage-over-time, self heal (1 s), passive regeneration abilities</summary>
    partial void EffectsTick(double dt)
    {
        _auraT -= dt;
        if (_auraT <= 0) { _auraT = AURA_TICK; AuraUpdate(); Periodic(AURA_TICK); }
    }

    void Periodic(double dt)
    {
        double t = Time;
        for (int i = 0; i < Units.Count; i++)
        {
            var u = Units[i];
            if (!u.Alive) continue;
            // Livingstone: every LIVINGSTONE_TICK_TIME s enemies in his aura take LIVINGSTONE_TICK_DAMAGE, he heals 25 % of it
            var drain = u.St.Aura.DrainBy;
            var HC = Data.Extra.HeroConsts;
            double tick = HC.TryGetValue("LIVINGSTONE_TICK_TIME", out var x1) && x1 != 0 ? x1 : 3, dmg = HC.TryGetValue("LIVINGSTONE_TICK_DAMAGE", out var x2) && x2 != 0 ? x2 : 10;
            // (Hero.usl DoLivingstoneDamage: TakeDmg(this, TICK_DAMAGE / GetDmg()) - his attack bonuses and the victim's
            // armour apply; only enemies his owner sees)
            if (drain != null && Math.Floor(t / tick) != Math.Floor((t - dt) / tick))
                foreach (var s in drain.ToArray())
                {
                    if (!s.Alive || (s.Owner != null && (HiddenFrom(u, s.Owner) || (FowOf(s.Owner) is { } fw && !fw.Visible(u.Pos.X, u.Pos.Z))))) continue;
                    double bd = BaseDmg(s), got;
                    if (bd > 0) got = TakeDmg(s, u, dmg / bd);
                    else { ApplyDamage(u, dmg, s); got = dmg; }
                    s.Hp = Math.Min(s.MaxHp, s.Hp + got * 0.25);
                }
            // self_heal ability: HealMe(amount) every second (FightingObj.usl:8208)
            if (u.Stats != null && u.Stats.Abilities.TryGetValue("self_heal", out var sh) && u.Hp < u.MaxHp && !u.St.Active("noHeal", t))
            {
                double amt = sh.Num("amount", 0); if (double.IsNaN(amt)) amt = 0;
                u.Hp = Math.Min(u.MaxHp, u.Hp + amt * dt);
                u.HealedT = t;
                if (Math.Floor(t) != Math.Floor(t - dt)) Emit("healfx", new Args { ["to"] = u });      // (HealMe: the unit glows)
            }
        }
        BuildingHeal(dt);
    }

    /// <summary>is e hidden from player p? (smoke tower / smoke bomb invisibility, camouflage, entrenchment)</summary>
    public bool HiddenFrom(Entity e, Player? p)
    {
        if (p == null || (e.Owner != null && p.IsFriend(e.Owner))) return false;
        if (Time < e.St.RevealT) return false;
        if (e.St.Aura.Invisible || e.St.Camo.Count > 0) return true;
        return false;
    }

    /// <summary>(public: moves and scripts switch camouflage layers)</summary>
    public void SetCamoLayer(Unit u, string layer, bool on) => SetCamo(u, layer, on);
}
