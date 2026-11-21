// The models a mission needs, loaded while the loading screen shows (main.js loadModels / mapModels): every model of
// the tribes the mission plays (the campaign's FullTribes: the human's and those its triggers and computer players
// bring in), the models the mission's own objects and spawns use (Campaign.Need), the map's animals and nests, the
// parts the classes attach (gamedata.json composites), the class files' models standing in for missing ones, the
// animation sources they borrow their clips from; not the landscape archives and wild animals the map does not use,
// not the other tribes'. A model of the manifest whose file is not in the data is left out (the view draws nothing
// for it and says so once). Also the models of everything the player can build and make (its buildings in every mode,
// its units with --preload need / all): the first placement of a building, the first unit of a kind, hitched while
// its model loaded; with WarmUp (the ghosts' and buildings' shaders compiled as the game starts) the first placement
// is as quick as the next. What is not loaded here loads when it first shows (a short hitch).
// The effect models of ui/gamefx.js (GameFx.GAME_FX_MODEL) are loaded with the map, as the JS does; the MIRAGE moves'
// and the missions' own effect models load when they first show.
// Ported from remake/src/main.js (loadModels, mapModels) (pw-exporter 98c7703).
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.View.Entities;
using PwRemake.View.Models;
using PwRemake.UI.Mission;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.App;

public partial class GameSession
{
    static readonly Dictionary<string, string> TRIBE_PREFIX = new() { ["Hu"] = "hu_", ["Aje"] = "aje_", ["Ninigi"] = "ninigi_", ["SEAS"] = "seas_" };
    // map objects of the landscape archives in manifests without the "map" flag
    static readonly Regex LANDSCAPE = new("^(jungle|northland|savanna|icewaste|ashvalley|cave\\d)_(vegetation|landobj)$|^seas_temple_ruins$");
    static readonly Regex ALWAYS_ANIMATED = new("_rider_|_crane_|collector_[a-e]$|_rally_point");

    /// <summary>
    /// model -> the parts the classes using it hang on its links (composites table, top-level parts: turrets, build-ups,
    /// cranes, flags; riders excluded): what ModelLibrary.DropBakedParts looks for in the model itself
    /// </summary>
    public static System.Func<string, IEnumerable<(string Link, string Gfx)>> PartsOnLinksOf(PwRemake.Sim.Rules.Rules D)
    {
        var map = new Dictionary<string, List<(string, string)>>();
        foreach (var (name, _) in D.Index)
        {
            var specs = D.Composites(name);
            if (specs.Count == 0) continue;
            var hosts = new HashSet<string>();
            for (int l = 1; l <= 5; l++) if (D.Stats(name, l, null)?.Gfx is { Length: > 0 } g) hosts.Add(g.ToLowerInvariant());
            if (D.ClassGfx(name) is string cg) hosts.Add(cg.ToLowerInvariant());
            foreach (var h in hosts)
            {
                if (!map.TryGetValue(h, out var l)) map[h] = l = new();
                foreach (var s in specs)
                    if (s.Pi < 0 && s.Kind != "rider")
                        foreach (var g in new[] { s.Gfx }.Concat(s.Variants))
                            if (!string.IsNullOrEmpty(g) && !l.Contains((s.Link, g.ToLowerInvariant()))) l.Add((s.Link, g.ToLowerInvariant()));
            }
        }
        return n => map.TryGetValue(n.ToLowerInvariant(), out var l) ? l : Enumerable.Empty<(string, string)>();
    }

    /// <summary>what the human player's tribe can make: the Build actions of its tech tree with a building (or not) as
    /// their result (what its build menus and its buildings offer, locked or not)</summary>
    public List<string> MyBuilds(bool buildings) => Rules.Actions(Me)
        .Where(a => a.Kind == "Build" && a.Results.Count > 0 && (Rules.Info(a.Results[0].Obj)?.Type == "BLDG") == buildings)
        .Select(a => a.Results[0].Obj).Distinct().ToList();

    /// <summary>the models of MyBuilds: a building's of every level, a unit's first (else the class file's, a mission's
    /// stand-in), lower case</summary>
    IEnumerable<string> MyBuildModels(bool buildings)
    {
        var D = Rules;
        foreach (var b in MyBuilds(buildings))
        {
            // (a building: every level's model; a unit: the one it comes out with - the upgraded ones load when they show)
            for (int l = 1; l <= (buildings ? 5 : 1); l++) if (D.Stats(b, l, Me)?.Gfx is { Length: > 0 } g) yield return g.ToLowerInvariant();
            // (the class file's model or the stand-in only when the tech tree's is not in the data: GameInput.GhostModel)
            if (D.Stats(b, 1, Me)?.Gfx is { Length: > 0 } g1 && EntityViews.HasFileQuiet(Models, g1)) continue;
            if (D.ClassGfx(b) is { Length: > 0 } cg) yield return cg.ToLowerInvariant();
            if (World.StandIns.TryGetValue(b.ToLowerInvariant(), out var si)) yield return si.ToLowerInvariant();
        }
    }

    /// <summary>
    /// the first-time costs of placing a building, paid as the game starts: the ghost and the building of everything
    /// the player can build drawn once out of sight (the shaders compiled: GameInput.WarmUpPlacement), the card
    /// sheets of the build menus read (a PNG each) and their cards greyed (the locked ones). Not with --no-warmup (tests: the costs as they were - the BUILDLAG
    /// lines of --ghost-rounds).
    /// </summary>
    void WarmUp()
    {
        if (_args.ContainsKey("no-warmup")) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Input.WarmUpPlacement(MyBuilds(true));
        // (the cards as the command bar draws them, also greyed - locked, dimmed: PwRemake.UI.Hud.HudCommandBar)
        var files = new HashSet<string>();
        foreach (var a in Rules.Actions(Me))
        {
            if (a.Kind != "Build" || a.Results.Count == 0) continue;
            var icon = UiAtlas.Entry(a.Id, 3) != null ? a.Id : UiAtlas.IconFor(a.Results[0].Obj);
            if (UiAtlas.Entry(icon, 3) is not { } e) continue;
            if (files.Add(e.File)) HudArt.Tex(e.File);
            PwRemake.UI.Hud.HudSprites.Gray(icon, 3, 0.5f, 1f); PwRemake.UI.Hud.HudSprites.Gray(icon, 3, 0.75f, 0.4f);
        }
        L.Info($"warm-up: {files.Count} card sheets and their greyed cards, {sw.ElapsedMilliseconds} ms in all");
    }

    List<(string Name, ModelLoadOptions Opts)> PreloadList()
    {
        var D = Rules; var M = Models.Manifest; var C = Mission?.Campaign; var S = Sim.Map;
        // (a skirmish: the tribes its players play, main.js loadModels with the resolved slots' tribes)
        var tribes = C != null && C.FullTribes.Count > 0 ? C.FullTribes : C != null ? new List<string> { Me.Tribe } : Sequel != null ? Sequel.Runtime.Tribes().ToList() : World.Players.Select(p => p.Tribe).Distinct().ToList();
        var skip = TRIBE_PREFIX.Where(kv => !tribes.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        // mapModels: the map's own models, its animals and nests, the felled trees' parts
        var need = new HashSet<string>(S.Models.Select(m => m.ToLowerInvariant()));
        foreach (var sp in S.Animals.Select(a => a.Species).Concat(S.Nests.Select(n => n.Species)).Distinct())
        {
            for (int l = 1; l <= 5; l++) if (D.Stats(sp, l, null)?.Gfx is { Length: > 0 } g) need.Add(g.ToLowerInvariant());
            if (D.ClassGfx(sp) is string cg) need.Add(cg);
        }
        var key = S.Setting switch { "Northland" => "nor", "Savanna" => "sav", "Icewaste" => "ice", "Ashvalley" => "ash", _ => "jun" };
        foreach (var n in new[] { "product_wood_jun", "product_stone_jun", $"product_wood_{key}", $"product_stone_{key}", "jungle_tree_01_stump", "jungle_tree_01_timber", "jungle_tree_02_timber", "jungle_fruit_bush" }) need.Add(n);
        if (C != null) foreach (var m in C.Need) need.Add(m.ToLowerInvariant());
        // the animals the map's breeding buildings and the player's own will breed (the kennel's sabre-tooths ...:
        // World.BreedsClass) - their model loaded when the first was born, a 200 - 400 ms frame (2026-11-20, Outbreak)
        foreach (var bc in World.Buildings.Select(b => b.Name).Concat(MyBuilds(true)).Distinct().Select(n => PwRemake.Sim.World.World.BreedsClass(D.Def(n, null)?.Script)).Where(c => c != null).Distinct())
        {
            for (int l = 1; l <= 5; l++) if (D.Stats(bc!, l, null)?.Gfx is { Length: > 0 } g) need.Add(g.ToLowerInvariant());
            if (D.ClassGfx(bc!) is string bg) need.Add(bg.ToLowerInvariant());
        }
        // the remake's own campaigns: their machines, civilians, creatures, set pieces, the sequel's effect models
        if (Sequel != null) foreach (var m in Sequel.Runtime.ExtraModels()) need.Add(m.ToLowerInvariant());
        // the game's effect models (ui/gamefx.js GAME_FX_MODEL: hits, pings, level-up, deaths ...; small - every one)
        foreach (var n in M.Models.Keys) if (PwRemake.View.Fx.GameFx.GAME_FX_MODEL.IsMatch(n)) need.Add(n);
        // the wild animals: loaded only when the map has them (and not models the tribes use themselves)
        var wild = new HashSet<string>();
        foreach (var (name, ix) in D.Index) if (ix.Tribe == "World" && ix.Type == "ANML" && D.Stats(name, 1, null)?.Gfx is { Length: > 0 } g) wild.Add(g.ToLowerInvariant());
        foreach (var (name, ix) in D.Index)
        {
            if (ix.Tribe == "World") continue;
            for (int l = 1; l <= 5; l++) if (D.Stats(name, l, null)?.Gfx is { Length: > 0 } g) wild.Remove(g.ToLowerInvariant());
            if (D.ClassGfx(name) is string cg) wild.Remove(cg);
        }
        bool Optional(string n) { var i = M.Info(n)!; return i.Map || LANDSCAPE.IsMatch(i.Arch) || wild.Contains(n); }
        var want = new List<string>();
        var have = new HashSet<string>();
        void Want(string n) { if (M.Info(n) != null && have.Add(n)) want.Add(n); }
        // (every model of the tribes, as the JS: --preload all; else what the mission's objects and spawns use - the
        // tribes' other models load when they first show)
        // --preload start: only the models of what stands on the map at the start (machines with little memory: a
        // character's template with its clips takes 10 - 30 MB)
        var mode = _args.GetValueOrDefault("preload", "need");
        bool all = mode == "all";
        if (mode == "start") need = new HashSet<string>(S.Models.Select(m => m.ToLowerInvariant()).Concat(World.Props.Select(p => p.Model.ToLowerInvariant())));
        if (all) foreach (var n in M.Models.Keys) if (!skip.Any(p => n.StartsWith(p)) && (!Optional(n) || need.Contains(n))) Want(n);
        foreach (var n in need) Want(n);
        foreach (var e in World.Units.Cast<PwRemake.Sim.World.Entity>().Concat(World.Buildings)) if (e.Gfx is { Length: > 0 } g) Want(g.ToLowerInvariant());
        // the buildings the player can build (its build menus): the first ghost of one whose model was not loaded yet
        // hitched (user report: "a lag when we build a building for the first time") - their models, every level's
        // (the parts they attach: below)
        // the units it can make likewise (the first unit of a kind coming out of its building hitched the same way) -
        // not with --preload start (a character's template with its clips takes 10 - 30 MB; the buildings' are small)
        int before = want.Count;
        bool warm = !_args.ContainsKey("no-warmup");
        if (warm) foreach (var g in MyBuildModels(true)) Want(g);
        int builds = want.Count - before;
        if (warm && mode != "start") foreach (var g in MyBuildModels(false)) Want(g);
        int units = want.Count - before - builds;
        // the parts the tribes' objects and the map's own objects attach (compose.js)
        foreach (var (name, ix) in D.Index)
        {
            var gfx = (D.Stats(name, 1, null)?.Gfx ?? D.ClassGfx(name) ?? "").ToLowerInvariant();
            if (!(all && tribes.Contains(ix.Tribe)) && !need.Contains(gfx) && !have.Contains(gfx)) continue;
            foreach (var s in D.Composites(name)) foreach (var g in new[] { s.Gfx }.Concat(s.Variants)) if (!string.IsNullOrEmpty(g)) Want(g.ToLowerInvariant());
        }
        // a class whose tech tree model doesn't exist is drawn with its class file's model
        foreach (var (name, ix) in D.Index)
        {
            if (!all || ix.Tribe == "World" || skip.Any(p => name.ToLowerInvariant().StartsWith(p))) continue;
            var cg = D.ClassGfx(name);
            if (cg == null || M.Info(cg) == null || have.Contains(cg)) continue;
            for (int l = 1; l <= 5; l++) if (D.Stats(name, l, null)?.Gfx is { Length: > 0 } g && M.Info(g.ToLowerInvariant()) == null) { Want(cg); break; }
        }
        foreach (var n in want.ToList()) if (M.Info(n)?.Anims is { Length: > 0 } a) Want(a.ToLowerInvariant());
        // the entity views prepare wall pieces and gates their own way (EntityViews.OptionsOf); only files in the data
        var (walls, gates) = EntityViews.WallModels(D);
        var res = new List<(string, ModelLoadOptions)>();
        int missing = 0;
        foreach (var n in want)
        {
            // (a model whose animation source is not in the data either cannot be loaded)
            if (!EntityViews.HasFileQuiet(Models, n) || (M.Info(n)?.Anims is { Length: > 0 } src && !EntityViews.HasFileQuiet(Models, src))) { missing++; continue; }
            res.Add((n, EntityViews.OptionsOf(n, walls, gates)));
        }
        L.Info($"models: {want.Count} wanted for {string.Join(", ", tribes)} ({need.Count} of the map and the mission, {builds} more of the buildings and {units} of the units you can make), {res.Count} in the data, {missing} files missing");
        return res;
    }
}
