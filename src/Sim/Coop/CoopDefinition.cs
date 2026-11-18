// A mission's co-op definition (docs/COOP_DESIGN.md "The co-op definition (schema v1)", docs/COOP.md): one Lua file per
// mission and configuration, game/coop/<config>/<NN>.lua (NN = the mission id of index.json), returning a table; a
// game/coop/common/<NN>.lua may hold what both configurations share - the configuration's file is laid over it (its
// keys replace the common file's, tables merged key by key).
//
// The file runs in the maps' sandbox (LuaHost: no files, no os, an instruction budget) and its table becomes JSON:
// the definition travels inside GameStart (GameStart.Coop.Definition) so that every machine of a networked game, a
// saved game and a replay build the same world from it, whatever game/coop holds on the machine that loads it later.
//
// The core (src/Sim/Coop) reads seats, armies, heroes, scaling and the trigger mapping from it. `extend` and
// `resources` are the map work's (src/Sim/Maps/MapExtension.cs): kept here as plain JSON and handed to CoopMapHook.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MoonSharp.Interpreter;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;

namespace PwRemake.Sim.Coop;

/// <summary>A co-op seat beyond seat 1 as the definition places it (`seats[2]`, `seats[3]`).</summary>
public sealed class CoopSeatDef
{
    public int Seat;
    /// <summary>start position (game metres, x / z as the simulation uses them) and heading (radians, map heading as
    /// start locations have it); null start: the seat takes an ally's slot (Ally) or has none</summary>
    public double? X, Z;
    public double Dir;
    /// <summary>"default": the tribe's start army of the mission (seat 1's start army in the seat's tribe), "none":
    /// nothing, else the list of classes (in the seat's tribe; a class of another tribe is swapped)</summary>
    public string Army = "default";
    public List<string>? ArmyList;
    /// <summary>the id of an existing allied player of the map whose slot this seat takes (null: a new player)</summary>
    public int? Ally;
    /// <summary>start resources (`start_resources = { food = .., wood = .., stone = .., iron = .. }`) - null: seat 1's
    /// of the mission. (A `resources` list under a seat is the map work's: MapResources.)</summary>
    public Dictionary<string, double>? Resources;
    /// <summary>the main building at the start (null: as the mission's human player has it; nobuild: never)</summary>
    public bool? Base;
    /// <summary>the map work's, per seat: land and resources that exist only when this seat is used (plain data,
    /// handed to CoopMapHook for the seats in use only)</summary>
    public JsonObject? Extend;
    public JsonArray? MapResources;
    /// <summary>`hero_army`: units the seat gets at the start of a hero phase (beside seat 1's start: the mission's own
    /// classes, in the seat's tribe) - mission 1's Norse warrior each, the user's wish; empty: none</summary>
    public List<string> HeroArmy = new();
}

/// <summary>`phase`: the hero phase (docs/COOP.md "Hero phase"): the segment of a mission before seat 1 has a base</summary>
public sealed class CoopPhaseDef
{
    /// <summary>true / false: the definition says; null: automatic - a building mission whose seat 1 starts with neither
    /// a worker nor a building has one</summary>
    public bool? Hero;
    /// <summary>when the base phase begins: null = automatic (the first worker, main building or building the
    /// triggers give seat 1); "trigger:&lt;name&gt;" (that trigger fired) or "quest:&lt;name&gt;" (that quest shown)</summary>
    public string? BaseWhen;
    /// <summary>the gifts of the hero phase: "deal" (default: round robin over the seats) or "seat1" (as the base phase)</summary>
    public string Gifts = "deal";
    /// <summary>`attack`: a region of the mission (the phase's objective, e.g. mission 8's barbarian camp "rescamp"): AI
    /// commanders without a command attack the enemies in it until the phase ends; null: they escort the humans' heroes</summary>
    public string? Attack;
}

/// <summary>`map_objects`: attributes of the mission's map objects changed in the co-op game (a nest's spawn_max ...)</summary>
public sealed class CoopMapObjectDef
{
    public string Name = "";
    public int Seats = 2;
    public Dictionary<string, string> Attr = new(StringComparer.Ordinal);
}

/// <summary>`gates`: a gate of the mission's own class in place of some of its wall pieces (seats = from how many seats);
/// the conditions of the triggers on those pieces watch the gate instead (mission 13: either gate falls - the mission is
/// lost, as the wall's own condition says)</summary>
public sealed class CoopGateDef
{
    public int Seats = 3;
    public string Class = "";
    public List<string> Replaces = new();
    public string Name = "";
}

/// <summary>`reroute`: scripted wave paths (WYPT) that end near `Near` end at `To` instead - every `Every`-th wave, and
/// always the waves whose first point lies in `From` (seats = from how many seats)</summary>
public sealed class CoopRerouteDef
{
    public int Seats = 3;
    public double NearX, NearZ, NearR = 40;
    public List<(double X, double Z)> To = new();
    public int Every = 2;
    public double? FromX, FromZ, FromR;
}

/// <summary>`start_split`: seat 1's placed objects in an area given to another seat at the start (for exactly `For`
/// seats), optionally moved (removed there and made again at `Move`)</summary>
public sealed class CoopSplitDef
{
    public int For = 3;
    public double X, Z, R = 10;
    public int To = 2;
    public List<string> Classes = new();
    public int Max = 999;
    public double? MoveX, MoveZ;
}

/// <summary>`scaling`: the enemies per extra seat (multiplied by the co-op difficulty's factor).</summary>
public sealed class CoopScalingDef
{
    /// <summary>more units per extra seat (0.6 = +60 %): placed garrisons, spawns of the triggers, scripted waves, the
    /// computer players' population limit</summary>
    public double Count = CoopScaling.DefaultCount;
    /// <summary>more health per extra seat (0.25 = +25 %: the damage they take is divided by 1.25)</summary>
    public double Health = CoopScaling.DefaultHealth;
    /// <summary>more damage per extra seat</summary>
    public double Damage = CoopScaling.DefaultDamage;
    /// <summary>player ids left as they are</summary>
    public List<int> Exclude = new();
    /// <summary>which kinds of "more units" apply (all true by default): placed units (garrisons), trigger spawns
    /// (SPGR / COBJ / CPLX), scripted waves (AIFT) and the computer players' own production</summary>
    public bool Garrisons = true, Spawns = true, Waves = true, Production = true;
}

public sealed class CoopDefinition
{
    static readonly Logger L = Log.For("Coop");
    public const int SchemaVersion = 1;

    public int Version = 1;
    /// <summary>a hero-party mission without bases: heroes divided evenly, no main buildings</summary>
    public bool NoBuild;
    /// <summary>the story heroes split among the humans (classes of the mission: Cole_s0, Bela_s0, Stina_s0)</summary>
    public List<string> Heroes = new();
    /// <summary>the map work's: terrain added at the edges (plain data, docs/COOP_DESIGN.md)</summary>
    public JsonObject? Extend;
    /// <summary>the map work's: extra resources (plain data)</summary>
    public JsonArray? Resources;
    /// <summary>seats 2 and 3 (by seat number)</summary>
    public readonly SortedDictionary<int, CoopSeatDef> Seats = new();
    /// <summary>existing allies of the map that may become seats (`allies.usable`)</summary>
    public List<int> UsableAllies = new();
    public CoopScalingDef Scaling = new();
    /// <summary>`triggers.give_to`: the seat that receives what the original gives "the player" (default 1)</summary>
    public int GiveTo = 1;
    public string Notes = "";
    /// <summary>`enemies`: enemy bases grown or added (CoopEnemies.cs)</summary>
    public List<CoopEnemyDef> Enemies = new();
    public CoopPhaseDef Phase = new();
    public List<CoopMapObjectDef> MapObjects = new();
    public List<CoopGateDef> Gates = new();
    public List<CoopRerouteDef> Reroutes = new();
    public List<CoopSplitDef> StartSplit = new();
    /// <summary>`mortal`: mission objects (by name) the triggers may not make invulnerable in co-op - e.g. a pre-placed
    /// defence the original keeps whole until a phase ends, dealt to another seat</summary>
    public HashSet<string> Mortal = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>`deal_start`: seat 1's start fighters (not heroes, not workers) are dealt round robin over the seats</summary>
    public bool DealStart;
    /// <summary>`campaign = false`: co-op campaign (mode 2) not offered for this mission (Shared only); `reason` shown</summary>
    public bool CampaignMode = true;
    public string Reason = "";
    /// <summary>the whole table as JSON (what GameStart carries)</summary>
    public JsonObject Raw = new();
    /// <summary>where it was read from (informational)</summary>
    public string Source = "";

    /// <summary>the most seats the definition supports: 1 + the seats it places</summary>
    public int MaxSeats => 1 + Seats.Keys.Count(k => k is 2 or 3);
    public bool Supports(int seats) => seats <= 1 || Enumerable.Range(2, seats - 1).All(Seats.ContainsKey);

    // ------------------------------------------------------------------ where the files are
    /// <summary>game/coop next to the running game (or PW_COOP)</summary>
    public static string Root()
    {
        var env = Environment.GetEnvironmentVariable("PW_COOP");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "game", "coop");
            if (Directory.Exists(c)) return c;
            c = Path.Combine(d.FullName, "coop");
            if (Directory.Exists(c) && (Directory.Exists(Path.Combine(c, "common")) || Directory.Exists(Path.Combine(c, "base")))) return c;
        }
        return Path.Combine("game", "coop");
    }

    /// <summary>the files of a mission: common first, then the configuration's (either may be missing)</summary>
    public static List<string> FilesOf(string configuration, int missionId, string? root = null)
    {
        root ??= Root();
        var nn = missionId.ToString("00");
        var res = new List<string>();
        foreach (var dir in new[] { "common", configuration })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var f = Path.Combine(root, dir, nn + ".lua");
            if (File.Exists(f)) res.Add(f);
        }
        return res;
    }

    /// <summary>does the mission have a co-op definition (mode 2 offered in the lobby)?</summary>
    public static bool Exists(string configuration, int missionId, string? root = null) => FilesOf(configuration, missionId, root).Count > 0;

    /// <summary>the mission's definition, or null (no file, or errors - logged; problems lists them)</summary>
    public static CoopDefinition? Load(string configuration, int missionId, string? root = null, List<string>? problems = null)
    {
        var files = FilesOf(configuration, missionId, root);
        if (files.Count == 0) return null;
        JsonObject? merged = null;
        foreach (var f in files)
        {
            JsonObject? t;
            try { t = LuaFileToJson(f); }
            catch (Exception e) { var msg = $"co-op definition {f}: {e.Message}"; L.Warn(msg); problems?.Add(msg); return null; }
            if (t == null) { var msg = $"co-op definition {f} does not return a table"; L.Warn(msg); problems?.Add(msg); return null; }
            merged = merged == null ? t : Merge(merged, t);
        }
        var d = FromJson(merged!, problems);
        d.Source = string.Join(" + ", files.Select(Path.GetFileName));
        return d;
    }

    /// <summary>runs a definition file in the sandbox and returns its table as JSON</summary>
    public static JsonObject? LuaFileToJson(string path)
    {
        var lua = new LuaHost(LuaMission.DefaultStdDir(), Path.GetDirectoryName(path));
        var r = lua.RunString(File.ReadAllText(path), Path.GetFileName(path));
        if (r == null) throw new InvalidOperationException(string.Join("; ", lua.Errors.DefaultIfEmpty("Lua error")));
        return r.Type == DataType.Table ? ToJson(r.Table) as JsonObject : null;
    }

    /// <summary>Lua source of a definition (tests)</summary>
    public static CoopDefinition FromLua(string code, List<string>? problems = null)
    {
        var lua = new LuaHost(LuaMission.DefaultStdDir(), null);
        var r = lua.RunString(code, "coop.lua");
        if (r == null || r.Type != DataType.Table) throw new InvalidOperationException("the definition does not return a table: " + string.Join("; ", lua.Errors));
        return FromJson((JsonObject)ToJson(r.Table)!, problems);
    }

    /// <summary>a Lua table as JSON: a sequence 1..n is an array, everything else an object (number keys as text)</summary>
    public static JsonNode? ToJson(Table t)
    {
        var pairs = t.Pairs.ToList();
        int n = t.Length;
        if (n > 0 && pairs.Count == n)
        {
            var a = new JsonArray();
            for (int i = 1; i <= n; i++) a.Add(ToJson(t.Get(i)));
            return a;
        }
        var o = new JsonObject();
        foreach (var p in pairs)
        {
            var k = p.Key.Type == DataType.Number ? TrigUtil.NumStr(p.Key.Number) : p.Key.CastToString();
            if (k == null) continue;
            o[k] = ToJson(p.Value);
        }
        return o;
    }
    static JsonNode? ToJson(DynValue v) => v.Type switch
    {
        DataType.Boolean => JsonValue.Create(v.Boolean),
        DataType.Number => JsonValue.Create(v.Number),
        DataType.String => JsonValue.Create(v.String),
        DataType.Table => ToJson(v.Table),
        _ => null,
    };

    /// <summary>b laid over a: b's keys replace a's, objects merged key by key</summary>
    public static JsonObject Merge(JsonObject a, JsonObject b)
    {
        var r = (JsonObject)a.DeepClone();
        foreach (var (k, v) in b)
        {
            if (v is JsonObject bo && r[k] is JsonObject ao) r[k] = Merge(ao, bo);
            else r[k] = v?.DeepClone();
        }
        return r;
    }

    /// <summary>a Lua list in the JSON (an array; an empty table is an empty object)</summary>
    static IEnumerable<JsonNode?> Items(JsonNode? n) => n is JsonArray a ? a : n is JsonObject o ? o.Select(x => x.Value) : Enumerable.Empty<JsonNode?>();

    /// <summary>is co-op campaign mode (mode 2) offered for the mission? (a definition with `campaign = false` is for
    /// Shared only)</summary>
    public static bool CampaignAvailable(string configuration, int missionId, string? root = null) => CampaignNote(configuration, missionId, root) == null;
    /// <summary>why mode 2 is not offered (null: it is) - cached per configuration and mission</summary>
    public static string? CampaignNote(string configuration, int missionId, string? root = null)
    {
        var key = (root ?? Root()) + "|" + configuration + "|" + missionId;
        lock (_avail)
        {
            if (_avail.TryGetValue(key, out var r)) return r;
            string? why = null;
            if (!Exists(configuration, missionId, root)) why = "No co-op campaign definition yet (Shared mode only)";
            else if (Load(configuration, missionId, root) is not { } d) why = "The co-op definition has errors (Shared mode only)";
            else if (!d.CampaignMode) why = d.Reason.Length > 0 ? d.Reason : "Co-op campaign: not reworked yet (Shared mode only)";
            return _avail[key] = why;
        }
    }
    static readonly Dictionary<string, string?> _avail = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ the schema
    public static CoopDefinition FromJson(JsonObject j, List<string>? problems = null)
    {
        var d = new CoopDefinition { Raw = (JsonObject)j.DeepClone() };
        void Problem(string s) { problems?.Add(s); L.Warn("co-op definition: " + s); }
        d.Version = j["version"].Int(1);
        if (d.Version != SchemaVersion) Problem($"version {d.Version} (this game reads {SchemaVersion})");
        d.NoBuild = j["nobuild"].Bool();
        if (j["heroes"] is JsonArray ha) d.Heroes = ha.Select(x => x.Str()).Where(x => x.Length > 0).ToList();
        d.Extend = j["extend"] as JsonObject;
        d.Resources = j["resources"] as JsonArray;
        if (j["seats"] is JsonObject so)
            foreach (var (k, v) in so)
            {
                if (!int.TryParse(k, out int seat) || seat < 2 || seat > 3 || v is not JsonObject s) { Problem($"seats[{k}]: seats 2 and 3 only"); continue; }
                var sd = new CoopSeatDef { Seat = seat };
                if (s["start"] is JsonObject st) { sd.X = st["x"].Num(); sd.Z = st["z"].Num(); sd.Dir = st["dir"].Num(); }
                if (s["army"] is JsonArray aa) { sd.Army = "list"; sd.ArmyList = aa.Select(x => x.Str()).Where(x => x.Length > 0).ToList(); }
                else if (s["army"] is JsonValue av) sd.Army = av.Str("default");
                if (s["ally"] is JsonValue al) sd.Ally = al.Int();
                if (s["base"] is JsonValue bv) sd.Base = bv.Bool();
                sd.Extend = s["extend"] as JsonObject;
                sd.MapResources = s["resources"] as JsonArray;
                if (s["start_resources"] is JsonObject ro2) sd.Resources = ro2.ToDictionary(x => x.Key, x => x.Value.Num());
                if (s["hero_army"] is JsonArray ha2) sd.HeroArmy = ha2.Select(x => x.Str()).Where(x => x.Length > 0).ToList();
                if (sd.Ally == null && sd.X == null) Problem($"seats[{seat}] has neither a start nor an ally");
                d.Seats[seat] = sd;
            }
        if (d.Seats.ContainsKey(3) && !d.Seats.ContainsKey(2)) Problem("seats[3] without seats[2]");
        if (j["allies"]?["usable"] is JsonArray ua) d.UsableAllies = ua.Select(x => x.Int()).ToList();
        foreach (var s in d.Seats.Values) if (s.Ally is int a && !d.UsableAllies.Contains(a)) Problem($"seats[{s.Seat}] takes ally {a}, which allies.usable does not list");
        if (j["scaling"] is JsonObject sc)
        {
            var S = d.Scaling;
            if (sc["count"] is JsonValue) S.Count = Math.Max(0, sc["count"].Num());
            if (sc["health"] is JsonValue) S.Health = Math.Max(0, sc["health"].Num());
            if (sc["damage"] is JsonValue) S.Damage = Math.Max(0, sc["damage"].Num());
            if (sc["exclude"] is JsonArray ex) S.Exclude = ex.Select(x => x.Int()).ToList();
            if (sc["garrisons"] is JsonValue) S.Garrisons = sc["garrisons"].Bool(true);
            if (sc["spawns"] is JsonValue) S.Spawns = sc["spawns"].Bool(true);
            if (sc["waves"] is JsonValue) S.Waves = sc["waves"].Bool(true);
            if (sc["production"] is JsonValue) S.Production = sc["production"].Bool(true);
        }
        if (j["triggers"] is JsonObject tr) d.GiveTo = Math.Clamp(tr["give_to"].Int(1), 1, 3);
        d.Notes = j["notes"].Str();
        d.Enemies = CoopEnemyDef.Parse(j["enemies"], Problem);
        if (j["phase"] is JsonObject ph)
        {
            if (ph["hero"] is JsonValue hv) d.Phase.Hero = hv.Bool();
            if (ph["base_when"] is JsonValue bw && bw.Str().Length > 0) d.Phase.BaseWhen = bw.Str();
            d.Phase.Gifts = ph["gifts"].Str("deal");
            if (ph["attack"] is JsonValue pa && pa.Str().Length > 0) d.Phase.Attack = pa.Str();
            if (d.Phase.BaseWhen != null && !d.Phase.BaseWhen.StartsWith("trigger:") && !d.Phase.BaseWhen.StartsWith("quest:")) Problem($"phase.base_when '{d.Phase.BaseWhen}': trigger:<name> or quest:<name>");
            if (d.Phase.Gifts is not ("deal" or "seat1")) Problem($"phase.gifts '{d.Phase.Gifts}': deal or seat1");
        }
        foreach (var n in Items(j["map_objects"]))
            if (n is JsonObject mo && mo["name"].Str().Length > 0)
                d.MapObjects.Add(new CoopMapObjectDef { Name = mo["name"].Str(), Seats = mo["seats"].Int(2), Attr = (mo["attr"] as JsonObject ?? new JsonObject()).ToDictionary(x => x.Key, x => x.Value is JsonValue v && v.TryGetValue<double>(out var dv) ? TrigUtil.NumStr(dv) : x.Value.Str(), StringComparer.Ordinal) });
            else Problem("map_objects: an entry without a name");
        foreach (var n in Items(j["gates"]))
            if (n is JsonObject go && go["class"].Str().Length > 0 && go["replaces"] is JsonArray ra && ra.Count > 0)
                d.Gates.Add(new CoopGateDef { Seats = go["seats"].Int(3), Class = go["class"].Str(), Name = go["name"].Str(), Replaces = ra.Select(x => x.Str()).ToList() });
            else Problem("gates: an entry needs class and replaces");
        foreach (var n in Items(j["reroute"]))
        {
            if (n is not JsonObject ro || ro["near"] is not JsonObject ne || ro["to"] is not JsonArray ta) { Problem("reroute: an entry needs near and to"); continue; }
            var rr = new CoopRerouteDef { Seats = ro["seats"].Int(3), NearX = ne["x"].Num(), NearZ = ne["z"].Num(), NearR = ne["r"] is JsonValue ? ne["r"].Num() : 40, Every = Math.Max(0, ro["every"].Int(2)) };
            foreach (var q in ta) if (q is JsonObject qo) rr.To.Add((qo["x"].Num(), qo["z"].Num()));
            if (ro["from"] is JsonObject fo) { rr.FromX = fo["x"].Num(); rr.FromZ = fo["z"].Num(); rr.FromR = fo["r"] is JsonValue ? fo["r"].Num() : 60; }
            d.Reroutes.Add(rr);
        }
        foreach (var n in Items(j["start_split"]))
        {
            if (n is not JsonObject so2 || so2["at"] is not JsonObject at) { Problem("start_split: an entry needs at"); continue; }
            var sp = new CoopSplitDef { For = so2["for"].Int(3), X = at["x"].Num(), Z = at["z"].Num(), R = at["r"] is JsonValue ? at["r"].Num() : 10, To = so2["to"].Int(2), Max = so2["max"] is JsonValue ? so2["max"].Int() : 999 };
            if (so2["classes"] is JsonArray ca) sp.Classes = ca.Select(x => x.Str()).ToList();
            if (so2["move"] is JsonObject mv) { sp.MoveX = mv["x"].Num(); sp.MoveZ = mv["z"].Num(); }
            d.StartSplit.Add(sp);
        }
        foreach (var n in Items(j["mortal"])) if (n.Str() is { Length: > 0 } mn) d.Mortal.Add(mn);
        d.DealStart = j["deal_start"].Bool();
        if (j["campaign"] is JsonValue cv) d.CampaignMode = cv.Bool(true);
        d.Reason = j["reason"].Str();
        return d;
    }
}
