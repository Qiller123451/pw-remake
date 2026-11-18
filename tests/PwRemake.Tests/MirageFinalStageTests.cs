// MIRAGE's sixth stage (final_stage) and what it unlocks: the SEAS great cannon and the Scorpion (its launch pad
// seas_final_boss turning into seas_scorpion), the other tribes' titans; the game options epoch six / no titans
// (src/Sim/Rules/GameplayOptions.cs, src/Sim/Systems/MirageBuildings.cs; player report: "the HUGE cannon is missing
// from the SEAS arsenal, same as the scorpion").
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class MirageFinalStageTests
{
    static readonly (string Tribe, string Base)[] TRIBES = { ("SEAS", "seas_headquarters"), ("Hu", "hu_fireplace"), ("Aje", "aje_resource_collector"), ("Ninigi", "ninigi_fireplace") };

    static void Rich(Player p) { p.CapsFixed = true; foreach (var r in Rules.RES) { p.Caps[r] = 20000; p[r] = 10000; } p["skulls"] = 2000; }

    /// <summary>a MIRAGE world whose player 1 has the given options (null: the defaults)</summary>
    static (W_ W, Player P) World(TestContext t, string tribe, GameplayOptions? o = null)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Mirage, tribe);
        if (o != null) { p.Gameplay = o; p.Tt = w.Data.PlayerTree(tribe, o); }
        return (w, p);
    }
    static ActionDef Act(W_ w, Player p, string path) => w.Data.Actions(p).FirstOrDefault(a => a.Path == path) ?? throw new Exception("no action " + path);
    static void FinalStage(W_ w, Player p) { w.RaiseEpoch(p, 5); w.CompleteUpgrade(null, Act(w, p, FinalPath(p.Tribe)), p); }
    static string FinalPath(string tribe) => $"/Actions/{tribe}/Upgrades/{TRIBES.First(x => x.Tribe == tribe).Base}/final_stage";

    /// <summary>final_stage: visibility -1, + age_5, + the option's xenage/epoch_six_enabled (on by default, as MIRAGE's
    /// MaxEpoch 6) - offered at epoch 5, not before, not without the option, not under the debug option "highest epoch 5"</summary>
    public static void FinalStageOfferedEveryTribe(TestContext t)
    {
        foreach (var (tribe, _) in TRIBES)
        {
            var (w, p) = World(t, tribe);
            var fs = Act(w, p, FinalPath(tribe));
            t.Equal("hidden", w.Data.Check(p, fs), $"{tribe}: not before epoch 5");
            w.RaiseEpoch(p, 5);
            fs = Act(w, p, FinalPath(tribe));
            p.Debug = true;             // (the buildings it needs, e.g. the Dragon Clan's teahouse and minefield, not asked for)
            t.Equal(null, w.Data.Check(p, fs), $"{tribe}: final_stage offered at epoch 5");
            p.MaxEpoch = 5;
            t.Equal("hidden", w.Data.Check(p, fs), $"{tribe}: hidden with the highest epoch 5");
            p.MaxEpoch = 0; p.Debug = false;

            var (w2, p2) = World(t, tribe, new GameplayOptions { EpochSix = false });
            w2.RaiseEpoch(p2, 5);
            t.Equal("hidden", w2.Data.Check(p2, Act(w2, p2, FinalPath(tribe))), $"{tribe}: hidden without the sixth stage option");
        }
        // the base game has no such filters: its trees are as before
        var (wb, pb, _) = Sys2.World(t, Sys2.Base, "Hu");
        t.Equal(0, pb.Tt!.OwnEnabled.Count, "base game: no option filters");
    }

    /// <summary>the SEAS: the great cannon (worker, Defence 8, 1500 wood 1000 stone) and the Scorpion's launch pad
    /// seas_final_boss (worker, 3000 food 2000 wood 1000 stone, level 5) come with final_stage; the garage's
    /// VHCL/seas_scorpion stays hidden as in the original (-1, final_stage adds -2)</summary>
    public static void SeasFinalStageUnlocksCannonAndScorpion(TestContext t)
    {
        var (w, p) = World(t, "SEAS");
        w.RaiseEpoch(p, 5);
        const string CANNON = "/Actions/SEAS/Build/BLDG/seas_great_cannon", PAD = "/Actions/SEAS/Build/BLDG/seas_final_boss", VHCL = "/Actions/SEAS/Build/VHCL/seas_scorpion";
        t.Equal("hidden", w.Data.Check(p, Act(w, p, CANNON)), "no cannon before the final stage");
        t.Equal("hidden", w.Data.Check(p, Act(w, p, PAD)), "no Scorpion before the final stage");
        w.CompleteUpgrade(null, Act(w, p, FinalPath("SEAS")), p);
        t.True(w.Data.Invented(p, "final_stage", "SEAS"), "final_stage invented");
        var cannon = Act(w, p, CANNON); var pad = Act(w, p, PAD);
        t.Equal(null, w.Data.Check(p, cannon), "great cannon buildable");
        t.Equal(null, w.Data.Check(p, pad), "Scorpion launch pad buildable");
        t.Equal("seas_worker", cannon.Locs[0].At, "the worker builds the cannon");
        t.Equal("DEFE", cannon.Locs[0].UiStr("subcat"), "in the defence row");
        t.Equal(8.0, double.Parse(cannon.Locs[0].UiStr("menupos")!), "at menupos 8");
        t.Equal(1500.0, cannon.Cost.Wood, "cannon wood"); t.Equal(1000.0, cannon.Cost.Stone, "cannon stone");
        t.Equal("seas_worker", pad.Locs[0].At, "the worker builds the launch pad");
        t.Equal(3000.0, pad.Cost.Food, "pad food"); t.Equal(2000.0, pad.Cost.Wood, "pad wood"); t.Equal(1000.0, pad.Cost.Stone, "pad stone");
        t.Equal(5, w.Data.StartLevel(pad), "the Scorpion comes at level 5");
        t.Equal("hidden", w.Data.Check(p, Act(w, p, VHCL)), "the garage's seas_scorpion action stays hidden (as in the original)");
        var again = w.Data.Actions(p).FirstOrDefault(a => a.Path == FinalPath("SEAS"));
        t.True(again == null || w.Data.Check(p, again) != null, "final_stage only once");
    }

    /// <summary>the option "no titans" (xenage/titans_disabled): the Scorpion's pad, the CBR titan and the garage action
    /// hidden and disabled; the great cannon (no titan) stays</summary>
    public static void TitansDisabledHidesScorpion(TestContext t)
    {
        var (w, p) = World(t, "SEAS", new GameplayOptions { RemoveTitans = true });
        FinalStage(w, p);
        var pad = Act(w, p, "/Actions/SEAS/Build/BLDG/seas_final_boss");
        t.True(!pad.Visible && pad.Disabled, "Scorpion launch pad hidden and disabled");
        t.Equal("hidden", w.Data.Check(p, Act(w, p, "/Actions/SEAS/Build/ANML/seas_rex")), "no CBR titan");
        t.Equal(null, w.Data.Check(p, Act(w, p, "/Actions/SEAS/Build/BLDG/seas_great_cannon")), "the great cannon stays");
        t.Equal(0.0, w.Data.Tree(p).Num("/Objects/SEAS/VHCL/seas_scorpion/ai_cap", -1), "the computer player wants none");
        // the other tribes' titans too
        foreach (var (tribe, path) in new[] { ("Hu", "/Actions/Hu/Build/BLDG/hu_launchpad_colossi"), ("Ninigi", "/Actions/Ninigi/Build/ANML/ninigi_kong") })
        {
            var (w2, p2) = World(t, tribe, new GameplayOptions { RemoveTitans = true });
            FinalStage(w2, p2);
            t.Equal("hidden", w2.Data.Check(p2, Act(w2, p2, path)), $"{tribe}: {path} hidden with no titans");
        }
    }

    /// <summary>the other tribes' final stage: the Norsemen's colossus launch pad, the Dragon Clan's pirate boss launch pad
    /// and King Kong, the Dustriders' catapult rex (at a farm in huge-farm mode: its local filter adds the last 1)</summary>
    public static void OtherTribesFinalStageUnlocks(TestContext t)
    {
        foreach (var (tribe, paths) in new[]
        {
            ("Hu", new[] { "/Actions/Hu/Build/BLDG/hu_launchpad_colossi" }),
            ("Ninigi", new[] { "/Actions/Ninigi/Build/BLDG/ninigi_launchpad_pirate_boss", "/Actions/Ninigi/Build/ANML/ninigi_kong" }),
        })
        {
            var (w, p) = World(t, tribe);
            w.RaiseEpoch(p, 5);
            foreach (var path in paths) t.Equal("hidden", w.Data.Check(p, Act(w, p, path)), $"{tribe}: {path} hidden before");
            w.CompleteUpgrade(null, Act(w, p, FinalPath(tribe)), p);
            foreach (var path in paths) t.Equal(null, w.Data.Check(p, Act(w, p, path)), $"{tribe}: {path} after the final stage");
        }
        {
            var (w, p) = World(t, "Aje");
            FinalStage(w, p);
            var farm = w.PlaceBuilding("aje_small_farm", p, 0, 0)!;
            const string REX = "/Actions/Aje/Build/ANML/aje_catapult_rex";
            t.True(!w.Data.ActionsAt(farm.RulesOwner(), "aje_small_farm").Any(a => a.Path == REX && a.Visible), "Aje: no catapult rex at a small farm");
            w.LocalFilter(farm, "Aje/Upgrades/aje_small_farm/aje_huge_farm", true);
            var rex = w.Data.ActionsAt(farm.RulesOwner(), "aje_small_farm").FirstOrDefault(a => a.Path == REX);
            t.True(rex != null && w.Data.Check(farm.RulesOwner(), rex, farm) == null, "Aje: catapult rex at a huge farm after the final stage");
        }
    }

    /// <summary>built by a worker: the great cannon is a building with its weapon; the launch pad is no longer selectable
    /// or hitable, plays "stand_up" after 1 s and becomes a level 5 seas_scorpion at its place, leaving no ruin; the
    /// pad's card in the pyramid becomes the Scorpion's</summary>
    public static void ScorpionLaunchpadBecomesScorpion(TestContext t)
    {
        var (w, p) = World(t, "SEAS");
        Rich(p);
        FinalStage(w, p);
        var worker = w.SpawnUnit("seas_worker", p, -20, 0)!;
        var cannonAct = Act(w, p, "/Actions/SEAS/Build/BLDG/seas_great_cannon");
        var cannon = w.StartConstruction(p, cannonAct, 60, 60, 0, new List<Unit> { worker }) as Building;
        t.True(cannon != null, "great cannon placed");
        w.FinishBuilding(cannon!);
        t.True(cannon!.Weapons.Long?.Projectile != null, "the great cannon shoots as a tower");
        p.Debug = true;
        var padAct = Act(w, p, "/Actions/SEAS/Build/BLDG/seas_final_boss");
        var pad = w.StartConstruction(p, padAct, 0, 0, 1.0, new List<Unit>()) as Building;
        p.Debug = false;
        t.True(pad != null && pad.Built, "launch pad built (debug: at once)");
        t.True(pad!.Untargetable && pad.Unselectable, "the finished pad can't be hit or selected");
        int before = p.Units;
        double stand = pad.Anim != null && pad.Anim.Has("stand_up") ? pad.Anim.Duration("stand_up") : 0;
        Sys2.Run(w, 0.9);
        t.True(pad.Alive && !w.Units.Any(u => u.Name == "seas_scorpion"), "not before 1 s");
        Sys2.Run(w, 0.2 + stand + 0.1);
        var sc = w.Units.FirstOrDefault(u => u.Alive && u.Name == "seas_scorpion");
        t.True(sc != null, $"the Scorpion is out after 1 s + stand_up ({stand:0.0} s)");
        t.True(!pad.Alive && pad.Ruin == null, "the pad is gone without a ruin");
        t.Equal(5, sc!.Level, "level 5");
        t.True(sc.Owner == p && Math.Abs(sc.Pos.X - pad.Pos.X) < 6 && Math.Abs(sc.Pos.Z - pad.Pos.Z) < 6, "at the pad's place");
        // (the pad counts in the unit limit from its site on - DoesCountInUnitLimit - and the Scorpion takes its card)
        t.Equal(before, p.Units, "the Scorpion takes the pad's card in the pyramid");
        // the colossus' pad turns at once
        var (wh, ph) = World(t, "Hu");
        Rich(ph); FinalStage(wh, ph); ph.Debug = true;
        var hp = wh.StartConstruction(ph, Act(wh, ph, "/Actions/Hu/Build/BLDG/hu_launchpad_colossi"), 0, 0, 0, new List<Unit>()) as Building;
        t.True(hp != null, "colossus launch pad placed");
        Sys2.Run(wh, 0.2);
        var col = wh.Units.FirstOrDefault(u => u.Alive && u.Name == "hu_colossi");
        t.True(col != null && !hp!.Alive, "the colossus replaced its pad");
        t.Equal(wh.Data.StartLevel(Act(wh, ph, "/Actions/Hu/Build/BLDG/hu_launchpad_colossi")), col!.Level, "at the pad action's level");
    }
}
