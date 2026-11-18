// Mission 15 "New World Order" played by script: after the opening film sc_7010 (quest "The Core of the Problem")
// the army marches to the first pumping station (film ms_7026: the countdown starts, quest "Countdown of Death") and
// takes the five stations one after the other, leaving two soldiers to hold each; with all five held the countdown
// stops and the crater opens (sequence sc_7040). Béla, Cole, Stina and Taslow walk into the crater (quest "The Core of
// the Problem"); the film sc_7041 ends the mission. The base trains soldiers meanwhile; those still on the base island
// fight the SEAS landings there first, and the skulls buy levels. The side quests are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_15.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play15 : CampaignBot
{
    public Play15(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        HuntFarGo = true;
        var FOUR = new[] { "Bela_s0", "Cole_s0", "special_eusmilus", "tesla_s0" };
        List<Unit> four() => FOUR.Select(unit).Where(u => u != null).Cast<Unit>().ToList();
        // (not a flyer - the SEAS helicopters: the soldiers and heroes on foot cannot hit them, an attack order on one ends
        // at once and was given again every second - the base's new soldiers stood at the shore for the rest of the run;
        // the archers shoot them by themselves, the cheat wears them down near the fighters)
        bool foe(Entity e) => e.Owner != null && e.Owner != me && !me.IsFriend(e.Owner) && !(e is Unit fu && fu.Flying);
        var ORDER = new[] { 1, 3, 2, 4, 5 };
        bool held(int n) => v("ctrlpt0" + n) == 1 && Var("ctrlpt0" + n) != null;
        string point(int n) => "controlpoint_0" + n;
        // two soldiers stay at every station the player holds. They hold their ground and fight only what enters the
        // station: a station is lost as soon as none of the player's objects stands in it while an enemy does
        // (Gc0Nb#lose_ctrlpt_0N)
        const int GARRISON = 2;
        var garrison = new Dictionary<int, List<Unit>>();
        List<Unit> guards() => garrison.Values.SelectMany(g => g).ToList();
        // (Taslow - 500 hit points - stays on the base island out of the fights: he has to walk into the crater at the end,
        // and at a held station he fell to its attackers)
        var homeGuard = new HashSet<Unit>();
        List<Unit> army() { var gs = guards().ToHashSet(); return fighters().Where(u => !gs.Contains(u) && !homeGuard.Contains(u) && u.Name != "lovelace_s0" && u.Name != "tesla_s0").ToList(); }
        // the base island is attacked too: the SEAS (players 3-4) cross by hovercraft and land at the base (AI water
        // crossings, docs/AI.md "Transports that stood in place" 2: before that fix they never crossed). The soldiers still
        // on the base island fight the landings first - without them the raids broke the main building (MIRAGE as the
        // Norsemen: the fireplace fell at 14 min, the population limit dropped to 8 and nothing more was trained)
        Entity? home() => myBuildings().FirstOrDefault(b => b.Def != null && (b.Def.Script ?? "").Contains("CFireplace")) ?? (Entity?)myBuildings().FirstOrDefault();
        void defend()
        {
            homeGuard.Clear();
            var h = home();
            if (h == null) return;
            bool raider(Entity e) => foe(e) && e.Kind == "unit" && e.DistTo(h) < 100;
            if (!All().Any(e => e.Alive && !e.Parked && !e.Inside && raider(e))) return;
            var d = army().Where(u => !FOUR.Contains(u.Name) && u.DistTo(h) < 130).ToList();
            if (d.Count == 0) return;
            homeGuard.UnionWith(d);
            hunt(d, raider, "home");
        }
        // a held station that lost its guards waits for new ones only while there can be any: soldiers left in the army or
        // room to train them (the Dragon Clan's 20 places were full - the heroes stood at station 3 for the rest of the run)
        bool canReinforce() => army().Any(u => !FOUR.Contains(u.Name)) || me.Units + me.QueuedUnits < me.MaxUnits;
        int alive(int k) => garrison.TryGetValue(k, out var g) ? g.Count(u => u.Alive) : 0;
        void hold()
        {
            foreach (var n in ORDER)
            {
                if (!held(n)) continue;
                var g = (garrison.TryGetValue(n, out var have) ? have : new List<Unit>()).Where(u => u.Alive).ToList();
                if (g.Count < GARRISON) g = g.Concat(army().Where(u => !FOUR.Contains(u.Name)).Take(GARRISON - g.Count)).ToList();
                garrison[n] = g;
                foreach (var u in g) u.Stance = 0;            // hold ground: shoot what comes in range, never chase it out (the stance buttons: no command)
                if (g.Count > 0 && !hunt(g, e => foe(e) && e.Kind == "unit" && inRegion(e, point(n)), "guard" + n)) goRegion(g, point(n), "guard" + n);
            }
        }
        void @base()
        {
            gather();
            housing();
            // (fewer workers where the population limit is small: the Dragon Clan's fireplace holds 15 + 5 per hammock - in
            // the base game one hammock, 20 places for workers, heroes and the ten guards of the stations)
            if (workers().Count < Math.Clamp((int)me.MaxUnits / 4, 4, 8)) produce("hu_worker", 1);
            if (fighters().Count >= 30) return;
            // the stations need guards faster than the volcano's few food sources feed them: the stock is topped up while
            // nothing is being trained or researched (a cheat, counted) - enough for an epoch, which the Dragon Clan's next
            // hammock (room for more units) and the Dustriders' warriors wait for
            bool shortG = ORDER.Where(held).Any(k => alive(k) < GARRISON);
            if (shortG && !myBuildings().Cast<Entity>().Concat(mine()).Any(b => b.Queue.Count > 0)) TopUp(new() { ["food"] = 800, ["wood"] = 800, ["stone"] = 800 });
            // (and spearmen: MIRAGE's Dustriders train only them before their warriors and archers show up - aje_spearman_1 is
            // the rodeo's one visible unit at epoch 1)
            produce("hu_warrior", 1); produce("hu_archer", 1); produce("hu_spearman", 1);
        }
        // the army takes the next station: what stands in it or within 25 m of the army is fought, then it walks on - but
        // first it stays at a held station that has fewer guards than it needs, until the base has sent them
        void take()
        {
            int weak = canReinforce() ? ORDER.FirstOrDefault(k => held(k) && alive(k) < GARRISON) : 0;
            int n = weak != 0 ? weak : ORDER.FirstOrDefault(k => !held(k));
            if (n == 0) return;
            var a = army();
            if (a.Count == 0) return;
            if (!hunt(a, e => foe(e) && e.Kind == "unit" && (e.DistTo(a[0]) < 25 || inRegion(e, point(n))), "army")) goRegion(a, point(n), "army");
        }
        var steps = new List<Step>
        {
            new("the opening film sc_7010: quest \"The Core of the Problem\"", () => { }, () => questState("L15MQ01") == "open", 120),
            // the film plays when the player comes near a station (S02#7026: any of the ovals of region sc_7026 around them,
            // not the station itself): the army walks to the one before the first station, fighting what it meets
            new("the army reaches the first pumping station: film ms_7026, quest \"Countdown of Death\"", () =>
            {
                @base();
                var a = army();
                if (a.Count > 0 && !hunt(a, e => foe(e) && e.Kind == "unit" && e.DistTo(a[0]) < 25, "army")) go(a, 380, 722, "army");
            }, () => questState("L15MQ02") == "open", 1500),
            new("the five pumping stations are taken and held: the countdown stops, sequence sc_7040 (quest \"Countdown of Death\")", () => { @base(); promote(); defend(); hold(); take(); }, () => questState("L15MQ02") == "done", 3400),
            new("Béla, Cole, Stina and Taslow walk into the crater: quest \"The Core of the Problem\"", () =>
            {
                @base(); promote(); defend(); hold();
                // a hero that fell on the way (the mission goes on without it - no defeat trigger - but needs all four in the
                // crater) is bought back at the tavern (its equivalent; built if missing), as a player would
                foreach (var h in FOUR) if (unit(h) == null) produce(h, 1);
                // (one that is alive but inside something - a transport, a building it entered - comes out)
                foreach (var u in W.Units.Where(u => u.Alive && u.Owner == me && u.Inside && FOUR.Contains(u.Name)).ToList()) W.LeaveTransport(u, null, null);
                var f = four();
                if (!hunt(f, e => foe(e) && e.Kind == "unit" && e.DistTo(f[0]) < 25, "heroes")) goRegion(f, "exit", "heroes");
            }, () => questState("L15MQ01") == "done", 1500),
            new("the film sc_7041 ends the mission", () => { }, () => Over, 300),
        };
        runSteps(steps);
        ok(!Over || S.Won, "QUIT: the mission is won");
        foreach (var h in FOUR) { var u = W.Units.FirstOrDefault(x => x.Name == h && x.Owner == me); info($"hero {h}: " + (u == null ? "none" : $"alive {u.Alive} parked {u.Parked} inside {u.Inside} at {ToMapR(u.Pos.X, u.Pos.Z)} task {u.Task}")); }
        return Report(new() { [2] = "S02#7026", [3] = "S07#7040", [4] = "L01b#mq01_done", [5] = "S08#7041" }) + "\n   won: " + EndInfo +
            $"\n   population {me.Units}/{N(me.MaxUnits)}, epoch {me.Epoch()}" +
            "\n   variables: " + string.Join(" ", new[] { "ctrlpts", "ctrlpt01", "ctrlpt02", "ctrlpt03", "ctrlpt04", "ctrlpt05", "cntdwn_aborted", "blocker" }.Select(n => n + "=" + Var(n)));
    }
}
