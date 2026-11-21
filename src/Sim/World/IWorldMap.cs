// What the world needs of the map it is played on (world.js, nav.js, the World options main.js passes in, and the
// map source's walk grid): its size, the playable rectangle, the ground height, the water level, the landscape
// setting, the time of day it starts at, where land units may walk. The map sources (src/Sim/Maps: original .ula
// maps, the remake's random maps, the sequel's maps) implement it; FlatMap is a flat test map.
//
// Also the hooks of the wall grid (game/wallmap.js, ported with the maps) and of building behaviours
// (systems/buildings.js) that entities.js and world.js call: small interfaces so World does not depend on their
// implementations.
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>the playable rectangle (x0 .. x1, z0 .. z1, metres)</summary>
public readonly record struct PlayRect(double X0, double X1, double Z0, double Z1)
{
    public bool Contains(double x, double z) => x >= X0 && x <= X1 && z >= Z0 && z <= Z1;
}

public interface IWorldMap
{
    /// <summary>side of the (square) map in metres; world coordinates run from -Size/2 to +Size/2</summary>
    double Size { get; }
    /// <summary>the playable rectangle (the map's camera bounds; units stay 2 m inside it)</summary>
    PlayRect Play { get; }
    /// <summary>ground height at (x, z)</summary>
    double Height(double x, double z);
    /// <summary>the water level (m), null: no water</summary>
    double? Water { get; }
    /// <summary>the landscape: Jungle, Northland, Savanna, Icewaste, Ashvalley</summary>
    string Setting { get; }
    /// <summary>the level's start time of day in seconds (0..86400), null: 12:00</summary>
    double? StartTime { get; }
    /// <summary>the map's day-night cycle (map.lua day_night: DayNight.cs), null: the original's clock</summary>
    DayNightSpec? DayNight => null;
    /// <summary>the origin of the 8 m wall grid ([x0, z0], WallMap)</summary>
    double[] WallGrid { get; }
    /// <summary>the map's own walk grid (original maps: the PaFi grid of the .ula, maps/ula.js pathing): where land
    /// units may walk; null: the slope rule (MaxSlope) decides</summary>
    Func<double, double, bool>? Walkable { get; }
    /// <summary>steepest walkable slope in degrees when there is no walk grid (maps/source.js MAX_SLOPE)</summary>
    double MaxSlope { get; }
    /// <summary>the kind of map ("ula", "random", "sequel" ...; the wall map's height rules)</summary>
    string Kind { get; }
}

/// <summary>A flat map for tests: height 0 everywhere (or a height function), optional water and blocked rectangles.</summary>
public sealed class FlatMap : IWorldMap
{
    public double Size { get; }
    public PlayRect Play { get; }
    public double? Water { get; init; }
    public string Setting { get; init; } = "Jungle";
    public double? StartTime { get; init; }
    public DayNightSpec? DayNight { get; init; }
    public double[] WallGrid { get; init; } = { 4, 4 };
    public double MaxSlope { get; init; } = 32;
    public string Kind { get; init; } = "flat";
    public Func<double, double, double>? HeightFn { get; init; }
    /// <summary>rectangles (x0, z0, x1, z1) where land units cannot walk - the map's walk grid of a test</summary>
    public List<(double X0, double Z0, double X1, double Z1)> Blocked { get; } = new();

    public FlatMap(double size = 256, double margin = 0)
    {
        Size = size;
        double h = size / 2 - margin;
        Play = new PlayRect(-h, h, -h, h);
    }

    public double Height(double x, double z) => HeightFn?.Invoke(x, z) ?? 0;

    public Func<double, double, bool>? Walkable => Blocked.Count == 0 ? null : (x, z) =>
    {
        foreach (var (x0, z0, x1, z1) in Blocked) if (x >= x0 && x <= x1 && z >= z0 && z <= z1) return false;
        return true;
    };
}

/// <summary>The 8 m wall grid of the world (game/wallmap.js: wall pieces, gates, towers, traps); ported with the maps.</summary>
public interface IWallMap
{
    /// <summary>the wall class table (gamedata "wallClasses"): class (lower case) -> [race, class, type]</summary>
    Dictionary<string, int[]> Classes { get; set; }
    (int I, int J) TileOf(double x, double z);
    /// <summary>a tile's height (quantised to 2 m)</summary>
    double ZOf(int i, int j);
    void Add(Building b);
    void Remove(Building b);
    void Update(int i, int j);
}

/// <summary>The behaviour of a building by its script class (systems/buildings.js: gates, traps, nests, warp gate ...).</summary>
public interface IBuildingBehaviour
{
    void Init(Building b, World w) { }
    void Built(Building b, World w) { }
    void Update(Building b, World w, double dt) { }
}

public static class MapRules
{
    /// <summary>maps/source.js slopeWalkable: walkable where the ground is not steeper than maxDeg degrees</summary>
    public static Func<double, double, bool> SlopeWalkable(Func<double, double, double> height, double maxDeg = 32)
    {
        double lim = SMath.Tan(maxDeg * Math.PI / 180);
        return (x, z) =>
        {
            double a = height(x - 1, z - 1), b = height(x + 1, z - 1), c = height(x + 1, z + 1), d = height(x - 1, z + 1);
            double m = Math.Max(Math.Max(Math.Abs(a - b), Math.Abs(b - c)), Math.Max(Math.Abs(c - d), Math.Abs(d - a))) / 2;
            return Math.Max(m, Math.Max(Math.Abs(a - c) / 2.83, Math.Abs(b - d) / 2.83)) <= lim;
        };
    }
}
