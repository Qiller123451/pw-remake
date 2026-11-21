// The phase 1 document's mission data (phases 5 - 6, docs/MAP_EDITOR.md "Regions and mission data"): a map opened from
// a folder of the remake's format brings its world.lua, map.lua's mission table, triggers.lua, custom.lua and texts; a
// map without them (an original multiplayer map, a new map) gets empty mission data with the map's regions. The
// regions are the map's (regions.lua: MapData.Regions follows every region edit, so the view and Test map's map source
// have them). The placed objects the tools pick come from world.lua's objects (with their GUIDs) and the map's
// objects (a GUID made from the name for those without one: EdGuid.Of).
//
// After phase 0's connection (PwMapDoc over PwMap) the same members map to PwMap.World / Mission / Triggers / Custom /
// Texts / Regions: MissionDoc.From(PwMap) and MissionDoc.ApplyTo(PwMap).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

public sealed partial class MapDataDoc
{
    MissionDoc? _mission;

    /// <summary>the map's mission data (IEditorMapDoc.Mission)</summary>
    public MissionDoc Mission
    {
        get
        {
            if (_mission == null) Attach(MissionDoc.From(null, null, null, null, null, Md.Regions));
            return _mission!;
        }
    }

    void Attach(MissionDoc m)
    {
        if (_mission != null) _mission.Changed -= OnMission;
        _mission = m;
        m.Objects = ObjectList;
        m.MapObjects = () => Md.Objects;
        m.Changed += OnMission;
    }

    void OnMission(MissionPart p)
    {
        if (p is MissionPart.Regions or MissionPart.All) Md.Regions = _mission!.ToMapRegions();
    }

    /// <summary>the mission data of a map read in the remake's format (world.lua, map.lua's mission table, triggers.lua,
    /// custom.lua, texts), if it has any</summary>
    void OpenMission(PwMap pw, string path)
    {
        try
        {
            if (pw.World == null && pw.Mission == null && pw.Triggers == null && pw.Custom == null && pw.Texts.Count == 0) return;
            Attach(MissionDoc.From(pw.World, pw.Mission, pw.Triggers, pw.Custom, pw.Texts, Md.Regions));
            _mission!.BindObjects(Md.Objects);
            L.Info($"mission data of {path}: {_mission!.Regions.Count} regions, {_mission.Groups.Count} groups, {_mission.Quests.Count} quests, {_mission.Variables.Count} variables, {_mission.Triggers.Triggers.Count()} triggers");
        }
        catch (Exception e) { L.Error($"the mission data of {path} could not be read (the map opens without it)", e); }
    }

    /// <summary>the mission data of another document (a clone's)</summary>
    void CopyMission(MapDataDoc o) { if (o._mission != null) Attach(o._mission.Clone()); }

    // ------------------------------------------------------------------ the objects the tools pick
    /// <summary>the map's objects as the tools pick them: the GUID world.lua gave them (attribute "guid"), else one made
    /// from the name (the trigger editor gives an object a GUID of its own when a trigger names it: MissionEdits.EnsureGuid)</summary>
    IReadOnlyList<EdObject> ObjectList() => Md.Objects.Select(o => new EdObject(
        o.Attr.TryGetValue(MissionDoc.GuidAttr, out var g) && g.Length > 0 ? g : EdGuid.Of(o.Name), o.Name, o.Cls, o.Type, o.X, o.Y, o.Owner)).ToList();

    /// <summary>the whole map as the new format's document (Test map's mission folder; the save once connected): the
    /// map's grids and lists (shared, not copied) and the mission data</summary>
    public PwMap ToMap()
    {
        var pw = PwMap.FromMapData(Md);
        pw.Objects = MissionDoc.MapFileObjects(Md.Objects);      // (world.lua's own objects and the editor's attributes stay out of objects.lua)
        // (a saved map is the user's own: its id is where it is - MapFiles.IdOf -, not the original it was made from)
        pw.Source = "";
        pw.Title = _title; pw.Description = _description; pw.Author = _author;
        pw.HeightMin = _heightMin; pw.HeightStep = _heightStep;
        foreach (var (k, v) in _files) pw.Files[k] = v;
        // the walk grid with the painted cells (walk.png 64 / 192: MapDataDoc.Terrain.cs)
        if (Md.Walk is { } wg) pw.Walk = new WalkGrid { W = wg.W, H = wg.H, OffX = wg.OffX, OffY = wg.OffY, Cells = wg.Cells, Paint = _walkOvr.Any(b => b != 0) ? (byte[])_walkOvr.Clone() : null };
        _mission?.ApplyTo(pw);
        return pw;
    }
}
