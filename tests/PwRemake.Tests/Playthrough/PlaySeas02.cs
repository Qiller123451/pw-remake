// MIRAGE's SEAS mission "Seeking Ada" (single_seas_02, mission id 23 of the MIRAGE configuration) played by script (no
// playthrough in the browser remake: written from the mission's triggers, tools/prepare's mission folder).
// After the opening film (quest "Seeking Ada") Leighton's squad finds the Barbarians' outpost (dialogue
// s02_aussenposten, quest "The Outpost") and destroys it (MQ03_done: player 1 has neither units nor producers); the film
// seq_ss2_apdest opens the quest "The Main Base" and brings the infiltration team. When its workers wait at the meeting
// point, Leighton joins them (dialogue s02_infiltration): they walk into the main base and open its gates. The squad
// enters the main base (quest "Ada's Rescue": the execution timer) and four of its units reach the sacrificial pit (end
// region): the film seq_ss2_end ends the mission. The base ("Building a Base") and the docks are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class PlaySeas02 : CampaignBot
{
    public PlaySeas02(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        var P1 = C.Players[1];
        Unit? leighton() => unit("livingstone_s0");
        List<Unit> army() => mine().Where(u => !u.IsWorker).ToList();
        bool foe(Entity e) => e.Owner != null && e.Owner != me && !me.IsFriend(e.Owner) && me.IsEnemy(e.Owner);
        void march(string region, Func<Entity, bool>? target = null)
        {
            var a = army(); if (a.Count == 0) return;
            if (hunt(a, e => foe(e) && e.Kind == "unit" && e.DistTo(a[0]) < 25, "army")) return;
            if (target != null && hunt(a, target, "army")) return;
            goRegion(a, region, "army");
        }
        bool p1Producer(Entity e) => e.Owner == P1 && (e.Kind == "unit" ? !e.Parked : ((e as Building)?.Built ?? false));
        string Q(int n) => "single_seas_02_MQ0" + n;
        var steps = new List<Step>
        {
            new("the opening film: quest \"Seeking Ada\"", () => { }, () => questState(Q(1)) == "open", 180),
            new("the squad finds the Barbarians' outpost: dialogue s02_aussenposten, quest \"The Outpost\"", () => march("Aussenposten"), () => questState(Q(3)) == "open", 1500),
            new("the outpost is destroyed (its units and producers): quest \"The Outpost\", film seq_ss2_apdest, quest \"The Main Base\"", () => march("Aussenposten", p1Producer), () => questState(Q(4)) == "open", 3000),
            new("the infiltration team waits at the meeting point and Leighton joins it: dialogue s02_infiltration", () =>
            {
                var l = leighton(); if (l == null) return;
                var others = army().Where(u => u != l).ToList();
                if (others.Count > 0 && !hunt(others, e => foe(e) && e.Kind == "unit" && e.DistTo(l) < 30, "army")) goRegion(others, "infiltration_wait", "army");
                if (fired("when ready")) goRegion(new[] { l }, "meeting point", "leighton"); else goRegion(new[] { l }, "infiltration_wait", "leighton");
            }, () => fired("activate"), 1500),
            new("the infiltration team opens the gates of the main base: dialogue s02_infiltration_end", () => march("infiltration_wait"), () => fired("spawn_real_infiltrationteam"), 900),
            new("the squad enters the main base: quest \"Ada's Rescue\"", () => march("Barbaren_Hauptlager"), () => questState(Q(5)) == "open", 1200),
            new("four units reach the sacrificial pit: film seq_ss2_end", () => march("end region"), () => fired("seq_end"), 900),
            new("the film seq_ss2_end ends the mission (quit)", () => { }, () => Over, 300),
        };
        runSteps(steps);
        ok(!Over || S.Won, "the mission is won");
        return Report(new() { [2] = "aussŸenposten.dlg", [3] = "MQ03_done", [4] = "activate", [5] = "spawn_real_infiltrationteam", [6] = "MQ05_start", [7] = "seq_end_before " }) + "\n   won: " + EndInfo +
            "\n   variables: " + string.Join(" ", new[] { "harbours_operate", "difficulty", "outpost_alive", "adaquest" }.Select(n => n + "=" + Var(n))) +
            "\n   outpost left: " + string.Join(" ", W.Units.Cast<Entity>().Concat(W.Buildings).Where(e => e.Alive && p1Producer(e)).GroupBy(e => e.Name).Select(g => g.Key + "x" + g.Count()));
    }
}
