// The map side of the co-op campaign mode (docs/COOP.md "Maps"): every mission's co-op definition (game/coop) loads
// and is valid, and for the 2-seat and the 3-seat game of every mission (both configurations): each co-op seat starts
// on walkable ground of the (extended) map, with a way by land (or by sea where the definition says the mission is
// naval) to seat 1's start or the definition's `reach` point, no enemy base next to it, and the resources a base needs
// near it; the extension's heights are continuous at the seam; the extended map's world, nav grid, fog and bounds
// agree; the generation is deterministic; single player maps are untouched.
//   dotnet run --project tests/PwRemake.Tests -- CoopMaps
// (PW_COOP_MISSIONS=5,6 limits the session tests to those missions; PW_COOP_VERBOSE=1 prints every seat's numbers.)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Coop;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class CoopMapsTests
{
    /// <summary>the missions whose co-op campaign (mode 2) is offered, per configuration: the main campaign 0-16 (both
    /// configurations; redesigned 2026-11-03). BoosterPack 1 (41-43) and MIRAGE's SEAS missions (20-23) keep their
    /// definitions with `campaign = false` (not reworked yet: Shared mode only) and are left out of these tests.</summary>
    public static readonly int[] BaseMissions = Enumerable.Range(0, 17).ToArray();
    public static readonly int[] MirageMissions = BaseMissions;
    /// <summary>the definitions kept for later (not reworked): 41-43 in both configurations, 20-23 in MIRAGE's</summary>
    public static readonly (string Cfg, int Id)[] NotReworked = { ("base", 41), ("base", 42), ("base", 43), ("mirage", 41), ("mirage", 42), ("mirage", 43), ("mirage", 20), ("mirage", 21), ("mirage", 22), ("mirage", 23) };
    static IEnumerable<(string Cfg, int Id)> All() =>
        BaseMissions.Select(i => ("base", i)).Concat(MirageMissions.Select(i => ("mirage", i)))
            .Where(m => Environment.GetEnvironmentVariable("PW_COOP_MISSIONS") is not { Length: > 0 } f || f.Split(',').Contains(m.Item2.ToString()))
            .Where(m => Environment.GetEnvironmentVariable("PW_COOP_CFG") is not { Length: > 0 } g || g.Split(',').Contains(m.Item1));
    static bool Verbose => Environment.GetEnvironmentVariable("PW_COOP_VERBOSE") == "1";

    /// <summary>game/coop of the repository (found from the test's folder upwards)</summary>
    public static string CoopRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "game", "coop");
            if (Directory.Exists(p)) return p;
        }
        throw new DirectoryNotFoundException("game/coop not found above " + AppContext.BaseDirectory);
    }

    /// <summary>the mission's definition as the game reads it (CoopDefinition.Load: common/NN.lua under &lt;config&gt;/NN.lua)</summary>
    static CoopDefinition Def(TestContext t, string cfg, int id)
    {
        var problems = new List<string>();
        var d = CoopDefinition.Load(cfg, id, CoopRoot(), problems);
        t.True(d != null, $"{cfg} {id}: a co-op definition (game/coop/common or {cfg}/{id:00}.lua)");
        t.True(problems.Count == 0, $"{cfg} {id}: the definition loads without problems ({string.Join("; ", problems)})");
        return d!;
    }
    static CoopMapPlan Plan(CoopDefinition d, int seats) => CoopMapPlan.FromJson(d.Raw, seats);
    static bool IsList(JsonNode? n) => n is JsonArray || n is JsonObject o && o.Count == 0;
    static IEnumerable<JsonNode?> ListOf(JsonNode? n) => n is JsonArray a ? a : Enumerable.Empty<JsonNode?>();
    static bool IsNum(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out _);
    static bool IsStr(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out _);

    // ------------------------------------------------------------------ the definitions
    static readonly HashSet<string> TopKeys = new() { "version", "nobuild", "heroes", "extend", "resources", "seats", "allies", "scaling", "triggers", "notes", "reach", "naval", "enemies",
        "phase", "map_objects", "gates", "reroute", "start_split", "deal_start", "mortal", "campaign", "reason" };
    static readonly HashSet<string> SeatKeys = new() { "start", "army", "ally", "base", "start_resources", "extend", "resources", "notes", "hero_army", "reach" };
    /// <summary>missions where the seats defend a front the enemies attack (no "enemies close" check)</summary>
    static readonly HashSet<int> DefendMissions = new() { 13 };
    static readonly HashSet<string> ExtendKeys = new() { "north", "south", "east", "west", "seed", "style", "water", "bridges", "land", "stamp", "grow", "keep" };
    static readonly HashSet<string> CircleKeys = new() { "x", "z", "r", "stamp" };
    static readonly HashSet<string> EnemyKeys = new() { "name", "seats", "owner", "tribe", "like", "at", "preset", "buildings", "army", "brain", "resources", "notes" };
    static readonly HashSet<string> BrainKeys = new() { "behaviour", "attack", "first_attack", "interval", "wave", "target" };
    static readonly HashSet<string> BridgeKeys = new() { "points", "width", "cut", "shape" };
    static readonly HashSet<string> StampKeys = new() { "x", "z", "r", "rot", "mirror" };
    static readonly HashSet<string> Kinds = new() { "wood", "stone", "food", "fish", "kit" };

    /// <summary>every definition loads (the core's loader) and is valid: schema v1's fields with the right types, the
    /// map part's keys, heroes and allies that exist in the mission, seats 2 and 3 with a start or an ally, a plan for
    /// 2 and for 3 seats</summary>
    public static void DefinitionsValid(TestContext t)
    {
        foreach (var (cfg, id) in All())
        {
            var def = Def(t, cfg, id);
            var d = def.Raw;
            string w = $"{cfg} {id:00}";
            foreach (var (k, _) in d) t.True(TopKeys.Contains(k), $"{w}: known top-level key {k}");
            t.Equal(1, def.Version, $"{w}: version");
            t.True(d["nobuild"] is JsonValue nb && nb.TryGetValue<bool>(out _), $"{w}: nobuild is true / false");
            t.True(def.Notes.Length > 20, $"{w}: notes explain the choices");
            t.True(d["heroes"] is JsonArray ha && ha.All(IsStr) && def.Heroes.Count > 0, $"{w}: heroes is a list of classes");
            var classes = MissionClasses(cfg, id);
            foreach (var h in def.Heroes) t.True(classes.Contains(h.ToLowerInvariant()), $"{w}: hero {h} is a class of the mission");
            if (d["extend"] != null) CheckExtend(t, w, d["extend"]);
            CheckResources(t, w, d["resources"]);
            var mission = MissionJson(cfg, id);
            t.True(def.Supports(3) && def.Supports(2), $"{w}: seats 2 and 3");
            for (int n = 2; n <= 3; n++)
            {
                var s = d["seats"]![n.ToString()] as JsonObject;
                t.True(s != null, $"{w}: seat {n}");
                foreach (var (k, _) in s!) t.True(SeatKeys.Contains(k), $"{w}: seat {n}: known key {k}");
                var army = s["army"];
                t.True(army is JsonValue av && av.GetValue<string>() is "default" or "none" || army is JsonArray aa && aa.All(IsStr), $"{w}: seat {n}: army is \"default\", \"none\" or a list of classes");
                if (def.NoBuild) t.True(army is JsonValue nv && nv.GetValue<string>() == "none", $"{w}: seat {n}: a no-build mission's seat brings no army (\"none\")");
                var sd = def.Seats[n];
                if (sd.Ally is int ally)
                {
                    t.True(def.UsableAllies.Contains(ally), $"{w}: seat {n} takes ally {ally}, which allies.usable lists");
                    t.True(mission?["players"]?[ally]?["present"]?.GetValue<bool>() == true, $"{w}: seat {n}'s ally {ally} is a player of the mission");
                }
                else t.True(s["start"] is JsonObject st && IsNum(st["x"]) && IsNum(st["z"]), $"{w}: seat {n} has a start (or an ally)");
                if (s["extend"] != null) CheckExtend(t, $"{w} seat {n}", s["extend"]);
                CheckResources(t, $"{w} seat {n}", s["resources"]);
            }
            CheckEnemies(t, w, cfg, id, def, mission);
            if (d["scaling"] is JsonObject sc)
            {
                foreach (var k in new[] { "count", "health", "damage" }) if (sc[k] != null) t.True(IsNum(sc[k]) && sc[k]!.GetValue<double>() >= 0, $"{w}: scaling.{k}");
                foreach (var p in def.Scaling.Exclude) t.True(p >= 1 && p <= 7 && mission?["players"]?[p]?["present"]?.GetValue<bool>() == true, $"{w}: scaling.exclude {p} is a player of the mission");
            }
            // a plan for each seat count: the co-op seats' sites (no site for an ally's seat)
            foreach (int n in new[] { 2, 3 })
            {
                var p = Plan(def, n);
                t.Equal(Enumerable.Range(2, n - 1).Count(k => def.Seats[k].X != null), p.Sites.Count(q => !q.Enemy), $"{w}: {n} seats: the co-op seats' sites");
                t.Equal(def.Enemies.Count(q => q.Seats <= n), p.Sites.Count(q => q.Enemy), $"{w}: {n} seats: the enemy bases' sites");
            }
        }
    }

    static void CheckExtend(TestContext t, string w, JsonNode? e)
    {
        t.True(e is JsonObject, $"{w}: extend is a table");
        foreach (var (k, _) in (JsonObject)e!) t.True(ExtendKeys.Contains(k), $"{w}: extend: known key {k}");
        foreach (var k in new[] { "north", "south", "east", "west" })
            if (e[k] != null) t.True(IsNum(e[k]) && e[k]!.GetValue<double>() is >= 0 and <= 512, $"{w}: extend.{k} 0 .. 512");
        t.True(e["style"] == null || e["style"]!.GetValue<string>() is "grow" or "continue" or "island", $"{w}: extend.style is grow, continue or island");
        foreach (var k in new[] { "grow", "keep" })
            foreach (var c in ListOf(e[k]))
            {
                t.True(c is JsonObject co && IsNum(co["x"]) && IsNum(co["z"]) && (co["r"] == null || IsNum(co["r"]) && co["r"]!.GetValue<double>() is > 0 and <= 400), $"{w}: extend.{k}: {{ x, z, r }} with r 0 .. 400");
                foreach (var (ck, _) in (JsonObject)c!) t.True(CircleKeys.Contains(ck) && (k == "grow" || ck != "stamp"), $"{w}: extend.{k}: known key {ck}");
            }
        var x = CoopMapPlan.ExtendOf(e)!;
        foreach (var b in x.Bridges) t.True(b.Points.Count >= 2 && b.Width >= 8, $"{w}: bridge has two points and a width of 8 m or more");
        foreach (var b in ListOf(e["bridges"]))
        {
            foreach (var (k, _) in (JsonObject)b!) t.True(BridgeKeys.Contains(k), $"{w}: bridge: known key {k}");
            t.True(b["shape"] == null || b["shape"]!.GetValue<string>() is "isthmus" or "ford" or "path", $"{w}: bridge shape is \"isthmus\", \"ford\" or \"path\"");
        }
        if (e["stamp"] is JsonObject st)
        {
            foreach (var (k, v) in st) t.True(StampKeys.Contains(k) && (k == "mirror" ? v is JsonValue mv && mv.TryGetValue<bool>(out _) : IsNum(v)), $"{w}: stamp: known key {k} with a {(k == "mirror" ? "true / false" : "number")}");
            t.True(st["x"] != null == (st["z"] != null), $"{w}: stamp: x and z together");
        }
        else t.True(e["stamp"] == null || e["stamp"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && !sb, $"{w}: stamp is a table or false");
    }
    /// <summary>`enemies`: known keys, an owner that is "new" or a computer player of the mission that is hostile to the
    /// human player (now or later: not an ally of the seats), a tribe the configuration plays (or `like`'s), `like` a
    /// computer player of the mission, a place, a preset or buildings or units whose classes exist in the tribe, seats
    /// 2 or 3, notes saying how the objectives treat it</summary>
    static void CheckEnemies(TestContext t, string w, string cfg, int id, CoopDefinition def, JsonNode? mission)
    {
        var raw = def.Raw["enemies"];
        if (raw == null) return;
        t.True(IsList(raw), $"{w}: enemies is a list");
        var c = CampaignTests.LoadCfgPublic(cfg)!;
        JsonNode? Pl(int pid) => mission?["players"]?.AsArray().FirstOrDefault(p => p?["id"]?.GetValue<int>() == pid && p?["present"]?.GetValue<bool>() == true);
        int human = mission?["players"]?.AsArray().FirstOrDefault(p => p?["type"]?.GetValue<string>() == "human")?["id"]?.GetValue<int>() ?? 0;
        // (the SEAS are an enemy tribe of the base game's later missions too: their classes are checked below)
        var tribes = new[] { "Hu", "Aje", "Ninigi", "SEAS" };
        int k = 0;
        foreach (var en in ListOf(raw))
        {
            k++;
            var e = en as JsonObject; var ed = def.Enemies.FirstOrDefault(q => q.Index == k);
            string we = $"{w}: enemies[{k}]";
            t.True(e != null && ed != null, $"{we} is a table");
            foreach (var (key, _) in e!) t.True(EnemyKeys.Contains(key), $"{we}: known key {key}");
            if (e["brain"] is JsonObject br) foreach (var (key, _) in br) t.True(BrainKeys.Contains(key), $"{we}: brain: known key {key}");
            t.True(ed!.Notes.Length > 20, $"{we}: notes say how the objectives treat it");
            t.True(ed.Seats is 2 or 3, $"{we}: seats 2 or 3");
            t.True(e["at"] is JsonObject at && IsNum(at["x"]) && IsNum(at["z"]), $"{we}: at = {{ x, z, r }}");
            int? pid = ed.Owner ?? ed.Like;
            if (ed.Owner is int o)
            {
                t.True(o != human && Pl(o) is { } op && op["type"]?.GetValue<string>() == "ai" && !def.UsableAllies.Contains(o), $"{we}: owner {o} is a computer player of the mission");
            }
            if (ed.Like is int l) t.True(l != human && Pl(l) is { } lp && lp["type"]?.GetValue<string>() == "ai", $"{we}: like {l} is a computer player of the mission");
            string tribe = ed.Tribe.Length > 0 ? ed.Tribe : pid is int p2 ? Pl(p2)?["tribe"]?.GetValue<string>() ?? "" : "";
            t.True(tribes.Contains(tribe), $"{we}: tribe {tribe} is Hu, Aje, Ninigi or SEAS");
            if (ed.New) t.True(ed.Like != null, $"{we}: a new enemy names the mission's player it stands as (like)");
            var classes = new List<string>();
            if (ed.Preset != null) { classes.AddRange(CoopEnemyDef.PresetClasses(ed.Preset, tribe)); classes.AddRange(CoopEnemyDef.PresetArmy(ed.Preset, tribe)); t.True(classes.Count > 0, $"{we}: preset {ed.Preset} for {tribe}"); }
            classes.AddRange(ed.Buildings.Select(b => b.Cls)); classes.AddRange(ed.Army.Select(a => a.Cls));
            foreach (var cl in classes) t.True(c.Rules.Exists(cl), $"{we}: class {cl} exists ({cfg})");
            if (ed.Brain && ed.New && ed.Preset == "outpost") t.True(false, $"{we}: an outpost has no main building - brain = false");
            if (ed.Target is int ts) t.True(ts is 2 or 3 && ts <= ed.Seats, $"{we}: brain.target is a seat that exists when it appears");
        }
    }

    static void CheckResources(TestContext t, string w, JsonNode? r)
    {
        if (r == null) return;
        t.True(IsList(r), $"{w}: resources is a list");
        foreach (var q in CoopMapPlan.ResourcesOf(r))
            t.True(q.Kind != null ? Kinds.Contains(q.Kind) : !string.IsNullOrEmpty(q.Class), $"{w}: resource {q} has a known kind or a class");
    }

    static System.Text.Json.Nodes.JsonNode? MissionJson(string cfg, int id)
    {
        var c = CampaignTests.LoadCfgPublic(cfg);
        if (c == null) return null;
        var e = MissionEntry.LoadIndex(c.Config.MissionsDir).FirstOrDefault(x => x.Id == id);
        if (e == null) return null;
        return PwRemake.Sim.Maps.PwMapReader.MissionOf(Path.Combine(c.Config.MissionsDir, e.Folder));
    }
    static HashSet<string> MissionClasses(string cfg, int id)
    {
        var c = CampaignTests.LoadCfgPublic(cfg)!;
        var e = MissionEntry.LoadIndex(c.Config.MissionsDir).First(x => x.Id == id);
        var set = new HashSet<string>();
        var m = PwRemake.Sim.Maps.PwMapReader.MissionOf(Path.Combine(c.Config.MissionsDir, e.Folder))!;
        foreach (var p in m["players"]!.AsArray())
            foreach (var a in p?["start_army"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray()) set.Add(((string?)a!["class"] ?? "").ToLowerInvariant());
        var wj = PwRemake.Sim.Maps.PwMapReader.WorldOf(Path.Combine(c.Config.MissionsDir, e.Folder))!;
        foreach (var o in wj["objects"]!.AsArray()) set.Add(((string?)o!["class"] ?? "").ToLowerInvariant());
        return set;
    }

    // ------------------------------------------------------------------ the extension itself
    static MapData OriginalMap(TestContext t, string cfg, int id)
    {
        var c = CampaignTests.LoadCfgPublic(cfg);
        if (c == null) t.Skip($"configuration {cfg} not found");
        var e = MissionEntry.LoadIndex(c!.Config.MissionsDir).First(x => x.Id == id);
        var f = WorldBuilder.FindMap(c.Config, e.Map);
        if (f == null) t.Skip($"map {e.Map} not staged");
        return MapFiles.Read(f!);
    }

    static ulong Hash(MapData md)
    {
        ulong h = 1469598103934665603UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        foreach (var v in md.Heights) Mix(BitConverter.SingleToUInt32Bits(v));
        foreach (var v in md.Mats) Mix(v);
        if (md.Walk != null) foreach (var v in md.Walk.Cells) Mix(v);
        foreach (var b in md.Forest.Blocks) { Mix((ulong)(b.X + 100000)); Mix((ulong)(b.Y + 100000)); foreach (var v in b.B) Mix(v); }
        foreach (var o in md.Objects) { Mix((ulong)BitConverter.DoubleToInt64Bits(o.X)); Mix((ulong)BitConverter.DoubleToInt64Bits(o.Y)); foreach (var ch in o.Cls) Mix(ch); }
        foreach (var p in md.Plants) { Mix((ulong)BitConverter.DoubleToInt64Bits(p.X)); Mix((ulong)BitConverter.DoubleToInt64Bits(p.Y)); }
        return h;
    }

    /// <summary>the extension's heights meet the old map's at the seam (no step), the grids have the new size, the old
    /// map's inside is unchanged (except where a bridge was graded), and the same plan gives the same map</summary>
    public static void ExtensionSeamAndDeterminism(TestContext t)
    {
        foreach (var (cfg, id) in All())
            foreach (int seats in new[] { 2, 3 })
            {
                var d = Def(t, cfg, id);
                var plan = Plan(d, seats);
                var md = OriginalMap(t, cfg, id);
                ulong h0 = Hash(md);
                var a = MapExtension.Apply(md, plan);
                var b = MapExtension.Apply(MapFiles.Read(FindMapFile(cfg, id)), plan);
                string w = $"{cfg} {id:00} ({seats} seats)";
                t.Equal(h0, Hash(md), $"{w}: the original map data is not changed");
                t.Equal(Hash(a), Hash(b), $"{w}: deterministic");
                var e = plan.Extend;
                if (e == null || !e.Any) { t.True(ReferenceEquals(a, md) || a.Hx == md.Hx, $"{w}: no extension, same size"); continue; }
                t.Equal(md.Hx + (e.West + e.East) / 2, a.Hx, $"{w}: width"); t.Equal(md.Hy + (e.North + e.South) / 2, a.Hy, $"{w}: height");
                t.Equal(md.OffX - e.West, a.OffX, $"{w}: grid corner x"); t.Equal(md.OffY - e.North, a.OffY, $"{w}: grid corner y");
                t.True(a.Mats.Length == a.Mx * a.My && a.Mx * 4 == a.Hx * 2 && a.My * 4 == a.Hy * 2, $"{w}: material grid fits");
                if (a.Walk != null) t.True(a.Walk.Cells.Length == a.Walk.W * a.Walk.H && a.Walk.W == md.Walk!.W + (e.West + e.East) / 2, $"{w}: walk grid fits");
                // the old map's inside (38 m in from its edges, away from the land bridges): its land is as it was (heights;
                // ground materials and walk grid away from its coasts); its sea may have become new land (a base site's
                // patch, its shelf) - but not within 20 m of the old land (no land connection but the bridges)
                var spines = a.Extension!.Bridges;
                int changed = 0, inside = 0, near = 0;
                string first = "";
                bool OldLand(double x, double y) => md.HeightAtMap(x, y) >= md.Water - 0.5;
                // (inside the frame the extension makes again at an extended edge: MapExtension.INSET samples, + a material cell)
                int fr = MapExtension.INSET + 3;
                for (int j = fr; j < md.Hy - fr; j += 3)
                    for (int i = fr; i < md.Hx - fr; i += 3)
                    {
                        double mx = md.OffX + 2.0 * i, my = md.OffY + 2.0 * j;
                        if (NearSpine(spines, mx, my, 40)) continue;
                        inside++;
                        if (OldLand(mx, my))
                        {
                            bool coast = false;
                            for (int k = 0; k < 8 && !coast; k++) if (!OldLand(mx + 10 * Math.Cos(k * Math.PI / 4), my + 10 * Math.Sin(k * Math.PI / 4))) coast = true;
                            if (Math.Abs(a.HeightAtMap(mx, my) - md.HeightAtMap(mx, my)) > 1e-4 || !coast && (a.MaterialAtMap(mx, my) != md.MaterialAtMap(mx, my) ||
                                (md.Walk != null && a.WalkableMap(mx + 1, my + 1) != md.WalkableMap(mx + 1, my + 1))))
                            { changed++; if (first == "") first = $"({mx:0}, {my:0})"; }
                        }
                        else if (a.HeightAtMap(mx, my) >= md.Water - 0.5)
                        {
                            for (int k = 0; k < 16; k++)
                                for (double r = 4; r <= 20; r += 4)
                                    if (OldLand(mx + r * Math.Cos(k * Math.PI / 8), my + r * Math.Sin(k * Math.PI / 8))) { near++; k = 99; break; }
                        }
                    }
                if (!e.Grows)
                {
                    t.Equal(0, changed, $"{w}: the old map's land unchanged ({inside} samples; first changed at {first})");
                    t.Equal(0, near, $"{w}: new land in the old map's sea keeps 20 m from its land");
                }
                else GrowKeepsOldMap(t, w, md, a, e, spines);
                // the seam: steps between neighbouring samples across each extended edge no bigger than inside the old map
                // next to it
                double X0 = md.OffX + MapExtension.INSET * 2, X1 = md.OffX + (md.Hx - 1 - MapExtension.INSET) * 2, Y0 = md.OffY + MapExtension.INSET * 2, Y1 = md.OffY + (md.Hy - 1 - MapExtension.INSET) * 2;
                double worst = 0, oldWorst = 0, wx = 0, wy = 0;
                void Seam(bool vertical, double fixedC, double from, double to)
                {
                    for (double u = from; u <= to; u += 2)
                    {
                        double x = vertical ? fixedC : u, y = vertical ? u : fixedC;
                        for (int k = -6; k <= 6; k++)
                        {
                            double x0 = vertical ? x + 2 * k : x, y0 = vertical ? y : y + 2 * k, x1 = vertical ? x0 + 2 : x0, y1 = vertical ? y0 : y0 + 2;
                            if (NearSpine(spines, x0, y0, 20)) continue;
                            // (the pair across the seam: one sample kept, one made - the old map's own border has steps of its own)
                            bool Kept(double xx, double yy) => xx >= X0 && xx <= X1 && yy >= Y0 && yy <= Y1;
                            if (Kept(x0, y0) == Kept(x1, y1)) continue;
                            // (a step bigger than the ground's own slope beside it: the new land's patches have steep hills)
                            double dxs = x1 - x0, dys = y1 - y0;
                            double st = Math.Abs(a.HeightAtMap(x1, y1) - a.HeightAtMap(x0, y0));
                            double side = 0;
                            for (int q = 1; q <= 4; q++)
                                side = Math.Max(side, Math.Max(Math.Abs(a.HeightAtMap(x0 - (q - 1) * dxs, y0 - (q - 1) * dys) - a.HeightAtMap(x0 - q * dxs, y0 - q * dys)),
                                    Math.Abs(a.HeightAtMap(x1 + q * dxs, y1 + q * dys) - a.HeightAtMap(x1 + (q - 1) * dxs, y1 + (q - 1) * dys))));
                            // (or the old map's own step where a base site's patch of it lies)
                            foreach (var sp in a.Extension!.Stamps)
                            {
                                if ((x0 - sp.SiteX) * (x0 - sp.SiteX) + (y0 - sp.SiteZ) * (y0 - sp.SiteZ) > sp.R * sp.R) continue;
                                var (s0x, s0y) = sp.ToSource(x0, y0); var (s1x, s1y) = sp.ToSource(x1, y1);
                                side = Math.Max(side, Math.Abs(md.HeightAtMap(s1x, s1y) - md.HeightAtMap(s0x, s0y)));
                            }
                            st = Math.Max(0, st - Math.Max(0, side - 0.5));
                            if (st > worst) { worst = st; wx = x0; wy = y0; }
                        }
                        double ox0 = vertical ? fixedC + (fixedC <= md.OffX ? 40 : -40) : x, oy0 = vertical ? y : fixedC + (fixedC <= md.OffY ? 40 : -40);
                        for (int k = 0; k < 6; k++)
                        {
                            double x0 = vertical ? ox0 + 2 * k : ox0, y0 = vertical ? oy0 : oy0 + 2 * k;
                            oldWorst = Math.Max(oldWorst, Math.Abs(md.HeightAtMap(vertical ? x0 + 2 : x0, vertical ? y0 : y0 + 2) - md.HeightAtMap(x0, y0)));
                        }
                    }
                }
                // (along an extended edge, not into the old map's own border at an edge that stays: its rows have a step)
                double ya = Y0 + (e.North > 0 ? 0 : 24), yb = Y1 - (e.South > 0 ? 0 : 24), xa = X0 + (e.West > 0 ? 0 : 24), xb = X1 - (e.East > 0 ? 0 : 24);
                if (e.West > 0) Seam(true, X0, ya, yb);
                if (e.East > 0) Seam(true, X1, ya, yb);
                if (e.North > 0) Seam(false, Y0, xa, xb);
                if (e.South > 0) Seam(false, Y1, xa, xb);
                if (Verbose) t.Log($"{w}: seam step {worst:0.00} m, old map near the edge {oldWorst:0.00} m");
                t.True(worst <= Math.Max(2.5, oldWorst + 0.5), $"{w}: the seam has no step: {worst:0.00} m beyond the slope beside it between 2 m samples at map ({wx:0}, {wy:0}) (the old map next to it: {oldWorst:0.00} m)");
            }
        // another seed, another landscape
        {
            var md = OriginalMap(t, "base", 1);
            var e1 = new CoopExtend { South = 128, Seed = 1 }; var e2 = new CoopExtend { South = 128, Seed = 2 };
            t.True(Hash(MapExtension.Apply(md, e1)) != Hash(MapExtension.Apply(md, e2)), "another seed gives another landscape");
            t.True(ReferenceEquals(MapExtension.Apply(md, (CoopMapPlan?)null), md), "no plan: the original map itself");
        }
    }
    /// <summary>style "grow": the old map's land above the shore (MapExtension.SHORE over the water) keeps its heights
    /// (away from the paths and bridges); around every protected object of the mission (MapExtension.ProtectRadius:
    /// buildings, walls, gates, placed pieces 40 m, units, start locations, nests 20 m) and in the `keep` zones the old
    /// map keeps its heights, ground and walk grid; no new land in a keep zone</summary>
    static void GrowKeepsOldMap(TestContext t, string w, MapData md, MapData a, CoopExtend e, List<CoopBridge> spines)
    {
        double water = md.Water;
        int high = 0, prot = 0, keep = 0, n = 0; string first = "";
        var objs = md.Objects.Select(o => (o.X, o.Y, R: MapExtension.ProtectRadius(o), O: o)).Where(q => q.R > 0).ToList();
        int fr = MapExtension.INSET + 3;
        for (int j = fr; j < md.Hy - fr; j += 2)
            for (int i = fr; i < md.Hx - fr; i += 2)
            {
                double mx = md.OffX + 2.0 * i, my = md.OffY + 2.0 * j;
                if (NearSpine(spines, mx, my, 8)) continue;
                n++;
                double h0 = md.HeightAtMap(mx, my), h1 = a.HeightAtMap(mx, my);
                bool same = Math.Abs(h1 - h0) < 1e-4;
                if (h0 >= water + MapExtension.SHORE && !same) { high++; if (first == "") first = $"high ground at ({mx:0}, {my:0})"; }
                bool inKeep = e.Keep.Any(z => (mx - z.X) * (mx - z.X) + (my - z.Z) * (my - z.Z) < z.R * z.R);
                // (the growth's protection is drawn on a 4 m grid: 6 m of tolerance)
                var near = objs.FirstOrDefault(q => (mx - q.X) * (mx - q.X) + (my - q.Y) * (my - q.Y) < (q.R - 6) * (q.R - 6));
                bool protectedHere = near.O != null;
                // (near an object the sea floor may rise a little under the water - the new coast's shelf -; a keep zone's
                // water keeps its depth)
                bool landChanged = !same && (inKeep || h0 >= water - 0.5 || h1 >= water - 0.5);
                if ((inKeep || protectedHere) && (landChanged || md.Walk != null && a.WalkableMap(mx + 1, my + 1) != md.WalkableMap(mx + 1, my + 1)))
                {
                    if (inKeep) keep++; else prot++;
                    if (first == "") first = $"{(inKeep ? "keep zone" : $"protected ({near.O!.Type} {near.O.Cls} {near.O.Name} at {near.X:0}, {near.Y:0})")} at ({mx:0}, {my:0})";
                }
            }
        t.Equal(0, high, $"{w}: the old map's land above the shore keeps its heights ({n} samples; first changed: {first})");
        t.Equal(0, prot, $"{w}: nothing changes near the mission's objects (first: {first})");
        t.Equal(0, keep, $"{w}: the keep zones stay as they were (first: {first})");
        // no new land in a keep zone (outside the old map too)
        foreach (var z in e.Keep)
            for (int k = 0; k < 16; k++)
                for (double r = 0; r < z.R; r += 8)
                {
                    double x = z.X + r * Math.Cos(k * Math.PI / 8), y = z.Z + r * Math.Sin(k * Math.PI / 8);
                    if (md.HeightAtMap(x, y) < water - 0.3 && a.HeightAtMap(x, y) >= water - 0.3 && !NearSpine(spines, x, y, 8))
                    { t.True(false, $"{w}: new land in the keep zone at ({x:0}, {y:0})"); return; }
                }
    }

    /// <summary>the grown and changed land looks like the original maps' (every mission, 2 and 3 seats): its ground
    /// follows its final slope - a rock material (one the old map keeps for steep ground, MapExtension.RockSlopes) lies
    /// only where the ground is steep (no rock texture on flat ground: what the stamps' rock left on levelled land
    /// was) - and its joins are smooth: where new or changed land meets the old map's (12 m on either side of the old
    /// land it was not allowed to change) and along the paths and land bridges, neighbouring samples differ by no
    /// more than 0.6 unless the old map's own ground there is nearly as steep (a path cut at the foot of an old cliff), and there are no creases
    /// (a flat shelf's abrupt edge: a sharp bend between three samples in a row)</summary>
    public static void GroundFollowsSlope(TestContext t)
    {
        var problems = new List<string>();
        foreach (var (cfg, id) in All())
            foreach (int seats in new[] { 2, 3 })
            {
                var plan = Plan(Def(t, cfg, id), seats);
                if (plan.Extend == null || !plan.Extend.Any) continue;
                var md = OriginalMap(t, cfg, id);
                var a = MapExtension.Apply(md, plan);
                var ext = a.Extension!;
                string w = $"{cfg} {id:00} ({seats} seats)";
                var rock = MapExtension.RockSlopes(md);
                double water = md.Water;
                bool Changed(double x, double y) => !ext.InOld(x, y) || Math.Abs(a.HeightAtMap(x, y) - md.HeightAtMap(x, y)) > 0.05;
                // ---- rock on flat ground: a rock material that covers most of its 3 x 3 cells (as the splat blends it)
                // where none of them is as steep as 0.6 of the slope the old map uses it from
                int rockCells = 0, flatRock = 0; string firstFlat = "";
                for (int j = 1; j < a.My - 1; j++)
                    for (int i = 1; i < a.Mx - 1; i++)
                    {
                        double x = a.OffX + 4.0 * i + 2, y = a.OffY + 4.0 * j + 2;
                        int k = a.Mats[j * a.Mx + i] & 7;
                        if (double.IsInfinity(rock[k]) || a.HeightAtMap(x, y) < water + 1.5 || !Changed(x, y)) continue;
                        // (the old map's own painting where its ground barely moved: as the original has it)
                        if (ext.InOld(x, y) && md.MaterialAtMap(x, y) == k && Math.Abs(a.HeightAtMap(x, y) - md.HeightAtMap(x, y)) < 0.5) continue;
                        int same = 0; double steepest = 0;
                        for (int b = -1; b <= 1; b++) for (int c = -1; c <= 1; c++)
                        {
                            if ((a.Mats[(j + b) * a.Mx + i + c] & 7) == k) same++;
                            steepest = Math.Max(steepest, MapExtension.Slope4(a, x + 4 * c, y + 4 * b));
                        }
                        if (same < 5) continue;
                        rockCells++;
                        if (steepest < rock[k] * 0.6) { flatRock++; if (firstFlat == "") firstFlat = $"({x:0}, {y:0}): slope {steepest:0.00}, material {k} from {rock[k]:0.00}"; }
                    }
                // ---- the joins: the 2 m samples of new or changed dry land within 12 m of unchanged old land, or beside a
                // path's or land bridge's spine (its graded band and falloff)
                int hx = a.Hx, hy = a.Hy;
                var unchanged = new bool[hx * hy]; var chg = new bool[hx * hy];
                for (int j = 0; j < hy; j++)
                    for (int i = 0; i < hx; i++)
                    {
                        double x = a.OffX + 2.0 * i, y = a.OffY + 2.0 * j;
                        bool c = Changed(x, y);
                        chg[j * hx + i] = c;
                        unchanged[j * hx + i] = !c && md.HeightAtMap(x, y) >= water + 0.3;
                    }
                var near = (bool[])unchanged.Clone();
                for (int it = 0; it < 6; it++)
                {
                    var n2 = (bool[])near.Clone();
                    for (int j = 1; j < hy - 1; j++)
                        for (int i = 1; i < hx - 1; i++)
                            if (!near[j * hx + i] && (near[j * hx + i - 1] || near[j * hx + i + 1] || near[(j - 1) * hx + i] || near[(j + 1) * hx + i])) n2[j * hx + i] = true;
                    near = n2;
                }
                int pairs = 0, steps = 0, creases = 0; double worst = 0; string firstStep = "", firstCrease = "";
                for (int j = 1; j < hy - 1; j++)
                    for (int i = 1; i < hx - 1; i++)
                    {
                        int k = j * hx + i;
                        if (!chg[k]) continue;
                        double x = a.OffX + 2.0 * i, y = a.OffY + 2.0 * j;
                        double h = a.Heights[k];
                        if (h < water + 0.3) continue;
                        bool spine = ext.Bridges.Any(b => b.Points.Count >= 2 && Enumerable.Range(0, b.Points.Count - 1).Any(q => SegD(x, y, b.Points[q], b.Points[q + 1]) < b.Width / 2 + 24));
                        if (!near[k] && !spine) continue;
                        foreach (var (di, dj) in new[] { (1, 0), (0, 1) })
                        {
                            int q = k + dj * hx + di, p = k - dj * hx - di;
                            if (a.Heights[q] < water + 0.3 || a.Heights[p] < water + 0.3) continue;
                            pairs++;
                            double s = Math.Abs(a.Heights[q] - h) / 2;
                            // (unless the old map's own ground there is as steep: an old cliff the join runs along)
                            double xq = x + 2 * di, yq = y + 2 * dj;
                            double own = ext.InOld(x, y) && ext.InOld(xq, yq) ? Math.Abs(md.HeightAtMap(xq, yq) - md.HeightAtMap(x, y)) / 2 : 0;
                            if (s > 0.6 && s > own * 1.3 + 0.1) { steps++; if (s > worst) { worst = s; firstStep = $"({x:0}, {y:0}) slope {s:0.00}{(spine ? " (path / bridge)" : "")}"; } }
                            // (a crease: the slope changes by more than 0.5 between neighbouring 2 m steps)
                            double bend = Math.Abs(a.Heights[q] - 2 * h + a.Heights[p]) / 2;
                            double ownBend = ext.InOld(x - 2 * di, y - 2 * dj) && ext.InOld(xq, yq) ? Math.Abs(md.HeightAtMap(xq, yq) - 2 * md.HeightAtMap(x, y) + md.HeightAtMap(x - 2 * di, y - 2 * dj)) / 2 : 0;
                            if (bend > 0.5 && bend > ownBend * 1.3 + 0.1) { creases++; if (firstCrease == "") firstCrease = $"({x:0}, {y:0}) bend {bend:0.00}{(spine ? " (path / bridge)" : "")}"; }
                        }
                    }
                if (Environment.GetEnvironmentVariable("PW_DBG_AT") is { Length: > 0 } dbgAt)
                {
                    var xy = dbgAt.Split(','); double cx = double.Parse(xy[0]), cy = double.Parse(xy[1]);
                    t.Log($"{w} heights - water round ({cx}, {cy}), 8 m apart; log: {string.Join(" | ", ext.Log.Where(l => l.Contains("seat") || l.Contains("joins") || l.Contains("stamp")))}");
                    for (int b = -6; b <= 6; b++)
                        t.Log(string.Join(" ", Enumerable.Range(-6, 13).Select(c => $"{a.HeightAtMap(cx + 8 * c, cy + 8 * b) - water,5:0.0}")));
                }
                if (Environment.GetEnvironmentVariable("PW_DBG") == "2" && firstFlat != "")
                {
                    var m = System.Text.RegularExpressions.Regex.Match(firstFlat, @"\((-?\d+), (-?\d+)\)");
                    double cx = double.Parse(m.Groups[1].Value), cy = double.Parse(m.Groups[2].Value);
                    t.Log($"{w} flat rock at ({cx}, {cy}) h-water {a.HeightAtMap(cx, cy) - water:0.0} old? {ext.InOld(cx, cy)} oldmat {md.MaterialAtMap(cx, cy)} oldh {md.HeightAtMap(cx, cy) - water:0.0}");
                    for (int b = -2; b <= 2; b++)
                        t.Log(string.Join(" ", Enumerable.Range(-2, 5).Select(c => $"{a.MaterialAtMap(cx + 4 * c, cy + 4 * b)}:{MapExtension.Slope4(a, cx + 4 * c, cy + 4 * b):0.00}:{a.HeightAtMap(cx + 4 * c, cy + 4 * b) - water:0.0}")));
                }
                if (Environment.GetEnvironmentVariable("PW_DBG") == "1" && firstStep != "")
                {
                    var m = System.Text.RegularExpressions.Regex.Match(firstStep, @"\((-?\d+), (-?\d+)\)");
                    double cx = double.Parse(m.Groups[1].Value), cy = double.Parse(m.Groups[2].Value);
                    t.Log($"{w} around ({cx}, {cy}): new / old heights - water, spine dist {ext.Bridges.Select(b => Enumerable.Range(0, b.Points.Count - 1).Min(q => SegD(cx, cy, b.Points[q], b.Points[q + 1])) - b.Width / 2).DefaultIfEmpty(999).Min():0}");
                    for (int b = -4; b <= 4; b++)
                        t.Log(string.Join(" ", Enumerable.Range(-4, 9).Select(c => $"{a.HeightAtMap(cx + 2 * c, cy + 2 * b) - water,5:0.0}")) + "   |   " + string.Join(" ", Enumerable.Range(-4, 9).Select(c => ext.InOld(cx + 2 * c, cy + 2 * b) ? $"{md.HeightAtMap(cx + 2 * c, cy + 2 * b) - water,5:0.0}" : "    -")));
                }
                string line = $"{w}: rock materials {string.Join(" ", rock.Select((v, k) => double.IsInfinity(v) ? "" : $"{k}>{v:0.00}").Where(v => v != ""))}; rock {rockCells} cells, {flatRock} on flat ground {firstFlat}; joins {pairs} pairs, {steps} steps (worst {firstStep}), {creases} creases {firstCrease}";
                if (Verbose) t.Log(line + $" [{md.Setting}: " + string.Join(" ", MapExtension.GroundUse(md).Select((u, k) => $"{k}:{u.Count}/{u.P20:0.00}/{u.P50:0.00}/{u.Steep:0.00}")) + "]");
                // (the limits: no rock on flat ground; on the joins at most 12 % of the pairs steeper than 0.6 where the old
                // ground was not - hillsides of the patches and of paths' cuts along the old map's slopes -, none steeper
                // than 3.5 (a wall: 5.9 before the joins were smoothed), creases at most 3.5 %)
                if (flatRock > 0 || steps > pairs * 12 / 100 || worst > 3.5 || creases > pairs * 35 / 1000) problems.Add(line);
            }
        t.True(problems.Count == 0, "the grown land's ground follows its slope and its joins are smooth:\n      " + string.Join("\n      ", problems));
    }
    static double SegD(double x, double y, CoopPoint a, CoopPoint b)
    {
        double dx = b.X - a.X, dy = b.Z - a.Z, l2 = dx * dx + dy * dy, tt = l2 > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Z) * dy) / l2, 0, 1) : 0;
        return Math.Sqrt((a.X + tt * dx - x) * (a.X + tt * dx - x) + (a.Z + tt * dy - y) * (a.Z + tt * dy - y));
    }

    /// <summary>is (x, y) within m of a land bridge's land (the spines as made, MapExtensionInfo.Bridges)</summary>
    static bool NearSpine(List<CoopBridge> spines, double x, double y, double m)
    {
        foreach (var b in spines)
            for (int k = 0; k + 1 < b.Points.Count; k++)
            {
                var p = b.Points[k]; var q = b.Points[k + 1];
                double dx = q.X - p.X, dy = q.Z - p.Z, l2 = dx * dx + dy * dy, tt = l2 > 0 ? Math.Clamp(((x - p.X) * dx + (y - p.Z) * dy) / l2, 0, 1) : 0;
                double px = p.X + tt * dx - x, py = p.Z + tt * dy - y;
                if (Math.Sqrt(px * px + py * py) < Math.Max(b.Width, b.Land * 1.2) / 2 + 10 + m) return true;
            }
        return false;
    }
    /// <summary>every base site in new land (2 and 3 seats) looks like the old map's land, not like a made disc: its build
    /// area is flat enough to build on (heights within 7 m, nearly all walkable, dry), its coast is irregular (the
    /// distance from the start to the water varies by direction: not a circle), its ground has several of the setting's
    /// materials, and the old map's plants or decoration and trees stand on it</summary>
    public static void BaseSitesLook(TestContext t)
    {
        var problems = new List<string>();
        foreach (var (cfg, id) in All())
            foreach (int seats in new[] { 2, 3 })
            {
                var d = Def(t, cfg, id);
                if (d.NoBuild) continue;
                var plan = Plan(d, seats);
                var md = OriginalMap(t, cfg, id);
                var a = MapExtension.Apply(md, plan);
                foreach (var site in plan.Sites)
                {
                    // (an enemy base's ground: CoopWorldTests.EnemyAdditions)
                    if (site.Enemy || site.Ally != null || md.Extension == null && a.Extension!.InOld(site.X, site.Z) && md.HeightAtMap(site.X, site.Z) >= md.Water - 0.5) continue;
                    string w = $"{cfg} {id:00} ({seats} seats) seat {site.Seat}";
                    // the build area (0.8 r): dry, walkable, flat enough
                    double r = site.R * 0.8, lo = 1e9, hi = -1e9; int n = 0, walk = 0, dry = 0;
                    for (double dz = -r; dz <= r; dz += 4)
                        for (double dx = -r; dx <= r; dx += 4)
                        {
                            if (dx * dx + dz * dz > r * r) continue;
                            double h = a.HeightAtMap(site.X + dx, site.Z + dz);
                            n++; lo = Math.Min(lo, h); hi = Math.Max(hi, h);
                            if (h > a.Water + 1) dry++;
                            if (a.Walk == null || a.WalkableMap(site.X + dx, site.Z + dz) == true) walk++;
                        }
                    // the coast: the distance to the water in 64 directions (not those of its land bridges)
                    var bridgeDirs = a.Extension!.Bridges.Where(b => Math.Abs(b.Points[0].X - site.X) + Math.Abs(b.Points[0].Z - site.Z) < 1)
                        .Select(b => Math.Atan2(b.Points[Math.Min(3, b.Points.Count - 1)].Z - site.Z, b.Points[Math.Min(3, b.Points.Count - 1)].X - site.X)).ToList();
                    var dist = new List<double>();
                    for (int k = 0; k < 64; k++)
                    {
                        double ang = k * Math.PI / 32;
                        if (bridgeDirs.Any(bd => Math.Abs(Math.IEEERemainder(ang - bd, 2 * Math.PI)) < 0.6)) continue;
                        double dd = 0;
                        for (dd = 4; dd < 500; dd += 4) if (a.HeightAtMap(site.X + dd * Math.Cos(ang), site.Z + dd * Math.Sin(ang)) < a.Water) break;
                        dist.Add(dd);
                    }
                    double mean = dist.Average(), cv = Math.Sqrt(dist.Sum(v => (v - mean) * (v - mean)) / dist.Count) / mean;
                    // the ground's materials and what stands on it within 160 m
                    var mc = new int[8]; int mn = 0;
                    for (double dz = -160; dz <= 160; dz += 8)
                        for (double dx = -160; dx <= 160; dx += 8)
                        {
                            if (dx * dx + dz * dz > 160 * 160 || a.HeightAtMap(site.X + dx, site.Z + dz) < a.Water + 0.5) continue;
                            int m = a.MaterialAtMap(site.X + dx, site.Z + dz); if (m >= 0) { mc[m]++; mn++; }
                        }
                    int mats = mc.Count(c => c >= mn * 0.03);
                    bool Near(double x, double y) => (x - site.X) * (x - site.X) + (y - site.Z) * (y - site.Z) < 160 * 160;
                    int deco = a.Plants.Count(p => Near(p.X, p.Y)) + a.Objects.Count(o => o.Type is "VGTN" or "DCCO" or "DECO" && Near(o.X, o.Y));
                    int trees = a.Objects.Count(o => o.Type == "TREE" && Near(o.X, o.Y)) + a.Forest.Blocks.Count(b => Near(b.X * 32 + 16, b.Y * 32 + 16)) * 8;
                    var st = a.Extension.Stamps.FirstOrDefault(q => q.Seat == site.Seat);
                    if (Verbose) t.Log($"{w}: {(st != null ? st.ToString() : "generated")}; build area range {hi - lo:0.0} m, walkable {100.0 * walk / n:0}%, dry {100.0 * dry / n:0}%; coast {dist.Min():0} .. {dist.Max():0} m, cv {cv:0.00}; materials {mats}; deco {deco}, trees {trees}");
                    if (hi - lo > 7) problems.Add($"{w}: the build area is not flat: heights {lo:0.0} .. {hi:0.0} m");
                    if (walk < n * 0.92 || dry < n) problems.Add($"{w}: the build area: {100.0 * walk / n:0}% walkable, {100.0 * dry / n:0}% dry");
                    if (cv < 0.08) problems.Add($"{w}: the coast is (nearly) a circle: distances {dist.Min():0} .. {dist.Max():0} m, variation {cv:0.00}");
                    if (mats < Math.Min(3, OldLandMaterials(md))) problems.Add($"{w}: only {mats} ground materials");
                    if (deco < 15 || trees < 15) problems.Add($"{w}: bare land: {deco} plants / decoration, {trees} trees within 160 m");
                }
            }
        foreach (var p in problems) t.Log(p);
        t.Equal(0, problems.Count, "base site problems (listed above)");
    }

    /// <summary>how many ground materials the old map's land has (an eighth of it each): a map of two cannot give a site three</summary>
    static int OldLandMaterials(MapData md)
    {
        var mc = new int[8]; int mn = 0;
        for (int j = 0; j < md.My; j += 2) for (int i = 0; i < md.Mx; i += 2)
        {
            double x = md.OffX + 4.0 * i + 2, y = md.OffY + 4.0 * j + 2;
            if (md.HeightAtMap(x, y) < md.Water + 0.5) continue;
            int m = md.MaterialAtMap(x, y); if (m >= 0) { mc[m]++; mn++; }
        }
        // (the materials that cover an eighth of its land or more: the ones a piece of it shows)
        return mc.Count(c => c >= mn * 0.125);
    }

    static string FindMapFile(string cfg, int id)
    {
        var c = CampaignTests.LoadCfgPublic(cfg)!;
        var e = MissionEntry.LoadIndex(c.Config.MissionsDir).First(x => x.Id == id);
        return WorldBuilder.FindMap(c.Config, e.Map)!;
    }

    /// <summary>The old map's objects and plants keep the ground they were placed on (mission 13's SEAS fortress: "the
    /// yellow ground coming out of the metal stairs"): every one the extension keeps stands as high above its ground as in
    /// the original (the map gives each its height; where the ground moved a little, it moved with it), and the world's
    /// ground is the map's own 2 m grid on every map - also the big ones and the co-op maps grown past 1400 m, whose
    /// mesh was 4 m before (the fortress' stairs and metal plateaus up to 4.5 m off their ground).</summary>
    public static void ObjectsKeepTheirGround(TestContext t)
    {
        var bad = new List<string>();
        foreach (var (cfg, id) in All())
            foreach (int seats in new[] { 2, 3 })
            {
                var d = Def(t, cfg, id);
                var md = OriginalMap(t, cfg, id);
                var a = MapExtension.Apply(md, Plan(d, seats));
                string w = $"{cfg} {id:00} ({seats} seats)";
                int n = 0, off = 0; string? first = null;
                // (matched by position and name, in order - a few stand twice on one spot: a kept object is a copy when
                // its ground moved)
                var objs = a.Objects.GroupBy(o => (o.Cls, o.X, o.Y)).ToDictionary(g => g.Key, g => new Queue<MapObject>(g));
                var plants = a.Plants.GroupBy(p => (p.Name, p.X, p.Y)).ToDictionary(g => g.Key, g => new Queue<MapPlant>(g));
                void Check(string name, double x, double y, double z0, double? z1)
                {
                    if (z1 is not double z) return;
                    n++;
                    double dg = a.HeightAtMap(x, y) - md.HeightAtMap(x, y);
                    if (Math.Abs(z - z0 - dg) > 0.02) { off++; first ??= $"{name} at ({x:0}, {y:0}): ground {dg:+0.00;-0.00} m, object {z - z0:+0.00;-0.00} m"; }
                }
                foreach (var o in md.Objects) Check(o.Cls, o.X, o.Y, o.Z, objs.TryGetValue((o.Cls, o.X, o.Y), out var oo) && oo.Count > 0 ? oo.Dequeue().Z : null);
                foreach (var p in md.Plants) Check(p.Name, p.X, p.Y, p.Z, plants.TryGetValue((p.Name, p.X, p.Y), out var pp) && pp.Count > 0 ? pp.Dequeue().Z : null);
                if (off > 0) bad.Add($"{w}: {off} of {n} kept objects and plants off their ground, e.g. {first}");
                if (Verbose) t.Log($"{w}: {n} kept objects and plants on their ground");
            }
        foreach (var b in bad) t.Log(b);
        t.Equal(0, bad.Count, "the old map's objects on their ground (listed above)");
        // the ground the world and the view are made of: the map's own 2 m samples on every map
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("base configuration not found");
        foreach (int id in new[] { 13, 2 })
        {
            var f = FindMapFile("base", id);
            var S = WorldBuilder.MapSourceOf(f, c!.Rules, c.Models, null, c.Forest);
            if (id == 2) S.CopyFrom(S.Rebuild!(MapExtension.Apply(S.Md!, Plan(Def(t, "base", 2), 3))));
            t.Equal(2.0, S.MeshCell, $"mission {id}{(id == 2 ? " (co-op, 3 seats)" : "")}, {S.Size} m: height samples every 2 m");
            var hf = new HeightField(S.Size, S.MeshCell, S.Height);
            var md = S.Md!;
            double worst = 0;
            for (double my = md.OffY + 40; my < md.OffY + md.Hy * 2 - 40; my += 2)
                for (double mx = md.OffX + 40; mx < md.OffX + md.Hx * 2 - 40; mx += 2)
                {
                    var (gx, gz) = S.ToGame(mx, my);
                    worst = Math.Max(worst, Math.Abs(hf.At(gx, gz) - md.HeightAtMap(mx, my)));
                }
            t.True(worst < 0.01, $"mission {id}: the world's ground at the map's samples = the map's ({worst:0.000} m off at most)");
        }
    }

    /// <summary>an original map without a plan: MapSourceOf gives the same source as before (single player untouched);
    /// an extended map: the source's size, origin, half size, play area, the world's nav grid and fog agree</summary>
    public static void ExtendedMapBounds(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("base configuration not found");
        var f = FindMapFile("base", 1);
        var s0 = MapSource.OriginalSource(MapFiles.Read(f), new RulesMapAdapter(c!.Rules), new MapSourceConfig(), n => c.Models.Has(n), c.Forest);
        var s1 = WorldBuilder.MapSourceOf(f, c.Rules, c.Models, null, c.Forest);
        t.Equal(s0.Size, s1.Size, "single player: size"); t.Equal(s0.Origin![0], s1.Origin![0], "single player: origin");
        t.Equal(s0.Trees.Count, s1.Trees.Count, "single player: trees"); t.Equal(s0.Half![0], s0.Origin[0], "an original map: half size = origin");
        var plan = Plan(Def(t, "base", 1), 3);
        var S = WorldBuilder.MapSourceOf(f, c.Rules, c.Models, null, c.Forest);
        S.CopyFrom(S.Rebuild!(MapExtension.Apply(S.Md!, plan)));
        var md = S.Md!;
        double W2 = md.Hx * 2, H2 = md.Hy * 2;
        t.True(S.Size >= Math.Max(W2, H2), "the world covers the extended map");
        t.Near(md.OffX + W2 / 2, S.Origin![0], 1e-9, "origin x = the extended map's centre (old map coordinates)");
        t.Near(md.OffY + H2 / 2, S.Origin[1], 1e-9, "origin y");
        t.Near(W2 / 2, S.Half![0], 1e-9, "half size x"); t.Near(H2 / 2, S.Half[1], 1e-9, "half size y");
        t.True(S.Play.X0 >= -S.Size / 2 && S.Play.X1 <= S.Size / 2 && S.Play.Z0 >= -S.Size / 2 && S.Play.Z1 <= S.Size / 2, "play area inside the world");
        t.Near(-W2 / 2 + 32, S.Play.X0, 1e-9, "play area: the extended map minus the 32 m frame");
        // a point of the old map: the same ground through the map source (game) and the map data (map coordinates)
        var site2 = plan.Sites.First(q => q.Seat == 2);
        foreach (var (mx, my) in new[] { (300.0, 400.0), (900.0, 300.0), (site2.X, site2.Z), (-100.0, 560.0), (600.0, 1000.0) })
        {
            var (gx, gz) = S.ToGame(mx, my);
            t.Near(md.HeightAtMap(mx, my), S.Height(gx, gz), 1e-6, $"height at map ({mx}, {my})");
            var (bx, by) = S.ToMap(gx, gz); t.Near(mx, bx, 1e-9, "ToMap(ToGame) x"); t.Near(my, by, 1e-9, "ToMap(ToGame) y");
            if (md.Walk != null) t.Equal(md.WalkableMap(mx, my) == true, S.Walkable!(gx, gz), $"walk grid at map ({mx}, {my})");
        }
        // the world built on it: nav grid and fog cover the world
        var built = WorldBuilder.Build(S, c.Rules, c.Models, new WorldOptions { Seed = 1 }, null, c.Brushes);
        var W = built.World;
        t.Near(S.Size, W.Nav.N * W.Nav.Cell, 2.01, "nav grid covers the world");
        var fog = W.EnableFog(new Player(9, "Hu", new PlayerOpts { Rules = c.Rules }));
        t.Near(S.Size, fog.N * fog.Cell, 4.01, "fog grid covers the world");
        // the old map's places keep their game positions relative to each other, and the new land is inside the world
        var (ax, az) = S.ToGame(site2.X, site2.Z);
        t.True(S.Play.Inside(ax, az, 20), "the new land's base site is inside the play area");
        t.True(W.Nav.Block[W.Nav.Idx(ax, az)] == 0, "the new land's base site is walkable");
    }

    // ------------------------------------------------------------------ every seat of every mission, 2 and 3 seats
    /// <summary>starts each mission as a co-op game the way the lobby does (GameStart.Coop: the definition, seat 2 a human,
    /// seat 3 an AI commander - or seat 3 empty), so the map hook extends the map, and checks every co-op seat's start:
    /// walkable, a way to seat 1 (or the definition's `reach` point) by land - by sea for a naval mission -, no enemy
    /// base close, and wood, stone and food near it (missions with bases)</summary>
    public static void SeatsReachAndResources(TestContext t)
    {
        var problems = new List<string>();
        foreach (var (cfg, id) in All())
        {
            var def = Def(t, cfg, id);
            var d = def.Raw;
            bool nobuild = def.NoBuild, naval = d["naval"] is JsonValue nv && nv.GetValue<bool>();
            string tribe = MissionJson(cfg, id)?["players"]?[0]?["tribe"]?.GetValue<string>() ?? "";
            foreach (int seats in new[] { 2, 3 })
            {
                var kinds = new[] { ("human", tribe), ("human", ""), ("ai", "") }.Take(seats).ToArray();
                var start = CoopTests.CoopStart(t, cfg, id, kinds);
                var s = Session(t, start);
                var C = s.Campaign; var W = s.World; var S = s.Map;
                string w = $"{cfg} {id:00} ({seats} seats)";
                foreach (var pr in s.Problems) problems.Add($"{w}: load problem: {pr}");
                // (a hero phase: the seats' bases come when seat 1 gets its base - checked as they will stand then)
                if (C.Coop!.HeroPhase) C.Coop.HandOver();
                var human = C.Human!;
                var sl = C.StartLocations[human.Id];
                (double X, double Z) seat1 = sl != null ? (sl.X, sl.Z) : (s.StartCamera.X, s.StartCamera.Z);
                (double X, double Z) target = d["reach"] is JsonObject r ? C.ToGame(r["x"]!.GetValue<double>(), r["z"]!.GetValue<double>()) : seat1;
                var land = Flood(W, human, target, W.Nav);
                t.Equal(seats, C.Coop!.Seats.Count, $"{w}: seats");
                foreach (var seat in C.Coop.Seats.Where(x => x.Seat >= 2))
                {
                    var ssl = C.StartLocations[seat.Slot];
                    if (ssl == null) { problems.Add($"{w} seat {seat.Seat}: no start location"); continue; }
                    (double X, double Z) at = (ssl.X, ssl.Z);
                    var (amx, amy) = C.ToMap(at.X, at.Z);
                    string ws = $"{w} seat {seat.Seat} at map {amx:0},{amy:0}";
                    if (d["seats"]?[seat.Seat.ToString()]?["start"] is JsonObject dst && (Math.Abs(dst["x"]!.GetValue<double>() - amx) > 0.01 || Math.Abs(dst["z"]!.GetValue<double>() - amy) > 0.01))
                        problems.Add($"{ws}: not where the definition puts it ({dst["x"]}, {dst["z"]})");
                    if (!S.Play.Inside(at.X, at.Z, 40)) { problems.Add($"{ws}: not 40 m inside the play area"); continue; }
                    int k = NearestFree(W.Nav, at.X, at.Z, 12);
                    if (k < 0) { problems.Add($"{ws}: no walkable ground at the start"); continue; }
                    // (a seat's own `reach`: where it must walk to - mission 1's seat 3 goes out through the prison camp)
                    var sland = d["seats"]?[seat.Seat.ToString()]?["reach"] is JsonObject sr ? Flood(W, human, C.ToGame(sr["x"]!.GetValue<double>(), sr["z"]!.GetValue<double>()), W.Nav) : land;
                    bool ok = sland[k];
                    if (!ok && naval) ok = BySea(W, human, k, sland);
                    if (!ok) problems.Add($"{ws}: no way to {(d["reach"] != null ? "the reach point" : "seat 1")}{(naval ? " by land or sea" : " by land")}");
                    // its main building stands (missions with bases)
                    var mine = W.Units.Where(u => u.Alive && u.Owner == seat.Player).ToList();
                    // (the Dustriders' main is a vehicle, their resource collector)
                    if (!nobuild && !W.Buildings.Any(b => b.Alive && b.Owner == seat.Player) && !mine.Any(u => u.Name == "aje_resource_collector"))
                        problems.Add($"{ws}: no main building placed");
                    // no enemy base next to the start
                    // (the definition's own enemy additions may stand closer - they are placed to threaten the seat - but not
                    // within 110 m)
                    var added = new HashSet<Player>(C.Coop.Enemies.List.Where(e => e.Def.New).Select(e => e.Player));
                    int enemyB = W.Buildings.Count(b => b.Alive && b.Owner != null && human.IsEnemy(b.Owner) && Dist(b.Pos.X, b.Pos.Z, at) < (added.Contains(b.Owner) ? 110 : 160));
                    int enemyU = W.Units.Count(u => u.Alive && u.Owner != null && human.IsEnemy(u.Owner) && Dist(u.Pos.X, u.Pos.Z, at) < 120);
                    // (mission 13: the seats defend the city wall - the attackers are the point)
                    if (!nobuild && !DefendMissions.Contains(id) && (enemyB > 0 || enemyU > 3)) problems.Add($"{ws}: enemies close ({enemyB} buildings within 160 m, {enemyU} units within 120 m)");
                    // the resources of a base
                    int trees = W.Resources.Count(q => q.Alive && q.Type == "tree" && Dist(q.Pos.X, q.Pos.Z, at) < 110);
                    int stones = W.Resources.Count(q => q.Alive && q.Type == "stone" && Dist(q.Pos.X, q.Pos.Z, at) < 120);
                    int food = W.Resources.Count(q => q.Alive && q.Type == "bush" && Dist(q.Pos.X, q.Pos.Z, at) < 120) + 3 * W.Resources.Count(q => q.Alive && q.Type == "fish" && Dist(q.Pos.X, q.Pos.Z, at) < 150);
                    if (Verbose) t.Log($"{ws}: way {ok}, units {mine.Count}, trees {trees}, stones {stones}, food {food}, enemies {enemyB}/{enemyU}");
                    if (!nobuild && seat.New && (trees < 15 || stones < 1 || food < 3))
                        problems.Add($"{ws}: few resources (trees {trees} within 110 m, stones {stones} within 120 m, food {food})");
                }
            }
        }
        foreach (var p in problems) t.Log(p);
        t.Equal(0, problems.Count, "seat problems (listed above)");
    }

    static MissionSession Session(TestContext t, GameStart start)
    {
        var c = CampaignTests.LoadCfgPublic(start.Configuration)!;
        return MissionSession.Create(start, c.Config, new SessionOptions
        {
            Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = true, NoTriggers = true,
        });
    }

    /// <summary>every co-op seat of every base mission starts with a proper base (2 and 3 seats, every tribe - the
    /// Dustriders' main building is a vehicle, MIRAGE's SEAS too): after the start (or the hero phase's hand-over) it has
    /// its tribe's main building, at least three workers (units that build or harvest) and room to build round it (half
    /// the nav cells within 40 m of its start walkable)</summary>
    public static void SeatsBaseStart(TestContext t)
    {
        var problems = new List<string>();
        foreach (var (cfg, id) in All())
        {
            var def = Def(t, cfg, id);
            if (def.NoBuild) continue;
            var combos = new List<(string, string)[]>
            {
                new[] { ("human", ""), ("human", "Hu"), ("ai", "Aje") },
                new[] { ("human", ""), ("ai", cfg == "mirage" ? "SEAS" : "Ninigi"), ("human", "Ninigi") },
                new[] { ("human", ""), ("ai", "Aje") },
            };
            foreach (var kinds in combos)
            {
                var start = CoopTests.CoopStart(t, cfg, id, kinds);
                var s = Session(t, start);
                var C = s.Campaign; var W = s.World;
                if (C.Coop!.HeroPhase) C.Coop.HandOver();
                foreach (var seat in C.Coop.Seats.Where(x => x.Seat >= 2))
                {
                    string w = $"{cfg} {id:00} ({kinds.Length} seats) seat {seat.Seat} ({seat.Tribe}{(seat.Ai ? ", AI" : "")})";
                    var sl = C.StartLocations[seat.Slot];
                    if (sl == null) { problems.Add($"{w}: no start location"); continue; }
                    string mb = PwRemake.Sim.Campaign.Campaign.MAIN_BUILDING.GetValueOrDefault(seat.Tribe, "");
                    bool main = W.Buildings.Any(b => b.Alive && b.Owner == seat.Player && b.Name == mb) || W.Units.Any(u => u.Alive && u.Owner == seat.Player && u.Name == mb);
                    int workers = W.Units.Count(u => u.Alive && u.Owner == seat.Player && C.D.Def(u.Name, null) is { } d && (d.CanBuild != 0 || d.CanHarvest != 0));
                    int n = 0, free = 0;
                    for (double dz = -40; dz <= 40; dz += W.Nav.Cell)
                        for (double dx = -40; dx <= 40; dx += W.Nav.Cell)
                        {
                            if (dx * dx + dz * dz > 1600) continue;
                            n++; if (W.Nav.Block[W.Nav.Idx(sl.X + dx, sl.Z + dz)] == 0) free++;
                        }
                    if (Verbose) t.Log($"{w}: main {main}, workers {workers}, room {free * 100 / Math.Max(1, n)} %, buildings {W.Buildings.Count(b => b.Alive && b.Owner == seat.Player)}");
                    if (!main || workers < 3 || free < n / 2)
                        problems.Add($"{w}: main building {(main ? "yes" : "NO")}, {workers} workers, {free * 100 / Math.Max(1, n)} % room within 40 m");
                }
            }
        }
        foreach (var p in problems) t.Log(p);
        t.Equal(0, problems.Count, "seats without a proper base start (listed above)");
    }

    /// <summary>The land the computer players see (World.LandIsland, the original's "island": the walk grid's land, parted
    /// by cliffs and water - not by buildings or gates), 2 and 3 seats, after the start or the hero phase's hand-over:
    /// every seat's start is on a land island (not -1); every co-op seat's start is on seat 1's (naval missions: by land
    /// or sea, SeatsReachAndResources); every new enemy camp whose brain sends waves (`brain.attack`: land armies, spawned at the
    /// camp) is on the island of the seat its waves go for - else its waves need a
    /// ferry the camp does not have (base 06's raiders' cove before 2026-11-12: a chain of lobes cut off from seat 3's
    /// land by a strait and two ridges; docs/COOP.md "Joins"). Lists every violation.</summary>
    public static void LandIslands(TestContext t)
    {
        var problems = new List<string>();
        int checkedN = 0;
        foreach (var (cfg, id) in All())
        {
            var def = Def(t, cfg, id);
            var d = def.Raw;
            bool naval = d["naval"] is JsonValue nv && nv.GetValue<bool>();
            foreach (int seats in new[] { 2, 3 })
            {
                var start = CoopTests.CoopStart(t, cfg, id, new[] { ("human", ""), ("ai", ""), ("ai", "") }.Take(seats).ToArray());
                var s = Session(t, start);
                var C = s.Campaign; var W = s.World; var R = C.Coop!;
                if (R.HeroPhase) R.HandOver();
                string w = $"{cfg} {id:00} ({seats} seats)";
                // (the paths the extension added to join cut-off grown land: MapExtension.Joins)
                foreach (var l in s.Map.Md?.Extension?.Log ?? new List<string>()) if (l.StartsWith("join:")) t.Log($"{w}: {l}");
                int Island(double x, double z) => W.LandIsland(x, z);
                string Map(double x, double z) { var (mx, mz) = C.ToMap(x, z); return $"({mx:0}, {mz:0})"; }
                var seatIsl = new Dictionary<int, int>();
                foreach (var seat in R.Seats)
                {
                    var sl = C.StartLocations[seat.Slot];
                    if (sl == null) { problems.Add($"{w}: seat {seat.Seat} has no start location"); continue; }
                    int isl = Island(sl.X, sl.Z);
                    seatIsl[seat.Seat] = isl;
                    checkedN++;
                    if (isl < 0) problems.Add($"{w}: seat {seat.Seat}'s start {Map(sl.X, sl.Z)} is on no land island (-1)");
                }
                if (seatIsl.TryGetValue(1, out int one) && one >= 0 && !naval)
                    foreach (var (seat, isl) in seatIsl)
                        if (seat >= 2 && isl >= 0 && isl != one)
                        {
                            var sl = C.StartLocations[R.Seats.First(x => x.Seat == seat).Slot]!;
                            problems.Add($"{w}: seat {seat}'s start {Map(sl.X, sl.Z)} is on island {isl}, seat 1's on {one}");
                        }
                foreach (var e in R.Enemies.List)
                {
                    var ed = e.Def;
                    if (!ed.New || !ed.Brain || !ed.Attack) continue;
                    // (the seat its waves go for: the definition's in use, else the nearest - CoopRuntimeEnemies.Tick)
                    var target = (ed.Target is int ts ? R.Seats.FirstOrDefault(x => x.Seat == ts) : null)
                        ?? R.Seats.Where(x => C.StartLocations[x.Slot] != null).OrderBy(x => Dist(C.StartLocations[x.Slot]!.X, C.StartLocations[x.Slot]!.Z, (e.X, e.Z))).FirstOrDefault();
                    if (target == null || !seatIsl.TryGetValue(target.Seat, out int ti)) continue;
                    checkedN++;
                    // (the camp: its main building, else its centre)
                    var main = e.Placed.OfType<Building>().FirstOrDefault(b => b.Alive);
                    double cx = main?.Pos.X ?? e.X, cz = main?.Pos.Z ?? e.Z;
                    int ci = Island(cx, cz);
                    if (ci < 0 || ci != ti)
                        problems.Add($"{w}: {ed}: its camp {Map(cx, cz)} is on island {ci}, its target seat {target.Seat}'s start on {ti} - its land waves cannot walk there");
                }
                if (Verbose) t.Log($"{w}: seats {string.Join(", ", seatIsl.Select(x => $"{x.Key}:{x.Value}"))}");
            }
        }
        foreach (var p in problems) t.Log(p);
        t.True(checkedN > 0, "starts and camps checked");
        t.Equal(0, problems.Count, "land island problems (listed above)");
    }

    /// <summary>A design aid (PW_COOP_SITES=1; the seats' base places, docs/COOP.md "Fair bases"): per base mission, on
    /// the single-player map, seat 1's base (CoopPhaseTests.Fair), the mission's enemy buildings (hostile at the start,
    /// and the bases the fairness test walks to), D1 = seat 1's nearest, the definition's seats and enemy additions
    /// against the rule, and places for a seat's base near seat 1: on old land (flat, reachable, clear) and in the sea
    /// next to a coast seat 1 walks to (for grown land), each at least D1 + 20 m from every enemy building. Map
    /// coordinates.</summary>
    public static void SitesSurvey(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_COOP_SITES") != "1") t.Skip("PW_COOP_SITES=1 runs it");
        foreach (var (cfg, id) in All().Where(m => m.Item1 == "base"))
        {
            var def = Def(t, cfg, id);
            if (def.NoBuild || !CoopPhaseTests.Fair.TryGetValue(id, out var fair)) continue;
            var c = CampaignTests.LoadCfgPublic(cfg)!;
            var s = MissionSession.Create(new GameStart { Configuration = cfg, Mission = id, Seed = 1 }, c.Config, new SessionOptions
                { Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = true, NoTriggers = true });
            var C = s.Campaign; var W = s.World; var S = s.Map; var md = S.Md!; var hu = C.Human!;
            var seat1 = C.ToGame(fair.Seat1.X, fair.Seat1.Z);
            var enemy = W.Buildings.Where(b => b.Alive && b.Owner != null && b.Owner != hu && hu.IsEnemy(b.Owner)).Select(b => (b.Pos.X, b.Pos.Z)).ToList();
            if (!CoopPhaseTests.TargetsNotEnemies.Contains(id)) enemy.AddRange(fair.Targets.Select(q => C.ToGame(q.X, q.Z)));
            double NearE((double X, double Z) p) => enemy.Count == 0 ? 1e9 : enemy.Min(e => Dist(e.Item1, e.Item2, p));
            double d1 = NearE(seat1);
            double mapW = S.Play.X1 - S.Play.X0 + 64, N = CoopPhaseTests.NearShare * mapW;
            var raw = def.Raw;
            t.Log($"== {cfg} {id:00}: map {mapW:0} m (N {N:0}), seat 1 base ({fair.Seat1.X:0}, {fair.Seat1.Z:0}), D1 {d1:0} m");
            var adds = new List<(string Name, (double, double) G)>();
            foreach (var e in ListOf(raw["enemies"]))
            {
                var g = C.ToGame(e!["at"]!["x"]!.GetValue<double>(), e["at"]!["z"]!.GetValue<double>());
                adds.Add((e["name"]!.GetValue<string>(), g));
                t.Log($"   addition {e["name"]} ({e["at"]!["x"]}, {e["at"]!["z"]}): {Dist(g.Item1, g.Item2, seat1):0} m from seat 1");
            }
            foreach (var sn in new[] { "2", "3" })
            {
                var st = raw["seats"]?[sn]?["start"];
                if (st == null) continue;
                var g = C.ToGame(st["x"]!.GetValue<double>(), st["z"]!.GetValue<double>());
                double ne = NearE(g), na = adds.Select(a => Dist(a.G.Item1, a.G.Item2, g)).DefaultIfEmpty(1e9).Min();
                t.Log($"   seat {sn} ({st["x"]}, {st["z"]}): {Dist(g.Item1, g.Item2, seat1):0} m from seat 1, enemy {ne:0}, addition {na:0}{(Math.Min(ne, na) < d1 ? "  << enemy closer than D1" : "")}{(Dist(g.Item1, g.Item2, seat1) > N ? "  << beyond N" : "")}");
            }
            var land = Flood(W, hu, seat1, W.Nav);
            var nav = W.Nav; int n = nav.N;
            var objs = md.Objects.Where(o => MapExtension.ProtectRadius(o) > 0).Select(o => C.ToGame(o.X, o.Y)).ToList();
            var gates = nav.GateAt.Values.Distinct().Where(g => g.Owner == null || hu.IsEnemy(g.Owner)).Select(g => (g.Pos.X, g.Pos.Z)).ToList();
            // reachable / other land cells (coarse: every 4th nav cell)
            var reach = new List<(double, double)>(); var other = new List<(double, double)>();
            for (int k = 0; k < n * n; k += 1)
            {
                int ci = k % n, cj = k / n; if (ci % 3 != 0 || cj % 3 != 0) continue;
                var (x, z) = nav.Center(k);
                if (S.Height(x, z) < md.Water + 0.3) continue;
                if (land[k]) reach.Add((x, z)); else if (nav.Block[k] == 0) other.Add((x, z));
            }
            double NearL(List<(double, double)> l, (double, double) p) => l.Count == 0 ? 1e9 : l.Min(q => Dist(q.Item1, q.Item2, p));
            // (for a site in the sea: the nearest land seat 1 reaches, and 30 m further on - a path's points)
            foreach (var sn in new[] { "2", "3" })
            {
                var st = raw["seats"]?[sn]?["start"]; if (st == null) continue;
                var g = C.ToGame(st["x"]!.GetValue<double>(), st["z"]!.GetValue<double>());
                if (S.Play.Inside(g.Item1, g.Item2, 0) && S.Height(g.Item1, g.Item2) >= md.Water) continue;
                var q = reach.OrderBy(r => Dist(r.Item1, r.Item2, g)).First();
                double dd = Dist(q.Item1, q.Item2, g), ux = (q.Item1 - g.Item1) / dd, uz = (q.Item2 - g.Item2) / dd;
                var qm = C.ToMap(q.Item1, q.Item2); var q2 = C.ToMap(q.Item1 + 30 * ux, q.Item2 + 30 * uz);
                t.Log($"   seat {sn}: nearest land seat 1 reaches ({qm.Item1:0}, {qm.Item2:0}) {dd:0} m, height {S.Height(q.Item1, q.Item2) - md.Water:0.0}; 30 m on ({q2.Item1:0}, {q2.Item2:0}) height {S.Height(q.Item1 + 30 * ux, q.Item2 + 30 * uz) - md.Water:0.0}");
            }
            var cands = new List<(double X, double Z, double D, double E, string Kind)>();
            for (double z = seat1.Z - N; z <= seat1.Z + N; z += 20)
                for (double x = seat1.X - N; x <= seat1.X + N; x += 20)
                {
                    double d = Dist(x, z, seat1);
                    if (d < 110 || d > N) continue;
                    double e = Math.Min(NearE((x, z)), 1e9);
                    if (e < d1 + 20) continue;
                    double clr = Environment.GetEnvironmentVariable("PW_COOP_SITES_CLEAR") is { Length: > 0 } cs ? double.Parse(cs) : 60;
                    if (objs.Any(o => Dist(o.Item1, o.Item2, (x, z)) < clr) || gates.Any(g => Dist(g.Item1, g.Item2, (x, z)) < 200)) continue;
                    bool inMap = S.Play.Inside(x, z, 60);
                    double h = inMap ? S.Height(x, z) : -99;
                    if (inMap && h >= md.Water + 0.5)
                    {
                        int k = nav.Idx(x, z);
                        if (nav.Block[k] != 0 || !land[k]) continue;
                        double lo = 1e9, hi = -1e9; int cnt = 0, free = 0;
                        for (double dz = -36; dz <= 36; dz += 6) for (double dx = -36; dx <= 36; dx += 6)
                        {
                            if (dx * dx + dz * dz > 36 * 36) continue;
                            double hh = S.Height(x + dx, z + dz); lo = Math.Min(lo, hh); hi = Math.Max(hi, hh); cnt++;
                            int q = nav.Idx(x + dx, z + dz); if (nav.Block[q] == 0 && land[q]) free++;
                        }
                        if (hi - lo > 8 || free < cnt * 0.7 || W.Buildings.Any(b => b.Alive && Dist(b.Pos.X, b.Pos.Z, (x, z)) < clr)) continue;
                        cands.Add((x, z, d, e, $"old land, {free * 100 / cnt} % free"));
                    }
                    else
                    {
                        // grown land: the coast seat 1 walks to 50 .. 130 m away, no other land (behind gates, islands seat 1 does
                        // not reach) within 220 m
                        double r = NearL(reach, (x, z));
                        if (r < 50 || r > 130 || NearL(other, (x, z)) < 220) continue;
                        cands.Add((x, z, d, e, $"sea, coast {r:0} m"));
                    }
                }
            if (Environment.GetEnvironmentVariable("PW_COOP_SITES_MAP") == "1")
            {
                // a text map round seat 1 (20 m a character, north up): 1 seat 1, E enemy building, B other building,
                // # reachable land, + other walkable land, ^ blocked land, ~ water, s / 2 / 3 the seats, a additions
                var seatsG = new[] { "2", "3" }.Select(sn => raw["seats"]?[sn]?["start"]).Where(q => q != null).Select(q => C.ToGame(q!["x"]!.GetValue<double>(), q["z"]!.GetValue<double>())).ToList();
                for (double z = seat1.Z - 400; z <= seat1.Z + 400; z += 20)
                {
                    var sb = new System.Text.StringBuilder();
                    for (double x = seat1.X - 500; x <= seat1.X + 500; x += 10)
                    {
                        bool In((double, double) p, double r) => Math.Abs(p.Item1 - x) <= r && Math.Abs(p.Item2 - z) <= 10;
                        char ch;
                        if (In(seat1, 5)) ch = '1';
                        else if (seatsG.Count > 0 && In(seatsG[0], 5)) ch = '2';
                        else if (seatsG.Count > 1 && In(seatsG[1], 5)) ch = '3';
                        else if (adds.Any(a => In(a.G, 5))) ch = 'a';
                        else if (W.Buildings.Any(b => b.Alive && In((b.Pos.X, b.Pos.Z), 5) && b.Owner != null && hu.IsEnemy(b.Owner))) ch = 'E';
                        else if (W.Buildings.Any(b => b.Alive && In((b.Pos.X, b.Pos.Z), 5))) ch = 'B';
                        else if (!S.Play.Inside(x, z, 0)) ch = ' ';
                        else if (S.Height(x, z) < md.Water) ch = '~';
                        else { int k = nav.Idx(x, z); ch = nav.Block[k] != 0 ? '^' : land[k] ? '#' : '+'; }
                        sb.Append(ch);
                    }
                    var zm = C.ToMap(seat1.X, z);
                    t.Log($"{zm.Item2,5:0} {sb}");
                }
                var x0 = C.ToMap(seat1.X - 500, seat1.Z); t.Log($"      x from {x0.Item1:0} by 10 m");
            }
            var pick = new List<(double X, double Z, double D, double E, string Kind)>();
            foreach (var cd in cands.OrderBy(cd => cd.D))
                if (pick.All(p => Dist(p.X, p.Z, (cd.X, cd.Z)) > 120)) pick.Add(cd);
            foreach (var cd in pick.Take(8))
            {
                var cm = C.ToMap(cd.X, cd.Z);
                t.Log($"   place ({cm.Item1:0}, {cm.Item2:0}) {cd.Kind}: {cd.D:0} m from seat 1, enemy {cd.E:0}");
            }
            if (pick.Count == 0) t.Log("   no place");
        }
    }

    static double Dist(double x, double z, (double X, double Z) p) => Math.Sqrt((x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z));

    /// <summary>the nav cells a land unit of the player reaches from `from` (8-neighbourhood, no corner cutting); gates of
    /// the player's friends and of nobody count as open</summary>
    static bool[] Flood(PwRemake.Sim.World.World W, Player p, (double X, double Z) from, NavGrid nav)
    {
        int n = nav.N; var seen = new bool[n * n];
        int k0 = NearestFree(nav, from.X, from.Z, 12);
        if (k0 < 0) return seen;
        bool Free(int k) => nav.Block[k] == 0 && (!nav.GateAt.TryGetValue(k, out var g) || g.Owner == null || g.Owner == p || !p.IsEnemy(g.Owner));
        var st = new Stack<int>(); st.Push(k0); seen[k0] = true;
        while (st.Count > 0)
        {
            int c = st.Pop(), ci = c % n, cj = c / n;
            for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
            {
                int i = ci + di, j = cj + dj;
                if ((di == 0 && dj == 0) || i < 0 || j < 0 || i >= n || j >= n) continue;
                int k = j * n + i;
                if (seen[k] || !Free(k) || (di != 0 && dj != 0 && (!Free(cj * n + i) || !Free(j * n + ci)))) continue;
                seen[k] = true; st.Push(k);
            }
        }
        return seen;
    }
    static int NearestFree(NavGrid nav, double x, double z, double r)
    {
        int best = -1; double bd = 1e9;
        for (double dz = -r; dz <= r; dz += nav.Cell) for (double dx = -r; dx <= r; dx += nav.Cell)
        {
            int k = nav.Idx(x + dx, z + dz);
            if (nav.Block[k] != 0) continue;
            double d = dx * dx + dz * dz;
            if (d < bd) { bd = d; best = k; }
        }
        return best;
    }
    /// <summary>a transport from the seat's coast reaches a coast of seat 1's land: the water cells next to the seat's
    /// land region and those next to `target` land are one water region</summary>
    static bool BySea(PwRemake.Sim.World.World W, Player p, int seatCell, bool[] targetLand)
    {
        var wn = W.WaterNav;
        if (wn == null) return false;
        var mine = Flood(W, p, W.Nav.Center(seatCell), W.Nav);
        int n = W.Nav.N;
        var wr = wn.Region;
        if (wr == null) return false;
        var a = new HashSet<int>(); var b = new HashSet<int>();
        for (int k = 0; k < n * n; k++)
        {
            if (!mine[k] && !targetLand[k]) continue;
            int ci = k % n, cj = k / n;
            for (int dj = -2; dj <= 2; dj++) for (int di = -2; di <= 2; di++)
            {
                int i = ci + di, j = cj + dj;
                if (i < 0 || j < 0 || i >= n || j >= n) continue;
                var (x, z) = W.Nav.Center(j * n + i);
                int q = wn.Idx(x, z);
                if (wn.Block[q] != 0 || wr[q] < 0) continue;
                if (mine[k]) a.Add(wr[q]); else b.Add(wr[q]);
            }
        }
        return a.Overlaps(b);
    }
}

/// <summary>A design aid for the co-op definitions (not a check): PW_COOP_SURVEY=1 ... -- CoopMapsSurvey prints, per
/// mission and map edge, the places where a bridged base site in an extension would land on ground seat 1 can walk to
/// (the world's nav grid of the original mission, gates of enemies closed): the landing point, the bridge's length over
/// water / low shore, the distance to the nearest enemy and neutral object, and the walk from seat 1's start.</summary>
[TestClass]
public static class CoopMapsSurvey
{
    public static void Survey(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_COOP_SURVEY") != "1") t.Skip("PW_COOP_SURVEY=1 runs it");
        var only = Environment.GetEnvironmentVariable("PW_COOP_MISSIONS");
        foreach (var cfg in new[] { "base", "mirage" })
            foreach (var id in CoopMapsTests.BaseMissions)
            {
                if (only is { Length: > 0 } && !only.Split(',').Contains(id.ToString())) continue;
                var c = CampaignTests.LoadCfgPublic(cfg)!;
                // PW_COOP_SURVEY_SEATS=2|3: the map of a co-op game with the mission's definition (to look at its result)
                int seatsN = int.TryParse(Environment.GetEnvironmentVariable("PW_COOP_SURVEY_SEATS"), out var sn) ? sn : 0;
                var gs = new GameStart { Configuration = cfg, Mission = id, Seed = 1 };
                if (seatsN > 0) gs = CoopTests.CoopStart(t, cfg, id, new[] { ("human", ""), ("human", ""), ("ai", "") }.Take(seatsN).ToArray());
                var s = MissionSession.Create(gs, c.Config, new SessionOptions
                    { Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = true, NoTriggers = true });
                var C = s.Campaign; var W = s.World; var nav = W.Nav; var md = s.Map.Md!; var hu = C.Human!;
                var sl = C.StartLocations[hu.Id]!;
                int n = nav.N;
                // walking distance from seat 1's start (cells), land only
                var dist = new int[n * n]; Array.Fill(dist, -1);
                bool Free(int k) => nav.Block[k] == 0 && (!nav.GateAt.TryGetValue(k, out var g) || g.Owner == null || !hu.IsEnemy(g.Owner));
                var q = new Queue<int>(); int k0 = nav.Idx(sl.X, sl.Z);
                for (int r = 0; r < 6 && !Free(k0); r++) k0 += 1;
                dist[k0] = 0; q.Enqueue(k0);
                while (q.Count > 0)
                {
                    int cc = q.Dequeue(), ci = cc % n, cj = cc / n;
                    foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int i = ci + di, j = cj + dj; if (i < 0 || j < 0 || i >= n || j >= n) continue;
                        int k = j * n + i; if (dist[k] >= 0 || !Free(k)) continue; dist[k] = dist[cc] + 1; q.Enqueue(k);
                    }
                }
                var enemies = W.Buildings.Cast<Entity>().Concat(W.Units).Where(e => e.Alive && e.Owner != null && e.Owner != hu && hu.IsEnemy(e.Owner)).Select(e => (e.Pos.X, e.Pos.Z)).ToList();
                var neutral = W.Buildings.Cast<Entity>().Concat(W.Units).Where(e => e.Alive && e.Owner != null && e.Owner != hu && hu.IsNeutral(e.Owner)).Select(e => (e.Pos.X, e.Pos.Z)).ToList();
                double Near(List<(double X, double Z)> l, double x, double z) => l.Count == 0 ? 9999 : l.Min(p => Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z)));
                if (Environment.GetEnvironmentVariable("PW_COOP_SURVEY_DIR") is { Length: > 0 } dir)
                {
                    // a picture of the nav grid: reachable from seat 1 (light), other walkable ground (green), blocked (dark),
                    // water (blue); 2 m per pixel, north up
                    var img = new byte[n * n * 3];
                    var seatLand = new bool[n * n];
                    foreach (var seat in C.Coop?.Seats.Where(x => x.Seat >= 2) ?? Enumerable.Empty<PwRemake.Sim.Coop.CoopSeatRun>())
                    {
                        if (C.StartLocations[seat.Slot] is not { } ssl) continue;
                        var (gx, gz) = (ssl.X, ssl.Z);
                        int k1 = nav.Idx(gx, gz); var st2 = new Stack<int>(); if (Free(k1)) { seatLand[k1] = true; st2.Push(k1); }
                        while (st2.Count > 0) { int cc = st2.Pop(), ci = cc % n, cj = cc / n; foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) { int i = ci + di, j = cj + dj; if (i < 0 || j < 0 || i >= n || j >= n) continue; int k = j * n + i; if (seatLand[k] || !Free(k)) continue; seatLand[k] = true; st2.Push(k); } }
                    }
                    for (int k = 0; k < n * n; k++)
                    {
                        var (x, z) = nav.Center(k);
                        bool water = s.Map.Water is double wl && s.Map.Height(x, z) < wl;
                        (byte r, byte g, byte b) col = seatLand[k] && dist[k] < 0 ? ((byte)240, (byte)150, (byte)40) : dist[k] >= 0 ? ((byte)200, (byte)230, (byte)160) : nav.Block[k] == 0 ? ((byte)60, (byte)120, (byte)50) : water ? ((byte)40, (byte)70, (byte)140) : ((byte)50, (byte)40, (byte)35);
                        img[k * 3] = col.r; img[k * 3 + 1] = col.g; img[k * 3 + 2] = col.b;
                    }
                    using var fs = File.Create(Path.Combine(dir, $"nav_{cfg}_{id:00}{(seatsN > 0 ? "_s" + seatsN : "")}.ppm"));
                    fs.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {n} {n} 255\n")); fs.Write(img);
                    File.WriteAllText(Path.Combine(dir, $"nav_{cfg}_{id:00}{(seatsN > 0 ? "_s" + seatsN : "")}.meta"), $"{(int)Math.Round(C.ToMap(-s.Map.Size / 2, 0).MX)} {(int)Math.Round(C.ToMap(0, -s.Map.Size / 2).MY)}");
                }
                var (smx, smy) = C.ToMap(sl.X, sl.Z);
                t.Log($"== {cfg} {id:00} map {md.Hx * 2}x{md.Hy * 2} water {md.Water} start ({smx:0},{smy:0}) reachable {dist.Count(v => v >= 0) * 4 / 10000.0:0.0} ha");
                foreach (var edge in new[] { "N", "S", "W", "E" })
                {
                    var cands = new List<(double lx, double ly, double ex, double ey, double len, double foe, double neu, double walk)>();
                    double W2 = md.Hx * 2, H2 = md.Hy * 2;
                    double along = edge is "N" or "S" ? W2 : H2, depth = edge is "N" or "S" ? H2 : W2;
                    for (double a = 48; a < along - 48; a += 8)
                    {
                        for (double k = 10; k < depth - 10; k += 2)
                        {
                            double mx = edge switch { "N" or "S" => a, "W" => k, _ => W2 - k }, my = edge switch { "W" or "E" => a, "N" => k, _ => H2 - k };
                            var (gx, gz) = C.ToGame(mx, my);
                            int cell = nav.Idx(gx, gz);
                            if (dist[cell] >= 0)
                            {
                                double ex = edge switch { "W" => 0, "E" => W2, _ => mx }, ey = edge switch { "N" => 0, "S" => H2, _ => my };
                                cands.Add((mx, my, ex, ey, k, Near(enemies, gx, gz), Near(neutral, gx, gz), dist[cell] * 2));
                                break;
                            }
                            if (md.HeightAtMap(mx, my) > md.Water + 5) break;
                        }
                    }
                    var shown = new List<(double, double)>();
                    foreach (var grp in new[] { cands.Where(cc => cc.walk < 450), cands.Where(cc => cc.walk >= 450) })
                        foreach (var cc in grp.Where(cc => cc.foe > 100).OrderBy(cc => cc.len - Math.Min(cc.foe, 400) * 0.5).Take(30))
                        {
                            if (shown.Any(p => Math.Abs(p.Item1 - cc.lx) + Math.Abs(p.Item2 - cc.ly) < 150)) continue;
                            shown.Add((cc.lx, cc.ly));
                            t.Log($"  {edge}: land ({cc.lx:0},{cc.ly:0}) edge ({cc.ex:0},{cc.ey:0}) bridge {cc.len:0} m, enemy {cc.foe:0} m, neutral {cc.neu:0} m, walk {cc.walk:0} m");
                            if (shown.Count >= 6) break;
                        }
                }
            }
    }
}
