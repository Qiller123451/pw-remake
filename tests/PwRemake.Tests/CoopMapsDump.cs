// A design aid for the co-op definitions (not a check): PW_COOP_DUMP=<dir> dotnet run -- CoopMapsDump writes, per mission
// (PW_COOP_MISSIONS) and seat count (PW_COOP_DUMP_SEATS = "0,2,3"; 0 = the single player map), the map as the game
// builds it - heights, walk grid, the ground seat 1 reaches (enemy gates closed) - and its objects, players, regions and
// seats as JSON. tools/coop/render_dump.py draws it (a top-down picture with a 100 m grid in the original map's
// coordinates).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Game;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class CoopMapsDump
{
    public static void Dump(TestContext t)
    {
        var dir = Environment.GetEnvironmentVariable("PW_COOP_DUMP");
        if (string.IsNullOrEmpty(dir)) t.Skip("PW_COOP_DUMP=<dir> runs it");
        Directory.CreateDirectory(dir!);
        var only = Environment.GetEnvironmentVariable("PW_COOP_MISSIONS");
        var seatList = (Environment.GetEnvironmentVariable("PW_COOP_DUMP_SEATS") ?? "0,3").Split(',').Select(int.Parse).ToArray();
        var cfgs = (Environment.GetEnvironmentVariable("PW_COOP_DUMP_CFG") ?? "base").Split(',');
        foreach (var cfg in cfgs)
            foreach (var id in cfg == "base" ? CoopMapsTests.BaseMissions : CoopMapsTests.MirageMissions)
            {
                if (only is { Length: > 0 } && !only.Split(',').Contains(id.ToString())) continue;
                foreach (int seats in seatList)
                {
                    try { One(t, dir!, cfg, id, seats); }
                    catch (Exception e) { t.Log($"{cfg} {id} {seats}: {e}"); }
                }
            }
    }

    static void One(TestContext t, string dir, string cfg, int id, int seats)
    {
        var c = CampaignTests.LoadCfgPublic(cfg)!;
        var gs = new GameStart { Configuration = cfg, Mission = id, Seed = 1, Difficulty = 1 };
        if (seats > 0) gs = CoopTests.CoopStart(t, cfg, id, new[] { ("human", ""), ("human", ""), ("ai", "") }.Take(seats).ToArray());
        var s = MissionSession.Create(gs, c.Config, new SessionOptions
            { Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = true, NoTriggers = true });
        var C = s.Campaign; var W = s.World; var nav = W.Nav; var md = s.Map.Md!; var hu = C.Human!;
        string name = $"{cfg}_{id:00}_s{seats}";
        // heights
        using (var fs = File.Create(Path.Combine(dir, name + ".h")))
        {
            var buf = new byte[md.Heights.Length * 4];
            Buffer.BlockCopy(md.Heights, 0, buf, 0, buf.Length);
            fs.Write(buf);
        }
        // per height sample: walk (old grid), nav blocked, reachable from seat 1, material
        int n = nav.N;
        bool closedAll = false;
        bool Free(int k) => nav.Block[k] == 0 && (!nav.GateAt.TryGetValue(k, out var g) || (closedAll ? g.Owner == hu : g.Owner == null || !hu.IsEnemy(g.Owner)));
        var sl = C.StartLocations[hu.Id];
        (double X, double Z) st = sl != null ? (sl.X, sl.Z) : (s.StartCamera.X, s.StartCamera.Z);
        bool[] Flood()
        {
            var reach = new bool[n * n];
            int k0 = -1; double bd = 1e18;
            for (double dz = -12; dz <= 12; dz += 2) for (double dx = -12; dx <= 12; dx += 2) { int k = nav.Idx(st.X + dx, st.Z + dz); if (Free(k) && dx * dx + dz * dz < bd) { bd = dx * dx + dz * dz; k0 = k; } }
            if (k0 >= 0)
            {
                var stack = new Stack<int>(); stack.Push(k0); reach[k0] = true;
                while (stack.Count > 0)
                {
                    int cc = stack.Pop(), ci = cc % n, cj = cc / n;
                    for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
                    {
                        int i = ci + di, j = cj + dj; if ((di == 0 && dj == 0) || i < 0 || j < 0 || i >= n || j >= n) continue;
                        int k = j * n + i; if (reach[k] || !Free(k) || (di != 0 && dj != 0 && (!Free(cj * n + i) || !Free(j * n + ci)))) continue;
                        reach[k] = true; stack.Push(k);
                    }
                }
            }
            return reach;
        }
        var reach = Flood();
        closedAll = true;
        var reachClosed = Flood();
        var flags = new byte[md.Hx * md.Hy];
        for (int j = 0; j < md.Hy; j++)
            for (int i = 0; i < md.Hx; i++)
            {
                double mx = md.OffX + 2.0 * i, my = md.OffY + 2.0 * j;
                var (gx, gz) = C.ToGame(mx, my);
                byte f = 0;
                if (gx > -s.Map.Size / 2 && gz > -s.Map.Size / 2 && gx < s.Map.Size / 2 && gz < s.Map.Size / 2)
                {
                    int k = nav.Idx(gx, gz);
                    if (nav.Block[k] != 0) f |= 1;
                    if (reach[k]) f |= 2;
                    if (reachClosed[k]) f |= 8;
                    if (nav.GateAt.ContainsKey(k)) f |= 4;
                }
                int m = md.MaterialAtMap(mx, my); f |= (byte)((Math.Max(0, m) & 7) << 4);
                flags[j * md.Hx + i] = f;
            }
        File.WriteAllBytes(Path.Combine(dir, name + ".f"), flags);
        // objects
        var o = new JsonObject
        {
            ["cfg"] = cfg, ["id"] = id, ["seats"] = seats, ["hx"] = md.Hx, ["hy"] = md.Hy, ["offx"] = md.OffX, ["offy"] = md.OffY, ["water"] = md.Water,
            ["setting"] = md.SettingName ?? md.Setting, ["human"] = hu.Id,
        };
        JsonArray Pt(double x, double z) { var (mx, my) = C.ToMap(x, z); return new JsonArray(Math.Round(mx, 1), Math.Round(my, 1)); }
        var players = new JsonArray();
        foreach (var p in W.Players)
            players.Add(new JsonObject { ["id"] = p.Id, ["tribe"] = p.Tribe, ["name"] = p.Name, ["rel"] = p == hu ? "me" : hu.IsEnemy(p) ? "enemy" : hu.IsFriend(p) ? "ally" : "neutral", ["ai"] = p.Ai,
                ["seat"] = C.Coop?.SeatOfSlot(p.Id)?.Seat ?? 0 });
        o["players"] = players;
        var bl = new JsonArray();
        foreach (var b in W.Buildings.Where(b => b.Alive))
            bl.Add(new JsonObject { ["c"] = b.Name, ["o"] = b.Owner?.Id ?? -1, ["p"] = Pt(b.Pos.X, b.Pos.Z), ["r"] = Math.Round(b.Radius, 1), ["gate"] = b.Def?.Gate == true, ["wall"] = b.Def?.WallKind ?? "" });
        o["buildings"] = bl;
        var ul = new JsonArray();
        foreach (var u in W.Units.Where(u => u.Alive && u.Owner != null))
            ul.Add(new JsonObject { ["c"] = u.Name, ["o"] = u.Owner!.Id, ["p"] = Pt(u.Pos.X, u.Pos.Z) });
        o["units"] = ul;
        var rl = new JsonArray();
        foreach (var r in W.Resources.Where(r => r.Alive))
            rl.Add(new JsonArray(r.Type, Math.Round(C.ToMap(r.Pos.X, r.Pos.Z).MX, 1), Math.Round(C.ToMap(r.Pos.X, r.Pos.Z).MY, 1)));
        o["resources"] = rl;
        var sls = new JsonArray();
        for (int k = 0; k < C.StartLocations.Length; k++) if (C.StartLocations[k] is { } q) sls.Add(new JsonObject { ["o"] = k, ["p"] = Pt(q.X, q.Z) });
        o["starts"] = sls;
        var regs = new JsonArray();
        foreach (var r in C.Regions.List)
            regs.Add(new JsonObject { ["name"] = r.Name, ["guid"] = r.Guid, ["shapes"] = new JsonArray(r.Shapes.Select(q => (JsonNode)new JsonArray(q.Oval ? 1 : 0, q.Enabled ? 1 : 0, Math.Round(q.X, 1), Math.Round(q.Y, 1), Math.Round(q.W, 1), Math.Round(q.H, 1))).ToArray()) });
        o["regions"] = regs;
        // (the map's objects the growth protects: MapExtension.ProtectRadius)
        o["protected"] = new JsonArray(md.Objects.Where(q => PwRemake.Sim.Maps.MapExtension.ProtectRadius(q) > 0).Select(q => (JsonNode)new JsonArray((JsonNode)q.Type, (JsonNode?)q.Cls, Math.Round(q.X, 1), Math.Round(q.Y, 1), PwRemake.Sim.Maps.MapExtension.ProtectRadius(q))).ToArray());
        if (md.Extension is { } ext)
        {
            o["stamps"] = new JsonArray(ext.Stamps.Select(q => (JsonNode)new JsonObject { ["seat"] = q.Seat, ["site"] = new JsonArray(q.SiteX, q.SiteZ), ["src"] = new JsonArray(q.SX, q.SZ), ["r"] = q.R, ["rot"] = q.RotDegrees, ["mirror"] = q.Mirror }).ToArray());
            o["log"] = new JsonArray(ext.Log.Select(x => (JsonNode)x).ToArray());
            o["old"] = new JsonArray(ext.OldX0, ext.OldY0, ext.OldX1, ext.OldY1);
        }
        o["problems"] = new JsonArray(s.Problems.Concat(C.Warnings.Where(w => w.StartsWith("co-op", StringComparison.Ordinal))).Select(x => (JsonNode)x).ToArray());
        File.WriteAllText(Path.Combine(dir, name + ".json"), o.ToJsonString());
        t.Log($"{name}: {md.Hx * 2}x{md.Hy * 2} m, {bl.Count} buildings, {ul.Count} units");
    }
}
