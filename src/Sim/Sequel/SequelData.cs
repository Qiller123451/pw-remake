// The sequel campaign's own classes (docs/SEQUEL.md "Rules"): copies of original classes with other models, values
// and weapons, written into the tech tree before the rules are read. Each copy takes the original's level filters,
// weapons (copied under the new name: their Users list the copy), build action and moves, with the changes of its
// entry in game/campaigns/containment/classes.json; the rules the original's scripts keep by class name (hero auras,
// Taslov's building speed) hold for it too (gamedata sameAs -> Rules.Kind).
// Only the sequel's games use these rules; the original campaign, the scenarios and the skirmish keep the original tree.
//
// classes.json (exported from the browser remake's game/sequel/rules.js SEQUEL_CLASSES / SEQUEL_HEROES):
//   from     the original class it starts from
//   tribe    (optional) the tribe it is written under (else SEAS, or World / Special as its original)
//   set      values of the object node (and of every level's bonus data): gfx, hitpoints, FOW, speeds ...
//   hp       hit points factor at every level
//   weapons  "none", or changes for the copied weapons {damage, range, level, only {regex} (ids to copy),
//            from: [other weapon ids to copy instead]}
//   build    false, or {at, cost: {food, wood, stone, power}, time, menupos, age, icon} - its build action
//   tags     the runtime's marks: tranq, shock, chain, capture, stabilizer, searchlight, pole, commandpost, net ...
//   desc     the name shown in the game (its other texts: texts.json)
//
// Ported from remake/src/game/sequel/rules.js sequelData (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Sequel;

/// <summary>One class of classes.json.</summary>
public sealed class SequelClass
{
    public string Name = "", From = "", Desc = "";
    public string? Tribe;
    public JsonObject? Set;
    public double Hp;
    /// <summary>"none" = no weapons; null = the original's unchanged</summary>
    public JsonNode? Weapons;
    /// <summary>false = no build action; null = the original's</summary>
    public JsonNode? Build;
    public List<string> Tags = new();
    public bool Hero;
}

/// <summary>The sequel's classes and what they change in the rules (rules.js).</summary>
public sealed class SequelClasses
{
    static readonly Logger L = Log.For("Sequel.Rules");
    /// <summary>name -> class, in the file's order (classes, then heroes)</summary>
    public readonly OrderedMap<string, SequelClass> All = new();
    /// <summary>name -> the texts of texts.json (intro [speaker, line], where, medium, long, vs)</summary>
    public readonly Dictionary<string, JsonObject> Texts = new(StringComparer.Ordinal);

    public static SequelClasses FromJson(JsonNode? classes, JsonNode? texts)
    {
        var s = new SequelClasses();
        void Read(JsonNode? n, bool hero)
        {
            if (n is not JsonObject o) return;
            foreach (var (name, v) in o)
            {
                if (v is not JsonObject c) continue;
                var sc = new SequelClass
                {
                    Name = name, From = c["from"].Str(), Desc = c["desc"].Str(), Tribe = c["tribe"] is JsonValue t ? t.Str() : null,
                    Set = c["set"] as JsonObject, Hp = c["hp"].Num(0), Weapons = c["weapons"]?.DeepClone(), Build = c["build"]?.DeepClone(), Hero = hero,
                };
                if (c["tags"] is JsonArray tg) foreach (var x in tg) sc.Tags.Add(x.Str());
                s.All.Set(name, sc);
            }
        }
        Read(classes?["classes"], false);
        Read(classes?["heroes"], true);
        if (texts is JsonObject to) foreach (var (k, v) in to) if (v is JsonObject e) s.Texts[k] = e;
        return s;
    }

    public bool Has(string name) => All.Has(name);
    public IReadOnlyList<string> TagsOf(string? name) => name != null && All.TryGetValue(name, out var c) ? c.Tags : Array.Empty<string>();
    public bool Tagged(string? name, string tag) => name != null && All.TryGetValue(name, out var c) && c.Tags.Contains(tag);
    public string? SourceOf(string name) => All.TryGetValue(name, out var c) ? c.From : null;

    static readonly string[] TYPES = { "CHTR", "ANML", "VHCL", "BLDG", "SHIP" };
    static string F6(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

    static (string Tribe, string Type, JsonObject Node)? FindClass(JsonObject O, string name)
    {
        foreach (var (tribe, tn) in O)
            foreach (var type in TYPES)
                if (tn?[type]?[name] is JsonObject n) return (tribe, type, n);
        return null;
    }

    static void ApplySet(JsonObject node, SequelClass spec)
    {
        if (spec.Set != null) foreach (var (k, v) in spec.Set) node[k] = v?.DeepClone();
        if (spec.Hp != 0 && node["hitpoints"] is JsonValue hp) node["hitpoints"] = F6(Json.ParseNum(hp.Str(), 0) * spec.Hp);
    }

    static JsonObject Obj(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject o) return o;
        var n = new JsonObject();
        parent[key] = n;
        return n;
    }

    /// <summary>The tech tree and game data with the sequel's classes (both changed in place - give copies);
    /// locked: classes whose build actions are left out (not yet brought in by the campaign: SequelCampaign.LockedBefore).</summary>
    public void Apply(JsonObject tt, JsonObject extra, ISet<string>? locked = null)
    {
        var O = (JsonObject)tt["StartTT"]!["Objects"]!;
        var texts = Obj(extra, "texts");
        var script = Obj(extra, "script");
        var composites = Obj(extra, "composites");
        var sameAs = Obj(extra, "sameAs");
        var idle = extra["idle"] as JsonObject;
        var actionsAll = (JsonObject)tt["StartTT"]!["Actions"]!;
        var filters = Obj(tt, "Filters");
        int made = 0;
        foreach (var (name, spec) in All)
        {
            var src = FindClass(O, spec.From);
            if (src == null) { L.Warn($"sequel: no class {spec.From} (for {name})"); continue; }
            var (stribe, stype, snode) = src.Value;
            string tribe = spec.Tribe ?? (stribe is "World" or "Special" ? stribe : "SEAS");
            var typeNode = Obj(Obj(O, tribe), stype);
            var node = (JsonObject)snode.DeepClone();
            ApplySet(node, spec);
            typeNode[name] = node;
            // level filters: the original's bonus data with the same changes
            if (filters[stribe]?["Upgrades"]?[spec.From] is JsonObject FU)
            {
                var copy = new JsonObject();
                foreach (var (k, f) in FU)
                {
                    if (f is not JsonObject) continue;
                    var js = f.ToJsonString().Replace($"/{spec.From}/", $"/{name}/").Replace($"/{spec.From}\"", $"/{name}\"");
                    var g = (JsonObject)JsonNode.Parse(js)!;
                    if (g["LevelBonusData"] is JsonObject lb) ApplySet(lb, spec);
                    copy[k] = g;
                }
                Obj(Obj(filters, tribe), "Upgrades")[name] = copy;
            }
            // weapons
            bool none = spec.Weapons is JsonValue wv && wv.Str() == "none";
            var wm = spec.Weapons as JsonObject;
            var wsrc = new List<(string Id, JsonObject W)>();
            if (wm?["from"] is JsonArray fromIds)
            {
                foreach (var idn in fromIds)
                {
                    var id = idn.Str();
                    foreach (var (_, tn) in O) if (tn?["Weapons"]?[id] is JsonObject w) { wsrc.Add((id, w)); break; }
                }
            }
            else if (O[stribe]?["Weapons"] is JsonObject W0)
            {
                Regex? only = wm?["only"]?["regex"] is JsonValue rx ? new Regex(rx.Str()) : null;
                foreach (var (id, w) in W0)
                {
                    if (w is not JsonObject wo) continue;
                    bool user = wo["Users"] is JsonObject us && us.Any(kv => kv.Value.Str() == spec.From);
                    if (user && (only == null || only.IsMatch(id))) wsrc.Add((id, wo));
                }
            }
            if (!none)
            {
                var WT = Obj(Obj(O, tribe), "Weapons");
                foreach (var (id, w) in wsrc)
                {
                    var c = (JsonObject)w.DeepClone();
                    c["Users"] = new JsonObject { ["0"] = name };
                    double dmg = wm?["damage"].Num(0) ?? 0;
                    if (dmg != 0 && c["damage"] is JsonValue d0) c["damage"] = F6(Json.ParseNum(d0.Str(), 0) * dmg);
                    if (dmg != 0 && c["enddamage"] is JsonValue d1) c["enddamage"] = F6(Json.ParseNum(d1.Str(), 0) * dmg);
                    if (wm?["range"] is JsonValue rg) c["range"] = F6(rg.Num());
                    if (wm?["level"] is JsonValue lv) c["level"] = lv.Num().ToString(CultureInfo.InvariantCulture);
                    WT[$"{id}__{name}"] = c;
                }
            }
            // the build action: the original's at `at` (MIRAGE builds some SEAS units in other places, or in variants its
            // filters show), else a visible one, else the first
            var b0 = spec.Build as JsonObject;
            bool noBuild = spec.Build is JsonValue bv && bv.GetValueKind() == System.Text.Json.JsonValueKind.False;
            var cands = new List<(string Cat, JsonObject A)>();
            if (actionsAll[stribe]?["Build"] is JsonObject A)
                foreach (var (cat, cn) in A)
                    if (cn is JsonObject co)
                        foreach (var (_, an) in co)
                            if (an is JsonObject a && a["results"] is JsonObject rs && rs.Any(r => r.Value?["_value"] is JsonValue rv && rv.Str().EndsWith("/" + spec.From)))
                                cands.Add((cat, a));
            static IEnumerable<JsonObject> Locs(JsonObject a) => a["locations"] is JsonObject l ? l.Select(kv => kv.Value).OfType<JsonObject>() : Enumerable.Empty<JsonObject>();
            string? at = b0?["at"] is JsonValue atv ? atv.Str() : null;
            (string Cat, JsonObject A)? act = null;
            if (at != null) foreach (var c in cands) if (Locs(c.A).Any(l => l["_value"].Str().EndsWith("/" + at))) { act = c; break; }
            if (act == null) foreach (var c in cands) if (c.A["visibility"].Str() is not ("0" or "-1")) { act = c; break; }
            if (act == null && cands.Count > 0) act = cands[0];
            if (act != null && !noBuild && !(locked?.Contains(name) ?? false))
            {
                var (cat, a0) = act.Value;
                var a = (JsonObject)a0.DeepClone();
                foreach (var (_, r) in (JsonObject)a["results"]!) if (r is JsonObject ro && ro["_value"] != null) ro["_value"] = $"/Objects/{tribe}/{stype}/{name}";
                a["visibility"] = "1";                         // the campaign decides what can be built (mission unlocks)
                if (at != null)
                {
                    var keep = Locs(a).FirstOrDefault(l => l["_value"].Str().EndsWith("/" + at));
                    JsonObject k2;
                    if (keep != null) k2 = (JsonObject)keep.DeepClone();
                    else { k2 = Locs(a).FirstOrDefault() is { } f ? (JsonObject)f.DeepClone() : new JsonObject(); k2["_value"] = $"/Objects/SEAS/BLDG/{at}"; }
                    a["locations"] = new JsonObject { ["0"] = k2 };
                }
                var cond = Obj(a, "conditions");
                if (b0?["cost"] is JsonObject cost)
                    cond["rescosts"] = new JsonObject
                    {
                        ["units"] = "0", ["food"] = Num(cost["food"]), ["wood"] = Num(cost["wood"]), ["stone"] = Num(cost["stone"]), ["iron"] = "0", ["power"] = Num(cost["power"]),
                    };
                if (b0?["age"] is JsonValue age) cond["inventobjects"] = age.Num() > 1 ? new JsonObject { ["0"] = "age_" + age.Num().ToString(CultureInfo.InvariantCulture) } : new JsonObject();
                if (b0?["time"] is JsonValue tm) a["duration"] = tm.Num().ToString(CultureInfo.InvariantCulture);
                foreach (var l in Locs(a))
                {
                    if (b0?["menupos"] is JsonValue mp && l["uiposition"] is JsonObject up) up["menupos"] = mp.Num().ToString(CultureInfo.InvariantCulture);
                    l["iconpath"] = b0?["icon"] is JsonValue ic ? ic.Str() : name;       // its own card (the toolkit's sequel step), else the HUD falls back to the original's
                }
                Obj(Obj(Obj(actionsAll, tribe), "Build"), cat)[name] = a;
            }
            // the original's moves (the heroes' special moves from level 3), unless it has no weapons
            if (!none)
            {
                var from = $"/Objects/{stribe}/{stype}/{spec.From}";
                foreach (var (_, tn) in actionsAll)
                {
                    if (tn?["Moves"] is not JsonObject mv) continue;
                    foreach (var (_, catn) in mv)
                    {
                        if (catn is not JsonObject co) continue;
                        foreach (var (_, m) in co)
                        {
                            if (m?["locations"] is not JsonObject Lc) continue;
                            var l = Lc.Select(kv => kv.Value).OfType<JsonObject>().FirstOrDefault(x => x["_value"].Str() == from);
                            if (l == null) continue;
                            var nl = (JsonObject)l.DeepClone();
                            nl["_value"] = $"/Objects/{tribe}/{stype}/{name}";
                            Lc[Lc.Count.ToString(CultureInfo.InvariantCulture)] = nl;
                        }
                    }
                }
            }
            // what the scripts and the interface know of the original: script class, idle animations, the name
            var lc = spec.From.ToLowerInvariant();
            if (script[lc] != null) script[name] = script[lc]!.DeepClone();
            sameAs[name] = spec.From;                                                    // Rules.Kind: the original's scripted rules
            if (!none && composites[lc] != null && !NoComposite.IsMatch(name)) composites[name] = composites[lc]!.DeepClone();
            if (idle?[lc] != null) idle[name] = idle[lc]!.DeepClone();
            // the texts: the sequel's own (texts.json), else the original's (the description key stays the original's: a class
            // without a card of its own shows the original's)
            var dk = snode["description"].Str().TrimStart('_').ToLowerInvariant();
            var t0 = (texts[lc] ?? texts[spec.From] ?? texts[dk]) as JsonObject ?? new JsonObject { ["vs"] = new JsonObject(), ["medium"] = "", ["long"] = "" };
            var t = (JsonObject)t0.DeepClone();
            if (Texts.TryGetValue(name, out var t1))
                foreach (var (k, v) in t1)
                {
                    if (k is "intro" or "where") continue;
                    if (k == "vs" && v is JsonObject vs) { var tv = t["vs"] as JsonObject ?? new JsonObject(); foreach (var (vk, vv) in vs) tv[vk] = vv?.DeepClone(); t["vs"] = tv; }
                    else t[k] = v?.DeepClone();
                }
            t["name"] = spec.Desc != "" ? spec.Desc : t0["name"]?.Str() ?? name;
            texts[name] = t;
            made++;
        }
        // the tranquilizer darts' debuff (SequelRuntime.DamageHook): an icon of the info window, seen by everybody
        var buffs = extra["buffs"] as JsonArray ?? new JsonArray();
        var nb = new JsonArray();
        foreach (var b in buffs) if (b?["attrib"].Str() != "sq_tranquilized") nb.Add(b!.DeepClone());
        nb.Add(new JsonObject { ["attrib"] = "sq_tranquilized", ["tile"] = -1, ["vis"] = 1, ["icon"] = "bufficon_poisoned", ["tip"] = "_iw_buff_sq_tranquilized" });
        extra["buffs"] = nb;
        texts["iw_buff_sq_tranquilized"] = new JsonObject
        {
            ["name"] = "Tranquilized", ["cls"] = "", ["vs"] = new JsonObject(), ["long"] = "",
            ["medium"] = "A tranquilizer dart is working: a blow that would kill this creature puts it to sleep at 1 hit point instead. It wears off 30 seconds after the last dart.",
        };
        L.Info($"sequel: {made} classes written into the tech tree ({locked?.Count ?? 0} locked)");
    }

    static string Num(JsonNode? n) => (n?.Num(0) ?? 0).ToString(CultureInfo.InvariantCulture);
    static readonly Regex NoComposite = new("^sq_(stabilizer|searchlight|radar|field_hq|electric_net|flak|power_pole|generator|windmill|watermill|tesla_coil|power_station)$");
}
