// BoosterPack 1, mission 4-2 "A Cold Day In Hell" played by script (no playthrough in the browser remake: written from
// the mission's triggers, tools/prepare's single_b42 mission folder). Also its SEAS version, MIRAGE's mission 21
// (single_b42_seas).
// After the opening film sb4201 (quests "The Archeologist", "The Devil's Right Hand") Leighton's party rescues the
// survivors of the excavation from the sabre-tooth cats (dialogues db_4202 / db_4203: they join), walks on to the
// excavation, where Kleemann's expedition fights the pirates (dialogue db_4205, quest "The Devil's Left Hand": Kleemann
// must live); with the pirates beaten the film sb4206 gives the player the expedition (quest "The Archeologist", then
// "The Chevaliers of Fortune": the Key to Walhalla). The party crosses the ice caves to the riddle room (film sb4223),
// where Kleemann steps on the five panels of the pentacle in order (Gi02a .. Gi06: a wrong panel brings an undead
// warrior and starts again); the gate opens (dialogue db_4224, quest "The Chevaliers of Fortune"), the film sb4225 puts
// everyone in front of the Avatar (quest "No Sweet without Sweat"); the Avatar is beaten (Gj05: the undead fall with it)
// and the film hb4229 ends the mission. The rune gates and their side chambers, the missing team (side quest) are left
// alone: the caves' main way leads to the riddle room without them.
// Cheats (CampaignBot): the player's units are healed (Kleemann's expedition kept above 80 % while it fights the pirates
// as the player's friend), enemies near the player's fighters are worn down, a unit that makes no progress for 60 s is
// teleported (all counted and reported).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class PlayB42 : CampaignBot
{
    public PlayB42(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        var P1 = C.Players[1];
        Unit? kleemann() => unit("schliemann_s0");
        List<Unit> army() => fighters();
        List<Unit> party() => mine();
        bool foe(Entity e) => e.Owner != me && (e.Owner == null ? e.Kind == "unit" && e.Def != null && e.Def.Aggressive == 1 : !me.IsFriend(e.Owner) && me.IsEnemy(e.Owner));
        // on the way: fight what stands within 25 m of the first unit, else walk on
        void march(string region, string key = "party") { var a = party(); if (a.Count > 0 && !hunt(a, e => foe(e) && e.Kind == "unit" && e.DistTo(a[0]) < 25, key)) goRegion(a, region, key); }
        void fightGroups(string fallback, params string[] groups)
        {
            var g = groups.SelectMany(grp).ToHashSet();
            if (!hunt(party(), e => g.Contains(e), "party")) goRegion(party(), fallback, "party");
        }
        // Kleemann on a panel (the other units wait next to him, fighting what comes)
        int panel() { for (int n = 1; n <= 5; n++) if (v("panel0" + n) != 1) return n; return 6; }
        void stepOnPanel()
        {
            var k = kleemann(); if (k == null) return;
            int n = panel(); if (n > 5) return;
            var others = party().Where(u => u != k).ToList();
            if (others.Count > 0 && !hunt(others, e => foe(e) && e.Kind == "unit" && e.DistTo(k) < 30, "guard")) goRegion(others, "riddle_room", "guard");
            goRegion(new[] { k }, "finalgate_panel0" + n, "kleemann");
        }
        Entity? avatar() => named("05_avatar");
        var cheat = new CheatOpts { Friends = { P1 } };
        var steps = new List<Step>
        {
            new("the opening film sb4201: quests \"The Archeologist\" and \"The Devil's Right Hand\"", () => { }, () => questState("LB42MQ01") == "open" && questState("LB42MQ02") == "open", 180),
            new("the survivors are rescued from the sabre-tooth cats: dialogue db_4203, they join (Gc03)", () =>
            {
                if (fired("Gc02#sighted")) fightGroups("encounter_survivors", "eusmilusgroup01", "eusmilusgroup02");
                else march("encounter_survivors");
            }, () => fired("Gc03#occupy"), 900),
            new("the party reaches the excavation: dialogue db_4205, quest \"The Devil's Left Hand\"", () => march("encounter_kleemann"), () => questState("LB42MQ03") == "open", 1200),
            new("the pirates are beaten, the film sb4206 gives the player Kleemann's expedition: quest \"The Archeologist\"", () => fightGroups("encounter_kleemann", "04_team01", "04_team02"), () => questState("LB42MQ01") == "done" && kleemann() != null, 900),
            new("quest \"The Chevaliers of Fortune\"", () => { }, () => questState("LB42MQ04") == "open", 120),
            new("the party crosses the ice caves to the riddle room: film sb4223", () => march("trigger_sb4223"), () => fired("Gi01#init"), 2400),
            new("Kleemann steps on the five panels of the pentacle in order: the gate opens (dialogue db_4224, quest \"The Chevaliers of Fortune\")", stepOnPanel, () => questState("LB42MQ04") == "done", 1200),
            new("the film sb4225: quest \"No Sweet without Sweat\"", () => { }, () => questState("LB42MQ05") == "open", 300),
            new("the Avatar is beaten: quests \"No Sweet without Sweat\", \"The Devil's Right / Left Hand\"", () =>
            {
                var av = avatar(); if (av == null) return;
                if (!hunt(army(), e => e == av, "army")) { }
            }, () => questState("LB42MQ05") == "done", 1200),
            new("the film hb4229 ends the mission (quit)", () => { }, () => Over, 300),
        };
        runSteps(steps, new RunOpts { CheatOpts = cheat });
        ok(!Over || S.Won, "the mission is won");
        return Report(new() { [2] = "Gc03#occupy", [3] = "L03a#mq03_set", [4] = "S02#sb4206", [5] = "L04a#mq04_set", [6] = "S07#sb4223", [7] = "Gi07#open_gate", [8] = "S08#sb4225", [9] = "Gj05#avatar_defeated", [10] = "S09#hb4229" }) + "\n   won: " + EndInfo +
            "\n   variables: " + string.Join(" ", new[] { "mq01_done", "mq04_done", "mq05_done", "panel01", "panel02", "panel03", "panel04", "panel05", "avatar_dead", "red_runes", "yellow_runes", "blue_runes" }.Select(n => n + "=" + Var(n)));
    }
}
