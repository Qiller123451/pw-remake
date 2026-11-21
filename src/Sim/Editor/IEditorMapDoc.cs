// The map editor's document: the map being edited, as the editor sees it (docs/MAP_EDITOR.md). The editor - its tools,
// commands, undo / redo, the view and "Test map" - talks only to this interface, never to a file format, so the map's
// storage can change under it: MapDataDoc (a map of the remake's format - docs/MAP_FORMAT.md - held as MapData, the
// shape the game builds its map source from; Save writes it with PwMapWriter).
//
// Coordinates are the map's own (docs/MAPS.md "Coordinates"): metres, x east, y south; the height samples lie on a
// 2 m grid, sample (i, j) at map (OriginX + i * Cell, OriginY + j * Cell). No Godot types (src/Sim).
using System;
using System.Collections.Generic;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

/// <summary>an inclusive rectangle of grid indices (height samples: I0..I1, J0..J1)</summary>
public readonly record struct GridRect(int I0, int J0, int I1, int J1)
{
    public bool IsEmpty => I1 < I0 || J1 < J0;
    public static readonly GridRect Empty = new(0, 0, -1, -1);
    /// <summary>the smallest rectangle holding both</summary>
    public GridRect Union(GridRect o) => IsEmpty ? o : o.IsEmpty ? this
        : new GridRect(Math.Min(I0, o.I0), Math.Min(J0, o.J0), Math.Max(I1, o.I1), Math.Max(J1, o.J1));
    public bool Contains(int i, int j) => i >= I0 && i <= I1 && j >= J0 && j <= J1;
    public override string ToString() => IsEmpty ? "empty" : $"[{I0}..{I1}] x [{J0}..{J1}]";
}

/// <summary>what changed in a document (the view refreshes only that)</summary>
public enum DocChangeKind { Heights, Materials, Water, Objects, Info, All, Walk, Biomes, Forest, Plants }

/// <summary>a change of the document: its kind and the height samples (2 m grid) the change covers (Heights,
/// Materials, Walk, Biomes, Forest, Plants: the touched area; empty: the whole map or none)</summary>
public readonly record struct DocChange(DocChangeKind Kind, GridRect Rect);

public interface IEditorMapDoc
{
    // ---- what the map is
    /// <summary>the map's name (the level name)</summary>
    string Name { get; set; }
    /// <summary>the ground texture set (Jungle, Savanna, Northland, Icewaste, Ashvalley, Cave1..3)</summary>
    string Setting { get; }
    /// <summary>the players the map is made for (its start locations)</summary>
    int MaxPlayers { get; }
    /// <summary>where it was read from (a map file or folder); null: a new map</summary>
    string? SourcePath { get; }

    // ---- the height grid
    /// <summary>height samples per side</summary>
    int HeightsW { get; }
    int HeightsH { get; }
    /// <summary>metres between samples (2 on every map)</summary>
    double Cell { get; }
    /// <summary>map coordinates of sample (0, 0)</summary>
    double OriginX { get; }
    double OriginY { get; }
    /// <summary>the height (m) of sample (i, j)</summary>
    float Height(int i, int j);
    /// <summary>sets one sample (no event: call HeightsChanged once for the samples of an edit)</summary>
    void SetHeight(int i, int j, float h);
    /// <summary>the samples in r were set: walkability follows the new ground there, Changed is raised</summary>
    void HeightsChanged(GridRect r);
    /// <summary>bilinear height at map (mx, my), clamped to the grid</summary>
    double HeightAt(double mx, double my);

    // ---- the rest the status line and the tools ask
    /// <summary>the water level (m); null: no water</summary>
    double? Water { get; }
    /// <summary>the ground material (0..7 of the setting's 8) at map (mx, my); -1 outside</summary>
    int MaterialAt(double mx, double my);
    /// <summary>can land units walk at map (mx, my) (the map's walk grid, else the slope rule and the water)</summary>
    bool WalkableAt(double mx, double my);

    // ---- the game and the file
    /// <summary>the map as the game builds its world from it (the view, Test map): the map source of the current edits</summary>
    MapSource ToSource(IMapRules rules, Func<string, bool> hasModel, ForestData? forest);
    /// <summary>a deep copy (Test map plays on a snapshot: the game cannot change the edited map)</summary>
    IEditorMapDoc Clone();
    /// <summary>can the document be written (SaveNote says why not)</summary>
    bool CanSave { get; }
    string SaveNote { get; }
    /// <summary>writes the map in the remake's format - a folder, or a .pwmap zip for a path ending in .pwmap - and
    /// takes the values as the file holds them (the map read again is this document); SourcePath is the path from now on.
    /// Throws NotSupportedException when !CanSave, IOException when the files cannot be written.</summary>
    void Save(string path);
    /// <summary>the configuration the map needs (map.lua requires, "" = any): the editor sets it before it saves or
    /// exports (MapRequirement.Decide - the classes, options and tribes the map uses and the editor's configuration)</summary>
    string Requires { get; set; }

    /// <summary>raised after every change (the view, the minimap and the status line follow it)</summary>
    event Action<DocChange>? Changed;

    // ================================================================ phase 2: the terrain and paint tools
    // (src/Sim/Editor/Terrain, docs/MAP_EDITOR.md "Terrain and paint"). Every setter changes the map without an event;
    // the matching ...Changed(r) after an edit's last setter raises Changed once with the area, as SetHeight /
    // HeightsChanged. Height edits move what stands on the ground with it (HeightsChanged: the placed objects' and the
    // plants' Z - those within 1 m above the ground or lower, the ground offset kept).

    // ---- ground materials: a 4 m grid from (OriginX, OriginY)
    int MatsW { get; }
    int MatsH { get; }
    /// <summary>metres per material cell (4 on every map)</summary>
    double MatCell { get; }
    /// <summary>the material (0..7: the setting's eight ground textures) of cell (i, j)</summary>
    int Material(int i, int j);
    void SetMaterial(int i, int j, int m);
    /// <summary>the material cells in r were set: Changed (Materials)</summary>
    void MaterialsChanged(GridRect cells);

    // ---- the water
    /// <summary>sets the water level (null: no water); the walk grid's auto cells follow it; Changed (Water, empty rect)</summary>
    void SetWater(double? level);

    // ---- the walk grid: 2 m cells from (WalkOriginX, WalkOriginY); each cell is Auto (the slope rule and the water -
    // or an original map's own grid where its ground is as opened) or painted walkable / blocked
    int WalkW { get; }
    int WalkH { get; }
    double WalkOriginX { get; }
    double WalkOriginY { get; }
    /// <summary>metres per walk cell (2)</summary>
    double WalkCell { get; }
    /// <summary>can land units walk on cell (c, d) (the override, else auto)</summary>
    bool WalkCellOk(int c, int d);
    WalkOverride GetWalkOverride(int c, int d);
    void SetWalkOverride(int c, int d, WalkOverride o);
    /// <summary>the overrides of the cells in r were set: the cells follow; Changed (Walk)</summary>
    void WalkChanged(GridRect cells);

    // ---- the biome layer (docs/BIOMES.md): null = the setting alone
    /// <summary>the map's layer (the tools read it; they paint a copy and give it back with SetBiomes)</summary>
    BiomeLayer? Biomes { get; }
    /// <summary>takes over a copy of the layer (null: none). The same biomes and grid: Changed (Biomes, samples);
    /// another list of biomes (one added) or none: Changed (All - the ground's textures are drawn anew)</summary>
    void SetBiomes(BiomeLayer? layer, GridRect samples);

    // ---- forest blocks: 32 m squares at map (32 x, 32 y), 31 item bytes (Forest.cs: 15 trees, 16 undergrowth)
    /// <summary>the squares the map has room for (inclusive square indices)</summary>
    GridRect ForestSquares { get; }
    /// <summary>the setting whose forest kinds the squares grow (forest.json: ForestData.ForestKinds)</summary>
    string ForestSetting { get; }
    /// <summary>a copy of square (x, y)'s 31 item bytes; null: no forest block there</summary>
    byte[]? ForestSquare(int x, int y);
    /// <summary>sets square (x, y)'s items (a copy is kept); null removes the block</summary>
    void SetForestSquare(int x, int y, byte[]? items);
    /// <summary>the squares in r were set: Changed (Forest)</summary>
    void ForestChanged(GridRect squares);

    // ---- plants (landscape decoration instances: IOMG; map coordinates, Z = their height)
    IReadOnlyList<MapPlant> Plants { get; }
    void InsertPlant(int index, MapPlant p);
    void RemovePlantAt(int index);
    /// <summary>plants in the map rectangle (x0, y0) .. (x1, y1) were added or removed: Changed (Plants)</summary>
    void PlantsChanged(double x0, double y0, double x1, double y1);

    // ---- objects (phase 3: docs/MAP_EDITOR.md "Objects")
    /// <summary>the map's objects in their order (MapObject: type tag, class, name, map x / y, height z, heading Rot and
    /// the stored quaternion Q, owner = start slot or null, Level 0-based, attributes) - units, buildings, animals, nests,
    /// resources, decoration, items, start locations (SLOC, owner = the player slot)</summary>
    IReadOnlyList<MapObject> Objects { get; }
    /// <summary>puts an object at that place of the list (no event: call ObjectsChanged once for an edit)</summary>
    void InsertObject(int index, MapObject o);
    /// <summary>takes an object off the map; its former index (-1: not on it). No event.</summary>
    int RemoveObject(MapObject o);
    /// <summary>objects were added, removed or changed: the players' count follows the start locations, Changed (Objects)
    /// is raised</summary>
    void ObjectsChanged();
    /// <summary>the map source the editor's view draws: ToSource without the map's objects (the editor draws and edits
    /// them itself); the start locations stay (the camera's start)</summary>
    MapSource ToViewSource(IMapRules rules, Func<string, bool> hasModel, ForestData? forest);

    // ---- players and map settings (phase 4: docs/MAP_EDITOR.md "Players and map settings")
    /// <summary>a copy of the map's eight player slots and the game's defaults (army budget, MIRAGE's options)</summary>
    MapPlayerSettings GetPlayers();
    /// <summary>sets the players (Changed: Info)</summary>
    void SetPlayers(MapPlayerSettings p);
    /// <summary>a copy of the map's settings (name, author, descriptions, game types, time, weather, atmosphere, camera,
    /// preview)</summary>
    MapSettings GetSettings();
    /// <summary>sets the settings (the name too; Changed: Info)</summary>
    void SetSettings(MapSettings s);

    // ================================================================ phases 5 - 6: regions, mission data, triggers
    // (src/Sim/Editor/Regions, src/Sim/Editor/Triggers, docs/MAP_EDITOR.md "Regions and mission data", "Triggers").
    // The mission data has its own change event (MissionDoc.Changed) and commands (MissionEdits); a region edit keeps the
    // map's regions (regions.lua) the same as the mission's.

    /// <summary>the map's mission data: regions, groups, variables, quests, question marks, camera points, the start
    /// camera, triggers.lua (TriggerScript), custom.lua, the texts - empty on a plain map. In the new format: world.lua,
    /// map.lua's mission table, triggers.lua, custom.lua, texts/&lt;lang&gt;.lua, regions.lua (MissionDoc.From(PwMap),
    /// MissionDoc.ApplyTo(PwMap))</summary>
    Regions.MissionDoc Mission { get; }
    /// <summary>the whole map as the new format's document (Test map writes it as a mission folder when the map has
    /// triggers; the save)</summary>
    PwMap ToMap();
}

/// <summary>a walk cell's painted state (the walkability brush)</summary>
public enum WalkOverride : byte { Auto, Walkable, Blocked }
