// Mission 2 "Druid Island" played by script: the heroes leave the base, drive the poachers from the four mammoth
// caves (at least seven mammoths must be counted as saved), and destroy the Dustrider base in the north.
// Cheats (CampaignBot): the player's units and the holy mammoths are healed, enemies near the player's fighters are
// worn down, a unit that makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_02.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play02 : CampaignBot
{
    public Play02(MissionSession s, PlayPresenter p) : base(s, p) { }

    // the workers stay at home: they repair the base while it is attacked, else gather. MIRAGE's Dustriders (Giraffe
    // from the first saved cave, 04_after_first_saved_nest_switch_giraffe) send a suicide attack and a landing party
    // against it while the heroes are at the caves; the Dragon Clan's fireplace (4000 hit points, the Norsemen's 5000)
    // fell to them and the mission was lost (02_game_over_player_dead: no producer left). (Not in the JS: in the base
    // game the attacks find the base standing anyway.)
    protected override void AfterSecond()
    {
        var hurt = myBuildings().Where(b => b.Built && b.Hp < b.MaxHp * 0.95).OrderBy(b => b.Hp / Math.Max(1, b.MaxHp)).FirstOrDefault();
        if (hurt == null) { gather(); return; }
        foreach (var w in workers()) if (!(w.Task.Type == "repair" || w.Task.Type == "build") || w.Task.Building != hurt) Order(w, "repair", target: hurt);
    }

    public override string Play()
    {
        var P1 = C.Players[1]; var P2 = C.Players[2]; var P3 = C.Players[3];
        Func<Entity, bool> foeIn(string region) => e => (e.Owner == P1 || e.Owner == P2) && inRegion(e, region);
        IEnumerable<Step> nest(int k) => new[]
        {
            new Step($"the way to mammoth cave {k}: the poachers there attack the mammoths", () => goRegion(fighters(), "enter_nest_" + k), () => firedP($"01_attack_first_mammoth_nest_{k}"), 400),
            new Step($"the poachers at cave {k} are driven away and their buildings destroyed: the cave is saved", () => { if (!hunt(fighters(), foeIn("check_nest_" + k), "all")) goRegion(fighters(), "check_nest_" + k); }, () => firedP($"01_nest_{k}_saved") || v("nests_saved") >= k, 900),
        };
        var steps = new List<Step>
        {
            new("the intro film ms_1090; quest \"Help for the Arch Druid\" is shown; the Dustriders' computer player builds (Turtle)", () => { }, () => quest("L02MQ01_help_druid")!.Visible && ui.seq.Any(s => s.StartsWith("ms_1090")), 20),
            new("the heroes leave the base: quest \"Mammoth Rescue\" and the counter in the info bar", () => goRegion(fighters(), "player_leave_base"), () => firedP("01_L02MQ02_vis") && quest("L02MQ02_retrieve_the_mammoths")!.Visible, 300),
        };
        steps.AddRange(nest(1)); steps.AddRange(nest(2)); steps.AddRange(nest(3)); steps.AddRange(nest(4));
        steps.AddRange(new List<Step>
        {
            new("seven mammoths are saved: quest accomplished, sequence sc_1082, the Dustriders land by turtle", () => { }, () => quest("L02MQ02_retrieve_the_mammoths")!.Accomplished && firedP("01_L02MQ03_vis"), 60),
            new("the Dustrider base in the north is destroyed (all units and production buildings)", () => { if (!hunt(fighters(), e => e.Owner == P1, "all")) goRegion(fighters(), "dustrider_base"); }, () => firedP("02_L02MQ03_acc"), 2500),
            new("the final sequence sc_1089 and QUIT", () => { }, () => Over, 60),
        });
        var mammoths = new CheatOpts { Friends = { P3 } };
        int far = runSteps(steps, new RunOpts { CheatOpts = mammoths });
        if (far >= 2) ok(Regex.IsMatch(E.InfoBarShown ?? "", @"Mammoth|\d+ ?/ ?\d+") || far >= 11, $"the info bar shows the mammoth counter (\"{E.InfoBarShown}\")");
        if (far >= 1) ok(Brain(1) != null && Regex.IsMatch(Brain(1)!.Behaviour ?? "", "Turtle|Giraffe|Schnecke"), "the Dustriders' computer player was woken: " + Brains());
        if (far >= 10) ok(v("nests_saved") == 4 && v("mammoths_saved") >= 7, $"four caves saved, {N(v("mammoths_saved"))} mammoths counted (of {Var("Mammoth_Max")})");
        if (far >= 11) ok(questState("L02MQ02_retrieve_the_mammoths") == "done" && ui.seq.Any(s => s.StartsWith("sc_1082")), "quest \"Mammoth Rescue\" accomplished, sequence sc_1082");
        if (far >= 11) ok(firedP("02_spawn_turtle_to_second_harborplace") && W.Units.Any(u => u.Name.Contains("transport_turtle")), "the landing parties were created (CPLX: transport turtles with passengers)");
        if (far >= 13) ok(Over && S.Won && questState("L02MQ03_destroy_the_poachers") == "done" && questState("L02MQ01_help_druid") == "done", "QUIT: the mission is won, all three main quests accomplished");
        info("variables: " + string.Join(" ", new[] { "Mammoth_Max", "mammoths_saved", "nests_saved" }.Select(n => n + "=" + Var(n))));
        return Report() + "\n   brains: " + Brains();
    }
}
