// MIRAGE skirmish: the Dustriders' computer player never attacked (the browser remake did the same). Three causes,
// none of them MIRAGE's (docs/AI.md "MIRAGE: the Dustriders' computer player"):
//  1. the attack plans' skirmish rows are conditioned on "!m_bCustommap" (AiGoalGeneralAttack.usl), an atom data.js
//     did not know: every MIRAGE player fell to the campaign rows - the Dustriders' Giraffe row is "appended" (= none);
//  2. MIRAGE's tech tree has hidden Build actions aje_medium_farm / aje_huge_farm that also put up an aje_small_farm,
//     sorted before it: the planner asked for the hidden one, never built the farm its epoch-1 army (the velociraptor
//     handler) comes from, and so had no fighters;
//  3. MIRAGE's gifts (AiModuleControlDefault.usl Think) were not in ai.json: no computer player got any.
// The test: an AI-vs-AI skirmish of MIRAGE, Hu against Aje: the Dustriders build their farm, train an army, have an
// attack plan and attack.
using System;
using System.IO;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Game;

namespace PwRemake.Tests;

[TestClass]
public static class MirageAjeTests
{
    const string MAP = "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer/multi_2_jun_001.ula";

    static (SkirmishSession Ss, TribeAI Aje) Game(TestContext t, string cfgId, double level = 4)
    {
        var c = CampaignTests.LoadCfgPublic(cfgId);
        if (c == null) t.Skip("no configuration " + cfgId);
        if (!File.Exists(MAP)) t.Skip("map not staged");
        var setup = new SkirmishSetup { Seed = 1, Points = -1, ChooseHQ = true, Slots = {
            new SkirmishSlot { Tribe = "Hu", Type = "ai", Level = level, Behaviour = "Giraffe", Team = 1, Hq = 0 },
            new SkirmishSlot { Tribe = "Aje", Type = "ai", Level = level, Behaviour = "Giraffe", Team = 2, Hq = 1 } } };
        var f = c!.Config.GameFile("Scripts/Ai/Modules/AiModuleControlDefault.usl");
        var ss = SkirmishSession.Create(setup, MAP, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, f != null ? File.ReadAllText(f) : null);
        return (ss, ss.Ai.Brains.Values.First(b => b.P.Tribe == "Aje"));
    }

    /// <summary>the m_bCustommap atom and the farm's own action</summary>
    public static void MirageAjeTables(TestContext t)
    {
        var (ss, aje) = Game(t, "mirage");
        var ctx = aje.Ctx();
        t.Equal(false, ctx.Custommap, "a skirmish map is no custom map");
        var plan = aje.Data.AttackPlan(ctx);
        t.Log($"Aje Giraffe attack plan: {string.Join(" ", plan.Attacks)}");
        t.True(plan.Attacks.Any(a => a != "none"), "the skirmish row of the attack plan (not the campaign's appended one)");
        ctx.Custommap = null;
        t.True(aje.Data.AttackPlan(ctx).Attacks.All(a => a == "none"), "unknown (data.js): the campaign row, no attacks");
        var r = aje.Resolve("BLDG/aje_small_farm", "");
        t.Equal("/Actions/Aje/Build/BLDG/aje_small_farm", r?.Action.Path, "the small farm's own action, not the hidden huge farm's");
        foreach (var p in ss.World.Players)
        {
            var hu = ss.Ai.Brains.Get(p.Id)!;
            t.True(hu.Data.AttackPlan(hu.Ctx()).Attacks.Any(a => a != "none"), p.Tribe + ": an attack plan");
        }
    }

    /// <summary>MIRAGE's gifts read from its control script (AiModuleControlDefault.usl Think)</summary>
    public static void MirageAiGifts(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("mirage");
        if (c == null) t.Skip("no configuration mirage");
        var f = c!.Config.GameFile("Scripts/Ai/Modules/AiModuleControlDefault.usl");
        if (f == null) t.Skip("MIRAGE's AI scripts not in the installation");
        var A = new AiData(c.Ai);
        t.Equal(0.0, A.Gift(9, true)["food"], "ai.json: no gifts (the toolkit reads the base game's form only)");
        t.True(A.ReadControlScript(File.ReadAllText(f!)), "the control script is read");
        t.Equal(0.0, A.Gift(4, true, 1, 0)["food"], "player 0 gets nothing (Think: m_iPlayerID!=0)");
        t.Equal(0.0, A.Gift(1, true, 1, 1)["food"], "difficulty 0-1: nothing");
        var med = A.Gift(4, true, 1, 1);
        t.Log($"difficulty 4, skirmish: {string.Join(" ", med.Select(kv => kv.Key + " " + kv.Value))}");
        t.Equal(20.0, med["food"], "Medium on a skirmish map: 20 food (SpawnResources)");
        t.Equal(0.0, med["skulls"], "difficulty 4 lists no skulls");
        var hard = A.Gift(9, true, 3, 1);
        t.Log($"difficulty 9, epoch 3, skirmish: {string.Join(" ", hard.Select(kv => kv.Key + " " + kv.Value))}");
        t.Equal(18.0, hard["food"], "Hard: the listed 9 food x 2 (epoch III)");
        t.Equal(4.0, hard["skulls"], "Hard: 2 skulls x 2");
        var camp = A.Gift(4, false, 2, 1);
        t.Equal(Math.Floor(3 * 1.5) + 10, camp["food"], "a campaign map: listed x epoch factor + 10");
        // the base game: ai.json has its gifts, the script is not read over them
        var cb = CampaignTests.LoadCfgPublic("base");
        if (cb != null) t.True(!new AiData(cb.Ai).ReadControlScript(File.ReadAllText(f)), "ai.json's own gifts stay");
    }

    /// <summary>15 minutes of Hu against Aje on MIRAGE: the Dustriders put up their farm, train an army and attack</summary>
    public static void MirageAjeAttacks(TestContext t)
    {
        var (ss, aje) = Game(t, "mirage");
        int peak = 0; bool farm = false;
        for (int m = 1; m <= 15; m++)
        {
            ss.Run(60);
            var S = aje.Census(true);
            peak = Math.Max(peak, S.Fighters.Count);
            farm |= S.Blds.Any(b => b.Name == "aje_small_farm");
            if (m % 3 == 0) t.Log($"{m} min: epoch {S.Age} workers {S.Workers.Count} fighters {S.Fighters.Count} res {string.Join("/", new[] { "food", "wood", "stone", "skulls" }.Select(r => Math.Round(aje.P.Res[r])))} | attacks {string.Join(", ", aje.Launched.Where(l => !l.Hunt).Select(l => $"{l.Type}{(l.Go != null ? "@" + l.Go : " forming")}"))} | {string.Join(" ", S.Blds.Select(b => b.Name).Distinct())}");
        }
        t.True(farm, "the Dustriders built their small farm");
        t.True(peak >= 3, $"they had an army (peak {peak})");
        t.True(aje.Launched.Any(l => !l.Hunt && !l.Scripted), "they attacked (or formed an attack squad)");
    }
}
