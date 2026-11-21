// What the map editor keeps while its screen is away (Test map plays a skirmish, then the editor comes back with the
// map unchanged): the document, its undo history, the camera, the tool settings and the players of Test map - and the
// configuration's rules, models and forest layouts, read once. Static: the editor screen (MapEditor) is made anew
// each time it is shown and takes everything from here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.App;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.View.Models;
using PwRemake.Sim.Editor.Triggers;

namespace PwRemake.Editor;

public static class EditorSession
{
    static readonly Logger L = Log.For("Editor");

    /// <summary>the map being edited (null: none yet - the editor starts with a new flat map)</summary>
    public static IEditorMapDoc? Doc { get; private set; }
    public static EditHistory? History { get; private set; }
    /// <summary>the camera when the editor screen went away (x, z, yaw, pitch, distance); null: the map's start</summary>
    public static (double X, double Z, double Yaw, double Pitch, double Dist)? Camera;
    /// <summary>the raise / lower brush's settings</summary>
    public static readonly HeightBrush Brush = new();
    /// <summary>the tool group shown in the palette</summary>
    public static string ToolGroup = "Terrain";
    /// <summary>a Test map game is running (its end returns to the editor)</summary>
    public static bool Testing;

    /// <summary>the configuration the cached data below belongs to</summary>
    static string? s_cfg;
    static PwRemake.Sim.Rules.Rules? s_rules;
    static ModelLibrary? s_lib;
    static ForestData? s_forest;

    public static GameConfiguration Config => GameApp.Config ?? throw new InvalidOperationException("no game configuration");

    static void Cache()
    {
        var cfg = Config;
        if (s_cfg == cfg.Id && s_rules != null) return;
        s_cfg = cfg.Id;
        s_rules = PwRemake.UI.SkirmishMenu.RulesOf(cfg);
        s_lib = new ModelLibrary(GameApp.Assets ?? new AssetSource(cfg));
        s_forest = ForestData.LoadFile(Path.Combine(cfg.Data, "forest.json"));
        L.Info($"editor data of {cfg.Id}: rules, models, forest {(s_forest != null ? "yes" : "no")}");
    }
    public static PwRemake.Sim.Rules.Rules Rules { get { Cache(); return s_rules!; } }
    public static ModelLibrary Models { get { Cache(); return s_lib!; } }
    public static ForestData? Forest { get { Cache(); return s_forest; } }

    /// <summary>are there changes of the map not saved (the question before quitting the game)</summary>
    public static bool Unsaved => Doc != null && History is { Dirty: true };

    /// <summary>a document to edit from now on (a new history; the camera goes to its start)</summary>
    public static void Use(IEditorMapDoc doc)
    {
        Doc = doc;
        History = new EditHistory(doc);
        Camera = null;
        L.Info($"editing {doc.Name} ({doc.HeightsW * doc.Cell} x {doc.HeightsH * doc.Cell} m, {doc.Setting}){(doc.SourcePath != null ? " from " + doc.SourcePath : "")}");
    }

    /// <summary>the map source of the document for the view (the rules' class models, the models the data has)</summary>
    public static MapSource SourceOf(IEditorMapDoc doc) => doc.ToViewSource(new PwRemake.View.World.RulesMapAdapter(Rules), m => Models.Has(m.ToLowerInvariant()), Forest);

    // ---- what the dialogs offer

    // ---- saving: the user's maps folder (MapFiles.UserDir: user://maps; ids "User/<name>")

    /// <summary>the user's maps folder (where Save puts maps)</summary>
    public static string UserMapsDir => MapFiles.UserDir ?? Path.Combine(AppPaths.UserDir, "maps");

    /// <summary>a name as a folder name: no path separators or characters Windows refuses, no leading / trailing dots or
    /// spaces, at most 64 characters</summary>
    public static string SafeName(string name)
    {
        var bad = new HashSet<char>(Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"));
        var s = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.').Trim();
        if (s.EndsWith(PwMapPaths.Ext, StringComparison.OrdinalIgnoreCase)) s = s[..^PwMapPaths.Ext.Length];
        return s.Length > 64 ? s[..64] : s;
    }

    /// <summary>does the document have a place of its own in the user's maps (Save writes there; else Save asks a name)</summary>
    public static bool SavesInPlace(IEditorMapDoc d) => d.SourcePath is { } p && MapFiles.IsUserPath(p) && (Directory.Exists(p) || p.EndsWith(PwMapPaths.Ext, StringComparison.OrdinalIgnoreCase));

    // ---- what the dialogs offer

    /// <summary>Open's sources: My maps (the user's maps folder: folders and .pwmap files), the skirmish list (the
    /// installation's multiplayer maps and the remake's maps), every map of the installation (the campaign's maps too),
    /// the campaign missions (their folders: map, world, triggers)</summary>
    public static readonly string[] OpenSources = { "My maps", "Multiplayer maps", "All maps of the installation", "Campaign missions" };
    public static int OpenSource;
    /// <summary>a map of Open's list; Unfit: why it is not for this configuration (map.lua requires: shown greyed)</summary>
    public sealed record OpenItem(string Path, string Label, string? Unfit = null);

    public static List<OpenItem> OpenList(int source)
    {
        var cfg = Config;
        switch (source)
        {
            case 0:
                return SkirmishMaps.List(cfg, true, unfit: true).Where(m => m.Folder == SkirmishMaps.UserGroup)
                    .Select(m => new OpenItem(m.FullPath, $"{m.Name}   ({m.File}{(m.Players > 0 ? $", {m.Players} players" : "")}){(m.Unfit != null ? " - " + m.Unfit : "")}", m.Unfit)).ToList();
            case 1:
            case 2:
                return SkirmishMaps.List(cfg, source == 2, unfit: true).Where(m => m.Folder != SkirmishMaps.UserGroup)
                    .Select(m => new OpenItem(m.FullPath, $"{m.Name}   ({m.Folder}/{m.File}{(m.Players > 0 ? $", {m.Players} players" : "")}){(m.Unfit != null ? " - " + m.Unfit : "")}", m.Unfit)).ToList();
            default:
                var res = new List<OpenItem>();
                foreach (var e in PwRemake.UI.MissionEntry.Load(cfg.MissionsDir))
                {
                    var f = e.FolderPath(cfg.MissionsDir);     // (the remake's own missions: game/missions)
                    if (PwMapReader.IsMap(f)) res.Add(new OpenItem(f, $"{e.Id:00} {e.Title}   ({e.Folder})"));
                }
                return res;
        }
    }

    /// <summary>the sizes of the original multiplayer maps (metres per side, from their headers)</summary>
    public static List<int> Sizes()
    {
        var sizes = new List<int>();
        foreach (var m in SkirmishMaps.List(Config))
            try { var h = SkirmishMaps.Header(m.FullPath); sizes.Add(h.Hx * 2); }
            catch (Exception e) { L.Debug($"size of {m.Path}: {e.Message}"); }
        return NewMapSpec.Sizes(sizes);
    }

    /// <summary>the settings the converted data has ground textures for (assets/terrain/&lt;setting&gt;/scape_0.jpg)</summary>
    public static List<string> Settings()
    {
        var a = GameApp.Assets;
        var all = Ula.KNOWN_SETTINGS.Values.Distinct().ToList();
        var have = all.Where(s => a?.Find($"assets/terrain/{s}/scape_0.jpg") != null).ToList();
        return have.Count > 0 ? have : new List<string> { "Jungle" };
    }

    /// <summary>the setup of a Test map game: the map's players (its player slots with a start location, their tribes,
    /// teams, colours, who plays them; the army budget and MIRAGE's options of the map - PlayersSetup)</summary>
    // ---- map.lua's requires (MapRequirement: the configuration the map needs - the lists of other configurations leave it out)
    static RequirementContext? s_req;
    static string? s_reqOf;

    /// <summary>what MapRequirement knows of the configurations: the editor's mod, the official game's classes and tribes</summary>
    public static RequirementContext Requirement()
    {
        var cfg = Config;
        if (s_req != null && s_reqOf == cfg.Id) return s_req;
        var ctx = new RequirementContext { Mod = cfg.IsMod ? cfg.Mod : "" };
        if (cfg.IsMod)
        {
            var bc = GameApp.Configurations.FirstOrDefault(c => !c.IsMod);
            if (bc != null) { ctx.BaseClasses = ObjectPalette.ClassNamesOf(bc.Data); ctx.BaseTribes = bc.PlayableTribes.ToList(); }
            ctx.ModClasses = ObjectPalette.ClassNamesOf(cfg.Data);
        }
        s_req = ctx; s_reqOf = cfg.Id;
        return ctx;
    }

    /// <summary>the map's requires decided (MapRequirement.Decide), before a save or an export</summary>
    public static void StampRequirement(IEditorMapDoc doc)
    {
        try
        {
            var ctx = Requirement();
            var was = doc.Requires;
            doc.Requires = MapRequirement.Decide(doc, ctx);
            if (doc.Requires != was) L.Info($"{doc.Name}: requires {(doc.Requires.Length > 0 ? doc.Requires + " (" + string.Join("; ", MapRequirement.Reasons(doc, ctx, 3)) + ")" : "nothing")}");
        }
        catch (Exception e) { L.Warn("the map's configuration requirement: " + e.Message); }
    }

    /// <summary>saves the document (its requires decided first)</summary>
    public static void Save(IEditorMapDoc doc, string path) { StampRequirement(doc); doc.Save(path); }

    public static SkirmishSetup TestSetup(string key, string name) => PlayersSetup.Make(Doc!, Config.PlayableTribes, key, name);
}
