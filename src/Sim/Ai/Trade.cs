// The computer player's trade route (goals/AiGoalManageMarketPlaces.usl; docs/spec/ai_group6.md §3): a market at
// home and one at an outpost 150-250 m away, towers along the way, carts shuttling between them.
//
//   * Only a Schnecke on a skirmish map from epoch 2, when the map's AI options allow a market outpost
//     ("markplace_outpost"; EM:527-537). SEAS has no cart (its steelwork trades nothing): no route.
//   * The place (MP:279-410): an outpost or a stone deposit 150-250 m from the start location, else a point of a ring
//     there, the farther the better; the way there must exist, end within 8 m of it, be at most 350 m long, and its
//     security points - every 60 m beyond the first 60 - lie away from enemies (64 m) and hunting animals (32 m).
//     Then an EmptyOutpost there (Outposts.cs). Five failures: no trade.
//   * A small tower at every security point that has none within 68 m; a market at home and at the outpost.
//   * Trading: every third think (about 30-40 s) one more cart - an idle one of the village, else trained at the
//     home market - at most 6 (the original has no limit: its carts would starve the army of the remake, whose carts
//     really earn); idle carts go to the market farther from them. With a cart the route is "ready" (the outposts'
//     fighters go home).
//   * A damaged outpost market calls a quick attack on the enemies at it (the original's has no targets: MP:793-818).
//
// Ported from remake/src/game/ai/trade.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.World;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Ai;

/// <summary>a point the trade route builds at (a security point, the home market's place)</summary>
public sealed class TradeSite : IAiSite
{
    public Pt Pos { get; set; }
    public string Id = "";
}

/// <summary>the trade goal's state</summary>
public sealed class TradeState
{
    public string State = "planning";
    public int Tries, N;
    public Outpost? Outpost;
    public List<TradeSite> Sec = new();
}

public sealed partial class TribeAI
{
    static readonly double[] R_ROUTE = { 150, 250 };
    const double MAX_PATH = 350, SEC = 60;
    const int MAX_CARTS = 6;
    static readonly Dictionary<string, string> MARKET = new() { ["Hu"] = "hu_warehouse", ["Aje"] = "aje_bazaar", ["Ninigi"] = "ninigi_warehouse" };
    static readonly Dictionary<string, string> CART = new() { ["Hu"] = "hu_cart", ["Aje"] = "aje_trade_dino", ["Ninigi"] = "ninigi_cart" };
    static readonly Dictionary<string, string> TRADE_TOWER = new() { ["Hu"] = "hu_small_tower", ["Aje"] = "aje_small_tower", ["Ninigi"] = "ninigi_small_tower" };

    public TradeState? Trade;
    /// <summary>a cart runs the route: the outposts' fighters go home</summary>
    public bool TradeReady;

    bool TradeActive(Census S) =>
        Multimap && Personality == "Schnecke" && S.Age >= 2 && MapOptions.MarkplaceOutpost && MARKET.ContainsKey(P.Tribe) && Start != null && !EconomyOff &&
        GameType == "Deathmatch";                                           // market places only in Deathmatch (EM:527-537)
    void TradeThink(Census S)
    {
        if (!TradeActive(S)) return;
        var p = P;
        var T = Trade ??= new TradeState();
        if (T.State == "off") return;
        if (T.State == "planning")
        {
            var r = PlanTradeRoute(S);
            int? id = r != null ? StartOutpost(r.Value.Pos, "EmptyOutpost") : null;
            if (id == null) { if (++T.Tries >= 5) T.State = "off"; return; }
            T.Outpost = Outposts.FirstOrDefault(o => o.Id == id);
            T.Sec = r!.Value.Sec.Select((pt, i) => new TradeSite { Pos = pt, Id = "sec" + i }).ToList();
            T.State = "building";
            return;
        }
        var op = T.Outpost;
        if (op == null) { T.State = "planning"; return; }
        T.N++;
        string mk = MARKET[p.Tribe], cart = CART[p.Tribe];
        var st = Start!.Value;
        var markets = S.Blds.Where(b => b.Name == mk).ToList();
        var homeM = markets.FirstOrDefault(b => st.Dist(b) < 90);
        var farM = markets.FirstOrDefault(b => op.Pos.Dist(b) <= 60);
        // towers along the way, the markets
        foreach (var s in T.Sec) if (!S.Blds.Any(b => b.Name == TRADE_TOWER[p.Tribe] && s.Pos.Dist(b) < 68)) AddOnTop("BLDG/" + TRADE_TOWER[p.Tribe], "", pos: s.Pos, outpost: s);
        if (homeM == null) AddOnTop("BLDG/" + mk, "", pos: st, outpost: new TradeSite { Pos = st, Id = "home" });
        if (farM == null) AddOnTop("BLDG/" + mk, "", pos: op.Pos, outpost: op);
        T.State = homeM != null && homeM.Built && farM != null && farM.Built ? "trading" : "building";
        if (farM != null && farM.Built && farM.Hp < farM.MaxHp * 0.9 && !(W.LastHitOf(farM) != -99 && W.Time - W.LastHitOf(farM) > 20))
        {
            var foes = WallFoes(farM.Pos.X, farM.Pos.Z, 30);
            if (foes.Count > 0) RequestAttack(S, "quick", foes[0].Owner, foes, Pt.Of(farM));
        }
        if (T.State != "trading") return;
        // the carts
        var carts = S.Units.Where(u => u.Name == cart && u.Alive).ToList();
        if (carts.Count > 0) TradeReady = true;
        if (T.N % 3 == 0 && carts.Count < MAX_CARTS && !homeM!.Queue.Any(q => q.Action.Results.Count > 0 && q.Action.Results[0].Obj == cart))
        {
            var X = TrainableOf(S, cart);
            if (X != null && X.Producer != null && CanPay(X.Action.Cost, p.Res)) Hands.QueueAction(p, X.Producer, X.Action);
        }
        foreach (var c in carts)
        {
            if (c.Task.Type != "idle" || c.Inside) continue;
            var to = DistE(c, homeM!) > DistE(c, farM!) ? homeM! : farM!;
            c.TradeHome = to == homeM ? farM : homeM;
            Hands.Order(p, new[] { c }, new Order("trade") { Target = to, Auto = true });
        }
    }
    /// <summary>the outpost's place and the route's security points -> {pos, sec} | null</summary>
    (Pt Pos, List<Pt> Sec)? PlanTradeRoute(Census S)
    {
        var st = Start!.Value;
        double Dist(Pt q) => st.Dist(q);
        var cands = new List<(double X, double Z, double V)>();
        foreach (var o in Outposts) if (Dist(o.Pos) >= R_ROUTE[0] && Dist(o.Pos) <= R_ROUTE[1]) cands.Add((o.Pos.X, o.Pos.Z, 2));
        foreach (var n in W.Resources) if (n.Alive && n.Res == "stone" && !n.Water && Dist(Pt.Of(n)) >= R_ROUTE[0] && Dist(Pt.Of(n)) <= R_ROUTE[1]) cands.Add((n.Pos.X, n.Pos.Z, 1.5));
        for (double r = R_ROUTE[0]; r <= R_ROUTE[1]; r += 25) for (int k = 0; k < 24; k++)
        {
            double x = st.X + SMath.Cos(k / 24.0 * 6.2832) * r, z = st.Z + SMath.Sin(k / 24.0 * 6.2832) * r;
            if (W.Nav.IsFree(x, z)) cands.Add((x, z, 0.2 + 0.8 * (r - R_ROUTE[0]) / (R_ROUTE[1] - R_ROUTE[0])));
        }
        cands = cands.OrderByDescending(c => c.V).ToList();
        int tried = 0;
        foreach (var c in cands)
        {
            if (tried++ > 40) break;
            if (TradeDanger(new Pt(c.X, c.Z), 64, 32)) continue;
            var r = TradeRoute(new Pt(c.X, c.Z));
            if (r != null) return r;
        }
        return null;
    }
    bool TradeDanger(Pt q, double rEnemy, double rBeast)
    {
        foreach (var u in W.Units)
        {
            if (!u.Alive || u.Inside) continue;
            double d = q.Dist(u);
            if ((d <= rEnemy && IsEnemyEntity(u) && !u.IsWorker) || (d <= rBeast && u.Wild && u.Def!.Aggressive == 1)) return true;
        }
        foreach (var b in W.Buildings) if (IsEnemyEntity(b) && q.Dist(b) <= rEnemy) return true;
        return false;
    }
    /// <summary>CheckRoute: the way, its length, its security points</summary>
    (Pt Pos, List<Pt> Sec)? TradeRoute(Pt c)
    {
        var st = Start!.Value; var nav = W.Nav;
        var path = nav.Find(st.X, st.Z, c.X, c.Z, 0.7, 30000, P);
        if (!nav.Found || path == null || path.Count == 0) return null;
        var last = path[^1];
        if (Hyp(last.X - c.X, last.Z - c.Z) > 8) return null;
        double len = 0, px = st.X, pz = st.Z, next = SEC * 2;
        var sec = new List<Pt>();
        foreach (var wp in path)
        {
            double x = wp.X, z = wp.Z, d = Hyp(x - px, z - pz);
            // the points every 60 m beyond the first 60 m
            while (len + d >= next) { double f = (next - len) / d; sec.Add(new Pt(px + (x - px) * f, pz + (z - pz) * f)); next += SEC; }
            len += d; px = x; pz = z;
        }
        if (len > MAX_PATH) return null;
        if (sec.Any(q => TradeDanger(q, 64, 32))) return null;
        return (c, sec);
    }
}
