// The game's statistics (the original's CStatisticMgr samples, docs/spec/ui_groups7_9.md §4): per player what
// it built, collected, lost and destroyed, when it reached each epoch, and every 10 s of game time a sample of its
// units, buildings and resources for the time line. Fed by the world's events (World.Emit); the interface shows it.
//
//   counters per player: food wood stone skulls (collected: what fitted into storage, Player.Gained) - workers,
//   infantry, animals, vehicles, ships (made) - tributeSent, tributeReceived - eco, mil, def (buildings finished, by
//   the build menu's row: ECON / COMB / DEFE; palisades no defence, as FightingObj.usl:3984-3995) - unitsLost,
//   buildingsLost, killed (enemies), razed (enemy buildings), animals (wild animals killed) - epochs [time of 2..5]
// The original counts no skulls (the sample is commented out, FightingObj.usl:3967): the remake does.
//
// Ported from remake/src/game/stats.js (pw-exporter 98c7703). World.GameStats is the JS W.stats.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PwRemake.Sim.World;

/// <summary>the counters of one player (stats.js ZERO)</summary>
public sealed class PlayerStats
{
    public double Food, Wood, Stone, Skulls, TributeSent, TributeReceived;
    public int Workers, Infantry, Animals, Vehicles, Ships, Eco, Mil, Def, UnitsLost, BuildingsLost, Killed, Razed, AnimalKills;
    /// <summary>the game time an epoch (index 2..5) was reached, null = not</summary>
    public double?[] Epochs = new double?[6];
    public PlayerStats Clone() { var c = (PlayerStats)MemberwiseClone(); c.Epochs = (double?[])Epochs.Clone(); return c; }
}

/// <summary>a player's values in a sample of the time line</summary>
public sealed class StatsSample
{
    public int Units, Civil, Military, Buildings;
    public double Food, Wood, Stone, Skulls;
    public double CFood, CWood, CStone, CSkulls;
}

public sealed class GameStats
{
    const double SAMPLE = 10;                    // seconds of game time between two samples of the time line
    static readonly Regex AgeRe = new(@"^age_(\d)$", RegexOptions.Compiled);
    readonly World W;
    /// <summary>player id -> counters</summary>
    public readonly OrderedMap<int, PlayerStats> P = new();
    /// <summary>[{ t, v: { player id: sample } }]</summary>
    public readonly List<(double T, Dictionary<int, StatsSample> V)> Series = new();
    public double NextT;
    /// <summary>building class -> ECON | COMB | DEFE</summary>
    readonly Dictionary<string, string?> _cats = new(StringComparer.Ordinal);

    public GameStats(World w) { W = w; }

    public PlayerStats? Of(Player? p)
    {
        if (p == null) return null;
        var s = P.Get(p.Id);
        if (s == null) { s = new PlayerStats(); P.Set(p.Id, s); }
        return s;
    }
    /// <summary>the build menu row of a building class (the location's uiposition/subcat)</summary>
    string? Cat(Building b)
    {
        if (_cats.TryGetValue(b.Name, out var c)) return c;
        var a = W.Data.Actions(b.Owner).Find(x => x.Kind == "Build" && x.Results.Any(r => r.Obj == b.Name));
        c = a?.Loc?.UiStr("subcat");
        if (string.IsNullOrEmpty(c)) c = null;
        _cats[b.Name] = c;
        return c;
    }
    /// <summary>one world event (World.Emit)</summary>
    public void On(WorldEvent e)
    {
        switch (e.Type)
        {
            case "trained":
            {
                var u = e.U("unit"); var s = u != null && u.CountsInPop ? Of(u.Owner) : null;
                if (s == null) break;
                if (u!.IsWorker) s.Workers++;
                else if (u.Cls == "SHIP" || u.Naval) s.Ships++;
                else if (u.Cls == "CHTR") s.Infantry++;
                else if (u.Cls == "ANML") s.Animals++;
                else s.Vehicles++;
                break;
            }
            case "built":
            {
                var b = e.B("building"); var s = b != null ? Of(b.Owner) : null;
                if (s == null) break;
                var c = Cat(b!);
                if (c == "ECON") s.Eco++; else if (c == "COMB") s.Mil++; else if (c == "DEFE" && !b!.Name.Contains("palisade")) s.Def++;
                break;
            }
            case "died":
            {
                var u = e.E("entity"); var k = e.E("killer")?.Owner;
                if (u?.Owner != null && u is Unit uu && uu.CountsInPop) Of(u.Owner)!.UnitsLost++;
                if (k != null && u != null && k != u.Owner) { if (u.Owner == null && u.Cls == "ANML") Of(k)!.AnimalKills++; else if (u.Owner != null) Of(k)!.Killed++; }
                break;
            }
            case "destroyed":
            {
                var b = e.E("entity"); var k = e.E("killer")?.Owner;
                if (b?.Owner != null && b.Kind == "building") Of(b.Owner)!.BuildingsLost++;
                if (k != null && b?.Owner != null && k != b.Owner) Of(k)!.Razed++;
                break;
            }
            case "upgrade":
            {
                var a = e["action"] as Rules.ActionDef; var m = a != null ? AgeRe.Match(a.Id) : null; var s = m != null && m.Success ? Of(e.P("player")) : null;
                if (s != null) { int i = m!.Groups[1].Value[0] - '0'; if (s.Epochs[i] == null) s.Epochs[i] = W.Time; }
                break;
            }
            case "tribute":
            {
                double n = e.Num("n");
                var f = Of(e.P("from")); var t = Of(e.P("to"));
                if (f != null) f.TributeSent += n;
                if (t != null) t.TributeReceived += n;
                break;
            }
        }
    }
    static double G(Dictionary<string, double> d, string k) => d.TryGetValue(k, out var v) ? v : 0;
    /// <summary>a sample of the time line every 10 s (World.Update)</summary>
    public void Update()
    {
        if (W.Time < NextT) return;
        NextT = W.Time + SAMPLE;
        var v = new Dictionary<int, StatsSample>();
        // (one pass over the units and buildings for all players: up to 16 players)
        var of = new Dictionary<Player, StatsSample>(W.Players.Count);
        foreach (var p in W.Players)
        {
            var o = new StatsSample { Food = Math.Floor(p["food"]), Wood = Math.Floor(p["wood"]), Stone = Math.Floor(p["stone"]), Skulls = Math.Floor(p["skulls"]) };
            var g = p.Gained;
            o.CFood = G(g, "food"); o.CWood = G(g, "wood"); o.CStone = G(g, "stone"); o.CSkulls = G(g, "skulls");
            of[p] = o;
            v[p.Id] = o;
        }
        foreach (var u in W.Units) if (u.Alive && u.CountsInPop && u.Owner is { } up && of.TryGetValue(up, out var ou)) { ou.Units++; if (u.IsWorker) ou.Civil++; else ou.Military++; }
        foreach (var b in W.Buildings) if (b.Alive && b.Built && b.Owner is { } bp && of.TryGetValue(bp, out var ob)) ob.Buildings++;
        Series.Add((Rules.TT.JsRound(W.Time), v));
    }
    /// <summary>the table values of a player (ui/stats.js): the counters with the collected resources</summary>
    public PlayerStats Table(Player p)
    {
        var s = Of(p)!.Clone(); var g = p.Gained;
        s.Food = Math.Floor(G(g, "food")); s.Wood = Math.Floor(G(g, "wood")); s.Stone = Math.Floor(G(g, "stone")); s.Skulls = Math.Floor(G(g, "skulls"));
        return s;
    }
}

public sealed partial class World
{
    GameStats? _gameStats;
    /// <summary>the game's statistics (game/stats.js: W.stats)</summary>
    public GameStats GameStats => _gameStats ??= new GameStats(this);
    partial void StatsOn(WorldEvent e) => GameStats.On(e);
    partial void StatsUpdate() => GameStats.Update();
}
