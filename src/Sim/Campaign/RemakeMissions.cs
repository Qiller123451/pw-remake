// The remake's own missions in the map format (docs/OUTBREAK.md, docs/CAMPAIGN.md "Custom campaigns"): mission folders
// shipped with the game in game/missions/<name>/ (map.lua with its mission table, world.lua, triggers.lua, custom.lua,
// texts, layout data; coop.lua: the mission's co-op definition). Unlike the configurations' missions (converted from the
// installation by Prepare) they are the same for every configuration - a mission picks its classes with api.exists.
//
// A game names such a mission by a token in GameStart.MissionFolder, "remake:<name>", not by a path: every machine of a
// co-op game, a saved game and a replay find the folder of their own installation (MissionSession.MissionFolderOf).
// The campaign menu's Custom campaigns tab lists them as the group "Remake missions"; the co-op lobby offers those
// with a coop.lua. No Godot types.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Campaign;

public sealed class RemakeMission
{
    public int Id;
    public string Name = "", Key = "", Folder = "", Title = "", Description = "", Tribe = "";
    public int MaxPlayers = 1;
    /// <summary>the mission's co-op definition (coop.lua in its folder), or null</summary>
    public string? CoopFile;
    /// <summary>GameStart.MissionFolder of the mission</summary>
    public string Token => RemakeMissions.Prefix + Name;
}

public static class RemakeMissions
{
    static readonly Logger L = Log.For("Campaign.Remake");
    public const string Prefix = "remake:";
    public const string GroupTitle = "Remake missions";

    /// <summary>game/missions next to the running game (or PW_MISSIONS)</summary>
    public static string Root()
    {
        var env = Environment.GetEnvironmentVariable("PW_MISSIONS");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "game", "missions");
            if (Directory.Exists(c)) return c;
            c = Path.Combine(d.FullName, "missions");
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "outbreak", "map.lua"))) return c;
        }
        return Path.Combine("game", "missions");
    }

    static List<RemakeMission>? _cache;
    static string? _cacheRoot;

    /// <summary>the missions (folders with a map.lua that has a mission table), by id</summary>
    public static List<RemakeMission> List()
    {
        var root = Root();
        if (_cache != null && _cacheRoot == root) return _cache;
        var res = new List<RemakeMission>();
        if (Directory.Exists(root))
            foreach (var d in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    if (!File.Exists(Path.Combine(d, "map.lua")) || Maps.PwMapReader.MissionOf(d) is not JsonObject m) continue;
                    var coop = Path.Combine(d, "coop.lua");
                    res.Add(new RemakeMission
                    {
                        Id = m["id"].Int(), Name = Path.GetFileName(d), Key = m["key"].Str(), Folder = d, Title = m["title"].Str(Path.GetFileName(d)),
                        Description = m["description"].Str(), Tribe = m["tribe"].Str("Hu"), CoopFile = File.Exists(coop) ? coop : null,
                        MaxPlayers = File.Exists(coop) ? 3 : 1,
                    });
                }
                catch (Exception e) { L.Warn($"{d}: {e.Message}"); }
            }
        _cache = res; _cacheRoot = root;
        return res;
    }

    public static RemakeMission? ById(int id) => List().FirstOrDefault(m => m.Id == id);
    public static RemakeMission? ByName(string name) => List().FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    public static bool IsToken(string? folder) => folder != null && folder.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>the folder of a GameStart's mission token ("remake:outbreak"), or null</summary>
    public static string? FolderOf(string? token) => IsToken(token) ? ByName(token![Prefix.Length..])?.Folder : null;

    /// <summary>a game of the mission (single player: no seats; the co-op lobby adds its own)</summary>
    public static Game.GameStart Start(RemakeMission m, string configuration, string tribe, int difficulty, ulong seed) => new()
    {
        Configuration = configuration, Mode = Game.GameMode.Campaign, Mission = m.Id, MissionFolder = m.Token, Tribe = tribe, Difficulty = difficulty, Seed = seed,
    };
}
