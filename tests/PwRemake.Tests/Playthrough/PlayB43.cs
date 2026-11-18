// BoosterPack 1, mission 4-3 "Operation Neptune" played by script (no playthrough in the browser remake: written from
// the mission's triggers, tools/prepare's single_b43 mission folder). Also its SEAS version, MIRAGE's mission 22
// (single_b43_seas).
// After the opening film sb4301 and the dialogue db_4301 (quest "Tabula Rasa") the army breaks the Dustriders' camp at
// the bay: their counter-attack (02_counterattack01: the tyrannosaurs) and every building of theirs in the player's
// future base (L01b; the pirates' reinforcements join on the way, Ga01 .. Ga06). The film sb4305 opens the quest "The
// Agony of Choice": a unit at the Norsemen's horn (trigger_signal) or Kleemann at the Dragon Clan's rocket panel. The
// army marches north to the horn (Ge01: victory_condition 1) and the film sb4302a ends the mission. The side quests
// (dinosaur nests, the Norse harbours, the titan technology) are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class PlayB43 : CampaignBot
{
    public PlayB43(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        var P2 = C.Players[2];
        List<Unit> army() => fighters();
        bool foe(Entity e) => e.Owner != null && e.Owner != me && !me.IsFriend(e.Owner) && me.IsEnemy(e.Owner);
        // (L01b: every building of player 2 in player_base except aje_bone_palisade|aje_bone_palisade_gate)
        bool wallLike(Entity e) => e.Name is "aje_bone_palisade" or "aje_bone_palisade_gate";
        void tabulaRasa()
        {
            var a = army(); if (a.Count == 0) return;
            var ca = grp("02_counterattack01").ToHashSet();
            if (ca.Count > 0 && hunt(a, e => ca.Contains(e), "army")) return;
            if (hunt(a, e => foe(e) && e.Kind == "unit" && e.DistTo(a[0]) < 25, "army")) return;
            // (MIRAGE's version: L01b waits for five named buildings; the others turn invulnerable and untargetable when
            // they are nearly down - Gx01 .. Gx05 - and are left)
            if (hunt(a, e => e.Owner == P2 && e.Kind == "building" && !wallLike(e) && !e.Untargetable && Invuln(e) <= 0 && inRegion(e, "player_base"), "army")) return;
            goRegion(a, "player_base", "army");
        }
        void toTheHorn()
        {
            var a = army(); if (a.Count == 0) return;
            if (!hunt(a, e => foe(e) && e.Kind == "unit" && e.DistTo(a[0]) < 25, "army")) goRegion(a, "trigger_signal", "army");
        }
        var steps = new List<Step>
        {
            new("the opening film sb4301, dialogue db_4301: quest \"Tabula Rasa\"", () => { }, () => questState("LB43MQ01") == "open", 180),
            new("the Dustriders' camp at the bay is broken (their counter-attack and buildings): quest \"Tabula Rasa\"", tabulaRasa, () => questState("LB43MQ01") == "done", 2400),
            new("the film sb4305: quest \"The Agony of Choice\"", () => { }, () => questState("LB43MQ02") == "open", 300),
            new("a unit reaches the Norsemen's horn: quest \"The Agony of Choice\"", toTheHorn, () => questState("LB43MQ02") == "done", 2400),
            new("the film sb4302a ends the mission (quit)", () => { }, () => Over, 300),
        };
        runSteps(steps);
        ok(!Over || S.Won, "the mission is won");
        return Report(new() { [1] = "L01a#mq01_set", [2] = "L01b#mq01_done", [3] = "L02a#mq02_set", [4] = "Ge01#signal", [5] = "S02a#sb4302a" }) + "\n   won: " + EndInfo +
            "\n   variables: " + string.Join(" ", new[] { "mq01_done", "mq02_set", "mq02_done", "victory_condition" }.Select(n => n + "=" + Var(n))) +
            "\n   Dustrider buildings left in the bay: " + string.Join(" ", W.Buildings.Where(b => b.Alive && b.Owner == P2 && !wallLike(b) && inRegion(b, "player_base")).Select(b => $"{b.Name}@{ToMapR(b.Pos.X, b.Pos.Z)} hp {N(Math.Round(b.Hp))} built {b.Built} untargetable {b.Untargetable} invuln {N(Invuln(b))}"));
    }
}
