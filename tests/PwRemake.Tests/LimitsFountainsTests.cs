// A: MIRAGE's building limits (src/Sim/Systems/BuildingLimits.cs; the game option "Building limit" - BldgMaxCapacity;
// the farm limits design mirage_aje_farm_limits_v3: a building counts under the class it is counted as, a queued upgrade
// with check = 1 counts toward its product from the moment it is queued, for its own owner, uncounted on cancel, once).
// B: MIRAGE's computer player heals at fountains and SEAS wells (src/Sim/Ai/Fountains.cs; mirage_ai_fountains_v2:
// AiGoalUseFountains, the heal lock of FightingObj.HandleGamePlayCommand).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class LimitsFountainsTests
{
    static readonly GameplayOptions LIMITS = new() { BuildingLimit = true };

    static void Rich(Player p) { p.CapsFixed = true; foreach (var r in Rules.RES) { p.Caps[r] = 50000; p[r] = 40000; } p["skulls"] = 5000; }

    /// <summary>a MIRAGE world (players 1 and 2) with the option on for both (or off)</summary>
    static (W_ W, Player P, Player E) World(TestContext t, string tribe, string enemy = "Hu", bool limits = true, Rules? rules = null)
    {
        W_ w; Player p, e;
        if (rules == null) (w, p, e) = Sys2.World(t, Sys2.Mirage, tribe, enemy);
        else
        {
            var (_, M) = Sys2.Load(t, Sys2.Mirage);
            w = new W_(rules, M, new FlatMap(256), new WorldOptions { Seed = 7 });
            p = new Player(1, tribe, new PlayerOpts { Rules = rules, Team = 1 });
            e = new Player(2, enemy, new PlayerOpts { Rules = rules, Team = 2, Ai = true });
            w.Players.Add(p); w.Players.Add(e);
        }
        var o = limits ? LIMITS : null;
        foreach (var x in new[] { p, e }) { x.Gameplay = o; x.Tt = w.Data.PlayerTree(x.Tribe, o); Rich(x); w.RaiseEpoch(x, 5); }
        return (w, p, e);
    }
    static Building Place(TestContext t, W_ w, string name, Player p, double x, double z, bool built = true)
    {
        var b = w.PlaceBuilding(name, p, x, z, 0, built);
        if (b == null) t.Skip("no model for " + name);
        return b!;
    }
    static ActionDef Act(TestContext t, W_ w, Player p, string path)
    {
        var a = w.Data.Actions(p).FirstOrDefault(x => x.Path == path);
        if (a == null) t.Skip("no action " + path);
        return a!;
    }
    /// <summary>the action as a building offers it (its local tree: a farm's or a tower's mode)</summary>
    static ActionDef ActAt(TestContext t, W_ w, Entity e, string path)
    {
        var a = w.Data.ActionsOf(e.RulesOwner(), e.Name, "building", "BLDG").FirstOrDefault(x => x.Path == path) ?? w.Data.Actions(e.Owner).FirstOrDefault(x => x.Path == path);
        if (a == null) t.Skip("no action " + path);
        return a!;
    }

    /// <summary>MIRAGE's tree with a farm limit (the design's test: max_quantity 10 in aje_small_farm; and per type)</summary>
    static Rules FarmRules(TestContext t, string? perType = null)
    {
        var tt = TtNode.FromJson(Json.ParseFile(Path.Combine(Sys2.Mirage, "techtree.json")));
        var farm = tt["StartTT"]!["Objects"]!["Aje"]!["BLDG"]!["aje_small_farm"]!;
        farm["max_quantity"] = new TtNode("10");
        if (perType != null) farm["max_quantity_aje_medium_farm"] = new TtNode(perType);
        return new Rules(tt, GameData.Load(Path.Combine(Sys2.Mirage, "gamedata.json")));
    }

    // ================================================================================== A: building limits
    /// <summary>the data: MIRAGE has max_quantity (temples 5, towers 10 ...), the base game none; the tower upgrades carry
    /// check = 1 and product_name; the remake adds check = 1 to the three Aje farm conversions (the v3 data fix) - for
    /// MIRAGE only; the option is MIRAGE's default off, and the menus offer it only where the tree has limits</summary>
    public static void LimitsData(TestContext t)
    {
        var (w, p, _) = World(t, "Aje");
        t.Equal(10, w.MaxQuantity(p, "aje_medium_tower"), "aje_medium_tower: 10");
        t.Equal(5, w.MaxQuantity(p, "aje_temple"), "aje_temple: 5");
        t.Equal(-1, w.MaxQuantity(p, "aje_small_farm"), "aje_small_farm: no limit in MIRAGE's data");
        var mt = Act(t, w, p, "/Actions/Aje/Upgrades/aje_small_tower/medium_tower");
        t.True(mt.LimitCheck && mt.Product == "aje_medium_tower", "tower upgrade: check = 1, product aje_medium_tower");
        t.Equal("aje_medium_tower", W_.LimitProductOf(mt), "the tower upgrade counts toward aje_medium_tower");
        foreach (var f in new[] { "aje_small_farm", "aje_medium_farm", "aje_huge_farm" })
        {
            var fa = w.Data.Actions(p).FirstOrDefault(x => x.Path == "/Actions/Aje/Upgrades/aje_small_farm/" + f);
            t.True(fa != null && fa.LimitCheck, $"farm conversion {f}: check = 1 (the remake's data fix)");
            t.Equal(f, fa != null ? W_.LimitProductOf(fa) : null, $"farm conversion {f}: counts toward {f}");
        }
        t.Equal("aje_medium_farm", W_.LimitProductOf(Act(t, w, p, "/Actions/Aje/Build/BLDG/aje_medium_farm")), "the build action aje_medium_farm: placed as that type");
        t.True(GameplayOptions.HasBuildingLimits(w.Data.Base), "MIRAGE: the menus offer the option");
        t.True(!GameplayOptions.Default.BuildingLimit, "MIRAGE's default: off");
        var go = System.Text.Json.JsonSerializer.Deserialize<GameplayOptions>("{\"buildingLimit\":true}")!;
        t.True(go.BuildingLimit && !go.IsDefault, "the option in the game start's JSON (saved games)");

        var (wb, pb, _) = Sys2.World(t, Sys2.Base, "Aje");
        pb.Gameplay = LIMITS;
        t.True(!GameplayOptions.HasBuildingLimits(wb.Data.Base), "base game: no limits, no option");
        t.Equal(-1, wb.MaxQuantity(pb, "aje_medium_tower"), "base game: no max_quantity");
        var fb = wb.Data.Actions(pb).FirstOrDefault(x => x.Path == "/Actions/Aje/Upgrades/aje_small_farm/aje_medium_farm");
        t.True(fb == null || !fb.LimitCheck, "base game: the farm conversions keep their data");
    }

    /// <summary>placing: a site counts at once; the next one is refused ("limit") by StartConstruction and greyed in the
    /// worker's card (CanQueue: the HUD's Why) with the tooltip's n/max; a destroyed one gives its place back; without
    /// the option nothing is limited</summary>
    public static void PlacementLimit(TestContext t)
    {
        var (w, p, _) = World(t, "Hu");
        var a = Act(t, w, p, "/Actions/Hu/Build/BLDG/hu_temple");
        t.Equal(null, w.Data.Check(p, a), "the temple can be built (epoch 5)");
        t.Equal(5, w.MaxQuantity(p, "hu_temple"), "hu_temple: 5");
        var worker = w.SpawnUnit("hu_worker", p, -60, -60)!;
        var temples = new List<Building>();
        for (int i = 0; i < 4; i++) temples.Add(Place(t, w, "hu_temple", p, -80 + i * 40, 40));
        t.Equal(4, w.LimitCount(p, "hu_temple"), "four temples");
        t.Equal(null, w.CanQueue(p, a, worker), "the card is free at 4/5");
        var r = w.StartConstruction(p, a, 0, -40, 0, new List<Unit> { worker });
        t.True(r is Building, "the fifth: a site");
        t.Equal(5, w.LimitCount(p, "hu_temple"), "a site counts at once (CBuilding.OnInit)");
        t.Equal(W_.LIMIT, w.CanQueue(p, a, worker), "the card greyed at 5/5 (CheckBuildConditions)");
        double wood = p["wood"];
        t.Equal((object)W_.LIMIT, w.StartConstruction(p, a, 60, -40, 0, new List<Unit> { worker }), "the sixth refused (CanAddBuilding)");
        t.Equal(wood, p["wood"], "nothing paid for it");
        var cmd = new PlaceBuildingCommand { Player = p.Id, Action = a.Path, X = 60, Z = -40, Workers = new[] { worker.Id } };
        t.Equal(W_.LIMIT, cmd.Apply(w), "the place command too");
        var (cur, max) = w.LimitOf(p, a)!.Value;
        t.Equal((5, 5), (cur, max), "LimitOf: 5/5");
        var tip = Tooltips.ActionTip(w, p, a, w.CanQueue(p, a, worker), new List<Entity> { worker });
        t.True(tip.Lines.Any(l => l.Text.EndsWith("5/5") && l.Bad), "tooltip: the count n/max");
        t.True(tip.Lines.Any(l => l.Bad && (l.Text.Contains("maximum", StringComparison.OrdinalIgnoreCase))), "tooltip: maximum reached");
        t.Equal("You've reached the maximum quantity of this building class.", HudGame.Reason(W_.LIMIT), "the message");
        var (lk, dim) = HudModel.CardState(new HudAction { A = a }, W_.LIMIT);
        t.True(!lk && dim, "the card is dimmed, not locked");
        // one burns down: its place is free again
        w.Kill(temples[0], null);
        Sys2.Run(w, 0.2);
        t.Equal(4, w.LimitCount(p, "hu_temple"), "a destroyed temple stops counting");
        t.Equal(null, w.CanQueue(p, a, worker), "and the card is free again");
        // a captured one counts for its new owner at once (SetOwner)
        temples[1].Owner = w.Players.First(x => x != p);
        t.Equal(3, w.LimitCount(p, "hu_temple"), "a temple given away counts for the other player");
        // without the option: nothing limited
        var (w2, p2, _) = World(t, "Hu", limits: false);
        for (int i = 0; i < 6; i++) Place(t, w2, "hu_temple", p2, -100 + i * 40, 40);
        var a2 = Act(t, w2, p2, "/Actions/Hu/Build/BLDG/hu_temple");
        t.True(w2.LimitOf(p2, a2) == null && w2.LimitWhy(p2, a2) == null, "option off: no limit");
        var wk2 = w2.SpawnUnit("hu_worker", p2, -60, -60)!;
        t.True(w2.StartConstruction(p2, a2, 0, -60, 0, new List<Unit> { wk2 }) is Building, "option off: the seventh is placed");
    }

    /// <summary>tower upgrades (check = 1): a queued upgrade counts toward aje_medium_tower from the moment it is queued -
    /// for the owner of the tower, not another player (v3: m_iOwner) - another tower's upgrade and a new medium tower are
    /// refused then; cancelled, it is uncounted; done, the tower counts as a medium tower once (not twice) and no more as
    /// a small one</summary>
    public static void QueuedUpgradeCounts(TestContext t)
    {
        var (w, p, e) = World(t, "Aje", "Aje");
        var small = Place(t, w, "aje_small_tower", p, -100, 60);
        var small2 = Place(t, w, "aje_small_tower", p, -60, 60);
        for (int i = 0; i < 9; i++) Place(t, w, "aje_medium_tower", p, -100 + i * 25, -60);
        t.Equal(9, w.LimitCount(p, "aje_medium_tower"), "nine medium towers");
        t.Equal(2, w.LimitCount(p, "aje_small_tower"), "two small ones");
        var up = ActAt(t, w, small, "/Actions/Aje/Upgrades/aje_small_tower/medium_tower");
        t.Equal(null, w.CanQueue(p, up, small), "the upgrade can be queued at 9/10");
        t.Equal(null, w.QueueAction(small, up), "queued");
        t.Equal(10, w.LimitCount(p, "aje_medium_tower"), "the queued upgrade counts at once (CAction.Init: AddClass)");
        t.Equal(2, w.LimitCount(p, "aje_small_tower"), "the tower still counts as a small one while it waits");
        var up2 = ActAt(t, w, small2, "/Actions/Aje/Upgrades/aje_small_tower/medium_tower");
        t.Equal(W_.LIMIT, w.CanQueue(p, up2, small2), "the second tower's upgrade is refused at 10/10");
        var worker = w.SpawnUnit("aje_worker", p, 0, 0)!;
        var build = Act(t, w, p, "/Actions/Aje/Build/BLDG/aje_medium_tower");
        t.Equal(W_.LIMIT, w.CanQueue(p, build, worker), "the medium tower's build card greyed (GetQueuedUpgradeCount)");
        t.Equal((object)W_.LIMIT, w.StartConstruction(p, build, 100, 100, 0, new List<Unit> { worker }), "and placing one refused");
        // the other player is not touched (the owner of the upgrade, v3)
        var es = Place(t, w, "aje_small_tower", e, 100, 60);
        t.Equal(0, w.LimitCount(e, "aje_medium_tower"), "the other player has no medium tower");
        t.Equal(null, w.CanQueue(e, ActAt(t, w, es, "/Actions/Aje/Upgrades/aje_small_tower/medium_tower"), es), "the other player's upgrade is free");
        // cancelled: uncounted
        w.CancelQueue(small, 0);
        t.Equal(9, w.LimitCount(p, "aje_medium_tower"), "cancelled: uncounted (CAction.Break: RemoveClass)");
        t.Equal(null, w.CanQueue(p, up2, small2), "the other tower may again");
        // done: the tower counts as a medium tower, once
        t.Equal(null, w.QueueAction(small, up), "queued again");
        for (int i = 0; i < 2000 && small.Queue.Count > 0; i++) w.Step();
        t.Equal(0, small.Queue.Count, "the upgrade finished");
        t.Equal("aje_medium_tower", w.LimitClassOf(small), "the tower's limit class: its flag aje_medium_tower (GetLimitClass)");
        t.Equal(10, w.LimitCount(p, "aje_medium_tower"), "done: still ten (counted once - DeliverProduct)");
        t.Equal(1, w.LimitCount(p, "aje_small_tower"), "and one small tower less");
        // the counts are the world's own state: the same after more steps (nothing to save; a save replays the game)
        Sys2.Run(w, 2);
        t.Equal(10, w.LimitCount(p, "aje_medium_tower"), "stable");
    }

    /// <summary>the design's test (README, max_quantity 10 in aje_small_farm): 9 small farms + 1 medium; ordering the
    /// medium farm to convert to small greys the worker's small farm button at once (10/10), also while the conversion
    /// waits behind another job; cancelled, it works again. Per type: max_quantity_aje_medium_farm</summary>
    public static void FarmConversionCounts(TestContext t)
    {
        var (w, p, _) = World(t, "Aje", rules: FarmRules(t));
        var farms = new List<Building>();
        for (int i = 0; i < 10; i++) farms.Add(Place(t, w, "aje_small_farm", p, -110 + i * 24, 40));
        var med = farms[9];
        w.CompleteUpgrade(med, ActAt(t, w, med, "/Actions/Aje/Upgrades/aje_small_farm/aje_medium_farm"));
        t.Equal("aje_medium_farm", w.LimitClassOf(med), "a medium farm counts as aje_medium_farm (its object flag)");
        t.Equal(9, w.LimitCount(p, "aje_small_farm"), "nine small farms");
        t.Equal(1, w.LimitCount(p, "aje_medium_farm"), "one medium farm");
        var worker = w.SpawnUnit("aje_worker", p, 0, 0)!;
        var build = Act(t, w, p, "/Actions/Aje/Build/BLDG/aje_small_farm");
        t.Equal(null, w.CanQueue(p, build, worker), "the small farm button is free at 9/10");
        var toSmall = ActAt(t, w, med, "/Actions/Aje/Upgrades/aje_small_farm/aje_small_farm");
        t.True(toSmall.LimitCheck, "the conversion to small has check = 1");
        t.Equal(null, w.QueueAction(med, toSmall), "the medium farm converts to small");
        t.Equal(10, w.LimitCount(p, "aje_small_farm"), "counted at once");
        t.Equal(W_.LIMIT, w.CanQueue(p, build, worker), "the small farm button greys out at once");
        var tip = Tooltips.ActionTip(w, p, build, w.CanQueue(p, build, worker), new List<Entity> { worker });
        t.True(tip.Lines.Any(l => l.Text.EndsWith("10/10")), "tooltip: 10/10");
        t.Equal((object)W_.LIMIT, w.StartConstruction(p, build, 0, -80, 0, new List<Unit> { worker }), "a worker cannot place an eleventh");
        w.CancelQueue(med, med.Queue.Count - 1);
        t.Equal(null, w.CanQueue(p, build, worker), "cancelled: the button works again");
        // waiting in the queue behind another job: grey while it waits
        var job = w.Data.ActionsOf(med.RulesOwner(), med.Name, "building", "BLDG").FirstOrDefault(x => x.Kind == "Build" && x.Cat != "Build/BLDG" && w.CanQueue(p, x, med) == null);
        if (job != null)
        {
            t.Equal(null, w.QueueAction(med, job), "another job first");
            t.Equal(null, w.QueueAction(med, toSmall), "the conversion behind it");
            t.Equal(W_.LIMIT, w.CanQueue(p, build, worker), "grey while the conversion waits");
        }
        // per type: max_quantity_aje_medium_farm 1 - the medium type is full, the small one is not
        var (w2, p2, _) = World(t, "Aje", rules: FarmRules(t, perType: "1"));
        var f2 = Place(t, w2, "aje_small_farm", p2, 0, 40);
        var f3 = Place(t, w2, "aje_small_farm", p2, 40, 40);
        t.Equal(1, w2.MaxQuantity(p2, "aje_medium_farm"), "max_quantity_aje_medium_farm");
        t.Equal(10, w2.MaxQuantity(p2, "aje_small_farm"), "the small type: max_quantity");
        t.Equal(null, w2.QueueAction(f2, ActAt(t, w2, f2, "/Actions/Aje/Upgrades/aje_small_farm/aje_medium_farm")), "one farm to medium");
        t.Equal(W_.LIMIT, w2.CanQueue(p2, ActAt(t, w2, f3, "/Actions/Aje/Upgrades/aje_small_farm/aje_medium_farm"), f3), "a second medium farm is refused");
        var wk2 = w2.SpawnUnit("aje_worker", p2, 0, 0)!;
        t.Equal(W_.LIMIT, w2.CanQueue(p2, Act(t, w2, p2, "/Actions/Aje/Build/BLDG/aje_medium_farm"), wk2), "and placing one as a medium farm (GetPlacementLimitClass)");
        // a farm placed as a medium farm counts as one from its site on (SetPlacedLimitClass)
        var (w3, p3, _) = World(t, "Aje", rules: FarmRules(t, perType: "5"));
        var wk3 = w3.SpawnUnit("aje_worker", p3, 0, 0)!;
        var medBuild = Act(t, w3, p3, "/Actions/Aje/Build/BLDG/aje_medium_farm");
        // (the action is hidden in MIRAGE's tree - visibility 0 - so the site is put up as StartConstruction would)
        var site = w3.Data.Check(p3, medBuild) == null ? w3.StartConstruction(p3, medBuild, 30, 30, 0, new List<Unit> { wk3 }) as Building : null;
        if (site == null) { site = Place(t, w3, "aje_small_farm", p3, 30, 30, built: false); site.Action = medBuild; }
        t.Equal("aje_medium_farm", w3.LimitClassOf(site), "a site placed as a medium farm counts as one");
        t.Equal(1, w3.LimitCount(p3, "aje_medium_farm"), "one medium farm");
        t.Equal(0, w3.LimitCount(p3, "aje_small_farm"), "not a small one");
        w3.FinishBuilding(site);
        t.Equal("aje_medium_farm", w3.LimitClassOf(site), "finished (resultactions: the medium mode): still a medium farm");
    }

    /// <summary>the computer player keeps to the limits: an entry of its build list at the limit is done (Missing 0, no
    /// place among the requests), below it only what the limit leaves room for; its hands' site is refused; its best
    /// tower is one it may still build</summary>
    public static void AiRespectsLimits(TestContext t)
    {
        var (w, p, _) = World(t, "Hu");
        var ai = new TribeAI(w, p, AiData.Load(Sys2.Mirage), new TribeAIOptions { Difficulty = 4, Behaviour = "Dodo", Multimap = true });
        Place(t, w, "hu_fireplace", p, -100, -100);
        for (int i = 0; i < 3; i++) Place(t, w, "hu_temple", p, -80 + i * 40, 40);
        var R0 = ai.Resolve("BLDG/hu_temple", "");
        t.True(R0 != null && R0.Kind == "bldg", "the request resolves");
        var e = new BuildEntry { Request = "BLDG/hu_temple", Count = 8, Unique = true, R = R0! };
        t.Equal(2, ai.Missing(ai.Census(true), e, R0!), "8 wanted, 3 there, limit 5: two more");
        Place(t, w, "hu_temple", p, 80, 40); Place(t, w, "hu_temple", p, 120, 40);
        t.Equal(0, ai.Missing(ai.Census(true), e, R0!), "at the limit: nothing more");
        var ne = new BuildEntry { Request = "BLDG/hu_temple", Count = 1, Unique = false, R = R0! };
        t.Equal(0, ai.Missing(ai.Census(true), ne, R0!), "a repeated entry too");
        var worker = w.SpawnUnit("hu_worker", p, 0, 0)!;
        t.Equal((object)W_.LIMIT, ai.Hands.StartConstruction(p, R0!.Action, 0, -60, 0, new List<Unit> { worker }), "its hands' site is refused");
        t.Equal(W_.LIMIT, ai.BuildCheck(p, R0.Action), "BuildCheck: limit");
        // the best tower: one below its limit
        for (int i = 0; i < 10; i++) Place(t, w, "hu_large_tower", p, -110 + i * 22, -40);
        var la = Act(t, w, p, "/Actions/Hu/Build/BLDG/hu_large_tower");
        t.Equal(W_.LIMIT, ai.BuildCheck(p, la), "ten large towers: the limit");
        t.True(w.Data.Actions(p).Where(a => a.Cat == "Build/BLDG" && w.Data.Def(a.Results[0].Obj, p)?.WallKind == "tower" && ai.BuildCheck(p, a) == null).All(a => a.Results[0].Obj != "hu_large_tower"),
            "no tower at its limit is a choice");
    }

    // ================================================================================== B: AI heals at fountains
    static (W_ W, Player P, Player E, TribeAI Ai) AiWorld(TestContext t, string tribe = "Hu", string data = "")
    {
        var (w, p, e) = Sys2.World(t, data == "" ? Sys2.Mirage : data, tribe, "Hu", size: 512);
        Rich(p);
        var ai = new TribeAI(w, p, AiData.Load(data == "" ? Sys2.Mirage : data), new TribeAIOptions { Difficulty = 4, Behaviour = "Dodo", Multimap = true });
        Place(t, w, p.Tribe == "SEAS" ? "seas_headquarters" : "hu_fireplace", p, -100, -100);
        return (w, p, e, ai);
    }
    static Well PlaceWell(TestContext t, W_ w, double x, double z)
    {
        var well = w.PlaceWell("healing_well_01", "healing_well_01", x, z, null, 0, null, null);
        if (well == null) t.Skip("no model healing_well_01");
        return well!;
    }
    static Unit Hurt(W_ w, Player p, string cls, double x, double z, double f)
    {
        var u = w.SpawnUnit(cls, p, x, z)!;
        u.Hp = Math.Floor(u.MaxHp * f);
        return u;
    }

    /// <summary>only with MIRAGE's data (the base game's AI has no such goal)</summary>
    public static void FountainsMirageOnly(TestContext t)
    {
        var (_, _, _, ai) = AiWorld(t);
        t.True(ai.UsesFountains, "MIRAGE: the goal runs");
        var (_, _, _, aib) = AiWorld(t, data: Sys2.Base);
        t.True(!aib.UsesFountains, "the base game: no goal");
    }

    /// <summary>a unit below 60 % goes to the nearest usable fountain within 200 (fighting or not), heals and walks back to
    /// where it stood; a unit at 60 % or more, one too far away, one in a fountain with less than 30 left are not sent;
    /// six per think at most</summary>
    public static void FountainsSendAndReturn(TestContext t)
    {
        var (w, p, e, ai) = AiWorld(t);
        var well = PlaceWell(t, w, 60, 0);
        var near = PlaceWell(t, w, 40, 0);
        var hurt = Hurt(w, p, "hu_warrior", 0, 0, 0.5);
        var fine = Hurt(w, p, "hu_warrior", 4, 0, 0.6);
        var far = Hurt(w, p, "hu_warrior", -190, 0, 0.3);           // 230 m from the nearest
        var foe = Hurt(w, e, "hu_warrior", 0, 10, 0.3);
        // fighting: the hurt unit attacks the enemy - it goes anyway (v2)
        w.Order(hurt, new Order("attack") { Target = foe });
        ai.FountainsThink(ai.Census(true));
        t.Equal("fountain", hurt.Task.Type, "a unit below 60 % goes, fight or not");
        t.True(hurt.Task.Target == near, "to the nearest");
        t.True(hurt.AiHealLock, "locked to the walk (Action_AILock)");
        t.True(fine.Task.Type != "fountain", "60 %: stays");
        t.True(far.Task.Type != "fountain", "farther than 200: stays");
        t.True(foe.Task.Type != "fountain", "another player's unit: not this brain's");
        for (int i = 0; i < 2000 && hurt.Task.Type == "fountain"; i++) w.Step();
        t.Equal(hurt.MaxHp, hurt.Hp, "healed");
        t.Equal("move", hurt.Task.Type, "then walks back (Q_Action /Walk)");
        t.True(!w.AiHealLocked(hurt), "the lock ended with the heal");
        for (int i = 0; i < 2000 && hurt.Task.Type == "move"; i++) w.Step();
        t.True(Math.Sqrt(hurt.Pos.X * hurt.Pos.X + hurt.Pos.Z * hurt.Pos.Z) < 6, "back where it stood");
        // an empty fountain is no place
        var (w2, p2, _, ai2) = AiWorld(t);
        var dry = PlaceWell(t, w2, 40, 0); dry.Fill = 29;
        var u2 = Hurt(w2, p2, "hu_warrior", 0, 0, 0.3);
        ai2.FountainsThink(ai2.Census(true));
        t.True(u2.Task.Type != "fountain", "a fountain with less than 30 left: not used");
        dry.Fill = 30;
        ai2.FountainsThink(ai2.Census(true));
        t.Equal("fountain", u2.Task.Type, "30 left: used");
        // six per think
        var (w3, p3, _, ai3) = AiWorld(t);
        PlaceWell(t, w3, 40, 0);
        var many = Enumerable.Range(0, 9).Select(i => Hurt(w3, p3, "hu_warrior", -20 + i * 3, 20, 0.3)).ToList();
        ai3.FountainsThink(ai3.Census(true));
        t.Equal(6, many.Count(u => u.Task.Type == "fountain"), "six units per think");
        ai3.FountainsThink(ai3.Census(true));
        t.Equal(9, many.Count(u => u.Task.Type == "fountain"), "the rest at the next think");
    }

    /// <summary>skipped: units already on the way, trapped / asleep / frozen, in a transport, flying, locked for scripts
    /// (AILU); a unit sent once is not sent again for ten thinks; a sleeping brain (Mikrobe) sends nobody</summary>
    public static void FountainsSkips(TestContext t)
    {
        var (w, p, _, ai) = AiWorld(t);
        PlaceWell(t, w, 40, 0);
        var asleep = Hurt(w, p, "hu_warrior", 0, 0, 0.3); w.Trap(asleep, 10, "asleep");
        var iced = Hurt(w, p, "hu_warrior", 0, 6, 0.3); w.Trap(iced, 10, "iced");
        var locked = Hurt(w, p, "hu_warrior", 0, 12, 0.3); ai.Locked.Add(locked);
        var flyer = Hurt(w, p, "hu_warrior", 0, 18, 0.3); flyer.Flying = true;
        ai.FountainsThink(ai.Census(true));
        t.True(asleep.Task.Type != "fountain" && iced.Task.Type != "fountain", "asleep / frozen: not sent");
        t.True(locked.Task.Type != "fountain", "locked for scripts: not sent");
        t.True(flyer.Task.Type != "fountain", "flying: not sent");
        // sent once, then stopped by its owner's other means (a trap): not sent again for ten thinks
        var u = Hurt(w, p, "hu_warrior", 0, -10, 0.3);
        ai.FountainsThink(ai.Census(true));
        t.Equal("fountain", u.Task.Type, "sent");
        u.Task = UnitTask.Idle();
        for (int i = 0; i < 9; i++) { ai.FountainsThink(ai.Census(true)); t.True(u.Task.Type != "fountain", $"not again at think {i + 2}"); }
        ai.FountainsThink(ai.Census(true));
        t.Equal("fountain", u.Task.Type, "again after ten thinks (RETRY_THINKS)");
        // Mikrobe
        var (w2, p2, _, _) = AiWorld(t);
        var sleeper = new TribeAI(w2, p2, AiData.Load(Sys2.Mirage), new TribeAIOptions { Difficulty = 4, Behaviour = "Mikrobe", Multimap = true });
        PlaceWell(t, w2, 40, 0);
        var u2 = Hurt(w2, p2, "hu_warrior", 0, 0, 0.3);
        sleeper.FountainsThink(sleeper.Census(true));
        t.True(u2.Task.Type != "fountain", "Mikrobe: nobody sent");
        // the goal's own timer: every 40 ticks (8 s) in the brain's update
        var (w3, p3, _, ai3) = AiWorld(t);
        PlaceWell(t, w3, 40, 0);
        var u3 = Hurt(w3, p3, "hu_warrior", 0, 0, 0.3);
        for (int i = 0; i < (int)(17 / W_.STEP) && u3.Task.Type != "fountain"; i++) { w3.Step(); ai3.Update(W_.STEP); }
        t.Equal("fountain", u3.Task.Type, "the brain sends it within two think waits");
    }

    /// <summary>the lock: the brain's other orders and special moves don't turn the unit around (AiOrderCommand,
    /// CommandHands.UseMove), the human player's orders do (his units are never locked anyway); trapped / asleep / frozen
    /// on the way, the lock ends for good; the place gone: the walk ends, the lock with it</summary>
    public static void FountainsHealLock(TestContext t)
    {
        var (w, p, e, ai) = AiWorld(t);
        var well = PlaceWell(t, w, 80, 0);
        var u = Hurt(w, p, "hu_warrior", 0, 0, 0.3);
        ai.FountainsThink(ai.Census(true));
        t.Equal("fountain", u.Task.Type, "on its way");
        ai.Hands.Order(p, new[] { u }, new Order("move") { X = -50, Z = -50 });
        t.Equal("fountain", u.Task.Type, "the brain's move order is ignored");
        ai.Hands.Order(p, new[] { u }, new Order("attackmove") { X = -50, Z = -50, Auto = true });
        t.Equal("fountain", u.Task.Type, "and its attack walk");
        var mv = w.MovesOf(u).FirstOrDefault(a => a.Id != "Kill");
        if (mv != null) t.Equal("locked", ai.Hands.UseMove(p, u, mv), "a special move of the brain is refused");
        // the human player's order (OrderCommand) is never held
        var human = new OrderCommand { Player = p.Id, Units = new[] { u.Id }, Type = "stop" };
        human.Apply(w);
        t.True(u.Task.Type != "fountain", "a player's own order turns it around");
        t.True(!w.AiHealLocked(u), "and the lock is over");
        // trapped on the way: free for the brain's orders again, for good
        ai = new TribeAI(w, p, AiData.Load(Sys2.Mirage), new TribeAIOptions { Difficulty = 4, Behaviour = "Dodo", Multimap = true });
        ai.FountainsThink(ai.Census(true));
        t.Equal("fountain", u.Task.Type, "sent again by a new think");
        w.Trap(u, 2, "iced");
        t.True(!w.AiHealLocked(u), "frozen: not locked");
        ai.Hands.Order(p, new[] { u }, new Order("move") { X = -50, Z = -50 });
        t.Equal("move", u.Task.Type, "the brain's order goes through");
        Sys2.Run(w, 2.5);
        t.True(!u.AiHealLock, "the lock stays over after the ice");
        // the place gone on the way: the walk ends (and the walk back follows)
        var (w2, p2, _, ai2) = AiWorld(t);
        var well2 = PlaceWell(t, w2, 80, 0);
        var u2 = Hurt(w2, p2, "hu_warrior", 0, 0, 0.3);
        ai2.FountainsThink(ai2.Census(true));
        t.Equal("fountain", u2.Task.Type, "on its way");
        well2.Fill = 0; well2.Prop.Visible = false;
        w2.Step();
        t.True(u2.Task.Type != "fountain" && !w2.AiHealLocked(u2), "the fountain gone: walk and lock over");
    }

    /// <summary>a SEAS AI uses its own finished SEAS well (and an ally's), not an enemy's nor a site</summary>
    public static void FountainsSeasWell(TestContext t)
    {
        var (w, p, e, ai) = AiWorld(t, "SEAS");
        var mine = Place(t, w, "seas_outpouring", p, 60, 0);
        var theirs = Place(t, w, "seas_outpouring", e, -50, 0);
        var u = Hurt(w, p, "seas_worker", -30, 0, 0.3);
        ai.FountainsThink(ai.Census(true));
        t.Equal("fountain", u.Task.Type, "sent");
        t.True(u.Task.Target == mine, "to its own well, not the nearer enemy's");
        for (int i = 0; i < 2000 && u.Task.Type == "fountain"; i++) w.Step();
        t.Equal(u.MaxHp, u.Hp, "healed at the SEAS well");
        var (w2, p2, _, ai2) = AiWorld(t, "SEAS");
        Place(t, w2, "seas_outpouring", p2, 60, 0, built: false);
        var u2 = Hurt(w2, p2, "seas_worker", 0, 0, 0.3);
        ai2.FountainsThink(ai2.Census(true));
        t.True(u2.Task.Type != "fountain", "a site is no place");
    }
}
