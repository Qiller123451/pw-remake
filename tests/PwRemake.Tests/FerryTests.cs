// Transports that stood in place (player report, MIRAGE mission 2 "Druid Island": "the transport turtles ... sometimes
// stand in place for a long time only to start moving later").
//
//   Mission02Turtles       base and MIRAGE mission 2: the Aje's scripted turtle landings (Root/A_AI/c_spawn_ship_attacks:
//                          ObjDo WalkAction "[x y z] | 1" from waypoint to waypoint, the next one after a fixed Time, the
//                          unloading after another). The script's times fit the original's gait 1 (about 1.5 m/s: every leg
//                          needs 1.4-1.65 m/s); the remake ignored the speed and sailed at full speed (7 m/s), so the turtles
//                          waited 35-120 s at each waypoint. Now a laden turtle never stands longer than 15 s and both land
//                          their troops (the script unloads at 3:11 and 5:37 after the trigger).
//   Mission15SeasCrossing  base mission 15: SEAS players 3 and 4 cross to the player's island in hovercraft - a vehicle,
//                          which the crossing never took (it looked in the fleet only): every SEAS crossing stood two minutes
//                          and gave up. Now they board, sail and land within 3 minutes, never standing more than 15 s on
//                          the way; players 1 and 2 never go by ship in Single 15 (GA:276: the attack fails at once).
//   Mission12ShipCrossing  base mission 12: the Hu (transport ship) and the Aje (turtle, amphibious) cross to the player's
//                          island with a transport each; boarding begins at once (it waited for every 7th think of the
//                          attack goal: up to 30 s, then 35 s between the rounds) and the squads land within 4 minutes.
//
// A transport shot down on its way is a legitimate end of a crossing, not a transport that stood (LandedOrShotDown). Mission
// 15's p3 hovercraft sails the last ~80 m along the player's shore, where the player's heroes and fighters (Bela, Tesla,
// Lovelace, Cole, chariots, spearmen) stand: on every seed it is shot at, and whether it reaches the landing before it
// sinks depends on who of them is still alive from the fights of the first minute (2026-11-13: after the animation audit
// - idle variants, gather times and death clips draw the world's random numbers in another order - Lovelace survives
// the p5 helicopter at 46 s that killed her before, and her shots sink the hovercraft at 94 % of its way to the landing; seeds
// 1-8 before the audit: p3 sunk before landing on seeds 3 and 4, after it on 1, 2, 5-8; after: sunk before landing on
// seeds 1, 6, 7). So a crossing passes when its squad lands in time OR when its transport, laden and on the way, was
// destroyed by an enemy's attack within the limit after covering at least half of the way from where it set sail to the
// landing - a transport that stood, sailed elsewhere or died of anything but enemy fire still fails - and at least one
// crossing of the mission must really land, so the landing itself is checked on every run.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class FerryTests
{
    /// <summary>longest time a unit stood still (one sample a second) while cond held</summary>
    sealed class StillClock
    {
        readonly Dictionary<object, double> since = new();
        public double Max; public string? Who;
        public void Sample(object key, bool still, double now, string who)
        {
            if (!still) { since.Remove(key); return; }
            if (!since.TryGetValue(key, out var t0)) { since[key] = now; return; }
            if (now - t0 > Max) { Max = now - t0; Who = who; }
        }
    }

    public static void Mission02Turtles(TestContext t)
    {
        foreach (var cfg in new[] { "base", "mirage" })
        {
            MissionSession s;
            try { s = CampaignTests.Session(t, cfg, 2, "", difficulty: 1); } catch (SkipException e) { t.Log($"{cfg}: {e.Message}"); continue; }
            var W = s.World;
            s.Run(2);
            // the end of the mammoth sequence starts the landings (S03 -> Ac01 / Ac02 / Ac03)
            t.True(s.Engine!.DebugEnable("03_sc_1082_7_mammoths_save_<-[Lb02]"), "the trigger of the landings");
            double t0 = W.Time;
            var clock = new StillClock();
            var laden = new HashSet<Unit>(); var landed = new Dictionary<Unit, double>();
            while (W.Time - t0 < 360)
            {
                s.Run(1);
                foreach (var u in W.Units)
                {
                    if (!u.Alive || u.Name != "aje_transport_turtle") continue;
                    int pax = u.Passengers?.Count ?? 0;
                    if (pax > 0) laden.Add(u);
                    else if (laden.Contains(u) && !landed.ContainsKey(u)) landed[u] = W.Time - t0;
                    // standing still with its troops aboard, the trigger time not yet up
                    clock.Sample(u, pax > 0 && u.Vel.Length < 0.3, W.Time, $"{u.Name}#{u.Id} at ({u.Pos.X:0}, {u.Pos.Z:0})");
                }
            }
            t.Log($"{cfg}: laden turtles {laden.Count}, landed at {string.Join(", ", landed.Values.Select(v => $"{v:0} s"))}; longest stop with troops aboard {clock.Max:0} s ({clock.Who})");
            t.True(laden.Count >= 2, $"{cfg}: the laden turtles of the landings");
            t.Equal(laden.Count, landed.Count, $"{cfg}: every laden turtle landed its troops within 6 minutes");
            t.True(clock.Max <= 15, $"{cfg}: no laden turtle stood more than 15 s (longest {clock.Max:0} s: {clock.Who})");
        }
    }

    /// <summary>runs a mission and makes the computer players whose home lies on another island than the player's main
    /// building attack it (StartAutoAttack, after warm s); tracks their crossings</summary>
    sealed class Crossings
    {
        public readonly Dictionary<(int P, int A), (double Start, double? Sail, double? Landed, string? End)> Log = new();
        public readonly StillClock Still = new();
        /// <summary>a crossing's transports while it lasted, their passengers at the last sample; the landing and how far
        /// the laden transports were from it when they set sail</summary>
        public readonly Dictionary<(int P, int A), Dictionary<Unit, int>> Ships = new();
        public readonly Dictionary<(int P, int A), (Pt Land, double Dist)> SailFrom = new();
        /// <summary>a laden transport destroyed on the way: when, by whom, an enemy of the crossing's player, the part of
        /// the way to the landing it had covered</summary>
        public readonly Dictionary<(int P, int A), (double T, string By, bool Enemy, double Covered)> Lost = new();
    }
    static double DistP(Entity e, Pt p) => Math.Sqrt((e.Pos.X - p.X) * (e.Pos.X - p.X) + (e.Pos.Z - p.Z) * (e.Pos.Z - p.Z));
    static Crossings Run(TestContext t, MissionSession s, double warm, double minutes, Func<TribeAI, bool> who, bool giveTransport)
    {
        var W = s.World; var C = new Crossings();
        s.Run(warm);
        var hum = W.Players.First(x => !x.Ai);
        var hb = W.Buildings.Where(b => b.Alive && b.Owner == hum).OrderByDescending(b => b.Def!.Script == "CHQ" ? 1 : 0).First();
        var brains = new List<TribeAI>();
        foreach (var pl in W.Players)
        {
            if (W.Brains.Get(pl.Id) is not TribeAI ai || !who(ai)) continue;
            var S = ai.Census(true);
            if (S.Home == null || S.Pool.Count == 0) continue;
            if (giveTransport)
            {
                // a transport of its own at the water off its harbour (the class of the water crossing goal)
                var cls = new[] { "hu_transport_ship", "aje_transport_turtle", "ninigi_transport_boat", "seas_hovercraft" }.First(n => n.StartsWith(pl.Tribe.ToLowerInvariant()));
                var hp = S.Blds.FirstOrDefault(b => System.Text.RegularExpressions.Regex.IsMatch(b.Name, "harbour|carrier"))?.Pos ?? new V3(S.Home.Value.X, 0, S.Home.Value.Z);
                var wn = W.WaterNav!; var (wx, wz) = wn.Center(wn.NearestFree(wn.Idx(hp.X, hp.Z), 40));
                t.True(W.SpawnUnit(cls, pl, wx, wz, null, 0) != null, $"p{pl.Id}: a {cls}");
            }
            var id = ai.StartAutoAttack(new List<Entity> { hb });
            t.Log($"p{pl.Id} {pl.Tribe}: attack {id} on the player's {hb.Name}: {string.Join("; ", ai.Attacks.Select(a => a.ToString()))}");
            brains.Add(ai);
        }
        double end = W.Time + minutes * 60;
        while (W.Time < end)
        {
            s.Run(1);
            foreach (var ai in brains)
            {
                foreach (var l in ai.Launched.Where(l => l.Result != null)) foreach (var k in C.Log.Keys.Where(k => k.P == ai.P.Id && k.A == l.Id && C.Log[k].End == null).ToList()) C.Log[k] = C.Log[k] with { End = l.Result };
                foreach (var a in ai.Attacks)
                {
                    var key = (ai.P.Id, a.Id);
                    var F = a.FerryState;
                    // a laden transport of the crossing destroyed since the last sample (its passengers are thrown out
                    // near a shore or die with it; the crossing goes on with whoever is out)
                    if (C.Ships.TryGetValue(key, out var seen) && !C.Lost.ContainsKey(key))
                        foreach (var (x, pax) in seen)
                            if (!x.Alive && pax > 0)
                            {
                                var by = x.LastAttacker;
                                double cov = C.SailFrom.TryGetValue(key, out var sf) && sf.Dist > 0 ? 1 - DistP(x, sf.Land) / sf.Dist : 0;
                                C.Lost[key] = (W.Time, $"{by?.Name}#{by?.Id} p{by?.Owner?.Id} at {x.LastHit:0} s", by?.Owner != null && ai.P.IsEnemy(by.Owner) && x.LastHit >= W.Time - 2, cov);
                                break;
                            }
                    if (F == null) { if (a.Crossed && C.Log.TryGetValue(key, out var r) && r.Landed == null) C.Log[key] = r with { Landed = W.Time }; continue; }
                    if (!C.Log.ContainsKey(key)) C.Log[key] = (W.Time, null, null, null);
                    if (!C.Ships.ContainsKey(key)) C.Ships[key] = new();
                    foreach (var x in F.Ships) C.Ships[key][x] = x.Passengers?.Count ?? 0;
                    if (F.Phase == "sail" && C.Log[key].Sail == null)
                    {
                        C.Log[key] = C.Log[key] with { Sail = W.Time };
                        var laden = F.Ships.Where(x => (x.Passengers?.Count ?? 0) > 0).ToList();
                        if (laden.Count > 0) C.SailFrom[key] = (F.Land, laden.Average(x => DistP(x, F.Land)));
                    }
                    var us = a.Alive();
                    var outp = us.Where(u => !u.Inside).ToList();
                    // a squad and its transports all standing still while boarding or sailing (waiting for a transport in
                    // training is a reason)
                    bool still = F.Phase != "ships" && outp.All(u => u.Vel.Length < 0.3) && F.Ships.All(x => x.Vel.Length < 0.3)
                        && (F.Phase == "board" ? outp.Count > 0 : F.Ships.Any(x => (x.Passengers?.Count ?? 0) > 0));
                    C.Still.Sample(key, still, W.Time, $"p{ai.P.Id} attack {a.Id} ({F.Phase}, round {F.Round}, {outp.Count} out, {us.Count - outp.Count} aboard)");
                }
            }
        }
        foreach (var (k, v) in C.Log) t.Log($"p{k.P} attack {k.A}: ferry {v.Start:0} s, sails {v.Sail:0}, landed {v.Landed:0}, end {v.End}"
            + (C.Lost.TryGetValue(k, out var l) ? $"; laden transport destroyed at {l.T:0} s by {l.By} ({(l.Enemy ? "enemy fire" : "not an enemy's hit")}, {l.Covered:P0} of the way)" : ""));
        t.Log($"longest stop of a squad and its transports {C.Still.Max:0} s ({C.Still.Who})");
        return C;
    }

    /// <summary>a crossing that landed within limit s of its start, or whose laden transport was shot down by an enemy within
    /// the limit after covering at least half of the way to the landing (see the head of the file); null: neither</summary>
    static string? LandedOrShotDown(Crossings C, (int P, int A) key, double limit)
    {
        var v = C.Log[key];
        if (v.Landed != null && v.Landed - v.Start <= limit) return $"landed after {v.Landed - v.Start:0} s";
        if (v.Sail != null && C.Lost.TryGetValue(key, out var l) && l.Enemy && l.T - v.Start <= limit && l.Covered >= 0.5)
            return $"shot down by {l.By} after {l.T - v.Start:0} s, {l.Covered:P0} of the way to the landing";
        return null;
    }

    public static void Mission15SeasCrossing(TestContext t)
    {
        var s = CampaignTests.Session(t, "base", 15, "", difficulty: 1);
        var C = Run(t, s, 30, 4, ai => ai.P.Id is >= 1 and <= 4, false);
        var W = s.World;
        // players 1 and 2 never cross in Single 15 (GA:276): their attacks fail at once
        foreach (var id in new[] { 1, 2 })
            if (W.Brains.Get(id) is TribeAI ai) t.True(ai.Attacks.All(a => a.FerryState == null) && !C.Log.Keys.Any(k => k.P == id), $"p{id}: no crossing");
        foreach (var id in new[] { 3, 4 })
        {
            var mine = C.Log.Where(kv => kv.Key.P == id).ToList();
            t.True(mine.Count > 0, $"p{id}: a crossing");
            var (fk, first) = mine.OrderBy(kv => kv.Value.Start).First();
            t.True(first.Sail != null && first.Sail - first.Start <= 60, $"p{id}: aboard and sailing within a minute ({first.Sail - first.Start:0} s)");
            var how = LandedOrShotDown(C, fk, 180);
            t.True(how != null, $"p{id}: landed within 3 minutes, or shot down on the way ({first.Landed - first.Start:0} s{(C.Lost.TryGetValue(fk, out var l) ? $"; lost at {l.T - first.Start:0} s by {l.By}, enemy {l.Enemy}, {l.Covered:P0} of the way" : "")})");
            t.Log($"p{id}: {how}");
        }
        t.True(C.Log.Any(kv => kv.Key.P is 3 or 4 && kv.Value.Landed != null && kv.Value.Landed - kv.Value.Start <= 180), "a SEAS squad landed within 3 minutes");
        t.True(C.Still.Max <= 15, $"no squad stood more than 15 s with its transports (longest {C.Still.Max:0} s: {C.Still.Who})");
    }

    public static void Mission12ShipCrossing(TestContext t)
    {
        var s = CampaignTests.Session(t, "base", 12, "", difficulty: 1);
        // the Hu (player 4: transport ship at its harbour) and the Aje (player 5: transport turtle, amphibious)
        var C = Run(t, s, 30, 5, ai => ai.P.Id is 4 or 5, true);
        foreach (var id in new[] { 4, 5 })
        {
            var mine = C.Log.Where(kv => kv.Key.P == id).OrderBy(kv => kv.Value.Start).ToList();
            t.True(mine.Count > 0, $"p{id}: a crossing");
            var (fk, first) = mine[0];
            t.True(first.Sail != null && first.Sail - first.Start <= 90, $"p{id}: aboard and sailing within 90 s ({first.Sail - first.Start:0} s)");
            var how = LandedOrShotDown(C, fk, 240);
            t.True(how != null, $"p{id}: landed within 4 minutes, or shot down on the way ({first.Landed - first.Start:0} s)");
            t.Log($"p{id}: {how}");
        }
        t.True(C.Log.Any(kv => kv.Key.P is 4 or 5 && kv.Value.Landed != null && kv.Value.Landed - kv.Value.Start <= 240), "a squad landed within 4 minutes");
        t.True(C.Still.Max <= 15, $"no squad stood more than 15 s with its transports (longest {C.Still.Max:0} s: {C.Still.Who})");
    }
}
