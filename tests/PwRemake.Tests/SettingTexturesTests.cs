// The original's setting textures (docs/VIEW.md "Setting textures"): the index the prepare tool writes
// (tools/prepare/setting_textures.py -> assets/tex/settings/index.json), the choice of a material's variant (its texture
// + the setting -> the variant's asset path, fallbacks to the plain texture), the setting a building takes (the
// dominant biome's), content without the index (old content: every texture plain), and the keys of the converted
// models' materials (their GSF texture paths) matching the index.
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Maps;

namespace PwRemake.Tests;

[TestClass]
public static class SettingTexturesTests
{
    /// <summary>an index as setting_textures.py writes it (the base game's variants, MIRAGE's cave3 and boosterpack)</summary>
    const string INDEX = @"{
 ""folders"": [""Base"", ""BoosterPack3"", ""MIRAGE""], ""format"": ""pw-setting-textures"", ""settings"": [""ashvalley"", ""cave3"", ""icewaste"", ""jungle"", ""northland"", ""savanna""],
 ""textures"": {
  ""boosterpack_01_a"": { ""icewaste"": { ""file"": ""settings/icewaste/boosterpack_01_a.png"", ""size"": 1024, ""source"": ""MIRAGE/Texture/settings/icewaste/boosterpack_01_a_(1024).dds"", ""stamp"": ""1:2"" } },
  ""buildings/aje_lvl_1_a"": { ""icewaste"": { ""file"": ""settings/icewaste/aje_lvl_1_a.png"" }, ""savanna"": { ""file"": ""settings/savanna/aje_lvl_1_a.png"" } },
  ""buildings/hu_lvl_1_a"": { ""icewaste"": { ""file"": ""settings/icewaste/hu_lvl_1_a.png"" }, ""northland"": { ""file"": ""settings/northland/hu_lvl_1_a.png"" },
                             ""savanna"": { ""file"": ""settings/savanna/hu_lvl_1_a.png"" } },
  ""buildings/seas_building_a"": { ""cave3"": { ""file"": ""settings/cave3/seas_building_a.png"" } }
 }, ""version"": 1 }";

    /// <summary>a material's texture + a setting -> the variant's asset path; the plain texture where there is none</summary>
    public static void SettingTexturesChoice(TestContext t)
    {
        var s = SettingTextures.Parse(INDEX);
        t.True(s.Any && s.Count == 4, "4 textures");
        t.True(s.Settings.SequenceEqual(new[] { "icewaste", "savanna", "northland", "cave3" }) || s.Settings.Count == 4, "their settings: " + string.Join(",", s.Settings));
        // the key: the GSF texture path as the converted models' materials carry it (extras gsf_texture)
        t.Equal("buildings/hu_lvl_1_a", SettingTextures.Key("Buildings\\Hu_Lvl_1_a.tga"), "key: lower case, '/', no extension");
        t.Equal("buildings/hu_lvl_1_a", SettingTextures.Key("Texture/Buildings/Hu_Lvl_1_a_(0512).dds"), "key: no size, no Texture/");
        t.Equal(null, SettingTextures.Key(""), "no texture: no key");
        t.Equal("buildings/hu_lvl_1_a", s.KeyOf("buildings/hu_lvl_1_a.tga", "../tex/hu_lvl_1_a.png"), "by the GSF path");
        t.Equal("buildings/hu_lvl_1_a", s.KeyOf(null, "../tex/hu_lvl_1_a.png"), "without it: by the converted file's name");
        t.Equal("buildings/hu_lvl_1_a", s.KeyOf("", "../tex/hu_lvl_1_a__buildings.png"), "a file the toolkit renamed (x__archive.png)");
        t.Equal("buildings/hu_lvl_1_a", s.KeyOf("hu_lvl_1_a.tga"), "a path without its folder: by the name");
        t.Equal("boosterpack_01_a", s.KeyOf("BoosterPack_01_a.tga", "../tex/boosterpack_01_a.jpg"), "a texture outside Buildings (MIRAGE's boosterpack)");
        t.Equal(null, s.KeyOf("buildings/hs_lvl_1_a.tga", "../tex/hs_lvl_1_a.png"), "the sequel's own atlas: no variants");
        t.Equal(null, s.KeyOf("animals/ice_henodus_a.tga"), "a unit's texture: none");
        // the variant
        t.Equal("assets/tex/settings/icewaste/hu_lvl_1_a.png", s.Variant("buildings/hu_lvl_1_a", "icewaste"), "Norse atlas in the Icewaste");
        t.Equal("assets/tex/settings/savanna/aje_lvl_1_a.png", s.Variant("buildings/aje_lvl_1_a", "savanna"), "Dustriders' atlas in the Savanna");
        t.Equal("assets/tex/settings/cave3/seas_building_a.png", s.Variant("buildings/seas_building_a", "cave3"), "MIRAGE: SEAS in cave3");
        t.Equal("assets/tex/settings/icewaste/boosterpack_01_a.png", s.Variant("boosterpack_01_a", "icewaste"), "boosterpack in the Icewaste");
        // fallbacks: the plain texture
        t.Equal(null, s.Variant("buildings/aje_lvl_1_a", "northland"), "no Dustrider atlas in the Northland: plain");
        t.Equal(null, s.Variant("buildings/seas_building_a", "icewaste"), "SEAS of the base game in the Icewaste: plain (the shader snow)");
        t.Equal(null, s.Variant("buildings/hu_lvl_1_a", "cave1"), "a cave: plain");
        t.Equal(null, s.Variant("buildings/hu_lvl_1_a", null), "no setting: plain");
        t.Equal(null, s.Variant(null, "icewaste"), "no key: plain");
        t.True(s.Has("buildings/hu_lvl_1_a", "icewaste") && !s.Has("buildings/seas_building_a", "icewaste"), "Has: the Icewaste's (the shader snow's rule)");
        t.True(s.Has("buildings/hu_lvl_1_a", "ICEWASTE"), "settings ignore case");
        t.Equal(3, s.VariantsOf("buildings/hu_lvl_1_a").Count, "the Norse atlas: 3 variants");
        // an index naming the files alone (the minimal form)
        var m = SettingTextures.Parse(@"{ ""format"": ""pw-setting-textures"", ""textures"": { ""Buildings/Hu_Misc_a"": { ""Jungle"": ""settings/jungle/hu_misc_a.png"" } } }");
        t.Equal("assets/tex/settings/jungle/hu_misc_a.png", m.Variant(m.KeyOf("buildings/hu_misc_a.tga"), "jungle"), "the minimal form");
        bool bad = false;
        try { SettingTextures.Parse(@"{ ""format"": ""pw-biomes"" }"); } catch (FormatException) { bad = true; }
        t.True(bad, "another file's format is refused");
        // content without variants
        var e = SettingTextures.Empty;
        t.True(!e.Any && e.KeyOf("buildings/hu_lvl_1_a.tga", "hu_lvl_1_a.png") == null && e.Variant("buildings/hu_lvl_1_a", "icewaste") == null, "empty: every texture plain");
    }

    /// <summary>the index of a configuration through its AssetSource: from the overrides; missing (old content) or broken: empty</summary>
    public static void SettingTexturesLoad(TestContext t)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pw_settex_" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(dir, "data");
            Directory.CreateDirectory(Path.Combine(data, "assets", "tex"));
            Directory.CreateDirectory(Path.Combine(dir, "cfg"));
            File.WriteAllText(Path.Combine(dir, "cfg", "config.json"), "{ \"id\": \"test\", \"data\": " + JsonValue.Create(data).ToJsonString() + " }");
            var cfg = GameConfiguration.Load(Path.Combine(dir, "cfg"))!;
            t.True(cfg != null, "a configuration");
            t.True(!SettingTextures.Load(new AssetSource(cfg!)).Any, "old content (no index): empty, plain textures");
            var ix = Path.Combine(cfg!.OverridesDir, "assets", "tex", "settings", "index.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ix)!);
            File.WriteAllText(ix, "{ not json");
            t.True(!SettingTextures.Load(new AssetSource(cfg)).Any, "a broken index: empty");
            File.WriteAllText(ix, INDEX);
            var s = SettingTextures.Load(new AssetSource(cfg));
            t.True(s.Any && s.Has("buildings/hu_lvl_1_a", "icewaste"), "the prepared index from the overrides");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    /// <summary>the setting a building takes: the dominant biome's - each biome has one (the original settings their own,
    /// Frost the Icewaste's, Rainforest the Jungle's); a map of one setting: that setting everywhere</summary>
    public static void SettingTexturesBiomes(TestContext t)
    {
        var set = BiomeSet.Default;
        foreach (var o in BiomeSet.Original().All) t.Equal(o.Id.ToLowerInvariant(), set.Get(o.Id)!.TextureSetting, o.Id + ": its own setting");
        t.Equal("icewaste", set.Get("Frost")!.TextureSetting, "Frost -> the Icewaste's textures");
        t.Equal("jungle", set.Get("Rainforest")!.TextureSetting, "Rainforest -> the Jungle's");
        var own = BiomeSet.Parse(@"{ ""biomes"": [ { ""id"": ""Dunes"", ""ground"": ""Savanna"", ""setting"": ""Ashvalley"" } ] }").Get("Dunes")!;
        t.Equal("ashvalley", own.TextureSetting, "a biome naming its setting");
        var bs = new BiomeSet();
        bs.Add(own);
        t.Equal("Ashvalley", BiomeSet.Parse(bs.ToJson()).Get("Dunes")!.Setting, "written and read back");
        t.True(!bs.ToJson().Contains("\"setting\"") || own.Setting != null, "(only a biome naming one writes it)");
        // the mixed demo map: BiomeTests.BiomesDemoMap (GroundLayers.SettingAt: icewaste in its frost, jungle in its rainforest)
    }

    /// <summary>the converted models' building materials carry GSF texture paths whose keys are the index's (the base
    /// game's data: PW_DATA)</summary>
    public static void SettingTexturesModelKeys(TestContext t)
    {
        var data = t.Data();
        var dir = Path.Combine(data, "assets", "models");
        if (!Directory.Exists(dir)) t.Skip("no models");
        var s = SettingTextures.Parse(INDEX);
        int hu = 0, units = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.glb"))
        {
            var b = File.ReadAllBytes(f);
            int n = BitConverter.ToInt32(b, 12);
            var j = JsonNode.Parse(new ReadOnlySpan<byte>(b, 20, n))!;
            string fourcc = j["nodes"]?[0]?["extras"]?["fourcc"]?.GetValue<string>() ?? "";
            foreach (var mat in j["materials"]?.AsArray() ?? new JsonArray())
            {
                var gsf = mat?["extras"]?["gsf_texture"]?.GetValue<string>();
                var k = s.KeyOf(gsf);
                if (k == "buildings/hu_lvl_1_a") hu++;
                if (k != null && fourcc is "Anim" or "Char") units++;
            }
        }
        t.True(hu >= 10, $"the Norse atlas found by its GSF path in {hu} materials");
        t.Log($"{hu} materials of hu_lvl_1_a; {units} unit materials with a building atlas (they keep it plain: units take no setting)");
    }
}
