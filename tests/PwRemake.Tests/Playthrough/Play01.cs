// Mission 1 "Stranded" played by script, the way a player would go through it: Cole walks to the places the quests
// name, fights what attacks him, gathers the Norsemen, Stina and Béla, and finally destroys the barbarians' outpost
// and main base. The mission's own triggers do everything else (dialogues, sequences, owner changes, quests, the
// computer players waking up, QUIT).
// Cheats (CampaignBot): the player's units are healed and the friendly Norsemen kept above 80 %, enemies near the
// player's fighters are worn down, a unit that makes no progress for 60 s is teleported (all reported).
// Ported from remake/tests/campaign_play_01.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play01 : CampaignBot
{
    public Play01(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        var P3 = C.Players[3]; var P1 = C.Players[1]; var P2 = C.Players[2];
        Unit? cole() => unit("Cole_s0"); Unit? bela() => unit("Bela_s0");
        Func<Entity, bool> p1In(string region) => e => e.Kind == "unit" && e.Owner == P1 && e.Cls == "CHTR" && inRegion(e, region);
        // the walk through the mission: [name, what to do each second, done?, seconds allowed]
        var steps = new List<Step>
        {
            new("the intro film and the mentor: the first quests appear", () => { }, () => quest("L01MQ01_find_stina")!.Visible && quest("L01MQ01b_find_bela")!.Visible, 40),
            new("Cole walks up the beach into the fight with the Dilophosaurus", () => go(new[] { cole() }, 273, 539), () => fired("start_ds_1020_and_warrior"), 120),
            new("the Dilophosauruses are killed and their nest destroyed", () => { if (!hunt(new[] { cole() }, e => (e.Name == "Dilophosaurus" || (e.IsNest && inRegion(e, "dilo_fight_area"))) && !(Invuln(e) > 0), "cole")) go(new[] { cole() }, 288, 512); }, () => fired("first_fight_ready"), 180),
            new("Cole meets the Norseman at the question mark; he thanks and walks home", () => go(new[] { cole() }, 275, 505), () => fired("hu_thx"), 120),
            new("Cole reaches the village: the barbarians inside turn hostile", () => go(new[] { cole() }, 282, 486), () => fired("cole arrive_first_village"), 120),
            new("the barbarians in the first village are beaten", () => { if (!hunt(new[] { cole() }, p1In("first_village_fight"), "cole")) go(new[] { cole() }, 268, 432); }, () => fired("all_hostile_in_village_killed") && fired("warrior_at_gate_no2"), 240),
            new("Cole talks to the warriors at the gate: quest \"Rescue from Dilophosauruses\" done, the Norsemen join", () => go(new[] { cole() }, 293, 414), () => fired("village_ready"), 180),
            new("outside the village: the barbarians with the captured spearmen are seen", () => go(fighters(), 323, 414), () => fired("check_red_sign"), 120),
            new("the direct way: the barbarians attack", () => go(fighters(), 353, 400), () => fired("start_direct_attack") || fired("start_sneak_attack"), 120),
            new("the guards are beaten and the spearmen freed", () => { if (!hunt(fighters(), p1In("spearman_complete"), "all")) go(fighters(), 424, 418); }, () => fired("player_0_in_ready"), 400),
            new("Cole finds Stina (sequence sc_1040); she joins", () => go(fighters(), 382, 503), () => fired("GC04#start_fighting_s1"), 240),
            new("the Smilodons around Stina are killed: quest \"Stina's Rescue\" done", () => { if (!hunt(fighters(), e => e.Name == "Smilodon" && inRegion(e, "stina_start_fight") && !(Invuln(e) > 0), "all")) go(fighters(), 410, 540); }, () => fired("GC08#stina_first_fight_win"), 240),
            new("on to the Smilodon pride", () => go(fighters(), 481, 542), () => fired("GD02#check_player_in_region"), 240),
            new("all Smilodons and their cave are destroyed (sequence sc_1050)", () => { if (!hunt(fighters(), e => (e.Name == "Smilodon" && inRegion(e, "Smilo_Dead")) || (e.IsNest && inRegion(e, "smilodon_pride_area")), "all")) go(fighters(), 470, 480); }, () => fired("GE01#create_quest_sign"), 500),
            new("Cole and Stina reach the prison (sequence sc_1060): Béla joins", () => go(fighters().Where(u => u.Name != "Bela_s0"), 506, 411), () => fired("GE03#play_with_bela_after_seq"), 300),
            new("Béla frees the archers", () => go(new[] { bela() }, 606, 332, "bela"), () => fired("free_archer"), 240),
            new("the guards of the prison camp are beaten", () => { if (!hunt(fighters().Where(u => inRegion(u, "prison_complete_area")), p1In("prison_complete_area"), "bela")) go(new[] { bela() }, 580, 360, "bela"); }, () => fired("check_all_blood_in_prison_dead"), 500),
            new("Béla frees the workers: the camp is the player's (sequence sc_1071), the base phase starts", () => go(new[] { bela() }, 626, 407, "bela"), () => fired("start_base"), 300),
            new("the barbarian outpost is destroyed (side quest)", () => { hunt(fighters(), e => e.Owner == P1, "all"); }, () => fired("Gh20#ai_outpost_dead"), 1500),
            new("the barbarian main base falls: its town centre is destroyed", () => { if (!hunt(fighters(), e => e.Owner == P2 && e.Name == "hu_fireplace", "all")) hunt(fighters(), e => e.Owner == P2, "all"); }, () => fired("Gi01#ai_main_base_dead"), 2000),
            new("the final film ms_1120 and QUIT", () => { }, () => Over, 60),
        };
        int far = runSteps(steps, new RunOpts { CheatOpts = new CheatOpts { Friends = { P3 } } });
        if (far >= 7) ok(questState("L01MQ03_help_hu_vs_dilo") == "done" && W.Units.Count(u => u.Alive && u.Owner == me) > 1, "quest \"Rescue from Dilophosauruses\" accomplished, Norsemen joined the player (OCPY)");
        if (far >= 10) ok(questState("L01MQ04_free_spearman") == "done" && unit(own("hu_spearman")) != null, "quest \"Liberation of the Spearmen\" accomplished, the spearmen are the player's");
        if (far >= 12) ok(questState("L01MQ01_find_stina") == "done" && (unit("Stina_s0") != null || unit("special_eusmilus") != null), "quest \"Stina's Rescue\" accomplished, Stina is the player's");
        if (far >= 14) ok(questState("L01MQ05_revenge_of_cats") == "done", "quest \"Wild Cat Alert\" accomplished");
        if (far >= 15) ok(questState("L01MQ01b_find_bela") == "done" && bela() != null, "quest \"Béla's Rescue\" accomplished, Béla is the player's");
        if (far >= 18) ok(questState("L01MQ06_free_the_prison") == "done" && W.Buildings.Any(b => b.Alive && b.Owner == me) && W.Units.Any(u => u.Alive && u.Owner == me && u.IsWorker), "quest \"Liberation of Prisoners\" accomplished: the player has buildings and workers");
        if (far >= 18) ok(Brain(1)?.Behaviour == "Dodo", $"the outpost's computer player was woken (AIBV Dodo): {Brains()}");
        if (far >= 20) ok(true, "main quest \"The Barbarian Threat\": " + questState("L01MQ02_vanish_the_blood_hu"));
        if (far >= 21) ok(Over && S.Won, "QUIT: the mission is won");
        return Report(new() { [3] = "first_fight_ready", [6] = "all_hostile_in_village_killed", [10] = "player_0_in_ready", [12] = "GC06#check_smilodons_dead", [14] = "GD04#check_all_smilodon_dead",
            [17] = "check_all_blood_in_prison_dead", [18] = "bela_worker_ready", [19] = "Gh20#ai_outpost_dead", [20] = "Gi01#ai_main_base_dead" }) +
            "\n   brains: " + Brains() + "; diplomacy of player 0: " + string.Join(" ", C.Players.Where(p => p != null).Select(p => p!.Id + "=" + me.Relation(p)));
    }
}
