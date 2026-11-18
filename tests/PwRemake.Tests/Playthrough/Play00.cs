// Mission 0 "Tutorial" played by script, the way a player follows Ada and the mentor: camera lessons, the heroes to
// the marker, the Smilodon in the village, rebuilding the village (food, workers, stone quarry, forester's lodge,
// resources, repairs), Béla from the tavern, ... The mission's own triggers do everything else.
//   PW_PLAY=1 PW_PLAY_MISSIONS=0 dotnet run --project tests/PwRemake.Tests -- Playthroughs
// Ported from remake/tests/campaign_play_00.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play00 : CampaignBot
{
    public Play00(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        (int, int) regionMap(string n) { var c = regionC(n)!.Value; return ToMapR(c.X, c.Z); }
        Entity? home() => named("hu_fireplace_1") ?? (Entity?)myBuildings().FirstOrDefault(b => b.Def != null && b.Def.Script == "CFireplace") ?? myBuildings().FirstOrDefault();
        (int, int) homeMap() { var h = home(); return h != null ? ToMapR(h.Pos.X, h.Pos.Z) : (711, 472); }
        Func<Entity, bool> smilodon(string? region = null) => e => Regex.IsMatch(e.Name, "Smilodon", RegexOptions.IgnoreCase) && e.Kind == "unit" && (region == null || inRegion(e, region)) && !(Invuln(e) > 0);
        int queued(System.Func<QueueItem, bool> f) => myBuildings().Cast<Entity>().Concat(mine()).Sum(b => b.Queue.Count(f));
        void workersTo(int n) { int have = workers().Count + queued(q => q.Action.Results[0].Obj.Contains("worker")); if (have < n) produce("hu_worker", n - have); }
        (int, int) woodNear() { var h = home(); var (x, z) = h != null ? (h.Pos.X, h.Pos.Z) : C.ToGame(711, 472); var n = W.NearestResource(x, z, "wood", 150); return n != null ? ToMapR(n.Pos.X, n.Pos.Z) : homeMap(); }
        void repairs()
        {
            foreach (var n in new[] { "hu_fireplace_1", "hu_stone_cottage_2" })
            {
                var b = named(n);
                if (b != null && b.Hp < b.MaxHp) { var ws = workers().Where(w => w.Task.Type != "repair").Take(2).ToList(); if (ws.Count > 0) Order(ws, "repair", target: b); }
            }
        }
        // the cottages Gca11 asks for (BLDG value ">2" in the original map: 3; MIRAGE's map of the tutorial asks ">3")
        int cottages() { var v = E.Find("Gca11#cottages_ready")?.Conds.FirstOrDefault(c => c.Type == "BLDG")?.P.S("value") ?? ">2"; var m = Regex.Match(v, @"(>=?)\s*(\d+)"); return !m.Success ? 3 : int.Parse(m.Groups[2].Value) + (m.Groups[1].Value == ">" ? 1 : 0); }
        var steps = new List<Step>
        {
            // ---- chapter 1
            new("the camera lessons: scroll, zoom, turn", () => camera(!fired("Gaa06#cam_is_moved") ? "scroll" : !fired("Gaa11#cam_is_zoomed") ? "zoom" : "pan"), () => fired("Gaa16#cam_is_panned"), 400),
            new("the heroes are the player's and walk to the marker", () => goRegion(heroes(), "First_Quest_Move"), () => fired("Gaa30#heros_moved"), 400),
            new("the Smilodon in the village is killed", () => { if (!hunt(heroes(), smilodon("Smilodon_1_dead"), "heroes")) goRegion(heroes(), "Smilodon_1_dead"); }, () => fired("Gaa38#smilodon_dead"), 400),
            new("the heroes follow Ada: chapter 1 is over", () => goRegion(heroes(), "End_Cap_1"), () => fired("Gaa44#Cap_End"), 400),
            // ---- chapter 2
            new("the workers harvest 90 food", () => gather("food"), () => fired("Gba06#Food_harvested"), 400),
            new("3 more workers", () => { workersTo(6); gather("food"); }, () => fired("Gba16#Worker_ready"), 400),
            new("a hero finds the stones", () => { goRegion(new[] { unit("Cole_s0") }, "Stone_sighted", "cole"); gather(); }, () => fired("Gba23#Stone_found"), 400),
            new("a stone quarry next to the stones", () => { build("hu_stone_quarry", regionMap("Stone_sighted")); gather(); }, () => fired("Gba37#StoneQ_build"), 600),     // (another tribe: a bigger building, the stone from one pile 150 m away)
            new("a forester's lodge next to the woods", () => { build("hu_lumberjack_cottage", woodNear()); gather(); }, () => fired("Gba36#Forrester_build"), 400),
            new("300 food, wood and stone", () => { workersTo(9); gather(new Dictionary<string, double> { ["food"] = 320, ["wood"] = 320, ["stone"] = 320 }); }, () => fired("Gba31#all_ready"), 1500),
            new("the town centre and the cottage are repaired", () => { repairs(); gather(); }, () => fired("Gba45#All_repaired"), 600),
            new("Béla is hired in the tavern", () => { if (unit("Bela_s0") == null) produce("Bela_s0", 1); }, () => fired("Gba63#Bela_is_free"), 400),
            // ---- chapter 3
            new("two more cottages", () => { build("hu_stone_cottage", homeMap(), count: cottages()); gather(); }, () => fired("Gca11#cottages_ready"), 900),
            new("epoch 2 in the town centre", () => { if (!fired("Gca15#age_2_reached")) produce("age_2", 1, "Upgrades"); gather(); }, () => fired("Gca15#age_2_reached"), 900),
            new("a barracks", () => { build("hu_arena", homeMap()); gather(); }, () => fired("Gca19#barracks_build"), 600),
            new("4 spearmen", () => { var sp = own("hu_spearman"); int have = mine().Count(u => u.Name == sp) + myBuildings().Sum(b => b.Queue.Count(q => q.Action.Results[0].Obj == sp)); if (have < 4) produce("hu_spearman", 4 - have); gather(); }, () => fired("Gca26#all_spearm_build"), 900),
            new("the heroes and spearmen find the nest", () => goRegion(fighters(), "Smilodon_nest_seen"), () => fired("Gca33#Nest_found"), 600),
            new("the Smilodons and their nest are destroyed", () => { if (!hunt(fighters(), e => smilodon()(e) || (e.IsNest && inRegion(e, "Smilodon_nest_seen")), "all")) goRegion(fighters(), "Smilodon_nest_seen"); }, () => fired("Gca39#Nest_Smilos_killed"), 600),
            // (not in the JS: MIRAGE's skulls fall short of the first level's 25 here - SEAS 23 - and the step waited for ever;
            // the skulls are topped up then, a cheat, counted - a player would hunt for them, the mission's Gca46 says so)
            new("a unit is promoted", () => { if (!promote()) TopUp(new() { ["skulls"] = 25 }); }, () => fired("Gca49#level_uped"), 400),
            new("back to Ada at the second gate", () => goRegion(fighters(), "Second_Gate"), () => fired("Gca44#by_ada"), 600),
            // ---- chapter 4
            new("an animal farm", () => { build("hu_small_animal_farm", homeMap()); gather(); }, () => fired("Gda05#farm_build"), 600),
            new("a chariot", () => { if (unit(own("hu_chariot")) == null && !myBuildings().Any(b => b.Queue.Count > 0)) produce("hu_chariot", 1); gather(); }, () => fired("Gda09#chariot_build"), 600),
            new("an archer", () => { if (unit(own("hu_archer")) == null && !myBuildings().Any(b => b.Queue.Any(q => q.Action.Results[0].Obj == own("hu_archer")))) produce("hu_archer", 1); gather(); }, () => fired("Gda13#archer_build"), 600),
            new("the archer boards the chariot", () => { var a = unit(own("hu_archer")); var c = unit(own("hu_chariot")); if (a != null && c != null && !a.Inside && a.Task.Type != "board") Order(a, "board", target: c); }, () => fired("Gda14#archer_loaded"), 300),
            new("the thief: caught or escaped", () => { var c = unit(own("hu_chariot")); hunt(new[] { c }, e => e.Owner == C.Players[3] && e.Kind == "unit", "chariot"); }, () => fired("Gda21#thief_hunted") || fired("Gda20#thief_safe"), 400),
            new("10 fighters at the third gate", () =>
            {
                var F = mine().Where(u => !u.IsWorker).ToList();
                if (F.Count < 11 && !myBuildings().Any(b => b.Queue.Count > 0)) { TopUp(new() { ["food"] = 400, ["wood"] = 300 }); produce("hu_warrior", 2); }
                goRegion(F, "Ready_for_last_fight"); gather();
            }, () => fired("Gda26#open_gate_3"), 1200),
            // (walking there is not enough: the item is picked up with the right click's "pick up" order, systems/items.js)
            new("the artifact is picked up", () =>
            {
                var it = R.ByName("item_amulett1_2"); var cole = unit("Cole_s0");
                Item? item = it?.Entity as Item ?? (it?.Prop != null ? W.ItemOfProp(it.Prop) : null) ?? W.Items.FirstOrDefault(x => x.ItemCls == "item_amulett1");
                if (cole == null || item == null || item.Carrier != null) return;
                if (cole.Task.Type != "pickup") Order(cole, "pickup", target: item);
            }, () => fired("Gda29#item_found"), 600),
            new("the attackers are beaten", () => { hunt(fighters(), e => e.Owner == C.Players[2] && e.Kind == "unit", "all"); }, () => fired("Gda33_0#Check"), 900),
            new("the final sequence and QUIT", () => { }, () => Over, 200),
        };
        runSteps(steps);
        return Report(new() { [1] = "Gaa16#cam_is_panned", [2] = "Gaa30#heros_moved", [3] = "Gaa38#smilodon_dead", [4] = "Gaa44#Cap_End", [5] = "Gba06#Food_harvested", [6] = "Gba16#Worker_ready",
            [7] = "Gba23#Stone_found", [8] = "Gba37#StoneQ_build", [9] = "Gba36#Forrester_build", [10] = "Gba31#all_ready", [11] = "Gba45#All_repaired", [12] = "Gba63#Bela_is_free",
            [13] = "Gca11#cottages_ready", [14] = "Gca15#age_2_reached", [15] = "Gca19#barracks_build", [16] = "Gca26#all_spearm_build", [17] = "Gca33#Nest_found", [18] = "Gca39#Nest_Smilos_killed",
            [19] = "Gca49#level_uped", [20] = "Gca44#by_ada", [21] = "Gda05#farm_build", [22] = "Gda09#chariot_build", [23] = "Gda13#archer_build", [24] = "Gda14#archer_loaded", [26] = "Gda26#open_gate_3",
            [27] = "Gda29#item_found", [28] = "Gda33_0#Check", [29] = "Ea01#Exit_Kap_2" }) + "\n   won: " + EndInfo;
    }
}
