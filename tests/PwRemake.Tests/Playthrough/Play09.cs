// Mission 9 "Perilous Path" played by script: the heroes follow the path north to the bridge (sequence sc_3025, the
// bridge breaks), reach the Dragon Clan village (dialogue ds_3020) and beat the Barbarians attacking it (quest
// "Rescue Mission", sequence sc_3030: the village joins, quest "Survival of the Fittest"), fetch the three parts of
// the wreck from the Barbarian altars (quest "Hunting the Looters"), clear the airplane and put up a tower next to
// it (quest "The Bird of Steel", sequence sc_3033, quest "A dangerous Path"), then the workers build the launch ramp
// (quest "Prepare for Start"); the film ms_3040 ends the mission. The side quests are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported, resources are topped up for the tower (all counted and reported).
// Ported from remake/tests/campaign_play_09.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play09 : CampaignBot
{
    public Play09(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        var P4 = C.Players[4];
        List<Unit> three() => new[] { "Bela_s0", "Cole_s0", "special_eusmilus" }.Select(unit).Where(u => u != null).Cast<Unit>().ToList();
        List<Unit> army() => fighters();
        bool foe(Entity e) { var a = army(); return a.Count > 0 && a[0].IsEnemy(e) && e.Owner != null; }
        HashSet<Entity> attackers() => new[] { "04_attgroup01", "04_attgroup02", "04_attgroup03", "04_attgroup04" }.SelectMany(grp).ToHashSet();
        // on the way: fight what stands within 25 m of the first unit, else walk on
        void march(List<Unit> a, string region, string key) { if (!(a.Count > 0 && hunt(a, e => foe(e) && e.Kind == "unit" && e.DistTo(a[0]) < 25, key))) goRegion(a, region, key); }
        bool part(int n) => Var("parts0" + n + "_done") is { } s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && x == 1;
        Entity? pad() => named("00_launchpad_01");
        (int, int) tower() { var c = regionC("airplane")!.Value; return ToMapR(c.X, c.Z); }
        var steps = new List<Step>
        {
            new("the opening film ms_3010: quests \"A dangerous Path\" and \"Survival of the Fittest\"", () => { }, () => questState("L09MQ01") == "open" && questState("L09MQ02") == "open", 120),
            new("the heroes follow the path to the bridge: sequence sc_3025 (the bridge breaks)", () => march(three(), "trigger_sc_3025", "heroes"), () => fired("Ga01#pos_sc_3025"), 1200),
            new("the heroes reach the Dragon Clan village: dialogue ds_3020, quest \"Rescue Mission\"", () => march(three(), "trigger_ds_3020", "heroes"), () => questState("L09MQ03") == "open", 1200),
            new("the Barbarians attacking the village are beaten: quest \"Rescue Mission\", sequence sc_3030, the village joins", () =>
            {
                var at = attackers();
                if (!hunt(army(), e => at.Contains(e), "army")) goRegion(army(), "trigger_ds_3020", "army");
            }, () => questState("L09MQ03") == "done" && questState("L09MQ02") == "done", 1800),
            new("quest \"Hunting the Looters\"", () => gather(), () => questState("L09MQ04") == "open", 60),
            new("the three parts of the wreck are fetched from the Barbarian altars", () =>
            {
                gather();
                int n = new[] { 2, 3, 4 }.FirstOrDefault(k => !part(k));
                if (n != 0) march(army(), "trigger_parts_0" + n, "army");
            }, () => questState("L09MQ04") == "done", 3600),
            new("the airplane is cleared and a tower stands next to it: quest \"The Bird of Steel\", sequence sc_3033", () =>
            {
                gather();
                var a = army();
                if (!(a.Count > 0 && hunt(a, e => e.Owner == P4 && inRegion(e, "airplane"), "army"))) goRegion(a, "airplane", "army");
                var ws = workers().Take(3).ToList();
                var r = build("ninigi_small_tower", tower(), workers: 3, region: "airplane");
                if (r is not true && !(r is "started")) { TopUp(new() { ["wood"] = 400, ["stone"] = 400 }); goRegion(ws, "airplane", "workers"); }
            }, () => questState("L09MQ05") == "done", 2400),
            new("the film sc_3033: quest \"A dangerous Path\" accomplished, the launch ramp is the player's", () => { var b = pad(); if (b != null) go(army().Concat(workers()), ToMapR(b.Pos.X, b.Pos.Z), "all"); }, () => questState("L09MQ01") == "done" && pad() != null && pad()!.Owner == me, 300),
            new("the workers build the launch ramp while the army guards it: quest \"Prepare for Start\"", () =>
            {
                var b = pad() as Building;
                if (b == null) return;
                var a = army();
                if (!hunt(a, e => foe(e) && e.Kind == "unit" && e.DistTo(b) < 90, "army")) go(a, ToMapR(b.Pos.X, b.Pos.Z), "army");
                // (workers left in the Dragon Clan village cannot walk to the ramp - the village is cut off by cliffs and
                // water on the map's walk grid, docs/CAMPAIGN.md: they are sent with go(), whose teleport takes them there;
                // a build order alone left MIRAGE's last worker building from the village for 50 minutes)
                var ws = workers(); var farW = ws.Where(w => b.SurfDist(w.Pos.X, w.Pos.Z) > 60).ToList();
                if (!b.Built && farW.Count > 0) go(farW, ToMapR(b.Pos.X, b.Pos.Z), "workers");
                foreach (var w in ws) if (!farW.Contains(w) && !b.Built && (w.Task.Type != "build" || w.Task.Building != b)) Order(w, "build", target: b);
            }, () => questState("L09MQ06") == "done", 3000),
            new("the film ms_3040 ends the mission", () => { }, () => Over, 300),
        };
        runSteps(steps);
        return Report(new() { [2] = "S01#sc_3025", [4] = "L03b#mq03_done", [6] = "L04b#mq04_done", [7] = "L05b#mq05_done", [9] = "L06b#mq06_done" }) + "\n   won: " + EndInfo +
            "\n   variables: " + string.Join(" ", new[] { "parts_collected", "mq03_done", "mq05_done", "mq06_done", "reinforcements" }.Select(n => n + "=" + Var(n)));
    }
}
