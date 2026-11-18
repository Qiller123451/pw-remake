// Mission 8 "Valley of the Gods" played by script: the heroes escort the archaeologist Kleemann (schliemann_s0) to
// the three keystones (their guards must be dead first), beat the undead that then come out of Valhalla's gate,
// walk into Valhalla and through its guardians to the final fight.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported). Before the fights at and behind Valhalla's gate
// (undead killers: 600 damage a blow) the skulls collected so far buy the heroes levels, as a player's would, and
// Kleemann stays back - the skulls go to the fighters, not to him (the way to the gate leads through the valley of the
// killer statues, unhittable hu_undead_killer that strike 500 a blow: Béla at level 2 fell to two blows in one second
// when Kleemann had taken 175 of the 230 skulls).
// Ported from remake/tests/campaign_play_08.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play08 : CampaignBot
{
    public Play08(MissionSession s, PlayPresenter p) : base(s, p) { }

    // (the heroes' deaths, for a failed run: when, who, where - the JS wrapped W.kill; here they are seen after each second)
    readonly List<string> deaths = new();
    HashSet<Unit> heroesSeen = new();
    protected override void AfterSecond()
    {
        foreach (var h in heroesSeen) if (!h.Alive || h.Owner != me) { var (mx, my) = ToMapR(h.Pos.X, h.Pos.Z); deaths.Add($"[{N(Round(W.Time))},\"{h.Name}\",{N(Round(h.Hp))},{mx},{my}]"); }
        heroesSeen = W.Units.Where(u => u.Alive && u.Owner == me && u.Def != null && u.Def.Unique).ToHashSet();
    }

    public override string Play()
    {
        List<Unit> party() => fighters();
        // (after the gathering Kleemann stays behind the fighters: if he dies the mission is lost - L02c / X01)
        Unit? kleemann() => W.Units.FirstOrDefault(u => u.Alive && u.Name == "schliemann_s0" && u.Owner == me);
        List<Unit> guard() => fighters().Where(u => u.Name != "schliemann_s0").ToList();
        void keepBack(double x, double z) { var k = kleemann(); var (gx, gz) = C.ToGame(x, z); if (k != null && Hypot(k.Pos.X - gx, k.Pos.Z - gz) > 6) Order(k, "move", gx, gz); }
        IEnumerable<Step> stone(int k, int[] at) => new[]
        {
            new Step($"the guards of keystone {k} are beaten", () => { if (!hunt(party(), inGroup("stoneguards0" + k), "all")) go(party(), at[0], at[1]); }, () => grp("stoneguards0" + k).Count == 0, 900),
            new Step($"Kleemann is brought to keystone {k}: the crystal is activated", () => go(party(), at[0], at[1]), () => v("keystone_0" + k) == 1, 400),
        };
        var steps = new List<Step>
        {
            new("the opening sequence sc_2590; quests \"Opening the Gate I\" and \"Protective Escort\"", () => { }, () => quest("L08MQ01")!.Visible && quest("L08MQ02")!.Visible, 30),
        };
        steps.AddRange(stone(1, new[] { 282, 886 }));
        steps.Add(new("sequence sc_2583a, quest 1 done, quest \"Opening the Gate II\" with the crystal counter", () => { }, () => quest("L08MQ01")!.Accomplished && quest("L08MQ03")!.Visible, 30));
        steps.AddRange(stone(2, new[] { 652, 867 })); steps.AddRange(stone(3, new[] { 211, 438 }));
        steps.AddRange(new List<Step>
        {
            new("all three crystals: sequence sc_2585, the undead march out of Valhalla (quest \"Counting the Undead\")", () => { }, () => quest("L08MQ03")!.Accomplished && quest("L08MQ04")!.Visible && grp("undead01").Count > 0, 40),
            new("the four undead groups are destroyed", () => { var und = new[] { "undead01", "undead02", "undead03", "undead04" }.SelectMany(grp).ToHashSet(); hunt(party(), e => und.Contains(e), "all"); }, () => v("undead_defeated") == 1, 1500),
            new("heroes and Kleemann gather before the gate: sequence sc_3004, the base is left behind", () => go(party(), 441, 418), () => firedP("G03#occupy_playerbase") && quest("L08MQ05")!.Visible, 500),
            new("the undead behind the gate are beaten: sequence sc_3005, Kleemann walks off", () => { promote(u => u.Name != "schliemann_s0"); keepBack(590, 325); if (!hunt(guard(), inGroup("undead05"), "all")) go(guard(), 730, 330); }, () => firedP("S05#sc_3005") && ui.seq.Any(s => s.StartsWith("sc_3005")), 900),
            new("the guardians of Valhalla are beaten (sequence sc_3009)", () => { promote(u => u.Name != "schliemann_s0"); if (!hunt(party(), e => e.Owner == C.Players[3] && inRegion(e, "showdown"), "all")) go(party(), 791, 265); }, () => firedP("Gg05a#init_schliemann"), 900),
            new("Kleemann, turned into an undead, is defeated: the final sequence sc_3012 wins the mission", () => { if (!hunt(party(), e => e.Name == "schliemann_zombie", "all")) go(party(), 791, 265); }, () => Over, 600),
        });
        int far = runSteps(steps);
        if (failedAt != null) info("deaths of heroes: [" + string.Join(",", deaths) + "]");
        var orders = E.Triggers.Where(t => t.Name.Contains("order_0") || t.Folder.Contains("order_0")).ToList();
        if (far >= 4) ok(E.InactiveFolders < 7 && orders.Any(t => t.NodeActive), "ACND activated one of the three \"capture order\" folders: " + string.Join(", ", orders.Where(t => t.NodeActive).Select(t => t.Name)));
        if (far >= 9) ok(questState("L08MQ03") == "done" && v("keystones") == 3, "three crystals counted, quest \"Opening the Gate II\" accomplished");
        if (far >= 13) ok(Over && S.Won && questState("L08MQ06") == "done" && questState("L08MQ05") == "done" && questState("L08MQ02") == "done", "SQNZ quit: the mission is won; escort, journey and revelations accomplished");
        return Report();
    }
}
