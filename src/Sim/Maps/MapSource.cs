// Map sources: everything the game needs to build a world, from either
//   * the random jungle generator (MapGen.cs) - the original "skirmish" map of the remake, or
//   * an original ParaWorld map file (.ula, see Ula.cs) - multiplayer, campaign and community maps.
//
// A MapSource is:
//   Name, Setting ('Jungle' | 'Northland' | 'Savanna' | 'Icewaste' | 'Ashvalley' | caves)
//   Size        side of the square world in metres (terrain, navigation grid and fog of war cover it)
//   MeshCell    terrain mesh resolution (m) - the world's HeightField samples Height every MeshCell metres (2: the
//               original's grid, on every map)
//   Play        playable rectangle {X0, X1, Z0, Z1} - units and buildings stay inside
//   Water       water level (m) or null
//   Height(x,z) terrain height in metres
//   Walkable(x,z)  the map's pathfinder grid (original maps), null = not known (then SlopeWalkable)
//   Textures    ground texture files (4 or 8), Scales [[sx, sz], ...]
//   Splat(hf)   -> (x, z) => weights (array of Textures.Count)
//   DefaultCamera  the level's camera angle (radians, the map's DefaultCamera; the game's default 0.785)
//   Trees[]     {Model, X, Z, Rot, Scale, Wood, Inside, Stump, Timber}
//   Decor[]     {Model, X, Z, Rot, Q, Scale, Block, Sprites}  (Block = obstacle for units; Q = stored map quaternion:
//               tilted landscape pieces such as plateaus and cliffs)
//   Stones[]    {Model, X, Z, Rot, Amount}
//   Bushes[]    {Model, X, Z, Rot, Amount}
//   Fish[]      {Model, X, Z, Amount}         (food in the water, for fishing boats)
//   Animals[]   {Species, X, Z, Home:[x, z], Swim}            single animals placed on the map
//   Nests[]     {Species, X, Z, Max, Rate, Amount, Swim, Start[]}  respawning nests (Start = pre-spawned animals)
//   Starts[]    [x, z] start locations; Bases[] = the two used by this skirmish
//   Models      the model names the map needs (loaded on top of the tribes' models), in the order first used
//   Origin      original maps: [ox, oy] with game x = map x - ox, game z = map y - oy (the game's map space is
//               left-handed: x east, y SOUTH, z up - docs/MAP_FORMAT.md; the world is right-handed with -z north)
// Game coordinates: x east, z south, y up, (0, 0) = centre of the world.
//
// What the JS source needs from the rules and the asset manifest comes through IMapRules and a model test (hasModel),
// so this file does not depend on the Rules port.
// Ported from remake/src/game/maps/source.js (pw-exporter 98c7703); weatherProgram from game/systems/landmarks.js.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

/// <summary>What a map source asks of the rules (the JS `D`: game/rules.js Rules). Names are looked up as the JS does.</summary>
public interface IMapRules
{
    /// <summary>D.classGfx(name): the model named in the class file (lower-cased key), or null</summary>
    string? ClassGfx(string name);
    /// <summary>D.script(name): the script class ("CFishShoal", "CLakeOfHealing" ...), or null</summary>
    string? Script(string name);
    /// <summary>D.resourceValue(section, name): the value of settings/Resources.txt when above 0, else null</summary>
    double? ResourceValue(string section, string name);
    /// <summary>D.exists(name): a class of the tech tree (StartTT Objects; case as given)</summary>
    bool Exists(string name);
    /// <summary>D.stats(name, 1, null)?.gfx: the model of a class's first level, or null</summary>
    string? StatsGfx(string name);
    /// <summary>D.extra.nests[nest class lower-cased]: the species a nest spawns when it has no spawn_type, or null</summary>
    string? NestSpecies(string nestClassLower);
}

/// <summary>the skirmish / campaign setup's part a map source reads (the JS cfg)</summary>
public sealed class MapSourceConfig
{
    /// <summary>random map: the number of players (the JS cfg.slots without the closed ones); 0 = 2</summary>
    public int Players;
    /// <summary>random map: the seed (0 = 1234)</summary>
    public double Seed;
    /// <summary>campaign missions place their units, buildings and addressed scenery themselves (campaign/setup.js):
    /// the names of the map's objects to leave out. Non-null also means "no players' objects" (owned / houses).</summary>
    public ISet<string>? SkipObjects;
    /// <summary>no forest blocks</summary>
    public bool NoForest;
}

public sealed class PlayArea
{
    public double X0, X1, Z0, Z1;
    public bool Inside(double x, double z, double m = 0) => x > X0 + m && x < X1 - m && z > Z0 + m && z < Z1 - m;
}

public sealed class MapTree
{
    public string Model = "";
    public double X, Z, Rot, Scale = 1, Wood;
    public bool Inside, Big, Forest;
    public string? Stump, Timber;
}

public sealed class MapDecor
{
    public string Model = "";
    public double X, Z, Rot, Scale = 1;
    /// <summary>height of the object in the map (original maps), null = on the ground</summary>
    public double? Y;
    public double[]? Q;
    public bool Block, Sprites, Forest;
}

/// <summary>a stone, a fruit bush or a fish shoal</summary>
public sealed class MapResource
{
    public string? Model;
    public double X, Z, Rot, Amount;
}

public sealed class MapAnimal
{
    public string Species = "";
    public double X, Z;
    public double[] Home = new double[2];
    public bool Swim;
}

/// <summary>a box of a nest's area in game coordinates (an oval = the ellipse inscribed in it)</summary>
public sealed class AreaBox { public bool Oval; public double X0, Z0, X1, Z1; }

public sealed class NestStart { public string Species = ""; public bool Swim; public double Born; }

public sealed class MapNest
{
    public string Name = "", Species = "";
    public double X, Z, Max, Rate, Amount;
    public bool Swim, Grown;
    public List<AreaBox>? ActionArea, SafeArea;
    public List<AreaBox> HotspotArea = new();
    /// <summary>hotspot_timetable rows (at least 3 numbers each)</summary>
    public List<double[]> Table = new();
    public List<NestStart> Start = new();
}

public sealed class MapWell
{
    public string Model = "", Cls = "";
    public double X, Z, Y, Rot;
    public double[] Q = new double[4];
    public Dictionary<string, string> Attr = new();
}

public sealed class MapLake { public string Name = "", Region = "", Types = ""; public bool Heal; public double Amount; }
public sealed class MapItem { public string Cls = ""; public double X, Z, Rot; }
public sealed class MapItemSpawn { public double X, Z, Rot; public List<string> Items = new(); public bool Always, Random; }
public sealed class ItemPool { public List<string> Pool = new(); public double Max; }

public sealed class MapOwned
{
    public string Type = "", Cls = "";
    public double X, Z, Rot;
    public int? Owner;
    public int Level;
    public Dictionary<string, string> Attr = new();
}

public sealed class MapFlag { public string Name = "", Cls = "", Model = ""; public double X, Z, Rot; }

public sealed class MpSlot
{
    public List<string>? Tribes;
    public double? Team, PopMax;
    /// <summary>food, wood, stone, skulls (the map's "iron")</summary>
    public Dictionary<string, double> Res = new();
}

/// <summary>the map's multiplayer settings (Root/Base and Root/PlayerSettings)</summary>
public sealed class MpSettings
{
    public double MaxPlayers;
    public List<string> GameTypes = new();
    public bool LastManStanding, Multiplayer;
    public double DefenderTimer;
    /// <summary>per start slot ("0", "1" ...)</summary>
    public Dictionary<string, MpSlot> Slots = new();
}

public sealed class WeatherTrack { public double Dur, Type, Intensity, Wind, Strength; }
public sealed class WeatherProgram { public string Name = ""; public bool Loop; public List<WeatherTrack> Tracks = new(); }

/// <summary>Model names in the order first used (the JS Set).</summary>
public sealed class ModelSet : IEnumerable<string>
{
    readonly List<string> _order = new();
    readonly HashSet<string> _set = new();
    public string? Use(string? m) { if (!string.IsNullOrEmpty(m) && _set.Add(m)) _order.Add(m); return m; }
    public bool Contains(string m) => _set.Contains(m);
    public int Count => _order.Count;
    public IEnumerator<string> GetEnumerator() => _order.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _order.GetEnumerator();
}

public sealed class MapSource
{
    static readonly Logger L = Log.For("Maps.Source");

    public string Kind = "", Name = "", Setting = "Jungle";
    public Dictionary<string, string> Info = new();
    public string File = "";
    /// <summary>the original map read from the file (null for the random map)</summary>
    public MapData? Md;
    /// <summary>wall grid: tile centres at map x ≡ 4, y ≡ 4 (mod 8) -> game offsets (docs/spec/walls.md §2); null = (0, 0)</summary>
    public double[]? WallGrid;
    public double Size, MeshCell;
    public PlayArea Play = new();
    public double? Water;
    /// <summary>map (x, y) -> game (x - ox, y - oy); null for the random map. ox, oy = the map's centre in map
    /// coordinates (half the terrain extent for an original map; MapExtension: the extended map's centre)</summary>
    public double[]? Origin;
    /// <summary>half the terrain extent (m) of an original map ([W2 / 2, H2 / 2]: the game area is -Half .. +Half);
    /// equals Origin unless the map was extended (MapExtension); null for the random map</summary>
    public double[]? Half;
    /// <summary>terrain height at game (x, z)</summary>
    public Func<double, double, double> Height = (x, z) => 0;
    /// <summary>can the terrain at game (x, z) be walked on (the map's pathfinder grid); null = not known</summary>
    public Func<double, double, bool>? Walkable;
    /// <summary>a source's own slope limit (degrees) for SlopeWalkable; null = MAX_SLOPE</summary>
    public double? MaxSlope;
    public List<string> Textures = new();
    public List<double[]> Scales = new();
    public List<string>? FallbackTextures;
    /// <summary>ground material weights at game (x, z), one per texture; the random map's needs the world's height field</summary>
    public Func<HeightField?, Func<double, double, double[]>> Splat = _ => (x, z) => Array.Empty<double>();
    public double DefaultCamera = 0.785;
    public double StartTime = 12 * 3600;
    public string LevelName = "", Atmos = "";
    public Dictionary<string, bool>? AiOptions;
    public List<MapTree> Trees = new();
    public List<MapDecor> Decor = new();
    public List<MapResource> Stones = new(), Bushes = new(), Fish = new();
    public List<MapAnimal> Animals = new();
    public List<MapNest> Nests = new();
    public List<double[]> Starts = new(), Bases = new();
    public ModelSet Models = new();
    public bool Skirt;
    public List<MapWell> Wells = new();
    public List<MapLake> Lakes = new();
    /// <summary>the map's regions (MAP coordinates)</summary>
    public List<MapRegion> Regions = new();
    public WeatherProgram? Weather;
    public List<MapItem> Items = new();
    public List<MapItemSpawn> ItemSpawns = new();
    public ItemPool ItemPool = new();
    /// <summary>the start slot (HQ index) of each start location; null = none (the made-up pair of a map without two)</summary>
    public List<int?> StartSlots = new();
    public MpSettings Mp = new();
    public List<MapOwned> Owned = new();
    public List<MapFlag> Flags = new();
    /// <summary>the ambience's ground material at game (x, z): 0 .. 7, 8 = forest (null for the random map)</summary>
    public Func<double, double, int>? SoundMat;
    public Func<double, double, double>? Noise;
    /// <summary>the map's biome layer (Biomes.cs; null: the setting alone) and the biome weights at game (x, z), one per
    /// BiomeLayer.Biomes, summing to 1 (null without a layer). Drawn and heard only: the simulation does not read them.</summary>
    public BiomeLayer? BiomeLayer;
    public Func<double, double, double[]>? BiomeAt;
    /// <summary>original maps: the same source made again from another MapData with the same rules, setup, models and
    /// forest table (a map extended for co-op: CoopMapHook.Maps.cs); null for the random map</summary>
    public Func<MapData, MapSource>? Rebuild;

    /// <summary>the biome layer the map is drawn with (null: none) - the map's file's, or a map viewer's brush's</summary>
    public void UseBiomes(BiomeLayer? l)
    {
        BiomeLayer = l;
        if (Md != null) Md.Biomes = l;
        BiomeAt = l == null ? null : (x, z) => { var (mx, my) = ToMap(x, z); return l.At(mx, my); };
    }

    /// <summary>takes over every field of another source (the co-op map hook replaces the landscape of the source the
    /// mission session already holds)</summary>
    public void CopyFrom(MapSource o)
    {
        foreach (var f in typeof(MapSource).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (!f.IsInitOnly) f.SetValue(this, f.GetValue(o));
    }

    // ---------------------------------------------------------------- queries (game coordinates)
    /// <summary>game (x, z) -> map (x, y)</summary>
    public (double mx, double my) ToMap(double x, double z) => Origin == null ? (x, z) : (x + Origin[0], z + Origin[1]);
    /// <summary>map (x, y) -> game (x, z)</summary>
    public (double x, double z) ToGame(double mx, double my) => Origin == null ? (mx, my) : (mx - Origin[0], my - Origin[1]);
    /// <summary>water depth at game (x, z): water level - ground, 0 on land or without water</summary>
    public double WaterDepth(double x, double z) => Water is double w ? Math.Max(0, w - Height(x, z)) : 0;
    /// <summary>the walk test the world uses: the map's grid, else the slope rule (main.js buildWorld)</summary>
    public Func<double, double, bool> WalkTest(Func<double, double, double>? height = null) =>
        Walkable ?? SlopeWalkable(height ?? Height, MaxSlope ?? MAX_SLOPE);
    /// <summary>the region of that name (map regions; null when there is none)</summary>
    public MapRegion? Region(string name) { foreach (var r in Regions) if (r.Name == name) return r; return null; }
    /// <summary>is game point (x, z) in the named region (false when there is no such region)</summary>
    public bool RegionContains(string name, double x, double z) { var r = Region(name); if (r == null) return false; var (mx, my) = ToMap(x, z); return r.ContainsMap(mx, my); }

    // ---------------------------------------------------------------- the random jungle map
    public static MapSource GeneratedSource(MapSourceConfig? cfg = null)
    {
        var gen = new MapGen();
        gen.SetPlayers(cfg != null && cfg.Players > 0 ? cfg.Players : 2);
        var g = gen.Generate(cfg != null && cfg.Seed != 0 ? cfg.Seed : 1234);
        double h = gen.Size / 2;
        var models = new ModelSet();
        foreach (var t in g.Trees) { models.Use(t.Model); models.Use(t.Model + "_stump"); models.Use(t.Model + "_timber"); }
        foreach (var d in g.Decor) models.Use(d.Model);
        foreach (var m in new[] { "resource_stone_jun", "jungle_fruit_bush", "jungle_tree_01_stump", "jungle_tree_01_timber", "jungle_tree_02_timber" }
                     .Concat(new[] { 1, 2, 3, 4, 5 }.Select(n => $"jungle_tree_0{n}_stump"))) models.Use(m);
        foreach (var a in g.Animals) models.Use(a.Species.ToLowerInvariant());
        var S = new MapSource
        {
            Kind = "generated", Name = "Jungle (random)", Setting = "Jungle",
            Size = gen.World, MeshCell = 2, Play = new PlayArea { X0 = -h, X1 = h, Z0 = -h, Z1 = h }, Water = null,
            Height = gen.HeightFn,
            Textures = new() { "scape_2.jpg", "scape_0.jpg", "scape_3.jpg", "scape_4.jpg" },
            Scales = Enumerable.Range(0, 4).Select(_ => new[] { 1 / 24.0, 1 / 24.0 }).ToList(),
            FallbackTextures = new() { "grass_green.jpg", "grass_yellow.jpg", "dirt.jpg", "rock.jpg" },
            Splat = hf => gen.SplatFn(hf ?? throw new ArgumentNullException(nameof(hf), "the random map's ground needs the height field")),
            // (each tree class has its own _Stump and _Timber, jungle_vegetation.txt)
            Trees = g.Trees.Select(t => new MapTree
            {
                Model = t.Model, X = t.X, Z = t.Z, Rot = t.Rot, Scale = t.Scale, Wood = t.Wood, Inside = t.Inside,
                Stump = t.Model + "_stump", Timber = t.Model + "_timber", Big = !t.Model.Contains("_med_"),
            }).ToList(),
            Decor = g.Decor.Select(d => new MapDecor { Model = d.Model, X = d.X, Z = d.Z, Rot = d.Rot, Scale = d.Scale, Block = d.Block }).ToList(),
            Stones = g.Stones.Select(s => new MapResource { Model = "resource_stone_jun", X = s.X, Z = s.Z, Rot = s.Rot, Amount = 2000 }).ToList(),
            Bushes = g.Bushes.Select(b => new MapResource { Model = "jungle_fruit_bush", X = b.X, Z = b.Z, Rot = b.Rot, Amount = 150 }).ToList(),
            Animals = g.Animals.Select(a => new MapAnimal { Species = a.Species, X = a.X, Z = a.Z, Home = a.Home }).ToList(),
            Starts = gen.Bases.Select(b => new[] { b[0], b[1] }).ToList(), Bases = gen.Bases.Select(b => new[] { b[0], b[1] }).ToList(),
            Models = models,
            StartSlots = gen.Bases.Select((b, i) => (int?)i).ToList(),
            Mp = new MpSettings { MaxPlayers = MapGen.MAX_PLAYERS, GameTypes = new() { "Deathmatch" }, LastManStanding = false, Multiplayer = true, DefenderTimer = 0 },
            Skirt = true,
        };
        return S;
    }

    // the level's items (Root/Items: Pool - the item classes a random spawn takes from - and MaxItems)
    public static ItemPool ItemPoolOf(InfoNode? tree)
    {
        var it = tree?.Sub("Items");
        var pool = it?.Sub("Pool");
        return new ItemPool
        {
            Pool = (pool?.Subs ?? new List<InfoNode>()).Select(s => s.Name.ToLowerInvariant()).ToList(),
            Max = JsNum.Or(it?.Sub("MaxItems")?.Value, 0),
        };
    }

    // the ground material of the ambience (engine/atmo.js) at game (x, z): the 32 m square's most frequent 4 m ground
    // material (0 .. 7; outside the map 2), 8 where the square is a forest block with more than half of its 15 trees
    // standing (PWClient.exe CAtmoSoundMgr 0x4331b0, 0x6d87e0, 0x6d3e00; the map's forest as it was saved)
    public static Func<double, double, int> SoundMaterial(MapData md, double ox, double oy)
    {
        var MT = md.Mats;
        int mx = md.Mx, my = md.My;
        var forest = new HashSet<double>();
        if (md.Forest != null)
            foreach (var b in md.Forest.Blocks)
            {
                int n = 0;
                for (int k = 0; k < 15; k++) if ((b.B[k] & 0x60) == 0x40) n++;
                if (n / 15.0 > 0.5) forest.Add(b.Y * 100000.0 + b.X);
            }
        double gx0 = md.OffX, gy0 = md.OffY;               // (the grids' corner: 0 unless extended, MapExtension)
        return (x, z) =>
        {
            double fi = Math.Floor((x + ox) / 32), fj = Math.Floor((z + oy) / 32);
            if (forest.Contains(fj * 100000 + fi)) return 8;
            double ci = Math.Floor((x + ox - gx0) / 32), cj = Math.Floor((z + oy - gy0) / 32);
            if (ci < 0 || cj < 0 || ci * 8 >= mx || cj * 8 >= my) return 2;
            Span<int> n = stackalloc int[8];
            for (int j = 0; j < 8; j++) for (int i = 0; i < 8; i++)
            {
                double a = ci * 8 + i, b = cj * 8 + j;
                n[a < mx && b < my ? MT[(int)b * mx + (int)a] & 7 : 2]++;
            }
            int best = 0;
            for (int k = 1; k < 8; k++) if (n[k] > n[best]) best = k;
            return best;
        };
    }

    // ---------------------------------------------------------------- original maps
    static string Lc(string? s) => (s ?? "").ToLowerInvariant();

    // big landscape pieces that units can't walk through
    public static readonly Regex SOLID = new("rock|plateau|cliff|mountain|stone(?!_jun)|ruin|pillar|wall|temple|monument|archway|statue|head|coffin|stoneglobe|waterfall|berg|basalt|arc0|wreck|crater|dead_tree|log", RegexOptions.CultureInvariant);
    /// <summary>does a decoration of the map block the way: collision decorations (DCCO) and solid models do - not
    /// MIRAGE's Halicarnassus gate (CHCGate.OnInit: SetPlaceBlocker(false), RemovePFBlocker)</summary>
    public static bool DecorBlocks(string? type, string gfx, string? script) => script != "CHCGate" && (type == "DCCO" || SOLID.IsMatch(gfx));
    public static readonly Regex NO_PROP = new("^(startlocation|itemspawn|domination_flag|questmark|region)", RegexOptions.CultureInvariant);
    static readonly Regex Fish_ = new("fish", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Swim_ = new("swim", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex HealPassive = new("^fx_heal_passive$", RegexOptions.CultureInvariant);
    static readonly Regex VirtualProduce = new("^virtual_produce_unit$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex NotBig = new("_med_|bamboo|dead", RegexOptions.CultureInvariant);

    // maps without the editor's pathfinder grid (the sequel's, the random ones): land units walk where the ground is
    // not steeper than maxDeg - the largest rise over the edges and diagonals of the 2 m cell around (x, z). The
    // original editor's grid is close to a 22 degree rule (docs/MAP_FORMAT.md "PaFi"); the made maps have gentle hills
    // and dunes meant to be walked and cliffs of 40 degrees and more, so their rule is 32 degrees.
    public const double MAX_SLOPE = 32;
    public static Func<double, double, bool> SlopeWalkable(Func<double, double, double> height, double maxDeg = MAX_SLOPE)
    {
        double lim = SMath.Tan(maxDeg * Math.PI / 180);
        return (x, z) =>
        {
            double a = height(x - 1, z - 1), b = height(x + 1, z - 1), c = height(x + 1, z + 1), d = height(x - 1, z + 1);
            double m = Math.Max(Math.Max(Math.Abs(a - b), Math.Abs(b - c)), Math.Max(Math.Abs(c - d), Math.Abs(d - a))) / 2;
            return Math.Max(m, Math.Max(Math.Abs(a - c) / 2.83, Math.Abs(b - d) / 2.83)) <= lim;
        };
    }

    static readonly Regex PlayerKey = new(@"^Root/PlayerSettings/Player_([0-9]+)/([^\n\r\u2028\u2029]+)\z", RegexOptions.CultureInvariant);
    static readonly Regex ResKey = new(@"^Restrictions/Resources/(food|wood|stone|iron)\z", RegexOptions.CultureInvariant);
    static readonly Regex AiKey = new(@"^Root/AIOptions/([A-Za-z0-9_]+)\z", RegexOptions.CultureInvariant);

    static double? OrNull(string v) { double d = JsNum.ToNumber(v); return JsNum.Truthy(d) ? d : null; }

    // Root/Base (MaxPlayers, GameType "Deathmatch|Domination|", LastManStanding, MapType, DefenderTimer) and per start
    // slot Root/PlayerSettings/Player_<n>/Restrictions (Base/Tribes "Hu:Aje:", DefTeam, Resources/<res>,
    // Chars/Population/Max)
    public static MpSettings MpSettingsOf(MapData md)
    {
        var d = md.Description;
        string? Base(string k) => d.TryGetValue("Root/Base/" + k, out var v) ? v : null;
        var slots = new Dictionary<string, MpSlot>();
        foreach (var (k, v) in d)
        {
            var m = PlayerKey.Match(k);
            if (!m.Success) continue;
            if (!slots.TryGetValue(m.Groups[1].Value, out var sl)) slots[m.Groups[1].Value] = sl = new MpSlot();
            string key = m.Groups[2].Value;
            if (key == "Restrictions/Base/Tribes") sl.Tribes = v.Split(':').Where(s => s.Length > 0).ToList();
            else if (key == "Restrictions/Base/DefTeam") sl.Team = OrNull(v);
            else if (key == "Restrictions/Chars/Population/Max") sl.PopMax = OrNull(v);
            else
            {
                var r = ResKey.Match(key);
                if (r.Success) sl.Res[r.Groups[1].Value == "iron" ? "skulls" : r.Groups[1].Value] = JsNum.Or(v, 0);
            }
        }
        string gt = Base("GameType") is { Length: > 0 } g ? g : "Deathmatch|";
        string mt = Base("MapType") is { Length: > 0 } t ? t : "multiplayer";
        return new MpSettings
        {
            MaxPlayers = JsNum.Or(Base("MaxPlayers"), JsNum.Truthy(md.MaxPlayers) ? md.MaxPlayers : 2),
            GameTypes = gt.Split('|').Where(s => s.Length > 0).ToList(),
            LastManStanding = (Base("LastManStanding") ?? "undefined") == "1",
            Multiplayer = mt == "multiplayer",
            DefenderTimer = JsNum.Or(Base("DefenderTimer"), 0),
            Slots = slots,
        };
    }

    static Dictionary<string, bool>? AiOptionsOf(Dictionary<string, string>? desc)
    {
        var output = new Dictionary<string, bool>();
        if (desc != null) foreach (var (k, v) in desc) { var m = AiKey.Match(k); if (m.Success) output[m.Groups[1].Value] = v == "1"; }
        return output.Count > 0 ? output : null;
    }

    /// <summary>the level's weather program (Root/WeatherData: Name, Loop, Tracks with Duration (minutes), Type,
    /// Intensity, WindDir (0 .. 7), WindStrength); null when the level has none</summary>
    // Ported from remake/src/game/systems/landmarks.js weatherProgram.
    public static WeatherProgram? WeatherProgramOf(InfoNode? tree)
    {
        var wd = tree?.Sub("WeatherData");
        if (wd == null) return null;
        static string Val(InfoNode? n, string k) => n?.Sub(k)?.Value ?? "";
        double[] DIRS = { 0, 315, 270, 225, 180, 135, 90, 45 };
        var tracks = (wd.Sub("Tracks")?.Subs ?? new List<InfoNode>()).Select(e =>
        {
            double dir = JsNum.Or(Val(e, "WindDir"), 0);
            return new WeatherTrack
            {
                Dur = JsNum.Or(Val(e, "Duration"), 0) * 60, Type = JsNum.Or(Val(e, "Type"), 0), Intensity = JsNum.Or(Val(e, "Intensity"), 0),
                Wind = dir >= 0 && dir < DIRS.Length && dir == Math.Floor(dir) ? DIRS[(int)dir] : 0, Strength = JsNum.Or(Val(e, "WindStrength"), 0),
            };
        }).ToList();
        return new WeatherProgram { Name = Val(wd, "Name"), Loop = Val(wd, "Loop") == "1", Tracks = tracks };
    }

    /// <summary>a choppable tree of a model: the wood it gives (settings/Resources.txt, WOOD "&lt;model&gt;_timber": 300
    /// for most trees, 150 the medium jungle trees and larches, 100 bamboo and dead trees, 250 beach palms, 400 the Ash
    /// Valley trees, 500 the beech; 300 for a model without an entry), its stump and fallen-trunk models, and whether
    /// it is a big one</summary>
    public static MapTree TreeOf(string g, IMapRules? D, Func<string, bool> has) => new()
    {
        Wood = D?.ResourceValue("WOOD", g + "_timber") is double w && JsNum.Truthy(w) ? w : 300,
        Stump = has(g + "_stump") ? g + "_stump" : null, Timber = has(g + "_timber") ? g + "_timber" : null,
        Big = !NotBig.IsMatch(g),
    };

    /// <summary>D = the rules (class models, scripts, resources), md = the parsed map (Ula), cfg = skirmish / campaign
    /// setup, hasModel = the asset manifest has a model of that name, forest = forest.json (null: no forest trees)</summary>
    public static MapSource OriginalSource(MapData md, IMapRules D, MapSourceConfig? cfg, Func<string, bool> hasModel, ForestData? forestData = null)
    {
        double W2 = md.Hx * 2, H2 = md.Hy * 2;                    // terrain extent (m)
        double size = Math.Ceiling(Math.Max(W2, H2) / 16) * 16;
        // the grids' corner in map coordinates: 0 for an original map; an extended one (MapExtension, co-op) keeps the
        // old map's coordinates and its grids start at (OffX, OffY) <= 0. ox, oy = the centre in map coordinates.
        double gx0 = md.OffX, gy0 = md.OffY;
        double ox = gx0 + W2 / 2, oy = gy0 + H2 / 2;
        (double, double) toGame(double mx, double my) => (mx - ox, my - oy);
        bool has(string? m) => !string.IsNullOrEmpty(m) && hasModel(m);
        string? gfxOf(string? cls) { string c = Lc(cls); string g = D.ClassGfx(c) is { Length: > 0 } cg ? cg : c; return has(g) ? g : has(c) ? c : null; }
        string scriptOf(string cls) => D.Script(Lc(cls)) ?? "";

        // heights: bilinear on the 2 m grid
        int hx = md.Hx, hy = md.Hy;
        var HS = md.Heights;
        double height(double x, double z)
        {
            double fx = (x + ox - gx0) / 2, fy = (z + oy - gy0) / 2;
            fx = Math.Max(0, Math.Min(hx - 1.001, fx)); fy = Math.Max(0, Math.Min(hy - 1.001, fy));
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fy);
            double u = fx - i, v = fy - j;
            double a = HS[j * hx + i], b = HS[j * hx + i + 1], c = HS[(j + 1) * hx + i], d = HS[(j + 1) * hx + i + 1];
            return (a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v;
        }
        // ground materials on the 4 m grid -> 8 weights. The lookup point is shifted by a smooth noise (up to ~3 m) and
        // four taps are averaged, so material borders are irregular and round instead of following the 4 m grid.
        int mx = md.Mx, my = md.My;
        var MT = md.Mats;
        void tap(double[] w, double x, double z, double k)
        {
            double fx = Math.Max(0, Math.Min(mx - 1.001, (x + ox - gx0) / 4 - 0.5)), fy = Math.Max(0, Math.Min(my - 1.001, (z + oy - gy0) / 4 - 0.5));
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fy);
            double u = fx - i, v = fy - j;
            w[MT[j * mx + i] & 7] += (1 - u) * (1 - v) * k; w[MT[j * mx + i + 1] & 7] += u * (1 - v) * k;
            w[MT[(j + 1) * mx + i] & 7] += (1 - u) * v * k; w[MT[(j + 1) * mx + i + 1] & 7] += u * v * k;
        }
        Func<double, double, double[]> splat(HeightField? _) => (x, z) =>
        {
            var w = new double[8];
            double wx = x + 3 * (Maps.Noise.Fbm(x * 0.06 + 11.3, z * 0.06, 2, 2) - 0.5) * 2, wz = z + 3 * (Maps.Noise.Fbm(x * 0.06, z * 0.06 + 7.1, 2, 2) - 0.5) * 2;
            tap(w, wx - 1.5, wz - 1.5, 0.25); tap(w, wx + 1.5, wz - 1.5, 0.25); tap(w, wx - 1.5, wz + 1.5, 0.25); tap(w, wx + 1.5, wz + 1.5, 0.25);
            return w;
        };
        // playable area: the map minus a 32 m frame (the original keeps the border unwalkable)
        var play = new PlayArea { X0 = -W2 / 2 + 32, X1 = W2 / 2 - 32, Z0 = -H2 / 2 + 32, Z1 = H2 / 2 - 32 };
        var models = new ModelSet();
        string? use(string? m) => models.Use(m);
        string settingKey = md.Setting switch { "Jungle" => "jun", "Northland" => "nor", "Savanna" => "sav", "Icewaste" => "ice", "Ashvalley" => "ash", _ => "jun" };

        var S = new MapSource { Md = md };
        var starts = new List<(double x, double z, int? slot)>();
        // campaign missions place their units, buildings and addressed scenery themselves (game/campaign/setup.js)
        var skip = cfg?.SkipObjects;
        foreach (var o in md.Objects)
        {
            if (skip != null && skip.Contains(o.Name)) continue;
            var (x, z) = toGame(o.X, o.Y);
            string cls = o.Cls, script = scriptOf(cls);
            string? g = gfxOf(cls);
            double rot = Frame.MapHeading(o.Rot);
            string A(string k) => o.AttrOr(k, "");
            switch (o.Type)
            {
                case "SLOC": starts.Add((x, z, o.Owner ?? starts.Count)); break;
                case "TREE":
                {
                    if (g == null) break;
                    var t = TreeOf(g, D, has);
                    t.Model = use(g)!; t.X = x; t.Z = z; t.Rot = rot; t.Scale = 1; t.Inside = play.Inside(x, z, 2);
                    use(t.Stump); use(t.Timber);
                    S.Trees.Add(t);
                    break;
                }
                case "STON": if (g != null) S.Stones.Add(new MapResource { Model = use(g), X = x, Z = z, Rot = rot, Amount = JsNum.Or(A("hitpoints"), 2000) }); break;
                case "FRUI":
                    if (script == "CFishShoal" || Fish_.IsMatch(cls)) S.Fish.Add(new MapResource { Model = g != null ? use(g) : null, X = x, Z = z, Amount = Math.Min(JsNum.Or(A("hitpoints"), 1000), 2500) });
                    else if (g != null) S.Bushes.Add(new MapResource { Model = use(g), X = x, Z = z, Rot = rot, Amount = JsNum.Or(A("hitpoints"), 150) });
                    break;
                case "NEST":
                {
                    // Nest.usl: on the level's start ceil(advance_time / spawn_rate) animals (by spawn_max and
                    // spawn_amount) were born before - one spawn_rate after the other - and stand somewhere in the
                    // nest's action area; then one every spawn_rate s while fewer than spawn_max live (systems/animals.js
                    // wildNestsUpdate). The nest's areas are the map's regions "<nest>_ActionArea", "_SafeArea" and
                    // "_HotspotArea".
                    string? sp = A("spawn_type") is { Length: > 0 } st ? st : D.NestSpecies(Lc(cls));
                    if (string.IsNullOrEmpty(sp) || !D.Exists(sp)) break;
                    bool hasAttr(string k) => o.Attr.TryGetValue(k, out var v) && v != "";
                    double max = !hasAttr("spawn_max") ? 3 : Math.Max(0, JsNum.ToNumber(A("spawn_max")));
                    double rate = Math.Max(5, JsNum.Or(A("spawn_rate"), 60));
                    double amount = !hasAttr("spawn_amount") ? -1 : JsNum.ToNumber(A("spawn_amount"));
                    double advance = JsNum.Or(A("advance_time"), 0);
                    double n = advance > 0 && amount != 0 ? Math.Min(max, Math.Ceiling(advance / rate)) : 0;
                    if (amount >= 0) n = Math.Min(n, amount);
                    bool swim = Swim_.IsMatch(cls) || script == "CSwimmingNest";
                    List<AreaBox>? area(string suffix)
                    {
                        var r = md.Regions.FirstOrDefault(q => q.Name == o.Name + suffix);
                        return r?.Shapes.Where(q => q.Enabled).Select(q => { var (x0, z0) = toGame(q.X, q.Y); return new AreaBox { Oval = q.Oval, X0 = x0, Z0 = z0, X1 = x0 + q.W, Z1 = z0 + q.H }; }).ToList();
                    }
                    var table = A("hotspot_timetable").Split('\n').Select(l => l.Split('\t').Select(JsNum.ToNumber).ToArray())
                        .Where(e => e.Length >= 3 && e.All(double.IsFinite)).ToList();
                    var nest = new MapNest
                    {
                        Name = o.Name, Species = sp, X = x, Z = z, Max = max, Rate = rate, Amount = amount < 0 ? -1 : amount - n, Swim = swim,
                        Grown = JsNum.ToNumber(A("spawn_grown")) == 1 && o.Attr.ContainsKey("spawn_grown"),
                        ActionArea = area("_ActionArea"), SafeArea = area("_SafeArea"), HotspotArea = area("_HotspotArea") ?? new List<AreaBox>(), Table = table,
                    };
                    for (double k = 0; k < n; k++) nest.Start.Add(new NestStart { Species = sp, Swim = swim, Born = -advance + k * rate });
                    S.Nests.Add(nest);
                    use(Lc(D.StatsGfx(sp)));
                    break;
                }
                case "ANML":
                    if (o.Owner == null && D.Exists(cls)) { S.Animals.Add(new MapAnimal { Species = cls, X = x, Z = z, Home = new[] { x, z } }); use(Lc(D.StatsGfx(cls))); }
                    break;
                case "DCCO": case "DECO": case "VGTN": case "WOOD":
                    if (g != null && !NO_PROP.IsMatch(g)) S.Decor.Add(new MapDecor { Model = use(g)!, X = x, Z = z, Rot = rot, Q = o.Q, Scale = 1, Block = DecorBlocks(o.Type, g, script), Y = o.Z, Sprites = true });
                    break;
                // healing wells (CFountain) and lakes of healing / damage (CLakeOf*: invisible, a region of the map): the
                // game's setup makes them (systems/landmarks.js)
                case "FNTN": if (g != null) S.Wells.Add(new MapWell { Model = use(g)!, Cls = cls, X = x, Z = z, Y = o.Z, Rot = rot, Q = o.Q, Attr = o.Attr }); break;
                case "DMGL":
                    S.Lakes.Add(new MapLake { Name = o.Name, Region = A("region_name"), Heal = script == "CLakeOfHealing", Amount = JsNum.Or(A("damage_per_tick"), 0), Types = A("damage_types") });
                    break;
                // placed effects (CFX: the heal areas, the holy forest). A heal glow that was on a unit when the map was
                // saved (fx_heal_passive: FightingObj.HealMe, gone 2.5 s later) is not shown. (Plain scenery without a
                // type - CGameObj: sandbags, fishing nets - is not found by Ula.Objects, which looks for the type tag.)
                // items (artefacts) lying on the map, and the spawns that put the level's items there (systems/items.js)
                case "ITEM": S.Items.Add(new MapItem { Cls = Lc(cls), X = x, Z = z, Rot = rot }); use("item_treasure_chest"); break;
                case "ITSP":
                    S.ItemSpawns.Add(new MapItemSpawn
                    {
                        X = x, Z = z, Rot = rot, Items = A("spawn_items").Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList(),
                        Always = A("generate_always") is "1" or "true", Random = A("random_item") is "1" or "true",
                    });
                    use("item_treasure_chest");
                    break;
                case "CFXE":
                    if (g != null && !NO_PROP.IsMatch(g) && !HealPassive.IsMatch(g)) S.Decor.Add(new MapDecor { Model = use(g)!, X = x, Z = z, Rot = rot, Q = o.Q, Scale = 1, Block = SOLID.IsMatch(g), Y = o.Z, Sprites = true });
                    break;
                // the domination flags (DOFL; GameOverMgr: only in the Domination game type - systems/gameover.js)
                case "DOFL": S.Flags.Add(new MapFlag { Name = o.Name, Cls = Lc(cls), Model = use(g ?? "domination_flag")!, X = x, Z = z, Rot = rot }); break;
                // a skirmish map's buildings and units of a start slot (the defender's fortress ...): the player on that
                // start location gets them (ServerApp: the owner is the head quarter index); those of no player stay
                // nobody's (houses, fences, a pirate ship: owner null)
                case "BLDG": case "CHTR": case "VHCL": case "SHIP":
                    if (skip == null && D.Exists(cls))
                    {
                        S.Owned.Add(new MapOwned { Type = o.Type, Cls = Lc(cls), X = x, Z = z, Rot = rot, Owner = o.Owner, Level = o.Level + 1, Attr = o.Attr });
                        use(Lc(D.StatsGfx(cls)));
                    }
                    // a class the tech tree does not know (the editor's houses, Darwin's tree house): its model as
                    // scenery, as the campaign shows them (campaign/setup.js); the engine's helpers
                    // (Virtual_Produce_Unit) stay invisible
                    else if (skip == null && g != null && !VirtualProduce.IsMatch(cls) && !NO_PROP.IsMatch(g))
                        S.Decor.Add(new MapDecor { Model = use(g)!, X = x, Z = z, Rot = rot, Q = o.Q, Scale = 1, Block = true, Y = o.Z, Sprites = true });
                    break;
                default: break;                                       // units / buildings of campaign maps, item spawns
            }
        }
        // landscape decoration instances (IOMG): plants, rocks, plateaus
        foreach (var p in md.Plants)
        {
            string? g = gfxOf(p.Name);
            if (g == null || NO_PROP.IsMatch(g)) continue;
            var (x, z) = toGame(p.X, p.Y);
            S.Decor.Add(new MapDecor { Model = use(g)!, X = x, Z = z, Rot = Frame.MapHeading(p.Rot), Scale = 1, Block = SOLID.IsMatch(g), Y = p.Z, Sprites = true });
        }
        // forest blocks (Forest.cs): the trees and undergrowth of the map's 32 m forest squares. A forest tree is a tree
        // like the placed ones - the original turns it into a <Setting>_Tree_0N_Timber object when it is chopped.
        var kinds = forestData?.ForestKinds(md.SettingName is { Length: > 0 } sn ? sn : md.Setting);
        if (kinds != null && md.Forest != null && md.Forest.Blocks.Count > 0 && !(cfg != null && cfg.NoForest))
        {
            var fi = forestData!.ForestItems(md, (mx_, my_) => height(mx_ - ox, my_ - oy));
            var tk = kinds.Trees.Select(k =>
            {
                string? g = k != null ? gfxOf(k.Standard) : null;
                if (g == null) return null;
                var t = TreeOf(g, D, has);
                string? stump = k!.Stump is { Length: > 0 } ks ? gfxOf(ks) : null, timber = k.Timber is { Length: > 0 } kt ? gfxOf(kt) : null;
                t.Model = use(g)!; t.Stump = use(stump ?? t.Stump); t.Timber = use(timber ?? t.Timber);
                return t;
            }).ToList();
            foreach (var t in fi.Trees)
            {
                var k = t.Kind < tk.Count ? tk[t.Kind] : null;
                if (k == null) continue;
                var (x, z) = toGame(t.X, t.Y);
                S.Trees.Add(new MapTree { Model = k.Model, X = x, Z = z, Rot = Frame.MapHeading(t.Rot), Scale = 1, Wood = k.Wood, Inside = play.Inside(x, z, 2), Stump = k.Stump, Timber = k.Timber, Big = k.Big, Forest = true });
            }
            var dk = kinds.Deco.Select(n => { string? g = !string.IsNullOrEmpty(n) ? gfxOf(n) : null; return g != null && !NO_PROP.IsMatch(g) ? use(g) : null; }).ToList();
            foreach (var d in fi.Deco)
            {
                string? g = d.Kind < dk.Count ? dk[d.Kind] : null;
                if (g == null) continue;
                var (x, z) = toGame(d.X, d.Y);
                S.Decor.Add(new MapDecor { Model = g, X = x, Z = z, Rot = 0, Scale = 1, Block = false, Sprites = true, Forest = true });
            }
        }
        // resources need their depleted look
        foreach (var s in new[] { $"product_stone_{settingKey}" }) if (has(s)) use(s);
        // two start locations for the skirmish: the pair farthest apart
        int b0 = 0, b1 = 1;
        double bd = -1;
        for (int i = 0; i < starts.Count; i++) for (int j = i + 1; j < starts.Count; j++)
        {
            double dx = starts[i].x - starts[j].x, dz = starts[i].z - starts[j].z, d = Math.Sqrt(dx * dx + dz * dz);
            if (d > bd) { bd = d; b0 = i; b1 = j; }
        }
        if (starts.Count < 2) { starts.Clear(); starts.Add((-W2 / 4, H2 / 4, null)); starts.Add((W2 / 4, -H2 / 4, null)); }

        S.Kind = "original"; S.Name = md.Name; S.Setting = md.Setting; S.Info = md.Info; S.File = md.File;
        // wall grid: tile centres at map x ≡ 4, y ≡ 4 (mod 8) -> game x = mx - ox, z = my - oy (docs/spec/walls.md §2)
        S.WallGrid = new[] { (((4 - ox) % 8) + 8) % 8, (((4 - oy) % 8) + 8) % 8 };
        // the height samples every 2 m on every map: the original's own grid. (The JS took 4 m above 1400 m, for the
        // browser; on those maps - single_06, 07, 12, 13 and the co-op maps grown past 1400 m - fine ground the map's
        // objects stand on with their stored height, the steps and metal plateaus of mission 13's SEAS fortress, was
        // smoothed away and came through them. docs/TERRAIN.md "Terrain".)
        S.Size = size; S.MeshCell = 2; S.Play = play; S.Water = md.Water;
        S.Origin = new[] { ox, oy };
        S.Half = new[] { W2 / 2, H2 / 2 };
        S.Height = height;
        // can the terrain at game (x, z) be walked on (the map's pathfinder grid, Ula.Pathing); null = not known
        if (md.Walk is WalkGrid wg)
            S.Walkable = (x, z) =>
            {
                double i = Math.Floor((x + ox - wg.OffX) / 2), j = Math.Floor((z + oy - wg.OffY) / 2);
                return i >= 0 && j >= 0 && i < wg.W && j < wg.H && wg.Cells[(int)j * wg.W + (int)i] == 1;
            };
        S.Textures = Enumerable.Range(0, 8).Select(k => $"{md.Setting}/scape_{k}.jpg").ToList();
        S.Scales = Enumerable.Range(0, 8).Select(_ => new[] { 1 / 24.0, 1 / 24.0 }).ToList();
        S.Splat = splat; S.DefaultCamera = md.DefaultCamera; S.StartTime = md.StartTime;
        // the names the music picks its playlist by (engine/music.js): Root/Base/LevelName and material_atmos
        S.LevelName = md.Description.TryGetValue("LevelName", out var lvn) && lvn.Length > 0 ? lvn : md.Name;
        S.Atmos = md.Description.TryGetValue("material_atmos", out var atm) ? atm : "";
        // what the level tells the computer players (Root/AIOptions: walls, harbour, watermap, hunt_animals ...)
        S.AiOptions = AiOptionsOf(md.Description);
        S.Starts = starts.Select(s => new[] { s.x, s.z }).ToList();
        S.Bases = new List<double[]> { new[] { starts[b0].x, starts[b0].z }, new[] { starts[b1].x, starts[b1].z } };
        S.Models = models; S.Skirt = false;
        // healing wells, lakes, the map's regions (map coordinates: campaign/regions.js) and its weather program
        S.Regions = md.Regions;
        S.Weather = WeatherProgramOf(md.InfoTree);
        S.ItemPool = ItemPoolOf(md.InfoTree);
        // skirmish: the start slots (the HQ index of each start location), the map's multiplayer settings, the
        // players' objects and the domination flags
        S.StartSlots = starts.Select(s => s.slot).ToList();
        S.Mp = MpSettingsOf(md);
        S.SoundMat = SoundMaterial(md, ox, oy);
        S.Noise = (x, z) => Maps.Noise.Fbm(x * 0.05, z * 0.05, 3, 2);
        S.UseBiomes(md.Biomes);
        S.Rebuild = m => OriginalSource(m, D, cfg, hasModel, forestData);
        L.Debug($"{md.File}: size {size} m, {S.Trees.Count} trees, {S.Decor.Count} decor, {S.Stones.Count} stones, {S.Nests.Count} nests, {S.Starts.Count} starts");
        return S;
    }

    /// <summary>OriginalSource with the asset manifest's model names as a set</summary>
    public static MapSource OriginalSource(MapData md, IMapRules D, MapSourceConfig? cfg, IReadOnlySet<string> models, ForestData? forestData = null) =>
        OriginalSource(md, D, cfg, models.Contains, forestData);
}
