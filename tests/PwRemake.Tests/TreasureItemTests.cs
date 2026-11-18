// The items a mission's triggers create (CreateObj "item_..."): a treasure chest on the ground that a unit picks up,
// as the map's own items - not a decoration. MIRAGE mission 13 (played as the SEAS): the zeppelin trader's reward
// item_pannier was a scenery prop that nothing could click or pick up (Kacper 2026-11-12). Also: a map's hidden item is
// not there to be picked up until it is shown (missions 6 and 12). Data: PW_CONTENT configs, PW_DATA(_MIRAGE).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class TreasureItemTests
{
    public static void TriggerItemIsPickable(TestContext t)
    {
        var s = CampaignTests.Session(t, "mirage", 13, "SEAS");
        var C = s.Campaign; var W = s.World; var E = s.Engine!;
        int before = W.Items.Count;
        // the trader's reward (CBL_Gb10#wares: the pile of goods appears, resources, CreateObj item_pannier)
        t.True(E.DebugFire("CBL_Gb10#wares"), "the reward trigger");
        t.Equal(before + 1, W.Items.Count, "one more item in the world");
        var it = W.Items.Last();
        t.Equal("item_pannier", it.ItemCls, "the pannier");
        var rec = C.RecOfProp(it.Prop!);
        t.True(rec != null && rec.Type == "ITEM", "an object of type ITEM");
        t.True(s.Host.Info(rec!.Id) is { IsItem: true }, "the triggers see an item");
        t.True(W.ItemLying(it), "lying there to be picked up");
        var (mx, my) = C.ToMap(it.Pos.X, it.Pos.Z);
        t.Near(426.8, mx, 1.5, "where the trigger puts it (x)"); t.Near(534.7, my, 1.5, "(y)");
        // the pile of goods next to it stays scenery (CTraderGoods: not selectable in the original either)
        // a unit of the player walks there and takes it
        var u = W.Units.Where(x => x.Alive && x.Owner == C.Human && W.CanCarry(x) && x.Item == null && !x.Parked)
            .OrderBy(x => Math.Abs(x.Pos.X - it.Pos.X) + Math.Abs(x.Pos.Z - it.Pos.Z)).FirstOrDefault();
        if (u == null) t.Skip("no carrier");
        W.Teleport(u!, it.Pos.X + 6, it.Pos.Z);
        W.Order(new[] { u! }, new Order("pickup") { Target = it });
        for (int i = 0; i < 20 * 30 && u!.Item == null; i++) s.Step();
        t.True(u!.Item == it && it.Carrier == u, $"{u.Name} carries the pannier");
    }

    public static void HiddenMapItemNotPickable(TestContext t)
    {
        var s = CampaignTests.Session(t, "mirage", 12);
        var W = s.World;
        var hidden = W.Items.Where(i => i.Prop is { Visible: false }).ToList();
        if (hidden.Count == 0) t.Skip("no hidden item");
        t.True(hidden.All(i => !W.ItemLying(i)), "a hidden item is not lying there");
        t.True(W.Items.Any(i => W.ItemLying(i)), "the shown ones are");
        t.Log($"hidden: {string.Join(", ", hidden.Select(i => i.ItemCls))}");
    }

    /// <summary>every item a mission's CreateObj makes (base and MIRAGE: missions 2, 4, 7 - 10, 13, 15, 22, 43) is an
    /// item class with a chest to show</summary>
    public static void CreateObjItemsAreItems(TestContext t)
    {
        int n = 0;
        foreach (var cfg in new[] { "base", "mirage" })
        {
            var c = CampaignTests.LoadCfgPublic(cfg);
            if (c == null) continue;
            var s = CampaignTests.Session(t, cfg, 13);
            var names = new SortedSet<string>();
            foreach (var f in System.IO.Directory.EnumerateFiles(c.Config.MissionsDir, "triggers.lua", System.IO.SearchOption.AllDirectories))
            {
                var txt = System.IO.File.ReadAllText(f);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(txt, @"CreateObj\{\s*(?:difficulty = ""\w+"",\s*)?obj_name = ""(item_\w+)"""))
                    names.Add(m.Groups[1].Value);
            }
            foreach (var cls in names) { t.True(s.Campaign.ItemModelOf(cls) != null, $"{cfg}: {cls} is an item with a model"); n++; }
            t.Log($"{cfg}: {string.Join(", ", names)}");
        }
        if (n == 0) t.Skip("no configurations");
    }
}
