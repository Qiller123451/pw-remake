// The computer player's tables: lookups into ai.json (pipeline/build_ai.py, docs/spec/ai.md §12) with a built-in
// fallback for every table, so the AI also plays on game data built before the "ai" step existed and for tribes whose
// settings folder is missing from an installation (armies / unit mix: then derived from the tech tree by the callers).
//
// Ported from remake/src/game/ai/data.js (pw-exporter 98c7703). The JS kept one module-wide table (setAiData); here an
// AiData object is loaded per configuration (AiData.Load(dataDir)) and shared by its brains; AiData.Fallback is
// the JS without ai.json (?noaidata).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.Ai;

/// <summary>A difficulty row (CM:159-187 and the Server scripts): data.js row().</summary>
public sealed class AiLevel
{
    public string Class = "Hard";
    public double Wait, ControlWait;
    public double GiftFood, GiftWood, GiftStone, GiftSkulls;
    public double AttackStrength, QuickAttackStrength, PyramidAttackUnits;
    public double Gather = 1, BuildTime = 1, ResearchTime = 1, WeaponDuration = 1;
    public int[] UnitLimitLevels = new int[5];
    public int UnitLimitTotal;
    public double Gift(string r) => r switch { "food" => GiftFood, "wood" => GiftWood, "stone" => GiftStone, "skulls" => GiftSkulls, _ => 0 };
    public AiLevel Clone() { var c = (AiLevel)MemberwiseClone(); c.UnitLimitLevels = (int[])UnitLimitLevels.Clone(); return c; }
}

/// <summary>Pool units an attack goal adds to its squad (the goals' Think): { min, max, bad } by class.</summary>
public readonly record struct SquadSize(double Min, double Max, double Bad);

/// <summary>A behaviour (CM:285-552): its module commands and what the brain does with it.</summary>
public sealed class AiBehaviour
{
    public string Name = "";
    /// <summary>the module commands, in order: name -> value (true = no value)</summary>
    public List<(string Key, JsonNode? Value)> Params = new();
    public bool Paused, EconomyOff, BrainWash, Kindergarten = true;
    public int Variants = 1;
    public string? Personality, ValueTable, SubStrategy;
}

/// <summary>A row of the build orders / fight upgrades / hero requests / housing / counter ships / attack plans (ai.json),
/// with its conditions (fields + raw "when" atoms + earlier branches it must not match).</summary>
public sealed class AiRow
{
    public string Request = "", ObjFlag = "";
    public int Count = 1;
    public bool Unique = true, OnTop;
    public string? Tribe, Behaviour, Level, NotLevel;
    public int MinAge, Age;
    public bool? Multimap, DefenderGame, Defender;
    public int? Player, Tactic;
    public List<string> When = new();
    public List<AiRow> Not = new();
    // attack plans
    public List<string> Attacks = new();
    public double Guerilla;
    public bool Appended;
    // hero requests
    public int Set;
    public int Line;

    public static AiRow From(JsonNode? n)
    {
        var r = new AiRow();
        if (n is not JsonObject o) return r;
        r.Request = o["request"].Str(); r.ObjFlag = o["objFlag"].Str();
        r.Count = o["count"] != null ? o["count"].Int(1) : 1;
        r.Unique = o["unique"] == null || o["unique"].Bool(true);
        r.OnTop = o["onTop"].Bool();
        r.Tribe = o["tribe"] is JsonValue t ? t.Str() : null;
        r.Behaviour = o["behaviour"] is JsonValue b ? b.Str() : null;
        r.Level = o["level"] is JsonValue lv ? lv.Str() : null;
        r.NotLevel = o["notLevel"] is JsonValue nl ? nl.Str() : null;
        r.MinAge = o["minAge"].Int(); r.Age = o["age"].Int();
        if (o["multimap"] is JsonValue mm) r.Multimap = mm.Bool();
        if (o["defenderGame"] is JsonValue dg) r.DefenderGame = dg.Bool();
        if (o["defender"] is JsonValue df) r.Defender = df.Bool();
        if (o["player"] is JsonValue pl) r.Player = pl.Int();
        if (o["tactic"] is JsonValue tc) r.Tactic = tc.Int();
        if (o["when"] is JsonArray w) foreach (var x in w) r.When.Add(x.Str());
        if (o["not"] is JsonArray no) foreach (var x in no) r.Not.Add(From(x));
        if (o["attacks"] is JsonArray at) foreach (var x in at) r.Attacks.Add(x.Str());
        r.Guerilla = o["guerilla"].Num(); r.Appended = o["appended"].Bool();
        r.Set = o["set"].Int(); r.Line = o["line"].Int();
        return r;
    }
    public override string ToString() => Request != "" ? Request : string.Join(" ", Attacks);
}

/// <summary>The context of table conditions (data.js matches / when): brain.js ctx().</summary>
public sealed class AiCtx
{
    public string Tribe = "";
    public string Letter = "G";
    public int Age = 1, D;
    public string Cls = "Hard";
    public bool Multimap;
    public string? LevelName;
    public int Player;
    public int Tactic = 1;
    public bool Harbour, Watermap, Warpgate, OwnWarpgate, DefenderGame, Defender;
    /// <summary>MIRAGE's m_bCustommap (CMirageAIMgr.CheckCustomAI(level, player, "CampaignAI"): the level is listed in
    /// Scripts/Game/misc/CustomMapSettings.txt - the campaign's levels - with a campaign AI for the player); null: unknown,
    /// as data.js (neither m_bCustommap nor !m_bCustommap holds)</summary>
    public bool? Custommap;
    public Func<string, bool>? Has;
    public AiCtx With(string letter) { var c = (AiCtx)MemberwiseClone(); c.Letter = letter; return c; }
}

/// <summary>An army row: min-max of one of the alternatives (cls, level 0-based or null, objFlag).</summary>
public sealed class ArmyRow
{
    public double Min, Max;
    public List<ArmyAlt> Alternatives = new();
}
public sealed class ArmyAlt
{
    public string Cls = "";
    public int? Level;
    public string? ObjFlag;
}

/// <summary>A group of the unit mix: its weight and its units (cls, npc, objFlag).</summary>
public sealed class MixGroup
{
    public double Weight = 1;
    public List<MixUnit> Units = new();
}
public sealed class MixUnit
{
    public string Cls = "";
    public string? Npc, ObjFlag;
}

public sealed class AiData
{
    /// <summary>one AI tick in seconds (think waits are counted in ticks)</summary>
    public const double TICK = 0.2;
    /// <summary>skirmish menu -> difficulty 0-9 (the campaign's defaults)</summary>
    public static readonly IReadOnlyDictionary<string, int> MENU_LEVELS = new Dictionary<string, int> { ["easy"] = 1, ["normal"] = 4, ["hard"] = 8 };

    /// <summary>ai.json (null: the built-in fallbacks)</summary>
    public readonly JsonNode? Json;
    public bool HasAiData => Json != null;
    /// <summary>the built-in tables (no ai.json)</summary>
    public static AiData Fallback => s_fallback ??= new AiData(null);
    static AiData? s_fallback;

    readonly AiLevel[] _levels = new AiLevel[10];
    readonly Dictionary<string, AiBehaviour> _behaviours = new(StringComparer.Ordinal);
    readonly List<string> _behaviourNames = new();
    readonly List<AiRow>? _buildOrders, _fightUpgrades, _attackPlans;
    readonly List<AiRow> _heroRequests = new(), _housing = new(), _counterShips = new();
    readonly Dictionary<string, List<ArmyRow>> _armyCache = new(StringComparer.Ordinal);

    public AiData(JsonNode? json)
    {
        Json = json is JsonObject ? json : null;
        for (int d = 0; d < 10; d++) _levels[d] = MakeLevel(d);
        if (Json?["behaviours"] is JsonObject bo) foreach (var (k, v) in bo) { _behaviours[k] = ParseBehaviour(k, v); _behaviourNames.Add(k); }
        else foreach (var k in s_fallbackBehaviours.Keys) _behaviourNames.Add(k);
        if (Json?["buildOrders"] is JsonArray b) _buildOrders = b.Select(AiRow.From).ToList();
        if (Json?["fightUpgrades"] is JsonArray f) _fightUpgrades = f.Select(AiRow.From).ToList();
        if (Json?["attackPlans"] is JsonArray ap) _attackPlans = ap.Select(AiRow.From).ToList();
        if (Json?["heroRequests"] is JsonArray h) _heroRequests = h.Select(AiRow.From).ToList();
        if (Json?["housing"] is JsonArray ho) _housing = ho.Select(AiRow.From).ToList();
        if (Json?["counterShips"] is JsonArray cs) _counterShips = cs.Select(AiRow.From).ToList();
    }

    /// <summary>ai.json of a data folder (the toolkit's build: /opt/data/base/ai.json ...); the fallbacks when it is missing</summary>
    public static AiData Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "ai.json");
        if (!File.Exists(path)) { Log.For("Ai").Warn($"no ai.json in {dataDir}: the computer player plays on its built-in tables"); return Fallback; }
        return new AiData(Core.Json.ParseFile(path));
    }

    // ---------------------------------------------------------------- difficulty (CM:159-187 and the Server scripts)
    static AiLevel Row(string cls, double wait, double[] gift, double strength, double gather, double bt, double rt, int[] lim, double wd, double py) => new()
    {
        Class = cls, Wait = wait, ControlWait = Math.Max(20, wait > 20 ? wait : 20), GiftFood = gift[0], GiftWood = gift[1], GiftStone = gift[2], GiftSkulls = gift[3],
        AttackStrength = strength, QuickAttackStrength = Math.Min(0.6, strength), PyramidAttackUnits = py, Gather = gather, BuildTime = bt, ResearchTime = rt,
        UnitLimitLevels = lim.Take(5).ToArray(), UnitLimitTotal = lim[5], WeaponDuration = wd,
    };
    static readonly int[] Z = { 0, 0, 0, 0, 0, 0 };
    static readonly AiLevel[] LEVELS =
    {
        Row("Easy", 100, new double[] { 0, 0, 0, 0 }, 0.2, 1, 1, 1, Z, 1, 2), Row("Easy", 75, new double[] { 0, 0, 0, 0 }, 0.25, 1, 1, 1, Z, 1, 3),
        Row("Easy", 50, new double[] { 1, 1, 0, 0 }, 0.3, 1, 1, 1, Z, 1, 4), Row("Medium", 30, new double[] { 2, 2, 1, 0 }, 0.35, 1, 1, 1, Z, 1, 5),
        Row("Medium", 25, new double[] { 3, 3, 1, 0 }, 0.4, 1, 1, 1, Z, 1, 6), Row("Medium", 20, new double[] { 4, 4, 1, 0 }, 0.45, 1.25, 0.95, 0.95, Z, 1, 7),
        Row("Hard", 16, new double[] { 5, 5, 2, 0 }, 0.5, 1.5, 0.9, 0.9, Z, 1, 8), Row("Hard", 14, new double[] { 7, 7, 2, 1 }, 0.55, 1.75, 0.8, 0.7, new[] { 2, 1, 1, 0, 0, 4 }, 1, 9),
        Row("Hard", 12, new double[] { 10, 10, 5, 1 }, 0.6, 2, 0.75, 0.6, new[] { 5, 3, 2, 0, 0, 10 }, 0.95, 10), Row("Hard", 10, new double[] { 50, 50, 20, 5 }, 0.65, 2.5, 0.5, 0.4, new[] { 10, 10, 7, 1, 0, 28 }, 0.9, 10),
    };
    public static int ClampD(double d) => (int)Math.Max(0, Math.Min(9, JsRound(double.IsFinite(d) ? d : 5)));
    static double JsRound(double v) => Math.Floor(v + 0.5);

    /// <summary>{ ...LEVELS[d], ...ai.json levels[d] }</summary>
    AiLevel MakeLevel(int d)
    {
        var L = LEVELS[d].Clone();
        var j = Json?["difficulty"]?["levels"]?[d.ToString()];
        if (j is not JsonObject o) return L;
        if (o["class"] != null) L.Class = o["class"].Str();
        if (o["wait"] != null) L.Wait = o["wait"].Num();
        if (o["controlWait"] != null) L.ControlWait = o["controlWait"].Num();
        if (o["gift"] is JsonObject g) { L.GiftFood = g["food"].Num(); L.GiftWood = g["wood"].Num(); L.GiftStone = g["stone"].Num(); L.GiftSkulls = g["skulls"].Num(); }
        if (o["attackStrength"] != null) L.AttackStrength = o["attackStrength"].Num();
        if (o["quickAttackStrength"] != null) L.QuickAttackStrength = o["quickAttackStrength"].Num();
        if (o["pyramidAttackUnits"] != null) L.PyramidAttackUnits = o["pyramidAttackUnits"].Num();
        if (o["gather"] != null) L.Gather = o["gather"].Num();
        if (o["buildTime"] != null) L.BuildTime = o["buildTime"].Num();
        if (o["researchTime"] != null) L.ResearchTime = o["researchTime"].Num();
        if (o["weaponDuration"] != null) L.WeaponDuration = o["weaponDuration"].Num();
        if (o["unitLimit"] is JsonObject u)
        {
            L.UnitLimitLevels = u["levels"] is JsonArray la ? la.Select(x => x.Int()).ToArray() : new int[5];
            L.UnitLimitTotal = u["total"].Int();
        }
        return L;
    }
    public AiLevel Level(double d) => _levels[ClampD(d)];

    /// <summary>what a control think really gives (CH:309-352): skirmish maps a fixed amount per listed resource by class
    /// (Easy 5, Medium 20 / 3 skulls, Hard as listed); other maps the listed amount + 10 (skulls as listed)</summary>
    // ---- MIRAGE's gifts (AiModuleControlDefault.usl Think:519-587): per difficulty iFood / iWood / iStone / iIron, times
    // a factor of the epoch (1, 1.5, 2, 2.5, 3, 4), not for player 0, from difficulty 2; then CAiCheatMgr.SpawnResources
    // (AiImprovementMgr.usl:320-365) as the base game's. The toolkit's ai.json reads the base game's literal
    // SpawnResources("food", 50) only: MIRAGE's gifts were all 0 (its computer players got nothing). ReadControlScript
    // reads them from the installation's script.
    double[][]? _scriptGift;
    readonly double[] _ageFactor = { 1, 1, 1, 1, 1, 1, 1 };
    bool _notPlayer0;
    int _giftFrom;
    /// <summary>the gifts were read from the control script (ReadControlScript)</summary>
    public bool ScriptGifts => _scriptGift != null;

    /// <summary>reads the gifts of a control script whose ai.json gifts are all 0 (MIRAGE's
    /// Scripts/Ai/Modules/AiModuleControlDefault.usl); false: nothing read (the table stays as it is)</summary>
    public bool ReadControlScript(string? usl)
    {
        if (string.IsNullOrEmpty(usl)) return false;
        for (int d = 0; d < 10; d++) { var lv = Level(d); if (lv.GiftFood != 0 || lv.GiftWood != 0 || lv.GiftStone != 0 || lv.GiftSkulls != 0) return false; }
        int at = usl.IndexOf("proc void Think()", StringComparison.Ordinal);
        if (at < 0) return false;
        int end = usl.IndexOf("endproc", at, StringComparison.Ordinal);
        var body = usl.Substring(at, (end > at ? end : usl.Length) - at);
        int sp = body.IndexOf("SpawnResources", StringComparison.Ordinal);
        if (sp < 0) return false;
        var gifts = new double[10][];
        foreach (Match m in Regex.Matches(body.Substring(0, sp), @"GetOldDifficultyInt\(\)==(\d)\)then(.*?)(?=elseif|endif)", RegexOptions.Singleline))
        {
            int d = m.Groups[1].Value[0] - '0';
            var g = new double[4];
            foreach (Match v in Regex.Matches(m.Groups[2].Value, @"i(Food|Wood|Stone|Iron)\s*=\s*(\d+)\s*;"))
                g[Array.IndexOf(new[] { "Food", "Wood", "Stone", "Iron" }, v.Groups[1].Value)] = double.Parse(v.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            gifts[d] = g;
        }
        if (gifts.All(g => g == null)) return false;
        for (int d = 0; d < 10; d++) gifts[d] ??= new double[4];
        // (the chain is checked from the highest epoch down: each age gets the factor of the highest threshold it reaches)
        var thr = Regex.Matches(body, @"iAge>=(\d)\)then\s*fFactor\s*=\s*([\d.]+)").Select(m => (A: m.Groups[1].Value[0] - '0', F: double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).ToList();
        for (int k = 1; k < _ageFactor.Length; k++) { var t = thr.Where(x => k >= x.A).OrderByDescending(x => x.A).FirstOrDefault(); _ageFactor[k] = t.A > 0 ? t.F : 1; }
        _notPlayer0 = Regex.IsMatch(body, @"m_iPlayerID\s*!=\s*0");
        var gf = Regex.Match(body, @"GetOldDifficultyInt\(\)\s*>\s*(\d)");
        _giftFrom = gf.Success ? gf.Groups[1].Value[0] - '0' + 1 : 0;
        _scriptGift = gifts;
        return true;
    }

    /// <summary>a control think's gift for a player in an epoch (ReadControlScript's rules when read, else Gift(d, multimap))</summary>
    public Dictionary<string, double> Gift(double d, bool multimap, int age, int playerId)
    {
        if (_scriptGift == null) return Gift(d, multimap);
        var outp = new Dictionary<string, double>(StringComparer.Ordinal) { ["food"] = 0, ["wood"] = 0, ["stone"] = 0, ["skulls"] = 0 };
        int di = (int)Math.Clamp(Math.Round(d), 0, 9);
        if ((_notPlayer0 && playerId == 0) || di < _giftFrom) return outp;
        var lv = Level(d); var g = _scriptGift[di];
        double f = _ageFactor[Math.Clamp(age, 1, 6)];
        var G = Json?["difficulty"]?["gift"];
        JsonNode? fix = multimap ? (G != null ? G["skirmish"]?[lv.Class] : lv.Class == "Easy" ? s_giftEasy : lv.Class == "Medium" ? s_giftMedium : null) : null;
        double campaignAdd = G?["campaignAdd"] != null ? G["campaignAdd"].Num() : 10;
        string[] R = { "food", "wood", "stone", "skulls" };
        for (int i = 0; i < 4; i++)
        {
            // food and wood are always given, stone and skulls when the difficulty lists them (Think:577-585)
            if (i >= 2 && g[i] <= 0) continue;
            double v = Math.Floor(g[i] * f);
            outp[R[i]] = fix != null ? fix[R[i]].Num() : multimap ? v : v + (i == 3 ? 0 : campaignAdd);
        }
        return outp;
    }

    public Dictionary<string, double> Gift(double d, bool multimap)
    {
        var lv = Level(d);
        var outp = new Dictionary<string, double>(StringComparer.Ordinal) { ["food"] = 0, ["wood"] = 0, ["stone"] = 0, ["skulls"] = 0 };
        var G = Json?["difficulty"]?["gift"];
        JsonNode? fix = null;
        double campaignAdd = 10;
        if (G != null) { if (multimap) fix = G["skirmish"]?[lv.Class]; campaignAdd = G["campaignAdd"] != null ? G["campaignAdd"].Num() : 0; }
        else if (multimap) fix = lv.Class == "Easy" ? s_giftEasy : lv.Class == "Medium" ? s_giftMedium : null;
        foreach (var r in new[] { "food", "wood", "stone", "skulls" })
        {
            double g = lv.Gift(r);
            if (g == 0) continue;                          // the script only calls SpawnResources for the listed ones
            outp[r] = fix != null ? fix[r].Num() : multimap ? g : g + (r == "skulls" ? 0 : campaignAdd);
        }
        return outp;
    }
    static readonly JsonNode s_giftEasy = JsonNode.Parse("{\"food\":5,\"wood\":5,\"stone\":5,\"skulls\":0}")!;
    static readonly JsonNode s_giftMedium = JsonNode.Parse("{\"food\":20,\"wood\":20,\"stone\":20,\"skulls\":3}")!;

    static readonly Dictionary<string, int[]> WORKERS = new() { ["Easy"] = new[] { 10, 11, 12, 13, 14 }, ["Medium"] = new[] { 13, 14, 15, 15, 15 }, ["Hard"] = new[] { 15, 15, 15, 15, 15 } };
    public int MaxWorkers(string cls, int age)
    {
        int i = Math.Max(1, Math.Min(5, age)) - 1;
        if (Json?["difficulty"]?["maxWorkersPerAge"]?[cls] is JsonArray a) { int v = i < a.Count ? a[i].Int() : 0; return v != 0 ? v : 15; }
        var t = WORKERS.TryGetValue(cls, out var w) ? w : WORKERS["Hard"];
        return t[i] != 0 ? t[i] : 15;
    }

    static readonly Dictionary<string, SquadSize> SQ = new() { ["Easy"] = new(1, 5, 1), ["Medium"] = new(2, 12, 1), ["Hard"] = new(3, 20, 1) };
    static readonly Dictionary<string, Dictionary<string, SquadSize>> SQUAD = new()
    {
        ["suicide"] = SQ, ["pureviolence"] = SQ, ["stealth"] = SQ, ["siege"] = SQ, ["guerilla"] = SQ,
        ["blitz"] = new() { ["Easy"] = new(1, 2, 1), ["Medium"] = new(2, 4, 1), ["Hard"] = new(3, 6, 1) },
        ["quick"] = new() { ["Easy"] = new(1, 2, 1), ["Medium"] = new(1, 2, 1), ["Hard"] = new(1, 2, 1) },
        ["rider"] = new() { ["Easy"] = new(1, 5, 1), ["Medium"] = new(2, 12, 1), ["Hard"] = new(3, 20, 1) },
        ["singleplayer"] = new() { ["Easy"] = new(1, 4, 1), ["Medium"] = new(1, 6, 0.75), ["Hard"] = new(2, 8, 0.5) },
    };
    /// <summary>pool units an attack goal adds to its squad</summary>
    public SquadSize Squad(string goal, string cls)
    {
        var j = Json?["difficulty"]?["squad"]?[goal];
        if (j is JsonObject)
        {
            var r = j[cls] ?? j["Hard"];
            if (r is JsonObject) return new SquadSize(r["min"].Num(), r["max"].Num(), r["bad"].Num());
            return SQ["Hard"];
        }
        var t = SQUAD.TryGetValue(goal, out var x) ? x : SQUAD["suicide"];
        return t.TryGetValue(cls, out var s) ? s : t.TryGetValue("Hard", out var h) ? h : SQ["Hard"];
    }
    public double SquadTimeout(string cls)
    {
        var j = Json?["difficulty"]?["squadTimeout"];
        double v = j != null ? (j[cls] != null ? j[cls].Num() : 0) : cls switch { "Easy" => 500, "Medium" => 400, "Hard" => 300, _ => 0 };
        return (v != 0 ? v : 300) * TICK;
    }
    public double KindergartenWait(string cls)
    {
        var j = Json?["difficulty"]?["kindergartenWait"];
        double v = j != null ? (j[cls] != null ? j[cls].Num() : 0) : cls switch { "Easy" => 500, "Medium" => 100, "Hard" => 10, _ => 0 };
        return (v != 0 ? v : 10) * TICK;
    }

    // ---------------------------------------------------------------- behaviours (CM:285-552)
    static AiBehaviour B(string name, string? village, string tactics, double agg, double walls, double towers, double close, double risk,
        string? subStrategy, int variants, string personality, string valueTable, bool economyOff = false, bool brainWash = true)
    {
        var b = new AiBehaviour { Name = name, Paused = false, EconomyOff = economyOff, BrainWash = brainWash, Variants = variants, Kindergarten = true, Personality = personality, ValueTable = valueTable, SubStrategy = subStrategy };
        b.Params.Add(("village_level", village == null ? null : JsonValue.Create(village)));
        b.Params.Add(("tactics", JsonValue.Create(tactics)));
        b.Params.Add(("aggressiveness", JsonValue.Create(agg))); b.Params.Add(("riskiness", JsonValue.Create(agg)));
        b.Params.Add(("upgrade_walls", JsonValue.Create(walls))); b.Params.Add(("upgrade_towers", JsonValue.Create(towers)));
        b.Params.Add(("close_start_location", JsonValue.Create(close))); b.Params.Add(("attacks_risk_level", JsonValue.Create(risk)));
        b.Params.Add(("guard_village", JsonValue.Create(1)));
        return b;
    }
    static readonly Dictionary<string, AiBehaviour> s_fallbackBehaviours = new(StringComparer.Ordinal)
    {
        ["Dodo"] = B("Dodo", "D{n}", "{sub}", 100, 0, 1, 0, 1.0, "D{n}", 4, "Dodo", "Dodo"),
        ["Giraffe"] = B("Giraffe", "G1", "{sub}", 50, 1, 1, 1, 1.0, "G{n}", 4, "Giraffe", "Giraffe"),
        ["Schnecke"] = B("Schnecke", "S2", "{sub}", 10, 1, 1, 1, 1.3, "S{n}", 4, "Schnecke", "Schnecke"),
        ["Turtle"] = B("Turtle", "S2", "{sub}", 10, 1, 1, 1, 1.3, "S5", 1, "Turtle", "Schnecke"),
        ["FightOnly"] = B("FightOnly", null, "D2", 100, 0, 0, 0, 1.0, null, 1, "Dodo", "Dodo", economyOff: true, brainWash: false),
        ["Mikrobe"] = new AiBehaviour { Name = "Mikrobe", Paused = true, EconomyOff = false, BrainWash = false, Variants = 1, Kindergarten = false, Personality = "Mikrobe", ValueTable = null },
    };
    static AiBehaviour ParseBehaviour(string name, JsonNode? n)
    {
        var b = new AiBehaviour { Name = name };
        if (n?["params"] is JsonObject p) foreach (var (k, v) in p) b.Params.Add((k, v?.DeepClone()));
        b.Paused = n?["paused"].Bool() ?? false;
        b.EconomyOff = n?["economyOff"].Bool() ?? false;
        b.BrainWash = n?["brainWash"].Bool() ?? false;
        b.Kindergarten = n?["kindergarten"] == null || n["kindergarten"].Bool(true);        // (cfg.kindergarten !== false)
        b.Variants = n?["variants"] != null ? n["variants"].Int(1) : 1;
        b.Personality = n?["personality"] is JsonValue pe ? pe.Str() : null;
        b.ValueTable = n?["valueTable"] is JsonValue vt ? vt.Str() : null;
        b.SubStrategy = n?["subStrategy"] is JsonValue ss ? ss.Str() : null;
        return b;
    }
    public AiBehaviour? Behaviour(string name)
    {
        if (_behaviours.TryGetValue(name, out var b)) return b;
        if (s_fallbackBehaviours.TryGetValue(name, out var f)) return f;
        // Singleplayer_L3_1 ... without ai.json: an attacking village on the Singleplayer tables
        if (name.StartsWith("Singleplayer", StringComparison.Ordinal))
        {
            var s = B(name, "X1", "X2", 100, 0, 1, 0, 1.0, "G1", 1, name, "Singleplayer");
            s.BrainWash = true; s.ValueTable = "Singleplayer";
            return s;
        }
        return null;
    }
    public IReadOnlyList<string> BehaviourNames => _behaviourNames;

    // ---------------------------------------------------------------- conditions of table rows
    // ctx: { tribe, letter, age, d, cls, multimap, levelName, player, tactic, harbour, watermap, warpgate, has(cls) }
    static readonly (Regex Re, Func<AiCtx, Match, bool> Fn)[] ATOM =
    {
        (new Regex(@"^m_bHarbour$"), (c, m) => c.Harbour), (new Regex(@"^m_bWatermap$"), (c, m) => c.Watermap), (new Regex(@"^m_bMapWarpgate$"), (c, m) => c.Warpgate),
        (new Regex("^m_sDifficulty==\"(\\w+)\"$"), (c, m) => c.Cls == m.Groups[1].Value), (new Regex("^m_sDifficulty!=\"(\\w+)\"$"), (c, m) => c.Cls != m.Groups[1].Value),
        (new Regex(@"^m_iDifficulty(>=|>|<=|<|==)(\d+)$"), (c, m) => Cmp(c.D, m.Groups[1].Value, double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))),
        (new Regex(@"^!m_pxSensor\^\.GetWarpGate\(\)$"), (c, m) => !c.OwnWarpgate), (new Regex(@"^has:(\w+)$"), (c, m) => c.Has != null && c.Has(m.Groups[1].Value)),
        (new Regex(@"^!has:(\w+)$"), (c, m) => !(c.Has != null && c.Has(m.Groups[1].Value))),
        // MIRAGE (AiGoalGeneralAttack.usl:87, 266: the skirmish rows of the attack plans are "m_bMultimap&&!m_bCustommap")
        (new Regex(@"^m_bCustommap$"), (c, m) => c.Custommap == true), (new Regex(@"^!m_bCustommap$"), (c, m) => c.Custommap == false),
    };
    static bool Cmp(double a, string op, double b) => op == ">=" ? a >= b : op == ">" ? a > b : op == "<=" ? a <= b : op == "<" ? a < b : a == b;
    /// <summary>raw script conditions the extractor could not turn into fields; unknown ones do not hold</summary>
    public static bool When(List<string> list, AiCtx ctx)
    {
        foreach (var w in list)
        {
            bool ok = false, known = false;
            foreach (var (re, fn) in ATOM) { var m = re.Match(w); if (m.Success) { known = true; ok = fn(ctx, m); break; } }
            if (!known || !ok) return false;
        }
        return true;
    }
    static bool Fields(AiRow r, AiCtx ctx)
    {
        if (!string.IsNullOrEmpty(r.Tribe) && r.Tribe != ctx.Tribe) return false;
        if (!string.IsNullOrEmpty(r.Behaviour) && r.Behaviour != ctx.Letter) return false;
        if (r.MinAge != 0 && ctx.Age < r.MinAge) return false;
        if (r.Age != 0 && ctx.Age != r.Age) return false;
        if (r.Multimap != null && r.Multimap != ctx.Multimap) return false;
        if (r.DefenderGame != null && r.DefenderGame != ctx.DefenderGame) return false;
        if (r.Defender != null && r.Defender != ctx.Defender) return false;
        if (r.Level != null && r.Level != ctx.LevelName) return false;
        if (r.NotLevel != null && r.NotLevel == ctx.LevelName) return false;
        if (r.Player != null && r.Player != ctx.Player) return false;
        if (r.Tactic != null && r.Tactic != ctx.Tactic) return false;
        return true;
    }
    /// <summary>a row holds when its own conditions hold and none of the earlier branches of its if-chains does (`not`)</summary>
    public static bool Matches(AiRow r, AiCtx ctx)
    {
        if (!Fields(r, ctx) || !When(r.When, ctx)) return false;
        foreach (var n in r.Not) if (Fields(n, ctx) && When(n.When, ctx)) return false;
        return true;
    }

    // ---------------------------------------------------------------- build orders (BV)
    /// <summary>legacy plans [building, minimum epoch, how many]: the build order without ai.json (and the export of the old AI)</summary>
    public static readonly IReadOnlyDictionary<string, (string Cls, int Min, int N)[]> PLANS = new Dictionary<string, (string, int, int)[]>
    {
        ["Hu"] = new[] { ("hu_stone_cottage", 1, 2), ("hu_lumberjack_cottage", 1, 1), ("hu_stone_quarry", 1, 1), ("hu_arena", 1, 1), ("hu_small_animal_farm", 1, 1),
            ("hu_corn_field", 2, 2), ("hu_small_tower", 2, 1), ("hu_weapons_smith", 2, 1), ("hu_warehouse", 2, 1), ("hu_temple", 3, 1),
            ("hu_machine_maker", 3, 1), ("hu_arena", 3, 2), ("hu_tavern", 3, 1), ("hu_corn_field", 3, 3), ("hu_large_tower", 4, 1) },
        ["Aje"] = new[] { ("aje_rodeo", 1, 1), ("aje_small_farm", 1, 1), ("aje_temple", 2, 1), ("aje_small_tower", 2, 1),
            ("aje_slaughterhouse", 3, 2), ("aje_weapons_builder", 3, 1), ("aje_rodeo", 3, 2), ("aje_bazaar", 3, 1), ("aje_cook_house", 3, 1),
            ("aje_medium_tower", 4, 1), ("aje_small_farm", 4, 2) },
        ["Ninigi"] = new[] { ("ninigi_lumbermill", 1, 1), ("ninigi_stone_quarry", 1, 1), ("ninigi_hunting_lodge", 1, 1), ("ninigi_dojo", 1, 1),
            ("ninigi_small_tower", 1, 1), ("ninigi_engineer", 2, 1), ("ninigi_animal_farm", 2, 1), ("ninigi_temple", 2, 1), ("ninigi_paddy", 2, 2),
            ("ninigi_weapon_maker", 3, 1), ("ninigi_warehouse", 3, 1), ("ninigi_teahouse", 3, 1), ("ninigi_dojo", 3, 2), ("ninigi_bamboofarm", 4, 1) },
        ["SEAS"] = new[] { ("seas_barracks", 1, 1), ("seas_steelwork", 1, 1), ("seas_garage", 1, 1), ("seas_greenhouse", 2, 2),
            ("seas_turret_tower", 2, 1), ("seas_barracks", 2, 2), ("seas_greenhouse", 3, 3), ("seas_garage", 3, 2) },
    };
    static List<AiRow> LegacyOrders(string tribe, int age)
    {
        var outp = new List<AiRow>();
        for (int e = 1; e <= 5; e++)
        {
            if (PLANS.TryGetValue(tribe, out var plan)) foreach (var (name, min, n) in plan) if (min == e && age >= e) outp.Add(new AiRow { Request = "BLDG/" + name, ObjFlag = "", Count = n, Unique = true });
            if (e < 5 && age >= e) outp.Add(new AiRow { Request = "age_" + (e + 1), ObjFlag = "", Count = 1, Unique = true });
        }
        return outp;
    }
    /// <summary>the build list for a tribe / behaviour letter in script order (UpdateBuildList, then CheckForFightUpgrades)</summary>
    public List<AiRow> BuildList(AiCtx ctx)
    {
        if (_buildOrders == null || !_buildOrders.Any(r => r.Tribe == ctx.Tribe)) return LegacyOrders(ctx.Tribe, ctx.Age);
        var outp = _buildOrders.Where(r => Matches(r, ctx)).ToList();
        if (!(ctx.Letter == "G" && ctx.Age < 4)) foreach (var r in _fightUpgrades ?? new List<AiRow>()) if (Matches(r, ctx)) outp.Add(r);     // BV:1629
        return outp;
    }
    public List<AiRow> HeroRequests(int set) => _heroRequests.Where(r => r.Set == set).ToList();
    public List<AiRow> Housing(string tribe) => _housing.Where(r => r.Tribe == tribe).ToList();
    public List<AiRow> CounterShips(AiCtx ctx) => _counterShips.Where(r => Matches(r, ctx)).ToList();

    // ---------------------------------------------------------------- attack plans (DA:962-1530)
    static string[] P(string a) => a.Split(' ');
    /// <summary>skirmish rows / campaign rows by behaviour letter (the shipped tables, §5.1)</summary>
    static readonly Dictionary<string, Dictionary<string, string[][]>> FALLBACK_PLANS = new()
    {
        ["Hu"] = new() { ["D"] = new[] { P("pyramid violence suicide violence suicide"), P("blitz suicide violence suicide violence") }, ["G"] = new[] { P("none suicide violence suicide violence"), P("scout suicide violence suicide violence") }, ["S"] = new[] { P("none none none suicide violence"), P("scout none violence suicide violence") } },
        ["Aje"] = new() { ["D"] = new[] { P("pyramid suicide violence suicide violence"), P("none suicide violence suicide violence") }, ["G"] = new[] { P("none suicide suicide violence violence"), P("none none none none none") }, ["S"] = new[] { P("none none none suicide violence"), P("scout none none suicide violence") } },
        ["Ninigi"] = new() { ["D"] = new[] { P("pyramid stealth violence suicide violence"), P("blitz suicide stealth suicide violence") }, ["G"] = new[] { P("none stealth suicide suicide violence"), P("scout stealth suicide suicide violence") }, ["S"] = new[] { P("none none none suicide violence"), P("scout none violence suicide violence") } },
        ["SEAS"] = new() { ["D"] = new[] { P("pyramid suicide violence suicide violence"), P("pyramid suicide violence suicide violence") }, ["G"] = new[] { P("none none violence suicide violence"), P("none suicide violence suicide violence") }, ["S"] = new[] { P("none none none suicide violence"), P("scout none violence suicide violence") } },
    };
    public sealed record AttackPlanRow(string[] Attacks, double Guerilla);
    /// <summary>-> { attacks: [5 entries by epoch], guerilla: % }</summary>
    public AttackPlanRow AttackPlan(AiCtx ctx)
    {
        var none = new[] { "none", "none", "none", "none", "none" };
        if (_attackPlans != null && _attackPlans.Count > 0)
        {
            foreach (var r in _attackPlans)
            {
                if (!Matches(r, ctx)) continue;
                // a list appended to the five defaults instead of replacing them (Aje Giraffe on campaign maps) stays "none"
                return new AttackPlanRow(r.Appended ? none.Concat(r.Attacks).Take(5).ToArray() : r.Attacks.Take(5).ToArray(), r.Guerilla);
            }
            return new AttackPlanRow(none, 0);
        }
        // Defender (DA:1010-1505): the defender attacks nobody; the attackers siege from the second epoch
        if (ctx.DefenderGame)
        {
            if (ctx.Defender) return new AttackPlanRow(none, 0);
            var row = ctx.Letter == "D" ? "pyramid siege siege siege siege" : ctx.Letter == "S" ? "none none siege siege siege" : ctx.Tribe == "Hu" ? "none suicide siege siege siege" : "none siege siege siege siege";
            return new AttackPlanRow(P(row), ctx.Tribe == "SEAS" ? 0 : 11);
        }
        if (ctx.Letter == "S" && ctx.Tactic == 5) return new AttackPlanRow(none, 0);
        if (ctx.Letter == "X") return new AttackPlanRow(P(ctx.Tactic == 1 ? "scout scout scout scout scout" : "suicide suicide suicide suicide suicide"), 0);
        var tt = FALLBACK_PLANS.TryGetValue(ctx.Tribe, out var tr) ? tr : FALLBACK_PLANS["Hu"];
        var t = tt.TryGetValue(ctx.Letter, out var l) ? l : FALLBACK_PLANS["Hu"]["G"];
        return new AttackPlanRow(t[ctx.Multimap ? 0 : 1], 0);
    }

    // ---------------------------------------------------------------- armies, unit mix, levels
    static List<ArmyRow> ParseArmy(JsonNode? a)
    {
        var outp = new List<ArmyRow>();
        if (a is not JsonArray arr) return outp;
        foreach (var e in arr)
        {
            if (e?["group"] != null && e["group"].Bool(true)) continue;
            var r = new ArmyRow { Min = e?["min"].Num() ?? 0, Max = e?["max"].Num() ?? 0 };
            if (e?["alternatives"] is JsonArray alts) foreach (var x in alts)
                r.Alternatives.Add(new ArmyAlt { Cls = x?["cls"].Str() ?? "", Level = x?["level"] is JsonValue lv ? lv.Int() : null, ObjFlag = x?["objFlag"] is JsonValue of ? of.Str() : null });
            outp.Add(r);
        }
        return outp;
    }
    /// <summary>army table of a tribe: 'Dodo' | 'Giraffe' | 'Schnecke' | 'Singleplayer' -> rows | null</summary>
    public List<ArmyRow>? Army(string tribe, string table, string name)
    {
        var T = Json?["armies"]?[tribe];
        if (T == null) return null;
        var a = T[table]?[name] ?? T["Singleplayer"]?[name] ?? T["Dodo"]?[name];
        return a != null ? ParseArmy(a) : null;
    }
    public bool HasArmies(string tribe) => Json?["armies"]?[tribe] != null;
    /// <summary>any tribe's table that knows the army (a campaign wave is named by the level designer, the tribe by the map)</summary>
    public List<ArmyRow>? ArmyAny(string name)
    {
        if (Json?["armies"] is not JsonObject A) return null;
        foreach (var (_, T) in A) if (T is JsonObject to) foreach (var (_, k) in to) if (k?[name] != null) return ParseArmy(k[name]);
        return null;
    }
    /// <summary>the standing army's composition for an epoch: { Level_n: [{weight, units[{cls, npc, objFlag}]}] } | null
    /// (the script always loads Units.txt, never the UnitsEasy / Medium / Hard files: MOD:96)</summary>
    public Dictionary<string, List<MixGroup>>? UnitMix(string tribe, int age)
    {
        if (Json?["unitMix"]?[tribe] is not JsonObject T) return null;
        var m = T["default"] ?? T.FirstOrDefault().Value;
        var a = m?["Age_" + age];
        if (a is not JsonObject ao) return null;
        var outp = new Dictionary<string, List<MixGroup>>(StringComparer.Ordinal);
        foreach (var (lv, gs) in ao)
        {
            var l = new List<MixGroup>();
            if (gs is JsonArray ga) foreach (var g in ga)
            {
                var mg = new MixGroup { Weight = g?["weight"] != null ? g["weight"].Num() : 1 };
                if (g?["units"] is JsonArray us) foreach (var u in us) mg.Units.Add(new MixUnit { Cls = u?["cls"].Str() ?? "", Npc = u?["npc"] is JsonValue np ? np.Str() : null, ObjFlag = u?["objFlag"] is JsonValue of ? of.Str() : null });
                l.Add(mg);
            }
            outp[lv] = l;
        }
        return outp;
    }
    /// <summary>Kindergarten: may a unit of this class at this level (1-based) be levelled up? (KG:255)</summary>
    public bool MayLevel(string cls, int lv, SimRandom rng)
    {
        var c = Json?["levelCaps"]?[cls];
        if (c == null) return lv < 3 ? true : lv == 3 ? rng.Next() < 0.25 : false;      // typical fighter row
        int i = lv - 1;
        if (i < c["free"].Num()) return true;
        return c["last"] is JsonArray last && last.Count >= 2 && last[0].Num() == i && rng.Next() * 100 < last[1].Num();
    }
    /// <summary>level (1-based) of a unit spawned for a scripted wave (CH GetProperLevel); 0 = unknown class</summary>
    public int SpawnLevel(string cls)
    {
        if (Json == null) return 1;               // (JS: `AI && ...` is null without ai.json, and null + 1 = 1)
        var v = Json["spawnLevels"]?[cls]; return v == null ? 0 : v.Int() + 1;
    }
    public JsonNode? ObjectData(string cls) => Json?["objectData"]?[cls];
    /// <summary>the rows of an outpost type for a tribe (request, objFlag, count)</summary>
    public List<AiRow>? Outpost(string type, string tribe) => Json?["outposts"]?[type]?[tribe] is JsonArray a ? a.Select(AiRow.From).ToList() : null;
}
