// A campaign start army spawns the classes it names (src/Sim/Campaign/Campaign.cs PlaceStart, Campaign.MountOf; co-op:
// CoopRuntime's "default" seat armies). Only a pure rider - a class with a 1-HP placeholder at every level of its tech
// tree object (MIRAGE 7's aje_velociraptor_handler_char) - comes as the animal it rides. Bug fixed 2026-11-20: hu_archer
// (the chariot's captain, a 1-HP placeholder only at level 1, below its lowest level 2) came as the chariot - missions
// 14 and 15 and every co-op seat army with an archer.
//
//   dotnet run --project tests/PwRemake.Tests -- StartArmy
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;

namespace PwRemake.Tests;

[TestClass]
public static class StartArmyTests
{
    /// <summary>every mission.json of a configuration and of the remake's own missions / campaigns</summary>
    static IEnumerable<string> MissionFiles(CampaignTests.Cfg c)
    {
        foreach (var f in Directory.EnumerateFiles(c.Config.MissionsDir, "mission.json", SearchOption.AllDirectories)) yield return f;
        foreach (var d in new[] { "missions", "campaigns" })
        {
            var root = Path.Combine(Repo.Root(), "game", d);
            if (Directory.Exists(root)) foreach (var f in Directory.EnumerateFiles(root, "mission.json", SearchOption.AllDirectories)) yield return f;
        }
    }

    /// <summary>no level of the class has real hit points (a rider that cannot stand on its own)</summary>
    static bool PureRider(PwRemake.Sim.Rules.Rules D, string cls)
    {
        for (int l = 1; l <= 5; l++) if (D.Stats(cls, l, null) is { Exists: true } s && !(s.Hp <= 1)) return false;
        return true;
    }

    public static void StartArmyClassesAsAsked(TestContext t)
    {
        int seen = 0;
        foreach (var cfg in new[] { "base", "mirage" })
        {
            var c = CampaignTests.LoadCfgPublic(cfg);
            if (c == null) { t.Log($"{cfg}: no data, skipped"); continue; }
            var D = c.Rules;
            t.Equal(null, Campaign.MountOf(D, "hu_archer"), $"{cfg}: hu_archer is an archer, not the chariot");
            if (D.Exists("aje_velociraptor_handler_char"))
                t.Equal("aje_velociraptor_handler", Campaign.MountOf(D, "aje_velociraptor_handler_char"), $"{cfg}: MIRAGE 7's raptor rider is its raptor");
            var classes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var f in MissionFiles(c))
            {
                if (Json.TryParseFile(f) is not JsonObject j) continue;
                foreach (var p in j["players"]?.AsArray() ?? new JsonArray())
                    foreach (var a in p?["start_army"]?.AsArray() ?? new JsonArray())
                        if (a?["class"].Str() is { Length: > 0 } cl && D.Exists(cl)) classes.Add(cl);
            }
            foreach (var cl in classes)
            {
                var m = Campaign.MountOf(D, cl);
                seen++;
                t.True(m == null || PureRider(D, cl), $"{cfg}: the start army's {cl} comes as {m} - a class with hit points of its own must spawn as itself");
                if (m != null) t.Log($"{cfg}: {cl} -> {m} (a rider without hit points)");
            }
        }
        if (seen == 0) t.Skip("no configuration data");
    }

    /// <summary>mission 14 starts with two archers (the original's start army): they stand as archers</summary>
    public static void StartArmyArchersMission14(TestContext t)
    {
        var s = CampaignTests.Session(t, "base", 14);
        var h = s.Campaign.Human!;
        var mine = s.World.Units.Where(u => u.Alive && u.Owner == h).Select(u => u.Name).ToList();
        t.Equal(2, mine.Count(n => n == "hu_archer"), "two archers: " + string.Join(", ", mine));
        t.True(!mine.Any(n => n.Contains("chariot")), "no chariot: " + string.Join(", ", mine));
    }
}
