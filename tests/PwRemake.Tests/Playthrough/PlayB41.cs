// BoosterPack 1, mission 4-1 "The Devil's Advocate" played by script (no playthrough in the browser remake: written
// from the mission's triggers, tools/prepare's single_b41 mission folder). Also its SEAS version, MIRAGE's mission 20
// (single_b41_seas: the same triggers with the SEAS classes and headquarters).
// After the opening film sb4101 (quests "Breakthrough Part I / II": 20 flamethrowers and Leighton to the rendezvous
// point on the far side of the island before the 150-minute timer runs out) the heroes' small party walks to the place
// of first contact, where Dustriders and Norsemen fight (dialogue db_4102); when the fight is over the dialogue db_4111
// asks for an outpost and epoch 4 (quest "The Bridgehead"). The workers put up a base, research the epochs and train
// flamethrowers; the flamethrowers march in squads to the rendezvous point (Flamer_Exit: a flamethrower in
// Activate_Flamer_occupy hands those in the region to the fugitives, Gz01 .. Gz30, who board the carrier) until 20 are
// saved (quest Part I; a small last squad takes three more along); Leighton waits there (Part II). With epoch 4
// researched (quest "The Bridgehead") Leighton boards the carrier (Xa01 .. Xa03) and the film sb4108 ends the mission
// (Xa04_Win). The side quests (the crystals, "Time is Money") are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported, resources are topped up for the epochs and the flamethrowers (all counted
// and reported).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class PlayB41 : CampaignBot
{
    public PlayB41(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        bool seas = C.MissionTribe == "SEAS";                           // mission 20, single_b41_seas
        string flamerCls = own(seas ? "seas_flamethrower" : "hu_flamethrower") ?? "hu_flamethrower";
        string mainCls = seas ? "seas_headquarters" : "hu_fireplace";
        string age4 = seas ? "seas_headquarters/age_4" : "hu_fireplace/age_4";
        Unit? leighton() => unit("livingstone_s0");
        List<Unit> flamers() => mine().Where(u => u.Name == flamerCls).ToList();
        // the base: next to the map ping of the quest "The Bridgehead" (Mc01: 293.9, 728.9), on the plateau of first contact
        var BASE = (294, 729);
        var WAIT = (1188, 336);                                        // F02: Leighton's place in Flamer_Exit
        bool foe(Entity e) => e.Owner != null && e.Owner != me && !me.IsFriend(e.Owner) && me.IsEnemy(e.Owner);
        var squad = new HashSet<Unit>();
        bool staged = false;
        string? collWhy = null;
        int lastCount = 0; double lastNew = 0;
        var STAGE = (1180, 330);                                       // inside Flamer_Exit, 30 m from Activate_Flamer_occupy
        int saved() => (int)v("Flamer_Fugees");
        int epoch() => me.Epoch();
        bool mainUp() => myBuildings().Any(b => b.Def != null && (b.Def.Script ?? "").Contains("CFireplace") || b.Name == own(mainCls)) || mine().Any(u => u.Name == "aje_resource_collector");
        // the main building: the workers put it up; the Dustriders (another tribe's mission without their mobile base)
        // make their resource collector at a small farm in small-farm mode (its local upgrade aje_small_farm shows it),
        // with a tent first: the collector needs a free place in the population (the tent: 5)
        void putUpMain()
        {
            if (me.Tribe != "Aje") { build(mainCls, BASE, workers: 3); return; }
            if (build("aje_tent", BASE, workers: 3) is not true) return;
            if (build("aje_small_farm", BASE, workers: 3) is not true) return;
            var farm = myBuildings().FirstOrDefault(b => b.Name == "aje_small_farm" && b.Built);
            if (farm == null || farm.Queue.Count > 0) return;
            TopUp(new() { ["food"] = 300, ["wood"] = 300, ["stone"] = 200 });
            // (the farm's own tree decides: its local upgrade shows the action - Rules.Check with the farm)
            var acts = ActionsOf(farm);
            var coll = acts.FirstOrDefault(a => a.Kind == "Build" && a.Id == "aje_resource_collector");
            var mode = acts.FirstOrDefault(a => a.Kind == "Upgrades" && a.Id == "aje_small_farm");
            if (coll != null && Ru.Check(farm.RulesOwner(), coll, farm) == null) collWhy = QueueAction(farm, coll) ?? "queued";
            else if (mode != null) QueueAction(farm, mode);
        }
        double nextStatus = 0, nextWhy = 0;
        void status()
        {
            if (W.Time < nextStatus) return;
            nextStatus = W.Time + 180;
            status1($"[{N(Round(W.Time))} s] epoch {epoch()}, units {me.Units}/{N(me.MaxUnits)}, flamethrowers {flamers().Count} (squad {squad.Count}{(squad.Count > 0 ? " at " + string.Join(" ", squad.Where(u => u.Alive).Take(3).Select(u => ToMapR(u.Pos.X, u.Pos.Z))) : "")}), saved {saved()}, fighters {fighters().Count}, workers {workers().Count}, buildings {myBuildings().Count}: " +
                 string.Join(" ", myBuildings().GroupBy(b => b.Name).Select(g => g.Key + "x" + g.Count())) + "; res " + string.Join(",", me.Res.Select(kv => kv.Key + " " + N(Math.Round(kv.Value)))) + ageWhy());
        }
        void status1(string line) { info(line); Progress?.Invoke(line); }
        string ageWhy()
        {
            if (me.Tribe == "Aje" && !mainUp())
            {
                var farm = myBuildings().FirstOrDefault(b => b.Name == "aje_small_farm");
                if (farm == null) return "; no small farm";
                var acts = ActionsOf(farm);
                return "; farm built " + farm.Built + ", queue " + farm.Queue.Count + ": " + string.Join(" ", acts.Where(a => a.Id.Contains("collector") || a.Id.Contains("small_farm")).Select(a => $"{a.Kind}/{a.Id}={Ru.Check(farm.RulesOwner(), a, farm) ?? "ok"}")) + "; queue: " + collWhy;
            }
            if (epoch() >= 4) return "";
            var a = actionFor("age_" + (epoch() + 1), "Upgrades");
            return a == null ? "; no epoch action" : $"; next epoch: {Ru.Check(me, a, null) ?? "ok"} (missing {string.Join(",", Ru.Missing(me, a))})";
        }
        // the base is kept: fighters not marching fight what comes within 60 m of the player's buildings
        void defend()
        {
            var home = myBuildings();
            if (home.Count == 0) return;
            var d = fighters().Where(u => !squad.Contains(u) && u.Name != "livingstone_s0").ToList();
            if (d.Count == 0) return;
            if (!hunt(d, e => foe(e) && e.Kind == "unit" && home.Any(b => b.DistTo(e) < 60), "defence")) go(d, BASE, "defence");
        }
        void @base()
        {
            status();
            defend();
            gather(new Dictionary<string, double> { ["food"] = 600, ["wood"] = 600, ["stone"] = 400 });
            if (!mainUp()) { putUpMain(); return; }
            housing();
            if (workers().Count < 8) produce(seas ? "seas_worker" : "hu_worker", 1);
            // the epochs: researched one after the other at the main building (their buildings put up by produce /
            // prerequisites); the stock topped up while nothing is researched (a cheat, counted)
            if (epoch() < 4)
            {
                var a = actionFor("age_" + (epoch() + 1), "Upgrades");
                if (a != null && !myBuildings().Cast<Entity>().Any(b => b.Queue.Count > 0 && b.Queue.Any(q => q.Action == a))) TopUp(new() { ["food"] = a.Cost.Food + 200, ["wood"] = a.Cost.Wood + 200, ["stone"] = a.Cost.Stone + 200 });
                produce("age_" + (epoch() + 1), 1, "Upgrades");
            }
            // the flamethrowers need Leighton at level 4 (inventobjects livingstone_s4: his Lvl4_tt filter): he is promoted
            // with skulls (topped up: a cheat, counted - a player collects them from the fights)
            var l = leighton();
            if (l != null && l.Level < 4)
            {
                var why = ChangeLevel(l, l.Level + 1);
                if (why != null) { TopUp(new() { ["skulls"] = 400 }); why = ChangeLevel(l, l.Level + 1); }
                if (why != null && W.Time >= nextWhy) { nextWhy = W.Time + 120; info($"[{N(Round(W.Time))} s] Leighton level {l.Level}: promotion refused: {why}"); }
            }
            // flamethrowers: as many as still have to be saved (and three more for a small last squad: Spare)
            int left = Math.Max(0, 20 - saved());
            int want = left + Spare(left) - flamers().Count;
            if (want > 0)
            {
                var a = actionFor(flamerCls);
                if (a != null) TopUp(new() { ["food"] = a.Cost.Food * 2 + 100, ["wood"] = a.Cost.Wood * 2 + 100, ["stone"] = a.Cost.Stone * 2 + 100, ["skulls"] = a.Cost.Skulls * 2 });
                // the building that trains them (MIRAGE's SEAS: the laboratory - their barracks offer only Leighton's
                // own override version, which produce() would wait for)
                var at = a?.Locs.Select(x => x.At).FirstOrDefault(nm => Ru.Info(nm)?.Type == "BLDG");
                if (at != null) build(at, BASE);
                produce(flamerCls, 1);
            }
        }
        // a small last squad takes three more along: the Norsemen's army between the base and the rendezvous point killed
        // squads of three again and again (MIRAGE as the Dustriders, once the fights went otherwise with the minimum-range
        // rule of the target choice - the Norsemen's log cannons among them; docs/SIMULATION.md "Target choice by the
        // minimum range")
        static int Spare(int left) => left > 0 && left < 8 ? 3 : 0;
        // a squad of flamethrowers marches to the rendezvous point (whatever stands in its way within 25 m is fought);
        // a squad is sent when it is as big as what is still missing, or 12, or the pyramid has no room for more. It
        // gathers in Flamer_Exit first (STAGE): the first flamethrower in Activate_Flamer_occupy (Gz01) hands over only
        // those standing in Flamer_Exit a second later (Gz01_1), a straggler would stay behind
        void march()
        {
            squad.RemoveWhere(u => !u.Alive || u.Owner != me);
            var idle = flamers().Where(u => !squad.Contains(u)).ToList();
            int need = 20 - saved() - squad.Count;
            if (need > 0) need += Spare(need);
            // (and when no new one was trained for two minutes: the population limit or the pyramid is reached)
            if (flamers().Count != lastCount) { lastCount = flamers().Count; lastNew = W.Time; }
            if (need > 0 && idle.Count > 0 && (idle.Count >= Math.Min(need, 12) || W.Time - lastNew > 120)) { foreach (var u in idle.Take(need)) squad.Add(u); staged = false; lastNew = W.Time; }
            var s = squad.Where(u => u.Alive).ToList();
            if (s.Count == 0) return;
            if (hunt(s, e => foe(e) && e.Kind == "unit" && e.DistTo(s[0]) < 25, "squad")) return;
            if (!staged) { go(s, STAGE, "squad"); staged = s.All(u => inRegion(u, "Flamer_Exit") && Hypot(u.Pos.X - C.ToGame(STAGE.Item1, STAGE.Item2).X, u.Pos.Z - C.ToGame(STAGE.Item1, STAGE.Item2).Z) < 15); return; }
            var (ax, az) = regionC("Activate_Flamer_occupy")!.Value;
            foreach (var u in s) if (!inRegion(u, "Activate_Flamer_occupy") && (u.Task.Type != "move" || Math.Abs(u.Task.X - ax) > 1)) Order(u, "move", ax, az);
        }
        void leightonWaits()
        {
            var l = leighton();
            if (l == null) return;
            go(new[] { l }, WAIT, "leighton");
        }
        var steps = new List<Step>
        {
            new("the opening film sb4101: quests \"Breakthrough Part I\" and \"Part II\"", () => { }, () => questState("LB41MQ01") == "open" && questState("LB41MQ02") == "open", 180),
            new("the party reaches the place of first contact: dialogue db_4102 (Dustriders and Norsemen fight)", () => go(mine(), BASE, "party"), () => fired("Ga03#Check_First_Contact"), 600),
            new("the fight is over, dialogue db_4111: quest \"The Bridgehead\" (epoch 4)", () => { gather(); }, () => questState("LB41MQ03") == "open", 600),
            new("the base reaches epoch 4: quest \"The Bridgehead\"", () => { @base(); march(); }, () => questState("LB41MQ03") == "done", 5400),
            new("20 flamethrowers reach the rendezvous point: quest \"Breakthrough Part I\"", () => { @base(); march(); }, () => questState("LB41MQ01") == "done", 3000),
            new("Leighton reaches the rendezvous point, boards the carrier: film sb4108 (quest \"Breakthrough Part II\")", () => { @base(); leightonWaits(); }, () => fired("Xa01#Leighton_leaves"), 1500),
            new("the film sb4108 ends the mission (QUIT)", () => { }, () => Over, 300),
        };
        runSteps(steps);
        ok(!Over || S.Won, "QUIT: the mission is won");
        return Report(new() { [2] = "Ga03#Check_First_Contact", [3] = "Ga17#Base_started", [4] = "Ga18#Base_Ready", [5] = "Gz50#Flamer_ready", [6] = "Xa01#Leighton_leaves", [7] = "Xa04_Win" }) + "\n   won: " + EndInfo +
            $"\n   epoch {epoch()}, flamethrowers {flamers().Count} ({flamerCls}), population {me.Units}/{N(me.MaxUnits)}" +
            "\n   variables: " + string.Join(" ", new[] { "Flamer_Fugees", "MQ01", "MQ02", "MQ03", "Bases_seen", "Ho_Destroyed", "Need_For_Speed_Failed" }.Select(n => n + "=" + Var(n)));
    }
}
