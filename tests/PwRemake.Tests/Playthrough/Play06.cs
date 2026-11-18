// Mission 6 "Pirates and Hostages" played by script: the heroes fight their way west to the pirates' prison and
// break its buildings (the hostages are free: sequence sc_8080, quest "Freeing the Hostages"); meanwhile the base
// grows. Then the big pirate ship in the bay is sunk (sequence sc_8013, quest "The big Pirate Ship"); the film ms_8090
// ends the mission. The ransom side quests are left alone.
// The ship lies in the walled bay behind the pirates' water gate, out of reach from the land ("The ship cannot be
// captured from dry land. Build a strong fleet to attack it from the sea": the quest's hint): the base builds a
// harbour (the Dustriders' floating harbour is allowed once the pirates have asked for the ransom, D06) and warships
// (the strongest the tribe can make), and the fleet breaks the water gate and sinks the ship, as the walkthroughs do.
// A warship backs off from what it fights when that is inside its minimum range (it cannot shoot there). After the
// prison the army goes home and fights the pirates' landings at the harbour.
// (The JS playthrough sent the heroes to the shore: the cheat's damage reached the ship's parts there through their
// radius - here they stand 60 m off and it never did.)
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported, resources are topped up while the base waits for them (all counted and
// reported).
// Ported from remake/tests/campaign_play_06.js (pw-exporter 98c7703); the fleet written here.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play06 : CampaignBot
{
    public Play06(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        var P1 = C.Players[1]; var P6 = C.Players[6];
        bool isShip(Entity u) => Ru.Info(u.Name)?.Type == "SHIP";
        List<Unit> army() => fighters().Where(u => !isShip(u)).ToList();
        List<Unit> ships() => mine().Where(isShip).ToList();
        List<Unit> warships() => ships().Where(u => !Regex.IsMatch(u.Name, "fishing|transport")).ToList();
        bool foe(Entity e) { var a = army(); return a.Count > 0 && a[0].IsEnemy(e) && e.Owner != null; }
        bool prison(Entity e) => e.Kind == "building" && e.Owner == P6 && inRegion(e, "prison_op") && !e.Name.Contains("palisade");
        // the ship's parts on deck (sail, tail, bow) and whatever the pirates have built in the bay: Gb01 waits for them
        bool boss(Entity e) => e.Owner == P1 && e.Kind == "building" && inRegion(e, "check_boss");
        // the warships each tribe can make, the strongest first (the Dustriders' catamaran breaks buildings from the
        // water, epoch 4 - the mentor's hint L06B09)
        var SHIPS = new Dictionary<string, string[]>
        {
            ["Aje"] = new[] { "aje_catamaran", "aje_cronosaurus", "aje_torpedo_turtle" },
            ["Hu"] = new[] { "hu_steam_boat", "hu_dragon_boat" },
            ["Ninigi"] = new[] { "ninigi_rocket_boat", "ninigi_fire_boat" },
            ["SEAS"] = new[] { "seas_cannon_turtle", "seas_submarine" },
        };
        var shipCls = SHIPS.TryGetValue(me.Tribe, out var sc) ? sc : SHIPS["Hu"];
        bool canMake(string cls) => myBuildings().Cast<Entity>().Concat(mine()).Where(b => b is not Building bb || bb.Built)
            .Any(b => ActionsOf(b).Any(a => a.Kind == "Build" && Makes(a, cls) && Ru.Check(me, a, b is Unit ? b : null) == null));
        void @base()
        {
            gather();
            housing();
            if (workers().Count < Math.Clamp((int)me.MaxUnits / 4, 4, 10)) produce("aje_worker", 1);
            if (warships().Count >= 12) return;
            // the strongest ship it can make now; the epochs for the best meanwhile (the stock topped up while nothing is
            // queued: a cheat, counted)
            var now = shipCls.FirstOrDefault(canMake);
            if (now != null) produce(now, 1);
            if (now != shipCls[0]) produce(shipCls[0], 1);
            if (!myBuildings().Cast<Entity>().Concat(mine()).Any(b => b.Queue.Count > 0)) TopUp(new() { ["food"] = 1200, ["wood"] = 1200, ["stone"] = 800 });
        }
        // the fleet: it gathers by the harbour until it has eight warships, then sails north round the island and west along
        // the north shore - north of the pirates' two mine belts (x 677 and 790, y 714-841: mines go off within 10 m) - to
        // the water gate (PT_Citywall_Gate_7, 517,623); it fights the pirates' ships and water turrets it meets,
        // breaks the gate's turrets and the gate, and goes for the ship's parts in the bay
        var WPS = new[] { (1330, 880), (900, 885), (600, 885), (522, 705) };
        int leg = 0; bool sailing = false;
        var pirates = new[] { C.Players[1], C.Players[2], C.Players[3], C.Players[4], C.Players[5] }.Where(p => p != null).ToHashSet();
        bool pirate(Entity e) => e.Owner != null && pirates.Contains(e.Owner) && e.Name != "ninigi_mineship_mine";
        bool gateWorks(Entity e) => e.Owner == P1 && (e.Name == "PT_Citywall_Gate" || e.Name == "ninigi_water_turret") && Hypot(e.Pos.X - C.ToGame(517, 623).X, e.Pos.Z - C.ToGame(517, 623).Z) < 60;
        // a warship with an enemy inside its cannon's minimum range (the steam boat's 20 m) cannot shoot it and the fight
        // ends (Fight.usl, docs/SIMULATION.md "Minimum range"): it backs off, as a player would move it, to shoot from
        // further away. (The pirates' fire boats - their AI crossings now set out at once, docs/AI.md - kept the fleet
        // busy by the water gate and it stopped 15-19 m from the last water turret and a fire boat: neither side's guns
        // fired for 40 minutes.) Only from what the fleet fights (ships, the gate's works, the flagship's parts), not from
// what is closer than 12 m: the cheat wears that down.
        void keepRange(List<Unit> s)
        {
            foreach (var u in s)
            {
                Entity? close = null; double cd = 1e9;
                foreach (var e in All())
                {
                    if (!e.Alive || e.Parked || e.Inside || !(pirate(e) && isShip(e) || gateWorks(e) || boss(e)) || !u.IsEnemy(e)) continue;
                    double d = u.DistTo(e);
                    if (d < 12 + e.Radius || d > 40 || d >= cd || !W.RangeZoneOf(u, e).Min) continue;
                    cd = d; close = e;
                }
                if (close == null) continue;
                double dx = u.Pos.X - close.Pos.X, dz = u.Pos.Z - close.Pos.Z, l = Math.Max(0.1, Hypot(dx, dz));
                Order(u, "move", u.Pos.X + dx / l * 15, u.Pos.Z + dz / l * 15);
            }
        }
        void fleet()
        {
            var s = warships();
            if (s.Count == 0) return;
            bool near(Entity e, double r) => s.Any(u => u.DistTo(e) < r);
            try { fleetOrders(s, near); } finally { keepRange(s); }
        }
        void fleetOrders(List<Unit> s, Func<Entity, double, bool> near)
        {
            // (eight, or four when the population limit leaves no room for more: the Dragon Clan's 20)
            if (!sailing) sailing = s.Count >= 8 || (s.Count >= 4 && me.Units + me.QueuedUnits >= me.MaxUnits);
            // what comes near on the water is fought (the pirates' ships and water turrets; not their towers on the shore,
            // which ships that fight close in never reached)
            if (hunt(s, e => pirate(e) && isShip(e) && near(e, 55), "fleet")) return;
            // (while it gathers it waits where the harbour put it: a ship sent to the harbour itself never got there)
            if (!sailing) return;
            if (leg < WPS.Length)
            {
                var (wx, wz) = C.ToGame(WPS[leg].Item1, WPS[leg].Item2);
                if (s.Count(u => Hypot(u.Pos.X - wx, u.Pos.Z - wz) < 45) * 2 >= s.Count) leg++;
                else { go(s, WPS[leg], "fleet"); return; }
            }
            if (hunt(s, gateWorks, "fleet")) return;
            if (!hunt(s, boss, "fleet")) go(s, 519, 560, "fleet");
        }
        // the base: after the prison the army goes home and fights the pirates' landings there (the Ninigi pirates ship
        // archers, spearmen, trikes and a baryonyx to the harbour from about 4 minutes on: left alone they broke the
        // harbour, the workers and the fireplace and no warship was made after the first dragon boats)
        Entity? home() => myBuildings().FirstOrDefault(b => b.Name.Contains("harbour")) ?? myBuildings().FirstOrDefault(b => (b.Def?.Script ?? "").Contains("CFireplace")) ?? (Entity?)myBuildings().FirstOrDefault();
        void defend()
        {
            var h = home();
            if (h == null) return;
            bool raider(Entity e) => foe(e) && e.Kind == "unit" && !isShip(e) && e.DistTo(h) < 100;
            hunt(army().Where(u => !u.Amphib).ToList(), raider, "home");
        }
        var steps = new List<Step>
        {
            new("quest \"Freeing the Hostages\" from the start", () => { }, () => questState("L06MQ01") == "open", 60),
            new("the army reaches the pirates' prison and breaks its buildings: the hostages are free (sc_8080)", () =>
            {
                @base();
                var a = army();
                if (!(a.Count > 0 && hunt(a, e => foe(e) && e.Kind == "unit" && e.DistTo(a[0]) < 25, "army")) && !(inRegion(a[0], "prison_op") && hunt(a, prison, "army"))) goRegion(a, "At the prison", "army");
            }, () => questState("L06MQ01") == "done", 2400),
            new("the fleet breaks the water gate and sinks the big pirate ship in the bay (sc_8013)", () =>
            {
                @base(); fleet(); defend();
                // amphibious units go for the ship themselves: MIRAGE's sequence units after sc_8080 (Gxa06) give the
                // Dragon Clan a baryonyx (the Dustriders' ankylosaurus), and its population - 20 - has no room left for a
                // fleet
                var amph = army().Where(u => u.Amphib).ToList();
                if (amph.Count > 0) hunt(amph, boss, "amphibians");
            }, () => questState("L06MQ02") == "done", 3600),
            new("the film ms_8090 ends the mission", () => { }, () => Over, 300),
        };
        // (the cheat's damage leaves the flagship's hull alone - 100 hit points, the parts on deck carry the fight; worn
        // down it ended the mission through Gb01's first condition before the parts were broken)
        runSteps(steps, new RunOpts { CheatOpts = new CheatOpts { Spare = e => e.Name == "pirate_boss" } });
        return Report(new() { [2] = "S05: NS_8080 - All hostages are free delay", [3] = "Gb01: delay S03: 8013" }) + "\n   won: " + EndInfo +
            $"\n   warships {string.Join(" ", warships().Select(u => u.Name))}; epoch {me.Epoch()}";
    }
}
