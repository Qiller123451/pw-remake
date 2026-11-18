// Objectives under the tribe swap (campaign menu "Your tribe", src/Sim/Campaign/Tribes.cs): the conditions of a mission
// name classes of the mission's tribe; for the player's objects they count the chosen tribe's equivalents
// (Registry.ClassIs -> TribeSwap.CountsAs), and a class the chosen tribe has nothing for is met at once (BLDG writes its
// counter as reached).
//
//   NestsCountForTheSwappedTribe   mission 13 (MIRAGE) as the SEAS: 8 nests at the gate fill "Nests built correctly"
//   TribeSpecificConditions        every condition of every mission (both configurations) that names a class of the
//                                  mission's tribe, with each playable tribe's equivalent; lists what is met at once
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;

namespace PwRemake.Tests;

[TestClass]
public static class ObjectiveSwapTests
{
    const string NEST = "ninigi_dilophosaurus_nest";
    const string NEST_REGION = "immlgggioogeknbbokgnaabbjaomncln";      // single_13: the nests' region (deleted from the map)

    /// <summary>Kacper: "nests built correctly seems to be broken... I have nests near the gate yet none count" (mission
    /// 13 as the SEAS). The nest was outside the role table and "nest" too generic a name: no equivalent, the condition
    /// was "met" but its counter stayed 0 and the quest (which waits on the counter) never ended.</summary>
    public static void NestsCountForTheSwappedTribe(TestContext t)
    {
        var s = CampaignTests.Session(t, "mirage", 13, "SEAS");
        var C = s.Campaign; var E = s.Engine!; var H = s.Host;
        var S = C.Swap!;
        t.Equal("seas_stygimoloch_nest", S.Cls(NEST), "the SEAS nest stands for the dilophosaurus nest");
        t.True(!S.Missing(NEST), "the SEAS can build it");
        // the condition's region (rgn_guid) was deleted from the map in the editor (the converter's warning "region ...
        // does not exist"): an unknown region is the whole map (Regions.Get), so nests anywhere count
        t.True(C.Regions.Find(NEST_REGION) == null, "the gate region is not on the map");
        // 5 stygimoloch nests and 3 machine-gun nests (TribeSwap.ALSO) in a row near the start, finished
        var classes = Enumerable.Repeat("seas_stygimoloch_nest", 5).Concat(Enumerable.Repeat("seas_mg_nest", 3)).ToList();
        var sl = C.StartLocations[0]!;
        int placed = 0;
        for (int i = 0; i < classes.Count; i++)
        {
            var (mx, my) = C.ToMap(sl.X + 14 * (i % 4) - 21, sl.Z + 14 * (i / 4) + 20);
            if (C.Spawn(classes[i], C.HumanSlot, mx, my, exact: true) != null) placed++;
        }
        t.Equal(8, placed, "nests placed");
        t.Equal(8, H.CountFinished(NEST, C.HumanSlot, NEST_REGION), "nests counted at the gate");
        // the counting trigger (Gha00) writes the counter the InfoBar shows and ends the quest
        t.True(E.DebugEnable("Gha00: Darwinquest Count Nests <-- Ie17"), "Gha00");
        s.Run(3);
        t.Equal("8", E.GetVarOrNull("count Dilophonests"), "count Dilophonests");
    }

    /// <summary>a BuildingCount on a class the chosen tribe has nothing for is met at once, and the counter it writes reads
    /// as reached: mission 13's quest waits on the variable ("count Dilophonests" &gt; 7), not on the condition</summary>
    public static void MissingClassCounterReached(TestContext t)
    {
        var m = TB.Mission(new[]
        {
            TB.T("count", TB.L(TB.C("BLDG", "class", NEST, "value", ">7", "variable", "count Dilophonests")), TB.L(TB.Var("done", "set", "1"))),
        });
        var (h, e) = TB.Make(m, h => h.MissingClasses.Add(NEST));
        e.Start();
        e.Update(1.5);
        t.Equal("8", e.GetVarOrNull("count Dilophonests"), "the counter reads 8 / 8");
        t.Equal("1", e.GetVarOrNull("done"), "the condition is met");
    }

    // ------------------------------------------------------------------ the audit
    static readonly Regex ClassTok = new(@"(?<![A-Za-z0-9_])(hu|aje|ninigi|seas)_[a-z0-9_]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // parameters that name object classes (the object query, BLDG, TECH, ITEM ...); texts, names and paths are not classes
    static bool ClassKey(string k) => k.Contains("class", StringComparison.Ordinal) || k is "unit" or "units" or "obj" or "objects" or "building";

    /// <summary>Every condition of every campaign mission (base + MIRAGE, main missions, 4-1..4-3, MIRAGE 20-23) naming a
    /// class of the mission's tribe for the human player, with each playable tribe's equivalent. Classes without one
    /// are met at once (BLDG: counter reached); classes of the human player's tribe that are not translated at all
    /// would be a bug: none may be left.</summary>
    public static void TribeSpecificConditions(TestContext t)
    {
        int total = 0, waived = 0;
        var report = new List<string>();
        foreach (var cfgId in new[] { "base", "mirage" })
        {
            var c = CampaignTests.LoadCfgPublic(cfgId);
            if (c == null) { t.Log($"{cfgId}: not found - skipped"); continue; }
            var tribes = c.Config.PlayableTribes;
            foreach (var e in MissionEntry.LoadIndex(c.Config.MissionsDir).OrderBy(e => e.Id))
            {
                var folder = Path.Combine(c.Config.MissionsDir, e.Folder);
                if (!File.Exists(Path.Combine(folder, "triggers.lua"))) continue;
                var mj = Json.TryParseFile(Path.Combine(folder, "mission.json"));
                var human = mj?["players"]?.AsArray().FirstOrDefault(p => p?["type"].Str() == "human");
                var from = human?["tribe"].Str() ?? e.Tribe;
                var slot = (human?["id"].Int() ?? 0).ToString();
                if (string.IsNullOrEmpty(from)) continue;
                var m = LuaMission.Load(folder);
                // the units the player starts with (Campaign.StartUnitClasses)
                var starts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var a in human?["start_army"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray()) starts.Add(a?["class"].Str().ToLowerInvariant() ?? "");
                foreach (var o in m.World?["objects"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                    if (o?["owner"] is System.Text.Json.Nodes.JsonValue ov && ov.TryGetValue(out double od) && od.ToString() == slot && o["type"].Str() is "CHTR" or "ANML" or "VHCL" or "SHIP") starts.Add(o["class"].Str().ToLowerInvariant());
                // class -> condition types naming it for the human (owner: the player's slot or none given)
                var named = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                foreach (var tr in m.Script.Triggers)
                    foreach (var n in tr.Conditions)
                    {
                        var own = n.P.GetValueOrDefault("obj_owner") ?? n.P.GetValueOrDefault("owner") ?? n.P.GetValueOrDefault("player_id");
                        if (own != null && own != slot && own != "-1" && own != "") continue;
                        foreach (var (k, v) in n.P)
                        {
                            if (!ClassKey(k)) continue;
                            foreach (Match x in ClassTok.Matches(v))
                            {
                                var cls = x.Value.ToLowerInvariant();
                                if (TribeSwap.TribeOfClass(cls) != from) continue;
                                if (!named.TryGetValue(cls, out var set)) named[cls] = set = new SortedSet<string>(StringComparer.Ordinal);
                                set.Add(k.StartsWith("exclude", StringComparison.Ordinal) || k.StartsWith("sub_exclude", StringComparison.Ordinal) ? n.Code + " excluded" : n.Code);
                            }
                        }
                    }
                var swaps = tribes.Where(x => x != from).ToDictionary(to => to, to => new TribeSwap(c.Rules, from, to, null, false, c.Models, starts));
                foreach (var (cls, codes) in named)
                {
                    var parts = new List<string>();
                    foreach (var to in tribes.Where(x => x != from))
                    {
                        var S = swaps[to];
                        total++;
                        var (tribal, eq) = S.EquivalentOf(cls);
                        if (!tribal) { parts.Add($"{to}: kept as it is"); continue; }      // TribeSwap.KEEP (Taslow's towers)
                        bool miss = S.Missing(cls);
                        if (miss && codes.Any(x => !x.EndsWith(" excluded", StringComparison.Ordinal))) waived++;
                        parts.Add($"{to}: {(eq ?? "-")}{(miss ? " (met at once)" : "")}");
                    }
                    report.Add($"{cfgId} {e.Id,2} {e.Key} [{from}] {cls} ({string.Join(",", codes)}) -> {string.Join("; ", parts)}");
                }
            }
        }
        foreach (var r in report) t.Log(r);
        t.Log($"{report.Count} tribe-specific classes in conditions, {total} swaps, {waived} met at once (the chosen tribe has nothing for it or only later; exclusions not counted)");
        if (report.Count == 0) t.Skip("no mission data");
        // the nest of mission 13 has an equivalent for every tribe that can play it
        t.True(report.Where(r => r.Contains(NEST + " ")).All(r => !r.Contains(": -") && !r.Contains("met at once")), "the nest has equivalents");
        // MIRAGE's SEAS missions: the given flamethrowers (20) and the trade dino (22) are found for every tribe
        t.True(report.Where(r => r.Contains("] seas_flamethrower ") || r.Contains("] seas_trade_dino ")).All(r => !r.Contains("met at once")), "SEAS missions' units");
    }
}
