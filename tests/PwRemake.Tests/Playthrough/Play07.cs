// Mission 7 "The Water Temple" played by script: Cole, Béla and Stina flee from the pirates to Taslow's house
// (question mark 1: the film ms_2560, Taslow joins, quest "The Chase" accomplished), take Taslow to the Water Temple
// (sequence sc_2567, quest "The Water Temple"), then guard Taslow's place against the SEAS' Black Widows until all are
// beaten (quest "Spider Attack"); the film ms_2580 ends the mission. The side quests are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_07.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play07 : CampaignBot
{
    public Play07(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        var P3 = C.Players[3];
        List<Unit> four() => new[] { "Cole_s0", "Bela_s0", "special_eusmilus", "tesla_s0" }.Select(unit).Where(u => u != null).Cast<Unit>().ToList();
        List<Unit> army() => fighters();
        // the three waves of Black Widows (the one of the film near the temple, Aha02; the medium and the hard hovercraft's,
        // Aha01 - both at medium) walk to Taslow's place (Ahc02 / Ahc03 / Ahc04: 448-462, 952-974), and one inside the
        // region "GameOver - Wehrspinnen at Teslaplace" loses the mission (X03). The army guards the place: it fights
        // the Widows within 220 m of it and the SEAS soldiers that come near, and goes after a Widow farther away only
        // when nothing has come for two minutes (a straggler: Gc05 waits for every Widow). (The JS chased the nearest of
        // Widows and hovercraft - a hovercraft at sea first - and MIRAGE's stronger SEAS reached the place meanwhile.)
        var (gx, gz) = C.ToGame(455, 962);
        double dG(Entity e) => Hypot(e.Pos.X - gx, e.Pos.Z - gz);
        bool widow(Entity e) => e.Owner == P3 && e.Name == "seas_wehrspinne" && !e.Inside;
        double quietSince = -1;
        var approach = new Dictionary<Unit, (double D, double T)>();
        void guard()
        {
            var a = army();
            if (a.Count == 0) return;
            if (hunt(a, e => widow(e) && dG(e) < 220, "army") || hunt(a, e => e.Owner == P3 && e.Kind == "unit" && Ru.Info(e.Name)?.Type != "SHIP" && e.Name != "seas_hovercraft" && dG(e) < 80, "army")) { quietSince = W.Time; return; }
            if (quietSince < 0) quietSince = W.Time;
            // a straggler: no Widow is still on board or has come closer to the place within 90 s (base game, Dragon Clan:
            // one of the hard wave stood at 543,436 "walking" for 40 minutes)
            bool coming = false;
            foreach (var u in W.Units.Where(u => u.Alive && u.Owner == P3 && u.Name == "seas_wehrspinne"))
            {
                if (u.Inside) { coming = true; continue; }
                double d = dG(u);
                if (!approach.TryGetValue(u, out var ap) || d < ap.D - 3) approach[u] = ap = (d, W.Time);
                if (W.Time - ap.T < 90) coming = true;
            }
            if (W.Time - quietSince > 120 && !coming && hunt(a, widow, "army")) return;
            go(a, 455, 962, "army");
        }
        var steps = new List<Step>
        {
            new("quest \"The Chase\" from the start", () => { }, () => questState("L07MQ01") == "open", 30),
            new("the heroes reach Taslow's house (question mark 1): film ms_2560, Taslow joins", () => goRegion(four(), "qm1", "heroes"), () => questState("L07MQ01") == "done" && unit("tesla_s0") != null, 2400),
            new("Cole, Béla, Stina and Taslow reach the Water Temple: sequence sc_2567, quest \"The Water Temple\"", () => goRegion(four(), "water sanctuary", "heroes"), () => questState("L07MQ02") == "done", 2400),
            new("the Black Widows and the hovercraft convoy of the SEAS are beaten: quest \"Spider Attack\"", guard, () => questState("L07MQ03") == "done", 3000),
            new("the film ms_2580 ends the mission", () => { }, () => Over, 300),
        };
        runSteps(steps);
        return Report(new() { [2] = "Qab01: green ?", [3] = "Ie07: fire NS2567", [4] = "Lc02: L07MQ03 - set acc" }) + "\n   won: " + EndInfo +
            "\n   variables: " + string.Join(" ", new[] { "all Wehrspinnen disembarked", "Wehrspinnen zerstört", "WehrspinnenConvoi destroyed", "fire NS2567", "all_Wehrspinnen_disembarked", "Wehrspinnen_zerstört", "WehrspinnenConvoi_destroyed", "fire_NS2567" }.Where(n => Var(n) != null).Select(n => n + "=" + Var(n)));
    }
}
