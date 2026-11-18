// Electricity, the sequel's fifth resource (docs/SEQUEL.md "Power"). Only the sequel uses it; an engine-free,
// deterministic system of the simulation (it walks the world's building list in its order).
//
// Plants make it: the generating stations of the maps and the diesel generators and water wheels the player builds
// need workers (a worker's job at the plant: Economy.cs, farm kind "power", credited here through World.Hooks.Power);
// windmills turn by themselves. A plant delivers only while a line connects it to a hub (the headquarters or a field
// command post): power poles, each within RANGE of the next. Lines can be cut: a destroyed pole cuts off everything
// behind it. Power is stockpiled like skulls (no storage cap) and spent on the electric units and structures.
//
// Ported from remake/src/game/sequel/power.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Sequel;

public sealed class PowerGrid
{
    /// <summary>the longest span between two poles (m)</summary>
    public const double RANGE = 40;
    /// <summary>per worker per second / per second without workers</summary>
    public static readonly Dictionary<string, (double PerWorker, double Base)> PLANTS = new()
    {
        ["sq_power_station"] = (1.2, 0), ["sq_generator"] = (0.5, 0), ["sq_watermill"] = (0.8, 0.15), ["sq_windmill"] = (0, 0.35),
    };
    /// <summary>where the wires hang on each kind of node (height above its ground, m)</summary>
    static readonly Dictionary<string, double> ATTACH = new() { ["sq_power_pole"] = 10.8, ["sq_power_station"] = 9, ["sq_generator"] = 2.6, ["sq_watermill"] = 4.5, ["sq_windmill"] = 7, ["sq_field_hq"] = 4.5 };

    readonly SequelRuntime M;
    /// <summary>the lines as drawn: (node, the node it hangs on)</summary>
    public readonly List<(Building A, Building B)> Links = new();
    /// <summary>buildings connected to a hub</summary>
    readonly HashSet<Building> _powered = new();
    readonly Dictionary<Building, Building?> _link = new();
    string _key = "";
    double _t;

    public PowerGrid(SequelRuntime m) { M = m; }

    public static bool IsPlant(Entity b) => PLANTS.ContainsKey(b.Name);
    public bool IsPole(Entity b) => M.Classes.Tagged(b.Name, "pole");
    public bool IsHub(Entity b) => b.Name.EndsWith("headquarters", StringComparison.Ordinal) || M.Classes.Tagged(b.Name, "commandpost");
    public static double AttachHeight(Entity b) => ATTACH.TryGetValue(b.Name, out var h) ? h : 10;
    public bool Powered(Entity b) => b is Building bb && _powered.Contains(bb);

    List<Building> Nodes(Player? p) => M.W.Buildings.Where(b => b.Alive && b.Built && b.Owner == p && (IsPlant(b) || IsPole(b) || IsHub(b))).ToList();

    /// <summary>which plants and poles reach a hub; the lines as a forest (every node hangs on its nearest reached
    /// neighbour) - worked out again when the nodes change (checked every 0.5 s)</summary>
    public void Update(double dt)
    {
        var me = M.Me;
        _t -= dt;
        if (_t > 0) return;
        _t = 0.5;
        var ns = Nodes(me);
        var key = string.Join(",", ns.Select(b => b.Id));
        if (key != _key)
        {
            _key = key;
            _powered.Clear(); _link.Clear();
            bool Near(Building a, Building b) => Hypot(a.Pos.X - b.Pos.X, a.Pos.Z - b.Pos.Z) <= RANGE + (IsHub(a) || IsHub(b) ? 12 : 0);
            // a breadth-first walk from the hubs over poles (plants pass nothing on: a plant next to a plant is not a line)
            var queue = new Queue<Building>(ns.Where(IsHub));
            foreach (var h in queue) _powered.Add(h);
            while (queue.Count > 0)
            {
                var a = queue.Dequeue();
                if (IsPlant(a)) continue;
                foreach (var b in ns)
                {
                    if (_powered.Contains(b) || IsHub(b) || !Near(a, b)) continue;
                    _powered.Add(b); _link[b] = a; queue.Enqueue(b);
                }
            }
            // the dead ends: poles not reached still hang on their nearest pole (drawn, but carrying nothing)
            foreach (var b in ns)
            {
                if (_powered.Contains(b) || IsHub(b)) continue;
                Building? best = null; double bd = RANGE;
                foreach (var c in ns)
                {
                    if (c == b || IsPlant(c) || IsHub(c)) continue;
                    double d = Hypot(b.Pos.X - c.Pos.X, b.Pos.Z - c.Pos.Z);
                    if (d <= bd && (!_link.TryGetValue(c, out var cl) || cl != b)) { bd = d; best = c; }
                }
                _link[b] = best;
            }
            Links.Clear();
            foreach (var b in ns) if (_link.TryGetValue(b, out var l) && l != null) Links.Add((b, l));
        }
        // windmills (and the trickle of a water wheel) make power without workers
        foreach (var b in ns)
            if (PLANTS.TryGetValue(b.Name, out var P) && P.Base > 0 && _powered.Contains(b)) me?.Deliver("power", P.Base * 0.5, M.W.Time);
    }

    /// <summary>a worker's work loop at a plant (World.Hooks.Power)</summary>
    public void Credit(Unit u, Building b, double sec)
    {
        if (!PLANTS.TryGetValue(b.Name, out var P) || b.Owner == null) return;
        if (_powered.Contains(b)) b.Owner.Deliver("power", P.PerWorker * sec, M.W.Time);
    }

    /// <summary>what the plants make now, per minute: connected and cut off (the HUD)</summary>
    public (int On, int Off, int Plants, int Cut) Rates()
    {
        var me = M.Me;
        double on = 0, off = 0; int plants = 0, cut = 0;
        foreach (var b in M.W.Buildings)
        {
            if (!b.Alive || !b.Built || b.Owner != me || !PLANTS.TryGetValue(b.Name, out var P)) continue;
            double r = (P.Base + P.PerWorker * b.Workers.Count) * 60;
            plants++;
            if (_powered.Contains(b)) on += r; else { off += r; cut++; }
        }
        return ((int)Math.Round(on), (int)Math.Round(off), plants, cut);
    }

    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
}
