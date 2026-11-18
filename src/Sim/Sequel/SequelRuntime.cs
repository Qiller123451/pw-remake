// The remake's own campaigns at run time (docs/SEQUEL.md): Containment (1964), its 1966 missions and the scenarios.
// It sets up a mission's players and start, runs its script (mission.lua: SequelLua) and the sequel's own rules:
// rifts that spawn dinosaurs until a stabilizer seals them, civilians to evacuate, tranquilizers and capture trucks,
// electrified nets, night and searchlights, electricity (PowerGrid), command powers, objectives and the radio.
//
// Everything is the world's: the runtime is part of the simulation (World.Campaign = it, updated in every step after the
// computer players), uses the world's random numbers and lists, and changes only through the simulation's own calls
// and the player's commands (SequelCommand: the briefing read, a power used) - so a saved game, which replays the
// inputs (docs/SAVES.md), comes back the same. What it shows is read by the interface (objectives, the radio lines,
// markers, the clock, the grid's lines, set pieces) or comes as world events ("sqmsg" messages, "sqfx" effects,
// "sqarc" arcs).
//
// The same runtime plays the scenarios (Kind "scenario"): standalone missions in ParaWorld's own world, with the
// game's normal rules and data, played as any of the four tribes; their definitions are made for the chosen tribe
// (mission.json "variants") and may have other computer players: roles foe, foe2 and ally.
//
// Ported from remake/src/game/sequel/runtime.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Sequel;

public sealed class SqObjective
{
    public string Id = "", Text = "", Note = "";
    public bool Done, Failed, Secondary, Civ, Hidden;
    public double Count, Of;
}

public sealed class SqRift
{
    public string Id = "";
    public Building? E;
    public double X, Z, Every, Next, Seal, SealTime, Cap, Guard;
    public List<List<(string Cls, double N)>> Waves = new();
    public int Wave;
    public bool Open = true, Sealing;
    /// <summary>where its creatures go: a point, an entity, or nothing (the nearest enemy)</summary>
    public double[]? TargetPos;
    public Entity? TargetE;
    public Building? Stab;
}

public sealed class SqZone { public string Name = "", Kind = ""; public double X, Z, R; }
public sealed class SqMarker { public double X, Z, Until; public string Label = ""; public int Color; }
public sealed class SqLine { public string Who = "", Text = ""; public double T; }

/// <summary>a set piece: a model the scene moves (not a unit, nothing can touch it)</summary>
public sealed class SqPiece
{
    public string Id = "", Model = "";
    public double X, Z, Rot, Spin, Speed;
    public double? Y;
    public List<double[]>? Path;
    public bool Remove;
}

/// <summary>the runtime's marks on a unit or building (runtime.js u.sq*)</summary>
public sealed class SqMark
{
    public double[]? TargetPos;
    public Entity? TargetE;
    public string? Rift, Tag;
    public double[]? Home;
    public double Asleep, Stunned, ZapT;
    public bool Waking, Rescued, Hiding, Told, InTown, Envoy;
    public double Scale;
}

/// <summary>what the runtime asks of the mission's script (SequelLua)</summary>
public interface ISequelScript
{
    /// <summary>runs the handler of that name (none: nothing); args: a table of values (numbers, strings, ids)</summary>
    void Call(string name, IReadOnlyDictionary<string, object?>? args = null);
    bool Has(string name);
}

public sealed partial class SequelRuntime : ICampaignStep
{
    static readonly Logger L = Log.For("Sequel");
    /// <summary>how close to a rift a stabilizer must stand (centre to centre; the texts say 30 m)</summary>
    public const double RIFT_RANGE = 32;
    /// <summary>how long the "Tranquilized" debuff of a dart lasts (seconds; every dart starts it again)</summary>
    public const double TRANQ_SEC = 30;
    public sealed record PowerDef(string Name, int Cost, double Cool, bool Target, string Text);
    /// <summary>command powers: cost in command points, cooldown, what it does (UsePower)</summary>
    public static readonly OrderedMap<string, PowerDef> POWERS = MakePowers();
    static OrderedMap<string, PowerDef> MakePowers()
    {
        var m = new OrderedMap<string, PowerDef>();
        m.Set("recon", new("Recon flight", 2, 40, true, "Kiko flies over the area: it is revealed for 40 seconds."));
        m.Set("supply", new("Supply drop", 2, 45, false, "+150 food, wood and stone."));
        m.Set("airdrop", new("Airdrop", 4, 60, true, "Four containment troopers parachute in."));
        m.Set("artillery", new("Artillery", 5, 75, true, "A barrage hits the area after 3 seconds."));
        return m;
    }
    public static readonly Dictionary<string, int> ROLE_IDS = new() { ["me"] = 0, ["outbreak"] = 1, ["civilians"] = 2, ["crane"] = 3, ["aje"] = 4, ["foe"] = 5, ["foe2"] = 6, ["ally"] = 7 };
    public static readonly string[] EXTRA_ROLES = { "crane", "aje", "foe", "foe2", "ally" };
    public sealed record RoleLook(string Name, int Color, int? Party, int Team);
    public static readonly Dictionary<string, RoleLook> ROLE_LOOK = new()
    {
        ["me"] = new("SEAS", 0x3a6ea8, 0x5a8ed8, 0), ["outbreak"] = new("The Outbreak", 0x9a3a20, null, 1), ["civilians"] = new("Civilians", 0xc8c0a0, null, 0),
        ["crane"] = new("Crane Industrial", 0x6a6a6a, 0x303030, 2), ["aje"] = new("Dustriders", 0xb07830, 0xd09040, 3),
        ["foe"] = new("The Enemy", 0x9a2a2a, 0xc84040, 4), ["foe2"] = new("The Rivals", 0x6a3a9a, 0x8a5ac8, 5), ["ally"] = new("Allies", 0x3a9a5a, 0x5ac87a, 0),
    };
    public static readonly Dictionary<string, string> TRIBE_NAMES = new() { ["Hu"] = "Norsemen", ["Aje"] = "Dustriders", ["Ninigi"] = "Dragon Clan", ["SEAS"] = "SEAS" };

    // ---------------------------------------------------------------- the mission
    public readonly GameWorld W;
    public readonly SequelClasses Classes;
    /// <summary>"sequel" (Containment) | "scenario"</summary>
    public readonly string Kind;
    public readonly int Id, Difficulty;
    public readonly string Tribe;
    /// <summary>the mission's definition (mission.json; a scenario's made for its tribe and difficulty)</summary>
    public readonly JsonObject Def;
    public ISequelScript? Script;
    /// <summary>the strength of the outbreak (hit points of its creatures)</summary>
    public readonly double Diff;
    public readonly PowerGrid Grid;

    // ---------------------------------------------------------------- its state (runtime.js SAVED)
    public double Time;
    public readonly List<SqObjective> Objectives = new();
    readonly List<(double T, string Name, Dictionary<string, object?> Args)> _timers = new();
    /// <summary>the radio: every line said, in order (the interface shows them one after the other)</summary>
    public readonly List<SqLine> Lines = new();
    public readonly List<SqRift> Rifts = new();
    public readonly OrderedMap<string, SqZone> Zones = new();
    public double Cp, CpEarned;
    public readonly Dictionary<string, double> PowerReady = new();
    public readonly OrderedMap<string, double> Counters = new();
    readonly List<(Unit Truck, Unit Load)> _cargo = new();
    public readonly List<SqMarker> Markers = new();
    public readonly List<SqPiece> Pieces = new();
    /// <summary>the briefing was read (SequelCommand "begin"); nothing runs before</summary>
    public bool Started;
    public (bool Won, double T, string Text)? Ended;
    public readonly Dictionary<string, Player> Roles = new();
    public bool NightOn;
    /// <summary>a countdown the mission shows (M.set_clock): to this mission time (0: none), with its label</summary>
    public double ClockAt;
    public string ClockLabel = "";
    readonly List<double[]> _plantSpots = new();
    double _raidT = 150, _civT = -1, _tickT, _passiveT;
    bool _raidSaid;
    readonly Dictionary<Entity, SqMark> _marks = new(ReferenceEqualityComparer.Instance);
    /// <summary>classes not yet brought in (their build actions are left out of the rules)</summary>
    public ISet<string> Locked = new HashSet<string>();
    long _evAbs;
    bool _chaining;
    /// <summary>the mission's end for the session (won, text, delay s): GameSession shows the end screen</summary>
    public Action<bool, string, double>? OnEnd;
    /// <summary>the mission was won: the menu's progress (SequelProgress) - the session stores it</summary>
    public Action<SequelRuntime>? OnWon;
    /// <summary>the computer players' brains by role (woken by the script: M.ai)</summary>
    public readonly Dictionary<string, ICampaignBrain> Brains = new();

    public SequelRuntime(GameWorld w, SequelClasses classes, string kind, int id, int difficulty, string tribe, JsonObject def)
    {
        W = w; Classes = classes; Kind = kind; Id = id; Difficulty = difficulty; Tribe = tribe; Def = def;
        NightOn = def["night"].Bool();
        Diff = difficulty switch { 0 => 0.75, 2 => 1.35, _ => 1 };
        Grid = new PowerGrid(this);
    }

    public Player? Me => Roles.TryGetValue("me", out var p) ? p : null;
    public Player? PlayerOf(string? role) => role != null && Roles.TryGetValue(role, out var p) ? p : null;
    public string? RoleOf(Player? p) { if (p == null) return null; foreach (var (k, v) in Roles) if (v == p) return k; return null; }
    public SqMark Mark(Entity e) { if (!_marks.TryGetValue(e, out var m)) _marks[e] = m = new SqMark(); return m; }
    public SqMark? MarkOf(Entity e) => _marks.TryGetValue(e, out var m) ? m : null;
    public bool Powered(Entity b) => Grid.Powered(b);
    bool Tagged(Entity e, string tag) => Classes.Tagged(e.Name, tag) || (MarkOf(e)?.Tag == tag);
    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
    double Rnd() => W.Rng.Next();

    // ---------------------------------------------------------------- what the script calls
    public void Call(string name, Dictionary<string, object?>? args = null)
    {
        if (name == "_barrage") { if (args != null) Barrage(Convert.ToDouble(args["x"]), Convert.ToDouble(args["z"])); return; }
        if (name == "_intro") { foreach (var u in Unlocks(false)) Introduce(u); return; }
        try { Script?.Call(name, args); }
        catch (Exception e) { L.Error($"{Kind} mission {Id}: {name}", e); }
    }
    public void After(double sec, string name, Dictionary<string, object?>? args = null) => _timers.Add((Time + sec, name, args ?? new()));
    public void Say(string who, string text) { Lines.Add(new SqLine { Who = who, Text = text, T = Time }); L.Debug(() => $"radio {who}: {text}"); }
    public void Notify(string text, string kind = "") => W.Emit("sqmsg", new Args { ["text"] = text, ["kind"] = kind });
    public void Fx(string kind, double x, double y, double z, double scale) => W.Emit("sqfx", new Args { ["fx"] = kind, ["x"] = x, ["y"] = y, ["z"] = z, ["scale"] = scale });

    public SqObjective Objective(string id, string text, double of = 0, bool secondary = false, bool civ = false, bool? hasOf = null)
    {
        var o = Objectives.FirstOrDefault(x => x.Id == id);
        // civ: townsfolk to bring somewhere - the line shows how many were lost and how many are still out (CivStatus)
        if (o == null) { o = new SqObjective { Id = id, Text = text, Of = of, Secondary = secondary, Civ = civ }; Objectives.Add(o); Notify($"New objective: {text}"); }
        else { o.Text = text; if (hasOf != false) o.Of = of; }
        return o;
    }
    public void Progress(string id, double count)
    {
        var o = Objectives.FirstOrDefault(x => x.Id == id);
        if (o == null || o.Done || o.Failed) return;
        o.Count = Math.Min(count, o.Of != 0 ? o.Of : count);
        if (o.Of != 0 && o.Count >= o.Of) Complete(id);
    }
    public void Complete(string id)
    {
        var o = Objectives.FirstOrDefault(x => x.Id == id);
        if (o == null || o.Done || o.Failed) return;
        o.Done = true;
        Grant(o.Secondary ? 2 : 3);
        Notify($"Objective complete: {o.Text}", "good");
        Call("done_" + id);
    }
    public void Fail(string id)
    {
        var o = Objectives.FirstOrDefault(x => x.Id == id);
        if (o == null || o.Done || o.Failed) return;
        o.Failed = true;
        Notify($"Objective failed: {o.Text}", "bad");
        if (!o.Secondary) Lose(Def["loseText"] is JsonValue lt ? lt.Str() : o.Text);
    }
    public bool IsDone(string id) => Objectives.FirstOrDefault(x => x.Id == id) is { Done: true };

    /// <summary>the classes a mission brings in (unlocks): named ones are introduced a few seconds in; ["name", "later"]
    /// ones when the script calls M.introduce</summary>
    public IEnumerable<string> Unlocks(bool all)
    {
        if (Def["unlocks"] is not JsonArray a) yield break;
        foreach (var u in a)
        {
            if (u is JsonValue v) yield return v.Str();
            else if (all && u is JsonArray l && l.Count > 0) yield return l[0].Str();
        }
    }
    /// <summary>a unit or building the mission brings in: on the radio, what it does (texts.json intro), and a notice where
    /// it is built; quiet: the script has said it already</summary>
    public void Introduce(string name, bool quiet = false)
    {
        Classes.Texts.TryGetValue(name, out var t);
        var spec = Classes.All.Get(name);
        if (!quiet && t?["intro"] is JsonArray intro && intro.Count >= 2) Say(intro[0].Str(), intro[1].Str());
        Notify($"New: {(spec != null && spec.Desc != "" ? spec.Desc : name)} - {(t?["where"] is JsonValue wh ? wh.Str() : "in the build menu")}", "good");
    }
    public void Grant(double n) { Cp += n; CpEarned += n; }
    public void AddMarker(double x, double z, string label, int color = 0xffd040, double sec = 1e9) => Markers.Add(new SqMarker { X = x, Z = z, Label = label, Color = color, Until = Time + sec });
    public void Unmark(string label) => Markers.RemoveAll(m => m.Label == label);
    public void Win(string? text = null)
    {
        if (Ended != null) return;
        var t = !string.IsNullOrEmpty(text) ? text : Def["winText"].Str();
        Ended = (true, Time, t);
        L.Info($"{Kind} mission {Id} won: {t}");
        OnWon?.Invoke(this);
        OnEnd?.Invoke(true, t, 4);
    }
    public void Lose(string? text = null)
    {
        if (Ended != null) return;
        Ended = (false, Time, text ?? "");
        L.Info($"{Kind} mission {Id} lost: {text}");
        OnEnd?.Invoke(false, text ?? "", 3);
    }
    public double Count(string key, double d = 1) { double v = Counters.Get(key) + d; Counters.Set(key, v); return v; }
    public double Counter(string key) => Counters.Get(key);
    public SqZone? Zone(string name) => Zones.Get(name);

    // ---------------------------------------------------------------- objects
    /// <summary>a class, or of a list the first the data has (and whose model the assets have)</summary>
    public string Cls(IReadOnlyList<string> c)
    {
        if (c.Count == 0) return "";
        var D = W.Data;
        bool Ok(string n)
        {
            if (!D.Exists(n)) return false;
            var st = D.Stats(n, Math.Max(1, D.MinLevel(n, null)), null);
            return st != null && !string.IsNullOrEmpty(st.Gfx) && W.Models.Has(st.Gfx);
        }
        foreach (var n in c) if (Ok(n)) return n;
        foreach (var n in c) if (D.Exists(n)) return n;
        return c[0];
    }

    public sealed class SpawnOpts2
    {
        public int Level = 1;
        public double[]? TargetPos;
        public Entity? TargetE;
        public string? Tag;
        public double Heading;
        public bool Swim;
    }
    /// <summary>n units of class cls for a role at (x, z) (in a small crowd)</summary>
    public List<Unit> Spawn(string cls, string? role, double x, double z, int n = 1, SpawnOpts2? o = null)
    {
        o ??= new SpawnOpts2();
        var p = PlayerOf(role);
        var res = new List<Unit>();
        for (int i = 0; i < n; i++)
        {
            double a = i * 2.4, r = n > 1 ? 2 + Math.Sqrt(i) * 2.2 : 0;
            // never below the class's own first level (MIRAGE's CBT-500 triceratops is made at level 3)
            int lv = Math.Max(o.Level, FirstLevel(cls, p));
            var u = W.SpawnUnit(cls, p, x + Math.Cos(a) * r, z + Math.Sin(a) * r, lv, o.Heading, o.Swim ? new UnitOpts { Swim = true } : null);
            if (u == null) continue;
            if (o.TargetPos != null || o.TargetE != null) { var m = Mark(u); m.TargetPos = o.TargetPos; m.TargetE = o.TargetE; }
            if (o.Tag != null) Mark(u).Tag = o.Tag;
            if (role == "outbreak" && Diff != 1) { u.MaxHp *= Diff; u.Hp = u.MaxHp; }
            res.Add(u);
        }
        return res;
    }
    readonly Dictionary<string, int> _lvMemo = new();
    /// <summary>the level a class is made at: its build action's (World.UnitStartLevel), else the class's first level</summary>
    public int FirstLevel(string cls, Player? p)
    {
        if (p == null || p.Tt == null) return 1;
        var k = p.Id + "|" + cls;
        if (!_lvMemo.TryGetValue(k, out var lv))
        {
            var a = W.Data.Actions(p).Find(x => x.Kind == "Build" && x.Results.Any(r => r.Obj == cls));
            _lvMemo[k] = lv = a != null ? W.UnitStartLevel(a, p) : W.Data.MinLevel(cls, p);
        }
        return lv;
    }
    public Building? PlaceBuilding(string cls, string? role, double x, double z, double rot = 0)
    {
        var b = W.PlaceAdapted(cls, PlayerOf(role), x, z, rot, true);      // (the ground levels to it)
        if (b != null && role == "me") W.RecomputeCapsOf(Me);
        return b;
    }
    /// <summary>a player's main building (SEAS' headquarters, the fireplaces) or the Dustriders' resource collector</summary>
    public Entity? Home(Player? p)
    {
        if (p == null) return null;
        var bas = W.Data.StartOf(p.Tribe)?.Base is { Length: > 0 } s ? s : "seas_headquarters";
        return (Entity?)W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == p && b.Name == bas) ?? W.Units.FirstOrDefault(u => u.Alive && u.Owner == p && u.Name == bas);
    }
    public IEnumerable<Unit> UnitsOf(string? role) { var p = PlayerOf(role); return W.Units.Where(u => u.Alive && u.Owner == p); }
    public IEnumerable<Unit> Near(double x, double z, double r, string? role) => UnitsOf(role).Where(u => Hypot(u.Pos.X - x, u.Pos.Z - z) <= r);
    /// <summary>attack-move units to a point or a target</summary>
    public void Send(IEnumerable<Unit> units, double[]? pos, Entity? target)
    {
        double tx, tz;
        if (pos != null) { tx = pos[0]; tz = pos[1]; }
        else if (target != null) { tx = target.Pos.X; tz = target.Pos.Z; }
        else return;
        foreach (var u in units)
        {
            var m = Mark(u); m.TargetPos = pos; m.TargetE = null;
            W.Order(u, new Order("attackmove") { X = tx, Z = tz, Auto = true });
        }
    }

    /// <summary>a pen of the player's: a square ring of fence pieces on the 8 m wall grid around a zone, with a gate in
    /// the middle of the side facing (tx, tz) - the trucks drive in through it</summary>
    public List<Building> Pen(string zone, double tx, double tz, string fence = "seas_fence", string gate = "seas_gate")
    {
        var res = new List<Building>();
        var M = W.WallMap as WallMap; var z = Zone(zone); var me = Me;
        if (M == null || z == null) return res;
        var (ci, cj) = M.TileOf(z.X, z.Z);
        int N = Math.Max(2, (int)Math.Ceiling((z.R + 4) / 8));
        int[][] sides = { new[] { 0, -1 }, new[] { 0, 1 }, new[] { -1, 0 }, new[] { 1, 0 } };
        var s = sides.Aggregate((b, q) => Hypot(z.X + q[0] * N * 8 - tx, z.Z + q[1] * N * 8 - tz) < Hypot(z.X + b[0] * N * 8 - tx, z.Z + b[1] * N * 8 - tz) ? q : b);
        int gi = ci + s[0] * N, gj = cj + s[1] * N;
        for (int j = cj - N; j <= cj + N; j++)
            for (int i = ci - N; i <= ci + N; i++)
            {
                if (Math.Max(Math.Abs(i - ci), Math.Abs(j - cj)) != N || (i == gi && j == gj)) continue;
                var (x, zz) = M.Centre(i, j);
                var b = W.PlaceBuilding(fence, me, x, zz, 0, true);
                if (b != null) res.Add(b);
            }
        var (gx, gz) = M.Centre(gi, gj);
        int d = s[1] != 0 ? 3 : 5;                // along the side: east-west on a north / south side, else north-south
        var g = W.PlaceBuilding(gate, me, gx, gz, WallMap.GateRot(d), true);
        if (g != null) res.Add(g);
        return res;
    }

    // set pieces: the big wheel's turning rim, a boat casting off. y: its height (null: the ground's); spin: turns per
    // second about its own z axis; path it sails along at `speed` m/s, facing its way, then the handler arrived_<id>
    public SqPiece Piece(string id, string model, double x, double z, double? y = null, double rot = 0, double spin = 0)
    {
        Pieces.RemoveAll(p => p.Id == id);
        var p = new SqPiece { Id = id, Model = model, X = x, Z = z, Y = y, Rot = rot, Spin = spin };
        Pieces.Add(p);
        return p;
    }
    public void Sail(string id, List<double[]> path, double speed, bool remove = false)
    {
        var p = Pieces.FirstOrDefault(q => q.Id == id);
        if (p != null) { p.Path = path.Select(q => (double[])q.Clone()).ToList(); p.Speed = speed; p.Remove = remove; }
    }
    void PiecesTick(double dt)
    {
        foreach (var p in Pieces.ToList())
        {
            if (p.Path == null || p.Path.Count == 0) continue;
            double step = p.Speed * dt;
            while (step > 0 && p.Path.Count > 0)
            {
                double tx = p.Path[0][0], tz = p.Path[0][1], dx = tx - p.X, dz = tz - p.Z, d = Hypot(dx, dz);
                if (d > 0.01) p.Rot = Math.Atan2(-dz, dx);
                if (d <= step) { p.X = tx; p.Z = tz; p.Path.RemoveAt(0); step -= d; }
                else { p.X += dx / d * step; p.Z += dz / d * step; step = 0; }
            }
            if (p.Path.Count == 0)
            {
                p.Path = null;
                if (p.Remove) Pieces.Remove(p);
                Call("arrived_" + p.Id, new() { ["id"] = p.Id });
            }
        }
    }

    /// <summary>a rift: spawns waves (lists of [class, count]) every `every` seconds while open; a stabilizer of the player
    /// within 30 m seals it after sealTime seconds. guard: a radius - its creatures stay by it</summary>
    public SqRift Rift(string id, double x, double z, double every = 45, double first = 10, List<List<(string, double)>>? waves = null, double sealTime = 60, double cap = 18,
        double[]? targetPos = null, Entity? targetE = null, double guard = 0)
    {
        var e = W.PlaceBuilding("sq_rift", PlayerOf("outbreak"), x, z, 0, true);
        if (e != null) { W.SetInvulnerable(e, true); e.Untargetable = true; }
        var r = new SqRift
        {
            Id = id, E = e, X = x, Z = z, Every = every, Next = Time + first, Waves = waves ?? new() { new() { ("Velociraptor", 3) } }, SealTime = sealTime, Cap = cap,
            TargetPos = targetPos, TargetE = targetE, Guard = guard,
        };
        Rifts.Add(r);
        AddMarker(x, z, "rift:" + id, 0x60e0ff);
        return r;
    }
    public void CloseRift(SqRift r)
    {
        if (!r.Open) return;
        r.Open = false;
        if (r.E != null && r.E.Alive) { W.SetInvulnerable(r.E, false); W.RemoveEntity(r.E); }
        Unmark("rift:" + r.Id);
        Grant(3);
        Fx("explosion", r.X, W.Height(r.X, r.Z) + 5, r.Z, 2);
        Notify("A rift has been sealed", "good");
        Call("riftClosed", new() { ["id"] = r.Id });
    }
    public bool Untargetable(Entity e, bool on) { e.Untargetable = on; return on; }

    // ---------------------------------------------------------------- every step (ICampaignStep)
    public void Update(double dt)
    {
        if (!Started || Ended != null) return;
        Time += dt;
        if (_timers.Count > 0 && _timers.Any(t => t.T <= Time))
        {
            var due = _timers.Where(t => t.T <= Time).ToList();
            _timers.RemoveAll(t => t.T <= Time);
            foreach (var (_, name, args) in due) Call(name, args);
        }
        ReadEvents();
        PiecesTick(dt);
        Grid.Update(dt);
        _tickT -= dt;
        if (_tickT <= 0)
        {
            _tickT = 0.5;
            foreach (var r in Rifts.ToList()) RiftTick(r, 0.5);
            StabTick();
            OutbreakTick();
            CivilianTick();
            CaptureTick();
            NetTick();
            PowerTick(0.5);
            NightTick();
            _passiveT += 0.5;
            if (_passiveT >= 75) { _passiveT = 0; Grant(1); }
            Call("tick");
        }
        Markers.RemoveAll(m => m.Until <= Time);
    }

    void ReadEvents()
    {
        var ev = W.Events;
        int i = W.EventIndex(_evAbs);
        for (; i < ev.Count; i++)
        {
            var e = ev[i];
            if (e.Type != "died" && e.Type != "destroyed") continue;
            if (e.E("entity") is not { } x) continue;
            if (x.Owner != null && x.Owner == PlayerOf("civilians") && x is Unit) { double n = Count("civLost"); Notify($"{(Def["civName"] is JsonValue cn ? cn.Str() : "A townsperson")} was killed ({n} lost)", "bad"); }
            if (x.Owner == PlayerOf("outbreak") && x is Unit) { Count("dinosKilled"); if ((x.Stats?.Size ?? 0) >= 6) Grant(1); }
            var by = x.LastDamager;
            // by: the role whose blow was the last (null: nobody's); by_me: the player's or a friend's
            Call("died", new() { ["e"] = x.Id, ["name"] = x.Name, ["role"] = RoleOf(x.Owner), ["by"] = RoleOf(by), ["by_me"] = by != null && Me != null && (by == Me || Me.IsFriend(by)) });
        }
        _evAbs = W.EventBase + ev.Count;
    }

    void RiftTick(SqRift r, double dt)
    {
        if (!r.Open) return;
        var me = Me;
        // a finished stabilizer near it seals it
        if (r.Stab == null || !r.Stab.Alive || !r.Stab.Built)
            r.Stab = W.Buildings.FirstOrDefault(b => b.Alive && b.Built && b.Owner == me && Classes.Tagged(b.Name, "stabilizer") && Hypot(b.Pos.X - r.X, b.Pos.Z - r.Z) < RIFT_RANGE);
        // a stabilizer starts sealing it: the rift fights back at once, then a wave every third of the seal time at most
        if (r.Stab != null && !r.Sealing) { r.Sealing = true; r.Next = Time; Notify("The rift fights back!", "bad"); }
        if (r.Stab == null) r.Sealing = false;
        if (r.Stab != null)
        {
            r.Seal += dt;
            if (r.Seal >= r.SealTime) { CloseRift(r); return; }
        }
        else r.Seal = Math.Max(0, r.Seal - dt * 0.5);
        if (Time < r.Next) return;
        int alive = W.Units.Count(u => u.Alive && MarkOf(u)?.Rift == r.Id);
        // a rift that is being sealed surges: waves come twice as fast, at least three during the seal, and the cap does
        // not hold them back
        r.Next = Time + (r.Stab != null ? Math.Min(r.Every * 0.5, r.SealTime / 3) : r.Every);
        if (alive >= r.Cap && r.Stab == null) return;
        if (r.Waves.Count == 0) return;
        var wave = r.Waves[Math.Min(r.Wave, r.Waves.Count - 1)];
        r.Wave++;
        Entity? tE = r.Stab ?? r.TargetE;
        double[]? tP = r.Stab != null ? null : r.TargetPos;
        // what comes out is shown: a ping on the minimap, and word of the first wave
        AddMarker(r.X, r.Z, "wave:" + r.Id, 0xff5a3a, 8);
        if (r.Wave == 1) Notify("Creatures are coming through the rift", "bad");
        foreach (var (cls, n) in wave)
        {
            int k = Math.Max(1, (int)Math.Round(n * (Difficulty == 0 ? 0.7 : Difficulty == 2 ? 1.3 : 1), MidpointRounding.AwayFromZero));
            double sx = r.X + (Rnd() - 0.5) * 6, sz = r.Z + 8 + Rnd() * 4;
            var target = tE != null && tE.Alive ? new[] { tE.Pos.X, tE.Pos.Z } : tP;
            var us = Spawn(cls, "outbreak", sx, sz, k, new SpawnOpts2 { TargetPos = target });
            foreach (var u in us) { var m = Mark(u); m.Rift = r.Id; if (r.Guard > 0 && tE == null && tP == null) m.Home = new[] { r.X, r.Z, r.Guard }; }
        }
        Fx("dust", r.X, W.Height(r.X, r.Z) + 2, r.Z, 8);
        Fx("explosion", r.X, W.Height(r.X, r.Z) + 4, r.Z, 1);
    }

    /// <summary>a stabilizer just finished: whether it is close enough to a rift to seal it</summary>
    void StabTick()
    {
        foreach (var b in W.Buildings)
        {
            if (!b.Alive || !b.Built || b.Owner != Me || !Classes.Tagged(b.Name, "stabilizer")) continue;
            var m = Mark(b);
            if (m.Told) continue;
            m.Told = true;
            SqRift? best = null; double bd = 1e9;
            foreach (var r in Rifts) { if (!r.Open) continue; double d = Hypot(b.Pos.X - r.X, b.Pos.Z - r.Z); if (d < bd) { bd = d; best = r; } }
            if (best == null) Notify("Rift Stabilizer ready - there is no open rift near it", "bad");
            else if (bd < RIFT_RANGE) Notify($"Rift Stabilizer ready - sealing the rift: keep it standing for {Math.Round(best.SealTime - best.Seal)} s", "good");
            else Notify($"Rift Stabilizer too far from the rift ({Math.Round(bd)} m) - it must stand within {RIFT_RANGE - 2} m of it", "bad");
        }
    }

    bool Trapped(Unit u) => u.St.T.TryGetValue("trapped", out var t) && t > W.Time;

    /// <summary>the outbreak's creatures: to their target, else the nearest enemy, else the player's base</summary>
    void OutbreakTick()
    {
        var ob = PlayerOf("outbreak");
        if (ob == null) return;
        foreach (var u in W.Units.ToList())
        {
            if (!u.Alive || u.Owner != ob || u.Inside || u.Parked) continue;
            var m = MarkOf(u);
            if (m != null && m.Asleep > Time) continue;
            if (u.Task.Type != "idle" && u.Task.Type != "roam") continue;
            if (Trapped(u)) continue;
            double[]? t = m?.TargetE != null ? (m.TargetE.Alive ? new[] { m.TargetE.Pos.X, m.TargetE.Pos.Z } : null) : m?.TargetPos;
            if (t != null && Hypot(u.Pos.X - t[0], u.Pos.Z - t[1]) < 10) t = null;
            var H = t == null ? m?.Home : null;
            if (H != null)
            {
                // a rift's guard: what comes within its radius of the rift, else back to it
                Entity? best = null; double bd = 1e9;
                foreach (var e in W.Units)
                {
                    if (!e.Alive || e.Owner == null || e.Owner == ob || e.Inside || e.Parked || Hypot(e.Pos.X - H[0], e.Pos.Z - H[1]) > H[2]) continue;
                    double d = Hypot(e.Pos.X - u.Pos.X, e.Pos.Z - u.Pos.Z); if (d < bd) { bd = d; best = e; }
                }
                if (best == null)
                    foreach (var b in W.Buildings)
                    {
                        if (!b.Alive || b.Owner == null || b.Owner == ob || Hypot(b.Pos.X - H[0], b.Pos.Z - H[1]) > H[2]) continue;
                        double d = Hypot(b.Pos.X - u.Pos.X, b.Pos.Z - u.Pos.Z); if (d < bd) { bd = d; best = b; }
                    }
                if (best != null) W.Order(u, new Order("attackmove") { X = best.Pos.X, Z = best.Pos.Z, Auto = true });
                else if (Hypot(u.Pos.X - H[0], u.Pos.Z - H[1]) > H[2] * 0.5) W.Order(u, new Order("move") { X = H[0] + (Rnd() - 0.5) * 20, Z = H[1] + (Rnd() - 0.5) * 20, Auto = true });
                continue;
            }
            if (t == null)
            {
                Entity? best = null; double bd = 160;
                foreach (var e in W.Units) { if (!e.Alive || e.Owner == null || e.Owner == ob || e.Inside || e.Parked) continue; double d = Hypot(e.Pos.X - u.Pos.X, e.Pos.Z - u.Pos.Z); if (d < bd) { bd = d; best = e; } }
                if (best == null) foreach (var b in W.Buildings) { if (!b.Alive || b.Owner == null || b.Owner == ob) continue; double d = Hypot(b.Pos.X - u.Pos.X, b.Pos.Z - u.Pos.Z); if (d < bd * 2) { bd = d / 2; best = b; } }
                if (best != null) t = new[] { best.Pos.X, best.Pos.Z };
            }
            if (t != null) W.Order(u, new Order("attackmove") { X = t[0], Z = t[1], Auto = true });
        }
    }

    /// <summary>civilians: run from creatures; a unit of the player's nearby leads them: they head for the nearest
    /// evacuation zone</summary>
    void CivilianTick()
    {
        var cv = PlayerOf("civilians"); var ob = PlayerOf("outbreak"); var me = Me;
        if (cv == null) return;
        var evac = Zones.Values.Where(z => z.Kind == "evac").ToList();
        foreach (var u in W.Units.ToList())
        {
            if (!u.Alive || u.Owner != cv) continue;
            var m = Mark(u);
            var zone = evac.FirstOrDefault(z => Hypot(u.Pos.X - z.X, u.Pos.Z - z.Z) <= z.R);
            if (zone != null && m.Rescued)
            {
                W.RemoveEntity(u);
                double n = Count("evacuated");
                Call("evacuated", new() { ["n"] = n, ["zone"] = zone.Name });
                continue;
            }
            Unit? danger = null; double dd = 22;
            W.UHash.Query(u.Pos.X, u.Pos.Z, 26, o => { if (o.Alive && o.Owner == ob) { double d = Hypot(o.Pos.X - u.Pos.X, o.Pos.Z - u.Pos.Z); if (d < dd) { dd = d; danger = o; } } });
            if (!m.Rescued)
            {
                bool found = false;
                W.UHash.Query(u.Pos.X, u.Pos.Z, 14, o => { if (!found && o.Alive && o.Owner == me) found = true; });
                if (found) { m.Rescued = true; m.Hiding = false; u.Untargetable = false; _civT = Time; Call("rescued", new() { ["u"] = u.Id }); }
            }
            if (danger != null && !m.Rescued && !m.Hiding)
            {
                double dx = u.Pos.X - danger.Pos.X, dz = u.Pos.Z - danger.Pos.Z, d = Hypot(dx, dz); if (d == 0) d = 1;
                W.Order(u, new Order("move") { X = u.Pos.X + dx / d * 18, Z = u.Pos.Z + dz / d * 18, Auto = true });
            }
            else if (m.Rescued && evac.Count > 0 && u.Task.Type == "idle")
            {
                var z0 = evac[0]; double bd = 1e9;
                foreach (var z in evac) { double d = Hypot(u.Pos.X - z.X, u.Pos.Z - z.Z); if (d < bd) { bd = d; z0 = z; } }
                W.Order(u, new Order("move") { X = z0.X + (Rnd() - 0.5) * z0.R, Z = z0.Z + (Rnd() - 0.5) * z0.R, Auto = true });
            }
        }
        CivStatus(cv);
    }

    /// <summary>the townsfolk objectives: how many were lost and how many are still out, an objective that can no longer
    /// be reached fails, and townsfolk nobody has found for a while are shown on the map</summary>
    void CivStatus(Player cv)
    {
        var outs = W.Units.Where(u => u.Alive && u.Owner == cv).ToList();
        double lost = Counter("civLost");
        var hiding = outs.Where(u => MarkOf(u)?.Hiding == true).ToList();
        foreach (var o in Objectives.ToList())
        {
            if (!o.Civ || o.Done || o.Failed) continue;
            o.Note = $"{lost} lost · {outs.Count} still out" + (hiding.Count > 0 ? $" ({hiding.Count} not found yet)" : "");
            if (o.Of != 0 && o.Count + outs.Count < o.Of)
            {
                if (o.Secondary) Fail(o.Id); else { o.Failed = true; Lose("Too many townsfolk were lost."); }
            }
        }
        if (_civT < 0) _civT = Time;
        if (hiding.Count > 0 && Objectives.Any(o => o.Civ && !o.Done && !o.Failed) && Time - _civT > 180)
        {
            _civT = Time;
            var spots = new List<double[]>();
            foreach (var u in hiding) if (!spots.Any(p => Hypot(p[0] - u.Pos.X, p[1] - u.Pos.Z) < 30)) spots.Add(new[] { u.Pos.X, u.Pos.Z });
            for (int i = 0; i < spots.Count; i++) AddMarker(spots[i][0], spots[i][1], "hiding:" + i, 0xfff0a0, 30);
            Notify($"{hiding.Count} townsfolk are still hiding - their places are marked on the map");
        }
    }

    // ---------------------------------------------------------------- tranquilizers and captures
    /// <summary>a tranquilized creature sleeps; a capture truck next to one loads it, at a pen it becomes the player's</summary>
    public void Sleep(Unit V, double sec = 50)
    {
        var m = Mark(V);
        m.Asleep = Time + sec;
        V.Untargetable = true;
        W.Trap(V, sec, "asleep");
        W.ReleaseTask(V); V.Task = UnitTask.Idle(); V.Path = new NavPath();
        // it lies down and keeps lying (the lying part of its rest clip loops) until it gets up as the sleep ends
        var a = V.Anim?.Pick("rest", "sleep", "idle_rest", "dying", "die_simple");
        if (a != null)
        {
            V.BusyAnim = true; m.Waking = false;
            V.Anim!.Play(a, a is "rest" or "sleep" or "idle_rest" ? new AnimOpts { Restart = true, Fade = 0.3 } : new AnimOpts { Loop = false, Fade = 0.3 });
        }
        // nobody goes on hitting a sleeper
        foreach (var u in W.Units) if (u.Alive && u != V && u.Task.Target == V) { W.ReleaseTask(u); u.Task = UnitTask.Idle(); u.Path = new NavPath(); }
        Call("asleep", new() { ["u"] = V.Id, ["name"] = V.Name });
    }
    public void Wake(Unit V)
    {
        var m = Mark(V);
        m.Asleep = 0; V.Untargetable = false; V.BusyAnim = false;
        V.St.T["trapped"] = 0;
        if (V.Anim != null && V.IdleAnim != null && !m.Waking) V.Anim.Play(V.IdleAnim);
        m.Waking = false;
    }
    void WakeTick(Unit V, SqMark m)
    {
        if (m.Waking || V.Anim == null || V.IdleAnim == null) return;
        double e = V.Anim.Duration(V.Anim.CurName + "#e");
        if (e > 0 && m.Asleep - Time <= e) { m.Waking = true; V.Anim.Play(V.IdleAnim); }
    }
    public bool Asleep(Entity e) => MarkOf(e) is { } m && m.Asleep > Time;

    /// <summary>electrified nets: a creature of the outbreak that walks onto one is held 15 s (tranquilizers work 5x as fast
    /// on it); a net needs 30 s to charge again</summary>
    void NetTick()
    {
        var me = Me; var outb = PlayerOf("outbreak");
        foreach (var n in W.Buildings)
        {
            if (!n.Alive || !n.Built || n.Owner != me || n.Name != "sq_electric_net") continue;
            var nm = Mark(n);
            if (nm.ZapT > Time) continue;
            var v = W.Units.FirstOrDefault(u => u.Alive && u.Owner == outb && u.Cls == "ANML" && !u.Parked && !Asleep(u) && Hypot(u.Pos.X - n.Pos.X, u.Pos.Z - n.Pos.Z) < 10);
            if (v == null) continue;
            nm.ZapT = Time + 30;
            W.Trap(v, 15, "net");
            Mark(v).Stunned = Time + 15;
            Fx("explosion", v.Pos.X, v.Pos.Y, v.Pos.Z, 1);
            Call("netted", new() { ["u"] = v.Id, ["net"] = n.Id });
        }
    }

    /// <summary>electricity: abandoned stations are taken over when the player gets there; the outbreak raids the lines</summary>
    void PowerTick(double dt)
    {
        var me = Me; var civ = PlayerOf("civilians"); var ob = PlayerOf("outbreak");
        foreach (var b in W.Buildings.ToList())
        {
            if (!b.Alive || !PowerGrid.IsPlant(b) || b.Owner != civ || civ == null) continue;
            if (!W.Units.Any(u => u.Alive && u.Owner == me && !u.Parked && Hypot(u.Pos.X - b.Pos.X, u.Pos.Z - b.Pos.Z) < 22)) continue;
            W.SetOwner(b, me);
            Notify("Generating station secured: put workers in it and run a power line to your base", "good");
            AddMarker(b.Pos.X, b.Pos.Z, "station", 0xffe070, 20);
            Call("plantTaken", new() { ["b"] = b.Id });
        }
        _raidT -= dt;
        if (_raidT > 0 || ob == null) return;
        _raidT = (120 + Rnd() * 60) / Diff;
        var lines = W.Buildings.Where(b => b.Alive && b.Owner == me && (Grid.IsPole(b) || PowerGrid.IsPlant(b)) && Grid.Powered(b)).ToList();
        if (lines.Count == 0) return;
        var t = lines[(int)Math.Floor(Rnd() * lines.Count)];
        var raiders = W.Units.Where(u => u.Alive && u.Owner == ob && !u.Parked && !Asleep(u) && Hypot(u.Pos.X - t.Pos.X, u.Pos.Z - t.Pos.Z) < 160).Take(3).ToList();
        if (raiders.Count == 0) return;
        foreach (var u in raiders) { var m = Mark(u); m.TargetE = t; m.TargetPos = null; W.Order(u, new Order("attack") { Target = t, Auto = true }); }
        AddMarker(t.Pos.X, t.Pos.Z, "raid", 0xff5030, 12);
        if (!_raidSaid) { _raidSaid = true; Say("kiko", "Colonel, they're going for the power line! Something figured out where the hum comes from."); }
    }

    void CaptureTick()
    {
        var me = Me;
        foreach (var u in W.Units.ToList())
        {
            var m = MarkOf(u);
            if (m == null || m.Asleep == 0 || u.Parked) continue;
            if (m.Asleep <= Time) Wake(u); else WakeTick(u, m);
        }
        // the pens of the mission; where it has none, the garage or the headquarters takes the animal in
        var pens = Zones.Values.Where(z => z.Kind == "pen").Select(z => (z.X, z.Z, z.R)).ToList();
        if (pens.Count == 0)
            pens = W.Buildings.Where(b => b.Alive && b.Built && b.Owner == me && (b.Name == "seas_garage" || b.Name == "seas_headquarters")).Select(b => (b.Pos.X, b.Pos.Z, b.Radius + 10)).ToList();
        foreach (var t in W.Units.ToList())
        {
            if (!t.Alive || t.Owner != me || !Classes.Tagged(t.Name, "capture")) continue;
            int li = _cargo.FindIndex(c => c.Truck == t);
            if (li < 0)
            {
                Unit? best = null; double bd = 9;
                W.UHash.Query(t.Pos.X, t.Pos.Z, 12, o => { if (o.Alive && Asleep(o) && !o.Parked) { double d = Hypot(o.Pos.X - t.Pos.X, o.Pos.Z - t.Pos.Z) - o.Radius; if (d < bd) { bd = d; best = o; } } });
                if (best != null)
                {
                    W.SetParked(best, true); _cargo.Add((t, best));
                    Notify($"Loaded: {W.Data.Text(best.Name)?.Name ?? best.Name}", "good");
                    Call("loaded", new() { ["u"] = best.Id, ["name"] = best.Name });
                }
                continue;
            }
            var pen = pens.FirstOrDefault(z => Hypot(t.Pos.X - z.X, t.Pos.Z - z.Z) <= z.Item3 + 4);
            if (pens.Any(z => Hypot(t.Pos.X - z.X, t.Pos.Z - z.Z) <= z.Item3 + 4))
            {
                var V = _cargo[li].Load;
                _cargo.RemoveAt(li);
                W.SetParked(V, false);
                W.Teleport(V, pen.X + (Rnd() - 0.5) * pen.Item3, pen.Z + (Rnd() - 0.5) * pen.Item3);
                Wake(V);
                W.SetOwner(V, me);
                var vm = Mark(V); vm.Rift = null; vm.TargetE = null; vm.TargetPos = null;
                Grant(2);
                double n = Count("captured");
                Notify($"Captured! ({n})", "good");
                Call("captured", new() { ["u"] = V.Id, ["n"] = n, ["name"] = V.Name });
            }
        }
        // a truck that died drops its load where it was
        foreach (var c in _cargo.ToList())
        {
            if (c.Truck.Alive) continue;
            _cargo.Remove(c);
            var V = c.Load; W.SetParked(V, false); W.Teleport(V, c.Truck.Pos.X + 4, c.Truck.Pos.Z); Wake(V);
        }
    }
    /// <summary>what the trucks carry (the view hides nothing: the loads are parked)</summary>
    public IReadOnlyList<(Unit Truck, Unit Load)> Cargo => _cargo;

    /// <summary>the damage hook (World.Hooks.Damage): a tranquilizer dart puts the "Tranquilized" debuff on a creature for
    /// TRANQ_SEC (every hit starts it again) and does its normal damage - five times that on a creature held by a net.
    /// true = handled</summary>
    public bool DamageHook(double dmg, Entity? A, Entity V)
    {
        if (A == null || V == null) return false;
        // a sleeper is left alone: no blow (darts, splash, a stray shot) reaches it
        if (Asleep(V)) return true;
        var tags = Classes.TagsOf(A.Name);
        if ((tags.Contains("shock") || tags.Contains("chain")) && !_chaining) Electric(dmg, A, V, tags);
        if (V is not Unit vu || V.Cls != "ANML" || !tags.Contains("tranq")) return false;
        vu.SqTranq = W.Time + TRANQ_SEC;
        if (MarkOf(V) is { } m && m.Stunned > Time) { W.Damage(V, dmg * 5, null); return true; }
        return false;
    }
    /// <summary>World.Hooks.Lethal: a tranquilized creature that would die falls asleep at 1 hit point</summary>
    public bool LethalHook(Entity V, Entity? A)
    {
        if (V is not Unit u) return false;
        if (Asleep(V)) return true;
        if (!(u.SqTranq > W.Time)) return false;
        u.SqTranq = 0;
        Sleep(u);
        return true;
    }
    /// <summary>the electric weapons: an arc to the target; shock stuns it for a moment, chain lightning jumps on to two
    /// more enemies nearby (each jump weaker)</summary>
    void Electric(double dmg, Entity A, Entity V, IReadOnlyList<string> tags)
    {
        double[] At(Entity e) => new[] { e.Pos.X, e.Pos.Y + (e is Building b ? b.Ht * 0.8 : 1.6), e.Pos.Z };
        void Arc(Entity a, Entity b) { var p = At(a); var q = At(b); W.Emit("sqarc", new Args { ["ax"] = p[0], ["ay"] = p[1], ["az"] = p[2], ["bx"] = q[0], ["by"] = q[1], ["bz"] = q[2] }); }
        Arc(A, V);
        if (tags.Contains("shock") && V is Unit vu && V.Alive) { W.Trap(V, 0.8, "shock"); var m = Mark(V); m.Stunned = Math.Max(m.Stunned, Time + 0.8); }
        if (!tags.Contains("chain") || A.Owner == null) return;
        _chaining = true;
        try
        {
            var hit = new HashSet<Entity>(ReferenceEqualityComparer.Instance) { V };
            Entity prev = V; double d = dmg;
            for (int k = 0; k < 2; k++)
            {
                d *= 0.6;
                Unit? best = null; double bd = 12;
                foreach (var u in W.Units)
                {
                    if (!u.Alive || hit.Contains(u) || u.Owner == null || !A.Owner.IsEnemy(u.Owner) || u.Parked || u.Inside) continue;
                    double dd = Hypot(u.Pos.X - prev.Pos.X, u.Pos.Z - prev.Pos.Z);
                    if (dd < bd) { bd = dd; best = u; }
                }
                if (best == null) break;
                hit.Add(best);
                Arc(prev, best);
                W.Damage(best, d, A);
                prev = best;
            }
        }
        finally { _chaining = false; }
    }

    // ---------------------------------------------------------------- night
    /// <summary>the night: darker light (the view: SequelFx), the units' sight 0.6 of the day's, searchlights</summary>
    public void SetNight(bool on)
    {
        NightOn = on;
        W.SightScale = on ? 0.6 : 1;
        W.FogDirty = true;
    }
    void NightTick()
    {
        if (!NightOn) return;
        var ob = PlayerOf("outbreak");
        foreach (var b in W.Buildings)
        {
            if (!b.Alive || !b.Built || b.Owner != Me || !Classes.Tagged(b.Name, "searchlight")) continue;
            W.UHash.Query(b.Pos.X, b.Pos.Z, 30, o => { if (o.Alive && o.Owner == ob && Hypot(o.Pos.X - b.Pos.X, o.Pos.Z - b.Pos.Z) < 26) { W.Trap(o, 0.5, "dazzled"); o.St.RevealT = W.Time + 1; } });
        }
    }
    /// <summary>extra sight for the fog of war at night: searchlights light 36 m (World.ExtraViewers)</summary>
    public IEnumerable<(double, double, double)> Viewers()
    {
        if (!NightOn) yield break;
        foreach (var b in W.Buildings)
            if (b.Alive && b.Built && b.Owner == Me && Classes.Tagged(b.Name, "searchlight")) yield return (b.Pos.X, b.Pos.Z, 36.0);
    }

    // ---------------------------------------------------------------- command powers
    public bool PowerAllowed(string id) => POWERS.Has(id) && (Def["powers"] is not JsonArray pw || pw.Any(x => x.Str() == id));
    /// <summary>off | cooldown | cost | ready</summary>
    public string PowerState(string id)
    {
        if (!POWERS.TryGetValue(id, out var P) || !PowerAllowed(id)) return "off";
        if (PowerReady.GetValueOrDefault(id) > Time) return "cooldown";
        if (Cp < P.Cost) return "cost";
        return "ready";
    }
    public bool UsePower(string id, double x, double z)
    {
        if (PowerState(id) != "ready") return false;
        var P = POWERS.Get(id)!; var me = Me!;
        Cp -= P.Cost;
        PowerReady[id] = Time + P.Cool;
        switch (id)
        {
            case "recon": W.RevealArea(me, x, z, 70, 40); break;
            case "supply": foreach (var r in Rules.Rules.RES) me.Res[r] = Math.Min(me[r] + 150, Math.Max(me.Caps.GetValueOrDefault(r, 300), me[r] + 150)); break;
            case "airdrop": foreach (var u in Spawn("sq_trooper", "me", x, z, 4, new SpawnOpts2 { Level = 2 })) Fx("dust", u.Pos.X, u.Pos.Y, u.Pos.Z, 2); break;
            case "artillery": After(3, "_barrage", new() { ["x"] = x, ["z"] = z }); break;
        }
        Notify(P.Name, "good");
        return true;
    }
    void Barrage(double x, double z)
    {
        var me = Me;
        for (int i = 0; i < 6; i++)
        {
            double px = x + (Rnd() - 0.5) * 18, pz = z + (Rnd() - 0.5) * 18;
            var p = new V3(px, W.Height(px, pz), pz);
            Fx("explosion", p.X, p.Y, p.Z, 1.4);
            W.AreaDamage(null, p, 8, 260, 60, new AreaOpts { Owner = me, Direct = true, Size = 7 });
        }
        W.Emit("sqshake", new Args { ["strength"] = 10.0 });
    }

    /// <summary>the state of the mission for the save's check (docs/SAVES.md): objectives, rifts, points, counters, time</summary>
    public ulong StateHash()
    {
        ulong h = 1469598103934665603UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        void MixD(double d) => Mix((ulong)BitConverter.DoubleToInt64Bits(Math.Round(d, 6)));
        void MixS(string s) { foreach (var ch in s) Mix(ch); Mix(0xff); }
        MixD(Time); MixD(Cp); Mix(Started ? 1UL : 0); Mix(Ended != null ? 1UL : 0);
        foreach (var o in Objectives) { MixS(o.Id); Mix(o.Done ? 1UL : 0); Mix(o.Failed ? 1UL : 0); MixD(o.Count); }
        foreach (var r in Rifts) { MixS(r.Id); Mix(r.Open ? 1UL : 0); MixD(r.Seal); Mix((ulong)r.Wave); }
        foreach (var (k, v) in Counters) { MixS(k); MixD(v); }
        Mix((ulong)Lines.Count); Mix((ulong)_timers.Count);
        return h;
    }
}
