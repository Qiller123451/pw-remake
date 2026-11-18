// The typed result of reading an original map file (.ula, Ula.ParseUla): the JS remake's `MapData` object of
// maps/ula.js, field for field. Everything here is in MAP coordinates: x east 0 .. W, y SOUTH 0 .. H, z up (metres;
// left-handed, docs/MAP_FORMAT.md). MapSource (maps/source.js) turns it into what the world build needs, in game
// coordinates (x - W/2, z = y - H/2).
//
//   grids     heights: 2 m (Hx x Hy samples, sample (i, j) at map (2i, 2j)), materials: 4 m (Mx x My cells, cell
//             (i, j) covers map 4i .. 4i + 4), walk (PaFi): 2 m cells (cell (i, j) covers map 2i .. 2i + 2)
//   objects   the placed objects (Objs) with their attribute records, plants (IOMG), forest blocks (Frst),
//             regions (Rgns), the level info (LInf: key/values, description tree, preview picture)
// Ported from remake/src/game/maps/ula.js "MapData" (pw-exporter 98c7703).
using System;
using System.Collections.Generic;

namespace PwRemake.Sim.Maps;

/// <summary>A placed object of the map (chunk Objs): a four-letter type tag (BLDG, CHTR, ANML, TREE, STON, FRUI,
/// NEST, SLOC, DCCO, DECO, VGTN, WOOD, FNTN, DMGL, ITEM, ITSP, CFXE, DOFL, VHCL, SHIP ...), its name (the class with a
/// number: "Hu_Warrior_12"), the class (the name without the number), map position, rotation, owner, attributes.</summary>
public sealed class MapObject
{
    public string Type = "", Name = "", Cls = "";
    public double X, Y, Z;
    /// <summary>the heading in map space: -2 atan2(qz, qw), counter-clockwise (the engine applies the conjugate of the
    /// stored quaternion, Direct3D row vectors)</summary>
    public double Rot;
    /// <summary>the stored quaternion [x, y, z, w] (tilted landscape pieces need all of it: Frame.MapQuat)</summary>
    public double[] Q = new double[4];
    /// <summary>the owner byte (the player / start slot index); null = 0xff, nobody's</summary>
    public int? Owner;
    /// <summary>offset of the record in the Objs chunk</summary>
    public int At;
    /// <summary>a level (0-based) set by a campaign's setup; the map file does not give one (ula.js neither)</summary>
    public int Level;
    /// <summary>the object's attributes (hitpoints, spawn_type, spawn_max, region_name ...), in file order</summary>
    public Dictionary<string, string> Attr = new();

    public string AttrOr(string key, string def = "") => Attr.TryGetValue(key, out var v) ? v : def;
    public override string ToString() => $"{Type} {Name} ({X:0.#}, {Y:0.#}, {Z:0.#})";
}

/// <summary>A landscape decoration instance (chunk IOMG): plants, rocks, plateaus.</summary>
public sealed class MapPlant
{
    public string Name = "";
    public double X, Y, Z, Rot;
    public double[] Q = new double[4];
}

/// <summary>One shape of a region: the box x .. x + w, y .. y + h (map coordinates), or the ellipse inscribed in it.</summary>
public sealed class RegionShape
{
    public bool Oval, Enabled = true;
    public double X, Y, W, H;

    /// <summary>is map point (mx, my) inside (the box, or the inscribed ellipse) - campaign/regions.js containsMap</summary>
    public bool ContainsMap(double mx, double my)
    {
        if (mx < X || my < Y || mx > X + W || my > Y + H) return false;
        if (!Oval) return true;
        double u = (mx - X) / W * 2 - 1, v = (my - Y) / H * 2 - 1;
        return u * u + v * v <= 1;
    }
}

/// <summary>A named region of the map (chunk Rgns): a union of rectangles and ovals. The nests' areas are regions
/// "&lt;nest&gt;_ActionArea", "_SafeArea", "_HotspotArea" (Nest.usl); campaign triggers test objects against them.</summary>
public sealed class MapRegion
{
    public string Name = "";
    public List<RegionShape> Shapes = new();

    /// <summary>is map point (mx, my) in one of the enabled shapes (campaign/regions.js containsMap of a fresh region;
    /// the trigger engine's Region adds switching and moving on top)</summary>
    public bool ContainsMap(double mx, double my)
    {
        foreach (var s in Shapes) if (s.Enabled && s.ContainsMap(mx, my)) return true;
        return false;
    }
}

/// <summary>The pathfinder's land grid of the map editor (chunk PaFi): 2 m cells, 1 = land units can walk there (not on
/// steep slopes, not in the water; placed objects are not in it).</summary>
public sealed class WalkGrid
{
    public int W, H;
    /// <summary>row-major, Cells[j * W + i] for map x OffX + 2i .. + 2, y OffY + 2j .. + 2; 1 = walkable</summary>
    public byte[] Cells = Array.Empty<byte>();
    /// <summary>map coordinates of the grid's corner (0 for an original map; negative for a map extended to the north /
    /// west by MapExtension)</summary>
    public int OffX, OffY;

    /// <summary>the cell of map point (mx, my) is walkable (outside the grid: no)</summary>
    public bool WalkableMap(double mx, double my)
    {
        double i = Math.Floor((mx - OffX) / 2), j = Math.Floor((my - OffY) / 2);
        return i >= 0 && j >= 0 && i < W && j < H && Cells[(int)j * W + (int)i] == 1;
    }
}

/// <summary>A 32 m forest square of the map (chunk Frst): 31 item bytes - 15 trees, 16 undergrowth plants; bits 5-6
/// = state (2 = standing), bits 0-4 = amount (Forest.cs).</summary>
public sealed class ForestBlock
{
    public int X, Y;
    public byte[] B = new byte[31];
}

public sealed class ForestMap
{
    /// <summary>squares per side (map width / 32 ...)</summary>
    public int W, H;
    public List<ForestBlock> Blocks = new();
}

/// <summary>A node of the level info's description tree ("Root/Base/DefaultCamera" ...).</summary>
public sealed class InfoNode
{
    public string Name = "", Value = "";
    public List<InfoNode> Subs = new();
    /// <summary>the first child of that name, or null</summary>
    public InfoNode? Sub(string name) { foreach (var s in Subs) if (s.Name == name) return s; return null; }
}

/// <summary>The 200 x 200 preview picture of the map list (RGBA, rows from the top; drawn turned by 45 degrees).</summary>
public sealed class MapPreview
{
    public int W, H;
    public byte[] Rgba = Array.Empty<byte>();
}

/// <summary>An original map, as read from its .ula file (Ula.ParseUla).</summary>
public sealed class MapData
{
    // ---- Terr
    /// <summary>map size in metres (the terrain header; the grids below give the extent the game uses)</summary>
    public int W, H;
    /// <summary>map coordinates of the grids' corner (height sample (0, 0), material cell (0, 0), forest square (0, 0)
    /// lies at map (OffX, OffY)). 0 for every original map; a map extended to the north / west (MapExtension: co-op)
    /// keeps the old map's coordinates, so its new land lies at negative coordinates and OffX / OffY are negative.</summary>
    public int OffX, OffY;
    /// <summary>the map was extended for co-op (MapExtension.Apply): what was added (null for an original map)</summary>
    public MapExtensionInfo? Extension;
    /// <summary>the remake's biome layer of the map (Biomes.cs: the map's extension file, MapFiles); null: the setting
    /// alone, as the original</summary>
    public BiomeLayer? Biomes;
    /// <summary>water level (m; 16 on most maps)</summary>
    public double Water;
    /// <summary>height samples per side (2 m grid): Hx = blocks x * 16</summary>
    public int Hx, Hy;
    /// <summary>heights in metres, Heights[j * Hx + i] at map (2i, 2j); float32 as the JS Float32Array</summary>
    public float[] Heights = Array.Empty<float>();
    /// <summary>ground material cells per side (4 m grid): Mx = blocks x * 8</summary>
    public int Mx, My;
    /// <summary>ground material of each 4 m cell: index 0 .. 7 into the setting's 8 ground materials (scape_k)</summary>
    public byte[] Mats = Array.Empty<byte>();
    /// <summary>the setting number of the terrain header as a name (SETTINGS; 'Jungle' when unknown)</summary>
    public string TerrainSetting = "Jungle";

    // ---- LInf
    /// <summary>the level info's key/values (LevelName, MapName, Setting, MaxPlayers ...), in file order</summary>
    public Dictionary<string, string> Info = new();
    public string File = "";
    public MapPreview? Preview;
    /// <summary>the description tree flattened: "Root/Base/StartTime" -> value, plus each last path element alone
    /// ("StartTime") where no other key had that name first</summary>
    public Dictionary<string, string> Description = new();
    public InfoNode? InfoTree;
    /// <summary>the camera's angle the level starts with (radians; 0.785 by default)</summary>
    public double DefaultCamera = 0.785;
    /// <summary>the time of day the level starts at, seconds of the day (Ula.DayTime)</summary>
    public double StartTime;
    /// <summary>the ground texture set the remake has for the map (KNOWN_SETTINGS: TestSet -> Jungle ...)</summary>
    public string Setting = "Jungle";
    /// <summary>the setting by its own name (forest kinds are per setting)</summary>
    public string SettingName = "Jungle";
    public string Name = "";
    public double MaxPlayers = 2;

    // ---- the rest
    public List<MapObject> Objects = new();
    public List<MapPlant> Plants = new();
    public ForestMap Forest = new();
    /// <summary>the editor's land grid (PaFi), null when the map has none or it does not fit the terrain</summary>
    public WalkGrid? Walk;
    public List<MapRegion> Regions = new();

    // ---------------------------------------------------------------- queries (map coordinates)
    /// <summary>terrain height at map (mx, my): bilinear on the 2 m grid, clamped to the grid (maps/source.js height,
    /// tests/forest_blocks.mjs heightAt)</summary>
    public double HeightAtMap(double mx, double my)
    {
        double fx = Math.Max(0, Math.Min(Hx - 1.001, (mx - OffX) / 2.0)), fy = Math.Max(0, Math.Min(Hy - 1.001, (my - OffY) / 2.0));
        int i = (int)Math.Floor(fx), j = (int)Math.Floor(fy);
        double u = fx - i, v = fy - j;
        var HS = Heights;
        return (HS[j * Hx + i] * (1 - u) + HS[j * Hx + i + 1] * u) * (1 - v) + (HS[(j + 1) * Hx + i] * (1 - u) + HS[(j + 1) * Hx + i + 1] * u) * v;
    }
    /// <summary>the ground material (0 .. 7) of the 4 m cell at map (mx, my); -1 outside</summary>
    public int MaterialAtMap(double mx, double my)
    {
        double i = Math.Floor((mx - OffX) / 4.0), j = Math.Floor((my - OffY) / 4.0);
        if (i < 0 || j < 0 || i >= Mx || j >= My || Mats.Length == 0) return -1;
        return Mats[(int)j * Mx + (int)i] & 7;
    }
    /// <summary>the editor's walk grid at map (mx, my); null when the map has none</summary>
    public bool? WalkableMap(double mx, double my) => Walk?.WalkableMap(mx, my);
    /// <summary>water depth at map (mx, my): water level - ground, 0 on dry land</summary>
    public double WaterDepthMap(double mx, double my) => Math.Max(0, Water - HeightAtMap(mx, my));
    /// <summary>the region of that name (first one), or null</summary>
    public MapRegion? Region(string name) { foreach (var r in Regions) if (r.Name == name) return r; return null; }
    /// <summary>the regions that contain map point (mx, my)</summary>
    public List<MapRegion> RegionsAtMap(double mx, double my) { var l = new List<MapRegion>(); foreach (var r in Regions) if (r.ContainsMap(mx, my)) l.Add(r); return l; }
}
