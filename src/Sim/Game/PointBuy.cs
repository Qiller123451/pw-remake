// The army budget ("point buy", docs/spec/pointbuy.md): a player starts with the units and resources bought for the
// game's points. Prices and default presets come from gamedata "pointBuy" (pipeline/build_data.py point_buy:
// NewPointBuyCosts.txt, DefPresets.txt), the computer players' armies from ai.json "pointBuy" (AiPBPreset*.txt).
//
// An army (Army, JSON - kept in the skirmish setup and the campaign's GameStart):
//   { cards: { <slot>: class }, fixed: { <slot>: level } (cards that may only move up and are never sold: the
//     campaign's, the Aje resource collector), res: { food, wood, stone }, min: { food, wood, stone } (no selling
//     below), locked: { <res>: true } (no buying or selling: a mission's "-1") }
// Slots are those of the army pyramid: 0-24 level 1, 25-39 level 2, 40-47 level 3, 48-50 level 4, 51 level 5.
//
// Ported from remake/src/game/pointbuy.js (pw-exporter 98c7703). Golden test: tests/golden/pointbuy_dump.mjs,
// tests/PwRemake.Tests/PointBuyTests.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Game;

/// <summary>an army bought with points (pointbuy.js's army object; JSON as the JS)</summary>
public sealed class Army
{
    /// <summary>slot (0..51) -> class (or a generic card: worker, Resource, Technics, Nature; Blocked)</summary>
    [JsonPropertyName("cards")] public SortedDictionary<int, string> Cards { get; set; } = new();
    /// <summary>slot -> the least level of a fixed card (never sold, moves only up)</summary>
    [JsonPropertyName("fixed")] public SortedDictionary<int, int> Fixed { get; set; } = new();
    [JsonPropertyName("res")] public Dictionary<string, double> Res { get; set; } = new() { ["food"] = 0, ["wood"] = 0, ["stone"] = 0 };
    /// <summary>no selling below these</summary>
    [JsonPropertyName("min")] public Dictionary<string, double> Min { get; set; } = new() { ["food"] = 0, ["wood"] = 0, ["stone"] = 0 };
    /// <summary>resources that can be neither bought nor sold (a mission's "-1")</summary>
    [JsonPropertyName("locked")] public Dictionary<string, bool> Locked { get; set; } = new();
    /// <summary>the Aje resource collector card was added (load)</summary>
    [JsonPropertyName("collector")] public bool Collector { get; set; }
    /// <summary>the army page's preset choice ("d:standard", "o:&lt;own name&gt;"; "" none)</summary>
    [JsonPropertyName("preset")] public string Preset { get; set; } = "";

    public double R(string r) => Res.TryGetValue(r, out var v) ? v : 0;
    public double MinOf(string r) => Min.TryGetValue(r, out var v) ? v : 0;
    public bool IsLocked(string r) => Locked.TryGetValue(r, out var v) && v;
    public bool IsFixed(int slot) => Fixed.ContainsKey(slot) && Fixed[slot] != 0;

    public Army Clone() => new()
    {
        Cards = new(Cards), Fixed = new(Fixed), Res = new(Res), Min = new(Min), Locked = new(Locked), Collector = Collector, Preset = Preset,
    };
}

/// <summary>a preset army: DefPresets.txt (gamedata), an own preset, an AiPBPreset, a mission's start army</summary>
public sealed class PbPreset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("cost")] public double Cost { get; set; }
    [JsonPropertyName("standard")] public bool Standard { get; set; }
    /// <summary>slot -> class, in slot order</summary>
    [JsonPropertyName("units")] public List<KeyValuePair<string, string>> Units { get; set; } = new();
    [JsonPropertyName("res")] public Dictionary<string, double> Res { get; set; } = new();
    /// <summary>standard | boomer | rusher ... (from the name _pb_&lt;kind&gt;_...)</summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    /// <summary>own presets: the tribe</summary>
    [JsonPropertyName("tribe")] public string? Tribe { get; set; }

    public PbPreset With(string kind) => new() { Name = Name, Cost = Cost, Standard = Standard, Units = Units, Res = Res, Kind = kind, Tribe = Tribe };

    /// <summary>a JSON object of slot -> class in JS order (integer keys ascending, then the others in file order)</summary>
    public static List<KeyValuePair<string, string>> UnitsOf(JsonNode? n)
    {
        var l = new List<KeyValuePair<string, string>>();
        if (n is not JsonObject o) return l;
        foreach (var (k, v) in o) l.Add(new(k, v.Str()));
        return JsOrder(l);
    }
    /// <summary>the iteration order of a JS object's keys: array-index keys ascending first, then the rest as inserted</summary>
    public static List<KeyValuePair<string, T>> JsOrder<T>(IEnumerable<KeyValuePair<string, T>> kv)
    {
        static bool Idx(string k, out uint i) => uint.TryParse(k, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out i) && i.ToString(System.Globalization.CultureInfo.InvariantCulture) == k && i != uint.MaxValue;
        var list = kv.ToList();
        var ints = list.Where(x => Idx(x.Key, out _)).OrderBy(x => { Idx(x.Key, out var i); return i; });
        return ints.Concat(list.Where(x => !Idx(x.Key, out _))).ToList();
    }
}

/// <summary>an entry of the shop (ShoppingList): the class, its tab (0 infantry, 1 cavalry, 2 heroes) and prices per level</summary>
public sealed record ShopEntry(string Cls, int Tab, double[]? Prices);

public sealed class PointBuy
{
    static readonly Logger L = Log.For("Game.PointBuy");
    public static readonly int[][] SLOT_LEVELS = { new[] { 0, 24 }, new[] { 25, 39 }, new[] { 40, 47 }, new[] { 48, 50 }, new[] { 51, 51 } };
    public static int SlotLevel(int slot) => slot >= 51 ? 5 : slot >= 48 ? 4 : slot >= 40 ? 3 : slot >= 25 ? 2 : 1;
    public static readonly string[] RES = { "food", "wood", "stone" };
    /// <summary>the multiplayer food gift (GIW:224-226, SL:640-658): free, and the least food</summary>
    public const int GIFT = 30;
    /// <summary>the Aje resource collector's card (PD:185-195)</summary>
    public const int COLLECTOR_SLOT = 24;
    static readonly Dictionary<string, string> GENERIC = new() { ["worker"] = "res", ["Resource"] = "res", ["Technics"] = "tec", ["Nature"] = "nat" };
    /// <summary>AIC:159-187</summary>
    static string Diff(double d) => d <= 2 ? "Easy" : d <= 5 ? "Medium" : "Hard";
    static readonly Regex KIND = new("^_pb_([a-z]+)_", RegexOptions.CultureInvariant);

    readonly Rules.Rules D;
    readonly JsonObject _costs, _cats, _heroes, _rates, _presets;
    readonly JsonObject _ai;

    public PointBuy(Rules.Rules d, JsonNode? aiData)
    {
        D = d;
        var pb = d.Extra.PointBuy as JsonObject;
        _costs = pb?["costs"] as JsonObject ?? new JsonObject();
        _cats = pb?["cats"] as JsonObject ?? new JsonObject();
        _heroes = pb?["heroes"] as JsonObject ?? new JsonObject();
        _rates = pb?["rates"] as JsonObject ?? new JsonObject();
        _presets = pb?["presets"] as JsonObject ?? new JsonObject();
        _ai = aiData?["pointBuy"] as JsonObject ?? new JsonObject();
    }
    /// <summary>the game data has the price lists (older data: no army budget)</summary>
    public bool Ready => _costs.Count > 0;

    // ------------------------------------------------------------ prices (CPointBuyMgr, PBM:231-303)
    /// <summary>the class a card or preset entry creates: generic workers by caste, Stina rides her sabre-tooth (SL:506-524)</summary>
    public (string Cls, string? Caste) Resolve(string name, string tribe)
    {
        if (GENERIC.TryGetValue(name, out var caste)) return (tribe == "SEAS" ? "seas_worker" : tribe.ToLowerInvariant() + "_worker", tribe == "SEAS" ? "res" : caste);
        if (name == "Stina_s0") return ("special_eusmilus", "nat");
        return (name, null);
    }
    static double[]? Row(JsonNode? n) => n is JsonArray a ? a.Select(x => x.Num()).ToArray() : null;
    JsonNode? RowNode(string name, string tribe) => _costs[tribe]?[name] ?? _heroes[name] ?? (name == "special_eusmilus" ? _heroes["Stina_s0"] : null);
    /// <summary>the price of a class at a level (1..5); 0 = not at that level or unknown (heroes: the same for every tribe)</summary>
    public double Price(string name, int level, string tribe)
    {
        var row = RowNode(name, tribe) as JsonArray;
        if (row == null || level < 1 || level > row.Count) return 0;
        return row[level - 1].Num();
    }
    public double CardCost(string name, int slot, string tribe) => Price(name, SlotLevel(slot), tribe);
    public double Rate(string res, string tribe)
    {
        var r = (_rates[tribe] ?? _rates["Hu"])?[res].Num() ?? 0;
        return r != 0 ? r : res switch { "food" => 1, "wood" => 1.5, "stone" => 2, _ => 0 };
    }
    public double ResCost(string res, double amount, string tribe) => Math.Ceiling(Rate(res, tribe) * amount - 1e-9);
    public double ResFor(string res, double points, string tribe) => Math.Max(0, Math.Floor(points / Rate(res, tribe) + 1e-9));
    public bool IsHero(string name) => _heroes.ContainsKey(name) || name == "special_eusmilus";
    /// <summary>the points a player has: the budget, the food gift of a multiplayer game and the price of the Aje
    /// collector card (GIW:399-424) - credits -1 = the predefined start</summary>
    public double Total(double credits, string tribe, bool mp = true, bool buildings = true)
    {
        if (credits < 0) return -1;
        return credits + (mp ? GIFT : 0) + (tribe == "Aje" && buildings ? Price("aje_resource_collector", 1, "Aje") : 0);
    }
    public double Cost(Army army, string tribe)
    {
        double n = 0;
        foreach (var (s, c) in army.Cards) n += CardCost(c, s, tribe);
        // (a mission's locked resources - "-1": the tribe's start stock - are given, not bought: the arena's 200 / 150 / 100
        // were charged 625 points the fixed heroes had already spent)
        foreach (var r in RES) if (!army.IsLocked(r)) n += ResCost(r, army.R(r), tribe);
        return n;
    }
    public double Left(Army army, double total, string tribe) => total - Cost(army, tribe);

    // ------------------------------------------------------------ the shop (ShoppingList, SHL:240-421)
    /// <summary>the classes to buy, in the price list's order; classes the game doesn't know are left out; allowed: the
    /// map's Restrictions/Chars lists of the player (campaign) or null</summary>
    public List<ShopEntry> Shop(string tribe, ISet<string>? allowed = null)
    {
        var outp = new List<ShopEntry>();
        bool Ok(string c) => allowed == null || allowed.Contains(c);
        if (_cats[tribe] is JsonObject cats)
            foreach (var (cls, cat) in cats)
            {
                if (!D.Exists(cls) || !Ok(cls)) continue;
                outp.Add(new ShopEntry(cls, cat.Str() == "Character" ? 0 : 1, Row(_costs[tribe]?[cls])));
            }
        foreach (var (h, prices) in _heroes)
        {
            var cls = h == "Stina_s0" ? "special_eusmilus" : h;
            if (h == "special_eusmilus" || !D.Exists(cls) || (allowed != null && !allowed.Contains(h) && !allowed.Contains(cls))) continue;
            outp.Add(new ShopEntry(cls, 2, Row(prices)));
        }
        return outp;
    }

    // ------------------------------------------------------------ the pyramid
    public bool HasHero(Army army, string name)
    {
        bool Same(string c) => c == name || (name == "special_eusmilus" && c == "Stina_s0") || (name == "Stina_s0" && c == "special_eusmilus");
        return army.Cards.Values.Any(Same);
    }
    /// <summary>the first free slot of a level (FindFreeSlot; the highest ids of a level are blocked first: NP:1950-1970)</summary>
    public int FreeSlot(Army army, int level, int blocked = 0)
    {
        int a = SLOT_LEVELS[level - 1][0], b = SLOT_LEVELS[level - 1][1];
        for (int s = a; s <= b - blocked; s++) if (!army.Cards.ContainsKey(s)) return s;
        return -1;
    }
    static int Blocked(int[]? blocked, int level) => blocked != null && level - 1 < blocked.Length ? blocked[level - 1] : 0;
    /// <summary>buy a class at a level (PBM:110-136) -> the slot or null</summary>
    public int? Buy(Army army, string name, int level, double total, string tribe, int[]? blocked = null)
    {
        if (IsHero(name) && HasHero(army, name)) return null;
        double price = Price(name, level, tribe);
        if (price == 0) return null;
        int slot = FreeSlot(army, level, Blocked(blocked, level));
        if (slot < 0 || Left(army, total, tribe) < price) return null;
        army.Cards[slot] = name;
        return slot;
    }
    /// <summary>a double-click in the shop: the lowest level with a price and a free slot (PBM:95-108)</summary>
    public int? BuyLowest(Army army, string name, double total, string tribe, int[]? blocked = null)
    {
        for (int lv = 1; lv <= 5; lv++)
        {
            if (Price(name, lv, tribe) == 0 || FreeSlot(army, lv, Blocked(blocked, lv)) < 0) continue;
            return Buy(army, name, lv, total, tribe, blocked);
        }
        return null;
    }
    public bool Sell(Army army, int slot)
    {
        if (army.Fixed.ContainsKey(slot)) return false;
        army.Cards.Remove(slot);
        return true;
    }
    /// <summary>a card dragged to another slot (NewPyramid AcceptDropWindowPB / DropWindowPB, NP:1828-1941): never below
    /// its fixed level, up only when the points allow the dearer price, down only to a level it has a price at; a card
    /// in the target slot changes places with it</summary>
    public bool CanMove(Army army, int from, int to, double total, string tribe)
    {
        if (!army.Cards.TryGetValue(from, out var name) || string.IsNullOrEmpty(name) || from == to || to < 0 || to > 51) return false;
        int lf = SlotLevel(from), lt = SlotLevel(to);
        int fx = army.Fixed.GetValueOrDefault(from);
        if (fx != 0 && lt < fx) return false;
        if (lt == lf) return true;
        army.Cards.TryGetValue(to, out var other);
        if (!string.IsNullOrEmpty(other) && army.Fixed.GetValueOrDefault(to) is int ft && ft != 0 && lf < ft) return false;
        double now = CardCost(name, from, tribe), then = CardCost(name, to, tribe);
        if (then == 0) return false;
        // (the original checks only the moved card; the remake also keeps the swapped one affordable)
        double swap = !string.IsNullOrEmpty(other) ? CardCost(other, from, tribe) - CardCost(other, to, tribe) : 0;
        if (!string.IsNullOrEmpty(other) && CardCost(other, from, tribe) == 0) return false;
        return Left(army, total, tribe) + now - then - swap >= 0;
    }
    public void Move(Army army, int from, int to)
    {
        army.Cards.TryGetValue(from, out var a); army.Cards.TryGetValue(to, out var b);
        int fa = army.Fixed.GetValueOrDefault(from), fb = army.Fixed.GetValueOrDefault(to);
        army.Cards.Remove(from); army.Cards.Remove(to);
        army.Fixed.Remove(from); army.Fixed.Remove(to);
        if (a != null) army.Cards[to] = a;
        if (fa != 0) army.Fixed[to] = fa;
        if (!string.IsNullOrEmpty(b)) { army.Cards[from] = b; if (fb != 0) army.Fixed[from] = fb; }
    }
    /// <summary>resources: + / - with the click's step (1, Shift 10, Ctrl 100, both 1000; PD:430-480) -> the change</summary>
    public double AddRes(Army army, string res, double step, double total, string tribe)
    {
        if (army.IsLocked(res)) return 0;
        double cur = army.R(res);
        if (step > 0)
        {
            // never more than the points left buy (the rounding is per resource total)
            double left = Left(army, total, tribe);
            double n = Math.Max(0, Math.Min(step, ResFor(res, left + ResCost(res, cur, tribe), tribe) - cur));
            army.Res[res] = cur + n;
            return n;
        }
        double min = army.MinOf(res);
        double k = Math.Max(0, Math.Min(-step, cur - min));
        army.Res[res] = cur - k;
        return -k;
    }

    // ------------------------------------------------------------ presets
    static PbPreset PresetOf(JsonNode? n) => new()
    {
        Name = n?["name"].Str() ?? "", Cost = n?["cost"].Num() ?? 0, Standard = n?["standard"].Bool() ?? false, Units = PbPreset.UnitsOf(n?["units"]),
        Res = n?["res"] is JsonObject ro ? ro.ToDictionary(kv => kv.Key, kv => kv.Value.Num()) : new(),
    };
    /// <summary>the default presets of a tribe for exactly this budget (PW:270-287), Standard first; a tribe without any
    /// (SEAS) gets a made-up Standard: three workers and the rest as resources (the remake's own)</summary>
    public List<PbPreset> Presets(string tribe, double credits)
    {
        var list = (_presets[tribe] as JsonArray ?? new JsonArray()).Select(PresetOf).Where(p => p.Cost == credits).ToList();
        list = list.OrderBy(p => p.Standard ? 0 : 1).ToList();          // (stable, as Array.sort)
        if (list.Count == 0 && credits >= 0) list.Add(MadeUp(tribe, credits));
        return list.Select(p => p.With(KIND.Match(p.Name) is { Success: true } m ? m.Groups[1].Value : "standard")).ToList();
    }
    public PbPreset? Preset(string tribe, double credits, string kind)
    {
        var l = Presets(tribe, credits);
        return l.FirstOrDefault(p => p.Kind == kind) ?? l.FirstOrDefault();
    }
    public PbPreset MadeUp(string tribe, double credits)
    {
        var w = Resolve("worker", tribe).Cls;
        var units = new List<KeyValuePair<string, string>> { new("0", w), new("1", w), new("2", w) };      // (a generic "worker" card costs nothing)
        double left = credits + GIFT - 3 * Price(w, 1, tribe);
        var res = new Dictionary<string, double>();
        for (int i = 0; i < RES.Length; i++)
        {
            var r = RES[i];
            res[r] = ResFor(r, Math.Max(0, left) / (RES.Length - i), tribe);
            left -= ResCost(r, res[r], tribe);
        }
        return new PbPreset { Name = "_pb_standard_" + Jsn(credits) + "_" + tribe.ToLowerInvariant(), Cost = credits, Standard = true, Units = units, Res = res };
    }
    static string Jsn(double v) => Rules.TT.JsString(v);
    public static Army Empty() => new();

    /// <summary>LoadPreset (PD:145-239): the units in the preset's order, each only while the points last (fixed at their
    /// level in a campaign), the Aje collector card, then the preset's resources as far as the points left buy them.
    /// mp: the multiplayer minimum of 30 food; res -1 = that resource is locked (a mission's), given as lockedRes</summary>
    public Army Load(PbPreset? preset, string tribe, double total, bool fix = false, bool buildings = true, bool mp = true, IReadOnlyDictionary<string, double>? lockedRes = null)
    {
        var army = Empty();
        double left = total;
        foreach (var (k, name) in preset?.Units ?? new())
        {
            if (!int.TryParse(k, out int slot) || !(slot >= 0 && slot <= 51) || name == "" || name == "Blocked") continue;
            double c = CardCost(name, slot, tribe);
            if (total >= 0 && c > left) continue;
            army.Cards[slot] = name;
            if (fix) army.Fixed[slot] = SlotLevel(slot);
            left -= c;
        }
        if (tribe == "Aje" && buildings) { army.Cards[COLLECTOR_SLOT] = "aje_resource_collector"; army.Fixed[COLLECTOR_SLOT] = 1; army.Collector = true; }
        foreach (var r in RES)
        {
            double a = preset != null && preset.Res.TryGetValue(r, out var pr) ? pr : 0;
            if (a < 0 || (lockedRes != null && lockedRes.ContainsKey(r)))
            {
                army.Locked[r] = true;
                a = lockedRes != null && lockedRes.TryGetValue(r, out var lr) ? lr : 0;
                army.Res[r] = a; army.Min[r] = a;
                continue;
            }
            if (total >= 0) { a = Math.Min(a, ResFor(r, left, tribe)); left -= ResCost(r, a, tribe); }
            army.Res[r] = a;
            army.Min[r] = fix ? a : 0;
        }
        if (mp) { army.Min["food"] = Math.Max(army.MinOf("food"), GIFT); army.Res["food"] = Math.Max(army.R("food"), GIFT); }
        return army;
    }
    /// <summary>Clear (PD:337-379): no cards but the Aje collector, 30 food</summary>
    public Army Clear(string tribe, bool buildings = true, bool mp = true) => Load(null, tribe, 0, buildings: buildings, mp: mp);

    // ------------------------------------------------------------ the computer player's army (AIC:148-155)
    /// <summary>&lt;Tribe&gt;/AiPBPreset&lt;sub&gt;&lt;Easy|Medium|Hard&gt;, then without the difficulty, then the generic
    /// files; the tier of the budget (exactly, else the largest below it: [?engine])</summary>
    public PbPreset? AiPreset(string tribe, double credits, string sub, double difficulty)
    {
        string d = Diff(difficulty);
        var sets = new[] { _ai[tribe] as JsonObject, _ai["generic"] as JsonObject }.Where(x => x != null).Select(x => x!).ToList();
        JsonObject? file = null;
        foreach (var set in sets) { file = (set[sub + d] ?? set[sub]) as JsonObject; if (file != null) break; }
        if (file == null) foreach (var set in sets) { file = (set["G1" + d] ?? set["G1"]) as JsonObject; if (file != null) break; }
        if (file == null) return null;
        var tiers = file.Select(kv => Json.ParseNum(kv.Key, double.NaN)).Where(t => !double.IsNaN(t) && t <= credits).OrderByDescending(t => t).ToList();
        JsonNode? e = file[Jsn(credits)];
        if (e == null && tiers.Count > 0) e = file[Jsn(tiers[0])];
        if (e == null) return null;
        var units = new List<KeyValuePair<string, string>>();
        // the generic files number the units 1..n and name them warrior / scout: the tribe's own of that kind [?engine]
        foreach (var (k, v) in PbPreset.UnitsOf(e["chars"])) units.Add(new(k, GenericClass(v, tribe)));
        return new PbPreset
        {
            Name = "ai", Cost = credits, Units = units,
            Res = e["resources"] is JsonObject ro ? ro.ToDictionary(kv => kv.Key, kv => kv.Value.Num()) : new(),
        };
    }
    public string GenericClass(string name, string tribe)
    {
        if (name != "warrior" && name != "scout") return name;
        var own = tribe.ToLowerInvariant() + "_" + name;
        if (D.Exists(own) && Price(own, 1, tribe) != 0) return own;
        var cats = name == "warrior" ? new[] { "Character" } : new[] { "Animals", "Vehicles" };
        if (_cats[tribe] is JsonObject co)
            foreach (var (c, k) in co)
                if (cats.Contains(k.Str()) && !c.EndsWith("_worker") && Price(c, 1, tribe) != 0 && D.Exists(c)) return c;
        return Resolve("worker", tribe).Cls;
    }

    // ------------------------------------------------------------ the game start (StartLocation.usl:482-689)
    /// <summary>-> the units created. Workers stand in three groups by caste (res 0°, tec 120°, nat / Stina 240°), the
    /// others anywhere around the start; each at its slot's level, at least the class's least level
    /// (CorrectLevelToMinLevel). check: a human player's army is checked against the points (SL:661-688): more than
    /// allowed or less than 30 food -> no resources, one worker (and the Aje collector)</summary>
    public List<Entity> Place(GameWorld W, Player p, Army army, double x, double z, double stand = 4, double face = 0, double total = -1, bool check = false)
    {
        var made = new List<Entity>();
        var tribe = p.Tribe;
        if (check && total >= 0 && (Cost(army, tribe) > total || army.R("food") < GIFT))
        {
            army = new Army { Cards = { [0] = "worker" } };
            if (tribe == "Aje") army.Cards[COLLECTOR_SLOT] = "aje_resource_collector";
            p.Cheated = true;
            L.Warn($"{p}: the army costs more than the points allowed (or has less than {GIFT} food): one worker instead");
        }
        int i = 0;
        foreach (var (k, name) in army.Cards)
        {
            if (name == "Blocked") continue;
            var (cls, caste) = Resolve(name, tribe);
            if (!D.Exists(cls)) { L.Warn("point buy: unknown class " + cls); continue; }
            double ang = caste == "tec" ? 2.094 : caste == "nat" ? 4.189 : caste == "res" ? 0 : (i * 2.399) % 6.283;
            double r = stand + 2 + (i % 3) * 1.4;
            double gx = x + SMath.Cos(face + ang) * r, gz = z + SMath.Sin(face + ang) * r;
            int lv = Math.Max(SlotLevel(k), D.MinLevel(cls, p));
            try { var u = W.SpawnUnit(cls, p, gx, gz, lv, face); if (u != null) made.Add(u); }
            catch (Exception e) { L.Warn($"point buy: not created {cls}: {e.Message}"); }
            i++;
        }
        foreach (var r in RES) p.Res[r] = army.R(r);
        p.Res["skulls"] = 0;
        return made;
    }
}
