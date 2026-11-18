// Mission 12 "Sea Battles" played by script: after the opening film sc_4025 the army takes the telescope tower ruin
// (it becomes the player's) and guards it while the workers repair it (quest "The Telescope Tower", film sc_4030).
// Then the SEAS carrier ships come for 40 minutes (quest "The SEAS Storm"): the base grows (epoch 2, more workers),
// the harbour builds warships that wait on the carriers' way and go for every carrier, one near its goal first (no
// more than three may get through); the land army guards the base. The film sc_4038 ends the mission. The side quests
// are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_12.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play12 : CampaignBot
{
    public Play12(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        bool isShip(Entity u) => Ru.Info(u.Name)?.Type == "SHIP";
        List<Unit> ships() => mine().Where(isShip).ToList();
        List<Unit> warships() => ships().Where(u => !Regex.IsMatch(u.Name, "fishing|transport|catamaran")).ToList();
        List<Unit> army() => fighters().Where(u => !isShip(u)).ToList();
        bool foe(Entity e) => e.Owner != null && e.Owner != me && !me.IsFriend(e.Owner);
        Building? ruin() => named("ninigi_telescope_tower_ruins_4") as Building;
        bool carriers(Entity e) => e.Name == "seas_carrier_fake" && e.Alive;
        bool escorts(Entity e) => Regex.IsMatch(e.Name, "^seas_(submarine|helicopter)$") && e.Owner != null && e.Owner != me;
        // where the fleet waits: on the carriers' way to the north-west bay (Gc05 / Gc07 waypoints)
        var WAIT = (251, 281);
        void @base()
        {
            gather();
            housing();
            // (fewer workers where the population limit is small: the Dragon Clan stays at 20 - its fleet needs the places)
            if (workers().Count < Math.Clamp((int)me.MaxUnits / 4, 5, 10)) produce("ninigi_worker", 1);
            // the warships need epoch 2
            var boat = actionFor(own("ninigi_fire_boat"));
            var why = boat != null ? Ru.Check(me, boat, null) : "none";
            if (why == "req" || why == "hidden") produce("age_2", 1, "Upgrades");
            else if (warships().Count < 8) produce("ninigi_fire_boat", 1);
            // the land army stays by the main building and fights what comes near (the harbour too)
            var home = homeAt(); var (hx, hz) = C.ToGame(home.X, home.Y); var a = army();
            // (only what stands on ground it can walk on: a SEAS helicopter hovering over the water off the shore had the
            // army walk towards it for ten minutes, teleported every minute)
            bool onLand(Entity e) { var nav = W.Nav; return nav.Block[nav.Idx(e.Pos.X, e.Pos.Z)] == 0; }
            if (a.Count > 0 && !hunt(a, e => foe(e) && e.Kind == "unit" && !isShip(e) && Hypot(e.Pos.X - hx, e.Pos.Z - hz) < 110 && onLand(e) && a.Any(u => W.CanTarget(u, e)), "army")) go(a, homeAt().X + 15, homeAt().Y + 15, "army");
        }
        void fleet()
        {
            var s = warships();
            if (s.Count == 0) return;
            // a carrier near its goal first (within 350 m of the region "Zielregion der Carrier": one that gets there
            // counts, Gca Touchdown Carrier), else the one nearest the fleet - chasing only the nearest one let a carrier
            // through while the fleet was after another across the map (MIRAGE as the Norsemen: four got through once the
            // fights at sea went otherwise - the Dustrider allies' catamarans choose their targets by their minimum range,
            // docs/SIMULATION.md "Target choice by the minimum range"); always the one nearest the goal sent the fleet
            // round the whole map (MIRAGE as the SEAS: their submarines were lost on the way)
            var goal = regionC("Zielregion der Carrier");
            Entity? urgent = goal is var (gx, gz) ? All().Where(e => carriers(e) && !e.Parked && !e.Inside && Hypot(e.Pos.X - gx, e.Pos.Z - gz) < 350).OrderBy(e => Hypot(e.Pos.X - gx, e.Pos.Z - gz)).FirstOrDefault() : null;
            if (urgent != null ? hunt(s, e => e == urgent, "fleet") : hunt(s, carriers, "fleet")) return;
            if (hunt(s, e => escorts(e) && e.DistTo(s[0]) < 40, "fleet")) return;
            go(s, WAIT, "fleet");
        }
        var steps = new List<Step>
        {
            new("the opening film sc_4025: quest \"The Telescope Tower\"", () => gather(), () => questState("L12MQ01") == "open", 120),
            new("the army reaches the tower ruin: it becomes the player's", () => { gather(); goRegion(army(), "TowerOwnerChange", "army"); }, () => ruin() != null && ruin()!.Owner == me, 900),
            new("the workers repair the telescope tower while the army guards it: quest \"The Telescope Tower\", film sc_4030", () =>
            {
                gather();
                var r = ruin();
                if (r == null) return;
                if (!hunt(army(), e => foe(e) && e.Kind == "unit" && e.DistTo(r) < 60, "army")) goRegion(army(), "TowerOwnerChange", "army");
                var ws = workers().Take(4).ToList();
                foreach (var w in ws) if (!r.Built && (w.Task.Type != "build" || w.Task.Building != r)) Order(w, "build", target: r);
            }, () => questState("L12MQ01") == "done", 1800),
            new("quest \"The SEAS Storm\"", () => @base(), () => questState("L12MQ03") == "open", 300),
            new("the fleet stops the carrier ships for 40 minutes: film sc_4038, the mission is won", () => { @base(); fleet(); }, () => Over, 3600),
        };
        runSteps(steps);
        ok(!Over || S.Won, "QUIT: the mission is won");
        return Report(new() { [3] = "La02: Accomplished", [5] = "Gcb04#check_all_carrier" }) + "\n   won: " + EndInfo +
            "\n   variables: " + string.Join(" ", new[] { "Carrier", "Turmstörer", "Tower_Island" }.Select(n => n + "=" + Var(n))) + "; warships " + string.Join(" ", warships().Select(u => u.Name));
    }
}
