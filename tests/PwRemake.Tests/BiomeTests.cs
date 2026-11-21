// Biomes (docs/BIOMES.md): an original map stays as it was (its weights, its grass), the shader's layer choice
// (LayerPick), the layer file, and the demo map (game/maps/frost_to_jungle.pwmap.json) in the skirmish list and in a
// skirmish - which plays exactly as its base map (biomes are drawn and heard, never simulated).
using System;
using System.IO;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;

namespace PwRemake.Tests;

[TestClass]
public static class BiomeTests
{
    const string INSTALL = "/mnt/user-data/uploads/Paraworld";
    const string DEMO = "Remake/frost_to_jungle.pwmap.json";

    /// <summary>the original settings' biomes in game/maps/biomes.json are the remake's look before biomes (the grass
    /// table GrassLayer.GRASS_DENSITY had, the setting's atlas, no light of their own)</summary>
    public static void BiomesOriginalSettings(TestContext t)
    {
        var set = BiomeSet.Default;
        t.True(set.Source.EndsWith("biomes.json"), "game/maps/biomes.json read: " + set.Source);
        var old = new System.Collections.Generic.Dictionary<string, double[]>
        {
            ["Jungle"] = new[] { 0.75, 0.04, 1.0, 0.3, 0.15, 0.0, 0.04, 0.04 },
            ["Northland"] = new[] { 1.0, 0.05, 0.85, 0.3, 0.04, 0.1, 0.1, 0.0 },
            ["Savanna"] = new[] { 0.12, 0.04, 1.0, 0.5, 0.0, 0.04, 0.04, 0.12 },
            ["Icewaste"] = new[] { 0.0, 0.0, 0.8, 0.08, 0.0, 0.3, 0.0, 0.0 },
            ["Ashvalley"] = new[] { 0.0, 0.1, 0.9, 0.2, 0.0, 0.05, 0.0, 0.0 },
        };
        foreach (var o in BiomeSet.Original().All)
        {
            var b = set.Get(o.Id);
            t.True(b != null && b.Original, o.Id + " in the file");
            t.True(b!.Look == null && b.Snow == 1 && b.Rain == 1, o.Id + ": no light or weather of its own");
            t.Equal(o.Ground, b.Ground, o.Id + " ground");
            t.True(b.Roles.SequenceEqual(o.Roles), o.Id + " roles");
            t.Equal(o.Grass, b.Grass, o.Id + " grass atlas");
            var want = old.TryGetValue(o.Id, out var d) ? d : new double[8];
            t.True(b.GrassDensity.SequenceEqual(want), o.Id + " grass density = the old table");
        }
        foreach (var b in set.All.Where(b => !b.Original))
            t.True(Enumerable.Range(0, 8).All(k => set.Get(b.Ground) != null), b.Id + ": a known ground");
        t.True(BiomeSet.Remap(set.Get("Icewaste")!, set.Get("Jungle")!).SequenceEqual(new[] { 0, 1, 7, 3, 4, 5, 2, 4 }), "Icewaste -> Jungle by role: the snow land -> green land, seabed -> seabed, icy rock -> rock");
        // snow on the buildings: the Icewaste and the Frost (the file and the built-in table agree)
        foreach (var b in set.All)
            t.Equal(b.Id is "Icewaste" or "Frost" ? 1.0 : 0.0, b.SnowCover, b.Id + ": snow on the buildings");
        t.Equal(1.0, BiomeSet.Original().Get("Icewaste")!.SnowCover, "built in: the Icewaste's snow");
        t.True(BiomeSet.Parse(set.ToJson()).Get("Frost")!.SnowCover == 1, "written and read back");
    }

    /// <summary>the layer choice of the ground shader: a vertex's strongest 8; a tile's palette (one biome: its 8 in order)</summary>
    public static void BiomesLayerPick(TestContext t)
    {
        Span<int> idx = stackalloc int[8]; Span<float> w = stackalloc float[8];
        var ten = new double[] { 0, 0.5, 0, 0.1, 0.9, 0, 0, 0.2, 0.05, 0.3, 0, 0.01, 0.02, 0.03, 0.04, 0.06 };
        int c = LayerPick.Top(ten, 8, idx, w);
        t.Equal(8, c, "8 of 11 layers kept");
        t.True(idx.ToArray().SequenceEqual(new[] { 4, 1, 9, 7, 3, 15, 8, 14 }), "the strongest, strongest first: " + string.Join(",", idx.ToArray()));
        c = LayerPick.Top(new double[] { 0, 0.4, 0, 0.6 }, 8, idx, w);
        t.True(c == 2 && idx[0] == 3 && idx[1] == 1, "fewer than 8");
        // a tile in biome 1 (layers 8..15) using 3 materials: biome 1's 8 in order (neighbours share it)
        var pure = new double[24]; pure[10] = 3; pure[8] = 1; pure[13] = 0.5;
        t.True(LayerPick.Palette(pure).SequenceEqual(Enumerable.Range(8, 8)), "one biome: its materials in order");
        // a transition tile: the strongest 8 of both biomes
        var mix = new double[16];
        for (int k = 0; k < 16; k++) mix[k] = k % 3 == 0 ? 0 : 1 + k;
        var p = LayerPick.Palette(mix);
        t.True(p.SequenceEqual(new[] { 7, 8, 10, 11, 13, 14 }.Concat(new[] { 4, 5 }).OrderBy(x => x)), "two biomes: the 8 strongest: " + string.Join(",", p));
        // an original map: never more than 8 layers -> the identity everywhere (TerrainMesh)
        var orig = new double[8]; orig[2] = 1;
        t.True(LayerPick.Palette(orig).SequenceEqual(Enumerable.Range(0, 8)), "8 layers: 0..7");
        var pk = LayerPick.Pack(new[] { 0, 1, 2, 3, 120, 121, 126, 127 });
        t.True(pk[0] == 256 && pk[1] == 2 + 768 && pk[3] == 126 + 256 * 127, "two layers per float");
    }

    /// <summary>the layer file and the authoring tools' painting</summary>
    public static void BiomesLayerFile(TestContext t)
    {
        var l = BiomeLayer.Create(new[] { "Frost", "Rainforest" }, 0, 0, 512, 256);
        t.Equal(64, l.W, "cells of 8 m");
        BiomeAuthoring.Gradient(l, 0, 1, (200, 128), (312, 128), noise: 0);
        t.Near(1, l.At(20, 128)[0], 1e-9, "west: frost");
        t.Near(1, l.At(500, 128)[1], 1e-9, "east: rainforest");
        t.Near(0.5, l.At(256, 128)[1], 0.03, "the middle: half each");
        int b = l.AddBiome("Savanna");
        var r = BiomeAuthoring.Paint(l, b, 100, 100, 40, strength: 1, noise: 0);
        t.True(r != null && l.At(100, 100)[2] > 0.95 && l.At(300, 200)[2] == 0, "a blob of a third biome");
        var back = BiomeLayer.Parse(l.ToJson());
        t.True(back.Biomes.SequenceEqual(l.Biomes) && back.Weights.SequenceEqual(l.Weights) && back.W == l.W && back.Cell == l.Cell, "the file reads back the same");
        var set = BiomeSet.Parse(BiomeSet.Default.ToJson());
        t.Equal(BiomeSet.Default.All.Count, set.All.Count, "the biomes file reads back");
    }

    static CampaignTests.Cfg Cfg(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no base configuration");
        if (!Directory.Exists(Path.Combine(c!.Config.Install, "Data"))) t.Skip("no installation");
        return c;
    }

    /// <summary>
    /// an original map is unchanged: no biome layer, the ground layers are the setting's textures and the weights the
    /// map's own (the same array); with a layer of its own setting at 100 % the weights come out the same numbers
    /// </summary>
    public static void BiomesUnchangedMaps(TestContext t)
    {
        var c = Cfg(t);
        var maps = SkirmishMaps.List(c.Config).Where(m => m.Folder != MapFiles.RemakeFolder).ToList();
        t.True(maps.Count >= 5, "the multiplayer maps");
        foreach (var m in maps)
        {
            var S = WorldBuilder.MapSourceOf(m.FullPath, c.Rules, c.Models, null, c.Forest);
            t.True(S.BiomeLayer == null && S.BiomeAt == null, m.File + ": no biome layer");
            var hf = new HeightField(S.Size, S.MeshCell, S.Height);
            var splat = S.Splat(hf);
            var g = new GroundLayers(S);
            t.True(g.Single && g.Count == 8 && g.Textures.SequenceEqual(S.Textures), m.File + ": its setting's 8 textures");
            var same = new GroundLayers(S);
            S.UseBiomes(BiomeLayer.For(S.Md!, new[] { S.Setting }));
            var own = new GroundLayers(S);
            t.True(!own.Single && own.Textures.SequenceEqual(S.Textures), m.File + ": its own biome = its own textures");
            int diff = 0;
            for (int k = 0; k < 400; k++)
            {
                double x = (k % 20 - 9.5) * S.Size / 21, z = (k / 20 - 9.5) * S.Size / 21;
                var w = splat(x, z);
                if (!ReferenceEquals(same.Of(x, z, w), w)) diff++;
                var o = own.Of(x, z, w);
                if (!o.SequenceEqual(w)) diff++;
                if (Math.Abs(same.GrassDensity(0, w) - own.GrassDensity(0, o)) > 0) diff++;
            }
            t.Equal(0, diff, m.File + ": the same weights and grass");
        }
    }

    /// <summary>the demo map: listed under Remake, Frost in the west and Rainforest in the east with a transition, and a
    /// skirmish on it is the same game as on its base map (the same world state after 20 s)</summary>
    public static void BiomesDemoMap(TestContext t)
    {
        var c = Cfg(t);
        var e = SkirmishMaps.List(c.Config).FirstOrDefault(m => m.Path == DEMO);
        t.True(e != null, "the demo map in the skirmish list");
        t.Equal("Frost and Jungle", e!.Name, "its name");
        t.True(e.Players >= 2 && e.SetupModes.Contains("deathmatch"), $"{e.Players} players, deathmatch");
        var path = SkirmishMaps.Resolve(c.Config, DEMO);
        t.True(path != null && PwRemake.Sim.Maps.PwMapReader.IsMap(path), "resolved to the converted map (docs/MAP_FORMAT.md)");
        var S = WorldBuilder.MapSourceOf(path!, c.Rules, c.Models, null, c.Forest);
        t.True(S.BiomeLayer != null && S.BiomeLayer.Biomes.SequenceEqual(new[] { "Frost", "Rainforest" }), "its biomes");
        double h = S.Size / 2 - 40;
        t.True(S.BiomeAt!(-h, 0)[0] > 0.99 && S.BiomeAt(h, 0)[1] > 0.99, "frost in the west, rainforest in the east");
        var g = new GroundLayers(S);
        t.Equal(16, g.Count, "16 layers");
        var hf = new HeightField(S.Size, S.MeshCell, S.Height);
        var splat = S.Splat(hf);
        int both = 0;
        for (double x = -120; x <= 120; x += 8)
        {
            var bw = S.BiomeAt(x, 0);
            var L = g.Of(x, 0, splat(x, 0));
            if (bw[0] > 0.2 && bw[1] > 0.2 && L.Take(8).Sum() > 0.2 && L.Skip(8).Sum() > 0.2) both++;
            t.Near(splat(x, 0).Sum(), L.Sum(), 1e-9, "the weights' sum kept");
        }
        t.True(both >= 3, $"a transition with both biomes' ground ({both} points)");
        // snow on the buildings (SnowCover): in the frost, none in the rainforest, between them in the transition
        t.True(g.AnySnow && g.SnowTexture == Biome.SNOW_TEXTURE, "a map with snow, the Icewaste's snow texture");
        t.Near(1, g.SnowCover(-h, 0), 0.01, "snow on the buildings in the frost");
        t.Near(0, g.SnowCover(h, 0), 0.01, "none in the rainforest");
        int mid = Enumerable.Range(-15, 31).Count(k => g.SnowCover(k * 8, 0) is > 0.2 and < 0.8);
        t.True(mid >= 2, $"some in the transition ({mid} points)");
        // the setting textures of the buildings (SettingMaterials): the dominant biome's setting
        t.Equal("icewaste", g.SettingAt(-h, 0), "the Icewaste's building textures in the frost");
        t.Equal("jungle", g.SettingAt(h, 0), "the Jungle's in the rainforest");
        t.True(g.TextureSettings.SequenceEqual(new[] { "icewaste", "jungle" }), "the map's settings");
        var one = new GroundLayers(WorldBuilder.MapSourceOf(SkirmishMaps.Resolve(c.Config, "Base/Maps/Multiplayer/multi_4_ice_601.ula")!, c.Rules, c.Models, null, c.Forest));
        t.True(one.SettingAt(-h, 0) == "icewaste" && one.SettingAt(h, 0) == "icewaste", "its base map: the Icewaste everywhere (the original)");
        foreach (var s in S.Starts) t.Log($"start {s[0]:0}, {s[1]:0}: " + string.Join(", ", S.BiomeLayer!.Biomes.Zip(S.BiomeAt(s[0], s[1]), (b, w) => $"{b} {w:0.00}")));

        // the same skirmish on the demo map and on its base map: the same game
        SkirmishSetup Setup(string map) => new()
        {
            Seed = 3, Points = -1, ChooseHQ = true, Map = map, Slots = {
                new SkirmishSlot { Tribe = "Hu", Type = "ai", Level = 4, Behaviour = "Giraffe", Team = 1, Hq = 0 },
                new SkirmishSlot { Tribe = "Aje", Type = "ai", Level = 4, Behaviour = "Giraffe", Team = 2, Hq = 1 } },
        };
        var baseMap = SkirmishMaps.Resolve(c.Config, "Base/Maps/Multiplayer/multi_4_ice_601.ula")!;
        var a = SkirmishSession.Create(Setup(DEMO), path, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
        var b = SkirmishSession.Create(Setup("Base/Maps/Multiplayer/multi_4_ice_601.ula"), baseMap, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
        t.True(a.Map.BiomeLayer != null && b.Map.BiomeLayer == null, "the demo has biomes, the base map none");
        a.Run(20); b.Run(20);
        t.True(a.World.Units.Count > 0, "units on the demo map");
        t.Equal(b.StateHash(), a.StateHash(), "the same world after 20 s");
    }
}
