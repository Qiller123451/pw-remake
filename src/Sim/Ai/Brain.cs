// The computer player: a port of the original's script AI (Data/Base/Scripts/Ai; docs/spec/ai.md, docs/COMPUTER_PLAYER.md).
//
// One TribeAI ("brain") per computer player. Like the original it is a set of modules that think on their own timers
// (one tick = 0.2 s, waits depend on the difficulty 0-9):
//   control   (this file)   difficulty and its handicaps, resource gifts, behaviours, module commands, unit locks
//   economy   (Economy.cs)  the build list (BuildVillage), realising requests, workers, gathering, housing, storage
//   army      (Army.cs)     the standing army by unit mix (MinistryOfDefense), level-ups (Kindergarten), special moves
//   fight     (Attack.cs)   the strategist (DisturbAttack) and the running attacks (attack goals, scripted waves)
//   defense   (Defense.cs)  danger detection, defenders, defend mode, guard point, towers, defence areas
// The tables come from ai.json (Data.cs); the brain plays by the rules: it pays for everything it builds, except
// what the original gives it for free (gifts and handicaps by difficulty, scripted waves of campaign triggers).
// What a player can do it does through its hands (IAiHands: the world's commands); what only the original's engine
// did for it (gifts, spawned waves, the free epoch) it does on the world directly, as the JS did.
//
// Enemies are whoever `player.IsEnemy(other)` says among `world.Players` - any number of players (the remake: up to 16),
// any relations.
//
// Ported from remake/src/game/ai/brain.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Ai;

/// <summary>A point on the ground (game coordinates x, z).</summary>
public readonly record struct Pt(double X, double Z)
{
    public static Pt Of(Entity e) => new(e.Pos.X, e.Pos.Z);
    public double Dist(Pt o) => Math.Sqrt((X - o.X) * (X - o.X) + (Z - o.Z) * (Z - o.Z));
    public double Dist(Entity e) => Math.Sqrt((X - e.Pos.X) * (X - e.Pos.X) + (Z - e.Pos.Z) * (Z - e.Pos.Z));
}

/// <summary>Who holds a unit (an attack, the village guard, a defence area, an outpost ...): told when the unit is taken
/// away (claims.drop).</summary>
public interface IClaimer { void Drop(Unit u) { } }

/// <summary>The map's AI options (Root/AIOptions of a skirmish map, map.ai_options of a campaign level).</summary>
public sealed class AiMapOptions
{
    public bool Walls, MarkplaceOutpost, Harbour, Warpgate, HuntAnimals, Watermap;
    public AiMapOptions Clone() => (AiMapOptions)MemberwiseClone();
    public static AiMapOptions From(JsonNode? n, AiMapOptions? over = null)
    {
        var o = over?.Clone() ?? new AiMapOptions();
        if (n is not JsonObject j) return o;
        if (j["walls"] != null) o.Walls = j["walls"].Bool();
        if (j["markplace_outpost"] != null) o.MarkplaceOutpost = j["markplace_outpost"].Bool();
        if (j["harbour"] != null) o.Harbour = j["harbour"].Bool();
        if (j["warpgate"] != null) o.Warpgate = j["warpgate"].Bool();
        if (j["hunt_animals"] != null) o.HuntAnimals = j["hunt_animals"].Bool();
        if (j["watermap"] != null) o.Watermap = j["watermap"].Bool();
        return o;
    }
}

/// <summary>new TribeAI(G, player, { difficulty = 5, behaviour = 'Mikrobe', mapOptions = {}, multimap = false, levelName, extraSquads })</summary>
public sealed class TribeAIOptions
{
    public double Difficulty = 5;
    public string Behaviour = "Mikrobe";
    public AiMapOptions MapOptions = new();
    public bool Multimap;
    /// <summary>MIRAGE's m_bCustommap (AiCtx.Custommap); null: not a custom map exactly when not a multiplayer map (the
    /// campaign's levels are MIRAGE's custom maps, CustomMapSettings.txt)</summary>
    public bool? Custommap;
    /// <summary>'Single 04' ...: level-specific rows of the attack plan (optional)</summary>
    public string? LevelName;
    /// <summary>raids of a tribe without army tables: the remake's own squads (skirmish, the campaign's Nightmare) or,
    /// false, the original's pool units only (the campaign: Attack.cs)</summary>
    public bool ExtraSquads = true;
}

/// <summary>What the brain owns right now (census).</summary>
public sealed class Census
{
    public double T;
    public List<Unit> Units = new(), Workers = new(), Idle = new(), Fighters = new(), Pool = new(), Fleet = new(), Heroes = new();
    public List<Building> Blds = new();
    public Dictionary<string, int> Count = new(StringComparer.Ordinal);
    public Entity? Base;
    public Pt? Home;
    public int Age;
    /// <summary>the base range (farthest building + 30 m), computed on first use (Defense.cs nearOwnWorker)</summary>
    public double? Range;
    public int CountOf(string n) => Count.TryGetValue(n, out var c) ? c : 0;
    public void Inc(string n) => Count[n] = CountOf(n) + 1;
}

/// <summary>an attack started (brain.launched): { t, go, type, n, scripted, hunt, result }</summary>
public sealed class LaunchLog
{
    public int Id;
    public double T;
    public double? Go, End;
    public string Type = "", Goal = "";
    public bool Scripted, Hunt;
    public int N;
    public string? Result;
    public override string ToString() => $"#{Id} {Type} ({Goal}) t={T} go={Go} n={N}{(Scripted ? " scripted" : "")}{(Hunt ? " hunt" : "")} -> {Result}";
}

public sealed partial class TribeAI
{
    static readonly Logger L = Log.For("Ai");
    static readonly Regex NOT_ARMY = new("resource_collector|_cart$|trade_dino|hovercraft|transport|tracker_dino|kennel_eusmilus|fishing_boat", RegexOptions.Compiled);
    const double TICK = AiData.TICK;
    static double Clamp(double v, double a, double b) => Math.Max(a, Math.Min(b, v));
    static double Hyp(double x, double z) => Math.Sqrt(x * x + z * z);
    static double D2(Pt a, Pt b) => a.Dist(b);
    static double DistE(Entity a, Entity b) => Hyp(a.Pos.X - b.Pos.X, a.Pos.Z - b.Pos.Z);
    static double DistP(Entity a, Pt b) => Hyp(a.Pos.X - b.X, a.Pos.Z - b.Z);
    static double DistP(Entity a, double x, double z) => Hyp(a.Pos.X - x, a.Pos.Z - z);

    /// <summary>module settings before any behaviour command</summary>
    public sealed class Prm
    {
        public double Aggressiveness = 100, Riskiness = 100, UpgradeWalls = 0, UpgradeTowers = 1, CloseStartLocation = 0, AttacksRiskLevel = 1, GuardVillage = 1;
        public string? Army;
        public bool HighDefense;
        public double CancelAttacksRadius = -1, MaxAge = 5;
        public bool NoCollect, NoFight, NoDefence, NoTowers, NoWalls;
        public string? VillageWall;
        public Prm Clone() => (Prm)MemberwiseClone();
    }

    public readonly W_ W;
    public readonly Rules.Rules D;
    public readonly Player P;
    public readonly AiData Data;
    /// <summary>the brain's hands: what a player can do (the world's commands)</summary>
    public IAiHands Hands;
    /// <summary>the brains of the game (AiPlayers): the debug option "passive" and the human player</summary>
    public AiPlayers? Owner;
    readonly SimRandom R;

    public readonly bool Multimap;
    /// <summary>MIRAGE's m_bCustommap of this player's level (TribeAIOptions.Custommap)</summary>
    public bool Custommap;
    public readonly AiMapOptions MapOptions;
    public readonly string? LevelName;
    public readonly bool ExtraSquads;

    // state
    /// <summary>AILU: units the AI must not touch</summary>
    public readonly OrderedSet<Unit> Locked = new();
    /// <summary>unit -> the attack / defence area that holds it</summary>
    public readonly OrderedMap<Unit, object> Claims = new();
    /// <summary>running attacks (Attack.cs)</summary>
    public readonly List<Attack> Attacks = new();
    /// <summary>log of every attack started: { t, type, n, scripted } (tests, statistics)</summary>
    public readonly List<LaunchLog> Launched = new();
    /// <summary>AIDA: id -> { x, z, r, max, units }</summary>
    public readonly OrderedMap<string, Pool> Areas = new();
    /// <summary>AIRG: map name -> [{ region, value }]</summary>
    public readonly OrderedMap<string, List<(IAiRegion? Region, double Value)>> Regions = new();
    /// <summary>build list in realisation (Economy.cs)</summary>
    public List<BuildEntry> Requests = new();
    Census? S;
    readonly Dictionary<string, double> Next = new(StringComparer.Ordinal);
    Dictionary<string, double> Wait = new(StringComparer.Ordinal);
    public int AttackSeq;
    /// <summary>the start location (fixed once the base is known)</summary>
    public Pt? Start;
    public Prm Params = new();
    public string Letter = "G";
    public int Tactic = 1;
    public string? Village;
    public string Personality = "Giraffe";
    public bool EconomyOff, Kindergarten, Paused = true;
    public string? Behaviour;
    public AiBehaviour? Cfg;
    public string? Sub;
    public int Difficulty;
    public string Cls = "Hard";
    public AiLevel Lv = null!;
    public bool GameOver;
    /// <summary>co-op campaign: the scripted waves of this player are this much bigger (CoopRuntime.RecomputeScaled; 1 =
    /// the original)</summary>
    public double WaveScale = 1;
    /// <summary>a co-op AI commander's brain (CoopCommander): the remake's improvements for allies apply (ArmyFloor)</summary>
    public bool CoopAlly;
    /// <summary>a co-op AI commander before its base is handed out (CoopCommander): no search for places farther out</summary>
    public bool HeroPhase;

    public TribeAI(W_ world, Player player, AiData data, TribeAIOptions? o = null, IAiHands? hands = null)
    {
        W = world; D = world.Data; P = player; Data = data; R = world.Rng;
        Hands = hands ?? new CommandHands(world);
        o ??= new TribeAIOptions();
        Multimap = o.Multimap;
        Custommap = o.Custommap ?? !o.Multimap;
        MapOptions = o.MapOptions.Clone();
        LevelName = o.LevelName;
        ExtraSquads = o.ExtraSquads;
        // what a brain that was never woken is (a sleeping campaign player can still be given waves and areas)
        SetDifficulty(o.Difficulty);
        GameTypeInit();                                // Defender / Domination (GameTypes.cs)
        SetBehaviour(string.IsNullOrEmpty(o.Behaviour) ? "Mikrobe" : o.Behaviour);
    }

    /// <summary>legacy (skirmish): new TribeAI(G, player, enemyPlayer, 'easy' | 'normal' | 'hard' | 0..9) - the menu's
    /// easy / normal / hard, a random personality (ServerApp "ai_Random": Dodo, Giraffe or Schnecke); the map's
    /// Root/AIOptions over the defaults (harbours where there is water)</summary>
    public static TribeAI Skirmish(W_ world, Player player, AiData data, string level, AiMapOptions? mapAiOptions = null, string? behaviour = null, IAiHands? hands = null)
    {
        double d = double.TryParse(level, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : AiData.MENU_LEVELS.TryGetValue(level, out var m) ? m : AiData.MENU_LEVELS["normal"];
        var mo = new AiMapOptions { Harbour = world.WaterNav != null, Watermap = world.WaterNav != null, Walls = false, MarkplaceOutpost = false, HuntAnimals = true };
        if (mapAiOptions != null) mo = mapAiOptions.Clone();
        mo.Warpgate = world.Opts.Warpgate;
        string b = behaviour ?? new[] { "Dodo", "Giraffe", "Schnecke" }[world.Rng.Int(3)];
        return new TribeAI(world, player, data, new TribeAIOptions { Difficulty = d, Behaviour = b, Multimap = true, MapOptions = mo }, hands);
    }

    // legacy fields of the old AI (tests/sim.js)
    public bool Attacking => Attacks.Any(a => a.State is "walk" or "fight");
    public int Waves => Launched.Count;

    double N(string k) => Next.TryGetValue(k, out var v) ? v : 0;
    /// <summary>the crossings' think wait (s; Naval.cs FerryThinks)</summary>
    double FerryWait = 2.4;
    double Wt(string k) => Wait.TryGetValue(k, out var v) ? v : 0;
    int Rnd(int n) => R.Int(n);

    // ------------------------------------------------------------------ difficulty (CM:159-187, §3)
    public void SetDifficulty(double d0)
    {
        int d = (int)Clamp(Math.Floor((double.IsFinite(d0) ? d0 : 0) + 0.5), 0, 9);
        var lv = Lv = Data.Level(d);
        Difficulty = d; Cls = lv.Class;
        double Wv = lv.Wait; int r7 = 1 + Rnd(7);
        // think waits in seconds (§1.2)
        Wait = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["control"] = (lv.ControlWait + r7) * TICK,
            ["eco"] = ((Cls == "Easy" ? 30 : Cls == "Medium" ? 10 : 1) + r7) * TICK,
            ["build"] = Math.Max(1, Wv) * TICK,
            ["collect"] = 0.6,
            ["fight"] = (Wv + r7) * TICK,
            ["strat"] = (Wv + 20) * TICK,                     // + rnd(20) ticks (stratWait)
            ["mod"] = (5 + r7) * TICK,
            ["atk"] = Math.Min(5, (Wv + 2) * TICK),            // the goal thinks every W + r ticks; orders are refreshed at most every 5 s
            ["defense"] = Math.Min(4, (Wv + r7) * TICK),
            ["guard"] = (10 + r7) * TICK,
            ["kinder"] = Data.KindergartenWait(Cls),
            ["moves"] = 1.5,
            ["walls"] = (Wv + 50 + r7) * TICK,                // the village wall goal (VW:91-95)
            ["outposts"] = (Wv + 25 + r7) * TICK,             // the outpost goals (BO:77)
            ["trade"] = (50 + r7) * TICK,                     // the market places (MP:76)
            ["area"] = (Wv + r7) * TICK,                      // the area module: units left on another land (AM:140)
        };
        FerryWait = (10 + r7) * TICK;                         // the water crossing goal (WC:80: 10 + iType ticks)
        var old = P.AiMods;
        // research: Action.usl tests the path for "Actions/Upgrades/", which a tech tree path ("/Actions/<Tribe>/Upgrades/")
        // never contains - so the build factor applies to upgrades too (and nothing to age_2)
        P.AiMods = new AiMods
        {
            Gather = lv.Gather != 0 ? lv.Gather : 1, BuildTime = lv.BuildTime != 0 ? lv.BuildTime : 1, ResearchTime = lv.BuildTime != 0 ? lv.BuildTime : 1,
            WeaponTime = lv.WeaponDuration != 0 ? lv.WeaponDuration : 1,
            Attack = old != null && old.Attack != 0 ? old.Attack : 1, Defense = old != null && old.Defense != 0 ? old.Defense : 1,
            Pyramid = lv.UnitLimitLevels.Any(v => v > 0) ? (int[])lv.UnitLimitLevels.Clone() : null, UnitLimit = lv.UnitLimitTotal,
        };
        W.AiRecomputeCaps(P);
        double now = W.Time;
        foreach (var k in Wait.Keys) if (!Next.ContainsKey(k)) Next[k] = now + 0.5 + R.Next() * 2;
        Next["strat"] = Math.Max(N("strat"), now + StratWait());
    }

    // ------------------------------------------------------------------ behaviours (CM:285-552, §2)
    /// <summary>the strategist's wait: (W + 20 + rnd(20)) ticks</summary>
    double StratWait() => Wt("strat") + Rnd(20) * TICK;

    /// <summary>AIBV: SetBehaviour("Dodo" | "Giraffe" | "Schnecke" | "Turtle" | "FightOnly" | "Mikrobe" | "Singleplayer_L3_1" ...)
    /// or a module command: SetBehaviour("upgrade_walls 1", "DFNS")</summary>
    public bool SetBehaviour(string name, string? module = "CTRL")
    {
        if (!string.IsNullOrEmpty(module) && module != "CTRL") return Command(module, name);
        // the defender of a Defender game is always a Schnecke (CM:292)
        if (GameType == "Defender" && IsDefender && name != "Mikrobe") name = "Schnecke";
        var cfg = Data.Behaviour(name);
        if (cfg == null) return false;
        Behaviour = name;
        Cfg = cfg;
        Paused = cfg.Paused;
        if (Paused) return true;                       // Mikrobe: SetPaused(true), nothing else changes
        EconomyOff = cfg.EconomyOff;
        Kindergarten = cfg.Kindergarten;
        Personality = cfg.Personality ?? name;         // Dodo | Giraffe | Schnecke | Turtle (fight factors, pest patrol)
        int n = 1 + Rnd(cfg.Variants != 0 ? cfg.Variants : 1);
        string sub = (cfg.SubStrategy ?? "G1").Replace("{n}", n.ToString(CultureInfo.InvariantCulture));
        Sub = sub;                                      // (also names its army's AiPBPreset file: game/pointbuy.js)
        string Fill(string s) => ReplaceFirst(ReplaceFirst(s, "{sub}", sub), "{n}", n.ToString(CultureInfo.InvariantCulture));
        // defaults of a module that gets no command, then the behaviour's commands
        Params = new Prm();
        Village = null;
        bool tactics = false;
        foreach (var (k, v) in cfg.Params)
        {
            if (k == "tactics") tactics = true;
            bool isTrue = v is JsonValue jv && jv.TryGetValue<bool>(out var bv) && bv;
            Command(null, k + (isTrue ? "" : " " + Fill(JsStr(v))));
        }
        if (!tactics) SetTactics(sub);
        if (cfg.BrainWash) BrainWash();
        return true;
    }
    static string ReplaceFirst(string s, string a, string b) { int i = s.IndexOf(a, StringComparison.Ordinal); return i < 0 ? s : s[..i] + b + s[(i + a.Length)..]; }
    /// <summary>String(v) of a JSON value as JS writes it (numbers: 100, 1.3; null: "null")</summary>
    static string JsStr(JsonNode? v)
    {
        if (v == null) return "null";
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            if (jv.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            if (jv.TryGetValue<double>(out var d)) return TT.JsString(d);
        }
        return v.ToJsonString();
    }
    void SetTactics(string t) { if (t.Length == 2) { Letter = t[0].ToString(); Tactic = int.TryParse(t[1].ToString(), out var x) && x != 0 ? x : 1; Plan = null; } }
    /// <summary>the engine's BrainWash: forget what was going on (requests, the plan's attacks; scripted waves go on)</summary>
    public void BrainWash()
    {
        Requests = new List<BuildEntry>();
        BuildList = null;
        foreach (var a in Attacks.ToList()) if (!a.Scripted) a.End("brainwash");
        Plan = null;
        Scouted = false;
        FirstAttack = 0;
    }
    /// <summary>module command strings (the modules' SetBehavior / Call): ECON | FGHT | DFNS | AREA, or null = find the module</summary>
    public bool Command(string? module, string cmd)
    {
        var t = Regex.Split(cmd.Trim(), @"\s+");
        string? v = t.Length > 1 ? t[1] : null;
        double num = Json.ParseNum(v, double.NaN);
        var Pm = Params;
        switch (t[0])
        {
            // economy (EM:450)
            case "village_level":
                if (v == "Deactivate") EconomyOff = true;
                else { v ??= "undefined"; Village = v.Length >= 2 ? v[..2] : v + "1"; BuildList = null; }
                return true;
            case "max_age": Pm.MaxAge = Clamp(double.IsNaN(num) || num == 0 ? 5 : num, 1, 5); return true;
            case "disable_collect_resources": Pm.NoCollect = true; return true;
            case "reset_buildings": case "forbid_building": case "enable_user_interaction": return true;
            // fight (FM:132)
            case "enable": return true;
            case "disable": Pm.NoFight = true; return true;
            case "aggressiveness": Pm.Aggressiveness = Clamp(num, 0, 100); return true;
            case "riskiness": Pm.Riskiness = Clamp(num, 0, 100); return true;
            case "tactics": SetTactics(v ?? "undefined"); return true;
            case "EnableDisturbAttack": Pm.NoFight = false; return true;
            // defense (DM:441, 608)
            case "upgrade_walls": Pm.UpgradeWalls = num; return true;
            case "upgrade_towers": Pm.UpgradeTowers = num; return true;
            case "close_start_location": Pm.CloseStartLocation = num; return true;
            case "attacks_risk_level": Pm.AttacksRiskLevel = num; return true;
            case "guard_village": Pm.GuardVillage = num; return true;
            case "army": Pm.Army = string.IsNullOrEmpty(v) ? null : v; return true;
            case "defend_place":
            {
                var c = Regex.Split(v ?? "", "[_ ,]+").Select(JsNumber).ToArray();
                if (c.Length >= 2 && double.IsFinite(c[0])) GuardBase = MapToGame(c[0], c[1]);
                return true;
            }
            case "AddDefenseArea":
            {
                var c = Regex.Split(Regex.Replace(t.Length > 2 ? t[2] : "", @"[\[\]{}]", ""), "[_ ,]+").Select(JsNumber).ToArray();
                var g = MapToGame(c.Length > 0 ? c[0] : double.NaN, c.Length > 1 ? c[1] : double.NaN);
                SetDefenceArea(t.Length > 1 ? t[1] : "undefined", g, Json.ParseNum(t.Length > 3 ? t[3] : null, double.NaN), t.Length > 4 ? ParseIntJs(t[4]) : double.NaN);
                return true;
            }
            case "HighDefenseMode": Pm.HighDefense = v == "true"; return true;
            case "CancelAttacksInDefense": Pm.CancelAttacksRadius = num; return true;
            case "deactivate_towers": Pm.UpgradeTowers = 0; Pm.NoTowers = true; return true;
            case "deactivate_walls": Pm.UpgradeWalls = 0; Pm.NoWalls = true; return true;      // (AIAssistDef_0: EnableWallplacer(false), CM:526)
            case "deactivate_unit_defence": Pm.NoDefence = true; return true;
            case "village_wall": Pm.VillageWall = string.Join(" ", t.Skip(1)); return true;       // stored: the AI builds no walls (COMPUTER_PLAYER.md)
        }
        return false;
    }
    /// <summary>JS Number(s): "" = 0, not a number = NaN</summary>
    static double JsNumber(string s) { s = s.Trim(); if (s.Length == 0) return 0; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN; }
    static double ParseIntJs(string s) { var m = Regex.Match(s.Trim(), @"^[+-]?\d+"); return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : double.NaN; }
    /// <summary>AICM {player_id, module, command}</summary>
    public bool CallModule(string module, string command) => Command(module, command);
    /// <summary>script positions are map coordinates (x east, y south): the AI players' conversion (a campaign: the map's
    /// origin - right on maps that are not square and on maps extended for co-op), else x = mapX - size/2, z = mapY - size/2</summary>
    public Pt MapToGame(double mx, double my)
    {
        if (W.Ai is { } a) return a.MapToGame(new PwRemake.Sim.Scripting.MapPos(mx, my, 0));
        double s = W.Map.Size; return new Pt(mx - s / 2, my - s / 2);
    }

    // ------------------------------------------------------------------ AILU / AIRG
    /// <summary>externally locked units are never pooled, allocated or given orders by the AI</summary>
    public void LockUnits(IEnumerable<Unit> units, bool lockIt = true)
    {
        foreach (var u in units)
        {
            if (u == null) continue;
            if (lockIt)
            {
                Locked.Add(u);
                if (Claims.TryGetValue(u, out var c) && c is IClaimer cl) cl.Drop(u);
                Claims.Delete(u);
            }
            else Locked.Delete(u);
        }
        S = null;
    }
    /// <summary>region: IAiRegion (Contains) | null (= the whole map)</summary>
    public void SetRegionMap(string mapName, IAiRegion? region, double value, bool add = true)
    {
        var l = Regions.Get(mapName) ?? new List<(IAiRegion?, double)>();
        if (!add) l.Clear();
        l.Add((region, double.IsFinite(value) ? value : 0));
        Regions.Set(mapName, l);
    }
    public double RegionValue(string mapName, double x, double z)
    {
        double v = 0;
        var l = Regions.Get(mapName);
        if (l == null) return 0;
        foreach (var (r, val) in l) if (r == null || r.Contains(x, z)) v += val;
        return v;
    }

    // ------------------------------------------------------------------ census (what the brain owns right now)
    public Census Census(bool force = false)
    {
        var p = P; double now = W.Time;
        if (!force && S != null && now - S.T < 0.35) return S;
        var s = new Census { T = now, Age = p.Epoch() };
        foreach (var b in W.Buildings) if (b.Alive && b.Owner == p) { s.Blds.Add(b); s.Inc(b.Name); }
        foreach (var u in W.Units)
        {
            if (!u.Alive || u.Owner != p || u.Autonomous || u.Illusion) continue;
            s.Units.Add(u); s.Inc(u.Name);
            if (Locked.Has(u) || (Crew != null && Crew.Has(u))) continue;            // (an outpost's crew: Outposts.cs)
            // hidden by the mission (OBAP 0: Parked, out of the world - it takes no orders): no worker, guard or squad member
            // (base / MIRAGE mission 12: the SEAS' hidden marksmen were a crossing's whole squad, which stood at the shore
            // through its seven boarding rounds and gave up)
            if (u.Parked) continue;
            if (u.Naval || u.Cls == "SHIP") { if (!u.Stationary) s.Fleet.Add(u); continue; }       // (the Aje's transport turtle swims and walks)
            if (u.IsWorker) { s.Workers.Add(u); if (u.Task.Type == "idle" && !u.Inside) s.Idle.Add(u); continue; }
            if (u.Def!.Unique) s.Heroes.Add(u);
            if (u.CannotFight || u.Stationary || u.IsDropoff || NOT_ARMY.IsMatch(u.Name)) continue;
            s.Fighters.Add(u);
            // (a unit that would not march with its squad is left out of the pool for five minutes)
            if (!u.Inside && !Claims.Has(u) && !(u.AiStuckT != null && now - u.AiStuckT.Value < 300)) s.Pool.Add(u);
        }
        foreach (var (u, c) in Claims) if (!u.Alive || u.Owner != p) { Claims.Delete(u); if (c is IClaimer cl) cl.Drop(u); }
        s.Base = s.Blds.FirstOrDefault(b => b.Def!.Script == "CFireplace" && b.Built) ?? (Entity?)s.Units.FirstOrDefault(u => u.Name == "aje_resource_collector")
            ?? s.Blds.FirstOrDefault(b => b.Built && b.IsDropoff) ?? s.Blds.FirstOrDefault(b => b.Built) ?? (Entity?)s.Blds.FirstOrDefault() ?? s.Units.FirstOrDefault();
        if (s.Base != null && Start == null) Start = Pt.Of(s.Base);
        s.Home = s.Base is Building ? Pt.Of(s.Base) : Start ?? (s.Base != null ? Pt.Of(s.Base) : null);
        S = s;
        return s;
    }
    /// <summary>the census taken last (null: none yet / dropped)</summary>
    public Census? LastCensus => S;
    void Forget() => S = null;
    /// <summary>the base moved to a new start (a co-op AI commander's base handed out after the hero phase: until then
    /// its census took the hero-phase units' place for the start - the Dustriders' home is the start, their main building
    /// being a vehicle): the start and home from now on, the census taken again</summary>
    public void Rebase(Pt start) { Start = start; Forget(); }

    // ------------------------------------------------------------------ helpers shared by the modules
    public List<Player> Enemies() => W.Players.Where(o => o != P && !o.Defeated && P.IsEnemy(o)).ToList();
    public bool IsEnemyEntity(Entity? e) => e != null && e.Alive && e.Owner != null && e.Owner != P && P.IsEnemy(e.Owner);
    public List<ActionDef> Acts(Entity e) => D.ActionsOf(e.RulesOwner(), e.Name, e.Kind, e.Def?.Type ?? e.Cls);
    /// <summary>level score of units as the scripts count it (CompareLevel, CheckUnitLevel): the 1-based level, heroes + 2</summary>
    public double Score(IEnumerable<Unit> units) { double s = 0; foreach (var u in units) if (u.Alive && !u.IsWorker) s += u.Level + (u.Def!.Unique ? 2 : 0); return s; }
    /// <summary>where a player's village is: its main building, else the centre of its buildings, else any unit</summary>
    public Pt? BaseOf(Player o)
    {
        int n = 0; double x = 0, z = 0; Building? main = null;
        foreach (var b in W.Buildings)
        {
            if (!b.Alive || b.Owner != o) continue;
            if (b.Def!.Script == "CFireplace" || b.Def.Script == "CHeadquarters") main ??= b;
            if (string.IsNullOrEmpty(b.Def.WallKind)) { n++; x += b.Pos.X; z += b.Pos.Z; }
        }
        if (main != null) return Pt.Of(main);
        if (n > 0) return new Pt(x / n, z / n);
        var u = W.Units.FirstOrDefault(e => e.Alive && e.Owner == o && !e.Inside);
        return u != null ? Pt.Of(u) : null;
    }
    public void Claim(IEnumerable<Unit> units, object owner) { foreach (var u in units) Claims.Set(u, owner); S = null; }
    public void Release(IEnumerable<Unit> units) { foreach (var u in units.ToList()) Claims.Delete(u); S = null; }
    /// <summary>context of table conditions (Data.cs Matches / When)</summary>
    public AiCtx Ctx()
    {
        var s = S ?? Census(); var M = MapOptions;
        return new AiCtx
        {
            Tribe = P.Tribe, Letter = (Village ?? Letter + "1")[0].ToString(), Age = s.Age, D = Difficulty, Cls = Cls, Multimap = Multimap, LevelName = LevelName, Player = P.Id,
            Tactic = Tactic, Harbour = M.Harbour && W.WaterNav != null, Watermap = M.Watermap && W.WaterNav != null, Warpgate = M.Warpgate,
            OwnWarpgate = s.Blds.Any(b => PwRemake.Sim.World.World.IsWarpGateScript(b.Def!.Script)), Has = c => s.CountOf(c) > 0,
            DefenderGame = GameType == "Defender", Defender = IsDefender, Custommap = Custommap,
        };
    }

    // ------------------------------------------------------------------ the control think (CM:556-613, CH)
    void ControlThink()
    {
        var p = P;
        // gifts: every control think, not on the arena map (multi_arena_001)
        if (!Regex.IsMatch(LevelName ?? "", "multi_arena_001", RegexOptions.IgnoreCase))
        {
            var g = Data.Gift(Difficulty, Multimap, (S ?? Census()).Age, P.Id);
            // SpawnResources is engine code; taken to fill the stores like any income (AddResource), not to overflow them
            foreach (var (r, v) in g)
            {
                if (v == 0) continue;
                p.Res[r] = r == "skulls" ? p[r] + v : Math.Max(p[r], Math.Min(Cap(r), p[r] + v));
            }
        }
        // fight factors from difficulty 5 (CH:1086-1169): damage dealt / taken by this player's units
        var m = p.AiMods;
        if (m != null)
        {
            double atk = 1, def = 1;
            if (Difficulty >= 5)
            {
                double a = (W.Map.Size != 0 ? W.Map.Size : 512) / 16;           // AI map size in areas
                double mapV = Clamp((((a - 16) / 8) * ((a - 16) / 8) - 1) / 20 + 0.3, 0.2, 0.6);
                def = Clamp(1 - mapV * Difficulty * 0.2, 0.5, 2);
                atk = Clamp(1 + mapV * Difficulty * 0.2 * 0.5, 0.5, 2);
                var k = Personality == "Dodo" ? new[] { 0.30, 0.05 } : Personality == "Giraffe" ? new[] { 0.15, 0.15 } : new[] { 0.03, 0.30 };   // fog of war on
                def = 1 + (def - 1) * k[0]; atk = 1 + (atk - 1) * k[1];
            }
            static double Step(double cur, double to) => Clamp(to, cur - 0.05, cur + 0.025);
            m.Attack = Step(m.Attack != 0 ? m.Attack : 1, atk); m.Defense = Step(m.Defense != 0 ? m.Defense : 1, def);
        }
        // free epoch catch-up (BV:1326-1363): from difficulty 6 on skirmish maps, when a human enemy is an epoch ahead
        if (Difficulty >= 6 && Multimap && !EconomyOff)
        {
            var s = Census(); int mine = s.Age;
            bool ahead = W.Players.Any(o => o != p && !o.Ai && !o.Defeated && o.Epoch() > mine);
            if (ahead && mine < 5 && FreeAge != mine + 1 && s.Base != null && !s.Base.Queue.Any(q => q.Action.Id.StartsWith("age_", StringComparison.Ordinal)))
            {
                var a = Acts(s.Base).FirstOrDefault(x => x.Id == "age_" + (mine + 1));
                if (a != null) { FreeAge = mine + 1; s.Base.Queue.Add(new QueueItem { Action = a, T = 0, Total = W.ProductionTime(a, p), Level = 0 }); }   // " /AI_Help": no check, no cost
            }
        }
    }
    int FreeAge;
    double Cap(string r) => P.Caps.TryGetValue(r, out var c) ? c : 0;

    // ------------------------------------------------------------------ main loop
    public void Update(double dt)
    {
        var p = P;
        if (p.Defeated) return;
        double now = W.Time;
        var PF = W.Profile;       // (where a step of the brain goes: "ai." + module, the step profile's finer phases)
        PF?.Sub();
        // running attacks (scripted waves also while the brain sleeps)
        if (Attacks.Count > 0 && now >= N("atk")) { Next["atk"] = now + Wt("atk"); if (Census().Base != null) AttacksThink(); }
        PF?.SubMark("ai.atk");
        // crossings think on their own (the water crossing goal: every 10 + r ticks, whatever the difficulty - WC:80)
        if (now >= N("ferry") && Attacks.Any(a => a.State == "ferry")) { Next["ferry"] = now + FerryWait; if (Census().Base != null) FerryThinks(); }
        PF?.SubMark("ai.ferry");
        if (Paused) return;
        ReadEvents();
        PF?.SubMark("ai.events");
        if (now >= N("control")) { Next["control"] = now + Wt("control"); ControlThink(); }
        PF?.SubMark("ai.control");
        Census? s = null;
        Census Cs() => s ??= Census();
        if (!EconomyOff)
        {
            if (now >= N("build")) { Next["build"] = now + Wt("build"); if (Cs().Base != null) BuildThink(s!); }
            PF?.SubMark("ai.build");
            if (now >= N("eco")) { Next["eco"] = now + Wt("eco"); if (Cs().Base != null) EconomyThink(s!); }
            PF?.SubMark("ai.eco");
            if (now >= N("collect")) { Next["collect"] = now + Wt("collect"); if (Cs().Base != null) CollectThink(s!); }
            PF?.SubMark("ai.collect");
        }
        if (now >= N("defense")) { Next["defense"] = now + Wt("defense"); if (Cs().Base != null) DefenseThink(s!); }
        PF?.SubMark("ai.defense");
        FountainsTick(now);                                // MIRAGE: damaged units heal at fountains (Fountains.cs)
        PF?.SubMark("ai.fountains");
        if (now >= N("guard")) { Next["guard"] = now + Wt("guard"); if (Cs().Base != null) GuardThink(s!); }
        PF?.SubMark("ai.guard");
        if (now >= N("walls") && Wt("walls") != 0) { Next["walls"] = now + Wt("walls"); if (Cs().Base != null) WallsThink(s!); }
        PF?.SubMark("ai.walls");
        if (now >= N("outposts") && Wt("outposts") != 0 && !EconomyOff) { Next["outposts"] = now + Wt("outposts"); if (Cs().Base != null) OutpostsThink(s!); }
        PF?.SubMark("ai.outposts");
        if (now >= N("trade") && Wt("trade") != 0 && !EconomyOff) { Next["trade"] = now + Wt("trade"); if (Cs().Base != null) TradeThink(s!); }
        PF?.SubMark("ai.trade");
        if (now >= N("area") && Wt("area") != 0 && W.WaterNav != null) { Next["area"] = now + Wt("area"); if (Cs().Base != null) StrandedThink(s!); }
        PF?.SubMark("ai.area");
        if (now >= N("mines") && W.WaterNav != null && !EconomyOff) { Next["mines"] = now + MineWait(); if (Cs().Base != null && MineBeltsOn(s!)) MineBeltThink(s!); }
        PF?.SubMark("ai.mines");
        if (now >= N("mod")) { Next["mod"] = now + Wt("mod"); if (Cs().Base != null) ArmyThink(s!); }
        PF?.SubMark("ai.mod");
        if (now >= N("fight")) { Next["fight"] = now + Wt("fight"); if (Cs().Base != null) { XtraThink(s!); TimerThink(s!); } }
        PF?.SubMark("ai.fight");
        if (now >= N("strat")) { Next["strat"] = now + StratWait(); if (Cs().Base != null) StrategyThink(s!); }
        PF?.SubMark("ai.strat");
        if (now >= N("kinder")) { Next["kinder"] = now + Math.Max(2, Wt("kinder")); if (Cs().Base != null) KindergartenThink(s!); }
        PF?.SubMark("ai.kinder");
        if (now >= N("moves")) { Next["moves"] = now + Wt("moves"); MovesThink(Cs()); }
        PF?.SubMark("ai.moves");
        if (GetItem != null && now >= N("item")) { Next["item"] = now + (30 + 1 + Rnd(W.Players.Count + 1)) * TICK; GetItemThink(Cs()); }
        PF?.SubMark("ai.item");
    }

    /// <summary>one think of a module now, whatever its timer says (debug tools, tests): control | build | eco | collect |
    /// defense | guard | walls | outposts | trade | area | mines | mod | fight | strat | kinder | moves | atk | ferry | item |
    /// fountains</summary>
    public void ThinkNow(string module)
    {
        var s = Census(true);
        if (module != "control" && module != "moves" && module != "item" && s.Base == null) return;
        switch (module)
        {
            case "control": ControlThink(); break;
            case "build": BuildThink(s); break;
            case "eco": EconomyThink(s); break;
            case "collect": CollectThink(s); break;
            case "defense": DefenseThink(s); break;
            case "guard": GuardThink(s); break;
            case "walls": WallsThink(s); break;
            case "outposts": OutpostsThink(s); break;
            case "trade": TradeThink(s); break;
            case "area": StrandedThink(s); break;
            case "mines": if (MineBeltsOn(s)) MineBeltThink(s); break;
            case "mod": ArmyThink(s); break;
            case "fight": XtraThink(s); TimerThink(s); break;
            case "strat": StrategyThink(s); break;
            case "kinder": KindergartenThink(s); break;
            case "moves": MovesThink(s); break;
            case "atk": AttacksThink(); break;
            case "ferry": FerryThinks(); break;
            case "item": GetItemThink(s); break;
            case "fountains": FountainsThink(s); break;
            default: throw new ArgumentException("no AI module " + module);
        }
    }

    /// <summary>GAOV: the game is over for the computer players (engine.js: the brains have no gameOver - nothing
    /// changes; kept as a flag)</summary>
    public void OnGameOver() { GameOver = true; }
}
