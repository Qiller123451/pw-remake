// Buildings that stay at low hit points (player report, MIRAGE mission 2 "Druid Island": "buildings stayed at low hp and
// became untargetable"). That is the mission's design: MIRAGE's single_02 (Root/G_gameplay/l_sequence_buildings_inv)
// makes the Dustriders' temple, rodeo, two farms (below 500 hit points) and two tents (below 150) invulnerable, visible
// but neither hitable nor selectable (OBAP flags=1) and gives them to player 2 - the camp stays standing, burning, for the
// ending sequence sc_1089, and player 1 counts as beaten without them (02_L02MQ03_acc). Elsewhere - a skirmish, a world
// without such triggers - every building class goes down to the last hit point when it is attacked.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class BuildingsLowHpTests
{
    /// <summary>MIRAGE mission 2: a Dustriders' tent hit below 150 hit points becomes the ending sequence's prop -
    /// invulnerable, untargetable, unselectable, player 2's - and nothing destroys it; an ordinary building of the camp
    /// is still destroyed</summary>
    public static void Mission02CampKeptForTheEnding(TestContext t)
    {
        var s = CampaignTests.Session(t, "mirage", 2);
        var C = s.Campaign; var W = s.World;
        var rec = C.Objects.All.FirstOrDefault(r => r.Name == "Aje_Tent_0");
        if (rec?.Entity is not Building b) { t.Skip("Aje_Tent_0 not on the map"); return; }
        var p1 = C.PlayerAt(1); var p2 = C.PlayerAt(2);
        t.True(b.Owner == p1, "the tent is the Dustriders' (player 1)");
        // a fighter of the human beside it can attack it while it is whole
        var war = W.SpawnUnit("hu_warrior", C.Human, b.Pos.X + b.Radius + 3, b.Pos.Z, 3)!;
        t.True(W.CanTarget(war, b), "a whole tent can be attacked");
        W.Order(new[] { war }, new Order("attack") { Target = b });
        W.Damage(b, b.Hp - 120, null);
        s.Run(3);
        t.True(!(war.Task.Type == "attack" && war.Task.Target == b), $"the player's warrior stopped attacking it ({war.Task.Type})");
        t.True(b.Alive && b.Hp <= 150, $"the tent stands at low hit points ({b.Hp})");
        t.True(W.Invulnerable(b, true) && b.Untargetable && b.Unselectable, "invulnerable, untargetable, unselectable (03_set_tent_1_inv / 04_set_tent_0_inv)");
        t.True(b.Owner == p2, "given to player 2 (the camp no longer counts as player 1's)");
        double hp = b.Hp;
        W.Damage(b, 5000, null);
        t.Equal(hp, b.Hp, "no damage takes it down");
        t.True(!W.CanTarget(war, b), "nobody can target it any more");
        // a building of the camp the mission does not keep (not a temple, rodeo, farm, tower or tent) still falls
        var other = W.Buildings.FirstOrDefault(x => x.Alive && x.Owner == p1 && !x.Untargetable && x.Rec != null && !System.Text.RegularExpressions.Regex.IsMatch(x.Name, "temple|rodeo|small_farm|small_tower|tent"));
        if (other != null)
        {
            W.Damage(other, other.Hp + 10, war);
            t.True(!other.Alive, $"{other.Name} is destroyed");
        }
    }

    public static void EveryBuildingClassFallsBase(TestContext t) => EveryBuildingClassFalls(t, RulesTestData.Base(t));
    public static void EveryBuildingClassFallsMirage(TestContext t) => EveryBuildingClassFalls(t, RulesTestData.Mirage(t));

    /// <summary>without a mission's triggers no building stops at low hit points or becomes untargetable: every building
    /// class of the four tribes, finished and down to 5 % of its hit points (at most 150), is destroyed by a warrior ordered to attack it</summary>
    static void EveryBuildingClassFalls(TestContext t, string data)
    {
        var (R, M) = Sys1.Load(t, data);
        int n = 0; var bad = new List<string>();
        foreach (var name in R.IndexOrder)
        {
            var info = R.Index[name];
            if (info.Type != "BLDG" || info.Tribe is not ("Hu" or "Aje" or "Ninigi" or "SEAS")) continue;
            var W = Sys1.NewWorld(R, M, 256, 3);
            var me = Sys1.AddPlayer(W, R, 0, "Hu"); var foe = Sys1.AddPlayer(W, R, 1, info.Tribe);
            Building? b;
            try { b = W.PlaceBuilding(name, foe, 0, 0, 0, true); } catch { continue; }
            if (b == null || !b.Alive || b.Untargetable || b.Hp <= 0) continue;
            if (W.HiddenFrom(b, me)) continue;              // (the Ninigi traps: hidden from their enemies)
            var war = W.SpawnUnit("hu_warrior", me, b.Pos.X + b.Radius + 4, b.Pos.Z, 5);
            if (war == null || !W.CanTarget(war, b)) { bad.Add($"{name}: cannot be targeted"); continue; }
            n++;
            b.Hp = Math.Max(1, Math.Ceiling(Math.Min(b.MaxHp * 0.05, 150)));
            W.Order(new[] { war }, new Order("attack") { Target = b });
            for (int i = 0; i < 20 * 60 && b.Alive; i++) W.Step();
            if (b.Alive) bad.Add($"{name}: still standing after 60 s at {b.Hp:0} / {b.MaxHp:0} hp (warrior: {war.Task.Type}, target {war.Task.Target?.Name})");
        }
        t.Log($"{n} building classes");
        foreach (var x in bad.Take(20)) t.Log("  " + x);
        t.True(n > 30, $"building classes tried ({n})");
        t.True(bad.Count == 0, $"every building class falls ({bad.Count} did not)");
    }
}
