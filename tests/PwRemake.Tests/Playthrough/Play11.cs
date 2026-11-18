// Mission 11 "Arena" played to the end by script, the way a player would: the three heroes are ordered onto
// whatever enters the arena; the mission's own triggers do the rest (waves out of the gates on the level timer,
// the squads of the governor's guards with their random patrol routes, the boss, the final film, QUIT).
// Cheats that keep the test short and deterministic: the heroes are healed every second and what they fight loses
// 15 % of its hit points per second (the boss only down to 10 %: it has to be alive for its hit point condition).
// (The JS file does not include campaign_bot.js: its own loop and checks. The class derives from CampaignBot for the
// runner, the order command and the report's helpers only.)
// Ported from remake/tests/campaign_play_11.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play11 : CampaignBot
{
    public Play11(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        List<Unit> heroes() => W.Units.Where(u => u.Alive && u.Owner == me).ToList();
        List<Unit> foes() => W.Units.Where(u => u.Alive && u.Owner != null && u.Owner != me && !u.Parked && !u.Inside).ToList();
        var seen = new Dictionary<string, double>();            // enemy class -> first seen at (s)
        var owners = new HashSet<int>();
        int maxFoes = 0, patrolled = 0, bossLevel = 0; double bossMin = 1; string? err = null;
        void drive()
        {
            var F = foes(); var H = heroes();
            maxFoes = Math.Max(maxFoes, F.Count);
            foreach (var f in F) { if (!seen.ContainsKey(f.Name)) seen[f.Name] = Round(W.Time); owners.Add(f.Owner!.Id); if (S.Host.Patrols.Has(f)) patrolled++; }
            foreach (var h in H)
            {
                h.Hp = h.MaxHp;
                var t = h.Task.Type == "attack" && h.Task.Target != null && h.Task.Target.Alive ? h.Task.Target : null;
                if (t == null && F.Count > 0) { F = F.OrderBy(a => h.DistTo(a)).ToList(); Order(h, "attack", target: F[0]); }
            }
            foreach (var f in F)
            {
                if (!H.Any(h => h.DistTo(f) < 10 + f.Radius)) continue;
                bool boss = f.Name == "arena_atroxosaurus";
                if (boss) { bossLevel = f.Level; bossMin = Math.Min(bossMin, f.Hp / f.MaxHp); }
                double hp = f.Hp - f.MaxHp * 0.15;
                if (boss) { if (f.Hp > f.MaxHp * 0.1) W.SetHp(f, Math.Max(f.MaxHp * 0.1, hp)); } else W.SetHp(f, hp);
            }
        }
        var checks = new Dictionary<string, double>();
        void at(string name, Func<bool> cond) { if (!checks.ContainsKey(name) && cond()) checks[name] = Round(W.Time); }
        var Q = E.Quests.ToList();
        try
        {
            for (int s = 0; s < 45 * 60 && !Over; s++)
            {
                StepOnly(20);
                drive();
                at("timer", () => E.Timers.TryGetValue("0", out var tm) && !tm.Show);
                at("q1", () => Q[0].Visible);
                at("q1done", () => Q[0].Accomplished);
                at("q2", () => Q[1].Visible);
                at("q2done", () => Q[1].Accomplished);
                at("q3", () => Q[2].Visible);
                at("boss", () => foes().Any(f => f.Name == "arena_atroxosaurus"));
            }
        }
        catch (Exception e) { err = e.ToString(); Exception = e.GetType().Name + ": " + e.Message; }
        ok(err == null, "the mission ran without an exception" + (err != null ? ": " + err : ""));
        int firedN(string n) => E.Find(n)?.Fired ?? -1;
        double first(string n) { foreach (var l in E.FiringLog) if (l.Trigger == n) return l.Time; return -1; }
        double? chk(string n) => checks.TryGetValue(n, out var x) ? x : null;
        string J(double? x) => x == null ? "undefined" : N(x.Value);
        var seq = UiP.SeqAt; var dlg = UiP.DlgAt;
        // --- the story, in order
        ok(seq.Count > 0 && seq[0].Id == "ms_4010" && Round(seq[0].T) <= 2, $"the intro film ms_4010 is requested at the start ({(seq.Count > 0 ? $"[\"{seq[0].Id}\",{N(Round(seq[0].T))}]" : "undefined")})");
        ok(chk("q1") != null && chk("q1") <= 3, $"quest 1 \"In the Dinosaur Den\" appears after the film ({J(chk("q1"))} s)");
        ok(chk("timer") != null, "the level timer 0 runs hidden (the next wave comes when it runs out)");
        var animals = new[] { "arena_polakanthus", "arena_gallimimus", "arena_baryonyx", "arena_smilodon", "arena_stygimoloch", "arena_triceratops", "arena_allosaurus" };
        var times = animals.Select(a => seen.TryGetValue(a, out var x) ? x : -1).ToArray();
        ok(times.Select((t, i) => t >= 0 && (i == 0 || t > times[i - 1])).All(x => x), $"the seven animal waves come out of the gates in order ({string.Join(" ", animals.Select((a, i) => a.Substring(6, 4) + "@" + N(times[i])))})");
        ok(times[1] - times[0] >= 45 && times[1] - times[0] <= 56, $"wave 2 comes 50 s after wave 1 (TIMR; {N(times[1] - times[0])} s)");
        ok(firedN("Ga: Action_player1") >= 3 && patrolled > 0, $"the random patrol trigger gives the animals routes ({firedN("Ga: Action_player1")} firings)");
        ok(chk("q1done") != null && chk("q2") != null && chk("q2") >= chk("q1done"), $"quest 1 accomplished ({J(chk("q1done"))} s), quest 2 \"The Mayor's Life Guard\" appears ({J(chk("q2"))} s)");
        ok(dlg.Any(d => d.Id == "ds_4014") && dlg.Any(d => d.Id == "ds_4012"), $"dialogue scenes are requested ({string.Join(" ", UiP.Dlg)})");
        ok(owners.Contains(1) && owners.Contains(2) && owners.Contains(3), "squads of all three guard players appear");
        var squads = new[] { "Gg01: Spawn Aje Group", "Gg02: Spawn Ninigi Group", "Gg03: Spawn Hu Group", "Gg04: Spawn Aje Group", "Gg05: Spawn Hu Group", "Gh01: Spawn Aje Group" };
        var st = squads.Select(first).ToArray();
        ok(st.Select((t, i) => t > 0 && (i == 0 || t > st[i - 1])).All(x => x), $"the guard squads come in order ({string.Join(", ", st.Select(t => N(Round(t))))} s)");
        ok(new[] { "aje_warrior", "aje_spearman", "aje_archer", "aje_thrower", "ninigi_sumo", "ninigi_ninja", "hu_pikeman", "hu_chariot", "hu_killer" }.All(seen.ContainsKey), "the squads' classes were created (models of three foreign tribes): " + string.Join(" ", seen.Keys.Where(k => !k.StartsWith("arena_"))));
        ok(firedN("Gb: Action_player2LongDelay") >= 1 && firedN("Gc: Action_player3Long Delay") >= 1, "patrol routes for players 2 and 3");
        ok(chk("q2done") != null && chk("q3") != null, $"quest 2 accomplished ({J(chk("q2done"))} s), quest 3 \"The Final Challenge\" appears ({J(chk("q3"))} s)");
        ok(seq.Any(x => x.Id == "sc_4009"), "the boss sequence sc_4009 is requested");
        ok(chk("boss") != null && bossLevel == 4, $"the level-4 Atroxosaurus enters ({J(chk("boss"))} s, level {bossLevel})");
        ok(firedN("Gh08: Atroxo Dead") == 1 && bossMin < 0.2, $"OBJP: the boss below 20 % hit points ({bossMin * 100:0} %)");
        ok(seq.Any(x => x.Id == "ms_4015"), "the final film ms_4015 is requested");
        ok(Q[2].Accomplished, "quest 3 accomplished");
        ok(!W.Units.Any(u => u.Alive && u.Name == "arena_atroxosaurus"), "the boss is deleted after the film (DELO)");
        ok(Over && S.Won, $"QUIT: the mission is won (G.endMission(true)) after {N(Round(W.Time / 60))} game minutes");
        ok(E.Boni == 6000, $"bonus: 3 quests x 2000 ({N(E.Boni)})");
        ok(E.Errors.Count == 0, $"no action errors ({E.Errors.Count})");
        ok(E.UnknownConditions.Count + E.UnknownActions.Count == 0, "no unknown condition / action type");
        info($"triggers fired {E.Triggers.Count(x => x.Fired > 0)} of {E.Triggers.Count}, {E.StatFired} firings, {E.StatActions} actions, most enemies at once {maxFoes}, warnings {E.Warnings.Count}");
        info("sequences " + string.Join(" ", UiP.Seq) + " quests " + string.Join(" ", UiP.Quest));
        foreach (var e in E.Errors.Take(10)) info($"error {e.Trigger} {e.Type} {e.Message}");
        foreach (var w in E.Warnings.Take(20)) info("warning " + w);
        if (!Over)
        {
            info("log tail:\n" + E.DebugPrint(30));
            info("enemies left: " + string.Join("; ", W.Units.Where(u => u.Alive && u.Owner != null && u.Owner != me).Select(u => { var (mx, my) = ToMapR(u.Pos.X, u.Pos.Z); return $"{u.Name} p{u.Owner!.Id} @{mx},{my} {u.Task.Type}{(u.Parked ? " parked" : "")}{(u.Inside ? " inside" : "")} hp {N(Round(u.Hp))}"; })));
            info("heroes: " + string.Join("; ", heroes().Select(u => { var (mx, my) = ToMapR(u.Pos.X, u.Pos.Z); return $"{u.Name} @{mx},{my} {u.Task.Type} -> {u.Task.Target?.Name}"; })));
            info("live triggers: " + string.Join(" | ", E.Triggers.Where(x => x.Live).Select(x => x.Name + "[" + string.Join(" ", x.Conds.Select(c => c.Type + "=" + c.State)) + "]")));
        }
        // (for the runner: the checks are the steps)
        StepCount = Out.Count(l => l.StartsWith("ok ") || l.StartsWith("FAIL "));
        foreach (var l in Out) if (l.StartsWith("ok ") || l.StartsWith("FAIL ")) reached.Add(l.StartsWith("ok "));
        int bad = reached.IndexOf(false);
        if (bad >= 0) { failedAt = bad; FailedStep = Out.Where(l => l.StartsWith("ok ") || l.StartsWith("FAIL ")).ElementAt(bad)[5..]; }
        return string.Join("\n", Out);
    }
}
