// The computer player and items (artefacts; game/systems/items.js). docs/spec/ai_group6.md §6.
//
//   * CheckGetItem (goals/AiGoalDisturbAttack.usl:633-681, every strategist think): on skirmish maps, in the epochs
//     1-3, not while hunting a warp gate, Hard always, Medium one think in 15, Easy never: the free item nearest the
//     start location on the home island ("GetDynamicIslandIdForChar"; World.LandIsland) is fetched.
//   * The GetItem goal (goals/AiGoalGetItem.usl, thinks every 30 + iType ticks): one runner (hu_warrior,
//     aje_spearman, ninigi_archer, seas_warrior with nothing in its hands) from the pool - else the army
//     "FindItem_1" (one runner) is trained - is sent to pick it up; an enemy (unit, building, hostile wild animal)
//     within 60 m of the item stops it (from epoch 3 with a pest patrol to there, Attack.cs RequestPest); one that
//     carries the item walks back to the start location (done within 30 m); 15 checks with empty hands while idle:
//     it gives up and walks back; after 30 checks the order again.
//   * Every attacking squad on a skirmish map: its first unit with empty hands picks up a free item within 30 m of
//     it (tasks/AiTaskAttackObject.usl:1056, 1496-1526).
//
// Ported from remake/src/game/ai/items.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Ai;

/// <summary>the GetItem goal: the item, its runner, the phase</summary>
public sealed class GetItemGoal : IClaimer
{
    public PwRemake.Sim.World.Item Item = null!;
    public Unit? Unit;
    public string Phase = "new";
    public int N, Steps;
    public double T;
    public bool Ordered;
    public Pt Home;
}

public sealed partial class TribeAI
{
    static readonly Dictionary<string, string> RUNNER = new() { ["Hu"] = "hu_warrior", ["Aje"] = "aje_spearman", ["Ninigi"] = "ninigi_archer", ["SEAS"] = "seas_warrior" };

    public GetItemGoal? GetItem;

    /// <summary>DisturbAttack.CheckGetItem</summary>
    void CheckGetItem(Census S)
    {
        if (!Multimap || LevelName == "_MN_MP_2_COLDBLOOD" || S.Age > 3 || Cls == "Easy" || GetItem != null || S.Home == null) return;
        if (Attacks.Any(a => a.Type == "siege_warpgate")) return;
        if (Cls == "Medium" && Rnd(15) != 0) return;
        var h = S.Home.Value;
        int isle = W.LandIsland(h.X, h.Z);
        Item? best = null; double bd = double.PositiveInfinity;
        foreach (var it in W.Items)
        {
            if (!W.ItemLying(it) || W.LandIsland(it.Pos.X, it.Pos.Z) != isle) continue;
            double d = DistP(it, h);
            if (d < bd) { bd = d; best = it; }
        }
        if (best != null) GetItem = new GetItemGoal { Item = best, Phase = "new", T = W.Time };
    }
    /// <summary>the GetItem goal, on the strategist's timer</summary>
    void GetItemThink(Census S)
    {
        var g = GetItem;
        if (g == null) return;
        var u = g.Unit; var it = g.Item;
        void End() { if (u != null) Release(new[] { u }); GetItem = null; }
        if (u != null && (!u.Alive || u.Owner != P)) { End(); return; }
        switch (g.Phase)
        {
            case "new":
            {
                RUNNER.TryGetValue(P.Tribe, out var cls);
                var r = S.Pool.FirstOrDefault(x => x.Name == cls && x.Item == null && !x.Inside && W.CanCarry(x));
                if (r == null)
                {
                    // the army FindItem_1: one runner, trained (CAiTaskBuildSquad); tried again on the next thinks
                    if (!g.Ordered && cls != null) g.Ordered = AddOnTop(cls, "", unique: false);
                    if (W.Time - g.T > 120) End();
                    return;
                }
                g.Unit = r;
                Claim(new[] { r }, g);
                g.Phase = "running";
                goto case "running";              // falls through
            }
            case "running":
            {
                var carrier = it.Carrier;
                if (!it.Alive || (carrier != null && carrier != g.Unit)) { End(); return; }
                var d = ItemDanger(Pt.Of(it), 60);
                if (d != null) { if (S.Age >= 3) RequestPest(d); End(); return; }
                Hands.Order(P, new[] { g.Unit! }, new Order("pickup") { Target = it, Auto = true });
                g.Phase = "checking"; g.N = 0; g.Steps = 0;
                return;
            }
            case "checking":
            {
                if (u!.Item == it) { ItemHome(g); return; }
                var carrier = it.Carrier;
                if (!it.Alive || (carrier != null && carrier != u)) { End(); return; }
                g.N++;
                if (u!.Item == null && u!.Task.Type == "idle" && ++g.Steps >= 15) { ItemHome(g); return; }
                if (g.N > 30) g.Phase = "running";
                return;
            }
            case "home":
                if (DistP(u!, g.Home) < 30) { End(); return; }
                if (u!.Task.Type == "idle") Hands.Order(P, new[] { u }, new Order("move") { X = g.Home.X, Z = g.Home.Z, Auto = true });
                if (W.Time - g.T > 600) End();
                return;
        }
    }
    void ItemHome(GetItemGoal g)
    {
        var S = this.S ?? Census();
        g.Phase = "home";
        g.Home = Start ?? S.Home!.Value;
        Hands.Order(P, new[] { g.Unit! }, new Order("move") { X = g.Home.X, Z = g.Home.Z, Auto = true });
    }
    /// <summary>GetItem.CheckForEnemy (GetItem:283-321): enemy units and buildings and the wild animals within r of p, the
    /// nearest to the start location first; when one of them is an enemy or a hostile animal, the position of that
    /// first one (from epoch 3 the goal asks for a pest patrol there), else null</summary>
    Pt? ItemDanger(Pt p, double r)
    {
        var from = Start ?? p; var list = new List<Entity>();
        bool evil = false;
        foreach (var e in W.Units)
        {
            if (!e.Alive || e.Inside || p.Dist(e) > r) continue;
            if (IsEnemyEntity(e)) { list.Add(e); evil = true; } else if (e.Wild) { list.Add(e); if (e.Def != null && e.Def.Aggressive == 1) evil = true; }
        }
        foreach (var b in W.Buildings) if (IsEnemyEntity(b) && p.Dist(b) <= r) { list.Add(b); evil = true; }
        if (!evil) return null;
        var first = list.Aggregate((a, b) => from.Dist(b) < from.Dist(a) ? b : a);
        return Pt.Of(first);
    }
    /// <summary>AiTaskAttackObject: a squad picks up what lies on its way (skirmish maps)</summary>
    void SquadPickup(List<Unit> us)
    {
        if (!Multimap) return;
        foreach (var u in us)
        {
            if (u.Item != null || !W.CanCarry(u) || u.Task.Type == "pickup") continue;
            var it = W.Items.FirstOrDefault(x => W.ItemLying(x) && DistE(x, u) <= 30);
            if (it != null) Hands.Order(P, new[] { u }, new Order("pickup") { Target = it, Auto = true });
            return;
        }
    }
}
