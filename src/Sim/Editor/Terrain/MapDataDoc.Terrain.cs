// The phase 2 part of the in-memory document (MapDataDoc.cs; docs/MAP_EDITOR.md "Terrain and paint"): ground
// materials, the water level, the walk grid with painted overrides, the biome layer, forest squares and plants - and
// what stands on the ground following a height edit.
//
// The walk grid: every map gets one (an original map has its editor's PaFi; a map without one - a new map - gets it
// made by the slope rule and the water, as the new map format's walk.png: "from the original's PaFi, else computed").
// A cell is either painted (walkable / blocked: WalkOverride) or Auto: an original map's own value where the ground
// round it is as opened and on the same side of the water as when opened, else the game's slope rule (32 degrees) and
// the water. So an undo gives every cell back exactly.
//
// Seating: a height edit moves the placed objects and the plants standing in it with the ground - those whose Z was at
// most 1 m above the ground (or lower: the plateaus and rocks sunk into it) keep their offset to it; ships, things on
// bridges and plateaus stay. The first time an edit reaches something its Z and the ground under it are remembered;
// the ground back where it was gives that Z back exactly (undo).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

public sealed partial class MapDataDoc
{
    /// <summary>objects and plants at most this far above the ground (m) follow a height edit</summary>
    public const double SeatAbove = 1.0;

    byte[] _walkOvr = Array.Empty<byte>();
    double _baseWater;
    // the heights the seats were last made on (a copy kept in step with Heights after every HeightsChanged)
    float[] _seat = Array.Empty<float>();
    readonly Dictionary<object, Seat> _seats = new(ReferenceEqualityComparer.Instance);

    /// <summary>what an object or plant had when a height edit first reached it (X, Y: where; LastZ: the Z the doc last
    /// gave it - another Z or place means it was moved since, and it is seated anew)</summary>
    sealed class Seat { public double X, Y, LastZ, BaseZ, BaseGround; }

    void InitTerrain()
    {
        _baseWater = Md.Water;
        _seat = (float[])Md.Heights.Clone();
        if (Md.Walk == null)
        {
            // (a map without its editor's grid: one made by the slope rule and the water)
            Md.Walk = new WalkGrid { W = Md.Hx, H = Md.Hy, OffX = Md.OffX, OffY = Md.OffY, Cells = new byte[Md.Hx * Md.Hy] };
            _walkOvr = new byte[Md.Walk.Cells.Length];
            RecomputeWalk(new GridRect(0, 0, Md.Walk.W - 1, Md.Walk.H - 1));
        }
        else
        {
            _walkOvr = new byte[Md.Walk.Cells.Length];
            // (a saved map's painted cells: walk.png 64 / 192 - the doc keeps them as its overrides)
            if (Md.Walk.Paint is { } pt && pt.Length == _walkOvr.Length) Array.Copy(pt, _walkOvr, pt.Length);
            Md.Walk.Paint = null;
        }
    }

    void CopyOverrides(MapDataDoc from)
    {
        if (from._walkOvr.Length == _walkOvr.Length) Array.Copy(from._walkOvr, _walkOvr, _walkOvr.Length);
    }

    static double Bilinear(float[] hs, int hx, int hy, double fx, double fy)
    {
        fx = Math.Max(0, Math.Min(hx - 1.001, fx)); fy = Math.Max(0, Math.Min(hy - 1.001, fy));
        int i = (int)Math.Floor(fx), j = (int)Math.Floor(fy);
        double u = fx - i, v = fy - j;
        return (hs[j * hx + i] * (1 - u) + hs[j * hx + i + 1] * u) * (1 - v) + (hs[(j + 1) * hx + i] * (1 - u) + hs[(j + 1) * hx + i + 1] * u) * v;
    }

    /// <summary>the objects and plants over the changed samples (and one around) follow the ground; the seats' copy of
    /// the heights is brought up to date there</summary>
    void Reseat(GridRect r)
    {
        double fx0 = r.I0 - 1, fx1 = r.I1 + 1, fy0 = r.J0 - 1, fy1 = r.J1 + 1;
        void One(object key, double x, double y, double z, Action<double> setZ)
        {
            double fx = (x - OriginX) / Cell, fy = (y - OriginY) / Cell;
            if (fx < fx0 || fx > fx1 || fy < fy0 || fy > fy1) return;
            if (!_seats.TryGetValue(key, out var s) || s.X != x || s.Y != y || s.LastZ != z)
                _seats[key] = s = new Seat { X = x, Y = y, LastZ = z, BaseZ = z, BaseGround = Bilinear(_seat, Md.Hx, Md.Hy, fx, fy) };
            if (s.BaseZ - s.BaseGround > SeatAbove) return;
            double g = Bilinear(Md.Heights, Md.Hx, Md.Hy, fx, fy);
            double nz = g == s.BaseGround ? s.BaseZ : g + (s.BaseZ - s.BaseGround);
            if (nz != z) setZ(nz);
            s.LastZ = nz;
        }
        foreach (var o in Md.Objects) One(o, o.X, o.Y, o.Z, v => o.Z = v);
        foreach (var p in Md.Plants) One(p, p.X, p.Y, p.Z, v => p.Z = v);
        int i0 = Math.Max(0, r.I0), i1 = Math.Min(Md.Hx - 1, r.I1);
        for (int j = Math.Max(0, r.J0); j <= Math.Min(Md.Hy - 1, r.J1); j++)
            if (i1 >= i0) Array.Copy(Md.Heights, j * Md.Hx + i0, _seat, j * Md.Hx + i0, i1 - i0 + 1);
    }

    // ---------------------------------------------------------------- the walk grid

    /// <summary>the cells in r (walk cells) again: override, else auto</summary>
    void RecomputeWalk(GridRect r)
    {
        if (Md.Walk is not { } wg) return;
        var slope = MapSource.SlopeWalkable(HeightAt);
        double? water = Water;
        for (int d = Math.Max(0, r.J0); d <= Math.Min(wg.H - 1, r.J1); d++)
            for (int c = Math.Max(0, r.I0); c <= Math.Min(wg.W - 1, r.I1); c++)
            {
                int k = d * wg.W + c;
                var o = k < _walkOvr.Length ? (WalkOverride)_walkOvr[k] : WalkOverride.Auto;
                if (o != WalkOverride.Auto) { wg.Cells[k] = (byte)(o == WalkOverride.Walkable ? 1 : 0); continue; }
                double mx = wg.OffX + 2 * c + 1, my = wg.OffY + 2 * d + 1;
                if (_baseWalk != null && Unchanged(mx, my) && SameWaterSide(mx, my)) { wg.Cells[k] = _baseWalk[k]; continue; }
                bool ok = slope(mx, my) && (water is not double w || HeightAt(mx, my) > w);
                wg.Cells[k] = (byte)(ok ? 1 : 0);
            }
    }

    /// <summary>is the ground at map (mx, my) on the same side of the water as when the map was opened</summary>
    bool SameWaterSide(double mx, double my)
    {
        if (Md.Water == _baseWater) return true;
        double h = HeightAt(mx, my);
        return (h > _baseWater) == (h > Md.Water);
    }

    public int WalkW => Md.Walk?.W ?? 0;
    public int WalkH => Md.Walk?.H ?? 0;
    public double WalkOriginX => Md.Walk?.OffX ?? Md.OffX;
    public double WalkOriginY => Md.Walk?.OffY ?? Md.OffY;
    public double WalkCell => 2;
    public bool WalkCellOk(int c, int d) => Md.Walk is { } wg && c >= 0 && d >= 0 && c < wg.W && d < wg.H && wg.Cells[d * wg.W + c] == 1;
    public WalkOverride GetWalkOverride(int c, int d) => c >= 0 && d >= 0 && c < WalkW && d < WalkH ? (WalkOverride)_walkOvr[d * WalkW + c] : WalkOverride.Auto;
    public void SetWalkOverride(int c, int d, WalkOverride o) { if (c >= 0 && d >= 0 && c < WalkW && d < WalkH) _walkOvr[d * WalkW + c] = (byte)o; }

    public void WalkChanged(GridRect cells)
    {
        if (cells.IsEmpty) return;
        RecomputeWalk(cells);
        // walk cell c covers map WalkOriginX + 2c .. + 2: samples (that - OriginX) / 2 ..
        int si = (int)Math.Floor((WalkOriginX - OriginX) / Cell), sj = (int)Math.Floor((WalkOriginY - OriginY) / Cell);
        Changed?.Invoke(new DocChange(DocChangeKind.Walk, new GridRect(si + cells.I0, sj + cells.J0, si + cells.I1 + 1, sj + cells.J1 + 1)));
    }

    // ---------------------------------------------------------------- materials, water

    public int MatsW => Md.Mx;
    public int MatsH => Md.My;
    public double MatCell => 4;
    public int Material(int i, int j) => Md.Mats[j * Md.Mx + i] & 7;
    public void SetMaterial(int i, int j, int m) { int k = j * Md.Mx + i; Md.Mats[k] = (byte)((Md.Mats[k] & ~7) | (m & 7)); }
    public void MaterialsChanged(GridRect cells)
    {
        if (cells.IsEmpty) return;
        Changed?.Invoke(new DocChange(DocChangeKind.Materials, new GridRect(cells.I0 * 2, cells.J0 * 2, cells.I1 * 2 + 2, cells.J1 * 2 + 2)));
    }

    public void SetWater(double? level)
    {
        Md.Water = level ?? -1000;
        RecomputeWalk(new GridRect(0, 0, WalkW - 1, WalkH - 1));
        Changed?.Invoke(new DocChange(DocChangeKind.Water, GridRect.Empty));
    }

    // ---------------------------------------------------------------- biomes

    public BiomeLayer? Biomes => Md.Biomes;

    /// <summary>a deep copy of a layer (null: null)</summary>
    public static BiomeLayer? CloneLayer(BiomeLayer? l) => l == null ? null : new BiomeLayer
    {
        Biomes = l.Biomes.ToList(), Cell = l.Cell, X0 = l.X0, Y0 = l.Y0, W = l.W, H = l.H, Weights = (byte[])l.Weights.Clone(),
        Remap = new Dictionary<string, int[]>(l.Remap.Select(kv => KeyValuePair.Create(kv.Key, (int[])kv.Value.Clone())), StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>the same biomes on the same grid (only the weights differ)</summary>
    public static bool SameShape(BiomeLayer a, BiomeLayer b) =>
        a.W == b.W && a.H == b.H && a.Cell == b.Cell && a.X0 == b.X0 && a.Y0 == b.Y0 && a.Biomes.SequenceEqual(b.Biomes, StringComparer.OrdinalIgnoreCase)
        && a.Weights.Length == b.Weights.Length && a.Remap.Count == b.Remap.Count;

    public void SetBiomes(BiomeLayer? layer, GridRect samples)
    {
        var cur = Md.Biomes;
        if (layer != null && cur != null && SameShape(cur, layer))
        {
            // (in place: the map source's biome lookup holds this layer)
            Array.Copy(layer.Weights, cur.Weights, cur.Weights.Length);
            Changed?.Invoke(new DocChange(DocChangeKind.Biomes, samples));
            return;
        }
        Md.Biomes = CloneLayer(layer);
        Changed?.Invoke(new DocChange(DocChangeKind.All, GridRect.Empty));
    }

    // ---------------------------------------------------------------- forest

    public GridRect ForestSquares => new(
        (int)Math.Floor(Md.OffX / (double)Forest.CELL), (int)Math.Floor(Md.OffY / (double)Forest.CELL),
        (int)Math.Ceiling((Md.OffX + Md.Hx * 2) / (double)Forest.CELL) - 1, (int)Math.Ceiling((Md.OffY + Md.Hy * 2) / (double)Forest.CELL) - 1);

    public string ForestSetting => Md.SettingName is { Length: > 0 } s ? s : Md.Setting;

    ForestBlock? Block(int x, int y) { foreach (var b in Md.Forest.Blocks) if (b.X == x && b.Y == y) return b; return null; }

    public byte[]? ForestSquare(int x, int y) => Block(x, y) is { } b ? (byte[])b.B.Clone() : null;

    public void SetForestSquare(int x, int y, byte[]? items)
    {
        var b = Block(x, y);
        if (items == null) { if (b != null) Md.Forest.Blocks.Remove(b); return; }
        if (items.Length != Forest.ITEMS) throw new ArgumentException($"a forest square has {Forest.ITEMS} item bytes, not {items.Length}");
        if (b != null) { Array.Copy(items, b.B, Forest.ITEMS); return; }
        Md.Forest.Blocks.Add(new ForestBlock { X = x, Y = y, B = (byte[])items.Clone() });
        // (the map's square count covers the new square: ParseForest's W x H)
        Md.Forest.W = Math.Max(Md.Forest.W, x + 1); Md.Forest.H = Math.Max(Md.Forest.H, y + 1);
    }

    public void ForestChanged(GridRect squares)
    {
        if (squares.IsEmpty) return;
        int k = Forest.CELL / 2;                       // (16 samples per square)
        int si = (int)Math.Floor(-OriginX / Cell), sj = (int)Math.Floor(-OriginY / Cell);
        Changed?.Invoke(new DocChange(DocChangeKind.Forest, new GridRect(si + squares.I0 * k, sj + squares.J0 * k, si + (squares.I1 + 1) * k, sj + (squares.J1 + 1) * k)));
    }

    // ---------------------------------------------------------------- plants

    public IReadOnlyList<MapPlant> Plants => Md.Plants;
    public void InsertPlant(int index, MapPlant p) => Md.Plants.Insert(index, p);
    public void RemovePlantAt(int index) => Md.Plants.RemoveAt(index);

    public void PlantsChanged(double x0, double y0, double x1, double y1)
    {
        if (x1 < x0 || y1 < y0) return;
        Changed?.Invoke(new DocChange(DocChangeKind.Plants, new GridRect(
            (int)Math.Floor((x0 - OriginX) / Cell), (int)Math.Floor((y0 - OriginY) / Cell), (int)Math.Ceiling((x1 - OriginX) / Cell), (int)Math.Ceiling((y1 - OriginY) / Cell))));
    }
}
