// Mission 10 "The Prophet" played by script: after the opening film sc_3080 the heroes and the archers make their way
// north through the jungle until the temple comes into sight (sequence sc_3070, quest "Stalking"), then fight through
// the SEAS base to the temple entrance (quest "No Trespassing"); the film sc_3090 ends the mission. The workers stay
// at the start. The side quests are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_10.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play10 : CampaignBot
{
    public Play10(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        List<Unit> three() => new[] { "Bela_s0", "Cole_s0", "special_eusmilus" }.Select(unit).Where(u => u != null).Cast<Unit>().ToList();
        List<Unit> army() => fighters().Where(u => u.Name != "darwin_s0").ToList();
        bool foe(Entity e) { var a = army(); return a.Count > 0 && a[0].IsEnemy(e) && e.Owner != null; }
        // on the way: fight what stands within 25 m of the first unit, else walk on
        void march(List<Unit> a, string region, string key) { if (!(a.Count > 0 && hunt(a, e => foe(e) && e.DistTo(a[0]) < 25 && (e.Kind == "unit" || Regex.IsMatch(e.Name, "tower|turret|gate|wall")), key))) goRegion(a, region, key); }
        var steps = new List<Step>
        {
            new("the opening film sc_3080: quest \"Stalking\"", () => { }, () => questState("L10MQ01") == "open", 120),
            new("the army makes its way north until the temple comes into sight: sequence sc_3070, quest \"Stalking\"", () => march(army(), "trigger_sc_3070", "army"), () => questState("L10MQ01") == "done", 2400),
            new("Béla, Cole and Stina fight through the SEAS base to the temple entrance: quest \"No Trespassing\"", () =>
            {
                var t = three();
                march(army().Where(u => !t.Contains(u)).ToList(), "trigger_temple", "army");
                march(three(), "trigger_temple", "heroes");
            }, () => questState("L10MQ02") == "done", 3000),
            new("the film sc_3090 ends the mission", () => { }, () => Over, 300),
        };
        runSteps(steps);
        return Report(new() { [2] = "L01b#mq01_done", [3] = "L02b#mq02_done", [4] = "S04#sc_3090" }) + "\n   won: " + EndInfo +
            "\n   variables: " + string.Join(" ", new[] { "alarm_set", "mq02_done", "player01_dead", "player02_dead" }.Select(n => n + "=" + Var(n)));
    }
}
