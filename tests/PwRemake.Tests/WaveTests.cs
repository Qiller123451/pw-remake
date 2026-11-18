// The campaign's scripted waves (AIFT with custom_attack) and the classes they ask for.
//
//   Mission03Sp2Attack   MIRAGE mission 3 "Amazon Island", medium: the Amazons' SP2Attack_1 asks for 3-4 dilophosaurs
//                        that they cannot train in time (the army pyramid's level-1 row is full); after the squad
//                        timeout (80 s at Medium) the wave sets out with pool units, as the original's BuildSquad task
//                        does (Ai/Attack.cs AttackBuild) - it used to end as "squad failed" and never come.
//   WaveClassesSweep     every mission of both configurations: every AIFT wave's army (the Singleplayer table of the
//                        slot's tribe), each class classified as the slot's player could produce it at the start of the
//                        mission (no build action / no producing building / banned by a filter / only in a building
//                        mode / needs an invention / ok). Prints the table (docs/CAMPAIGN.md "Scripted waves"); fails
//                        when a wave asks for a class its player can never produce (none in both configurations).
//   PW_CONTENT (test content), the configurations' data (CampaignTests.LoadCfg).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class WaveTests
{
    public static void Mission03Sp2Attack(TestContext t)
    {
        // trigger 06m picks one of the mission's scripted waves at random at 8:01: the first seed whose pick is
        // SP2Attack_1 (seed 6 since the MIRAGE AI fixes, seed 2 before; the pick moves whenever the computer
        // player's random draws change)
        MissionSession? s = null;
        TribeAI? ai = null;
        LaunchLog? log = null;
        foreach (ulong seed in new ulong[] { 6, 2, 1, 3, 4, 5, 7, 8 })
        {
            s = CampaignTests.Session(t, "mirage", 3, "", difficulty: 1, seed: seed);
            ai = s.World.Brains.Get(1) as TribeAI;
            if (ai == null) t.Skip("no computer player 1");
            while (s.World.Time < 500 && ai!.Launched.Count == 0) s.Run(10);
            log = ai!.Launched.FirstOrDefault(l => l.Type == "SP2Attack_1");
            t.Log($"seed {seed}: " + string.Join(", ", ai.Launched.Select(l => $"{l.Type} at {l.T:0} s")));
            if (log != null) break;
        }
        t.True(log != null, "the scripted wave SP2Attack_1 was ordered (seeds 1-8)");
        for (int i = 0; i < 12 * 6 && log!.Go == null; i++) s!.Run(10);
        var S = ai!.Census(true);
        var T = ai.TrainableOf(S, "aje_dilophosaurus");
        t.Log($"SP2Attack_1 ordered at {log!.T} s, set out at {log.Go} s with {log.N} units; the dilophosaurus now: {(T?.Producer != null ? "trainable" : T?.Why ?? "no action")}");
        t.True(log.Go != null && log.N > 0, "the wave set out (with pool units) instead of giving up");
        t.True(log.Go!.Value - log.T <= 100, "after the squad timeout (80 s at Medium) at the latest");
    }

    sealed record WaveRow(string Cfg, int Mission, string Trigger, int Slot, string Tribe, string Type, bool Spawn, string Cls, string Verdict);

    public static void WaveClassesSweep(TestContext t)
    {
        var rows = new List<WaveRow>();
        int missions = 0;
        foreach (var cfg in new[] { "base", "mirage" })
        {
            var c = CampaignTests.LoadCfgPublic(cfg);
            if (c == null) { t.Log($"configuration {cfg} not found"); continue; }
            var vis = VisibilityFilters(System.IO.Path.Combine(c.Data, "techtree.json"));
            foreach (var e in MissionEntry.LoadIndex(c.Config.MissionsDir).OrderBy(x => x.Id))
            {
                MissionSession s;
                try { s = CampaignTests.Session(t, cfg, e.Id, "", difficulty: 1); }
                catch (SkipException) { continue; }
                missions++;
                foreach (var tr in s.Lua.Script.Triggers)
                    foreach (var n in tr.Actions.Where(a => a.Code == "AIFT"))
                    {
                        var P = new PwRemake.Sim.Scripting.TriggerParams(n.P);
                        if (!P.On("custom_attack")) continue;
                        var type = P.S("attack_type");
                        if (type is "PyramidAttack" or "RessourceOutpost" or "AttackOutpost") continue;
                        int slot = P.Slot("player_id", 0);
                        var ai = s.World.Brains.Get(slot) as TribeAI;
                        if (ai == null) { rows.Add(new(cfg, e.Id, tr.Name, slot, "?", type, P.On("attack_with_all"), "-", "no computer player")); continue; }
                        var army = ai.Data.Army(ai.P.Tribe, "Singleplayer", type);
                        if (army == null) { rows.Add(new(cfg, e.Id, tr.Name, slot, ai.P.Tribe, type, P.On("attack_with_all"), "-", "no army table: pool units")); continue; }
                        foreach (var r in army)
                        {
                            var verdicts = r.Alternatives.Select(a => (a.Cls, V: Verdict(ai, a.Cls, vis))).ToList();
                            var best = verdicts.FirstOrDefault(v => v.V == "ok" || v.V.StartsWith("ok:")) is { Cls: not null } ok ? ok : verdicts[0];
                            rows.Add(new(cfg, e.Id, tr.Name, slot, ai.P.Tribe, type, P.On("attack_with_all"), best.Cls, best.V));
                        }
                    }
            }
        }
        t.True(missions > 10, $"missions swept: {missions}");
        // one line per (configuration, mission, slot, army, class): the triggers named
        var lines = rows.GroupBy(r => (r.Cfg, r.Mission, r.Slot, r.Tribe, r.Type, r.Spawn, r.Cls, r.Verdict))
            .OrderBy(g => g.Key.Cfg).ThenBy(g => g.Key.Mission).ThenBy(g => g.Key.Type).ToList();
        int bad = 0;
        foreach (var g in lines)
        {
            var k = g.Key;
            bool not = !(k.Verdict == "ok" || k.Verdict.StartsWith("ok:") || k.Verdict.StartsWith("needs") || k.Verdict.StartsWith("no army table"));
            if (not) bad++;
            t.Log($"{(not ? "!! " : "   ")}{k.Cfg,-6} {k.Mission,2} p{k.Slot} {k.Tribe,-6} {k.Type,-22} {(k.Spawn ? "spawn" : "train"),-5} {k.Cls,-32} {k.Verdict}   [{string.Join(", ", g.Select(x => x.Trigger).Distinct().Take(3))}]");
        }
        t.Log($"{lines.Count} wave classes, {bad} the player cannot produce at the start");
        // (2026-11-01: none - every class a wave asks for has a build action, a producing building and an upgrade that
        // shows it; whether the player has that epoch / mode / free pyramid place when the wave comes is the game's
        // matter: an incomplete squad goes with pool units after the timeout, AttackBuild)
        t.Equal(0, bad, "wave classes the player can never produce");
        t.True(lines.Count > 20, "waves found");
    }

    /// <summary>can the slot's player produce a class (at the start of the mission)?</summary>
    static string Verdict(TribeAI ai, string cls, Dictionary<string, List<string>> vis)
    {
        var p = ai.P; var D = ai.W.Data;
        var R = ai.Resolve("CHTR/" + cls, "");
        if (R == null || R.Kind != "unit") return D.Exists(cls) ? $"no build action ({D.Info(cls)?.Tribe ?? "?"} class)" : "unknown class";
        var a = R.Action;
        // where it is made: a building the player has or can build
        var at = a.Locs.Select(l => l.At).Distinct().ToList();
        var have = ai.W.Buildings.Where(b => b.Alive && b.Owner == p && at.Contains(b.Name)).Select(b => b.Name).Distinct().ToList();
        var buildable = at.Where(x => ai.Resolve("BLDG/" + x, "") is { } rb && D.Check(p, rb.Action) != "disabled").ToList();
        var units = at.Where(x => !(D.Info(x) is { Type: "BLDG" })).ToList();          // made by a unit (a worker, a hero)
        if (have.Count == 0 && buildable.Count == 0 && units.Count == 0) return $"no producing building ({string.Join("/", at)} cannot be built)";
        var chk = D.Check(p, a);
        if (chk == "disabled") return "banned (a filter of the mission or the mod)";
        if (chk == "hidden")
        {
            // shown by an upgrade's filter: an epoch (age_N), a mode of its building (a local upgrade: the Aje farm's
            // small / medium / huge farm), another invention - or by nothing (a filter only a mission could switch on)
            var ap = a.Path.TrimStart('/');
            if (!ap.StartsWith("Actions/", StringComparison.Ordinal)) ap = "Actions/" + ap;
            var path = "/" + ap + "/visibility";
            if (!vis.TryGetValue(path, out var by) || by.Count == 0) return "hidden (no filter shows it)";
            var ups = by.Select(f => f.Split('/')).Where(x => x.Length > 5 && x[3] == "Upgrades").Select(x => (Bld: x[4], Up: x[5])).Distinct().ToList();
            var local = ups.Where(u => D.ActionsOf(p, u.Bld, "building", "BLDG").Any(x => x.Id == u.Up && x.Kind == "Upgrades" && x.Results.Any(r => r.Cls == "local"))).ToList();
            if (local.Count > 0) return $"ok: in a building mode ({string.Join(", ", local.Select(u => u.Bld + " " + u.Up))})";
            return "needs " + string.Join(" or ", ups.Select(u => u.Up).Distinct()) + (ups.Count == 0 ? string.Join(", ", by) : "");
        }
        if (chk == "req") return "needs " + string.Join(", ", D.Missing(p, a));
        return have.Count > 0 || units.Count > 0 ? "ok" : $"ok: once {string.Join("/", buildable)} stands";
    }

    /// <summary>the filters that make an action visible: action path + "/visibility" -> filter paths (techtree.json)</summary>
    static Dictionary<string, List<string>> VisibilityFilters(string file)
    {
        var res = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (PwRemake.Sim.Core.Json.TryParseFile(file)?["Filters"] is not System.Text.Json.Nodes.JsonObject f) return res;
        void Walk(System.Text.Json.Nodes.JsonNode? n, string p)
        {
            if (n is not System.Text.Json.Nodes.JsonObject o) return;
            if (o["path"] is System.Text.Json.Nodes.JsonValue pv && pv.TryGetValue(out string? path) && path.EndsWith("/visibility", StringComparison.Ordinal))
            {
                if (!res.TryGetValue(path, out var l)) res[path] = l = new List<string>();
                l.Add(p);
            }
            foreach (var (k, v) in o) Walk(v, p + "/" + k);
        }
        Walk(f, "/Filters");
        return res;
    }
}
