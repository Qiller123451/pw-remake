// A hash of the world's state (docs/ARCHITECTURE.md "Determinism"): two worlds made from the same data with the same
// seed and the same commands must give the same hash after the same steps - the test of determinism, and later the
// check of a lockstep game (machines compare it every few seconds) and of a replay.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PwRemake.Sim.World;

public sealed partial class World
{
    /// <summary>FNV-1a over the simulation state (time, random numbers, players, entities, timers)</summary>
    public ulong StateHash()
    {
        var h = new Fnv();
        h.D(Time); h.L(StepNo); h.I(NextId);
        foreach (var s in Rng.State) h.U(s);
        foreach (var p in Players)
        {
            h.I(p.Id); h.I(p.Units); h.D(p.MaxUnits);
            foreach (var r in Rules.Rules.COSTS) h.D(p[r]);
            h.I(p.Tt?.Version ?? 0);
        }
        foreach (var u in Units)
        {
            h.I(u.Id); h.S(u.Name); h.B(u.Alive); h.I(u.Level);
            h.D(u.Pos.X); h.D(u.Pos.Y); h.D(u.Pos.Z); h.D(u.Heading); h.D(u.Hp); h.D(u.MaxHp);
            h.S(u.Task.Type); h.I(u.Path.Count); h.S(u.Anim?.CurName); h.D(u.Anim?.Cur?.Time ?? 0); h.B(u.BusyAnim);
        }
        foreach (var b in Buildings) { h.I(b.Id); h.S(b.Name); h.B(b.Alive); h.D(b.Hp); h.D(b.Progress); h.B(b.Built); h.S(b.Anim?.CurName); }
        foreach (var r in Resources) { h.I(r.Id); h.B(r.Alive); h.D(r.Amount); }
        foreach (var p in Projectiles) { h.D(p.Pos.X); h.D(p.Pos.Z); h.D(p.T); }
        foreach (var t in Timers) { h.D(t.T); h.S(t.Kind); }
        h.I(Effects.Count); h.I(Spirits.Count); h.I(Nav.Version);
        return h.Value;
    }

    /// <summary>the hash of each part of the state (a lockstep game reports them with the hash: a desync names the parts
    /// that differ - docs/MULTIPLAYER.md)</summary>
    public List<(string Part, ulong Hash)> StateHashParts()
    {
        var res = new List<(string, ulong)>();
        var h = new Fnv(); h.D(Time); h.L(StepNo); h.I(NextId); foreach (var s in Rng.State) h.U(s); res.Add(("time+random", h.Value));
        h = new Fnv(); foreach (var p in Players) { h.I(p.Id); h.I(p.Units); h.D(p.MaxUnits); foreach (var r in Rules.Rules.COSTS) h.D(p[r]); h.I(p.Tt?.Version ?? 0); } res.Add(("players", h.Value));
        h = new Fnv(); foreach (var u in Units) { h.I(u.Id); h.S(u.Name); h.B(u.Alive); h.I(u.Level); h.D(u.Hp); h.D(u.MaxHp); } res.Add(("units", h.Value));
        h = new Fnv(); foreach (var u in Units) { h.D(u.Pos.X); h.D(u.Pos.Y); h.D(u.Pos.Z); h.D(u.Heading); h.I(u.Path.Count); } res.Add(("unit positions", h.Value));
        h = new Fnv(); foreach (var u in Units) { h.S(u.Task.Type); h.S(u.Anim?.CurName); h.D(u.Anim?.Cur?.Time ?? 0); h.B(u.BusyAnim); } res.Add(("unit tasks", h.Value));
        h = new Fnv(); foreach (var b in Buildings) { h.I(b.Id); h.S(b.Name); h.B(b.Alive); h.D(b.Hp); h.D(b.Progress); h.B(b.Built); h.S(b.Anim?.CurName); } res.Add(("buildings", h.Value));
        h = new Fnv(); foreach (var r in Resources) { h.I(r.Id); h.B(r.Alive); h.D(r.Amount); } res.Add(("resources", h.Value));
        h = new Fnv(); foreach (var p in Projectiles) { h.D(p.Pos.X); h.D(p.Pos.Z); h.D(p.T); } res.Add(("projectiles", h.Value));
        h = new Fnv(); foreach (var t in Timers) { h.D(t.T); h.S(t.Kind); } h.I(Effects.Count); h.I(Spirits.Count); h.I(Nav.Version); res.Add(("timers+effects", h.Value));
        return res;
    }

    /// <summary>the state as text, one line per player, entity, timer (numbers round-trippable): a lockstep game's machines
    /// write it at the same step when their hashes differed, the host compares them line by line (docs/MULTIPLAYER.md)</summary>
    public void StateDump(System.IO.TextWriter o)
    {
        static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        o.WriteLine($"step {StepNo} time {R(Time)} nextId {NextId} rng {string.Join(",", Rng.State)}");
        foreach (var p in Players)
            o.WriteLine($"player {p.Id} {p.Tribe} units {p.Units} max {R(p.MaxUnits)} res {string.Join(",", Rules.Rules.COSTS.Select(r => r + "=" + R(p[r])))} tt {p.Tt?.Version ?? 0} techs {p.Techs.Count} kills {p.Kills} lost {p.Lost} defeated {p.Defeated} won {p.Won}");
        foreach (var u in Units)
            o.WriteLine($"unit {u.Id} {u.Name} p{u.Owner?.Id ?? -1} alive {u.Alive} lv {u.Level} pos {R(u.Pos.X)},{R(u.Pos.Y)},{R(u.Pos.Z)} hd {R(u.Heading)} hp {R(u.Hp)}/{R(u.MaxHp)} task {u.Task.Type} path {u.Path.Count} anim {u.Anim?.CurName} {R(u.Anim?.Cur?.Time ?? 0)} busy {u.BusyAnim}");
        foreach (var b in Buildings)
            o.WriteLine($"building {b.Id} {b.Name} p{b.Owner?.Id ?? -1} alive {b.Alive} hp {R(b.Hp)} progress {R(b.Progress)} built {b.Built} pos {R(b.Pos.X)},{R(b.Pos.Z)} anim {b.Anim?.CurName}");
        foreach (var r in Resources) o.WriteLine($"resource {r.Id} {r.Name} alive {r.Alive} amount {R(r.Amount)}");
        foreach (var p in Projectiles) o.WriteLine($"projectile {R(p.Pos.X)},{R(p.Pos.Z)} t {R(p.T)}");
        foreach (var t in Timers) o.WriteLine($"timer {t.Kind} {R(t.T)}");
        o.WriteLine($"effects {Effects.Count} spirits {Spirits.Count} nav {Nav.Version}");
    }

    struct Fnv
    {
        ulong _h;
        public ulong Value => _h == 0 ? 14695981039346656037UL : _h;
        void Byte(byte b) { if (_h == 0) _h = 14695981039346656037UL; _h ^= b; _h *= 1099511628211UL; }
        public void U(ulong v) { for (int i = 0; i < 8; i++) Byte((byte)(v >> (i * 8))); }
        public void L(long v) => U((ulong)v);
        public void I(int v) => U((ulong)(uint)v);
        public void D(double v) => U((ulong)BitConverter.DoubleToInt64Bits(v));
        public void B(bool v) => Byte(v ? (byte)1 : (byte)0);
        public void S(string? s) { if (s == null) { Byte(0); return; } foreach (var b in Encoding.UTF8.GetBytes(s)) Byte(b); Byte(0xff); }
    }
}
