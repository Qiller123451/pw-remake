// The trigger engine of the campaign: makes a mission play. Specification: remake/docs/spec/triggers.md (§1 how
// triggers run, §3 variables, §6 subsystems); the world it works on: IMissionHost. The conditions are in
// Conditions.cs, the actions in Actions.cs; this file is the core:
//
//   * trigger records with their flags (once, enabled, random, by_difficulty), folders (ACND) and the boolean
//     expression over the conditions;
//   * Invalidate() -> evaluate at once -> queue the firing -> the actions run at the end of the step (§1.1);
//   * how conditions hear about the world: channels they subscribe to while their trigger listens (Sub), a tick
//     list (TIME, RTME), a poll list (things without an event: positions, attributes) and one "objects changed"
//     version that every spawn / removal / owner / group change bumps;
//   * level variables, level timers, quests, the sequence and dialogue queues, delayed jobs;
//   * the debug helpers (Debug*) and the firing log.
//
// The engine never throws into the game loop: every action and every condition callback is guarded; what went
// wrong is collected in Errors ([trigger, node type, message]) and logged (category Campaign.Triggers).
// Ported from remake/src/game/campaign/engine.js (pw-exporter 98c7703). What the JS engine drew or drove itself
// (effects, waypoint patrols, sequence actors) is the host's (IMissionHost).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Scripting;

/// <summary>The channels conditions subscribe to (engine.js sub / forever).</summary>
public enum Channel { Objects, Vars, Removed, Lost, Group, Diplomacy, Attacked, Boarded, Item, Wayr, Quest, Timer, Sequence, Dialog }

/// <summary>One compiled trigger of the mission (engine.js Trigger).</summary>
public sealed class Trigger
{
    public readonly TriggerEngine E;
    public readonly TriggerDef Def;
    public readonly int Index;
    public string Guid => Def.Guid;
    public string Name => Def.Name;
    public readonly string Folder;
    public readonly bool Once, Random, ByDifficulty, StartEnabled, NodeOff;
    /// <summary>The flag TRIG switches.</summary>
    public bool Enabled;
    /// <summary>No folder above it is inactive.</summary>
    public bool NodeActive = true;
    /// <summary>Enabled and NodeActive: its conditions listen.</summary>
    public bool Live;
    public int Fired, Queued;
    public double LastFired = -1;
    internal bool Enabling, Dirty;
    public readonly List<Condition> Conds = new();
    public readonly List<TriggerAction> Actions = new();
    readonly TriggerExpr expr;

    internal Trigger(TriggerEngine e, TriggerDef d, int index)
    {
        E = e; Def = d; Index = index;
        Folder = string.IsNullOrEmpty(d.Folder) ? "Root" : d.Folder;
        Once = d.Once; Random = d.Random; ByDifficulty = d.ByDifficulty; StartEnabled = d.Enabled; NodeOff = d.NodeOff;
        for (int i = 0; i < d.Conditions.Count; i++)
        {
            var n = d.Conditions[i];
            var p = TriggerParams.WithDefaults(e.Script.ConditionDefaults.GetValueOrDefault(n.Code), n.P);
            var c = TriggerTypes.CreateCondition(n.Code) ?? e.UnknownCondition(n.Code);
            c.Setup(e, this, n, p, i);
            Conds.Add(c);
        }
        foreach (var n in d.Actions)
        {
            var p = TriggerParams.WithDefaults(e.Script.ActionDefaults.GetValueOrDefault(n.Code), n.P);
            var a = TriggerTypes.CreateAction(n.Code) ?? new UnknownAction();
            a.Setup(n, p);
            Actions.Add(a);
        }
        try { expr = TriggerExpr.Compile(d.Expression, Conds.Count); }
        catch (Exception ex) { e.Error(this, "expression", ex); expr = TriggerExpr.Compile("", 0); }
        foreach (var c in Conds) e.Guard(this, c.Type, c.Init);
    }

    public bool Evaluate() => expr.Eval(i => Conds[i].State != 0);
    public override string ToString() => Name;
}

/// <summary>A level timer (§6.3; TIMR action) or the countdown of a TIME condition with show = 1.</summary>
public sealed class LevelTimer
{
    public string Id = "", Label = "";
    public double Left, Total;
    public bool Show, Paused, Countup;
    /// <summary>Belongs to a TIME condition (counted by it, not by the engine).</summary>
    public bool Cond;
}

/// <summary>A firing in the log.</summary>
public readonly record struct FiringEntry(double Time, string Trigger, string[] Actions, bool Forced);

public sealed class TriggerEngine
{
    static readonly Logger L = Log.For("Campaign.Triggers");
    static readonly string[] ProfileNames = { "Games_Started", "Kill_Counter", "Other_Stuff", "Tutorial_Started", "Level_1_Started" };
    static readonly string[] ProfileDefaults = { "0", "0", "", "0", "0" };

    public readonly IMissionHost Host;
    public readonly MissionScript Script;
    /// <summary>The Lua host the mission's Script / Check nodes run in (null for a mission without Lua).</summary>
    public LuaHost? Lua;

    public double Time;
    public bool Started;
    public readonly List<Trigger> Triggers = new();
    readonly Dictionary<string, Trigger> byGuid = new(StringComparer.Ordinal), byName = new(StringComparer.Ordinal);
    readonly List<Trigger> queue = new();
    public readonly List<FiringEntry> FiringLog = new();
    /// <summary>[trigger, node type, message] of everything that threw.</summary>
    public readonly List<(string Trigger, string Type, string Message)> Errors = new();
    public readonly List<string> Warnings = new();
    public double Boni;
    internal int FlushRound;

    // subscriptions
    readonly Dictionary<Channel, OrderedSet<Condition>> subs = new();
    readonly Dictionary<Channel, List<Condition>> always = new();
    internal readonly OrderedSet<Condition> Ticking = new(), Polled = new();
    int objVersion, objSeen;

    // folders (§1.5)
    readonly HashSet<string> inactive = new(StringComparer.Ordinal);
    /// <summary>how many folders are inactive now (the debug view; the JS tests read E.inactive.size)</summary>
    public int InactiveFolders => inactive.Count;

    // level state
    readonly Dictionary<string, string> vars = new(StringComparer.Ordinal);
    readonly List<string> varOrder = new();
    public readonly Dictionary<string, string> Profile = new(StringComparer.Ordinal);
    readonly Dictionary<string, QuestState> quests = new(StringComparer.Ordinal);
    /// <summary>Level timers by id (TIMR) and the shown TIME countdowns ("time:&lt;guid&gt;:&lt;n&gt;"): what the HUD shows.</summary>
    public readonly Dictionary<string, LevelTimer> Timers = new(StringComparer.Ordinal);
    readonly List<string> timerOrder = new();
    public string? InfoBar;
    public string InfoBarShown = "";
    /// <summary>MPNG markers added: id -> count.</summary>
    public readonly Dictionary<string, int> Markers = new(StringComparer.Ordinal);
    /// <summary>ACDO Invulnerability by region: object -> region key (actions.js invulnRegion).</summary>
    internal readonly Dictionary<int, string> InvulnRegion = new();
    public readonly List<int> Reveals = new();

    // sequences and dialogues
    readonly List<SequenceRequest> seqQueue = new();
    public SequenceRequest? SeqRunning;
    public string LastSequence = "";
    public int SequencesEnded;
    internal (string File, int Round)? SeqEnd;
    public readonly HashSet<string> DialogsEnded = new(StringComparer.Ordinal);
    readonly List<(int Token, string Path, Trigger? T)> dialogsOn = new();
    int dialogToken;

    // the end of the mission
    double overT = -1;
    public bool Won;
    public (bool Won, double T, string Reason)? Ended;
    string loseText = "";

    // delayed jobs on game time (data, not lambdas: saved games)
    internal enum JobKind { SpawnOut, CloseSpawner, LoadPassengers, DialogEnd }
    internal sealed class Job
    {
        public double At; public JobKind Kind; public Trigger? T;
        public int Src = -1, Level, Slot, Group = -1, Token;
        public string Cls = "", Passengers = "";
        public MapPos? Target;
    }
    readonly List<Job> jobs = new();

    readonly HashSet<string> unknownCond = new(), unknownAction = new();
    public int StatFired, StatActions, StatPolls;
    public List<string> RegionsFixed = new();

    public TriggerEngine(IMissionHost host, MissionScript script, LuaHost? lua = null)
    {
        Host = host; Script = script; Lua = lua;
        foreach (var (k, v) in script.Variables) SetVarRaw(k, v);
        foreach (var q in script.Quests) { if (q.Guid.Length > 0) quests[q.Guid] = q; quests.TryAdd(q.Name, q); }
        // ---- triggers (not compiled = does not exist for the game, §1.2: the loaders leave them out)
        for (int i = 0; i < script.Triggers.Count; i++)
        {
            var t = new Trigger(this, script.Triggers[i], i);
            Triggers.Add(t);
            if (t.Guid.Length > 0) byGuid[t.Guid] = t;
            byName.TryAdd(t.Name, t);
        }
        // ---- folders: a folder that directly holds a node_off trigger starts inactive (§1.5)
        foreach (var t in Triggers) if (t.NodeOff) inactive.Add(t.Folder);
        foreach (var t in Triggers) t.NodeActive = FolderActive(t.Folder);
        LoadProfile();
    }

    // ------------------------------------------------------------------------------------------- diagnostics
    internal Condition UnknownCondition(string type) { Unknown("condition", type); return new Conditions.NONE(); }
    internal void Unknown(string kind, string type)
    {
        var s = kind == "condition" ? unknownCond : unknownAction;
        if (!s.Add(type)) return;
        Warn($"trigger {kind} type {type} is not implemented ({(kind == "condition" ? "never true" : "does nothing")})");
    }
    public IReadOnlyCollection<string> UnknownConditions => unknownCond;
    public IReadOnlyCollection<string> UnknownActions => unknownAction;
    public void Warn(string msg)
    {
        if (Warnings.Contains(msg)) return;
        Warnings.Add(msg);
        L.Warn(msg);
    }
    public void Error(Trigger? t, string type, Exception e)
    {
        string msg = e is MoonSharp.Interpreter.InterpreterException ie ? (ie.DecoratedMessage ?? ie.Message) : e.Message;
        if (Errors.Count < 500) Errors.Add((t?.Name ?? "-", type, msg));
        L.Error($"trigger \"{t?.Name ?? "-"}\" {type}: {msg}", e is MoonSharp.Interpreter.InterpreterException ? null : e);
    }
    public void Guard(Trigger? t, string type, Action fn) { try { fn(); } catch (Exception e) { Error(t, type, e); } }

    // ------------------------------------------------------------------------------------------- subscriptions
    internal void Sub(Channel ch, Condition c)
    {
        if (!subs.TryGetValue(ch, out var s)) subs[ch] = s = new OrderedSet<Condition>();
        s.Add(c);
    }
    internal void Unsub(Channel ch, Condition c) { if (subs.TryGetValue(ch, out var s)) s.Remove(c); }
    /// <summary>Subscribed for the whole level (TIMR, SQEN, DSEN): pushed also while the trigger is disabled.</summary>
    internal void Forever(Channel ch, Condition c)
    {
        if (!always.TryGetValue(ch, out var s)) always[ch] = s = new List<Condition>();
        s.Add(c);
    }
    /// <summary>Calls f on every subscriber of a channel (live triggers) and every whole-level subscriber.</summary>
    internal void Push(Channel ch, Action<Condition> f)
    {
        if (subs.TryGetValue(ch, out var s) && s.Count > 0)
            foreach (var c in s.Snapshot()) if (c.T.Live) Guard(c.T, c.Type, () => f(c));
        if (always.TryGetValue(ch, out var a))
            foreach (var c in a.ToArray()) Guard(c.T, c.Type, () => f(c));
    }
    /// <summary>The set of objects changed (spawn, removal, owner, group, appearance): REGN / DEAD / BLDG re-check
    /// in the next Update.</summary>
    public void Touch() => objVersion++;

    // ------------------------------------------------------------------------------------------- events from the world
    // The host calls these when things happen (registry / world events, setup.js C.on ... in attach()).
    /// <summary>An object was created (spawned, produced).</summary>
    public void OnObjectSpawned(int obj) => Touch();
    /// <summary>The owner of an object changed.</summary>
    public void OnOwnerChanged(int obj) => Touch();
    /// <summary>A construction finished.</summary>
    public void OnBuilt(int obj) => Touch();
    /// <summary>An object is gone (died, destroyed or deleted; registry drop): DEAD / DYIN.</summary>
    public void OnObjectRemoved(int obj) { Touch(); Push(Channel.Removed, c => c.OnRemoved(obj)); }
    /// <summary>A unit died / a building was destroyed (world events died / destroyed), with its owner: PLDE.</summary>
    public void OnObjectLost(int obj, int owner) => Push(Channel.Lost, c => c.OnLost(obj, owner));
    /// <summary>A group gained or lost a member: CKGR, GREG.</summary>
    public void OnGroupChanged(int group, int member, bool added) { Touch(); Push(Channel.Group, c => c.OnGroup(group)); }
    /// <summary>Diplomacy changed (also by the player): DIPL conditions.</summary>
    public void OnDiplomacyChanged() => Push(Channel.Diplomacy, c => c.OnDiplomacy());
    /// <summary>A hit (world event "attacked"): ISFG.</summary>
    public void OnAttacked(int victim, int attacker) => Push(Channel.Attacked, c => c.OnAttacked(victim, attacker));
    /// <summary>A unit boarded a transporter: UNTT TRMO.</summary>
    public void OnBoarded(int unit, int transport) => Push(Channel.Boarded, c => c.OnBoarded(unit, transport));
    /// <summary>A unit took an item of a class: ITEM.</summary>
    public void OnItemTaken(string itemClass, int taker) => Push(Channel.Item, c => c.OnItem(itemClass, taker));
    /// <summary>A unit reached the end of its mode-0 waypoint path (StartPatrol): WAYR.</summary>
    public void OnWaypointReached(int obj) => Push(Channel.Wayr, c => c.OnWaypoint(obj));

    // ------------------------------------------------------------------------------------------- start
    public void Start()
    {
        if (Started) return;
        Started = true;
        FixRegions();
        // ---- §6.7 no. 7: every trigger with the flag `enabled` in an active folder is enabled, in stored order
        foreach (var t in Triggers.ToArray()) if (t.StartEnabled) Enable(t);
    }

    // A region that a trigger query names must be able to contain something. The data marks some shapes "not enabled"
    // (docs/CAMPAIGN_FORMAT.md §4: a flag bit decoded by correlation with the nests' extra areas that ARGN opens);
    // mission 8's `trigger_sc_3004` has that bit cleared, is tested by a live REGN and no ARGN ever touches it - in
    // the original the mission goes on there, so the bit cannot mean "disabled" for it. Such shapes are switched on.
    void FixRegions()
    {
        var argn = new HashSet<string>(StringComparer.Ordinal);
        var used = new List<string>();
        foreach (var t in Triggers)
        {
            foreach (var a in t.Actions) if (a.Type == "ARGN" || a.Type == "MRGN") argn.Add(a.P.S("rgn_guid"));
            foreach (var n in t.Conds.Select(c => (c.Type, c.P)).Concat(t.Actions.Select(a => (a.Type, a.P))))
                foreach (var (k, v) in n.P.All) if (k.EndsWith("rgn_guid", StringComparison.Ordinal) && n.Type != "ARGN" && !used.Contains(v)) used.Add(v);
        }
        foreach (var g in used)
        {
            if (argn.Contains(g) || Host.IsWorldRegion(g)) continue;
            if (Host.EnableDisabledShapes(g)) RegionsFixed.Add(g);
        }
    }

    // ------------------------------------------------------------------------------------------- core (§1.1)
    public bool FolderActive(string folder)
    {
        if (inactive.Count == 0) return true;
        var f = folder;
        for (; ; )
        {
            if (inactive.Contains(f)) return false;
            int i = f.LastIndexOf('/');
            if (i < 0) return true;
            f = f[..i];
        }
    }
    /// <summary>A trigger by GUID, else by name.</summary>
    public Trigger? Find(string? r) => r == null ? null : byGuid.GetValueOrDefault(r) ?? byName.GetValueOrDefault(r);
    public Trigger? ByGuid(string? g) => g == null ? null : byGuid.GetValueOrDefault(g);

    // start / stop listening. While a trigger is being enabled its conditions wake one after the other (OnEnabled);
    // the expression is evaluated ONCE when all of them have their fresh state, not at each condition's Invalidate:
    // otherwise a re-enabled trigger sees the stale states of the conditions that have not woken yet and fires at once
    // (mission 15's map ping pairs M0xa / M0xb would re-enable each other for ever).
    void Listen(Trigger t, bool on)
    {
        if (t.Live == on) return;
        t.Live = on;
        if (on) { t.Enabling = true; t.Dirty = false; }
        foreach (var c in t.Conds)
        {
            c.Live = on;
            Guard(t, c.Type, on ? c.OnEnabled : c.OnDisabled);
            if (t.Live != on) break;
        }
        if (!on) return;
        t.Enabling = false;
        if (t.Live && t.Dirty) Invalidate(t);
        foreach (var c in t.Conds) if (c.PulseOnEnable) { c.PulseOnEnable = false; c.State = 0; }
    }
    /// <summary>TRIG state 1 / level start. Already on: nothing happens, running timers go on (§8 no. 3).</summary>
    public void Enable(Trigger? t)
    {
        if (t == null || t.Enabled) return;
        t.Enabled = true;
        if (t.NodeActive) Listen(t, true);
    }
    public void Disable(Trigger? t)
    {
        if (t == null || !t.Enabled) return;
        t.Enabled = false;
        Listen(t, false);
    }
    /// <summary>Called by a condition whose state may have changed: evaluate now, fire at the end of the step.</summary>
    public void Invalidate(Trigger t)
    {
        if (!t.Live) return;
        if (t.Enabling) { t.Dirty = true; return; }
        if (!t.Evaluate()) return;
        if (t.Once) Disable(t);
        t.Queued++;
        queue.Add(t);
    }
    /// <summary>ACND (§1.5): deactivating covers the folders below, activating only this folder.</summary>
    public void SetNode(string? path, bool active)
    {
        path = (path ?? "").TrimEnd('/');
        if (path.Length == 0) return;
        if (active) inactive.Remove(path); else inactive.Add(path);
        foreach (var t in Triggers.ToArray())
        {
            if (t.Folder != path && !t.Folder.StartsWith(path + "/", StringComparison.Ordinal)) continue;
            bool a = FolderActive(t.Folder);
            if (a == t.NodeActive) continue;
            t.NodeActive = a;
            Listen(t, a && t.Enabled);
        }
    }
    /// <summary>End of the step: the queued triggers run their actions, first queued first (§1.1, §8 no. 2).</summary>
    public void Flush()
    {
        int n = 0;
        while (queue.Count > 0)
        {
            if (++n > 5000) { Error(queue[0], "loop", new Exception("more than 5000 firings in one step: queue dropped")); queue.Clear(); break; }
            var t = queue[0];
            queue.RemoveAt(0);
            t.Queued--;
            Fire(t);
        }
        FlushRound++;
    }
    /// <summary>Which actions of a firing run (§1.4).</summary>
    public List<TriggerAction> Pick(Trigger t)
    {
        var list = t.Actions;
        if (t.ByDifficulty) { int d = Host.Difficulty; list = list.Where(a => a.Difficulty == d).ToList(); }
        if (t.Random && list.Count > 0) list = new List<TriggerAction> { list[Math.Min(list.Count - 1, (int)Math.Floor(Host.Random() * list.Count))] };
        return list;
    }
    public void Fire(Trigger t, bool forced = false)
    {
        var list = Pick(t);
        t.Fired++; t.LastFired = Time;
        StatFired++;
        var entry = new FiringEntry(Math.Round(Time * 100) / 100, t.Name, list.Select(a => a.Type).ToArray(), forced);
        FiringLog.Add(entry);
        if (FiringLog.Count > 6000) FiringLog.RemoveRange(0, 1000);
        L.Debug(() => $"[{entry.Time:0.0}s] {t.Name}: {string.Join(' ', entry.Actions)}");
        foreach (var a in list) Run(t, a);
    }
    void Run(Trigger t, TriggerAction a)
    {
        StatActions++;
        try { a.Run(this, t); } catch (Exception e) { Error(t, a.Type, e); }
    }
    internal void After(double seconds, Job j) { j.At = Time + Math.Max(0, seconds); jobs.Add(j); }

    // ------------------------------------------------------------------------------------------- per simulation step
    /// <summary>One step of the simulation (phase 6 of World.Step): after the world events of the step have been
    /// reported (On...()), the timers count, polled conditions look, and the queue runs.</summary>
    public void Update(double dt)
    {
        if (!Started) Start();
        if (Ended != null && overT < 0) return;           // the mission has ended
        Time += dt;
        // delayed jobs
        if (jobs.Count > 0 && jobs.Exists(j => j.At <= Time))
        {
            var due = jobs.Where(j => j.At <= Time).ToList();
            if (due.Count > 0) { jobs.RemoveAll(j => j.At <= Time); foreach (var j in due) Guard(j.T, "job", () => RunJob(j)); }
        }
        if (overT >= 0) { overT -= dt; if (overT <= 0) { overT = -1; Host.EndMission(false, loseText); } return; }
        // level timers (§6.3)
        if (Timers.Count > 0)
            foreach (var id in _timerBuf.Reset(timerOrder))
            {
                if (!Timers.TryGetValue(id, out var tm) || tm.Paused || tm.Cond) continue;
                tm.Left -= dt;
                if (tm.Left <= 0) { RemoveTimer(id); Push(Channel.Timer, c => c.OnTimer(id)); }
            }
        // (the copies into lists kept by the engine, the guard inline: this runs every step)
        if (Ticking.Count > 0)
            foreach (var c in Ticking.SnapshotInto(_tickBuf))
                if (c.T.Live) { try { c.Tick(dt); } catch (Exception e) { Error(c.T, c.Type, e); } }
        // "objects changed" and the polled conditions
        bool changed = objVersion != objSeen;
        objSeen = objVersion;
        if (changed) Push(Channel.Objects, c => c.OnObjects());
        if (Polled.Count > 0)
            foreach (var c in Polled.SnapshotInto(_pollBuf))
            {
                if (!c.T.Live || c.NextPoll > Time) continue;
                c.NextPoll = Time + c.Every;
                StatPolls++;
                try { c.Poll(); } catch (Exception e) { Error(c.T, c.Type, e); }
            }
        SequenceTick();
        Flush();
    }

    readonly List<Condition> _tickBuf = new(), _pollBuf = new();
    readonly ListBuf<string> _timerBuf = new();
    sealed class ListBuf<T> { readonly List<T> _l = new(); public List<T> Reset(IEnumerable<T> src) { _l.Clear(); _l.AddRange(src); return _l; } }

    void RunJob(Job j)
    {
        switch (j.Kind)
        {
            case JobKind.SpawnOut: Actions.SPGR.SpawnOut(this, j); break;
            case JobKind.CloseSpawner: if (Host.Info(j.Src) != null) Host.PlayAnim(j.Src, "close", 1); break;
            case JobKind.LoadPassengers: Actions.CPLX.LoadPassengers(this, j); break;
            case JobKind.DialogEnd: EndDialog(j.Token); break;
        }
    }

    // ------------------------------------------------------------------------------------------- variables (§3)
    void SetVarRaw(string name, string v) { if (!vars.ContainsKey(name)) varOrder.Add(name); vars[name] = v; }
    /// <summary>A level variable's value, or null if it does not exist.</summary>
    public string? GetVarOrNull(string name) => vars.GetValueOrDefault(name);
    public string GetVar(string name) => vars.GetValueOrDefault(name) ?? "";
    public bool HasVar(string name) => vars.ContainsKey(name);
    public IEnumerable<KeyValuePair<string, string>> Variables => varOrder.Select(k => new KeyValuePair<string, string>(k, vars[k]));
    internal string? VarLookup(string name) => vars.GetValueOrDefault(name);

    // Variables of the player's profile (VARS / CVAR with local != 1): kept between missions. Only the names of
    // Server/settings/ProfileVariables.txt exist (ActionFactory.usl:4049-4062); the campaign uses Tutorial_Started
    // (the tutorial sets it; mission 1 then plays the short intro) and Level_1_Started.
    void LoadProfile()
    {
        for (int i = 0; i < ProfileNames.Length; i++) Profile[ProfileNames[i]] = ProfileDefaults[i];
        IReadOnlyDictionary<string, string>? p = null;
        try { p = Host.LoadProfile(); } catch (Exception e) { L.Warn("profile variables could not be read", e); }
        if (p != null) foreach (var (k, v) in p) if (Profile.ContainsKey(k)) Profile[k] = v;
    }
    static string Arith(string cur, string op, string val)
    {
        long a = TrigUtil.ToInt(cur), b = TrigUtil.ToInt(val), r = a;
        if (op == "+") r = a + b; else if (op == "-") r = a - b; else if (op == "*") r = a * b; else if (op == "/") { if (b != 0) r = a / b; }
        return r.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    public void SetProfileVar(string name, string op, string value)
    {
        if (!Profile.ContainsKey(name)) return;
        var val = TrigUtil.ValueString(value, VarLookup);
        Profile[name] = op == "set" || op == "=" || op == "" ? val : Arith(Profile[name], op, val);
        try { Host.SaveProfile(Profile); } catch (Exception e) { L.Warn("profile variables could not be saved", e); }
        VarsChanged();
    }
    /// <summary>VARS: set stores the string as it is; + - * / are integer arithmetic (a division by 0 changes nothing).</summary>
    public void SetVar(string name, string op, string value)
    {
        if (!vars.ContainsKey(name)) SetVarRaw(name, "0");
        var val = TrigUtil.ValueString(value, VarLookup);
        vars[name] = op == "set" || op == "=" || op == "" ? val : Arith(vars[name], op, val);
        VarsChanged();
    }
    /// <summary>BLDG with `variable`: the count written into a level variable (created if missing).</summary>
    internal void SetVarQuiet(string name, string v) => SetVarRaw(name, v);
    public void VarsChanged()
    {
        Push(Channel.Vars, c => c.OnVars());
        if (InfoBar != null) ShowInfoBar();
    }

    // ------------------------------------------------------------------------------------------- texts, info bar (§6.4)
    /// <summary>A text of the mission (texts/&lt;lang&gt;.json), else the key.</summary>
    public string Text(string key) => Script.Texts.TryGetValue(key, out var t) ? t : key;
    /// <summary>INBA: "key&lt;TAB&gt;arg1&lt;TAB&gt;arg2": %1, %2 of the text are the arguments; $(name) = a variable.</summary>
    public void SetInfoBar(string? text)
    {
        InfoBar = string.IsNullOrEmpty(text) ? null : text;
        ShowInfoBar();
    }
    public string InfoBarText()
    {
        if (InfoBar == null) return "";
        var parts = InfoBar.Split('\t');
        var s = Text(parts[0]);
        var args = parts.Skip(1).Select(a => TrigUtil.ValueString(a, VarLookup)).ToArray();
        return System.Text.RegularExpressions.Regex.Replace(s, @"%(\d)", m => { int d = m.Groups[1].Value[0] - '0'; return d >= 1 && d - 1 < args.Length ? args[d - 1] : m.Value; });
    }
    void ShowInfoBar() { InfoBarShown = InfoBarText(); var s = InfoBarShown; Guard(null, "INBA", () => Host.SetInfoBar(s)); }

    // ------------------------------------------------------------------------------------------- quests (§6.1)
    public IEnumerable<QuestState> Quests => Script.Quests;
    public QuestState? Quest(string? r) => r == null ? null : quests.GetValueOrDefault(r);
    /// <summary>quest_guid, fallback quest_name.</summary>
    public QuestState? Quest(TriggerParams p) => Quest(p["quest_guid"]) ?? Quest(p["quest_name"]);
    /// <summary>dest_state: 0 show, 1 accomplished, 2 unaccomplishable, 3 hide (ActionFactory.usl:2544-2642).</summary>
    public void SetQuest(QuestState? q, long state)
    {
        if (q == null) return;
        string? change = null;
        if (state == 0) { q.Accomplished = false; q.Visible = true; change = "shown"; }
        else if (state == 1)
        {
            if (!q.Accomplished) { int d = Host.Difficulty; Boni += d >= 0 && d < 3 ? q.Bonus[d] : 0; }
            q.Accomplished = true; q.Visible = true; change = "done";
        }
        else if (state == 2) { q.Unaccomplishable = true; q.Visible = true; change = "failed"; }
        else if (state == 3) { q.Visible = false; change = "hidden"; }
        if (change == null) return;
        Guard(null, "QUES", () => Host.QuestChanged(q, change));
        Push(Channel.Quest, c => c.OnQuest(q));
    }

    // ------------------------------------------------------------------------------------------- level timers (§6.3)
    public void Timer(string id, string ev, double duration = 0, bool show = true, string label = "")
    {
        Timers.TryGetValue(id, out var tm);
        if (ev == "create")
        {
            // creating an existing timer restarts it (ActionFactory.usl:6030-6072)
            var d = Math.Max(0, duration);
            if (tm == null) timerOrder.Add(id);          // (a Map keeps the place of a key that is set again)
            Timers[id] = new LevelTimer { Id = id, Left = d, Total = d, Show = show, Label = label.Length > 0 ? label : tm?.Label ?? "", Paused = false };
        }
        else if (ev == "pause") { if (tm != null) tm.Paused = true; }
        else if (ev == "unpause") { if (tm != null) tm.Paused = false; }
        else RemoveTimer(id);                       // "kill" and anything else
    }
    internal void SetCondTimer(LevelTimer t) { Timers[t.Id] = t; }
    internal void RemoveTimer(string id) { if (Timers.Remove(id)) timerOrder.Remove(id); }

    // ------------------------------------------------------------------------------------------- sequences, dialogues
    /// <summary>SQNZ: queued; the host shows it and calls EndSequence() when it is over or skipped. Then: the SQEN push
    /// (§4.3), quit = the mission is won (§5.1).</summary>
    public void PlaySequence(SequenceRequest r) => seqQueue.Add(r);
    void SequenceTick()
    {
        if (SeqRunning != null || seqQueue.Count == 0) return;
        if (Host.IsBusy) return;
        var r = seqQueue[0];
        seqQueue.RemoveAt(0);
        SeqRunning = r;
        bool playing = false;
        try { playing = Host.PlaySequence(r); } catch (Exception e) { Error(Find(r.Trigger), "SQNZ", e); }
        // a sequence without a file ends at once (§6.8)
        if (!playing) EndSequence();
    }
    /// <summary>The host: the running sequence is over (played or skipped).</summary>
    public void EndSequence()
    {
        var r = SeqRunning;
        if (r == null) return;
        SeqRunning = null;
        Guard(Find(r.Trigger), "SQNZ", () => Host.SequenceFinished(r));
        LastSequence = TrigUtil.FilePart(r.Path);
        SequencesEnded++;
        // (SQEN conditions enabled by the triggers this end fires still hear it: Conditions.SQEN)
        SeqEnd = (LastSequence, FlushRound);
        var f = LastSequence;
        Push(Channel.Sequence, c => c.OnSequence(f));
        if (r.Quit) Win();
    }
    public IReadOnlyList<SequenceRequest> SequenceQueue => seqQueue;

    /// <summary>DGSC: the host queues the scene; the game goes on. DSEN conditions hear its end.</summary>
    public void PlayDialog(string path, Trigger? t)
    {
        int token = ++dialogToken;
        dialogsOn.Add((token, path, t));
        bool shown = false;
        try { shown = Host.PlayDialog(path, token); } catch (Exception e) { Error(t, "DGSC", e); }
        if (!shown) After(0, new Job { Kind = JobKind.DialogEnd, Token = token, T = t });
    }
    /// <summary>The host: the dialogue scene with this token is over.</summary>
    public void EndDialog(int token)
    {
        int i = dialogsOn.FindIndex(d => d.Token == token);
        if (i < 0) return;
        var d = dialogsOn[i];
        dialogsOn.RemoveAt(i);
        var f = TrigUtil.FilePart(d.Path);
        DialogsEnded.Add(f);
        Push(Channel.Dialog, c => c.OnDialog(f));
    }
    public IEnumerable<(int Token, string Path)> DialogsRunning => dialogsOn.Select(d => (d.Token, d.Path));

    // ------------------------------------------------------------------------------------------- the end of a mission
    public void Win(string text = "")
    {
        Won = true;
        Ended = (true, Time, "");
        Guard(null, "QUIT", () => Host.EndMission(true, text));
    }
    /// <summary>GAOV: the defeat screen 4 s later (ActionFactory.usl:2466-2539); a second GAOV meanwhile is ignored.</summary>
    public void Lose(string? reasonKey, Trigger? by = null)
    {
        if (overT >= 0 || Ended != null) return;
        // which trigger lost the mission, and what made it fire - the first question after an unexpected defeat
        L.Info($"[{Time:0.0}s] mission lost by trigger {(by == null ? "(none)" : DebugRow(by))}; last firings:\n{DebugPrint(12)}");
        Ended = (false, Time, reasonKey ?? "");
        loseText = string.IsNullOrEmpty(reasonKey) ? "" : Text(reasonKey);
        overT = 4;
        Guard(null, "GAOV", Host.AiGameOver);
    }

    // ------------------------------------------------------------------------------------------- debug helpers
    /// <summary>A line per trigger: name, folder, state, conditions with their states, actions.</summary>
    public string DebugRow(Trigger t) =>
        $"{t.Name} [{t.Folder}] enabled={t.Enabled} live={t.Live} node={t.NodeActive} once={t.Once} fired={t.Fired} " +
        $"cond: {string.Join(' ', t.Conds.Select(c => c.Type + "=" + c.State))} expr: {(t.Def.Expression.Length > 0 ? t.Def.Expression : "(all)")} " +
        $"actions: {string.Join(' ', t.Actions.Select(a => a.Type))}";
    /// <summary>Run a trigger's actions now, whatever its conditions say (debug tools).</summary>
    public bool DebugFire(string name) { var t = Find(name); if (t == null) return false; if (t.Once) Disable(t); Fire(t, true); Flush(); return true; }
    public bool DebugEnable(string name, bool on = true) { var t = Find(name); if (t == null) return false; if (on) Enable(t); else Disable(t); Flush(); return true; }
    public string DebugPrint(int n = 50) => string.Join("\n", FiringLog.Skip(Math.Max(0, FiringLog.Count - n)).Select(e => $"{e.Time,7:0.0}  {e.Trigger}: {string.Join(' ', e.Actions)}"));
}
