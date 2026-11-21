// The map editor's document (docs/MAP_EDITOR.md): a map of the remake's format (docs/MAP_FORMAT.md: a folder or a
// .pwmap - MapFiles.ReadDocument; an original .ula or a remake recipe converted in memory) or a new flat map, edited in
// memory as MapData - the shape the game builds its map source from (MapSource.OriginalSource), so the view and "Test
// map" see the edits. Save writes it in the map format (ToMap() - PwMap - with PwMapWriter: a folder, or a .pwmap zip).
//
// The walk grid: an original map has its editor's grid (PaFi, docs/MAPS.md); where the heights are edited its cells
// follow the new ground by the game's slope rule (MapSource.SlopeWalkable, 32 degrees) and the water. A map without a
// grid (a new one) gets one made by that rule (phase 2: MapDataDoc.Terrain.cs - the walkability brush paints over it;
// the painted cells are saved in walk.png as 64 / 192 and read back as overrides).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

/// <summary>what the New map dialog asks</summary>
public sealed class NewMapSpec
{
    public string Name = "New map";
    /// <summary>metres per side (a multiple of 32: the original's 16-sample blocks on the 2 m grid)</summary>
    public int Size = 512;
    public string Setting = "Jungle";
    public double BaseHeight = 20;
    /// <summary>null: no water</summary>
    public double? Water = 16;
    public int Players = 2;

    /// <summary>the map sizes the original's maps have (metres per side), from their headers; the usual ones without maps</summary>
    public static List<int> Sizes(IEnumerable<int> mapSizes)
    {
        var l = mapSizes.Where(s => s >= 128 && s <= 4096).Select(s => (s + 31) / 32 * 32).Distinct().OrderBy(s => s).ToList();
        return l.Count > 0 ? l : new List<int> { 256, 384, 512, 768, 1024 };
    }
}

public sealed partial class MapDataDoc : IEditorMapDoc
{
    static readonly Logger L = Log.For("Editor");
    /// <summary>the map being edited (its Heights array is shared with the map source the view draws)</summary>
    public MapData Md { get; }
    /// <summary>the map folder or file it was read from or last saved to (null: a new map)</summary>
    public string? SourcePath { get; private set; }
    /// <summary>what the map format holds beyond MapData, kept for the save: map.lua's own title / description /
    /// author, the heights' range, other files of the folder (Lua modules custom.lua requires ...)</summary>
    string? _title, _description, _author;
    double _heightMin, _heightStep = PwMap.DefaultHeightStep;
    SortedDictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public MapDataDoc(MapData md, string? sourcePath)
    {
        Md = md; SourcePath = sourcePath;
        if (md.Heights.Length != md.Hx * md.Hy) throw new ArgumentException($"map {md.Name}: {md.Heights.Length} height samples, not {md.Hx} x {md.Hy}");
        _baseHeights = (float[])md.Heights.Clone();
        InitTerrain();                                       // (phase 2: a walk grid for every map, overrides, seats)
        _baseWalk = md.Walk != null ? (byte[])md.Walk.Cells.Clone() : null;
    }

    // the heights and walk cells as opened: a walk cell whose ground is as it was keeps the map's own value (an undo
    // gives the original grid back exactly), one whose ground was edited follows the slope rule
    readonly float[] _baseHeights;
    readonly byte[]? _baseWalk;

    /// <summary>a map as the game reads it: a map folder or .pwmap of the remake's format (with its mission data), an
    /// original .ula or a remake recipe .pwmap.json (converted in memory)</summary>
    public static MapDataDoc Open(string path)
    {
        var pw = MapFiles.ReadDocument(path);
        var md = pw.ToMapData();
        L.Info($"opened {path}: {md.Name}, {md.Hx * 2} x {md.Hy * 2} m, {md.Setting}, water {md.Water}, {md.Objects.Count} objects");
        var d = new MapDataDoc(md, path)
        {
            _title = pw.Title, _description = pw.Description, _author = pw.Author, _heightMin = pw.HeightMin, _heightStep = pw.HeightStep,
            _files = new SortedDictionary<string, byte[]>(pw.Files, StringComparer.Ordinal),
        };
        d.OpenMission(pw, path);                             // (phases 5 - 6: world.lua, triggers.lua ... - Regions/MapDataDoc.Mission.cs)
        return d;
    }

    /// <summary>a flat map: the size, setting, ground height and water level of the spec, material 0 everywhere, the
    /// players' start locations evenly on a circle round the centre</summary>
    public static MapDataDoc CreateFlat(NewMapSpec spec)
    {
        int size = Math.Max(64, (spec.Size + 31) / 32 * 32);
        int hx = size / 2, mx = size / 4;
        var md = new MapData
        {
            W = size, H = size, Hx = hx, Hy = hx, Heights = Enumerable.Repeat((float)spec.BaseHeight, hx * hx).ToArray(),
            Mx = mx, My = mx, Mats = new byte[mx * mx], Water = spec.Water ?? -1000,
            Setting = spec.Setting, SettingName = spec.Setting, TerrainSetting = spec.Setting, Name = spec.Name,
            MaxPlayers = Math.Max(1, spec.Players), StartTime = 12 * 3600, DefaultCamera = 0.785, File = spec.Name,
            Forest = new ForestMap { W = size / 32, H = size / 32 },
        };
        md.Info["LevelName"] = spec.Name;
        md.Info["Setting"] = spec.Setting;
        md.Info["MaxPlayers"] = md.MaxPlayers.ToString(System.Globalization.CultureInfo.InvariantCulture);
        md.Description["Root/Base/MaxPlayers"] = md.Info["MaxPlayers"];
        md.Description["Root/Base/GameType"] = "Deathmatch|";
        md.Description["Root/Base/MapType"] = "multiplayer";
        // the start locations (SLOC objects, as an original map's): on a circle at 3/8 of the size round the centre
        int n = Math.Max(2, spec.Players);
        for (int k = 0; k < n; k++)
        {
            double a = Math.PI * 3 / 4 + 2 * Math.PI * k / n, r = size * 3 / 8.0;
            md.Objects.Add(new MapObject
            {
                Type = "SLOC", Name = $"Start {k + 1}", Cls = "StartLocation", X = size / 2.0 + r * Math.Cos(a), Y = size / 2.0 - r * Math.Sin(a),
                Z = spec.BaseHeight, Owner = k, Q = new double[] { 0, 0, 0, 1 },
            });
        }
        L.Info($"new map: {spec.Name}, {size} x {size} m, {spec.Setting}, ground {spec.BaseHeight} m, water {(spec.Water?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")}, {n} start locations");
        return new MapDataDoc(md, null);
    }

    // ---- IEditorMapDoc
    public string Name { get => Md.Name; set { Md.Name = value; Md.Info["LevelName"] = value; Changed?.Invoke(new DocChange(DocChangeKind.Info, GridRect.Empty)); } }
    public string Setting => Md.Setting;
    /// <summary>map.lua's requires (MapRequirement decides it when the editor saves or exports)</summary>
    public string Requires { get => Md.Requires; set => Md.Requires = value ?? ""; }
    public int MaxPlayers => (int)Math.Max(1, Md.MaxPlayers);
    public int HeightsW => Md.Hx;
    public int HeightsH => Md.Hy;
    public double Cell => 2;
    public double OriginX => Md.OffX;
    public double OriginY => Md.OffY;
    public double? Water => Md.Water > -100 ? Md.Water : null;

    public float Height(int i, int j) => Md.Heights[j * Md.Hx + i];
    public void SetHeight(int i, int j, float h) => Md.Heights[j * Md.Hx + i] = h;
    public double HeightAt(double mx, double my) => Md.HeightAtMap(mx, my);
    public int MaterialAt(double mx, double my) => Md.MaterialAtMap(mx, my);

    public bool WalkableAt(double mx, double my)
    {
        if (Md.Walk is { } wg) return wg.WalkableMap(mx, my);
        return SlopeOk(mx, my) && (Water is not double w || HeightAt(mx, my) > w);
    }

    /// <summary>the game's slope rule at map (mx, my) (MapSource.SlopeWalkable on the map's heights)</summary>
    bool SlopeOk(double mx, double my) => MapSource.SlopeWalkable(HeightAt)(mx, my);

    public void HeightsChanged(GridRect r)
    {
        if (r.IsEmpty) return;
        UpdateWalk(r);
        Reseat(r);
        Changed?.Invoke(new DocChange(DocChangeKind.Heights, r));
    }

    /// <summary>the walk grid's cells over the changed samples (and one around: the slope rule looks 1 m to each side)
    /// follow the ground: a cell whose four corner samples are as opened keeps the map's own value, an edited one gets
    /// the slope rule and the water</summary>
    void UpdateWalk(GridRect r)
    {
        if (Md.Walk is not { } wg) return;
        // sample i lies at map OffX + 2i; walk cell c covers wg.OffX + 2c .. + 2
        double x0 = OriginX + (r.I0 - 1) * Cell, x1 = OriginX + (r.I1 + 1) * Cell, y0 = OriginY + (r.J0 - 1) * Cell, y1 = OriginY + (r.J1 + 1) * Cell;
        int c0 = Math.Max(0, (int)Math.Floor((x0 - wg.OffX) / 2)), c1 = Math.Min(wg.W - 1, (int)Math.Floor((x1 - wg.OffX) / 2));
        int d0 = Math.Max(0, (int)Math.Floor((y0 - wg.OffY) / 2)), d1 = Math.Min(wg.H - 1, (int)Math.Floor((y1 - wg.OffY) / 2));
        RecomputeWalk(new GridRect(c0, d0, c1, d1));
    }

    /// <summary>are the samples round map (mx, my) (the slope rule's 2 x 2 m) as opened</summary>
    bool Unchanged(double mx, double my)
    {
        int i0 = Math.Max(0, (int)Math.Floor((mx - 1 - OriginX) / Cell)), i1 = Math.Min(HeightsW - 1, (int)Math.Ceiling((mx + 1 - OriginX) / Cell));
        int j0 = Math.Max(0, (int)Math.Floor((my - 1 - OriginY) / Cell)), j1 = Math.Min(HeightsH - 1, (int)Math.Ceiling((my + 1 - OriginY) / Cell));
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                if (Md.Heights[j * HeightsW + i] != _baseHeights[j * HeightsW + i]) return false;
        return true;
    }

    public MapSource ToSource(IMapRules rules, Func<string, bool> hasModel, ForestData? forest)
    {
        var S = MapSource.OriginalSource(Md, rules, null, hasModel, forest);
        AddOwnedAnimals(S, rules);
        return S;
    }

    public IEditorMapDoc Clone()
    {
        var c = new MapDataDoc(CloneData(Md), SourcePath)
        {
            _title = _title, _description = _description, _author = _author, _heightMin = _heightMin, _heightStep = _heightStep,
            _files = new SortedDictionary<string, byte[]>(_files, StringComparer.Ordinal),
        };
        c.CopyOverrides(this); c.CopyMission(this);
        return c;
    }

    /// <summary>a deep copy of the parts of a MapData the game reads or the editor changes (grids, objects, plants,
    /// forest squares, regions, the level info)</summary>
    public static MapData CloneData(MapData md) => new()
    {
        W = md.W, H = md.H, OffX = md.OffX, OffY = md.OffY, Extension = md.Extension, Biomes = CloneLayer(md.Biomes), Water = md.Water,
        Hx = md.Hx, Hy = md.Hy, Heights = (float[])md.Heights.Clone(), Mx = md.Mx, My = md.My, Mats = (byte[])md.Mats.Clone(),
        TerrainSetting = md.TerrainSetting, Info = new Dictionary<string, string>(md.Info), File = md.File, Preview = md.Preview,
        Description = new Dictionary<string, string>(md.Description), InfoTree = md.InfoTree, DefaultCamera = md.DefaultCamera,
        StartTime = md.StartTime, DayNight = md.DayNight, Setting = md.Setting, SettingName = md.SettingName, Name = md.Name, MaxPlayers = md.MaxPlayers, Requires = md.Requires,
        Objects = md.Objects.Select(o => new MapObject
        {
            Type = o.Type, Name = o.Name, Cls = o.Cls, X = o.X, Y = o.Y, Z = o.Z, Rot = o.Rot, Q = (double[])o.Q.Clone(), Owner = o.Owner,
            At = o.At, Level = o.Level, Attr = new Dictionary<string, string>(o.Attr),
        }).ToList(),
        Plants = md.Plants.Select(p => new MapPlant { Name = p.Name, X = p.X, Y = p.Y, Z = p.Z, Rot = p.Rot, Q = (double[])p.Q.Clone() }).ToList(),
        Forest = new ForestMap { W = md.Forest.W, H = md.Forest.H, Blocks = md.Forest.Blocks.Select(b => new ForestBlock { X = b.X, Y = b.Y, B = (byte[])b.B.Clone() }).ToList() },
        Walk = md.Walk?.Clone(),
        Regions = md.Regions.Select(r => new MapRegion { Name = r.Name, Shapes = r.Shapes.Select(s => new RegionShape { Oval = s.Oval, Enabled = s.Enabled, X = s.X, Y = s.Y, W = s.W, H = s.H }).ToList() }).ToList(),
    };

    public bool CanSave => true;
    public string SaveNote => "";

    /// <summary>writes the map in the remake's format: a folder (PwMapWriter.WriteFolder: map.lua, terrain/*.png, the Lua
    /// files ...; the format's files the map no longer has are removed, other files kept) or, for a path ending in .pwmap,
    /// one zip. Then the document takes the values as the file holds them (Canonical: heights on the file's 16-bit steps,
    /// positions as 32-bit floats ...), so that the map read again is this document exactly. From now on SourcePath is
    /// the path.</summary>
    public void Save(string path)
    {
        var pw = ToMap();
        if (path.EndsWith(PwMapPaths.Ext, StringComparison.OrdinalIgnoreCase)) PwMapWriter.WriteZip(pw, path);
        else PwMapWriter.WriteFolder(pw, path);
        Canonical(PwMapReader.Read(path));
        SourcePath = path;
        L.Info($"saved {Name} to {path}");
    }

    /// <summary>takes the values of the map as written and read back (the same lists in the same order): the heights (the
    /// 16-bit steps of height.png), the objects' and plants' positions and rotations (32-bit floats; the heading from the
    /// quaternion), the regions' boxes; the heights' range for the next save. Nothing the eye could see.</summary>
    void Canonical(PwMap back)
    {
        _heightMin = back.HeightMin; _heightStep = back.HeightStep;
        if (back.Heights.Length == Md.Heights.Length)
        {
            Array.Copy(back.Heights, Md.Heights, Md.Heights.Length);
            Array.Copy(back.Heights, _seat, Math.Min(_seat.Length, back.Heights.Length));
        }
        // the level settings flattened as the reader flattens them (MapData.Description: the tree's short keys too)
        var flat = back.DescriptionFlat();
        Md.Description.Clear();
        foreach (var (k, v) in flat) Md.Description[k] = v;
        var mine = Md.Objects.Where(o => !o.Attr.ContainsKey(Regions.MissionDoc.MissionOnlyAttr)).ToList();      // (objects.lua's, in its order: MissionDoc.MapFileObjects)
        if (mine.Count == back.Objects.Count)
            for (int i = 0; i < mine.Count; i++)
            {
                MapObject a = mine[i], b = back.Objects[i];
                a.X = b.X; a.Y = b.Y; a.Z = b.Z; a.Q = b.Q; a.Rot = b.Rot;
            }
        _mission?.Saved(back.World);
        Md.Regions = back.Regions;                                // (regions.lua as written: the mission's regions, 32-bit floats)
        if (Md.Plants.Count == back.Plants.Count)
            for (int i = 0; i < Md.Plants.Count; i++) { var a = Md.Plants[i]; var b = back.Plants[i]; a.X = b.X; a.Y = b.Y; a.Z = b.Z; a.Q = b.Q; a.Rot = b.Rot; }
    }

    public event Action<DocChange>? Changed;

    // ---------------------------------------------------------------- objects, players, settings (phases 3 and 4)

    public IReadOnlyList<MapObject> Objects => Md.Objects;
    public void InsertObject(int index, MapObject o) => Md.Objects.Insert(Math.Clamp(index, 0, Md.Objects.Count), o);
    public int RemoveObject(MapObject o) { int i = Md.Objects.IndexOf(o); if (i >= 0) Md.Objects.RemoveAt(i); return i; }

    /// <summary>the players the map is for follow its start locations (Root/Base/MaxPlayers: the skirmish's limit)</summary>
    public void ObjectsChanged()
    {
        int n = Md.Objects.Count(o => o.Type == "SLOC");
        if (n > 0 && n != (int)Md.MaxPlayers)
        {
            Md.MaxPlayers = n;
            string v = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Md.Info["MaxPlayers"] = v;
            LevelSettings.Detach(Md);
            LevelSettings.Set(Md, "Root/Base/MaxPlayers", v); Md.Description["MaxPlayers"] = v;
        }
        Changed?.Invoke(new DocChange(DocChangeKind.Objects, GridRect.Empty));
    }

    /// <summary>the view's source: the same map (the grids are shared - the view follows the edits) with only the start
    /// locations of the objects</summary>
    public MapSource ToViewSource(IMapRules rules, Func<string, bool> hasModel, ForestData? forest)
    {
        var v = new MapData();
        foreach (var f in typeof(MapData).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (!f.IsInitOnly) f.SetValue(v, f.GetValue(Md));
        v.Objects = Md.Objects.Where(o => o.Type == "SLOC").ToList();
        return MapSource.OriginalSource(v, rules, null, hasModel, forest);
    }

    /// <summary>the tribes' animals of the map (ANML with an owner: riding dinosaurs, the Aje's animals): MapSource keeps only
    /// the wild ones (owner none) - they join the players' objects, so a placed tribe animal comes alive on Test map like a
    /// unit (SkirmishSession: the player on that start slot gets it)</summary>
    void AddOwnedAnimals(MapSource S, IMapRules rules)
    {
        foreach (var o in Md.Objects)
        {
            if (o.Type != "ANML" || o.Owner == null || !rules.Exists(o.Cls)) continue;
            var (x, z) = S.ToGame(o.X, o.Y);
            S.Owned.Add(new MapOwned { Type = "ANML", Cls = o.Cls, X = x, Z = z, Rot = Frame.MapHeading(o.Rot), Owner = o.Owner, Level = o.Level + 1, Attr = o.Attr });
            S.Models.Use(rules.StatsGfx(o.Cls)?.ToLowerInvariant());
        }
    }

    public MapPlayerSettings GetPlayers() => MapPlayerSettings.Read(Md.Description);
    public void SetPlayers(MapPlayerSettings p) { p.Write(Md); Changed?.Invoke(new DocChange(DocChangeKind.Info, GridRect.Empty)); }
    public MapSettings GetSettings() => MapSettings.Read(Md);
    public void SetSettings(MapSettings s) { s.Write(Md); Changed?.Invoke(new DocChange(DocChangeKind.Info, GridRect.Empty)); }
}
