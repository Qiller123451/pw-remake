// Tests of the campaign menu's "Custom campaigns" (src/Sim/Campaign/CustomCampaigns.cs, docs/CAMPAIGN.md "Custom
// campaigns"): the configuration's missions grouped from both configurations' index.json (the original campaign, the
// Booster Pack, MIRAGE's own missions; a mission from a data folder the configuration lacks left out), the end screen's
// next mission inside a group, campaign.lua read / write / checked, the unlock order, the .pwcampaign export and import
// round trip (and a file outside the campaign refused), an edited copy of a campaign mission recognised in My maps.
// Data: PW_CONTENT (default /home/claude/testcontent) with configs/base and configs/mirage.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Content;
using PwRemake.Sim.Maps;

namespace PwRemake.Tests;

[TestClass]
public static class CustomCampaignTests
{
    static string Content() => Environment.GetEnvironmentVariable("PW_CONTENT") is { Length: > 0 } c ? c : "/home/claude/testcontent";
    static string Tmp(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "pw-customcamp-tests", name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    static (GameConfiguration Cfg, List<MissionEntry> Index) Load(TestContext t, string id)
    {
        var dir = Path.Combine(Content(), "configs", id);
        var cfg = GameConfiguration.Load(dir);
        if (cfg == null || !File.Exists(Path.Combine(cfg.MissionsDir, "index.json"))) t.Skip($"configuration {id} with missions/index.json not found under {Content()}");
        return (cfg!, MissionEntry.LoadIndex(cfg!.MissionsDir));
    }

    static string Ids(IEnumerable<MissionEntry> l) => string.Join(",", l.Select(e => e.Id));

    // ============================================================================================ grouping
    /// <summary>the official game: the original campaign 0 - 16, the Booster Pack 41 - 43 (BoosterPack1's maps), no MIRAGE
    /// group - and MIRAGE's SEAS missions 20 - 23 left out even when the index lists them (the toolkit fills gaps of the
    /// list from every folder of the installation: their maps are MIRAGE's)</summary>
    public static void CustomCampaignGroupsBase(TestContext t)
    {
        var (cfg, index) = Load(t, "base");
        t.Equal("0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16", Ids(MissionGroups.Original(cfg, index)), "base: the original campaign in playing order");
        var groups = MissionGroups.Custom(cfg, index);
        t.Equal("Booster Pack", string.Join("|", groups.Select(g => g.Title)), "base: only the Booster Pack group");
        t.Equal("41,42,43", Ids(groups[0].Missions), "base: the Booster Pack's missions");
        t.Equal("BoosterPack1", MissionGroups.SourceFolder(groups[0].Missions[0]), "base: 4-1's map is BoosterPack1's");
        // the index as the toolkit writes it on an installation with MIRAGE: + the SEAS missions from MIRAGE's folder
        var mirage = Load(t, "mirage").Index.Where(e => e.Id is >= 20 and <= 23).ToList();
        t.Equal(4, mirage.Count, "mirage: missions 20 - 23 in its index");
        var withSeas = index.Concat(mirage).ToList();
        t.True(mirage.All(e => !MissionGroups.Belongs(cfg, e)), "base: MIRAGE's maps are not the official game's");
        t.Equal("Booster Pack", string.Join("|", MissionGroups.Custom(cfg, withSeas).Select(g => g.Title)), "base: the SEAS missions left out");
        t.Equal(17, MissionGroups.Original(cfg, withSeas).Count, "base: the original campaign the same");
        // the end screen's Continue inside a group
        t.Equal(5, MissionGroups.NextInGroup(cfg, withSeas, 4)?.Id ?? -1, "base: 4 -> 5 (not the Booster Pack's 4-1)");
        t.Equal(null, MissionGroups.NextInGroup(cfg, withSeas, 16)?.Id, "base: 16 is the campaign's end");
        t.Equal(42, MissionGroups.NextInGroup(cfg, withSeas, 41)?.Id ?? -1, "base: 4-1 -> 4-2");
        t.Equal(null, MissionGroups.NextInGroup(cfg, withSeas, 43)?.Id, "base: 4-3 is the Booster Pack's end (not mission 5)");
    }

    /// <summary>MIRAGE: the original campaign 0 - 16, the Booster Pack 41 - 43 (left out when hidden, as the menu does:
    /// CampaignStatus), MIRAGE's own missions 20 - 23 (the SEAS versions of 4-1 .. 4-3, "Seeking Ada")</summary>
    public static void CustomCampaignGroupsMirage(TestContext t)
    {
        var (cfg, index) = Load(t, "mirage");
        t.Equal("0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16", Ids(MissionGroups.Original(cfg, index)), "mirage: the original campaign");
        var groups = MissionGroups.Custom(cfg, index);
        t.Equal("Booster Pack|MIRAGE", string.Join("|", groups.Select(g => g.Title)), "mirage: Booster Pack and MIRAGE groups");
        t.Equal("41,42,43", Ids(groups[0].Missions), "mirage: the Booster Pack's missions");
        t.Equal("20,21,22,23", Ids(groups[1].Missions), "mirage: MIRAGE's own missions");
        t.True(groups[1].Missions.All(e => e.Tribe == "SEAS" && MissionGroups.SourceFolder(e) == "MIRAGE"), "mirage: its own missions are the SEAS', from MIRAGE's folder");
        var hidden = MissionGroups.Custom(cfg, index, id => id is 41 or 42 or 43);
        t.Equal("MIRAGE", string.Join("|", hidden.Select(g => g.Title)), "mirage: the hidden Booster Pack missions leave an empty group out");
        t.Equal(21, MissionGroups.NextInGroup(cfg, index, 20)?.Id ?? -1, "mirage: 20 -> 21");
        t.Equal(null, MissionGroups.NextInGroup(cfg, index, 23)?.Id, "mirage: 23 is MIRAGE's group's end");
        t.Equal(null, MissionGroups.NextInGroup(cfg, index, 16)?.Id, "mirage: 16 is the campaign's end (not MIRAGE's 20)");
        t.Equal(MissionGroupKind.Mod, MissionGroups.KindOf(cfg, index.First(e => e.Id == 23)), "mirage: single_seas_02 is MIRAGE's own");
    }

    // ============================================================================================ campaign.lua
    /// <summary>campaign.lua written and read back (the texts with quotes and new lines), listed; data only (a call is
    /// refused); the checks: a name, missions, every mission there, a map of another configuration</summary>
    public static void CustomCampaignLuaReadWrite(TestContext t)
    {
        var (cfg, index) = Load(t, "base");
        var dir = Tmp("lua");
        var user = Tmp("lua-maps");
        var c = new UserCampaign { Name = "The \"long\" way", Description = "Line one\nline two", Author = "Kacper", Missions = { "tutorial", "single_05", "single_b41" } };
        var folder = c.Save(dir);
        t.Equal(Path.Combine(dir, "The _long_ way"), folder, "a folder named after the campaign");
        var r = UserCampaign.Read(folder);
        t.Equal(c.Name, r.Name, "name read back");
        t.Equal(c.Description, r.Description, "description read back");
        t.Equal(c.Author, r.Author, "author read back");
        t.Equal(string.Join(",", c.Missions), string.Join(",", r.Missions), "missions read back in order");
        t.Equal(1, UserCampaign.List(dir).Count, "listed");
        t.Equal(Path.Combine(dir, "The _long_ way (2)"), UserCampaign.FreeFolder(dir, c.Name), "a taken name gets (2)");
        t.Equal(0, r.Problems(true, cfg, index, user).Count, "a campaign of the configuration's missions has no problems");
        // the checks
        t.True(new UserCampaign { Missions = { "tutorial" } }.Problems(false).Any(p => p.Contains("no name")), "no name");
        t.True(new UserCampaign { Name = "x" }.Problems(false).Any(p => p.Contains("no missions")), "no missions");
        bool refused = false;
        try { new UserCampaign().Save(dir); } catch (InvalidOperationException) { refused = true; }
        t.True(refused, "a campaign without a name is not saved");
        var missing = new UserCampaign { Name = "x", Missions = { "single_05", "No such map" } };
        t.True(missing.Problems(true, cfg, index, user).Any(p => p.StartsWith("Mission 2") && p.Contains("No such map")), "a mission that is not there");
        t.Equal(CampaignRefKind.Missing, missing.Resolve("No such map", index, user).Kind, "not found");
        t.Equal(CampaignRefKind.Mission, missing.Resolve("SINGLE_05", index, user).Kind, "a mission by key (any case)");
        // a map of My maps that needs MIRAGE, in the official game
        var mf = Path.Combine(Content(), "configs", "base", "missions", "02_single_02");
        if (Directory.Exists(mf))
        {
            var m = Path.Combine(user, "Seas map");
            CopyDir(mf, m);
            var lua = File.ReadAllText(Path.Combine(m, "map.lua")).Replace("  version = 1,\n", "  version = 1,\n  requires = \"MIRAGE\",\n");
            File.WriteAllText(Path.Combine(m, "map.lua"), lua);
            var nc = new UserCampaign { Name = "x", Missions = { "Seas map" } };
            t.Equal(CampaignRefKind.MyMap, nc.Resolve("Seas map", index, user).Kind, "a map of My maps by name");
            t.True(nc.Problems(true, cfg, index, user).Any(p => p.Contains("needs MIRAGE")), "a map that needs MIRAGE in the official game");
            var mc = GameConfiguration.Load(Path.Combine(Content(), "configs", "mirage"));
            if (mc != null) t.Equal(0, nc.Problems(true, mc, index, user).Count, "the same map in MIRAGE");
        }
        // data only
        bool code = false;
        try { UserCampaign.Parse("return { name = \"x\", missions = { os.exit() } }", "evil/campaign.lua"); } catch (LuaDataException) { code = true; }
        t.True(code, "a call in campaign.lua is refused");
        bool fmt = false;
        try { UserCampaign.Parse("return { format = \"pw-remake-map\", name = \"x\" }", "map/campaign.lua"); } catch (LuaDataException) { fmt = true; }
        t.True(fmt, "another format is refused");
        t.Equal(0, UserCampaign.Parse("return { name = \"x\", missions = {} }", "empty/campaign.lua").Missions.Count, "an empty mission list");
    }

    /// <summary>each mission opens when the one before was won; a win never takes progress back; Start's mission</summary>
    public static void CustomCampaignUnlockOrder(TestContext t)
    {
        t.True(UserCampaign.IsOpen(0, 0), "the first mission is open");
        t.True(!UserCampaign.IsOpen(1, 0), "the second is not before the first was won");
        int won = UserCampaign.AfterWin(0, 0);
        t.Equal(1, won, "the first won");
        t.True(UserCampaign.IsOpen(1, won) && !UserCampaign.IsOpen(2, won), "the second opens, not the third");
        won = UserCampaign.AfterWin(1, won);
        t.Equal(2, won, "the second won");
        t.Equal(2, UserCampaign.AfterWin(0, won), "winning the first again keeps the progress");
        var c = new UserCampaign { Name = "x", Missions = { "a", "b", "c" } };
        t.Equal(0, c.Current(0), "Start: the first");
        t.Equal(2, c.Current(2), "Start: the first not won");
        t.Equal(2, c.Current(3), "Start: all won - the last");
        t.Equal(-1, new UserCampaign().Current(0), "no missions: none");
        t.True(!UserCampaign.IsOpen(-1, 5), "no mission -1");
    }

    // ============================================================================================ sharing
    /// <summary>a campaign of a map folder, a .pwmap and a mission of the configuration exported as one .pwcampaign and
    /// imported into another campaigns folder: campaign.lua the same, the two maps in the campaign's own maps/ with the
    /// same content (MapFiles.ContentHash), found before My maps; the configuration's mission not packed; imported twice:
    /// "(2)"; a zip with a file outside the campaign or a damaged map is refused and leaves nothing behind</summary>
    public static void CustomCampaignExportImport(TestContext t)
    {
        var (cfg, index) = Load(t, "base");
        var mf = Path.Combine(Content(), "configs", "base", "missions", "05_single_05");
        if (!Directory.Exists(mf)) t.Skip("mission 5 not found");
        var user = Tmp("x-maps");
        CopyDir(mf, Path.Combine(user, "Holy City edit"));
        File.WriteAllBytes(Path.Combine(user, "Arena.pwmap"), UserCampaign.PwmapBytes(Path.Combine(user, "Holy City edit")));
        var dir = Tmp("x-campaigns");
        var c = new UserCampaign { Name = "Share me", Description = "two maps and a mission", Missions = { "Holy City edit", "single_02", "Arena" } };
        c.Save(dir);
        t.Equal(CampaignRefKind.MyMap, c.Resolve("Arena", index, user).Kind, "a .pwmap of My maps by its name without .pwmap");
        var file = Path.Combine(Tmp("x-out"), "Share me.pwcampaign");
        var packed = c.Export(file, index, user);
        t.Equal("Holy City edit,Arena", string.Join(",", packed), "the maps are packed, the configuration's mission is not");
        using (var z = ZipFile.OpenRead(file))
            t.Equal("Share me/campaign.lua|maps/Arena.pwmap|maps/Holy City edit.pwmap".Replace("Share me/", ""),
                string.Join("|", z.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal)), "the zip's files");
        var other = Tmp("x-import");
        var i1 = UserCampaign.Import(file, other);
        t.Equal(Path.Combine(other, "Share me"), i1.Folder, "imported into a folder named after it");
        t.Equal(c.Name, i1.Name, "name"); t.Equal(c.Description, i1.Description, "description");
        t.Equal(string.Join(",", c.Missions), string.Join(",", i1.Missions), "missions");
        var empty = Tmp("x-empty-maps");
        var r1 = i1.Resolve("Holy City edit", index, empty);
        t.Equal(CampaignRefKind.Bundled, r1.Kind, "the folder map: the campaign's own");
        t.Equal(MapFiles.ContentHash(Path.Combine(user, "Holy City edit")), MapFiles.ContentHash(r1.MapPath!), "the folder map's content the same");
        t.Equal(CampaignRefKind.Bundled, i1.Resolve("Arena", index, user).Kind, "the campaign's own map before My maps");
        t.Equal(CampaignRefKind.Mission, i1.Resolve("single_02", index, empty).Kind, "the configuration's mission");
        t.Equal(0, i1.Problems(true, cfg, index, empty).Count, "the imported campaign plays without My maps");
        var i2 = UserCampaign.Import(file, other);
        t.Equal(Path.Combine(other, "Share me (2)"), i2.Folder, "imported again: (2)");
        // a file outside the campaign
        var evil = Path.Combine(Tmp("x-evil"), "evil.pwcampaign");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("campaign.lua").Open())) w.Write("return { name = \"Evil\", missions = { \"x\" } }");
            using (var w = z.CreateEntry("maps/../../evil.pwmap").Open()) w.Write(new byte[] { 1, 2, 3 });
        }
        bool refused = false;
        try { UserCampaign.Import(evil, other); } catch (InvalidDataException) { refused = true; }
        t.True(refused, "a file outside the campaign is refused");
        t.True(!Directory.Exists(Path.Combine(other, "Evil")) && !Directory.Exists(Path.Combine(other, "Evil.tmp")), "nothing left behind");
        // a damaged map
        var bad = Path.Combine(Tmp("x-bad"), "bad.pwcampaign");
        using (var z = ZipFile.Open(bad, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("campaign.lua").Open())) w.Write("return { name = \"Bad\", missions = { \"m\" } }");
            using (var w = z.CreateEntry("maps/m.pwmap").Open()) w.Write(new byte[] { 1, 2, 3 });
        }
        refused = false;
        try { UserCampaign.Import(bad, other); } catch (Exception) { refused = true; }
        t.True(refused && !Directory.Exists(Path.Combine(other, "Bad")) && !Directory.Exists(Path.Combine(other, "Bad.tmp")), "a damaged map is refused, nothing left behind");
    }

    // ============================================================================================ edited copies
    /// <summary>a campaign mission saved by the editor into My maps (a copy without map.lua's source line, the mission
    /// table kept) is an edited copy of that mission: "(edited)" with the original's title; a map of the user's own is not</summary>
    public static void CustomCampaignEditedCopy(TestContext t)
    {
        var (_, index) = Load(t, "base");
        var mf = Path.Combine(Content(), "configs", "base", "missions", "05_single_05");
        if (!Directory.Exists(mf)) t.Skip("mission 5 not found");
        var user = Tmp("ed-maps");
        var copy = Path.Combine(user, "My Holy City");
        CopyDir(mf, copy);
        var lua = File.ReadAllLines(Path.Combine(copy, "map.lua")).Where(l => !l.TrimStart().StartsWith("source = ", StringComparison.Ordinal));
        File.WriteAllLines(Path.Combine(copy, "map.lua"), lua);
        var m = MyMapInfo.Read(copy, index, user);
        t.True(m.Edited, "an edited copy");
        t.Equal(5, m.Original?.Id ?? -1, "of mission 5");
        t.Equal("Mission 5: The Holy City (edited)", m.Title, "shown with the original's title");
        t.Equal("My Holy City", m.Ref, "named by its folder in My maps");
        // the same as a .pwmap
        var pw = Path.Combine(user, "Zipped.pwmap");
        File.WriteAllBytes(pw, UserCampaign.PwmapBytes(copy));
        var z = MyMapInfo.Read(pw, index, user);
        t.True(z.Edited && z.Original?.Id == 5 && z.Ref == "Zipped.pwmap", "a .pwmap copy too");
        // a map without a mission table (the user's own): not an edited copy
        var own = Path.Combine(user, "Own");
        CopyDir(mf, own);
        var text = File.ReadAllText(Path.Combine(own, "map.lua"));
        int at = text.IndexOf("\n  mission = {", StringComparison.Ordinal);
        t.True(at > 0, "the mission's map.lua has a mission table");
        File.WriteAllText(Path.Combine(own, "map.lua"), text[..at] + "\n}\n");
        var o = MyMapInfo.Read(own, index, user);
        t.True(!o.Edited && o.Original == null, "the user's own map is not an edited copy");
        // a mission table without the original's map (Test map's: key custom_map) - not an edited copy either
        var tm = Path.Combine(user, "Test");
        CopyDir(mf, tm);
        var tt = File.ReadAllText(Path.Combine(tm, "map.lua")).Replace("map = \"Base/Maps/Cpn_single_001/single_05.ula\",", "map = \"\",");
        File.WriteAllText(Path.Combine(tm, "map.lua"), tt);
        t.True(!MyMapInfo.Read(tm, index, user).Edited, "a mission table without the original map");
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
        }
    }
}
