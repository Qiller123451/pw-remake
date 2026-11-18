// Mission 13 "The Rush" played by script: after the opening film sc_4070 (quest "Hold the Fort") the base grows and
// trains an army that waits in two squads in front of the city wall on the SEAS' ways and fights what comes near;
// the big SEAS attack after 45 minutes is beaten (quest "Hold the Fort" accomplished). Then the army marches to the
// SEAS main base and breaks its headquarters (quest "Counter Attack"); the film ms_6020 ends the mission. The side
// quests are left alone.
// Cheats (CampaignBot): the player's units are healed, enemies near the player's fighters are worn down, a unit that
// makes no progress for 60 s is teleported (all counted and reported).
// Ported from remake/tests/campaign_play_13.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

public sealed class Play13 : CampaignBot
{
    public Play13(MissionSession s, PlayPresenter p) : base(s, p) { }

    public override string Play()
    {
        List<Unit> army() => fighters().Where(u => u.Name != "darwin_s0").ToList();
        bool foe(Entity e) => e.Owner != null && e.Owner != me && !me.IsFriend(e.Owner);
        // (not in the JS: MIRAGE's map of the mission has two headquarters on the plateau, seas_headquarters_15 and _16,
        // both to be broken - Lb02 - and no seas_headquarters_7: then the SEAS main base's headquarters one after the other)
        Entity? hq() => named("seas_headquarters_7") ?? W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == C.Players[1] && b.Name == "seas_headquarters");
        // three squads in front of the city wall, where the SEAS' attack paths end (CBL_Aab04b and the outposts' routes
        // come down at x 763, 820 and 907; the wall runs on to x 980) and at its west end (x 570 .. 660: the cliffs west
        // of the gate keep the first outpost's attackers on that side, map PaFi grid)
        var POSTS = new[] { (636, 322), (800, 370), (930, 370) };
        List<Building> WALLS() => W.Buildings.Where(b => b.Alive && Regex.IsMatch(b.Name, "^hcl13_(city_wall|gate)")).ToList();
        void @base()
        {
            gather(new Dictionary<string, double> { ["food"] = 600, ["wood"] = 500, ["stone"] = 300 });
            housing();
            if (workers().Count < 12) produce("ninigi_worker", 1);
            if (army().Count < 30) { produce("ninigi_warrior", 1); produce("ninigi_archer", 1); }
        }
        void guard()
        {
            var a = army(); var walls = WALLS();
            for (int i = 0; i < POSTS.Length; i++)
            {
                var (mx, my) = POSTS[i];
                var squad = a.Where((u, k) => k % POSTS.Length == i).ToList(); var (x, z) = C.ToGame(mx, my);
                // whatever comes near the post or the wall pieces nearest to it; the middle squad also covers the base
                int near(Entity w) { var wx = C.ToMap(w.Pos.X, w.Pos.Z).MX; int b = 0; for (int k = 0; k < POSTS.Length; k++) if (Math.Abs(POSTS[k].Item1 - wx) < Math.Abs(POSTS[b].Item1 - wx)) b = k; return b; }
                var mineW = walls.Where(w => near(w) == i).ToList();
                var ownE = i == 1 ? myBuildings().Cast<Entity>().Concat(workers()).ToList() : new List<Entity>();
                bool close(Entity e) => Hypot(e.Pos.X - x, e.Pos.Z - z) < 90 || mineW.Any(w => w.DistTo(e) < 50) || ownE.Any(b => b.DistTo(e) < 40);
                if (!hunt(squad, e => foe(e) && e.Kind == "unit" && close(e), "squad" + i)) go(squad, mx, my, "squad" + i);
            }
        }
        var steps = new List<Step>
        {
            new("the opening film sc_4070: quest \"Hold the Fort\"", () => gather(), () => questState("L13MQ01") == "open", 120),
            new("the army waits in front of the gate until the big SEAS attack is beaten: quest \"Hold the Fort\"", () => { @base(); guard(); }, () => questState("L13MQ01") == "done", 4200),
            new("quest \"Counter Attack\"", () => { @base(); guard(); }, () => questState("L13MQ02") == "open", 600),
            new("the army breaks the SEAS headquarters on the plateau: quest \"Counter Attack\"", () =>
            {
                @base();
                var a = army(); var h = hq();
                if (a.Count == 0 || h == null) return;
                if (!hunt(a, e => foe(e) && e.Kind == "unit" && e.DistTo(a[0]) < 25, "army")) hunt(a, e => e == h, "army");
            }, () => questState("L13MQ02") == "done", 3000),
            new("the film ms_6020 ends the mission", () => { }, () => Over, 300),
        };
        runSteps(steps);
        ok(!Over || S.Won, "QUIT: the mission is won");
        foreach (var g in new[] { "team_mainbase", "team_outpost01", "team_outpost02", "team_outpost03" })
        {
            var m = grp(g);
            info($"group {g}: {m.Count} alive " + string.Join(" ", m.Take(4).Select(e => { var (mx, my) = ToMapR(e.Pos.X, e.Pos.Z); return $"{e.Name}@{mx},{my}:{(e as Unit)?.Task.Type}{(e is Unit u && S.Host.Patrols.Has(u) ? "(wp)" : "")}"; })));
        }
        info("city wall pieces left: " + WALLS().Count + " of 20 (lost: " + string.Join(" ", new[] { "hcl13_gate_3" }.Concat(Enumerable.Range(0, 19).Select(i => "hcl13_city_wall_" + (74 + i))).Where(n => named(n) == null)) + ")");
        return Report(new() { [2] = "CBL_Ge02#defeated", [4] = "Lb02: L13MQ02 - set acc", [5] = "S02: HS 6020" }) + "\n   won: " + EndInfo +
            "\n   variables: " + string.Join(" ", new[] { "init_main_attack", "main_attack_done", "team_mainbase_def", "team_outpost01_def", "team_outpost02_def", "team_outpost03_def" }.Select(n => n + "=" + Var(n)));
    }
}
