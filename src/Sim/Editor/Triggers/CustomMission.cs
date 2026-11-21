// Custom maps (docs/MAP_EDITOR.md "Playing your map"): a map of the user's (the map editor's saves, imported .pwmap
// files: MapFiles.UserMaps) whose triggers.lua or custom.lua has something is played as a mission - a skirmish has no
// trigger engine, only a campaign mission runs one. The map is written as a mission folder the way Test map writes it
// (TestMission.Write: a plain map gets a mission table from its player slots - you on the first slot a human may play,
// the computer the others; a map with its own mission table keeps its players) into the user's cache folder, one folder
// per map and content (<cache>/custom-maps/<name>-<content hash>): playing it again, Retry and the saved games of it find
// the same folder; a changed map gets a new one. The campaign menu's "Custom maps" lists them. No Godot types.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor.Triggers;

public static class CustomMission
{
    static readonly Logger L = Log.For("Editor");
    /// <summary>the GameStart's mission id of a custom map (the campaign's are 0 .. 60, Test map's 99)</summary>
    public const int Id = 98;
    public const string Key = "custom_map";
    /// <summary>the folder below the cache root the prepared missions go to</summary>
    public const string Folder = "custom-maps";

    /// <summary>does the map (a folder or .pwmap) have triggers or custom code</summary>
    public static bool HasTriggers(string mapPath)
    {
        try
        {
            var s = new PwMapReader.Source(mapPath);
            return !string.IsNullOrWhiteSpace(Code(s.Text(PwMapPaths.Triggers))) || !string.IsNullOrWhiteSpace(Code(s.Text(PwMapPaths.Custom)));
        }
        catch (Exception e) { L.Debug($"{mapPath}: {e.Message}"); return false; }
    }

    /// <summary>Lua without its comments (a file of comments only is empty)</summary>
    static string? Code(string? lua) => lua == null ? null : string.Join("\n", lua.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)));

    /// <summary>the user's maps with triggers (for a configuration: without the maps that need another - map.lua requires,
    /// MapFiles.RequirementProblem)</summary>
    public static List<string> List(Content.GameConfiguration? cfg = null) => MapFiles.UserMaps().Where(HasTriggers).Where(p => cfg == null || Fits(cfg, p)).ToList();

    static bool Fits(Content.GameConfiguration cfg, string path)
    {
        try { return MapFiles.RequirementProblem(cfg, Game.SkirmishMaps.Header(path).Requires) == null; }
        catch (Exception e) { L.Debug($"{path}: {e.Message}"); return false; }
    }

    /// <summary>is a GameStart's mission folder a prepared custom map</summary>
    public static bool IsCustomFolder(string? folder) =>
        !string.IsNullOrEmpty(folder) && Path.IsPathRooted(folder) && Path.GetFileName(Path.GetDirectoryName(folder.TrimEnd('/', '\\')) ?? "") == Folder;

    /// <summary>the mission folder of a map (by its name and content)</summary>
    public static string FolderFor(string cacheRoot, string mapPath)
    {
        var name = Path.GetFileName(mapPath.TrimEnd('/', '\\'));
        if (name.EndsWith(PwMapPaths.Ext, StringComparison.OrdinalIgnoreCase)) name = name[..^PwMapPaths.Ext.Length];
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return Path.Combine(cacheRoot, Folder, $"{safe}-{MapFiles.ContentHash(mapPath)[..10]}");
    }

    /// <summary>the map as a mission folder (made once per content); playable / startUnits as TestMission.Write</summary>
    public static string Prepare(string mapPath, string cacheRoot, IReadOnlyCollection<string> playable, Func<string, IReadOnlyList<string>>? startUnits = null)
    {
        var folder = FolderFor(cacheRoot, mapPath);
        if (File.Exists(Path.Combine(folder, PwMapPaths.Map)) && File.Exists(Path.Combine(folder, PwMapPaths.World))) return folder;
        var doc = MapDataDoc.Open(mapPath);
        if (!doc.Mission.HasTriggers) throw new InvalidOperationException($"{doc.Name}: the map has no triggers (play it as a skirmish)");
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        var tmp = folder + ".tmp";
        TestMission.Write(doc, tmp, playable, startUnits, Id, Key, "Custom map: " + doc.Name);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.Move(tmp, folder);
        L.Info($"custom map {mapPath} prepared as a mission: {folder}");
        return folder;
    }
}
