// Mission 16 "Showdown!" played by script: after the opening film sc_7050 (quests "Survival" and "Stampede") the
// heroes break the barracks and garages of the three SEAS bases and beat their troops (quest "Stampede", sequence
// sc_7052: the Scorpio wakes up, quest "The Scorpion"), destroy the Scorpio (sequence sc_7054, quest "Exo Enforcer")
// and then Babbit's combat suit (quests "Exo Enforcer" and "Survival"); the film ms_7060 ends the mission. The side
// quest is left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_16.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play16 : CampaignBot
{
    public Play16(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        var P2 = C.Players[2];
        List<Unit> army() => fighters();
        bool foe(Entity e) => e.Owner != null && e.Owner != me && !me.IsFriend(e.Owner);
        bool near(Entity e, List<Unit> a) => e.DistTo(a[0]) < 25;
        bool @base(Entity e) => e.Owner == P2 && e.Kind == "building" && Regex.IsMatch(e.Name, "seas_(barracks|garage)");
        bool troops(Entity e) => e.Owner == P2 && e.Kind == "unit";
        // fight what is close first, then the step's target
        Action fight(Func<Entity, bool> target) => () => { var a = army(); if (a.Count > 0 && !hunt(a, e => foe(e) && e.Kind == "unit" && near(e, a), "army")) hunt(a, target, "army"); };
        var steps = new List<Step>
        {
            new("the opening film sc_7050: quests \"Survival\" and \"Stampede\"", () => { }, () => questState("L16MQ02") == "open", 120),
            new("the barracks and garages of the three SEAS bases are broken: quest \"Stampede\"", fight(@base), () => questState("L16MQ02") == "done", 2400),
            new("the last SEAS troops are beaten: sequence sc_7052, quest \"The Scorpion\"", fight(troops), () => questState("L16MQ03") == "open", 900),
            new("the Scorpio is destroyed: sequence sc_7054, quest \"Exo Enforcer\"", fight(e => e.Name == "scorpion" || e.Name.StartsWith("scorpion_panel")), () => questState("L16MQ04") == "open", 1800),
            new("Babbit's combat suit is beaten: quests \"Exo Enforcer\" and \"Survival\"", fight(e => e.Name == "babbage_mobile_suit"), () => questState("L16MQ04") == "done" && questState("L16MQ01") == "done", 1200),
            new("the film ms_7060 ends the mission", () => { }, () => Over, 600),
        };
        runSteps(steps);
        ok(!Over || S.Won, "QUIT: the mission is won");
        return Report(new() { [2] = "02_L16MQ02_acc_<-[Lb01]", [3] = "02_sc_7052_skorpion_quest_<-[Lb02]", [4] = "02_L16MQ03_acc_<-[Lc01]", [5] = "02_L16MQ04_acc_<-[Ld01]", [6] = "02_win_level" }) + "\n   won: " + EndInfo +
            "\n   variables: " + string.Join(" ", new[] { "C_dead", "V_dead", "CV_dead", "conquered_towers" }.Select(n => n + "=" + Var(n)));
    }
}
