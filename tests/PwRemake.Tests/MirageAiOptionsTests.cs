// The computer player's missing parts (2026-11-20, docs/AI.md "The cheat manager's free units" and "MIRAGE: the AI
// follows the game options"):
//   CheatSpawnSkirmish9   CAiCheatMgr.SpawnUnit (AiImprovementMgr.usl:125-296): the missing units of a squad come for
//                         free only on a skirmish map at difficulty 9 (MIRAGE: not for a CampaignAI player); no trade
//                         units, no fishing boats, workers below 15, a finished producing building.
//   OptionAtoms           bEpochSix / m_bFinalStage of the build list (BV:406-409) follow the game options.
//   FlyersAndTitans       SpecialAllowed (MOD:1071, the intended FlyingEnabled test) and UpdateSolutionWithSpecUnits'
//                         flyers (GA:642-686) follow FlyingEnabled / RemoveTitans.
//   CheatWeaponDuration   GetAICheatModifier (FO:9747) applies to buildings (towers, FO:9654) and the carriers of
//                         build-up weapons (BuildUpBase.usl:1136) as to units: World.AttackDuration.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Game;
using PwRemake.Sim.Rules;

namespace PwRemake.Tests;

[TestClass]
public static class MirageAiOptionsTests
{
    const string MAP = "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer/multi_2_jun_001.ula";

    static (SkirmishSession Ss, TribeAI Hu) Game(TestContext t, string cfgId, double level, bool flying = false, bool noTitans = false)
    {
        var c = CampaignTests.LoadCfgPublic(cfgId);
        if (c == null) t.Skip("no configuration " + cfgId);
        if (!File.Exists(MAP)) t.Skip("map not staged");
        var setup = new SkirmishSetup { Seed = 1, Points = -1, ChooseHQ = true, FlyingEnabled = flying, RemoveTitans = noTitans, Slots = {
            new SkirmishSlot { Tribe = "Hu", Type = "ai", Level = level, Behaviour = "Giraffe", Team = 1, Hq = 0 },
            new SkirmishSlot { Tribe = "Aje", Type = "ai", Level = level, Behaviour = "Giraffe", Team = 2, Hq = 1 } } };
        var f = c!.Config.GameFile("Scripts/Ai/Modules/AiModuleControlDefault.usl");
        var ss = SkirmishSession.Create(setup, MAP, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, f != null ? File.ReadAllText(f) : null);
        return (ss, ss.Ai.Brains.Values.First(b => b.P.Tribe == "Hu"));
    }

    public static void CheatSpawnSkirmish9(TestContext t)
    {
        foreach (var cfg in new[] { "base", "mirage" })
        {
            var (ss, hu) = Game(t, cfg, 9);
            ss.Run(2);
            var S = hu.Census(true);
            t.True(hu.CheatSpawnAllowed, cfg + ": skirmish, difficulty 9: the cheat manager spawns");
            int n0 = hu.P.AtLevel[0];
            var w = hu.CheatSpawn(S, hu.P.Tribe == "Hu" ? "hu_worker" : "aje_worker", null, 0, S.Home);
            t.True(w != null && w.Alive && w.Owner == hu.P, cfg + ": a worker appears at the headquarters");
            t.Equal(n0 + 1, hu.P.AtLevel[0], cfg + ": counted in the pyramid's first row");
            t.True(hu.CheatSpawn(S, "hu_cart", null, 0, S.Home) == null, cfg + ": no trade units");
            t.True(hu.CheatSpawn(S, "hu_fishing_boat", null, 0, S.Home) == null, cfg + ": no fishing boats");
            t.True(hu.CheatSpawn(S, "hu_mammoth", null, 0, S.Home) == null, cfg + ": no unit without its finished producing building");
            hu.Custommap = true;
            t.True(!hu.CheatSpawnAllowed && hu.CheatSpawn(S, "hu_worker", null, 0, S.Home) == null, cfg + ": not for a CampaignAI player");
            hu.Custommap = false;
            var (ss4, hu4) = Game(t, cfg, 8);
            ss4.Run(2);
            var S4 = hu4.Census(true);
            t.True(!hu4.CheatSpawnAllowed && hu4.CheatSpawn(S4, "hu_worker", null, 0, S4.Home) == null, cfg + ": difficulty 8: nothing");
        }
    }

    public static void OptionAtoms(TestContext t)
    {
        var ctx = new AiCtx { EpochSix = true, FinalStage = false };
        t.True(AiData.When(new List<string> { "bEpochSix", "!m_bFinalStage" }, ctx), "epoch six on, not yet invented: final_stage is asked for");
        t.True(!AiData.When(new List<string> { "m_bFinalStage" }, ctx), "the sixth stage's buildings wait for it");
        ctx.FinalStage = true;
        t.True(AiData.When(new List<string> { "m_bFinalStage" }, ctx) && !AiData.When(new List<string> { "!m_bFinalStage" }, ctx), "invented: its buildings");
        ctx.EpochSix = false; ctx.FinalStage = false;
        t.True(!AiData.When(new List<string> { "bEpochSix" }, ctx), "epoch six off: no final_stage request");
        t.True(!AiData.When(new List<string> { "bFreeSpec" }, ctx) && !AiData.When(new List<string> { "bSupply" }, ctx), "options the remake does not have stay off");
        var (ss, hu) = Game(t, "mirage", 4);
        t.True(hu.OptEpochSix && hu.Ctx().EpochSix && !hu.Ctx().FinalStage, "MIRAGE's default MaxEpoch 6 reaches the AI");
        var (sb, hb) = Game(t, "base", 4);
        t.True(!hb.OptEpochSix && !hb.Ctx().EpochSix, "the base game: no CMirageAIMgr");
    }

    public static void FlyersAndTitans(TestContext t)
    {
        var lords = new HashSet<string>(StringComparer.Ordinal);
        var (_, off) = Game(t, "mirage", 4);
        t.True(!off.OptFlying && !off.SpecialAllowed("flyers_enabled", lords), "flying off: no flyer rows");
        lords.Add("flyers_enabled"); lords.Add("titans_enabled");    // (as if FreeSpecials let them past the hero test)
        t.True(!off.SpecialAllowed("flyers_enabled", lords), "flying off: refused before the hero test (MIRAGE checks RemoveTitans here)");
        t.True(off.SpecialAllowed("titans_enabled", lords), "titans on: the hero test decides");
        var (_, on) = Game(t, "mirage", 4, flying: true, noTitans: true);
        t.True(on.SpecialAllowed("flyers_enabled", lords), "flying on, titans removed: flyer rows stay (MIRAGE's bug dropped them)");
        t.True(!on.SpecialAllowed("titans_enabled", lords), "titans removed: no titan rows");
        lords.Clear();
        t.True(!on.SpecialAllowed("flyers_enabled", lords), "without FreeSpecials the hero test still refuses the value");
        var army = new List<ArmyRow> { new() { Min = 3, Max = 5, Alternatives = { new ArmyAlt { Cls = "hu_warrior" } } } };
        t.Equal(1, on.SpecUnits(new Census { Age = 2 }, army).Count, "epoch 2: no flyers");
        var withF = on.SpecUnits(new Census { Age = 3 }, army);
        t.True(withF.Count == 2 && withF[1].Alternatives[0].Cls == "hu_pteranodon" && withF[1].Min == 2, "flying on, epoch 3, difficulty 4: 2 pteranodons join the squad");
        t.Equal(1, off.SpecUnits(new Census { Age = 3 }, army).Count, "flying off: none");
        var (_, low) = Game(t, "mirage", 3, flying: true);
        t.Equal(1, low.SpecUnits(new Census { Age = 3 }, army).Count, "difficulty 3: none (GetUnits: GetOldDifficultyInt() >= 4)");
    }

    public static void CheatWeaponDuration(TestContext t)
    {
        var (ss, hu) = Game(t, "mirage", 9);
        var (sh, h8) = Game(t, "mirage", 4);
        var b9 = ss.World.Buildings.First(b => b.Owner == hu.P);
        var b4 = sh.World.Buildings.First(b => b.Owner == h8.P);
        t.Near(0.9, ss.World.AttackDuration(b9, null) / sh.World.AttackDuration(b4, null), 1e-9, "a difficulty-9 player's buildings (towers) fire 10 % faster");
        var u9 = ss.World.Units.First(u => u.Owner == hu.P);
        t.Near(0.9 * 2, ss.World.AttackDuration(u9, null), 1e-9, "and its units (and so the carriers of build-up weapons, GunnerUpdate)");
    }
}
