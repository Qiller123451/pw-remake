// The world of a map, built from a map source (docs/MAPS.md "How the world uses it"): the ground (the map's height
// samples), the nav grids, the landscape objects (trees, scenery that blocks, stones, fruit bushes, fish), the
// players (a campaign: Campaign.Build), the map's wild animals and nests, the computer players' brains.
//
// Ported from the simulation part of remake/src/main.js (pw-exporter 98c7703): buildWorld (without the renderer, the
// terrain mesh, grass, sprites, the minimap - the view's), mapSource, mapRegions and createBrains (the campaign's
// part; skirmishPlayers is the skirmish setup's and not needed by a mission). The view reads the same MapSource and
// HeightField to draw.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Campaign;

/// <summary>What a map source asks of the rules (docs/MAPS.md: the adapter, one line per question).</summary>
public sealed class RulesMapAdapter : IMapRules
{
    readonly Rules.Rules _r;
    public RulesMapAdapter(Rules.Rules rules) { _r = rules; }
    public string? ClassGfx(string name) => _r.ClassGfx(name);
    public string? Script(string name) => _r.Script(name);
    public double? ResourceValue(string section, string name) { double v = _r.ResourceValue(section, name, 0); return v > 0 ? v : null; }
    public bool Exists(string name) => _r.Exists(name);
    public string? StatsGfx(string name) => _r.Stats(name, 1, null)?.Gfx is { Length: > 0 } g ? g : null;
    public string? NestSpecies(string nestClassLower) => null;           // (gamedata "nests": absent in today's data)
}

/// <summary>The world's map: a map source with the world's height field (main.js: `height = (x, z) => hf.at(x, z)` -
/// the world and the terrain mesh read the same samples; buildings put down level them).</summary>
public sealed class SourceMap : IWorldMap
{
    public readonly MapSource S;
    public readonly HeightField Hf;
    public SourceMap(MapSource s) { S = s; Hf = new HeightField(s.Size, s.MeshCell, s.Height); }
    public double Size => S.Size;
    public PlayRect Play => new(S.Play.X0, S.Play.X1, S.Play.Z0, S.Play.Z1);
    public double Height(double x, double z) => Hf.At(x, z);
    public double? Water => S.Water;
    public string Setting => S.Setting;
    public double? StartTime => S.StartTime;
    public double[] WallGrid => S.WallGrid ?? new double[] { 0, 0 };
    public Func<double, double, bool>? Walkable => S.Walkable;
    public double MaxSlope => S.MaxSlope ?? MapSource.MAX_SLOPE;
    public string Kind => S.Kind;
}

/// <summary>What WorldBuilder.Build made besides the world: the map, the ground's logs (a saved game does them again).</summary>
public sealed class BuiltWorld
{
    public GameWorld World = null!;
    public SourceMap Map = null!;
    /// <summary>buildings that levelled the ground: (gfx, x, z, rot) in order</summary>
    public readonly List<(string Gfx, double X, double Z, double Rot)> GroundLog = new();
    /// <summary>the ground brushes AdaptTerrain uses (with BuildingGround's fallback; the view paints with the same)</summary>
    public GroundBrushes? Brushes;
    /// <summary>ClearVegetation calls: (x, z, r)</summary>
    public readonly List<(double X, double Z, double R)> VegetationLog = new();
}

public static class WorldBuilder
{
    static readonly Logger L = Log.For("Campaign.World");
    sealed class DecorEntry { public double X, Z; public List<int>? Cells; public bool Gone; }

    /// <summary>main.js mapSource for a campaign mission: the original map (.ula below the installation's Data folder,
    /// any of the configuration's folders) without the objects the campaign places itself (skip)</summary>
    public static MapSource MapSourceOf(string ulaPath, Rules.Rules rules, ModelManifest models, ISet<string>? skip, ForestData? forest)
    {
        var md = MapFiles.Read(ulaPath);              // (with the remake's biome layer, or a remake map: docs/BIOMES.md)
        return MapSource.OriginalSource(md, new RulesMapAdapter(rules), new MapSourceConfig { SkipObjects = skip }, n => models.Has(n), forest);
    }

    /// <summary>the .ula of a mission: entry.Map ("Base/Maps/Cpn_single_001/single_11.ula") below the installation's Data
    /// folder, else the same path below each of the configuration's folders (the last one that has it)</summary>
    public static string? FindMap(GameConfiguration cfg, string map)
    {
        if (string.IsNullOrEmpty(map)) return null;
        MapFiles.UseInstall(cfg.Install);
        if (MapFiles.FindRemake(map) is { } rm) return rm;       // "Remake/<file>.pwmap.json": game/maps
        var p = PathUtil.FindCaseInsensitive(Path.Combine(cfg.Install, "Data"), map);
        if (p != null) return p;
        var rest = Regex.Replace(PathUtil.Norm(map), "^[^/]+/", "");
        return cfg.GameFile(rest);
    }

    /// <summary>main.js buildWorld (the simulation): the world on the map source, its landscape, the campaign's players
    /// and objects (c.Build), the map's wild animals and nests (c.WildlifeReady). The brains and the debug options come
    /// after (MissionSession).</summary>
    /// <param name="players">without a campaign: puts the players on the map (a skirmish: SkirmishSession; main.js
    /// skirmishPlayers) - called where main.js makes the players, after the landscape and before the animals</param>
    public static BuiltWorld Build(MapSource S, Rules.Rules D, ModelManifest M, WorldOptions opts, Campaign? c, GroundBrushes? brushes = null, Action<GameWorld>? players = null)
    {
        var map = new SourceMap(S);
        var hf = map.Hf;
        var W = new GameWorld(D, M, map, opts);
        var res = new BuiltWorld { World = W, Map = map };
        var nav = W.Nav;
        bool Tpl(string? m) => !string.IsNullOrEmpty(m) && M.Has(m);
        double RadiusOf(string name) { var b = M.Box(name); return b == null ? 0 : Math.Max(b.Value.Max.X - b.Value.Min.X, b.Value.Max.Z - b.Value.Min.Z) / 2; }
        // AdaptTerrain: under a building put down in the game the ground is levelled and takes the brush's materials
        // (Maps/Ground.cs; the view repaints the cells of the logged brush)
        if (brushes != null)
        {
            BuildingGround.Use(brushes, D, M);         // (every building a brush: the original's, else one of its tribe's)
            res.Brushes = brushes;
            W.AdaptTerrain = (gfx, x, z, rot) =>
            {
                var b = brushes.Get(gfx);
                if (b == null) return false;
                Ground.LevelGround(hf, b, brushes.Mpp, x, z, rot);
                res.GroundLog.Add((gfx, x, z, rot));
                W.Emit("ground", new Args { ["x"] = x, ["z"] = z, ["r"] = Math.Sqrt(b.W * b.W + b.H * b.H) * brushes.Mpp / 2 + 8 });   // the grass follows the new ground
                return true;
            };
        }

        // ---------------------------------------------------------------- map content
        foreach (var t in S.Trees)
        {
            if (!Tpl(t.Model)) continue;
            var cells = nav.MarkCircle(t.X, t.Z, (t.Big ? 1.6 : 1.1) * t.Scale);
            if (!t.Inside) continue;
            var stump = Tpl(t.Stump) ? t.Stump : Tpl("jungle_tree_01_stump") ? "jungle_tree_01_stump" : null;
            var timber = Tpl(t.Timber) ? t.Timber : null;     // (the falling log: the view's)
            W.AddResource("tree", "wood", t.X, t.Z, t.Wood, new ResOpts { Radius = t.Big ? 1.5 : 1.1, Cells = cells, StumpKind = stump, TimberKind = timber, Rot = t.Rot, Scale = t.Scale, Name = t.Model, MaxWorkers = 4 });
        }
        // the map's plants and decoration, for ClearVegetation
        var decorList = new List<DecorEntry>();
        var spriteBush = new Regex("^jungle_(bush_|underwood_|mountain_bush)"); var spriteFlower = new Regex("^jungle_(flowers|reed|wildrice|beachgrass)");
        var sequelDeco = new Regex("^sq_");
        foreach (var d in S.Decor)
        {
            if (spriteBush.IsMatch(d.Model) || spriteFlower.IsMatch(d.Model)) { decorList.Add(new DecorEntry { X = d.X, Z = d.Z }); continue; }   // (leaf sprites: the view's)
            if (!Tpl(d.Model)) continue;
            // what stands in the way: the model's pathfinder boxes; a model without any blocks nothing on an original map
            // (the engine has nothing to block with; the map's walk grid holds the rest - an ice arch is walked under), a
            // circle on the remake's own maps
            List<int>? cells = null;
            if (d.Block)
            {
                cells = Prop.PfCells(nav, M, d.Model, d.X, d.Z, d.Rot, d.Scale);
                // a bridge without boxes: the ground just under its deck (World/Bridges.cs - not walked out of sight)
                if (cells == null && S.Walkable != null && d.Y != null && Bridges.IsBridge(d.Model))
                    cells = Bridges.Shade(nav, M, hf.At, d.Model, d.X, d.Z, d.Rot, d.Y.Value, d.Scale);
                if (cells != null) nav.Mark(cells, 1);
                else if (S.Walkable == null) cells = nav.MarkCircle(d.X, d.Z, Math.Min(24, RadiusOf(d.Model) * d.Scale * 0.7));
            }
            // the sequel's houses, cars and camps stand where the story put them (not plants)
            if (!sequelDeco.IsMatch(d.Model)) decorList.Add(new DecorEntry { X = d.X, Z = d.Z, Cells = cells });
        }
        // a building put down: the decoration, plants, trees and stumps within its radius go (ServerApp.usl PlaceObj, object
        // types DECO, VGTN, WOOD and ForestMgr.RemoveStumps); fruit bushes and stones stay
        W.ClearVegetation = (x, z, r) =>
        {
            foreach (var e in decorList)
            {
                if (e.Gone || Math.Sqrt((e.X - x) * (e.X - x) + (e.Z - z) * (e.Z - z)) > r) continue;
                e.Gone = true;
                if (e.Cells != null) nav.Mark(e.Cells, -1);
            }
            foreach (var n in W.Resources)
            {
                if ((n.Type != "tree" && n.Type != "timber") || Math.Sqrt((n.Pos.X - x) * (n.Pos.X - x) + (n.Pos.Z - z) * (n.Pos.Z - z)) > r) continue;
                if (n.Alive) W.DepleteNodeOf(n);
            }
            res.VegetationLog.Add((x, z, r));
        };
        foreach (var s in S.Stones)
        {
            if (!Tpl(s.Model)) continue;
            double r = Math.Max(2, RadiusOf(s.Model!) * 0.8);
            var cells = nav.MarkCircle(s.X, s.Z, r * 0.85);
            W.AddResource("stone", "stone", s.X, s.Z, s.Amount != 0 ? s.Amount : 2000, new ResOpts { Radius = r, Cells = cells, Name = s.Model, MaxWorkers = 8 });
        }
        foreach (var b in S.Bushes)
        {
            if (!Tpl(b.Model)) continue;
            double r = Math.Max(1.2, RadiusOf(b.Model!) * 0.7);
            var cells = nav.MarkCircle(b.X, b.Z, r * 0.6);
            W.AddResource("bush", "food", b.X, b.Z, b.Amount != 0 ? b.Amount : 150, new ResOpts { Radius = r, Cells = cells, Name = b.Model, MaxWorkers = 4 });
        }
        // fish shoals: food in the water (fishing boats)
        foreach (var f in S.Fish)
            W.AddResource("fish", "food", f.X, f.Z, f.Amount != 0 ? f.Amount : 1000, new ResOpts { Radius = 3, Water = true, Name = f.Model ?? "fish", MaxWorkers = 2 });

        // ---------------------------------------------------------------- players
        if (c != null)
        {
            // a campaign mission: the players, objects, groups and start locations of the mission data (Campaign.cs)
            c.ItemPool = S.ItemPool;
            c.Build(W, S.Origin);
        }
        else players?.Invoke(W);
        Unit? WildAt(string species, double x, double z, double[] home, bool swim)
        {
            if (swim && W.WaterNav == null) return null;
            var u = W.SpawnUnit(species, null, x, z, null, W.Rng.Next() * 6.28, swim ? new UnitOpts { Swim = true } : null);
            if (u != null) { u.HomeX = home[0]; u.HomeZ = home[1]; }
            return u;
        }
        foreach (var a in S.Animals) WildAt(a.Species, a.X, a.Z, a.Home, a.Swim);
        // nests of original maps keep respawning their animals
        // (Nest.OnPostLoad Advance_Population: the animals born before the start stand somewhere in the action area)
        foreach (var n in S.Nests)
        {
            if (n.Swim && W.WaterNav == null) continue;
            var nest = W.AddWildNest(n);
            if (nest == null) continue;
            c?.MapNests.Add((n, nest));
            foreach (var a in n.Start)
            {
                var p = W.NestPoint(nest, false, true) ?? (n.X, n.Z);
                var u = WildAt(a.Species, p.X, p.Z, new[] { n.X, n.Z }, a.Swim);
                if (u == null) continue;
                W.JoinNest(nest, u, a.Born);
                W.FlockAdd(nest.Flock!, u);
            }
            nest.Alive = nest.Members.Count;
            W.UpdateFlockPos(nest);
        }
        if (c != null) c.WildlifeReady();
        // healing wells, lakes of healing / damage, the weather program: a campaign's wells and lakes are objects of its
        // mission (Campaign.Build); the lakes' regions are the map's (mapRegions)
        if (c == null)
        {
            var regions = new Regions(new System.Text.Json.Nodes.JsonArray(S.Regions.Select(r => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
            {
                ["guid"] = r.Name, ["name"] = r.Name,
                ["shapes"] = new System.Text.Json.Nodes.JsonArray(r.Shapes.Select(q => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject { ["type"] = q.Oval ? "oval" : "rect", ["enabled"] = q.Enabled, ["x"] = q.X, ["y"] = q.Y, ["w"] = q.W, ["h"] = q.H }).ToArray()),
            }).ToArray()), (mx, my) => S.ToGame(mx, my), (x, z) => S.ToMap(x, z));
            W.RegionOf = nm => regions.Find(nm);
            foreach (var w in S.Wells) W.PlaceWell(w.Model, w.Cls, w.X, w.Z, w.Y, w.Rot, w.Q, w.Attr);
            foreach (var l in S.Lakes) W.AddLake(l);
            // the level's items (artefacts): placed ones, then what the item spawns make (a campaign's own: Campaign.Build)
            foreach (var i in S.Items) W.PlaceItem(i.Cls, i.X, i.Z, i.Rot);
            if (S.ItemSpawns.Count > 0)
                W.GenerateItems(S.ItemSpawns.Select(s => new ItemSpawn { X = s.X, Z = s.Z, Rot = s.Rot, Items = s.Items.ToList(), Always = s.Always, Random = s.Random }).ToList(), S.ItemPool.Pool, (int)S.ItemPool.Max);
        }
        W.StartWeather(S.Weather);
        return res;
    }

    /// <summary>main.js createBrains for a campaign: every AI slot of a campaign map sleeps ('ai_Mikrobe') until a trigger
    /// wakes it (AIBV); its difficulty is the map's AI_Difficulty_Easy|Medium|Hard for the chosen campaign difficulty
    /// (CampaignMgr.usl:132-139). Raids as the original makes them, the remake's bigger squads on Nightmare only.</summary>
    public static void CreateBrains(Campaign c, IBrainFactory? factory)
    {
        var W = c.World;
        W.Brains.Clear();
        if (factory == null) return;
        var key = new[] { "easy", "medium", "hard" }[Math.Clamp(c.Difficulty, 0, 2)];
        var levelName = "Single " + c.Id.ToString().PadLeft(2, '0');
        foreach (var pl in c.Players)
        {
            var d = pl?.Data;
            if (pl == null || pl == c.Human || !pl.Ai || c.Coop?.IsSeatSlot(pl.Id) == true || d == null || !d["control"].Str().StartsWith("ai_", StringComparison.Ordinal)) continue;   // (!Ai: a co-op human's)
            var ad = d["ai_difficulty"]?[key];
            int diff = ad is System.Text.Json.Nodes.JsonValue ? ad.Int() : new[] { 1, 4, 8 }[Math.Clamp(c.Difficulty, 0, 2)];
            var behaviour = d["control"].Str().Substring(3);
            var b = factory.Create(W, pl, new BrainOptions
            {
                Difficulty = diff, Behaviour = behaviour.Length > 0 ? behaviour : "Mikrobe", MapOptions = c.Map["ai_options"] ?? new System.Text.Json.Nodes.JsonObject(),
                Multimap = false, LevelName = levelName, ExtraSquads = c.Nightmare,
            }, c);
            if (b != null) W.Brains.Set(pl.Id, b);
        }
    }
}
