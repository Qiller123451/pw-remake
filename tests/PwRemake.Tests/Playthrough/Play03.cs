// Mission 3 "Amazon Island" played by script: the heroes make their way into the Amazon fortress and attack the
// temple; when it is down to 2000 hit points the Amazons give in (sequence sc_2010) and the mission is won. The
// workers keep the base going meanwhile.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_03.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play03 : CampaignBot
{
    public Play03(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        Entity? temple() => named("amazon_temple_5");
        (int, int) templeAt() { var t = temple(); return t != null ? ToMapR(t.Pos.X, t.Pos.Z) : (587, 822); }
        var steps = new List<Step>
        {
            new("the opening film ms_2005: quest \"The Amazon Temple\"", () => gather(), () => questState("L03MQ01") == "open", 120),
            new("the heroes reach the temple and attack it: quest accomplished, sequence sc_2010", () =>
            {
                gather();
                var t = temple();
                if (t != null && fighters().Any(u => u.DistTo(t) < 30)) { foreach (var u in fighters()) if (u.Task.Type != "attack" || u.Task.Target != t) Order(u, "attack", target: t); }
                else go(fighters(), templeAt());
            }, () => questState("L03MQ01") == "done", 2400),
            new("the film ends the mission (QUIT)", () => { }, () => Over, 200),
        };
        runSteps(steps);
        return Report(new() { [2] = "02_L03MQ01_acc_<-[La01]", [3] = "02_win_game" }) + "\n   won: " + EndInfo;
    }
}
