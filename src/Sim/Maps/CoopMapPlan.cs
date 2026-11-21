// The map part of a co-op definition (docs/COOP.md "Maps", schema v1 of docs/COOP_DESIGN.md) as plain data: the
// extension of the map at its edges (`extend`), the resources added (`resources`) and the seats' starts (for their
// base sites), each with the part that belongs to one seat only (`seats[n].extend`, `seats[n].resources`: left out
// when that seat is empty - a 2-seat game). MapExtension.Apply turns an original map (MapData) and a plan into the
// map a co-op game is played on.
//
// Nothing here depends on src/Sim/Coop: the core's definition loader (CoopDefinition) keeps the definition as JSON
// (CoopDefinition.Raw) and the map hook (CoopMapHook.Maps.cs) hands it to CoopMapPlan.FromJson.
//
// Coordinates are the original map's MAP coordinates (as the mission's triggers, world.json and mission.json use them):
// x east, z = map y (south), metres from the old map's north-west corner. The extension keeps them: new land to the
// north / west lies at negative coordinates, to the south / east beyond the old map's size. (Campaign.ToGame turns
// them into game coordinates, as for every other position of a mission.)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.Json.Nodes;

namespace PwRemake.Sim.Maps;

/// <summary>a point in the old map's map coordinates (x east, z = map y south)</summary>
public sealed class CoopPoint
{
    public double X, Z;
    public CoopPoint() { }
    public CoopPoint(double x, double z) { X = x; Z = z; }
    public override string ToString() => $"({X:0.#}, {Z:0.#})";
}

/// <summary>a land bridge from a base site to the old map's coast: a walkable spine along a polyline (bent into a natural
/// curve by the generator) inside a wide, irregular isthmus of low land - or a narrow, low sandy ford (`shape = "ford"`,
/// the naval maps: little land in the sea lanes). In the new land and, where it crosses the old map's water, raised out
/// of the old map's water (co-op only).</summary>
public sealed class CoopBridge
{
    public List<CoopPoint> Points = new();
    /// <summary>width of the walkable spine (m); the isthmus around it is about three times as wide</summary>
    public double Width = 16;
    /// <summary>how deep the strip may cut into ground higher than its line (m): a pass through a coast's cliffs</summary>
    public double Cut = 6;
    /// <summary>"isthmus" (default: a wide land bridge with beaches, dunes and plants), "ford" (narrow, low, sandy) or
    /// "path" (a graded way through grown land, no land of its own)</summary>
    public string Shape = "isthmus";
    /// <summary>the width of the land around the spine (m; set by the generator: MapExtensionInfo.Bridges)</summary>
    public double Land;
    public bool Ford => Shape == "ford";
    /// <summary>"path": a walkable way through grown land (style "grow"): only the spine is graded, no land of its own</summary>
    public bool Path => Shape == "path";
}

/// <summary>the patch of the old map a base site's land is made from (`seats[n].extend.stamp`): the heights, ground
/// materials, plants, decoration and natural resources around (X, Z) within R, turned by Rot degrees (clockwise seen
/// from above, north up; null: so that the patch's coast faces the site's land bridge) and mirrored. null fields are
/// chosen by the generator (MapExtension.AutoStamp).</summary>
public sealed class CoopStamp
{
    /// <summary>the patch's centre in the old map (where the site's start lands); null: chosen automatically</summary>
    public double? X, Z;
    public double R = 160;
    public double? Rot;
    public bool? Mirror;
    public override string ToString() => $"stamp ({X?.ToString("0") ?? "auto"}, {Z?.ToString("0") ?? "auto"}) r {R:0} rot {Rot?.ToString("0") ?? "auto"} mirror {Mirror?.ToString() ?? "auto"}";
}

/// <summary>a round piece of land the generator raises in the new land (besides the seats' base sites)</summary>
public sealed class CoopLand
{
    public double X, Z, R = 40;
}

/// <summary>a lobe of the grown land (`extend.grow`, style "grow"): a round piece of new land of radius R around (X, Z)
/// whose ground is a patch of the old map (a stamp: chosen automatically unless given, `stamp = false`: generated
/// hills). The lobes of a definition (and every co-op seat's base site, a lobe of its own) melt into one landmass with
/// the old map's land: where they cover the old map's sea it becomes land, its old beaches there go over into the new
/// land; their outer edge is a natural coast.</summary>
public sealed class CoopGrow
{
    public double X, Z, R = 180;
    public CoopStamp? Stamp;
    public bool NoStamp;
    /// <summary>the seat whose base site this lobe is (0: a lobe of `grow`)</summary>
    public int Seat;
    public override string ToString() => $"grow ({X:0}, {Z:0}) r {R:0}";
}

/// <summary>a zone of the old map the extension never changes (`extend.keep`), besides the automatic ones (40 m around
/// the mission's buildings, units, start locations and other objects)</summary>
public sealed class CoopKeep
{
    public double X, Z, R = 60;
}

/// <summary>`extend` of a co-op definition: terrain added at the map's edges</summary>
public sealed class CoopExtend
{
    /// <summary>metres added at each edge (rounded up to 32 m: the forest squares')</summary>
    public int North, South, East, West;
    /// <summary>seed of the generated terrain, plants and resources</summary>
    public int Seed = 1;
    /// <summary>"grow" (the default): the edge's land goes on and the base sites and `grow` lobes become one landmass
    /// with the old map's land (MapExtension.Grow.cs); "island": water between the old map and the new land (only the
    /// bases' sites, `land` and `bridges` are land); "continue": the edge's land goes on, base sites are islands</summary>
    public string Style = "grow";
    public bool Grows => Style == "grow";
    /// <summary>a water level for the new land (null: the map's; the world has one water level - only the new land's
    /// heights are generated against this one)</summary>
    public double? Water;
    public List<CoopBridge> Bridges = new();
    public List<CoopLand> Land = new();
    /// <summary>style "grow": lobes of new land joined to the old map's (besides the base sites' own)</summary>
    public List<CoopGrow> Grow = new();
    /// <summary>zones of the old map that stay exactly as they are</summary>
    public List<CoopKeep> Keep = new();

    public bool Any => North > 0 || South > 0 || East > 0 || West > 0 || Bridges.Count > 0 || Land.Count > 0 || Grow.Count > 0;

    /// <summary>this extension plus another one (a seat's own): the larger edge, the other's lists added; seed and
    /// style stay this one's</summary>
    public CoopExtend Merge(CoopExtend? o)
    {
        if (o == null) return this;
        return new CoopExtend
        {
            North = Math.Max(North, o.North), South = Math.Max(South, o.South), East = Math.Max(East, o.East), West = Math.Max(West, o.West),
            Seed = Seed, Style = Style, Water = Water ?? o.Water,
            Bridges = Bridges.Concat(o.Bridges).ToList(), Land = Land.Concat(o.Land).ToList(),
            Grow = Grow.Concat(o.Grow).ToList(), Keep = Keep.Concat(o.Keep).ToList(),
        };
    }
}

/// <summary>an entry of `resources`: one object of a class at a point, or a generated group of a kind in an area</summary>
public sealed class CoopResource
{
    /// <summary>the class of a resource object (stone_big, Northland_Tree_02, Resource_Stone_Nor ...) or null</summary>
    public string? Class;
    /// <summary>"wood" (trees), "stone", "food" (fruit bushes), "fish" (shoals in the water), "kit" (a base's set:
    /// a wood patch, two stones, fruit bushes - around the point) or null (Class given)</summary>
    public string? Kind;
    public double X, Z;
    /// <summary>radius of the area (0: exactly at X, Z)</summary>
    public double R;
    public int Count = 1;
    /// <summary>the resource amount (stones / bushes / fish: hitpoints); null = the class's default</summary>
    public double? Amount;
    public override string ToString() => $"{Kind ?? Class} x{Count} at ({X:0}, {Z:0}) r {R:0}";
}

/// <summary>a co-op seat's start as far as the map cares: where its base goes</summary>
public sealed class CoopSite
{
    public int Seat;
    public double X, Z;
    /// <summary>the seat takes an existing ally's slot (no base site of its own)</summary>
    public int? Ally;
    /// <summary>radius of the flat base site the generator makes when the start lies in the new land</summary>
    public double R = 56;
    /// <summary>the patch of the old map the site's land is made from (null: chosen automatically)</summary>
    public CoopStamp? Stamp;
    /// <summary>`stamp = false`: generated land instead of a patch of the old map</summary>
    public bool NoStamp;
    /// <summary>style "grow": the radius of the site's own lobe of land (`start.land`, default 190 m)</summary>
    public double LandR = 190;
    /// <summary>an enemy base of the definition (`enemies[k].at`): its build area is levelled when it lies in new land,
    /// and resources keep away from it; no stamp, no lobe</summary>
    public bool Enemy;
    /// <summary>an enemy base whose waves walk to the seats (a new player whose brain attacks, not by ship): style "grow"
    /// joins its land to the seats' (MapExtension.Joins); one without (a static outpost, a battery on an islet) may stand
    /// on land of its own</summary>
    public bool Waves;
}

/// <summary>everything MapExtension needs from a co-op definition, for a given number of seats</summary>
public sealed class CoopMapPlan
{
    /// <summary>the extension (the definition's top-level one merged with the active seats' own); null = none</summary>
    public CoopExtend? Extend;
    public List<CoopResource> Resources = new();
    /// <summary>the active co-op seats' starts (2 .. seats) and the enemy bases of the definition for this seat count
    /// (CoopSite.Enemy)</summary>
    public List<CoopSite> Sites = new();
    /// <summary>the number of seats the plan was made for (2 or 3)</summary>
    public int Seats = 3;
    /// <summary>seed of the generation (extend.seed; 1 without an extension)</summary>
    public int Seed => Extend?.Seed ?? 1;
    /// <summary>nothing to do: no extension, no resources, no base site in new land</summary>
    public bool Empty => (Extend == null || !Extend.Any) && Resources.Count == 0;

    // ---------------------------------------------------------------- from the definition (JSON, as CoopDefinition keeps it)
    /// <summary>the map part of a co-op definition (CoopDefinition.Raw: the table its file returns, as JSON) for the
    /// seats in use (seat numbers; 1 may be among them): `extend` + `seats[n].extend` and `resources` +
    /// `seats[n].resources` of every co-op seat n in use, and those seats' starts (`seats[n].start`, `seats[n].ally`).
    /// An empty seat 3 leaves out everything that is only seat 3's.</summary>
    public static CoopMapPlan FromJson(JsonObject def, IEnumerable<int> seatsInUse)
    {
        var used = seatsInUse.Where(n => n >= 2).Distinct().OrderBy(n => n).ToList();
        var p = new CoopMapPlan { Seats = 1 + used.Count };
        p.Extend = ExtendOf(def["extend"]);
        p.Resources.AddRange(ResourcesOf(def["resources"]));
        if (def["seats"] is JsonObject st)
            foreach (int n in used)
            {
                if (st[n.ToString(CultureInfo.InvariantCulture)] is not JsonObject s) continue;
                var e = ExtendOf(s["extend"]);
                if (e != null) p.Extend = p.Extend == null ? e : p.Extend.Merge(e);
                p.Resources.AddRange(ResourcesOf(s["resources"]));
                if (s["start"] is JsonObject start)
                {
                    var stamp = s["extend"]?["stamp"];
                    p.Sites.Add(new CoopSite
                    {
                        Seat = n, X = Num(start, "x"), Z = Num(start, "z"), R = Num(start, "r", 56),
                        Ally = s["ally"] is JsonValue a && a.TryGetValue<double>(out var ad) ? (int)ad : null,
                        Stamp = StampOf(stamp), NoStamp = stamp is JsonValue sv && sv.TryGetValue<bool>(out var sb) && !sb,
                        LandR = Num(start, "land", 190),
                    });
                }
            }
        // the enemy bases of the definition that appear with this many seats: their build areas
        int k = 0;
        foreach (var en in Items(def["enemies"]))
        {
            k++;
            if (en is not JsonObject eo || eo["at"] is not JsonObject at) continue;
            if (Num(eo, "seats", 2) > p.Seats) continue;
            // (waves: a new player - owner "new" - whose brain attacks: CoopEnemyDef's defaults; its waves are land armies)
            var br = eo["brain"];
            bool brain = !(br is JsonValue bv && bv.TryGetValue<bool>(out var bb) && !bb);
            bool attack = !(br is JsonObject bo && bo["attack"] is JsonValue av && av.TryGetValue<bool>(out var ab) && !ab);
            p.Sites.Add(new CoopSite { Seat = -k, X = Num(at, "x"), Z = Num(at, "z"), R = Num(at, "r", 40), Enemy = true, Waves = Str(eo, "owner") == "new" && brain && attack });
        }
        return p;
    }
    /// <summary>the plan of a game of `seats` seats (2: seats 1-2, 3: seats 1-3)</summary>
    public static CoopMapPlan FromJson(JsonObject def, int seats) => FromJson(def, Enumerable.Range(1, Math.Max(1, seats)));

    static double Num(JsonNode? t, string k, double def = 0)
    {
        var v = t?[k];
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out var d)) return d;
            if (jv.TryGetValue<string>(out var str) && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)) return d2;
        }
        return def;
    }
    static string? Str(JsonNode? t, string k) => t?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    /// <summary>a list in the definition: a Lua sequence is a JSON array, an empty table an empty object</summary>
    static IEnumerable<JsonNode> Items(JsonNode? n) => n is JsonArray a ? a.OfType<JsonNode>() : n is JsonObject o ? o.Select(x => x.Value).OfType<JsonNode>() : Enumerable.Empty<JsonNode>();

    /// <summary>`extend` (or a seat's `extend`); null when absent</summary>
    public static CoopExtend? ExtendOf(JsonNode? v)
    {
        if (v is not JsonObject t) return null;
        int Edge(string k) { double m = Num(t, k); return m <= 0 ? 0 : (int)(Math.Ceiling(m / 32) * 32); }
        var e = new CoopExtend
        {
            North = Edge("north"), South = Edge("south"), East = Edge("east"), West = Edge("west"),
            Seed = (int)Num(t, "seed", 1), Style = Str(t, "style") ?? "grow",
            Water = t["water"] is JsonValue wv && wv.TryGetValue<double>(out var w) ? w : null,
        };
        foreach (var b in Items(t["bridges"]))
        {
            var br = new CoopBridge { Width = Num(b, "width", 16), Cut = Num(b, "cut", 6), Shape = Str(b, "shape") ?? "isthmus" };
            foreach (var q in Items(b["points"] ?? b)) if (q is JsonObject) br.Points.Add(new CoopPoint(Num(q, "x"), Num(q, "z")));
            if (br.Points.Count >= 2) e.Bridges.Add(br);
        }
        foreach (var l in Items(t["land"])) e.Land.Add(new CoopLand { X = Num(l, "x"), Z = Num(l, "z"), R = Num(l, "r", 40) });
        foreach (var g in Items(t["grow"]))
            e.Grow.Add(new CoopGrow { X = Num(g, "x"), Z = Num(g, "z"), R = Num(g, "r", 180), Stamp = StampOf(g["stamp"]), NoStamp = g["stamp"] is JsonValue gv && gv.TryGetValue<bool>(out var gb) && !gb });
        foreach (var q in Items(t["keep"])) e.Keep.Add(new CoopKeep { X = Num(q, "x"), Z = Num(q, "z"), R = Num(q, "r", 60) });
        return e;
    }

    /// <summary>a seat's `extend.stamp` = { x =, z =, r =, rot =, mirror = } (every field optional); null when absent or false</summary>
    public static CoopStamp? StampOf(JsonNode? v)
    {
        if (v is not JsonObject t) return null;
        double? N(string k) => t[k] is JsonValue jv && jv.TryGetValue<double>(out var d) ? d : null;
        return new CoopStamp
        {
            X = N("x"), Z = N("z"), R = N("r") ?? 160, Rot = N("rot"),
            Mirror = t["mirror"] is JsonValue mv && mv.TryGetValue<bool>(out var mb) ? mb : null,
        };
    }

    /// <summary>`resources` (or a seat's)</summary>
    public static IEnumerable<CoopResource> ResourcesOf(JsonNode? v)
    {
        foreach (var t in Items(v))
        {
            if (t is not JsonObject) continue;
            var at = t["area"] is JsonObject area ? area : t;
            yield return new CoopResource
            {
                Class = Str(t, "class"), Kind = Str(t, "kind"), X = Num(at, "x"), Z = Num(at, "z"), R = Num(at, "r"),
                Count = (int)Num(t, "count", 1), Amount = t["amount"] is JsonValue av && av.TryGetValue<double>(out var am) ? am : null,
            };
        }
    }
}
