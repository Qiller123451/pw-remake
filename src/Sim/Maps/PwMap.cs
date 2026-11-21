// The remake's map document (docs/MAP_FORMAT.md): everything a map file holds - and what the map editor will edit -
// in memory. PwMapReader reads it from a map folder or a .pwmap zip, PwMapWriter writes it, PwMapConvert makes it from
// an original map (.ula) and a mission folder of the old layout. The simulation reads a map through ToMapData(): the
// typed shape the map source (MapSource.OriginalSource) and the co-op extension (MapExtension) work on.
//
// Coordinates are the original's MAP coordinates (docs/MAPS.md): x east, y south, z up, metres from the map's north-west
// corner; the grids start at map (0, 0).
//
//   info, settings    the original's level info (key/values) and level settings tree (Root/Base, Root/PlayerSettings,
//                     Root/WeatherData, Root/AIOptions ...) - what the map list, the skirmish and the AI read
//   terrain           heights (2 m grid), ground materials (4 m grid) with the texture of each material, the walk grid
//                     (2 m cells), the remake's biome layer, forest blocks (32 m squares), plants (landscape decoration)
//   objects, regions  the placed objects and the named regions of the map
//   mission           a campaign mission's data (mission.json: menu fields, map_info, players), world (world.json),
//                     texts per language, triggers.lua, custom.lua - null on a plain map
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace PwRemake.Sim.Maps;

public sealed class PwMap
{
    public const string FormatName = "pw-remake-map";
    public const int FormatVersion = 1;
    /// <summary>the heights' default step: the original's own (1/128 m; Ula Terr), so converted heights are exact</summary>
    public const double DefaultHeightStep = 1 / 128.0;

    // ---------------------------------------------------------------- map.lua
    /// <summary>the map's name (MapData.Name: the level name, else the map name, else the file name)</summary>
    public string Name = "";
    /// <summary>optional: shown instead of the level settings' LevelName / Description / Author (a remake map's own)</summary>
    public string? Title, Description, Author;
    /// <summary>where the map was converted from: its id in setups ("Base/Maps/Multiplayer/multi_2_jun_001.ula",
    /// "Remake/frost_to_jungle.pwmap.json"); "" for a map made in the editor</summary>
    public string Source = "";
    /// <summary>the original file's name ("multi_2_jun_001.ula"; the loading screen and journal find their entries by it)</summary>
    public string File = "";
    /// <summary>the configuration the map needs ("MIRAGE": it places a mod's classes, sets its options or tribes - the map
    /// editor decides it when it saves: MapRequirement); "" = any. A list (skirmish, multiplayer, custom maps) of another
    /// configuration leaves the map out (MapFiles.RequirementProblem).</summary>
    public string Requires = "";
    /// <summary>the map size of the header (m); the grids give the extent the game uses (Hx * 2 ...)</summary>
    public int W, H;
    /// <summary>the ground texture set (Ula.KNOWN_SETTINGS), the level's own setting name (forest kinds), the terrain
    /// header's setting</summary>
    public string Setting = "Jungle", SettingName = "Jungle", TerrainSetting = "Jungle";
    public double Water = 16;
    /// <summary>seconds of the day the level starts at</summary>
    public double StartTime = 12 * 3600;
    /// <summary>map.lua day_night: the remake's day-night cycle (World/DayNight.cs); null: the original's clock</summary>
    public World.DayNightSpec? DayNight;
    /// <summary>the camera's angle at the start (radians)</summary>
    public double DefaultCamera = 0.785;
    public double MaxPlayers = 2;
    /// <summary>the original's level info key/values (LInf), in file order</summary>
    public Dictionary<string, string> Info = new();
    /// <summary>the level settings tree (the description tree of the original: Root/Base/..., Root/PlayerSettings/...)</summary>
    public InfoNode? Settings;
    /// <summary>only for a map whose settings tree was damaged: the flat key/values that were read</summary>
    public Dictionary<string, string>? FlatSettings;

    // ---------------------------------------------------------------- terrain
    /// <summary>height samples per side (2 m grid; sample (i, j) at map (2i, 2j))</summary>
    public int Hx, Hy;
    public float[] Heights = Array.Empty<float>();
    /// <summary>height = HeightMin + value * HeightStep for the 16-bit values of terrain/height.png</summary>
    public double HeightMin = 0, HeightStep = DefaultHeightStep;
    /// <summary>ground material cells per side (4 m grid)</summary>
    public int Mx, My;
    /// <summary>the material index of every 4 m cell (0 .. 7: into Materials)</summary>
    public byte[] Mats = Array.Empty<byte>();
    /// <summary>the ground texture of each material index ("Jungle/scape_0.jpg" ...: below assets/terrain/)</summary>
    public List<string> Materials = new();
    public WalkGrid? Walk;
    public BiomeLayer? Biomes;
    public ForestMap Forest = new();
    public List<MapPlant> Plants = new();

    // ---------------------------------------------------------------- objects, regions, preview
    public List<MapObject> Objects = new();
    public List<MapRegion> Regions = new();
    /// <summary>the map list's picture (minimap.png)</summary>
    public MapPreview? Preview;

    // ---------------------------------------------------------------- a mission
    /// <summary>mission.json (title, tribe, point_buy, map_info, players ...) - null on a plain map</summary>
    public JsonObject? Mission;
    /// <summary>world.json: objects, groups, regions, quests, question marks, variables, dialogues, sequences ...</summary>
    public JsonNode? World;
    /// <summary>texts per language ("uk" -> key -> text)</summary>
    public SortedDictionary<string, JsonObject> Texts = new(StringComparer.Ordinal);
    /// <summary>triggers.lua and custom.lua as text (null: none)</summary>
    public string? Triggers, Custom;
    /// <summary>other files of the map folder (Lua modules custom.lua requires ...): relative path -> bytes</summary>
    public SortedDictionary<string, byte[]> Files = new(StringComparer.Ordinal);

    public bool IsMission => Mission != null;

    /// <summary>the 8 ground textures of a setting (MapSource: "&lt;Setting&gt;/scape_k.jpg")</summary>
    public static List<string> DefaultMaterials(string setting) => Enumerable.Range(0, 8).Select(k => $"{setting}/scape_{k}.jpg").ToList();

    // ---------------------------------------------------------------- the level settings flattened
    /// <summary>the settings tree flattened as the .ula reader did ("Root/Base/StartTime" -> value, and each last path
    /// element alone where no other key had that name first), with the map's own title / description / author over it</summary>
    public Dictionary<string, string> DescriptionFlat()
    {
        var d = new Dictionary<string, string>();
        if (Settings != null)
        {
            void Walk(InfoNode n, string prefix)
            {
                string key = prefix.Length > 0 ? prefix + "/" + n.Name : n.Name;
                if (n.Value.Length > 0) d[key] = n.Value;
                foreach (var s in n.Subs) Walk(s, key);
            }
            Walk(Settings, "");
            foreach (var k in new List<string>(d.Keys))
            {
                string s = k.Substring(k.LastIndexOf('/') + 1);
                if (!d.ContainsKey(s)) d[s] = d[k];
            }
        }
        else if (FlatSettings != null) foreach (var (k, v) in FlatSettings) d[k] = v;
        if (Title != null) d["Root/Base/LevelName"] = Title;
        if (Description != null) d["Root/Base/Description"] = Description;
        if (Author != null) d["Root/Base/Author"] = Author;
        return d;
    }

    // ---------------------------------------------------------------- copies
    /// <summary>a deep copy (the editor's Test map snapshot, undo states): grids, objects, plants, regions, forest,
    /// settings, the mission's data and files - nothing shared with this map</summary>
    public PwMap Clone()
    {
        static InfoNode Tree(InfoNode n) { var c = new InfoNode { Name = n.Name, Value = n.Value }; foreach (var s in n.Subs) c.Subs.Add(Tree(s)); return c; }
        var m = (PwMap)MemberwiseClone();
        m.Info = new Dictionary<string, string>(Info);
        m.DayNight = DayNight?.Clone();
        m.Settings = Settings == null ? null : Tree(Settings);
        m.FlatSettings = FlatSettings == null ? null : new Dictionary<string, string>(FlatSettings);
        m.Heights = (float[])Heights.Clone(); m.Mats = (byte[])Mats.Clone(); m.Materials = Materials.ToList();
        m.Walk = Walk?.Clone();
        m.Biomes = Biomes == null ? null : new BiomeLayer
        {
            Biomes = Biomes.Biomes.ToList(), Cell = Biomes.Cell, X0 = Biomes.X0, Y0 = Biomes.Y0, W = Biomes.W, H = Biomes.H, Weights = (byte[])Biomes.Weights.Clone(),
            Remap = Biomes.Remap.ToDictionary(kv => kv.Key, kv => (int[])kv.Value.Clone(), StringComparer.OrdinalIgnoreCase),
        };
        m.Forest = new ForestMap { W = Forest.W, H = Forest.H, Blocks = Forest.Blocks.Select(b => new ForestBlock { X = b.X, Y = b.Y, B = (byte[])b.B.Clone() }).ToList() };
        m.Plants = Plants.Select(p => new MapPlant { Name = p.Name, X = p.X, Y = p.Y, Z = p.Z, Rot = p.Rot, Q = (double[])p.Q.Clone() }).ToList();
        m.Objects = Objects.Select(o => new MapObject { Type = o.Type, Name = o.Name, Cls = o.Cls, X = o.X, Y = o.Y, Z = o.Z, Rot = o.Rot, Q = (double[])o.Q.Clone(), Owner = o.Owner, At = o.At, Level = o.Level, Attr = new Dictionary<string, string>(o.Attr) }).ToList();
        m.Regions = Regions.Select(r => new MapRegion { Name = r.Name, Shapes = r.Shapes.Select(x => new RegionShape { Oval = x.Oval, Enabled = x.Enabled, X = x.X, Y = x.Y, W = x.W, H = x.H }).ToList() }).ToList();
        m.Preview = Preview == null ? null : new MapPreview { W = Preview.W, H = Preview.H, Rgba = (byte[])Preview.Rgba.Clone() };
        m.Mission = (JsonObject?)Mission?.DeepClone(); m.World = World?.DeepClone();
        m.Texts = new SortedDictionary<string, JsonObject>(Texts.ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value.DeepClone()), StringComparer.Ordinal);
        m.Files = new SortedDictionary<string, byte[]>(Files.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone()), StringComparer.Ordinal);
        return m;
    }

    // ---------------------------------------------------------------- MapData
    /// <summary>the simulation's shape of the map (the arrays and lists are shared, not copied)</summary>
    public MapData ToMapData()
    {
        var md = new MapData
        {
            W = W, H = H, Water = Water, Hx = Hx, Hy = Hy, Heights = Heights, Mx = Mx, My = My, Mats = Mats,
            TerrainSetting = TerrainSetting, Info = Info, File = File, Preview = Preview, Description = DescriptionFlat(),
            InfoTree = Settings, DefaultCamera = DefaultCamera, StartTime = StartTime, Setting = Setting, SettingName = SettingName,
            Name = Name, MaxPlayers = MaxPlayers, Objects = Objects, Plants = Plants, Forest = Forest, Walk = Walk, Regions = Regions,
            Biomes = Biomes, Materials = Materials.Count == 8 && Materials.SequenceEqual(DefaultMaterials(Setting)) ? null : Materials,
            Requires = Requires, DayNight = DayNight,
        };
        return md;
    }

    /// <summary>a document of a MapData: an original map read by the .ula reader (PwMapConvert), or a map the editor
    /// holds as MapData (its save: FromMapData, then PwMapWriter). The arrays and lists are shared, not copied.</summary>
    public static PwMap FromMapData(MapData md)
    {
        var m = new PwMap
        {
            Name = md.Name, File = md.File, W = md.W, H = md.H, Setting = md.Setting, SettingName = md.SettingName,
            TerrainSetting = md.TerrainSetting, Water = md.Water, StartTime = md.StartTime, DefaultCamera = md.DefaultCamera,
            MaxPlayers = md.MaxPlayers, Info = md.Info, Settings = md.InfoTree,
            Hx = md.Hx, Hy = md.Hy, Heights = md.Heights, Mx = md.Mx, My = md.My, Mats = md.Mats,
            Materials = md.Materials?.ToList() ?? DefaultMaterials(md.Setting),
            Walk = md.Walk, Biomes = md.Biomes, Forest = md.Forest, Plants = md.Plants, Objects = md.Objects, Regions = md.Regions,
            Preview = md.Preview, Requires = md.Requires, DayNight = md.DayNight,
        };
        if (md.InfoTree == null && md.Description.Count > 0) m.FlatSettings = new Dictionary<string, string>(md.Description);
        return m;
    }
}
