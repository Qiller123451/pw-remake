// Mission 14 "Prisoners" played by script: after the landing film (sc_6050 / _6050a / _6050b, quest "Hunt Leighton")
// the army walks inland until the first prison comes into sight (sequence sc_7042, quest "Ada's Liberation"), fights
// its way past the SEAS main base onto the way to the last prison (sequence sc_6105: Leighton flees, quest "Hunt
// Leighton" accomplished) and breaks the main prison where Ada is held (quest "Ada's Liberation"); the film ms_7020
// ends the mission. The side quests are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_14.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play14 : CampaignBot
{
    public Play14(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        List<Unit> army() => fighters();
        bool foe(Entity e) => e.Owner != null && e.Owner != me && !me.IsFriend(e.Owner);
        Entity? jail() => named("seas_jail_part_01_8");
        // on the way: fight what stands within 25 m of the first unit (units, towers and turrets), else walk on
        void march(string region)
        {
            var a = army();
            if (!(a.Count > 0 && hunt(a, e => foe(e) && e.DistTo(a[0]) < 25 && (e.Kind == "unit" || Regex.IsMatch(e.Name, "turret|tower|bunker")), "army"))) goRegion(a, region, "army");
        }
        var steps = new List<Step>
        {
            new("the landing film: quest \"Hunt Leighton\"", () => { }, () => questState("L14MQ01") == "open", 120),
            new("the army walks inland until the first prison comes into sight: sequence sc_7042, quest \"Ada's Liberation\"", () => march("second_sequence"), () => questState("L14MQ02") == "open", 1200),
            new("the army gets past the SEAS main base onto the way to the last prison: sequence sc_6105, quest \"Hunt Leighton\"", () => march("way_to_last_prison"), () => questState("L14MQ01") == "done", 2400),
            new("the army breaks the main prison: quest \"Ada's Liberation\"", () =>
            {
                var a = army(); var j = jail();
                if (a.Count == 0 || j == null) return;
                // MIRAGE's map makes the main part invulnerable at sc_7042 and vulnerable again only when both side parts
                // are below 1200 hit points (05c_main jail veruable; each side part turns invulnerable itself then, 05a /
                // 05b): while it is protected the army breaks the side parts first
                Entity? target = j;
                if (Invuln(j) > 0) target = new[] { "seas_jail_part_02_3", "seas_jail_part_02_4" }.Select(named).FirstOrDefault(p => p != null && Invuln(p) <= 0 && p.Hp >= 1200) ?? j;
                if (!hunt(a, e => foe(e) && e.Kind == "unit" && e.DistTo(a[0]) < 25, "army")) hunt(a, e => e == target, "army");
            }, () => questState("L14MQ02") == "done", 2400),
            new("the film ms_7020 ends the mission", () => { }, () => Over, 300),
        };
        runSteps(steps);
        ok(!Over || S.Won, "QUIT: the mission is won");
        return Report(new() { [2] = "03_ns_7042_must_free_Ada", [3] = "02_ns_6105_leighton_flees", [4] = "02_L14MQ02_acc_<-[Lb01]", [5] = "01_win_level" }) + "\n   won: " + EndInfo;
    }
}
