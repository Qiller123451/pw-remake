// The grown co-op maps keep the mission's gameplay, and the enemy additions work (docs/COOP.md "Maps", "Enemy bases"):
//
//   CoopWorldTests.GrowthKeepsGameplay   every mission, 2 and 3 seats: no two parts of the old map that single player
//                                        keeps apart (every gate closed: walls, gates, cliffs, the sea) are joined by the
//                                        new land (a strip of beach with nothing on it, under 6000 m2, aside); no sea lane
//                                        of the old map is cut (no open water of 16000 m2 or more cut off)
//   CoopWorldTests.EnemyAdditions        every addition in use is placed where the definition says, on dry walkable
//                                        ground, owned as it says (a new player in a free slot, standing to everybody as
//                                        its `like`; or the mission's player); what the objectives count of it can be
//                                        reached (an owner's: seat 1 walks - or sails - to it); a new player is named by
//                                        no trigger and stands in no region a trigger counts for "anybody"
//   CoopWorldTests.CoopSmoke             every mission, 3 seats (seat 1 and two AI commanders) and 2 seats (seat 3
//                                        empty): the game starts, runs a few minutes with every brain and trigger, no
//                                        exception, no trigger error; the new enemy players build and send a wave
//   CoopWorldTests.WaveCannotWalk        a camp on other land than its seat's (base 06's cove moved onto an islet): its
//                                        wave goes by boat and comes ashore on the seat's land
//
//   dotnet run --project tests/PwRemake.Tests -- CoopWorld      (PW_COOP_MISSIONS=2,13 limits the missions)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Coop;
using PwRemake.Sim.Game;
using PwRemake.Sim.World;
using PwRemake.Sim.Core;

namespace PwRemake.Tests;

[TestClass]
public static class CoopWorldTests
{
    static IEnumerable<(string Cfg, int Id)> All() =>
        CoopMapsTests.BaseMissions.Select(i => ("base", i)).Concat(CoopMapsTests.MirageMissions.Select(i => ("mirage", i)))
            .Where(m => Environment.GetEnvironmentVariable("PW_COOP_MISSIONS") is not { Length: > 0 } f || f.Split(',').Contains(m.Item2.ToString()))
            .Where(m => Environment.GetEnvironmentVariable("PW_COOP_CFG") is not { Length: > 0 } g || g.Split(',').Contains(m.Item1));

    static MissionSession Session(GameStart start, bool brains = false)
    {
        var c = CampaignTests.LoadCfgPublic(start.Configuration)!;
        return MissionSession.Create(start, c.Config, new SessionOptions
        {
            Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = !brains, NoTriggers = !brains,
            Presenter = brains && start.Seats != null ? new LockstepPresenter(start) : null,
        });
    }
    static GameStart Single(TestContext t, string cfg, int id)
    {
        var c = CampaignTests.LoadCfgPublic(cfg);
        if (c == null) t.Skip($"configuration {cfg} not found");
        return new GameStart { Configuration = cfg, Mission = id, Difficulty = 1, Seed = 1 };
    }
    static string Triggers(string cfg, int id)
    {
        var c = CampaignTests.LoadCfgPublic(cfg)!;
        var e = MissionEntry.LoadIndex(c.Config.MissionsDir).First(x => x.Id == id);
        var f = Path.Combine(c.Config.MissionsDir, e.Folder, "triggers.lua");
        return File.Exists(f) ? File.ReadAllText(f) : "";
    }

    /// <summary>the walkable parts of the world's ground with every gate closed (8-neighbourhood): a label per nav cell
    /// (0 = blocked), and each label's size</summary>
    static (int[] Lab, int[] Size) Parts(World W)
    {
        var nav = W.Nav; int n = nav.N;
        var lab = new int[n * n]; var sizes = new List<int> { 0 };
        bool Free(int k) => nav.Block[k] == 0 && !nav.GateAt.ContainsKey(k);
        var st = new Stack<int>();
        for (int k0 = 0; k0 < n * n; k0++)
        {
            if (lab[k0] != 0 || !Free(k0)) continue;
            int id = sizes.Count, cnt = 0; sizes.Add(0);
            lab[k0] = id; st.Push(k0);
            while (st.Count > 0)
            {
                int c = st.Pop(), ci = c % n, cj = c / n; cnt++;
                for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
                {
                    int i = ci + di, j = cj + dj;
                    if ((di == 0 && dj == 0) || i < 0 || j < 0 || i >= n || j >= n) continue;
                    int k = j * n + i;
                    if (lab[k] != 0 || !Free(k)) continue;
                    lab[k] = id; st.Push(k);
                }
            }
            sizes[id] = cnt;
        }
        return (lab, sizes.ToArray());
    }

    // ======================================================================================== gameplay of the old map
    public static void GrowthKeepsGameplay(TestContext t)
    {
        var problems = new List<string>();
        foreach (var (cfg, id) in All())
        {
            var def = CoopDefinition.Load(cfg, id);
            if (def == null || def.NoBuild) continue;
            var sp = Session(Single(t, cfg, id));
            var C0 = sp.Campaign; var W0 = sp.World; var md0 = sp.Map.Md!;
            var (lab0, size0) = Parts(W0);
            foreach (int seats in new[] { 2, 3 })
            {
                var start = CoopTests.CoopStart(t, cfg, id, new[] { ("human", ""), ("human", ""), ("ai", "") }.Take(seats).ToArray());
                var co = Session(start);
                var C1 = co.Campaign; var W1 = co.World; var md1 = co.Map.Md!;
                var (lab1, _) = Parts(W1);
                string w = $"{cfg} {id:00} ({seats} seats)";
                // (the old map's ground on a 4 m grid, where single player's and the co-op world's ground are the same)
                var pairs = new Dictionary<int, Dictionary<int, int>>();
                var at = new Dictionary<int, (double X, double Z)>();
                var water = new Dictionary<int, Dictionary<int, int>>();
                double x0 = md0.OffX + 40, x1 = md0.OffX + (md0.Hx - 1) * 2 - 40, z0 = md0.OffY + 40, z1 = md0.OffY + (md0.Hy - 1) * 2 - 40;
                for (double mz = z0; mz <= z1; mz += 4)
                    for (double mx = x0; mx <= x1; mx += 4)
                    {
                        double h0 = md0.HeightAtMap(mx, mz), h1 = md1.HeightAtMap(mx, mz);
                        var (ax, az) = C0.ToGame(mx, mz); var (bx, bz) = C1.ToGame(mx, mz);
                        int k0 = W0.Nav.Idx(ax, az), k1 = W1.Nav.Idx(bx, bz);
                        if (Math.Abs(h1 - h0) < 0.05 && lab0[k0] != 0 && lab1[k1] != 0 && size0[lab0[k0]] >= 400)
                        {
                            var d = pairs.TryGetValue(lab1[k1], out var dd) ? dd : pairs[lab1[k1]] = new Dictionary<int, int>();
                            d[lab0[k0]] = d.GetValueOrDefault(lab0[k0]) + 1;
                            at.TryAdd(lab0[k0], (mx, mz));
                        }
                        // the open water (deep enough for ships before and after)
                        if (W0.WaterNav is { Region: { } r0 } wn0 && W1.WaterNav is { Region: { } r1 } wn1 && h0 < md0.Water - 1.5 && h1 < md1.Water - 1.5)
                        {
                            int q0 = wn0.Idx(ax, az), q1 = wn1.Idx(bx, bz);
                            if (wn0.Block[q0] != 0 || wn1.Block[q1] != 0 || r0[q0] < 0 || r1[q1] < 0) continue;
                            var d = water.TryGetValue(r0[q0], out var dd) ? dd : water[r0[q0]] = new Dictionary<int, int>();
                            d[r1[q1]] = d.GetValueOrDefault(r1[q1]) + 1;
                        }
                    }
                // (a small part with nothing of the mission on it - a strip of beach below a cliff - may join: no way leads
                // on from it)
                var occupied = new HashSet<int>();
                foreach (var e in W0.Buildings.Cast<Entity>().Concat(W0.Units).Where(e => e.Alive && e.Owner != null))
                    if (lab0[W0.Nav.Idx(e.Pos.X, e.Pos.Z)] is int l0 && l0 > 0) occupied.Add(l0);
                foreach (var (c1, d) in pairs)
                {
                    var big = d.Where(x => x.Value >= 40 && (size0[x.Key] >= 1500 || occupied.Contains(x.Key))).Select(x => x.Key).ToList();
                    if (big.Count > 1)
                        problems.Add($"{w}: the new land joins parts of the old map single player keeps apart: " + string.Join(" + ", big.Select(b => $"the part around ({at[b].X:0}, {at[b].Z:0}) ({d[b]} samples)")));
                }
                foreach (var (r0, d) in water)
                {
                    // (a filled bay's rest may be cut off; a sea of 16000 m2 or more is a way for ships)
                    var big = d.Where(x => x.Value >= 1000).OrderByDescending(x => x.Value).ToList();
                    if (big.Count > 1) problems.Add($"{w}: the old map's open water {r0} is cut into {big.Count} parts ({string.Join(", ", big.Select(b => b.Value))} samples)");
                }
                if (Environment.GetEnvironmentVariable("PW_COOP_VERBOSE") == "1") t.Log($"{w}: {pairs.Count} co-op parts over the old ground, {water.Count} waters");
            }
        }
        foreach (var p in problems) t.Log(p);
        t.Equal(0, problems.Count, "gameplay problems of the grown maps (listed above)");
    }

    // ======================================================================================== the enemy additions
    public static void EnemyAdditions(TestContext t)
    {
        var problems = new List<string>();
        int checkedN = 0;
        foreach (var (cfg, id) in All())
        {
            var def = CoopDefinition.Load(cfg, id);
            if (def == null || def.Enemies.Count == 0) continue;
            var trig = Triggers(cfg, id);
            foreach (int seats in new[] { 2, 3 })
            {
                var start = CoopTests.CoopStart(t, cfg, id, new[] { ("human", ""), ("human", ""), ("ai", "") }.Take(seats).ToArray());
                var s = Session(start);
                var C = s.Campaign; var W = s.World; var R = C.Coop!;
                string w = $"{cfg} {id:00} ({seats} seats)";
                var inUse = def.Enemies.Where(e => e.Seats <= seats).ToList();
                if (R.Enemies.List.Count != inUse.Count) problems.Add($"{w}: {inUse.Count} additions for {seats} seats, {R.Enemies.List.Count} made");
                var human = C.Human!;
                // (a hero phase: the additions come when the bases are handed out - checked as they stand then)
                if (R.HeroPhase) R.HandOver();
                // (seat 1's ground: by land, enemy gates closed; and every seat's - its waves walk to the seats)
                var nav = W.Nav; int n = nav.N;
                bool Free(int k) => nav.Block[k] == 0 && (!nav.GateAt.TryGetValue(k, out var g) || g.Owner == null || !human.IsEnemy(g.Owner));
                bool[] FloodFrom(IEnumerable<(double X, double Z)> froms)
                {
                    var seen = new bool[n * n];
                    var st = new Stack<int>();
                    foreach (var (fx, fz) in froms)
                    {
                        int k0 = nav.Idx(fx, fz);
                        for (int r = 1; r < 8 && !Free(k0); r++) k0 = nav.Idx(fx + 2 * r, fz);
                        if (!seen[k0]) { seen[k0] = true; st.Push(k0); }
                    }
                    while (st.Count > 0)
                    {
                        int c = st.Pop(), ci = c % n, cj = c / n;
                        for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
                        {
                            int i = ci + di, j = cj + dj; if (i < 0 || j < 0 || i >= n || j >= n) continue;
                            int k = j * n + i; if (seen[k] || !Free(k)) continue; seen[k] = true; st.Push(k);
                        }
                    }
                    return seen;
                }
                var sl1 = C.StartLocations[human.Id]!;
                var reach = FloodFrom(new[] { (sl1.X, sl1.Z) });
                var reachSeats = FloodFrom(R.Seats.Select(x => C.StartLocations[x.Slot]).Where(x => x != null).Select(x => (x!.X, x.Z)));
                bool Reached(Entity e) => ReachedIn(e, reach);
                bool ReachedSeats(Entity e) => ReachedIn(e, reachSeats);
                bool ReachedIn(Entity e, bool[] reach)
                {
                    for (double r = e.Radius + 2; r <= e.Radius + 14; r += 2)
                        for (int k = 0; k < 16; k++)
                        {
                            var (sn, cs) = PwRemake.Sim.Maps.MapExtension.SinCos(k * Math.PI / 8);
                            int q = nav.Idx(e.Pos.X + cs * r, e.Pos.Z + sn * r);
                            if (reach[q]) return true;
                        }
                    return false;
                }
                foreach (var e in R.Enemies.List)
                {
                    var d = e.Def; string we = $"{w}: {d}";
                    checkedN++;
                    var blds = e.Placed.OfType<Building>().ToList(); var units = e.Placed.OfType<Unit>().ToList();
                    // (the Dustriders' main "building" is a vehicle, their resource collector)
                    int want = (d.Preset != null ? CoopEnemyDef.PresetClasses(d.Preset, e.Player.Tribe) : new List<string>()).Concat(d.Buildings.Select(b => b.Cls))
                        .Count(cl => C.D.Info(cl)?.Type == "BLDG");
                    if (blds.Count < want) problems.Add($"{we}: {blds.Count} of {want} buildings placed");
                    if (units.Count < d.Army.Sum(a => a.Count)) problems.Add($"{we}: {units.Count} units placed");
                    var (ax, az) = C.ToGame(d.X, d.Z);
                    foreach (var b in blds)
                    {
                        if (b.Owner != e.Player) problems.Add($"{we}: {b.Name} owned by {b.Owner?.Id}");
                        double dd = Math.Sqrt((b.Pos.X - ax) * (b.Pos.X - ax) + (b.Pos.Z - az) * (b.Pos.Z - az));
                        if (dd > d.R + 60) problems.Add($"{we}: {b.Name} {dd:0} m from its place");
                        if (s.Map.Water is double wl && s.Map.Height(b.Pos.X, b.Pos.Z) < wl + 0.3 && b.Def?.Coastal != true) problems.Add($"{we}: {b.Name} in the water");
                    }
                    if (d.New)
                    {
                        if (C.Data.Players.Any(p => p != null && p["present"].Bool() && p["id"].Int() == e.Slot) || R.IsSeatSlot(e.Slot)) problems.Add($"{we}: slot {e.Slot} is the mission's or a seat's");
                        if (e.Like != null)
                            foreach (var q in W.Players.Where(q => q != e.Player && q != e.Like && !R.Enemies.List.Any(o => o.Player == q)))
                                if (e.Player.Relation(q) != e.Like.Relation(q) || q.Relation(e.Player) != q.Relation(e.Like)) { problems.Add($"{we}: stands to player {q.Id} not as player {e.Like.Id} does"); break; }
                        // no trigger names its slot, no "anybody" count of a region covers it
                        if (Regex.IsMatch(trig, $@"(obj_owner|player_id|owner|player) = {e.Slot}\b")) problems.Add($"{we}: the triggers name its slot {e.Slot}");
                        foreach (var r in C.Regions.List)
                            if (r.Contains(ax, az) && Regex.IsMatch(trig, $@"Region\{{[^}}]*rgn_guid = ""{Regex.Escape(r.Guid)}""[^}}]*obj_owner = -1[^}}]*obj_class = ""NA""", RegexOptions.Singleline))
                                problems.Add($"{we}: stands in region {r.Name}, which a trigger counts for anybody");
                        if (d.Brain && d.Attack && e.Brain == null) problems.Add($"{we}: no brain");
                        // (its waves walk: its camp is on the seats' land)
                        bool navalM = def.Raw["naval"] is System.Text.Json.Nodes.JsonValue nv2 && nv2.GetValue<bool>();
                        if (d.Attack && !navalM && blds.Count > 0 && !blds.Any(ReachedSeats)) problems.Add($"{we}: its camp cannot be reached by land from the seats (its waves could not walk)");
                    }
                    else
                    {
                        // the owner's objectives count these: seat 1 must be able to get at them
                        bool naval = def.Raw["naval"] is System.Text.Json.Nodes.JsonValue nv && nv.GetValue<bool>();
                        foreach (var b in blds) if (!naval && !Reached(b)) problems.Add($"{we}: {b.Name} at {C.ToMap(b.Pos.X, b.Pos.Z)} cannot be reached by seat 1 (its owner's objectives count it)");
                    }
                }
            }
        }
        foreach (var p in problems) t.Log(p);
        t.True(checkedN > 0, "additions checked");
        t.Equal(0, problems.Count, "enemy addition problems (listed above)");
    }

    // ======================================================================================== a wave that cannot walk
    /// <summary>A new enemy camp whose land is not its seat's (World.LandIsland) still sends its waves (docs/COOP.md
    /// "Waves that cannot walk"): base 06 with the raiders' cove moved onto the islet south of seat 1 (the keep zone at
    /// (1330, 710): old land, no new land joins it) - the wave goes by boat (transports spawned with it, a scripted
    /// landing), lands on seat 3's land and walks on. (Before: the brain's ferry found no transport and the wave never
    /// left.)</summary>
    public static void WaveCannotWalk(TestContext t)
    {
        var src = File.ReadAllText(Path.Combine(CoopMapsTests.CoopRoot(), "common", "06.lua"));
        var lua = src.Replace("at = { x = 1785, z = 855, r = 40 }", "at = { x = 1330, z = 740, r = 30 }");
        t.True(lua != src, "06.lua has the cove at (1785, 855)");
        var start = CoopTests.CoopStartWith(t, "base", 6, lua, new[] { ("human", ""), ("ai", ""), ("ai", "") }, coopDifficulty: 1);
        var s = Session(start, brains: true);
        var C = s.Campaign; var W = s.World; var R = C.Coop!;
        var e = R.Enemies.List.Single(x => x.Def.New);
        var seat3 = R.Seats.Single(x => x.Seat == 3);
        var sl = C.StartLocations[seat3.Slot]!;
        int camp = W.LandIsland(e.X, e.Z), land3 = W.LandIslandNear(sl.X, sl.Z, 24);
        t.True(camp >= 0 && land3 >= 0 && camp != land3, $"the camp's land ({camp}) is not seat 3's ({land3})");
        s.Run(5);
        e.NextWave = W.Time;
        s.Run(2);
        t.Equal(1, e.Waves, "waves called");
        t.Equal(1, e.Launched, "waves launched");
        t.Equal("boat", e.Way, "the wave's way");
        var ships = W.Units.Where(u => u.Alive && u.Owner == e.Player && u.Naval).ToList();
        t.True(ships.Count > 0 && ships.Any(x => (x.Passengers?.Count ?? 0) > 0), $"transports with the wave aboard ({ships.Count})");
        // it lands on seat 3's land
        bool landed = false;
        for (int k = 0; k < 40 && !landed; k++)
        {
            s.Run(5);
            landed = W.Units.Any(u => u.Alive && u.Owner == e.Player && !u.Naval && !u.Inside && W.LandIsland(u.Pos.X, u.Pos.Z) == land3);
        }
        t.True(landed, $"the wave came ashore on seat 3's land by {W.Time:0} s");
    }

    // ======================================================================================== a short game of each mission
    /// <summary>PW_COOP_SMOKE_SECONDS (default 150): how long each game runs before the waves are called</summary>
    public static void CoopSmoke(TestContext t)
    {
        double secs = double.TryParse(Environment.GetEnvironmentVariable("PW_COOP_SMOKE_SECONDS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 150;
        var problems = new List<string>();
        var cfgs = (Environment.GetEnvironmentVariable("PW_COOP_SMOKE_CFG") ?? "base,mirage").Split(',');
        foreach (var (cfg, id) in All().Where(m => cfgs.Contains(m.Cfg)))
        {
            if (CoopDefinition.Load(cfg, id) == null) continue;
            foreach (int seats in new[] { 3, 2 })
            {
                string w = $"{cfg} {id:00} ({seats} seats)";
                try
                {
                    var start = CoopTests.CoopStart(t, cfg, id, new[] { ("human", ""), ("ai", ""), ("ai", "") }.Take(seats).ToArray(), coopDifficulty: 1);
                    var s = Session(start, brains: true);
                    var R = s.Campaign.Coop!; var W = s.World;
                    foreach (var pr in s.Problems) problems.Add($"{w}: load problem: {pr}");
                    var before = R.Enemies.List.ToDictionary(e => e, e => (Res: e.Player["food"] + e.Player["wood"] + e.Player["stone"], Units: W.Units.Count(u => u.Alive && u.Owner == e.Player), Blds: W.Buildings.Count(b => b.Owner == e.Player)));
                    var seatBefore = R.Seats.Where(x => x.Seat >= 2).ToDictionary(x => x, x => W.Buildings.Where(b => b.Owner == x.Player).ToHashSet());
                    s.Run(secs);
                    // every AI commander seat with a base builds on it (mission 13's compound edge left one with its main
                    // building alone): new buildings or construction sites
                    // (a hero phase still on: no bases yet)
                    if (!R.Def.NoBuild && !R.HeroPhase)
                        foreach (var x in R.Seats.Where(x => x.Seat >= 2 && x.Ai))
                        {
                            int now = W.Buildings.Count(b => b.Owner == x.Player), built = W.Buildings.Count(b => b.Owner == x.Player && !seatBefore[x].Contains(b));
                            t.Log($"{w}: seat {x.Seat} ({x.Tribe}, AI) buildings {seatBefore[x].Count} -> {now}, {built} new");
                            if (Environment.GetEnvironmentVariable("PW_COOP_VERBOSE") == "1")
                                t.Log($"    {string.Join(", ", W.Buildings.Where(b => b.Owner == x.Player).GroupBy(b => b.Name + (b.Built ? "" : " (site)")).Select(g => $"{g.Key} x{g.Count()}"))}; units {W.Units.Count(u => u.Alive && u.Owner == x.Player)}; res {x.Player["food"]:0}/{x.Player["wood"]:0}/{x.Player["stone"]:0}; commander {x.Commander?.ToString() ?? "-"}");
                            if (built == 0) problems.Add($"{w}: seat {x.Seat} ({x.Tribe}, AI commander) built nothing in {secs:0} s ({now} buildings)");
                        }
                    // the new enemies have built (or trained, or spent on it) by now; then their waves are called
                    foreach (var e in R.Enemies.List.Where(e => e.Def.New && e.Brain != null))
                    {
                        var b = before[e];
                        double res = e.Player["food"] + e.Player["wood"] + e.Player["stone"];
                        int units = W.Units.Count(u => u.Alive && u.Owner == e.Player), blds = W.Buildings.Count(x => x.Owner == e.Player);
                        if (!(res < b.Res - 1 || units > b.Units || blds > b.Blds)) problems.Add($"{w}: {e.Def}: built nothing in {secs:0} s (resources {res:0}, units {units}, buildings {blds})");
                        e.NextWave = W.Time;
                    }
                    s.Run(20);
                    // (an enemy still at peace with every seat - its `like` is neutral until the story turns it - sends none)
                    foreach (var e in R.Enemies.List.Where(e => e.Def.New && e.Def.Attack && e.Brain != null && R.Seats.Any(x => e.Player.IsEnemy(x.Player))))
                        if (e.Launched == 0) problems.Add($"{w}: {e.Def}: no wave launched ({e.Waves} called)");
                    if (s.Engine!.Errors.Count > 0) problems.Add($"{w}: trigger errors: {string.Join("; ", s.Engine.Errors.Take(3))}");
                    t.Log($"{w}: {W.Time:0} s, enemies {string.Join(", ", R.Enemies.List.Select(e => $"{e.Slot}:{e.Waves}/{e.Launched}{(e.Brain != null ? "" : " no brain")}{(R.Seats.Any(x => e.Player.IsEnemy(x.Player)) ? "" : " at peace")}"))}{(s.Over ? " (over)" : "")}");
                }
                catch (Exception e) { problems.Add($"{w}: {e.GetType().Name}: {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()}"); }
            }
        }
        foreach (var p in problems) t.Log(p);
        t.Equal(0, problems.Count, "co-op smoke problems (listed above)");
    }
}
