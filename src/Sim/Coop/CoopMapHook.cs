// The seam between the co-op core (this folder) and the map work (src/Sim/Maps/MapExtension.cs, branch port/coopmaps):
// the definition's `extend` (terrain added at the map's edges) and `resources` (extra resource objects) are the map
// work's. The core keeps them as plain JSON (CoopDefinition.Extend / Resources) and calls the two partial methods below
// at the right moments of MissionSession.Create; the map branch implements them in a file of its own:
//
//   namespace PwRemake.Sim.Coop;
//   public static partial class CoopMapHook
//   {
//       static partial void BeforeWorld(CoopMapContext ctx) { MapExtension.Apply(ctx); }
//       static partial void AfterWorld(CoopMapContext ctx) { MapExtension.PlaceResources(ctx); }
//   }
//
// Without that file the partial methods do not exist (C# removes the calls): the map stays as it is.
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Coop;

/// <summary>What the map work gets: the definition's map parts, the map source (before the world is built: change
/// heights, ground, plants, water, resources there) and, after the build, the world and the campaign.</summary>
public sealed class CoopMapContext
{
    public CoopDefinition Definition = null!;
    /// <summary>`extend` of the definition (null: none)</summary>
    public JsonObject? Extend => Definition.Extend;
    /// <summary>`resources` of the definition (null: none)</summary>
    public JsonArray? Resources => Definition.Resources;
    /// <summary>the map's landscape source (MapSource: heights, ground, forests, resource nodes, origin)</summary>
    public MapSource Map = null!;
    public Rules.Rules Rules = null!;
    public ModelManifest Models = null!;
    public GameConfiguration Config = null!;
    public Campaign.Campaign Campaign = null!;
    /// <summary>null before the world is built</summary>
    public World.World? World;
    /// <summary>the seats' start positions (game x, z) - where bases must fit (seats in use only)</summary>
    public List<(int Seat, double X, double Z)> Starts = new();
    /// <summary>the seats in use (1..3; an empty seat 3 is not here)</summary>
    public List<int> SeatsInUse = new();
    /// <summary>per-seat `extend` / `resources` of the seats in use (seats[n].extend, seats[n].resources): land and
    /// resources that exist only when that seat is played</summary>
    public List<(int Seat, JsonObject? Extend, JsonArray? Resources)> SeatParts = new();
    /// <summary>problems the map work reports (shown like the mission's load problems)</summary>
    public readonly List<string> Problems = new();
}

public static partial class CoopMapHook
{
    /// <summary>before WorldBuilder.Build: the map source can still be changed (extension, resources)</summary>
    public static void Before(CoopMapContext ctx) => BeforeWorld(ctx);
    /// <summary>after the world, its players and the seats are built</summary>
    public static void After(CoopMapContext ctx) => AfterWorld(ctx);

    static partial void BeforeWorld(CoopMapContext ctx);
    static partial void AfterWorld(CoopMapContext ctx);
}
