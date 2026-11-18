// Regions of a campaign map (map chunk Rgns, docs/CAMPAIGN_FORMAT.md §4 and §10.4 of the toolkit): named unions of
// rectangles and ovals in MAP coordinates (x east, y south; a shape = minimum corner + size, an oval = the ellipse
// inscribed in the box). Triggers test "is this object in the region" (REGN, every object query) and switch regions
// or single shapes on and off (ARGN) or move them (MRGN).
//
// All tests here take GAME coordinates (x, z) like everything else at run time; the conversion is done inside.
//
// Ported from remake/src/game/campaign/regions.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Campaign;

public sealed class RegionShape
{
    public bool Oval, Enabled;
    public double X, Y, W, H;
}

public class Region
{
    public readonly Regions Owner;
    public readonly string Guid, Name;
    public readonly JsonNode? Data;
    /// <summary>ARGN with sub_idx -1</summary>
    public bool Enabled = true;
    public bool World { get; protected init; }
    public readonly List<RegionShape> Shapes;
    /// <summary>bumped by SetEnabled / MoveTo (conditions may cache on it)</summary>
    public int Version;
    double[] _bbox = new double[4];

    public Region(string guid, string name, IEnumerable<RegionShape> shapes, Regions owner, JsonNode? data = null)
    {
        Owner = owner; Guid = guid; Name = name; Data = data;
        Shapes = shapes.ToList();
        Rebox();
    }

    public static Region FromJson(JsonNode d, Regions owner) => new(d["guid"].Str(), d["name"].Str(),
        (d["shapes"]?.AsArray() ?? new JsonArray()).Select(s => new RegionShape
        {
            Oval = s!["type"].Str() == "oval", Enabled = !(s["enabled"] is JsonValue ev && ev.TryGetValue(out bool b) && !b),
            X = s["x"].Num(), Y = s["y"].Num(), W = s["w"].Num(), H = s["h"].Num(),
        }), owner, d);

    void Rebox()
    {
        double x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
        foreach (var s in Shapes) if (s.Enabled) { x0 = Math.Min(x0, s.X); y0 = Math.Min(y0, s.Y); x1 = Math.Max(x1, s.X + s.W); y1 = Math.Max(y1, s.Y + s.H); }
        _bbox = new[] { x0, y0, x1, y1 };
    }
    /// <summary>[x0, y0, x1, y1] of the enabled shapes (map); x0 &gt; x1 when there is none</summary>
    public IReadOnlyList<double> BBox => _bbox;

    /// <summary>map coordinates</summary>
    public virtual bool ContainsMap(double mx, double my)
    {
        if (!Enabled) return false;
        var b = _bbox;
        if (mx < b[0] || my < b[1] || mx > b[2] || my > b[3]) return false;
        foreach (var s in Shapes)
        {
            if (!s.Enabled || mx < s.X || my < s.Y || mx > s.X + s.W || my > s.Y + s.H) continue;
            if (!s.Oval) return true;
            double u = (mx - s.X) / s.W * 2 - 1, v = (my - s.Y) / s.H * 2 - 1;
            if (u * u + v * v <= 1) return true;
        }
        return false;
    }
    /// <summary>game coordinates</summary>
    public virtual bool Contains(double x, double z) { var (mx, my) = Owner.ToMap(x, z); return ContainsMap(mx, my); }
    /// <summary>centre of the enabled shapes' bounding box, game coordinates (null for an empty / disabled region)</summary>
    public virtual (double X, double Z)? Center()
    {
        var b = _bbox;
        if (b[0] > b[2]) return null;
        return Owner.ToGame((b[0] + b[2]) / 2, (b[1] + b[3]) / 2);
    }
    /// <summary>bounding circle in game coordinates (for spatial queries)</summary>
    public virtual (double X, double Z, double R)? Bounds()
    {
        var b = _bbox;
        if (b[0] > b[2]) return null;
        var (x, z) = Owner.ToGame((b[0] + b[2]) / 2, (b[1] + b[3]) / 2);
        return (x, z, Math.Sqrt((b[2] - b[0]) * (b[2] - b[0]) + (b[3] - b[1]) * (b[3] - b[1])) / 2);
    }
    /// <summary>ARGN: the whole region (shape &lt; 0) or one shape (0-based)</summary>
    public virtual void SetEnabled(bool on, int shape = -1)
    {
        if (shape < 0) Enabled = on;
        else if (shape < Shapes.Count) Shapes[shape].Enabled = on;
        Rebox(); Version++; Owner.Version++;
    }
    /// <summary>MRGN: move the region so that its bounding box is centred on the game point (x, z)</summary>
    public virtual void MoveTo(double x, double z)
    {
        var b = _bbox;
        if (b[0] > b[2]) return;
        var (mx, my) = Owner.ToMap(x, z);
        double dx = mx - (b[0] + b[2]) / 2, dy = my - (b[1] + b[3]) / 2;
        foreach (var s in Shapes) { s.X += dx; s.Y += dy; }
        Rebox(); Version++; Owner.Version++;
    }
    public override string ToString() => $"region {Name}";
}

/// <summary>the whole map (trigger parameter "UniqueWorldRegion", an empty or an unknown GUID: ConditionFactory.usl:455-458)</summary>
public sealed class WorldRegion : Region
{
    public WorldRegion(Regions owner) : base("UniqueWorldRegion", "UniqueWorldRegion", Array.Empty<RegionShape>(), owner) { World = true; }
    public override bool ContainsMap(double mx, double my) => true;
    public override bool Contains(double x, double z) => true;
    public override (double X, double Z)? Center() => (0, 0);
    public override (double X, double Z, double R)? Bounds() => null;
    public override void SetEnabled(bool on, int shape = -1) { }
    public override void MoveTo(double x, double z) { }
}

public sealed class Regions
{
    readonly Func<double, double, (double, double)> _toGame, _toMap;
    public int Version;
    public readonly WorldRegion World;
    public readonly List<Region> List;
    readonly Dictionary<string, Region> _byGuid = new(StringComparer.Ordinal), _byName = new(StringComparer.Ordinal);

    /// <summary>list = the mission's regions[] (world.json); toGame(mx, my) -> (x, z); toMap(x, z) -> (mx, my)</summary>
    public Regions(JsonArray? list, Func<double, double, (double, double)> toGame, Func<double, double, (double, double)> toMap)
    {
        _toGame = toGame; _toMap = toMap;
        World = new WorldRegion(this);
        List = (list ?? new JsonArray()).Where(d => d != null).Select(d => Region.FromJson(d!, this)).ToList();
        foreach (var r in List) { _byGuid[r.Guid] = r; _byName.TryAdd(r.Name, r); }
    }
    public (double X, double Z) ToGame(double mx, double my) => _toGame(mx, my);
    public (double MX, double MY) ToMap(double x, double z) => _toMap(x, z);

    /// <summary>by GUID or name; null if there is none</summary>
    public Region? Find(string? r) => r == null ? null : _byGuid.TryGetValue(r, out var g) ? g : _byName.TryGetValue(r, out var n) ? n : null;
    /// <summary>as the trigger parameters mean it: unknown, empty or "UniqueWorldRegion" = the whole map</summary>
    public Region Get(string? r) => Find(r) ?? World;
    /// <summary>the regions containing the game point (x, z)</summary>
    public List<Region> At(double x, double z) { var (mx, my) = ToMap(x, z); return List.Where(r => r.ContainsMap(mx, my)).ToList(); }
}
