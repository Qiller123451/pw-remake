// Mission 4 "The Dustriders" played by script: the village holds out against the animal attacks while the heroes and
// the Dustrider warriors march north-east, break into the Barbarians' main base (quest "Back on Earth"), beat its
// last line of defence or reach the harbour behind it (quest "Barbarian Main Base") - the film ms_2045 ends the
// mission. The side quests are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_04.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play04 : CampaignBot
{
    public Play04(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        List<Unit> army() => fighters().Where(u => u.Cls == "CHTR" || u.Cls == "ANML").ToList();
        bool foe(Entity e) { var a = army(); return a.Count > 0 && a[0].IsEnemy(e) && e.Owner != null; }
        // on the way: fight what stands within 30 m of the army, else walk on
        Action march(string region) => () => { var a = army(); if (!(a.Count > 0 && hunt(a, e => foe(e) && e.DistTo(a[0]) < 30, "army"))) goRegion(a, region, "army"); };
        var steps = new List<Step>
        {
            new("quest \"Back on Earth\" from the start", () => { }, () => questState("L04MQ01") == "open", 30),
            new("the army breaks into the Barbarian main base: quest \"Back on Earth\" accomplished, the last line of defence appears", march("inside_mainbase"), () => firedP("Gb06"), 2400),
            new("the last line of defence is beaten (or the harbour reached): quest \"Barbarian Main Base\" accomplished", () => { if (!hunt(army(), e => grp("last_defense_line").Contains(e), "army")) march("near_the_harbour")(); }, () => questState("L04MQ02") == "done", 1200),
            new("the film ms_2045 ends the mission", () => { }, () => Over, 200),
        };
        runSteps(steps);
        ok(!(steps.Count > reached.Count(x => x)) || (Over && S.Won && questState("L04MQ01") == "done"), "QUIT: the mission is won, \"Back on Earth\" accomplished");
        return Report(new() { [2] = "Gb06: Entered the mainbase", [3] = "Lb02: SetAccomplished" }) + "\n   won: " + EndInfo;
    }
}
