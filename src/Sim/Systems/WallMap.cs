// The wall grid: a port of the engine's WallMap ("URS.Shared.WallMap", the same code in PWServer.exe and
// PWClient.exe; reverse-engineered in _notes/walls_re.md, docs/spec/walls.md). Addresses are PWServer's.
//
//   * Tiles are 8 m squares (tile = trunc(map x / 8), centres at 4 mod 8 of the map). Every tile holds one 16-bit
//     word (0x7dcb70): owner, race, wall class, gate flag, tower class, a blocked bit, built bits, the dock entrance
//     bits - and a separate trap bit. Wall pieces never rotate; every wall-map object stands at the tile centre at a
//     height quantised to 2 m ("level": 0x7dc7f0).
//   * Directions d (engine tables 0xa89560 / 0xa89580; game x east, z south = map y):
//       d 0 NW (-1,-1)  1 N (0,-1)  2 NE (1,-1)  3 E (1,0)  4 SE (1,1)  5 S (0,1)  6 SW (-1,1)  7 W (-1,0)
//     odd d = straight, even d = diagonal, opposite = (d + 4) & 7.
//   * A line between two points is not a straight raster but the engine's shortest path over placeable tiles
//     (FindPath 0x7e0730) - all or nothing.
//   * A wall piece's look is a list of model parts chosen by name (BuildName 0x7df930): a hub (x0 diagonal family,
//     x1 straight family, x2 generic, x03..x47 the 135 degree bends) and one piece per direction ("3b": towards E,
//     arm end on the same level; a / c one level lower / higher; d a closed side; e / f arm / closed side of an x2).
//   * Pathfinding: each wall piece blocks with its model's pf boxes whose direction / hub bits are in its mask
//     (PFMask 0x7de2e0).
//
// Ported from remake/src/game/wallmap.js (pw-exporter 98c7703), 1:1. The world holds it as World.WallMap (IWallMap);
// World.Walls is this class.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>the objects standing on one tile of the wall grid</summary>
public sealed class WallTileObjs
{
    public Building? Wall, Gate, Tower, Trap;
    public Building? this[string kind]
    {
        get => kind switch { "wall" => Wall, "gate" => Gate, "tower" => Tower, "trap" => Trap, _ => null };
        set { switch (kind) { case "wall": Wall = value; break; case "gate": Gate = value; break; case "tower": Tower = value; break; case "trap": Trap = value; break; } }
    }
    public bool Empty => Wall == null && Gate == null && Tower == null && Trap == null;
}

/// <summary>a tile of a wall line (CalcWall): an own piece there already (Existing), a gate may go there later (GateOK)</summary>
public sealed class WallLineTile
{
    public int I, J;
    public bool Existing, GateOK;
    public WallLineTile Clone() => (WallLineTile)MemberwiseClone();
}

/// <summary>CalcWall's result: the class (race, class, type) and the tiles; a gate's axis d</summary>
public sealed class WallCalc
{
    public int Race, Cls, Type;
    public int D = -1;
    public List<WallLineTile> Tiles = new();
    /// <summary>a gate put into the gap a destroyed two-tile map gate left (GateGap): where it stands</summary>
    public GateGap? Gap;
}

/// <summary>The gap a destroyed two-tile gate of a campaign map left in its wall line (the old gate mode, walls_re.md
/// §1.4: centred on a tile edge or corner, tiles A and B = A + d, the wall ends at A - d and B + d, 12 m from its
/// centre): where it stood, so that a new gate goes back into the gap as the map's gate stood (the remake's rule -
/// with NewGates a one-tile gate needs wall pieces right next to its tile, which a two-tile gap does not have).</summary>
public sealed class GateGap
{
    public int AI, AJ, BI, BJ, D;
    /// <summary>the old gate's model origin and rotation</summary>
    public double X, Z, Rot;
}

public sealed class WallMap : IWallMap
{
    public const int TILE = 8;
    // engine directions
    public static readonly int[] DX = { -1, 0, 1, 1, 1, 0, -1, -1 };
    public static readonly int[] DZ = { -1, -1, -1, 0, 1, 1, 1, 0 };
    /// <summary>the remake's older direction order, kept for the wall walking and siege code (r: 0 E, 1 NE, 2 N, 3 NW,
    /// 4 W, 5 SW, 6 S, 7 SE): r = (3 - d) &amp; 7. Mask() returns arm bits in this order.</summary>
    public static readonly int[][] DIRS = { new[] { 1, 0 }, new[] { 1, -1 }, new[] { 0, -1 }, new[] { -1, -1 }, new[] { -1, 0 }, new[] { -1, 1 }, new[] { 0, 1 }, new[] { 1, 1 } };
    public static int DToR(int d) => (3 - d) & 7;
    /// <summary>object types of the wall class table (wallclassconfig_ex.txt; the client adds WC_Siege 4)</summary>
    public static class WT { public const int WALL = 0, GATE = 1, TOWER = 2, TRAP = 3, SIEGE = 4, ENTRANCE = 5; }
    public static readonly Dictionary<string, int> RACE = new() { ["Hu"] = 0, ["Aje"] = 1, ["Ninigi"] = 2, ["SEAS"] = 3 };
    const int NONE = -10;
    // the shipped default of the setting "Server/NewGates": gates are one tile, centred on a tile (0x7e2090)
    const bool NEW_GATES = true;
    // DAT_00aed570 (16; never written): start and end of a line must not be under it - 1
    const double SEA_LEVEL = 16;

    // the word of a tile (§1.2)
    const int OWNER = 0xf, RACE_SHIFT = 4, CLASS_MASK = 0x180, GATE_BIT = 0x200, TOWER_MASK = 0xc00, BLOCKED = 0x1000;
    static bool Occupied(int v) => (v & 0x8d80) != 0;
    static bool IsWall(int v) => (v & CLASS_MASK) != 0 && (v & GATE_BIT) == 0;
    static bool IsGate(int v) => (v & CLASS_MASK) != 0 && (v & GATE_BIT) != 0;
    static int TowerOf(int v) => (v & TOWER_MASK) >> 10;
    static int OwnerOf(int v) { int o = v & OWNER; return o == 0xf ? -1 : o; }
    static int RaceOfWord(int v) => (v >> RACE_SHIFT) & 7;
    static int ClassOfWord(int v) => (v & CLASS_MASK) >> 7;
    const int EMPTY = 0xf;                     // a tile without anything: owner none

    /// <summary>round to nearest, ties to even (the FPU's default rounding, "6.7553994e15" trick)</summary>
    public static double RoundEven(double v)
    {
        double f = Math.Floor(v), r = v - f;
        if (r > 0.5) return f + 1;
        if (r < 0.5) return f;
        return f % 2 == 0 ? f : f + 1;
    }

    // Shape of a connection set (0x7dc3d0): -1 invalid, 0 end / lone / irregular flat, 1 straight, 2 right angle,
    // 3 T, 4 135 degree bend, 5 cross
    static int Shape(int[] rel)
    {
        int c = 0, f = -1; bool flat = true;
        for (int d = 0; d < 8; d++)
        {
            if (rel[d] == NONE) continue;
            if (rel[d] < -1 || rel[d] > 1) return -1;
            if (rel[d] != 0) flat = false;
            if (f < 0) f = d;
            c++;
        }
        bool On(int d) => rel[d & 7] != NONE;
        if (c <= 1) return 0;
        if (c == 2)
        {
            if (On(f + 1) || On(f + 7)) return -1;
            if (On(f + 4)) return 1;
            if (On(f + 2) || On(f + 6)) return 2;
            if (On(f + 3) || On(f + 5)) return 4;
        }
        if (c == 3)
        {
            if ((On(f + 2) && On(f + 4)) || (On(f + 4) && On(f + 6)) || (On(f + 2) && On(f + 6))) return 3;
            if ((On(f + 2) && On(f + 5)) || (On(f + 6) && On(f + 3)) || (On(f + 3) && On(f + 5))) return 0;
        }
        if (c == 4 && On(f + 2) && On(f + 4) && On(f + 6)) return 5;
        return flat ? 0 : -1;
    }
    // a diagonal arm only stays when neither straight direction next to it is connected (0x7dc350)
    static void CornerRule(int[] rel)
    {
        foreach (var s in new[] { 1, 3, 5, 7 }) if (rel[s] != NONE) { rel[s - 1] = NONE; rel[(s + 1) & 7] = NONE; }
    }
    // regular patterns of BuildName / PFMask (count c, first connected f)
    static bool Regular(int[] rel)
    {
        int c = 0, f = -1;
        for (int d = 0; d < 8; d++) if (rel[d] != NONE) { if (f < 0) f = d; c++; }
        bool On(int d) => rel[d & 7] != NONE;
        if (c == 2) return true;
        if (c == 3) return (On(f + 2) && On(f + 4)) || (On(f + 4) && On(f + 6)) || (On(f + 2) && On(f + 6));
        if (c == 4) return On(f + 2) && On(f + 4) && On(f + 6);
        return false;
    }

    /// <summary>BuildName (0x7df930): the model part components of a wall piece for its connections, [] = not drawable</summary>
    public static List<string> BuildName(int[] rel)
    {
        int c = 0, f = -1; bool flat = true;
        for (int d = 0; d < 8; d++)
        {
            if (rel[d] == NONE) continue;
            if (rel[d] < -1 || rel[d] > 1) return new List<string>();
            if (rel[d] != 0) flat = false;
            if (f < 0) f = d;
            c++;
        }
        bool On(int d) => rel[d & 7] != NONE;
        string L(int d) => ((char)(98 + rel[d & 7])).ToString();          // a / b / c
        List<string> P() { var o = new List<string> { f % 2 == 0 ? "x0" : "x1" }; foreach (var k in new[] { 0, 2, 4, 6 }) { int d = (f + k) & 7; o.Add(On(d) ? d + L(d) : d + "d"); } return o; }
        List<string> X2(Func<int, string> conn, string open) { var o = new List<string> { "x2" }; for (int d = 0; d < 8; d++) o.Add(On(d) ? d + conn(d) : d + open); return o; }
        if (c == 0) return new List<string> { "x2", "0f", "1f", "2f", "3f", "4f", "5f", "6f", "7f" };
        if (c == 1) return flat ? X2(_ => "e", "f") : P();
        if (c == 2)
        {
            if (On(f + 1) || On(f + 7)) return new List<string>();                         // "sharp corner"
            if (On(f + 4) || On(f + 2) || On(f + 6)) return P();
            int b = On(f + 3) ? (f + 3) & 7 : (f + 5) & 7, a = Math.Min(f, b), z = Math.Max(f, b);
            return new List<string> { "x" + a + z, a + L(a), z + L(z) };
        }
        if (c == 3)
        {
            if ((On(f + 2) && On(f + 4)) || (On(f + 4) && On(f + 6)) || (On(f + 2) && On(f + 6))) return P();
            return X2(d => L(d), "d");
        }
        if (c == 4)
        {
            if (On(f + 2) && On(f + 4) && On(f + 6)) return P();
            return flat ? X2(_ => "e", "f") : new List<string>();                         // "four neighbours present but not flat"
        }
        return new List<string>();
    }

    // ---------------------------------------------------------------- state
    /// <summary>(x0, z0): the centre of tile (0, 0) in game coordinates (maps/source.js wallGrid)</summary>
    public readonly double X0, Z0;
    /// <summary>key -> {wall, gate, tower, trap} objects of the tile</summary>
    readonly Dictionary<long, WallTileObjs> _tiles = new();
    /// <summary>key -> tile word</summary>
    readonly Dictionary<long, int> _words = new();
    /// <summary>keys with the trap bit</summary>
    readonly HashSet<long> _traps = new();
    /// <summary>tile key -> the gap of a destroyed two-tile gate on that tile (both tiles of the gap)</summary>
    readonly Dictionary<long, GateGap> _gaps = new();
    /// <summary>[key, word, trap] records of a temporary change</summary>
    Dictionary<long, (int? V, bool T)>? _undo;
    /// <summary>the world gives the ground height, the bounds and the obstacles</summary>
    public World? World;
    /// <summary>class name -> [race, class, type] (wallclassconfig_ex.txt)</summary>
    public Dictionary<string, int[]> Classes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>solid scenery at a nav cell (the JS asked W.sceneryCell, which no world set: null)</summary>
    public Func<int, bool>? SceneryCell;
    double? _hOff;
    /// <summary>without a world (the map editor): the ground height at game (x, z)</summary>
    Func<double, double, double>? _ground;

    public WallMap(double x0 = 4, double z0 = 4) { X0 = x0; Z0 = z0; }

    /// <summary>a wall map without a world (the map editor: Detached + Put, then PartsOf / ZOf): the map's wall grid
    /// (MapSource.WallGrid), the class table, the ground height and the map's water level / kind for the engine's
    /// heights (as HeightAt does for a world). No obstacles: every tile is valid, no trees, the whole grid is on the map.</summary>
    public static WallMap Detached(double[]? grid, Dictionary<string, int[]> classes, Func<double, double, double> ground, double? water, string? kind)
    {
        var m = new WallMap(grid != null && grid.Length > 0 ? grid[0] : 4, grid != null && grid.Length > 1 ? grid[1] : 4) { Classes = classes, _ground = ground };
        m._hOff = water != null ? SEA_LEVEL - water.Value : (kind == "original" ? 0 : 30);
        return m;
    }

    /// <summary>a wall-map object written without a Building (the map editor): its tile(s) as Add writes them - an old
    /// two-tile gate (not centred on a tile) on both tiles. rot: the heading (Building.Rot). null: not a wall-map class.</summary>
    public (int I, int J, int Type, int D)? Put(string cls, int owner, double x, double z, double rot)
    {
        if (ClassOf(cls) is not { } c) return null;
        int type = c[2], race = type != WT.TRAP ? c[0] : 1;
        var (i, j) = TileOf(x, z);
        int d = type == WT.GATE ? GateDOf(rot) : -1;
        var old = type == WT.GATE ? OldGateTiles(x, z, d) : null;
        if (old != null) { (i, j) = old.Value.A; d = old.Value.D; }
        Write(i, j, owner & OWNER, race, c[1], type);
        if (old != null) Write(old.Value.B.I, old.Value.B.J, owner & OWNER, race, c[1], type);
        return (i, j, type, d);
    }

    /// <summary>the wall map of a world (world.js constructor: new WallMap(...opts.wallGrid), its world and class table)</summary>
    public static WallMap For(World w)
    {
        var g = w.Map.WallGrid;
        var m = new WallMap(g != null && g.Length > 0 ? g[0] : 4, g != null && g.Length > 1 ? g[1] : 4) { World = w, Classes = ClassTable(w.Data) };
        w.WallMap = m;
        return m;
    }

    /// <summary>the wall class table (gamedata "wallClasses", pipeline/build_data.py wall_classes); older data: every
    /// wall-placer class by its kind, class 1 (world.js wallClassTable)</summary>
    public static Dictionary<string, int[]> ClassTable(Rules.Rules D)
    {
        var t = D.Extra.WallClasses;
        if (t.Count > 0) return t;
        var o = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var name in D.IndexOrder)
        {
            var ix = D.Index[name];
            if (ix.Type != "BLDG") continue;
            var d = D.Def(name, null);
            if (d?.WallKind != null && KIND_TYPE.TryGetValue(d.WallKind, out var ty)) o[name.ToLowerInvariant()] = d.WallKind == "trap" ? new[] { 1, 0, 3 } : new[] { RACE.TryGetValue(ix.Tribe, out var r) ? r : 0, 1, ty };
        }
        return o;
    }

    // ---------------------------------------------------------------- grid
    public (int I, int J) TileOf(double x, double z) => ((int)Math.Floor((x - X0 + 4) / TILE), (int)Math.Floor((z - Z0 + 4) / TILE));
    public (double X, double Z) Centre(int i, int j) => (X0 + i * TILE, Z0 + j * TILE);
    public (double X, double Z) Snap(double x, double z) { var (i, j) = TileOf(x, z); return Centre(i, j); }
    static long Key(int i, int j) => (long)(i + 32768) * 65536 + (j + 32768);
    public WallTileObjs? At(int i, int j) => _tiles.TryGetValue(Key(i, j), out var t) ? t : null;
    /// <summary>the map (0x7e2010: (scape size + 7) >> 3 tiles): the tiles whose square lies inside the world</summary>
    public bool OnMap(int i, int j)
    {
        var W = World;
        if (W == null) return true;
        double h = W.Map.Size / 2; var (cx, cz) = Centre(i, j);
        return cx - 4 >= -h && cx + 4 <= h && cz - 4 >= -h && cz + 4 <= h;
    }
    public int Word(int i, int j) => _words.TryGetValue(Key(i, j), out var v) ? v : EMPTY;
    public bool Trap(int i, int j) => _traps.Contains(Key(i, j));
    void SetWord(int i, int j, int v) { long k = Key(i, j); if (_undo != null) Record(k); if (v == EMPTY) _words.Remove(k); else _words[k] = v; }
    void SetTrap(int i, int j, bool on) { long k = Key(i, j); if (_undo != null) Record(k); if (on) _traps.Add(k); else _traps.Remove(k); }
    // temporary changes (0x7e1950 / 0x7e1970 / 0x7e19d0)
    public void Begin() { _undo ??= new Dictionary<long, (int?, bool)>(); }
    void Record(long k) { if (!_undo!.ContainsKey(k)) _undo[k] = (_words.TryGetValue(k, out var v) ? v : null, _traps.Contains(k)); }
    void PutBack(Dictionary<long, (int? V, bool T)> recs)
    {
        foreach (var (k, (v, t)) in recs) { if (v == null) _words.Remove(k); else _words[k] = v.Value; if (t) _traps.Add(k); else _traps.Remove(k); }
    }
    public void Restore()
    {
        if (_undo == null) return;
        PutBack(_undo);
        _undo = null;
    }

    // ---------------------------------------------------------------- heights (0x7dc7f0, 0x7dc860)
    /// <summary>the engine's absolute heights (sea level 16 m; the original maps keep them). The remake's own maps lie
    /// lower: their heights are lifted so that the sea (or, without one, the ground) sits where the engine expects it</summary>
    public double HeightAt(double x, double z)
    {
        var W = World;
        if (W == null) return _ground != null ? _ground(x, z) + (_hOff ?? 0) : 30;
        _hOff ??= W.WaterLevel != null ? SEA_LEVEL - W.WaterLevel.Value : (W.MapKind == "original" ? 0 : 30);
        return W.Height(x, z) + _hOff.Value;
    }
    public int Level(int i, int j) { var (x, z) = Centre(i, j); return (int)RoundEven((HeightAt(x, z) - 1.7) / 2) + 1; }
    /// <summary>the arm end in direction d: the middle of the tile edge (straight) or the tile corner (diagonal)</summary>
    public int EdgeLevel(int i, int j, int d) { var (x, z) = Centre(i, j); return (int)RoundEven((HeightAt(x + DX[d] * 4, z + DZ[d] * 4) - 1.7) / 2) + 1; }
    /// <summary>where every wall-map object stands (0x7dd040 with flag 1): the tile centre at 2 x level</summary>
    public double ZOf(int i, int j) => 2 * Level(i, j) - (_hOff ?? 0);

    // ---------------------------------------------------------------- class table (0x7de880)
    public int[]? ClassOf(string? name)
    {
        var lc = (name ?? "").ToLowerInvariant();
        return Classes != null && Classes.TryGetValue(lc, out var c) ? c : null;
    }
    /// <summary>the remake's overbuilding on (Weaker); off: the original's rule, every own piece "have" (the golden tests
    /// against the browser remake)</summary>
    public bool Overbuild = true;

    /// <summary>the real wall class of an object (its class table entry; the tile word cannot tell 3, 4 and 5 apart)</summary>
    public int ClassOfObj(Building? b) => b != null && ClassOf(b.Name) is { } c ? c[1] : 0;

    /// <summary>The remake's overbuilding (the original keeps every own piece of a line as "have"): the owner's wall piece
    /// on (i, j) when it is of the race and of a lower class than cls - a stronger wall line builds over it (the piece
    /// gives way to the new site as a gate's tile does, no refund: World.StartConstruction). Gates are never built over;
    /// equal or stronger pieces stay. null: none.</summary>
    public Building? Weaker(int i, int j, int owner, int race, int cls)
    {
        if (!Overbuild || !OnMap(i, j)) return null;
        int v = Word(i, j);
        if (!IsWall(v) || (owner != -1 && OwnerOf(v) != owner) || RaceOfWord(v) != race) return null;
        var w = At(i, j)?.Wall;
        if (w == null || !w.Alive || ClassOf(w.Name) is not { } c || c[2] != WT.WALL || c[0] != race) return null;
        return c[1] < cls ? w : null;
    }

    public int RaceOf(Player? owner) => owner != null && RACE.TryGetValue(owner.Tribe, out var r) ? r : -1;
    public int OwnerId(Player? owner) => owner != null ? owner.Id & 0xf : -1;

    // ---------------------------------------------------------------- writing a tile (0x7dcb70 / 0x7dcc40)
    public void Write(int i, int j, int owner, int race, int cls, int type)
    {
        int v = Word(i, j);
        int bas = (v & 0xff80) | ((race & 7) << RACE_SHIFT) | (owner & OWNER);
        // the word has 2 bits per class and 0 = nothing, but wallclassconfig_ex.txt has Class_0 .. Class_5: the engine
        // cut 4 -> 0 (a class-4 tower was not recorded: no ghost, the tile stayed free) and 5 -> 1. Classes above 3 are
        // stored as 3, as MIRAGE's wall-class patch of both exes does (ParaWorldToolkit_wip/mirage_wallclass_45); the
        // objects keep their real class, the tile only cannot tell 3, 4 and 5 apart.
        cls = Math.Min(cls, 3);
        if (type == WT.TRAP) { v = bas | BLOCKED; SetTrap(i, j, true); }
        else if (type == WT.ENTRANCE) v = bas | 0xe000;
        else if (type == WT.TOWER) v = (bas & ~TOWER_MASK) | ((cls & 3) << 10);
        else v = (bas & 0xfc7f) | ((cls & 3) << 7) | ((type & 1) << 9);
        SetWord(i, j, v);
    }
    void ClearPart(int i, int j, int type)
    {
        int v = Word(i, j);
        if (type == WT.TOWER) v &= ~(TOWER_MASK | 0x4000);
        else if (type == WT.TRAP) { v &= ~BLOCKED; SetTrap(i, j, false); }
        else if (type == WT.ENTRANCE) v &= ~0xe000;
        else v &= ~(CLASS_MASK | GATE_BIT | 0x2000);
        if ((v & 0x1d80) == 0) v = (v & ~OWNER) | 0xf;
        if ((v & ~(OWNER | (7 << RACE_SHIFT))) == 0 && (v & OWNER) == 0xf) v = EMPTY;
        SetWord(i, j, v);
    }
    void SetBlocked(int i, int j, bool on) { int v = Word(i, j); SetWord(i, j, on ? v | BLOCKED : v & ~BLOCKED); }

    // ---------------------------------------------------------------- the rules
    /// <summary>HasOwn (0x7dcd20)</summary>
    public bool HasOwn(int i, int j, int owner, int race, int type)
    {
        if (!OnMap(i, j)) return false;
        int v = Word(i, j);
        if (type == WT.TRAP) return Trap(i, j);
        if (!Occupied(v)) return false;
        bool mine = (owner == -1 || OwnerOf(v) == owner) && (race == -1 || RaceOfWord(v) == race);
        if (type == WT.TOWER) return TowerOf(v) != 0 && mine;
        if (type == WT.GATE || type == WT.SIEGE) return TowerOf(v) == 0 && mine;
        return (IsWall(v) || IsGate(v)) && mine;
    }
    /// <summary>Blocked (0x7dced0)</summary>
    public bool Blocked(int i, int j, int owner, int race, int type)
    {
        if (!OnMap(i, j)) return true;
        int v = Word(i, j);
        if (type == WT.TRAP && Trap(i, j)) return (v & BLOCKED) != 0 && OwnerOf(v) == owner;     // an enemy trap is hidden
        if ((v & BLOCKED) != 0) return true;
        if (type == WT.GATE) { if (TowerOf(v) != 0 || IsGate(v)) return true; if (!IsWall(v) && !NEW_GATES) return true; }
        if (type == WT.SIEGE) { if (TowerOf(v) != 0 || IsGate(v) || !IsWall(v)) return true; }
        if (!Occupied(v)) return false;
        if (type == WT.TRAP) return true;
        if (type == WT.TOWER && (IsGate(v) || TowerOf(v) != 0)) return true;
        if (type == WT.WALL && IsGate(v) && NEW_GATES) return true;
        return !((OwnerOf(v) == owner || owner == -1) && (RaceOfWord(v) == race || race == -1));
    }
    /// <summary>TileValid (0x7dc920): not both cross-shaped windows of 2 m cells completely unbuildable (the remake: the
    /// terrain's unwalkable and wet cells)</summary>
    public bool TileValid(int i, int j)
    {
        var W = World;
        if (W == null) return true;
        var bad = W.SteepCell; var wet = W.WetCell; var nav = W.Nav;
        if (bad == null && wet == null) return true;
        var (cx, cz) = Centre(i, j);
        bool Set(int a, int b) { int k = nav.Idx(cx - 4 + a * 2 + 1, cz - 4 + b * 2 + 1); return k < 0 || (bad != null && bad[k] != 0) || (wet != null && wet[k] != 0); }
        bool w1 = true, w2 = true;
        for (int a = 1; a < 4 && w1; a++) for (int b = 0; b < 5 && w1; b++) if (!Set(a, b)) w1 = false;
        for (int a = 0; a < 5 && w2; a++) for (int b = 1; b < 4 && w2; b++) if (!Set(a, b)) w2 = false;
        return !(w1 && w2);
    }
    /// <summary>standing trees within r (the forest layer count 0x8db7d0: distance^2 &lt; 2 r^2 + 0.1)</summary>
    public int Trees(double x, double z, double r)
    {
        var W = World;
        if (W == null) return 0;
        double R2 = 2 * r * r + 0.1;
        int n = 0;
        W.RHash.Query(x, z, Math.Sqrt(R2) + 1, t => { if (t.Alive && t.Type == "tree" && (t.Pos.X - x) * (t.Pos.X - x) + (t.Pos.Z - z) * (t.Pos.Z - z) < R2) n++; });
        return n;
    }
    /// <summary>Connections (0x7dd250): rel[d] = NONE or the arm end's height step in levels; null = invalid tile</summary>
    public int[]? Connections(int i, int j, int owner, int race, int type)
    {
        var rel = new int[8]; Array.Fill(rel, NONE);
        if (!TileValid(i, j)) return null;
        int L = Level(i, j);
        if (L <= 6) return null;
        for (int d = 0; d < 8; d++) if (HasOwn(i + DX[d], j + DZ[d], owner, race, type)) rel[d] = EdgeLevel(i, j, d) - L;
        CornerRule(rel);
        // an end piece that faces bad ground: a flat arm runs on into it
        int c = 0, f = -1;
        for (int d = 0; d < 8; d++) if (rel[d] != NONE) { if (f < 0) f = d; c++; }
        if (c == 1)
        {
            int o = (f + 4) & 7, he = EdgeLevel(i, j, o) - L;
            int i2 = i + 2 * DX[o], j2 = j + 2 * DZ[o], h2 = Level(i2, j2) - L;
            var (x2, z2) = Centre(i2, j2);
            bool good = Math.Abs(he) < 2 && Math.Abs(h2) < 3 && Math.Abs(h2 - he) < 2 && TileValid(i2, j2) && Trees(x2, z2, 10) < 1;
            if (!good) rel[o] = 0;
        }
        return Shape(rel) == -1 ? null : rel;
    }
    /// <summary>ShapeOK (0x7dd500)</summary>
    public bool ShapeOK(int i, int j, int owner, int race, int type)
    {
        if (!OnMap(i, j)) return false;
        if (type == WT.TOWER && 2 * Level(i, j) < 16) return false;
        int v = Word(i, j);
        bool own = (owner == -1 || OwnerOf(v) == owner) && (race == -1 || RaceOfWord(v) == race) && (v & 0xd80) != 0;
        if (!own && !Trap(i, j)) return true;
        return Connections(i, j, OwnerOf(v), RaceOfWord(v), type) != null;
    }
    /// <summary>Placeable (0x7de030): the tile written for a moment, then the shape of it and of its 8 neighbours</summary>
    public bool Placeable(int i, int j, int owner, int race, int cls, int type)
    {
        if (!OnMap(i, j) || Blocked(i, j, owner, race, type)) return false;
        long k = Key(i, j);
        int? v = _words.TryGetValue(k, out var vv) ? vv : null; bool t = _traps.Contains(k);
        Write(i, j, owner, race, cls, type);
        bool ok = ShapeOK(i, j, owner, race, type);
        for (int d = 0; ok && d < 8; d++) ok = ShapeOK(i + DX[d], j + DZ[d], owner, race, type);
        // (put the word back without touching an undo list)
        if (v == null) _words.Remove(k); else _words[k] = v.Value;
        if (t) _traps.Add(k); else _traps.Remove(k);
        return ok;
    }
    /// <summary>the obstacle scan (0x7e2400) over tiles [i0..i1] x [j0..j1], temporary (inside Begin() / Restore()):
    /// a tile is blocked by a standing tree within 2.83 m, more than 3 within 9.9 m, or when its centre lies inside an
    /// object that is no wall-map object (buildings, stones, fruit bushes, solid scenery) - or a trap</summary>
    public void ScanObstacles(int i0, int j0, int i1, int j1)
    {
        var W = World;
        if (W == null) return;
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++)
        {
            if (!OnMap(i, j) || (Word(i, j) & BLOCKED) != 0) continue;
            var (x, z) = Centre(i, j);
            bool b = Trees(x, z, 2) > 0 || Trees(x, z, 7) > 3;
            if (!b) b = ObjectAt(x, z);
            if (b) SetBlocked(i, j, true);
        }
    }
    /// <summary>is the point inside an object that is no wall-map object (or is a trap)?</summary>
    public bool ObjectAt(double x, double z)
    {
        var W = World!;
        bool hit = false;
        W.BHash.Query(x, z, 40, b =>
        {
            if (hit || !b.Alive || (b.Def!.WallKind != null && b.Def.WallKind != "trap")) return;
            if (World.InBoxes(b.Boxes, b.Rot, x, z, 0)) hit = true;
        });
        if (!hit) W.RHash.Query(x, z, 12, n => { if (!hit && n.Alive && n.Type != "tree" && n.Type != "timber" && Math.Sqrt((n.Pos.X - x) * (n.Pos.X - x) + (n.Pos.Z - z) * (n.Pos.Z - z)) < Math.Max(1, n.Radius != 0 ? n.Radius : 1)) hit = true; });
        if (!hit)
        {
            int k = W.Nav.Idx(x, z);
            // solid scenery (rocks, ruins, the campaign's props): its cells in the nav grid
            if (k >= 0 && SceneryCell != null && SceneryCell(k)) hit = true;
        }
        return hit;
    }

    // ---------------------------------------------------------------- CalcWall (0x7e26c0)
    /// <summary>owner: a player (or null = any); name: the class to place; start / end: game points; held: the tiles of
    /// the frozen legs of a Shift polyline (wall.tiles). -> the class and tiles, or null</summary>
    public WallCalc? CalcWall(Player? owner, string name, double sx, double sz, double ex, double ez, List<WallLineTile>? held = null)
    {
        var c = ClassOf(name);
        if (c == null) return null;
        int race = c[0], cls = c[1], type = c[2];
        int me = OwnerId(owner);
        var (si, sj) = TileOf(sx, sz);
        if (!OnMap(si, sj) || HeightAt(sx, sz) < SEA_LEVEL - 1) return null;   // ("start under water")
        var wall = new WallCalc { Race = race, Cls = cls, Type = type, Tiles = (held ?? new List<WallLineTile>()).Select(t => t.Clone()).ToList() };
        if (type == WT.GATE) return CalcGate(me, race, cls, si, sj, wall, name);
        var (ei, ej) = TileOf(ex, ez);
        if (type != WT.TOWER) { if (!OnMap(ei, ej) || HeightAt(ex, ez) < SEA_LEVEL - 1) return null; }
        int i0 = Math.Min(si, ei) - 3, i1 = Math.Max(si, ei) + 3, j0 = Math.Min(sj, ej) - 3, j1 = Math.Max(sj, ej) + 3;
        Begin();
        ScanObstacles(i0, j0, i1, j1);
        foreach (var t in wall.Tiles)
        {
            if (!Placeable(t.I, t.J, me, race, cls, type)) { Restore(); return null; }
            Write(t.I, t.J, me, race, cls, type);
        }
        if (!Placeable(si, sj, me, race, cls, type)) { Restore(); return null; }
        if (type == WT.TOWER)
        {
            Restore();
            var tiles = wall.Tiles.Where(t => t.I != si || t.J != sj).ToList();
            tiles.Add(new WallLineTile { I = si, J = sj, Existing = HasOwn(si, sj, me, race, WT.TOWER), GateOK = false });
            return new WallCalc { Race = race, Cls = cls, Type = type, Tiles = tiles };
        }
        List<(int I, int J)>? list;
        if (si == ei && sj == ej) list = new List<(int, int)> { (si, sj) };
        else list = FindPath(si, sj, ei, ej, me, race, cls, type);
        Restore();
        if (list == null) return null;
        var seen = new HashSet<(int, int)>(wall.Tiles.Select(t => (t.I, t.J)));
        foreach (var (i, j) in list)
        {
            // an own piece is "have" (no cost, not built) - unless the line is a stronger wall of its race: then the weaker
            // piece is built over (the remake's overbuilding, Weaker)
            bool ex2 = HasOwn(i, j, me, race, type == WT.TRAP ? WT.TRAP : WT.WALL) && !(type == WT.WALL && Weaker(i, j, me, race, cls) != null);
            if (seen.Contains((i, j))) { var t = wall.Tiles.First(x => x.I == i && x.J == j); t.Existing = ex2; continue; }
            seen.Add((i, j));
            wall.Tiles.Add(new WallLineTile { I = i, J = j, Existing = ex2, GateOK = false });
        }
        // where a gate could go later (the client's hint, CalcWall (C)0x4f8950): the client writes the line as race 0,
        // class 1 and asks GateFits for race 0 - whatever the tribe (writing the tribe's own race made Blocked() refuse
        // every tile of an Aje / Ninigi / SEAS line: no cyan tiles for them)
        if (type == WT.WALL && wall.Tiles.Count > 2)
        {
            Begin();
            foreach (var t in wall.Tiles) Write(t.I, t.J, me, 0, 1, WT.WALL);
            for (int k = 1; k < wall.Tiles.Count - 1; k++)
            {
                var a = wall.Tiles[k - 1]; var t = wall.Tiles[k]; int d = DirIndex(t.I - a.I, t.J - a.J);
                if (d >= 0) t.GateOK = GateFits(t.I, t.J, d, -1, 0);
            }
            Restore();
        }
        return wall;
    }
    public static int DirIndex(int dx, int dz) { for (int d = 0; d < 8; d++) if (DX[d] == dx && DZ[d] == dz) return d; return -1; }

    /// <summary>FindPath (0x7e0730): a shortest path (straight 1, diagonal 1.414) towards the end, over placeable tiles
    /// with the provisional wall written in, traced back from the end (the lowest direction index wins ties)</summary>
    List<(int I, int J)>? FindPath(int si, int sj, int ei, int ej, int me, int race, int cls, int type)
    {
        int spanX = Math.Abs(ei - si);
        int m = si == ei && sj == ej ? 1 : Math.Max(5, Math.Min(10, spanX / 4));
        int wi0 = Math.Min(si, ei) - m, wi1 = Math.Max(si, ei) + m, wj0 = Math.Min(sj, ej) - m, wj1 = Math.Max(sj, ej) + m;
        while (wi0 < wi1 && !OnMap(wi0, sj) && !OnMap(wi0, ej)) wi0++;
        while (wi1 > wi0 && !OnMap(wi1, sj) && !OnMap(wi1, ej)) wi1--;
        while (wj0 < wj1 && !OnMap(si, wj0) && !OnMap(ei, wj0)) wj0++;
        while (wj1 > wj0 && !OnMap(si, wj1) && !OnMap(ei, wj1)) wj1--;
        int ww = wi1 - wi0 + 1, wh = wj1 - wj0 + 1;
        bool InW(int i, int j) => i >= wi0 && i <= wi1 && j >= wj0 && j <= wj1;
        int Idx(int i, int j) => (j - wj0) * ww + (i - wi0);
        const float BIG = 1e7f, BLK = 2e7f, DIAG = 1.414f;
        var cost = new float[ww * wh];
        bool P(int i, int j) => Placeable(i, j, me, race, cls, type);
        for (int j = wj0; j <= wj1; j++) for (int i = wi0; i <= wi1; i++) cost[Idx(i, j)] = P(i, j) ? BIG : BLK;
        if (cost[Idx(si, sj)] == BLK || cost[Idx(ei, ej)] == BLK) return null;
        int sxs = Math.Sign(ei - si), sys = Math.Sign(ej - sj);
        var allow = new bool[8];
        for (int d = 0; d < 8; d++) allow[d] = (sxs != 0 && DX[d] == sxs) || (sys != 0 && DZ[d] == sys);
        cost[Idx(si, sj)] = 1;
        var queue = new List<(int I, int J)> { (si, sj) };
        int head = 0, budget = Math.Min((ww + wh) * 50, 2500);
        void WriteT(int i, int j) => Write(i, j, me, race, cls, type);
        while (head < queue.Count && queue.Count - head < ww * wh && budget-- > 0)
        {
            var (ci, cj) = queue[head++];
            // look-ahead: the provisional wall from cur back to the start
            Begin();
            var outer = _undo;
            _undo = new Dictionary<long, (int?, bool)>();
            WriteT(ci, cj);
            int pi = ci, pj = cj, steps = 0;
            while ((pi != si || pj != sj) && steps++ < budget)
            {
                float best = BIG; int ni = int.MinValue, nj = 0;
                for (int d = 0; d < 8; d++)
                {
                    if (!allow[(d + 4) & 7]) continue;
                    int i = pi + DX[d], j = pj + DZ[d];
                    if (InW(i, j) && cost[Idx(i, j)] < best && P(i, j)) { best = cost[Idx(i, j)]; ni = i; nj = j; }
                }
                if (ni == int.MinValue) break;
                pi = ni; pj = nj; WriteT(pi, pj);
            }
            for (int d = 0; d < 8; d++)
            {
                if (!allow[d]) continue;
                int i = ci + DX[d], j = cj + DZ[d];
                if (!InW(i, j) || cost[Idx(i, j)] >= BLK) continue;
                float c = (float)((double)cost[Idx(ci, cj)] + (d % 2 != 0 ? 1.0 : (double)DIAG));
                if (c < cost[Idx(i, j)] && P(i, j)) { cost[Idx(i, j)] = c; queue.Add((i, j)); }
            }
            // undo the look-ahead (only its own writes)
            var mine = _undo;
            _undo = outer;
            PutBack(mine);
        }
        if (cost[Idx(ei, ej)] >= BIG) return null;
        // the trace from the end, building the path as it goes
        var path = new List<(int, int)> { (ei, ej) };
        var outer2 = _undo;
        _undo = new Dictionary<long, (int?, bool)>();
        WriteT(ei, ej);
        int qi = ei, qj = ej;
        for (int k = 1; k <= 1000 && (qi != si || qj != sj); k++)
        {
            float best = BLK; int ni = int.MinValue, nj = 0;
            for (int d = 0; d < 8; d++)
            {
                int i = qi + DX[d], j = qj + DZ[d];
                if (InW(i, j) && P(i, j) && cost[Idx(i, j)] < best) { best = cost[Idx(i, j)]; ni = i; nj = j; }
            }
            if (ni == int.MinValue) break;
            qi = ni; qj = nj; path.Add((qi, qj)); WriteT(qi, qj);
        }
        var mine2 = _undo;
        _undo = outer2;
        PutBack(mine2);
        if (qi != si || qj != sj) return null;
        path.Reverse();
        return path;
    }

    // ---------------------------------------------------------------- gates (0x7dd600, 0x7e26c0 gate branch)
    static int[] Flank(int d) => d % 2 != 0 ? new[] { d + 2, d + 6 } : new[] { d + 1, d + 3, d + 5, d + 7 };
    /// <summary>GateFits: the gate tile empty or an own wall piece; wall pieces (any owner, not gates) on both sides along
    /// d; the flank tiles free; the two wing tiles at most 2 levels apart</summary>
    public bool GateFits(int i, int j, int d, int owner, int race)
    {
        if (Blocked(i, j, owner, race, WT.GATE)) return false;
        int ai = i + DX[d], aj = j + DZ[d], bi = i - DX[d], bj = j - DZ[d];
        if (!IsWall(Word(ai, aj)) || !IsWall(Word(bi, bj))) return false;
        foreach (var f in Flank(d)) if (Occupied(Word(i + DX[f & 7], j + DZ[f & 7]))) return false;
        if (Math.Abs(Level(ai, aj) - Level(bi, bj)) > 2) return false;
        return true;
    }
    WallCalc? CalcGate(int me, int race, int cls, int i, int j, WallCalc wall, string name)
    {
        // the gap of a destroyed two-tile map gate: a new gate goes back where that one stood
        if (GapAt(i, j) is { } g)
        {
            int gc = Math.Max(cls, Math.Max(ClassOfWord(Word(g.AI - DX[g.D], g.AJ - DZ[g.D])), ClassOfWord(Word(g.BI + DX[g.D], g.BJ + DZ[g.D]))));
            return new WallCalc { Race = race, Cls = gc, Type = WT.GATE, D = g.D, Gap = g, Tiles = new List<WallLineTile> { new() { I = g.AI, J = g.AJ, Existing = true }, new() { I = g.BI, J = g.BJ, Existing = true } } };
        }
        int d = 0;
        for (; d < 8; d++) if (GateFits(i, j, d, me, race)) break;
        if (d == 8) return null;
        // a gate takes the stronger wall class of its tiles
        int c = Math.Max(cls, Math.Max(ClassOfWord(Word(i, j)), ClassOfWord(Word(i + DX[d], j + DZ[d]))));
        return new WallCalc { Race = race, Cls = c, Type = WT.GATE, D = d, Tiles = new List<WallLineTile> { new() { I = i, J = j, Existing = true }, new() { I = i + DX[d], J = j + DZ[d], Existing = true } } };
    }
    /// <summary>the gate's rotation for axis d: the model's local x runs along d (the engine's -(d+5) pi/4 in its
    /// left-handed map space; the remake turns about +y with x east, z south)</summary>
    public static double GateRot(int d) => -SMath.Atan2(DZ[d], DX[d]);
    /// <summary>the wall pieces at both ends of a gate (GetGateConnectingWalls 0x491150)</summary>
    public List<Building> GateWings(int i, int j, int d)
    {
        var o = new List<Building>();
        foreach (var s in new[] { 1, -1 }) { var t = At(i + s * DX[d], j + s * DZ[d]); if (t?.Wall != null && t.Wall.Alive) o.Add(t.Wall); }
        return o;
    }

    /// <summary>the gap of a destroyed two-tile gate on tile (i, j) that a gate can go into now: both tiles of the gap
    /// empty (no wall piece, gate, tower, trap, blocked bit - a wall piece put into it makes it an ordinary line again,
    /// where the one-tile rule decides) and the wall pieces at both ends standing (any owner, as GateFits)</summary>
    public GateGap? GapAt(int i, int j)
    {
        if (!_gaps.TryGetValue(Key(i, j), out var g)) return null;
        bool Free(int a, int b) => OnMap(a, b) && (Word(a, b) & (0x8d80 | BLOCKED)) == 0 && !Trap(a, b);
        if (!Free(g.AI, g.AJ) || !Free(g.BI, g.BJ)) return null;
        if (!IsWall(Word(g.AI - DX[g.D], g.AJ - DZ[g.D])) || !IsWall(Word(g.BI + DX[g.D], g.BJ + DZ[g.D]))) return null;
        return g;
    }
    /// <summary>every remembered gap (whether a gate fits there now or not)</summary>
    public IEnumerable<GateGap> Gaps => _gaps.Values.Distinct();

    // ---------------------------------------------------------------- objects on the map (AddObj 0x7de950, RemoveObj 0x7dee80)
    // an old two-tile gate (the campaign maps: on a tile edge or corner, walls_re.md §1.4) -> [tile A, tile B, d] with
    // B = A + d, or null for a gate centred on a tile
    ((int I, int J) A, (int I, int J) B, int D)? OldGateTiles(Building b, int d) => OldGateTiles(b.Origin.X, b.Origin.Z, d);
    ((int I, int J) A, (int I, int J) B, int D)? OldGateTiles(double x, double z, int d)
    {
        var (ci, cj) = TileOf(x, z); var (cx, cz) = Centre(ci, cj);
        if (Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz)) < 1) return null;
        ((int, int), (int, int), int)? best = null; double bd = 1;
        foreach (var e in new[] { d, (d + 4) & 7, (d + 2) & 7, (d + 6) & 7, (d + 1) & 7, (d + 3) & 7, (d + 5) & 7, (d + 7) & 7 })
        {
            double ax = x - 4 * DX[e], az = z - 4 * DZ[e]; var (ai, aj) = TileOf(ax, az); var (tx, tz) = Centre(ai, aj);
            double err = Math.Sqrt((ax - tx) * (ax - tx) + (az - tz) * (az - tz));
            if (err < bd - 1e-6) { bd = err; best = ((ai, aj), (ai + DX[e], aj + DZ[e]), e); }
        }
        return best;
    }

    static readonly Dictionary<string, int> KIND_TYPE = new() { ["wall"] = WT.WALL, ["gate"] = WT.GATE, ["tower"] = WT.TOWER, ["trap"] = WT.TRAP };

    public void Add(Building b)
    {
        var c = ClassOf(b.Name);
        var (i, j) = TileOf(b.Origin.X, b.Origin.Z);
        var old = b.Def!.WallKind == "gate" ? OldGateTiles(b, b.GateD ?? GateDOf(b)) : null;
        if (old != null) { (i, j) = old.Value.A; b.GateD = old.Value.D; b.Tile2 = old.Value.B; }
        b.Tile = (i, j);
        // a gate in a remembered gap closes it (the gap is remembered again when this one goes)
        if (old != null && _gaps.TryGetValue(Key(i, j), out var gp)) { _gaps.Remove(Key(gp.AI, gp.AJ)); _gaps.Remove(Key(gp.BI, gp.BJ)); }
        long k = Key(i, j);
        if (!_tiles.TryGetValue(k, out var t)) t = new WallTileObjs();
        t[b.Def.WallKind!] = b;
        _tiles[k] = t;
        int? type = c != null ? c[2] : KIND_TYPE.TryGetValue(b.Def.WallKind!, out var kt) ? kt : null;
        if (type == null) return;
        int cls = c != null ? c[1] : 1, race = c != null && type != WT.TRAP ? c[0] : RaceOf(b.Owner);
        b.WallType = type;
        Write(i, j, OwnerId(b.Owner), race, cls, type.Value);
        if (old != null)
        {
            // the second tile of an old gate: a gate too (the pair rule gives the walls on both sides their arms into it),
            // no flank blocking
            var (i2, j2) = old.Value.B; long k2 = Key(i2, j2);
            if (!_tiles.TryGetValue(k2, out var t2)) t2 = new WallTileObjs();
            t2.Gate = b; _tiles[k2] = t2;
            Write(i2, j2, OwnerId(b.Owner), race, cls, type.Value);
            if (b.Built) SetReady(b);
            Refresh(i, j, type.Value, b);
            Refresh(i2, j2, type.Value, b);
            return;
        }
        if (b.Built) SetReady(b);
        // a gate centred on a tile: its flank tiles are blocked for good
        if (type == WT.GATE)
        {
            int d = b.GateD ?? GateDOf(b);
            b.GateD = d;
            foreach (var f in Flank(d)) SetBlocked(i + DX[f & 7], j + DZ[f & 7], true);
        }
        Refresh(i, j, type.Value, b);
    }

    public void Remove(Building b)
    {
        if (b.Tile == null) return;
        int? wt = b.WallType;
        if (b.Tile2 is var (i2, j2))
        {
            // the gap it leaves (a gate of its own goes back there: GapAt)
            if (b.Tile is var (ga, gb) && b.GateD is int gd)
            {
                var gap = new GateGap { AI = ga, AJ = gb, BI = i2, BJ = j2, D = gd, X = b.Origin.X, Z = b.Origin.Z, Rot = b.Rot };
                _gaps[Key(ga, gb)] = gap; _gaps[Key(i2, j2)] = gap;
            }
            long k2 = Key(i2, j2);
            if (_tiles.TryGetValue(k2, out var t2) && t2.Gate == b) { t2.Gate = null; if (t2.Empty) _tiles.Remove(k2); }
            if (wt != null) ClearPart(i2, j2, wt.Value);
            b.Tile2 = null;
            Refresh(i2, j2, wt ?? WT.WALL, b);
            var (ia, ja) = b.Tile.Value; long ka = Key(ia, ja);
            if (_tiles.TryGetValue(ka, out var ta) && ta.Gate == b) { ta.Gate = null; if (ta.Empty) _tiles.Remove(ka); }
            if (wt != null) ClearPart(ia, ja, wt.Value);
            b.Tile = null;
            Refresh(ia, ja, wt ?? WT.WALL, b);
            return;
        }
        var (i, j) = b.Tile.Value;
        long k = Key(i, j);
        if (_tiles.TryGetValue(k, out var t) && t[b.Def!.WallKind!] == b) { t[b.Def.WallKind!] = null; if (t.Empty) _tiles.Remove(k); }
        if (wt != null)
        {
            // the tile keeps what else stands on it (a tower on a wall piece, the wall piece under a tower)
            ClearPart(i, j, wt.Value);
            if (wt == WT.GATE && b.GateD != null) { int d = b.GateD.Value; foreach (var f in Flank(d)) SetBlocked(i + DX[f & 7], j + DZ[f & 7], false); }
        }
        b.Tile = null;
        Refresh(i, j, wt ?? WT.WALL, b);
    }

    /// <summary>WallClassChanged (0x48f3b0 -> AddObj): an object on the grid became another class of its race and type
    /// (World.WallClassChanged) - its tiles take the new class in place (owner, built bits, a tower on the tile kept), and
    /// they and their own neighbours get a tile update (the new model's parts and pathfinder boxes)</summary>
    public void ClassChanged(Building b)
    {
        if (b.Tile == null || b.WallType is not int type || ClassOf(b.Name) is not { } c) return;
        foreach (var tl in new[] { b.Tile, b.Tile2 })
        {
            if (tl is not var (i, j)) continue;
            Write(i, j, OwnerId(b.Owner), c[0], c[1], type);
            Refresh(i, j, type, b);
        }
    }

    int GateDOf(Building b) => GateDOf(b.Rot);
    static int GateDOf(double a)
    {
        // d from the gate's rotation: its local x along d
        double x = SMath.Cos(a), z = -SMath.Sin(a);
        int d = DirIndex((int)Math.Floor(x + 0.5), (int)Math.Floor(z + 0.5));
        return d >= 0 ? d : 3;
    }

    /// <summary>WallMap.SetReady: the built bits</summary>
    public void SetReady(Building b)
    {
        foreach (var tl in new[] { b.Tile, b.Tile2 })
        {
            if (tl is not var (i, j)) continue;
            int v = Word(i, j);
            if (b.WallType == WT.TOWER) SetWord(i, j, v | 0x4000);
            else if (b.WallType == WT.WALL || b.WallType == WT.GATE) SetWord(i, j, v | 0x2000 | (b.WallType == WT.GATE ? 0x4000 : 0));
        }
    }

    /// <summary>a tile changed: the wall pieces there and on its own neighbours get their parts and blocking again
    /// (towers do not refresh their neighbours: 0x7dce10)</summary>
    public void Refresh(int i, int j, int type = WT.WALL, Building? b = null)
    {
        Update(i, j);
        if (type == WT.TOWER) return;
        int v = Word(i, j), owner = b != null ? OwnerId(b.Owner) : OwnerOf(v), race = b != null && b.WallType != WT.TRAP ? RaceOfWord(v) : -1;
        for (int d = 0; d < 8; d++)
        {
            int ni = i + DX[d], nj = j + DZ[d], nv = Word(ni, nj);
            if ((IsWall(nv) || IsGate(nv)) && (owner == -1 || OwnerOf(nv) == owner)) Update(ni, nj);
        }
        if (type == WT.GATE && b != null && b.GateD != null)
        {
            int ci = i + DX[b.GateD.Value], cj = j + DZ[b.GateD.Value];
            for (int d = 0; d < 8; d++) { int ni = ci + DX[d], nj = cj + DZ[d], nv = Word(ni, nj); if (IsWall(nv) || IsGate(nv)) Update(ni, nj); }
        }
    }

    /// <summary>the tile update: a wall piece's parts and pathfinder mask, a gate's end pieces</summary>
    public void Update(int i, int j)
    {
        var t = At(i, j);
        if (t == null) return;
        if (t.Wall != null && t.Wall.Alive) t.Wall.SetParts(PartsOf(i, j), PfMask(i, j), Mask(i, j));
        if (t.Gate != null && t.Gate.Alive) t.Gate.SetGateEnds((t.Gate.GateD ?? 3) % 2 == 0);
    }

    /// <summary>the visual connections of the wall piece on (i, j) (GfxName 0x7e1730): the part list</summary>
    public List<string> PartsOf(int i, int j)
    {
        var rel = VisualRel(i, j);
        return rel != null ? BuildName(rel) : new List<string>();
    }
    int[]? VisualRel(int i, int j)
    {
        int v = Word(i, j);
        if (!(IsWall(v) || IsGate(v))) return null;
        var rel = Connections(i, j, OwnerOf(v), RaceOfWord(v), WT.WALL);
        if (rel == null) return null;
        if (NEW_GATES) for (int d = 0; d < 8; d++)
            {
                int ni = i + DX[d], nj = j + DZ[d];
                if (!IsGate(Word(ni, nj))) continue;
                bool pair = false;
                for (int e = 0; e < 8 && !pair; e++) if (IsGate(Word(ni + DX[e], nj + DZ[e]))) pair = true;
                if (!pair) rel[d] = NONE;
            }
        return rel;
    }
    /// <summary>PFMask (0x7de2e0): the pf box bits of a wall piece - arm d -> bit 5 + ((d + 7) &amp; 7), bit 15 the small hub
    /// box, bit 14 the big one (end pieces, lone posts, irregular junctions); an L corner also closes the diagonal
    /// between its arms. An illegal tile blocks nothing.</summary>
    public int PfMask(int i, int j)
    {
        int v = Word(i, j);
        if (!(IsWall(v) || IsGate(v))) return 0;
        var rel = VisualRel(i, j);           // (no box into a one-tile gate: its posts close the gap, the passage stays free)
        if (rel == null) return 0;
        var pf = new int[8]; Array.Fill(pf, NONE);
        for (int d = 0; d < 8; d++)
        {
            if (rel[d] != NONE) pf[d] = rel[d];
            else if (rel[(d + 7) & 7] != NONE && rel[(d + 1) & 7] != NONE) pf[d] = 0;
        }
        int m = 0;
        for (int d = 0; d < 8; d++) if (pf[d] != NONE) m |= 1 << ((d + 7) & 7);
        return (m | (Regular(pf) ? 0x400 : 0x600)) << 5;
    }
    /// <summary>the arm bits of the wall piece on (i, j) in the old direction order (wall walking, ladders, the AI)</summary>
    public int Mask(int i, int j)
    {
        var rel = VisualRel(i, j);
        if (rel == null) return 0;
        int m = 0;
        for (int d = 0; d < 8; d++) if (rel[d] != NONE && HasOwn(i + DX[d], j + DZ[d], -1, -1, WT.WALL)) m |= 1 << DToR(d);
        return m;
    }
    /// <summary>the object a neighbouring wall piece connects to (walls and gates of the owner; a tower only through the
    /// wall piece under it)</summary>
    public Building? Joint(int i, int j, Player? owner)
    {
        var t = At(i, j);
        if (t == null) return null;
        var o = t.Gate ?? t.Wall;
        return o != null && o.Alive && o.Owner == owner ? o : null;
    }
    /// <summary>the old straight raster (the AI's ring plans): a Chebyshev DDA from tile a to tile b</summary>
    public static List<(int I, int J)> Line((int I, int J) a, (int I, int J) b)
    {
        var (i0, j0) = a; var (i1, j1) = b;
        int n = Math.Max(Math.Abs(i1 - i0), Math.Abs(j1 - j0));
        var o = new List<(int, int)>();
        for (int k = 0; k <= n; k++) o.Add((i0 + (int)JsRound((i1 - i0) * (n != 0 ? (double)k / n : 0)), j0 + (int)JsRound((j1 - j0) * (n != 0 ? (double)k / n : 0))));
        return o;
    }
    static double JsRound(double v) => Math.Floor(v + 0.5);
}

public sealed partial class Building
{
    /// <summary>the wall grid: a gate's axis d, the second tile of an old two-tile gate, the object type written</summary>
    public int? GateD, WallType;
    public (int I, int J)? Tile2;
}

public sealed partial class World
{
    /// <summary>the wall grid as its class (World.WallMap is the interface the core holds)</summary>
    public WallMap? Walls => WallMap as WallMap;
    /// <summary>world.js constructor: every world has its wall grid (a map setup may replace it)</summary>
    void InitWallMap() => global::PwRemake.Sim.World.WallMap.For(this);
}
