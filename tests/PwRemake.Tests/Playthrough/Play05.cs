// Mission 5 "The Holy City" played by script: the three heroes cross the city to the temple, free the three
// support points from the pirates (rodeo, animal farm, then the weapon smith), and fight their way to the harbour.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_05.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play05 : CampaignBot
{
    public Play05(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        List<Unit> party() => fighters();
        bool hostile(Entity e) { var p = party(); return e.Owner != null && p.Count > 0 && p[0].IsEnemy(e) && e.Kind == "unit"; }
        // a support point: the heroes clear its region of the pirates, then beat the second wave that comes for it
        IEnumerable<Step> spot(int k, string region, string name) => new[]
        {
            new Step($"support point {k} ({name}): the pirates in it are beaten", () => { if (!hunt(party(), e => hostile(e) && inRegion(e, region), "all")) goRegion(party(), region); }, () => v($"spot0{k}_cleared") >= 1, 900),
            new Step($"the pirates' second wave against support point {k} is beaten", () => { if (!hunt(party(), e => grp($"2nd_pirates_spot_0{k}").Contains(e), "all")) goRegion(party(), region); }, () => k == 2 ? v("sum_of_spots") >= 3 : firedP(k == 1 ? "Gb05" : "Gb09"), 900),
        };
        var steps = new List<Step>
        {
            new("quest \"To the Temple\" from the start", () => { }, () => quest("L05MQ01")!.Visible, 20),
            new("the heroes cross the city to the temple: sequence sc_2055, quest \"Pirates in the Holy City\", the pirates take the support points", () => go(party(), 411, 363), () => quest("L05MQ01")!.Accomplished && quest("L05MQ02")!.Visible && firedP("Gb01: InitQuest"), 600),
        };
        steps.AddRange(spot(1, "Spot1_Rodeo", "the rodeo")); steps.AddRange(spot(3, "Spot3_AnimalFarm", "the animal farm"));
        steps.Add(new("two support points freed: quest accomplished, the last fortification (weapon smith) opens", () => { }, () => quest("L05MQ02")!.Accomplished, 60));
        steps.AddRange(spot(2, "Spot2_WeaponsBuilder", "the weapon smith"));
        steps.AddRange(new List<Step>
        {
            new("the last fortification is freed: sequence sc_2049, quest \"To the Harbour\"", () => { }, () => quest("L05MQ04")!.Accomplished && quest("L05MQ03")!.Visible, 120),
            new("the way to the harbour is fought free: the pirates' last defence falls", () => { if (!hunt(party(), e => grp("patrol01").Contains(e) || grp("patrol02").Contains(e), "all")) if (!hunt(party(), e => hostile(e) && inRegion(e, "harbour"), "all")) goRegion(party(), "harbour"); }, () => quest("L05MQ03")!.Accomplished, 1800),
            new("the film ms_2090 ends the mission (SQNZ quit)", () => { }, () => Over, 120),
        });
        int far = runSteps(steps);
        if (far >= 11) ok(Over && S.Won && new[] { "L05MQ01", "L05MQ02", "L05MQ04", "L05MQ03" }.All(n => questState(n) == "done"), "the mission is won, the four main quests accomplished");
        info("variables: " + string.Join(" ", new[] { "sum_of_spots", "available_spots", "spot01_cleared", "spot02_cleared", "spot03_cleared", "Ninja_dead" }.Select(n => n + "=" + Var(n))));
        return Report();
    }
}
