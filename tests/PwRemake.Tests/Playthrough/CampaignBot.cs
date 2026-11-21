// Helpers for the scripted campaign playthroughs (Playthrough/PlayNN.cs derive from this class, as
// remake/tests/campaign_play_NN.js start with "// include: campaign_bot.js"). A playthrough is a list of steps [name,
// act each second, done, seconds allowed]; RunSteps() drives them and Report() prints the result. The helpers play with
// whatever tribe the mission runs with (Campaign/Tribes.cs): Own(cls) is the player's equivalent of a class the mission
// names.
//
// Cheats that keep a run short and deterministic (a player needs none of them), all counted in the report:
// heal the player's units, enemies near the player's fighters lose hit points (Cheats()), a group that makes no
// progress towards a goal for 60 s is teleported (Go()), resources topped up for a goal (TopUp()).
//
// Orders go through the world's commands (src/Sim/Commands), as a player's would: each command is applied at once
// between two steps (where the local sink applies it at the start of the next step - nothing happens in between), so
// the bot gets the reason back as the JS got the return value of W.order / W.queueAction / W.startConstruction (the
// computer player's CommandHands do the same). The cheats change the world directly, as the JS bot did.
//
// Ported from remake/tests/campaign_bot.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Rules;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using PwRemake.UI.Mission.Logic;
using DialogPlay = PwRemake.Sim.Campaign.DialogPlay;
using Camp = PwRemake.Sim.Campaign.Campaign;
using GameWorld = PwRemake.Sim.World.World;
using RulesData = PwRemake.Sim.Rules.Rules;

namespace PwRemake.Tests.Playthrough;

/// <summary>The mission UI of a playthrough: the game's mission UI logic (game/src/UI/Mission/Logic: MissionUiState
/// through MissionUiBridge) in test mode, as the JS ran its real mission UI with ?manual - dialogue scenes play on game
/// time (their frames' lengths from the texts: no sound files here), every cutscene ends on the next tick. It records
/// what it is asked (the JS wrapped G.mission's playSequence, playDialog, questChanged) and has a camera the
/// tutorial's UNTT CAME conditions watch (G.rtscam). Attach() after MissionSession.Create; Tick(dt) after every step.</summary>
public sealed class PlayPresenter : NullPresenter
{
    public readonly List<string> Seq = new(), Dlg = new(), Quest = new();
    public readonly List<(string Id, double T)> SeqAt = new(), DlgAt = new();
    public Func<double> Time = () => 0;
    /// <summary>map x, y, distance, yaw</summary>
    public double CamX, CamY, CamDist = 40, CamYaw;
    public MissionUiBridge? Ui;
    static string Id(string path) => System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
    static string R(double t) => CampaignBot.N(CampaignBot.Round(t));

    sealed class QuietOutput : IMissionUiOutput
    {
        public void Sound(string name) { }
        public IUiSound? PlayFile(string file, double vol, bool voice, double offset, bool loop) => null;
        public string? EventFile(string name) => null;
        public void Duck(bool on) { }
        public void MusicSequence(bool on) { }
        public bool Paused { get; set; }
        public bool FilmPlayable(string film) => false;
    }
    /// <summary>the mission's UI data (texts, dialogue scenes, sequences) and the engine the scenes report to</summary>
    public void Attach(string missionFolder, TriggerEngine engine, string lang)
    {
        var state = new MissionUiState(MissionUiData.Load(missionFolder, lang), new QuietOutput()) { Manual = true };
        Ui = new MissionUiBridge(state);
        Ui.Bind(engine);
    }
    /// <summary>game time (the JS: G.mission.tick(dt) in every simulation step, and its update)</summary>
    public void Tick(double dt) { if (Ui == null) return; Ui.State.Tick(dt); Ui.State.Update(dt); }

    public override bool IsBusy => Ui?.IsBusy ?? false;
    public override bool PlaySequence(SequencePlay play)
    {
        var id = Id(play.Request.Path); Seq.Add(id + "@" + R(Time())); SeqAt.Add((id, Time()));
        return Ui != null && Ui.PlaySequence(play.Request);
    }
    public override void SequenceEnded(SequencePlay play) => Ui?.SequenceFinished(play.Request);
    public override bool PlayDialog(DialogPlay d)
    {
        var id = Id(d.Path); Dlg.Add(id + "@" + R(Time())); DlgAt.Add((id, Time()));
        return Ui != null && Ui.PlayDialog(d.Path, d.Token);
    }
    public override void QuestChanged(QuestState q, string change) { Quest.Add((q.Name.Length > 16 ? q.Name[..16] : q.Name) + ":" + change + "@" + R(Time())); Ui?.QuestChanged(q, change); }
    public override void SetInfoBar(string text) => Ui?.SetInfoBar(text);
    public override (double X, double Y, double Dist, double Yaw)? Camera => (CamX, CamY, CamDist, CamYaw);
}

/// <summary>A step of a playthrough: [name, act each second, done, seconds allowed].</summary>
public sealed record Step(string Name, Action Act, Func<bool> Done, double Limit);

/// <summary>Options of RunSteps (the JS runSteps(steps, opts)).</summary>
public sealed class RunOpts
{
    public bool Cheats = true;
    public CheatOpts CheatOpts = new();
}

/// <summary>Options of Cheats() (the JS cheats(opts)): friends kept above 80 %, noDamage, spare(e).</summary>
public sealed class CheatOpts
{
    public List<Player?> Friends = new();
    public bool NoDamage;
    public Func<Entity, bool>? Spare;
}

public abstract class CampaignBot
{
    public readonly MissionSession S;
    public readonly PlayPresenter UiP;
    protected readonly Camp C;
    protected readonly GameWorld W;
    protected readonly TriggerEngine E;
    protected readonly Registry R;
    protected readonly RulesData Ru;
    protected readonly Player me;
    protected readonly List<string> Out = new();
    /// <summary>what the mission UI was asked (ui.seq, ui.dlg, ui.quest)</summary>
    protected (List<string> seq, List<string> dlg, List<string> quest) ui => (UiP.Seq, UiP.Dlg, UiP.Quest);
    public int Teleports, Kills, TopUps;
    /// <summary>a step the bot reached at (for the runner's console): name, game time</summary>
    public Action<string>? Progress;

    protected CampaignBot(MissionSession s, PlayPresenter p)
    {
        S = s; UiP = p; C = s.Campaign; W = s.World; E = s.Engine ?? throw new InvalidOperationException("a playthrough needs the trigger engine");
        R = C.Objects; Ru = W.Data; me = s.Human ?? throw new InvalidOperationException("the mission has no human player");
        p.Time = () => W.Time;
        if (p.Ui == null) p.Attach(s.Lua.Folder, E, s.Config.Language);
        // the camera starts where the mission puts it (map coordinates)
        var (cx, cy) = C.ToMap(s.StartCamera.X, s.StartCamera.Z);
        p.CamX = cx; p.CamY = cy; p.CamYaw = s.StartCamera.Yaw;
    }

    /// <summary>the playthrough: its steps, RunSteps, Report (the body of campaign_play_NN.js)</summary>
    public abstract string Play();

    // ---------------------------------------------------------------- small things of the JS
    public static double Round(double v) => Math.Floor(v + 0.5);                      // Math.round
    public static string N(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e15 ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);
    protected static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
    protected void ok(bool c, string m) => Out.Add($"{(c ? "ok" : "FAIL")} {m}");
    protected void info(string m) => Out.Add("   " + m);
    /// <summary>PW_PLAY_STUCKLOG=1: a teleported unit's state is written before it is moved (where it stands, its order,
    /// its way, the ground under it, what stands round it) - why it made no progress</summary>
    static readonly bool StuckLog = Environment.GetEnvironmentVariable("PW_PLAY_STUCKLOG") == "1";
    string StuckInfo(Unit u, double gx, double gz)
    {
        var nav = W.NavFor(u); var k = u.Task;
        string P(double a, double b) { var (mx, my) = C.ToMap(a, b); return $"{N(Round(mx))},{N(Round(my))}"; }
        var sb = new System.Text.StringBuilder();
        sb.Append($"  stuck {u.Name}#{u.Id} at {P(u.Pos.X, u.Pos.Z)} (game {u.Pos.X:0.#},{u.Pos.Z:0.#}) r{u.Radius:0.##} naval {u.Naval} block {nav.Block[nav.Idx(u.Pos.X, u.Pos.Z)]} vel {Hypot(u.Vel.X, u.Vel.Z):0.##}; task {k.Type}{(k.Target != null ? " -> " + k.Target.Name + "#" + k.Target.Id + " at " + P(k.Target.Pos.X, k.Target.Pos.Z) : "")} ({k.X:0.#},{k.Z:0.#}) phase {k.Phase}; goal {(u.Goal is var (ox, oz) ? P(ox, oz) : "-")} noway {(u.NoWay != null)} breach {u.Breach?.Name}; path {u.Path.Count}");
        if (u.Path.Count > 0) sb.Append(": " + string.Join(" ", u.Path.Take(6).Select(w => P(w.X, w.Z))) + (u.Path.Count > 6 ? " ..." : ""));
        if (k.Target != null) { var zh = W.RangeZoneOf(u, k.Target); sb.Append($"; target hp {k.Target.Hp:0}/{k.Target.MaxHp:0} y {k.Target.Pos.Y:0.#} (own y {u.Pos.Y:0.#}) untargetable {k.Target.Untargetable} canTarget {W.CanTarget(u, k.Target)} zone {zh.Zone} d {zh.D:0.#} min {zh.Min} high {zh.High} los {zh.Los}; chasing {k.Chasing} fails {k.Fails} user {k.User} lastHitDone {W.Time - u.LastHitDone:0.#} s ago; weapon {u.Weapons.Long?.Range}"); }
        var p = nav.Find(u.Pos.X, u.Pos.Z, gx, gz, u.Radius, 30000, u.Owner);
        sb.Append($"; fresh search found {nav.Found} ({p?.Count ?? 0} legs)");
        var near = W.Units.Where(o => o != u && o.Alive && !o.Parked && !o.Inside && Hypot(o.Pos.X - u.Pos.X, o.Pos.Z - u.Pos.Z) < u.Radius + o.Radius + 3).Take(6);
        sb.Append("; near: " + string.Join(", ", near.Select(o => $"{o.Name}#{o.Id}({o.Owner?.Name}) {Hypot(o.Pos.X - u.Pos.X, o.Pos.Z - u.Pos.Z):0.#}m {o.Task.Type}")));
        var nb = W.Buildings.Where(b => b.Alive && Hypot(b.Pos.X - u.Pos.X, b.Pos.Z - u.Pos.Z) < b.Radius + u.Radius + 4).Take(4);
        sb.Append("; buildings: " + string.Join(", ", nb.Select(b => $"{b.Name}({b.Owner?.Name}) {Hypot(b.Pos.X - u.Pos.X, b.Pos.Z - u.Pos.Z):0.#}m")));
        return sb.ToString();
    }
    protected bool Over => S.Over;
    /// <summary>JSON.stringify(G.endInfo)</summary>
    protected string EndInfo => S.Host.Ended is { } en ? $"{{\"won\":{(en.Won ? "true" : "false")},\"text\":\"{en.Text}\"}}" : "undefined";
    protected static double Invuln(Entity e) => e is Unit u ? u.InvulnT : e is Building b ? b.InvulnT : 0;
    protected IEnumerable<Entity> All() => W.Units.Cast<Entity>().Concat(W.Buildings);
    /// <summary>C.variables.get(n) (null when unset)</summary>
    protected string? Var(string n) => E.GetVarOrNull(n);
    /// <summary>+C.variables.get(n) || 0</summary>
    protected double v(string n) => double.TryParse(E.GetVarOrNull(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    protected Trigger? T(string prefix) => E.Triggers.FirstOrDefault(t => t.Name.StartsWith(prefix, StringComparison.Ordinal));
    protected bool firedP(string prefix) { var t = T(prefix); return t != null && t.Fired > 0; }
    /// <summary>the living members of a group (R.group(name).list())</summary>
    protected List<Entity> grp(string name)
    {
        var g = R.GroupOf(name);
        return g == null ? new List<Entity>() : g.List().Select(r => r.Entity).Where(e => e != null && e.Alive).Cast<Entity>().ToList();
    }
    protected Func<Entity, bool> inGroup(string name) { var s = grp(name).ToHashSet(); return e => s.Contains(e); }
    protected (int, int) ToMapR(double x, double z) { var (mx, my) = C.ToMap(x, z); return ((int)Round(mx), (int)Round(my)); }
    protected string Brains() => string.Join(" ", W.Brains.Values.OfType<TribeAI>().Select(b => b.P.Id + ":" + b.Behaviour + (b.Launched.Count > 0 ? " attacks " + b.Launched.Count : "")));
    protected TribeAI? Brain(int id) => W.Brains.TryGetValue(id, out var b) ? b as TribeAI : null;

    // ---------------------------------------------------------------- commands (as the player)
    /// <summary>a command of the human player, applied now; null = done, else the reason</summary>
    protected string? Do(Command c)
    {
        c.Player = me.Id; c.Step = W.StepNo;
        string? why = c.Apply(W);
        W.Recorded?.Add(c);
        return why;
    }
    protected void Order(IEnumerable<Unit> units, string type, double x = 0, double z = 0, Entity? target = null)
    {
        var ids = units.Where(u => u != null).Select(u => u.Id).ToArray();
        if (ids.Length == 0) return;
        Do(new OrderCommand { Units = ids, Type = type, X = x, Z = z, Target = target?.Id ?? 0 });
    }
    protected void Order(Unit u, string type, double x = 0, double z = 0, Entity? target = null) => Order(new[] { u }, type, x, z, target);
    protected string? QueueAction(Entity producer, ActionDef a) => Do(new QueueActionCommand { Producer = producer.Id, Action = a.Path, Count = 1 });
    protected string? ChangeLevel(Unit u, int level) => Do(new ChangeLevelCommand { Unit = u.Id, Level = level });

    // ---------------------------------------------------------------- what is there
    /// <summary>the player's equivalent of a class the mission names (null: no equivalent)</summary>
    protected string? own(string cls)
    {
        var t = TribeSwap.TribeOfClass(cls.ToLowerInvariant());
        if (t == null || t == me.Tribe) return cls;
        if (C.Swap != null && t == C.MissionTribe) { var (tribal, e) = C.Swap.EquivalentOf(cls); return tribal ? e : cls; }
        // (a class of a third tribe - the helpers name the Norse ones, hu_stone_cottage: the player's equivalent too, also
        // in a mission played with its own tribe, where the JS returned the Norse class - a Dustrider base in mission 6
        // never got a tent)
        if (!_swaps.TryGetValue(t, out var sw)) _swaps[t] = sw = new TribeSwap(Ru, t, me.Tribe);
        var (tr2, e2) = sw.EquivalentOf(cls);
        return tr2 ? e2 : cls;
    }
    readonly Dictionary<string, TribeSwap> _swaps = new(StringComparer.Ordinal);
    /// <summary>the player's - in a co-op game (docs/COOP.md: a co-op playthrough) every human seat's: the bot plays the humans together</summary>
    protected bool ours(Entity e) => e.Owner != null && (e.Owner == me || C.Coop?.SeatOf(e.Owner) is { Ai: false });
    protected List<Unit> mine() => W.Units.Where(u => u.Alive && ours(u) && !u.Parked && !u.Inside).ToList();
    protected List<Building> myBuildings() => W.Buildings.Where(b => b.Alive && b.Owner == me).ToList();
    /// <summary>not the Aje resource collector, carts, transports ...</summary>
    protected List<Unit> fighters() => mine().Where(u => !u.IsWorker && !u.CannotFight).ToList();
    protected List<Unit> workers() => mine().Where(u => u.IsWorker).ToList();
    protected List<Unit> heroes() => mine().Where(u => u.Def != null && u.Def.Unique).ToList();
    protected Unit? unit(string? cls) => W.Units.FirstOrDefault(u => u.Alive && u.Name == cls && ours(u));
    protected Building? building(string cls) { var c = own(cls); return myBuildings().FirstOrDefault(b => b.Name == c); }
    protected bool fired(string n) => (E.Find(n)?.Fired ?? -1) > 0;
    protected QuestState? quest(string n) => E.Quests.FirstOrDefault(q => q.Name == n);
    protected string questState(string n) { var x = quest(n); return x == null ? "?" : x.Accomplished ? "done" : x.Unaccomplishable ? "failed" : x.Visible ? "open" : "hidden"; }
    /// <summary>a region's centre, game coordinates</summary>
    protected (double X, double Z)? regionC(string name) => C.Regions.Find(name)?.Center();
    protected bool inRegion(Entity? e, string name) { var r = C.Regions.Find(name); return e != null && r != null && r.Contains(e.Pos.X, e.Pos.Z); }
    protected Entity? named(string n) { var r = R.ByName(n); return r?.Entity is { Alive: true } e ? e : null; }

    // ---------------------------------------------------------------- orders
    sealed class Stuck { public double D, T, X, Z, Seen; public string At = ""; }
    readonly Dictionary<Unit, Stuck> stuck = new();
    /// <summary>send units to a map point; a unit that does not get closer for 60 s is teleported there (reported)</summary>
    protected bool go(IEnumerable<Unit?> units0, double mx, double my, string key = "all")
    {
        var units = units0.Where(u => u != null).Cast<Unit>().ToList();
        var (x, z) = C.ToGame(mx, my);
        // there: within 6 m of the goal, plus the room the group takes (n units stand round a point in a disc of about
        // sqrt(n) m: mission 12's army of 20 at the tower ruin, a hero jostled 6-7 m from it for a minute, moving, was
        // teleported), plus its own radius for a big unit (a chariot, radius 2.3, stood 7 m from the ruin behind the
        // workers and soldiers nearer it, where its walk ends - Unit.QueuedBehind). A goal on ground the unit cannot stand
        // on (a building, the rocks round mission 9's launch ramp) is measured from the ring of free ground round it -
        // the distance from the goal to the nearest free cell, where the units' own walk ends (Nav.Find) - not from the
        // point: mission 9's army sent to the launch ramp's centre, 20 m inside the blocked ground round it, stood at its
        // edge guarding it and was teleported onto it again and again (45 times a run)
        // (a building's footprint: from its walls - a long one's far side lies beyond the ring of its nearest free cell)
        var on = W.Buildings.FirstOrDefault(b => b.Alive && !b.Parked && b.Boxes.Any(q => q[2] > 0.5 && q[3] > 0.5) && b.SurfDist(x, z) <= 0);
        var ring = new Dictionary<NavGrid, double>();
        double dist(Unit u)
        {
            var nav = W.NavFor(u);
            if (!ring.TryGetValue(nav, out var r0))
            {
                int c = nav.Idx(x, z), f = nav.Block[c] != 0 ? nav.NearestFree(c) : c;
                var (fx, fz) = nav.Center(f);
                ring[nav] = r0 = f == c || nav.Block[f] != 0 ? 0 : Math.Max(0, Hypot(fx - x, fz - z) - nav.Cell);
            }
            double dr = Math.Max(0, Hypot(u.Pos.X - x, u.Pos.Z - z) - r0);
            return on != null ? Math.Min(dr, Math.Max(0, on.SurfDist(u.Pos.X, u.Pos.Z))) : dr;
        }
        double there = 6 + Math.Sqrt(units.Count);
        var far = units.Where(u => dist(u) > there + (u.Radius > 1 ? u.Radius : 0)).ToList();
        // (a unit with no way there carried across takes the others of the group with no way there along - a transport's
        // load: one by one, as their counts ran out, mission 9's army was spread over three islands, and the heroes alone
        // at the airplane fell; mission 15's army came to the stations piecemeal)
        bool carry = false; var moved = new HashSet<Unit>();
        foreach (var u in far)
        {
            double d = dist(u);
            var s = stuck.TryGetValue(u, out var have) ? have : new Stuck { D = 1e9, T = W.Time, At = "", X = u.Pos.X, Z = u.Pos.Z, Seen = W.Time };
            var at = N(mx) + "," + N(my);
            // the count runs only while go() keeps sending the unit: one that go() left alone for more than 10 s - it got
            // there (within 6 m), or the playthrough had it do something else (hunt()'s attack orders: mission 12's fleet
            // shooting a carrier for a minute, standing) - starts it anew from where it stands. Its old count had it
            // teleported the moment go() sent it on again (mission 12: every fight of the fleet ended with the whole
            // fleet teleported, the army that had stood guard at the tower ruin was teleported when the next step sent
            // it home - 25 of the run's 25 teleports, none of them a unit that could not get on)
            if (W.Time - s.Seen > 10) { s.D = d; s.T = W.Time; s.X = u.Pos.X; s.Z = u.Pos.Z; }
            s.Seen = W.Time;
            // progress: nearer the goal, or (a goal that moves - an enemy being hunted, a new step's point) 8 m away from
            // where the unit stood when the count began. (Not the JS's "a new goal starts the count anew": a unit that
            // cannot get across water to enemies walking about on the other side was never teleported - mission 15's
            // soldiers from the base island, which the stations' crater is cut off from by a channel 8 m deep)
            // (moving 8 m does not count for a unit that has no way there - Unit.NoWay: one sent from mission 9's village
            // basin, cut off from the launch ramp 400 m away, wandered about it - the failed search ends elsewhere from
            // each place - and its count never ran out: MIRAGE's workers never got to the ramp)
            // (and coming nearer counts for it only by 8 m: since units sent to a goal they cannot reach walk to the point
            // nearest it and stop - Nav.Find, 2026-11-20 - mission 15's army sent from the base island to the first
            // station, 220 m across the channel, crowded at the shore; jostling there, one or another came 2 m nearer now
            // and then, and they were teleported one by one over minutes instead of together a minute after they got
            // there: the stations were taken piecemeal and lost again - the Dragon Clan lost the run)
            bool way = u.NoWay == null;
            if ((s.At == at && d < s.D - (way ? 2 : 8)) || (way && Hypot(u.Pos.X - s.X, u.Pos.Z - s.Z) > 8)) { s.D = d; s.T = W.Time; s.X = u.Pos.X; s.Z = u.Pos.Z; }
            else if (s.At != at) s.D = d;
            s.At = at;
            stuck[u] = s;
            if (StuckLog && W.Time - s.T > 20 && (int)(W.Time - s.T) % 5 == 0) { var k0 = u.Task; var (ux, uy) = C.ToMap(u.Pos.X, u.Pos.Z); info($"  ..{N(Round(W.Time))} s {key} {u.Name}#{u.Id} at {N(Round(ux))},{N(Round(uy))} no progress {N(Round(W.Time - s.T))} s: task {k0.Type}{(k0.Target != null ? " " + k0.Target.Name + "#" + k0.Target.Id + " hp " + N(Round(k0.Target.Hp)) : "")} user {k0.User} path {u.Path.Count} vel {Hypot(u.Vel.X, u.Vel.Z):0.##}"); }
            if (W.Time - s.T > 60)
            {
                // (not in the JS: a goal with no free ground of the unit's kind within 80 m - a ship at sea - is no place
                // to put it: World.Teleport would leave a land unit in the open sea, where it sank mission 7's SEAS convoy
                // before it set out and the quest could not be finished any more; the unit stays where it got to)
                var nav = W.NavFor(u);
                if (nav.Block[nav.NearestFree(nav.Idx(x, z))] != 0) { info($"no teleport ({key}: {u.Name}) to {at} at {N(Round(W.Time))} s: no free ground there"); s.T = W.Time; s.D = 0; continue; }
                if (StuckLog) { var (sx0, sz0) = C.ToMap(s.X, s.Z); info(StuckInfo(u, x, z) + $"; count began {N(Round(W.Time - s.T))} s ago at {N(Round(sx0))},{N(Round(sz0))}, best distance {N(Round(s.D))}, now {N(Round(d))}"); }
                C.Teleport(Registry.Get(u), mx, my);       // (as the JS: C.teleport(u.rec || u) - a unit without a record stays)
                Teleports++; info($"teleport ({key}: {u.Name}) to {at} at {N(Round(W.Time))} s"); s.T = W.Time; s.D = 0;
                if (u.NoWay != null) carry = true;
                moved.Add(u); continue;
            }
            // (the JS set the task's goal to the point after the order and skipped units walking there; the order's
            // own point is the point with the unit's offset)
            double ox = x + (u.Id % 3 - 1) * 1.5, oz = z + ((u.Id >> 2) % 3 - 1) * 1.5;
            var k = u.Task;
            if (k.Type == "move" && Math.Abs(k.X - ox) < 1 && Math.Abs(k.Z - oz) < 1) continue;
            Order(u, "move", ox, oz);
        }
        if (carry)
            foreach (var u in far)
            {
                if (moved.Contains(u) || u.NoWay == null || !u.Alive || u.Inside) continue;
                var s = stuck[u];
                C.Teleport(Registry.Get(u), mx, my);
                Teleports++; info($"teleport ({key}: {u.Name}) to {N(mx)},{N(my)} at {N(Round(W.Time))} s (with the group)"); s.T = W.Time; s.D = 0;
            }
        return far.Count == 0;
    }
    protected bool go(IEnumerable<Unit?> units, (int X, int Y) at, string key = "all") => go(units, at.X, at.Y, key);
    protected bool goRegion(IEnumerable<Unit?> units, string name, string key = "all")
    {
        var c = regionC(name) ?? throw new InvalidOperationException("no region " + name);
        var (mx, my) = C.ToMap(c.X, c.Z);
        return go(units, Round(mx), Round(my), key);
    }
    /// <summary>hunt(): units more than 60 m from the target walk there with go() (and its teleport) - a playthrough
    /// whose army is trained on an island switches it on (Play15)</summary>
    protected bool HuntFarGo;
    /// <summary>go after the nearest living target that passes the filter (the cheat damage helps)</summary>
    protected bool hunt(IEnumerable<Unit?> units0, Func<Entity, bool> filter, string key = "all")
    {
        var units = units0.Where(u => u != null).Cast<Unit>().ToList();
        if (units.Count == 0) return false;
        var c = units[0];
        Entity? best = null; double bd = 1e9;
        foreach (var e in All().ToList())
        {
            if (!e.Alive || e.Parked || e.Inside || !filter(e)) continue;
            double d = Hypot(e.Pos.X - c.Pos.X, e.Pos.Z - c.Pos.Z);
            if (d < bd) { bd = d; best = e; }
        }
        if (best == null) return false;
        if (bd - best.Radius < 25)
        {
            // (the ones far from it walk there with go(), whose teleport takes them on when they cannot get there - an
            // attack order across water left them standing on the other shore: mission 15's soldiers from the base island)
            var farU = HuntFarGo ? units.Where(u => Hypot(u.Pos.X - best.Pos.X, u.Pos.Z - best.Pos.Z) - best.Radius > 60).ToList() : new List<Unit>();
            foreach (var u in units) if (!farU.Contains(u) && (u.Task.Type != "attack" || u.Task.Target != best)) Order(u, "attack", target: best);
            if (farU.Count > 0) { var (fx, fy) = C.ToMap(best.Pos.X, best.Pos.Z); go(farU, Round(fx), Round(fy), key); }
            return true;
        }
        var (mx, my) = C.ToMap(best.Pos.X, best.Pos.Z);
        go(units, Round(mx), Round(my), key);
        return true;
    }
    static readonly Regex AggressiveWild = new("Smilodon|Dilophosaurus", RegexOptions.Compiled);
    /// <summary>the player's units are healed (and those of opts.Friends kept above 80 %); enemies within 12 m of a
    /// fighter lose a fifth of their hit points per call (opts.Spare(e): leave that one alone)</summary>
    protected void Cheats(CheatOpts? opts = null)
    {
        opts ??= new CheatOpts();
        var M = mine();
        foreach (var u in M) u.Hp = u.MaxHp;
        if (Environment.GetEnvironmentVariable("PW_PLAY_NO_FRIEND_CHEAT") == null)   // (off: friends fight unaided, as in a real game)
        foreach (var p in opts.Friends) if (p != null) foreach (var u in W.Units) if (u.Alive && u.Owner == p) u.Hp = Math.Max(u.Hp, u.MaxHp * 0.8);
        if (opts.NoDamage) return;
        var F = M.Where(u => !u.IsWorker).ToList();
        if (F.Count == 0) return;
        bool Near(Entity e) { foreach (var u in F) if (Hypot(u.Pos.X - e.Pos.X, u.Pos.Z - e.Pos.Z) < 12 + e.Radius) return true; return false; }
        foreach (var e in All().ToList())
        {
            if (!e.Alive || e.Parked || e.Inside || e.Untargetable || Invuln(e) > 0 || !F[0].IsEnemy(e) || !Near(e)) continue;
            if (e.Owner == null && e.Kind == "unit" && e.Def != null && e.Def.Aggressive != 1 && !AggressiveWild.IsMatch(e.Name)) continue;   // peaceful wildlife is left alone
            if (opts.Spare != null && opts.Spare(e)) continue;
            W.SetHp(e, e.Hp - e.MaxHp * 0.2);
            if (!e.Alive) Kills++;
        }
    }

    // ---------------------------------------------------------------- economy
    protected static bool Makes(ActionDef a, string want) => a.Id.ToLowerInvariant() == want || a.Results.Any(r => r.Obj.ToLowerInvariant() == want);
    /// <summary>the action that makes class `cls` (a building, a unit, an upgrade id) for the player</summary>
    protected ActionDef? actionFor(string? cls, string kind = "Build")
    {
        var want = (cls ?? "").ToLowerInvariant();
        // (the action of that id first: MIRAGE's aje_huge_farm lists aje_small_farm among its results and came first -
        // the JS took the first action naming the class either way, written for the base game's data)
        return Ru.Actions(me).FirstOrDefault(a => a.Kind == kind && a.Id.ToLowerInvariant() == want) ?? Ru.Actions(me).FirstOrDefault(a => a.Kind == kind && Makes(a, want));
    }
    /// <summary>what an object of the player offers: its own tree (a building's local upgrades - the Dustriders' farm
    /// modes - show actions the player's tree hides; Rules.ActionsOf with the object's rules owner, as the HUD and the
    /// computer player ask)</summary>
    protected List<ActionDef> ActionsOf(Entity b) => Ru.ActionsOf(b.RulesOwner(), b.Name, b.Kind, b.Def?.Type ?? b.Cls);
    string StartConstruction(ActionDef a, Placement pl, int n)
    {
        var ws = workers().OrderBy(u => Hypot(u.Pos.X - pl.X, u.Pos.Z - pl.Z)).Take(n).Select(u => u.Id).ToArray();
        return Do(new PlaceBuildingCommand { Action = a.Path, X = pl.X, Z = pl.Z, Rot = pl.Rot, Workers = ws }) ?? "started";
    }
    /// <summary>put up buildings of class cls (the player's equivalent) near map point (mx, my) with `workers` workers
    /// each until the player has `count` of them (standing or being built): true then, else the reason it cannot start
    /// (cost, req, place ...) or "started". region: only places inside that region</summary>
    protected object build(string cls, double mx, double my, int workers = 2, int count = 1, string? region = null)
    {
        var c = own(cls);
        if (c == null) return "none";
        if (myBuildings().Count(b => b.Name == c && (region == null || inRegion(b, region))) >= count) return true;
        var a = actionFor(c);
        if (a == null) return "no action";
        var (x0, z0) = C.ToGame(mx, my);
        for (int k = 0; k < 400; k++)                // up to 206 m away: a crowded village leaves no room for a big one (another tribe's)
        {
            double r = 6 + k * 0.5, ang = k * 2.4;
            // (turned a quarter when it does not fit as it is: a long building in a narrow place)
            var pl = W.PlacementOf(c, x0 + Math.Cos(ang) * r, z0 + Math.Sin(ang) * r, 0, me);
            if (!pl.Ok && pl.Existing == null) pl = W.PlacementOf(c, x0 + Math.Cos(ang) * r, z0 + Math.Sin(ang) * r, Math.PI / 2, me);
            if (!pl.Ok || pl.Existing != null) continue;
            if (region != null && !C.Regions.Find(region)!.Contains(pl.X, pl.Z)) continue;
            return StartConstruction(a, pl, workers);
        }
        // no room around the spot: the nearest free place anywhere the player may build (an 8 m grid over the map)
        if (region == null)
        {
            double H = W.Map.Size / 2;
            var cand = new List<(double D, double X, double Z)>();
            for (double x = -H + 16; x < H - 16; x += 8) for (double z = -H + 16; z < H - 16; z += 8) cand.Add((Hypot(x - x0, z - z0), x, z));
            foreach (var (_, x, z) in cand.OrderBy(q => q.D))
            {
                var pl = W.PlacementOf(c, x, z, 0, me);
                if (!pl.Ok || pl.Existing != null) continue;
                return StartConstruction(a, pl, workers);
            }
        }
        return "place";
    }
    protected object build(string cls, (int X, int Y) at, int workers = 2, int count = 1, string? region = null) => build(cls, at.X, at.Y, workers, count, region);
    /// <summary>queue n units / upgrades of class cls at whatever of the player's offers it (buildings, and units that
    /// produce: the Dustriders' resource collector); returns how many were queued. An upgrade that waits for buildings
    /// (the Dragon Clan's epochs need a lumber mill and a dojo) gets them built near the main building.</summary>
    protected int produce(string cls, int n = 1, string kind = "Build")
    {
        var c = (own(cls) ?? cls).ToLowerInvariant();
        int k = 0; ActionDef? blocked = null; bool anyOk = false;
        foreach (var b in myBuildings().Cast<Entity>().Concat(mine().Where(u => !u.IsWorker)).ToList())
        {
            if (b is Building bb && !bb.Built) continue;
            foreach (var a in ActionsOf(b))
            {
                if (a.Kind != kind || a.Cat == "Build/BLDG" || !Makes(a, c)) continue;
                var ck = Ru.Check(me, a, b is Unit ? b : null);
                if (ck == "req" || ck == "hidden") { blocked ??= a; continue; }      // hidden: shown only from the epoch it needs (the first: the lowest level's)
                anyOk = true;
                // an upgrade the player cannot pay yet that also costs skulls (the Dustriders' epochs): the skulls are
                // topped up (a cheat, counted) - a player would hunt for them
                if (kind == "Upgrades" && a.Cost.Skulls > me.Res["skulls"]) TopUp(new() { ["skulls"] = a.Cost.Skulls });
                // an upgrade that costs more than the stores hold (the Dustriders' epoch 3: 700 food and wood, the collector
                // and its tents hold 500): the stock for it (a cheat, counted) - a player would build a store first
                if (kind == "Upgrades" && (a.Cost.Food > me.Caps["food"] || a.Cost.Wood > me.Caps["wood"] || a.Cost.Stone > me.Caps["stone"]))
                    TopUp(new() { ["food"] = a.Cost.Food, ["wood"] = a.Cost.Wood, ["stone"] = a.Cost.Stone });
                while (k < n && QueueAction(b, a) == null) k++;
                if (k >= n) return k;
            }
        }
        // what it waits for: a building is put up, an epoch researched (the Dustriders' warriors come with epoch 2)
        // (only when nothing of it can be made now: a unit that only waits for its cost does not send the base after its
        // higher levels' epochs)
        if (k == 0 && blocked != null && !anyOk)
        {
            prerequisites(blocked, kind == "Upgrades");
            // hidden without a requirement of its own: shown by an epoch's modificators (MIRAGE's Dustrider warriors and
            // archers, aje_warrior_1 / aje_archer_1 at the rodeo, appear with epoch 2) - the next epoch
            if (kind != "Upgrades" && Ru.Check(me, blocked, null) == "hidden" && Ru.Missing(me, blocked).Count == 0 && me.Epoch() < 5)
                produce("age_" + (me.Epoch() + 1), 1, "Upgrades");
        }
        // nothing the player has makes it: put up a building that does (the Norse barracks for warriors)
        if (k == 0 && blocked == null)
        {
            var a = Ru.Actions(me).FirstOrDefault(x => x.Kind == kind && x.Cat != "Build/BLDG" && Makes(x, c));
            var at = a?.Locs.Select(l => l.At).FirstOrDefault(nm => Ru.Info(nm)?.Type == "BLDG");
            if (at != null) build(at, homeAt());
        }
        return k;
    }
    static readonly Regex AgeRe = new(@"^age_\d$", RegexOptions.Compiled);
    /// <summary>what an action waits for: a building is put up, an epoch researched (the Dustriders' warriors and the
    /// Dragon Clan's second hammock come with epoch 2)</summary>
    protected void prerequisites(ActionDef a, bool noEpoch = false)
    {
        foreach (var m in Ru.Missing(me, a))
        {
            if (Ru.Info(m)?.Type == "BLDG") build(m, homeAt());
            else if (AgeRe.IsMatch(m) && (!noEpoch || AgeRe.IsMatch(a.Id))) produce(m, 1, "Upgrades");     // (an epoch waits for the one before)
        }
    }
    /// <summary>room for more units when the population limit is (nearly) reached: the Dragon Clan's hammocks at the
    /// fireplace (+5 each), the other tribes' houses (cottage, tent, barracks). true when something was started</summary>
    protected bool housing()
    {
        if (me.Units + me.QueuedUnits < me.MaxUnits - 1) return false;
        ActionDef? later = null;
        foreach (var b in myBuildings())
        {
            if (!b.Built) continue;
            foreach (var a in ActionsOf(b))
            {
                if (a.Kind != "Upgrades" || !a.Id.Contains("hammock")) continue;
                var ck = Ru.Check(me, a, null);
                if (ck == null && QueueAction(b, a) == null) return true;
                if ((ck == "req" || ck == "hidden") && later == null) later = a;
            }
        }
        var h = own("hu_stone_cottage");
        if (h == null)
        {
            // the next hammock waits for an epoch: as a requirement, or shown by epoch 2's modificators (the first hammock;
            // MIRAGE's second one also needs its optional supply system - without it the Dragon Clan stays at 20)
            if (later != null) { prerequisites(later); if (Ru.Missing(me, later).Count == 0 && me.Epoch() < 2) produce("age_2", 1, "Upgrades"); }
            return false;
        }
        // one house at a time: while one is going up, its builders stay on it (a new site every call took them away and
        // none was ever finished: mission 15's seven Dustrider tents)
        var site = myBuildings().FirstOrDefault(b => b.Name == h && !b.Built);
        if (site != null)
        {
            if (!site.Builders.Any(w => w.Alive && w.Task.Type == "build" && w.Task.Building == site))
            {
                var ws = workers().OrderBy(u => u.DistTo(site)).Take(2).ToList();
                if (ws.Count > 0) Order(ws, "build", target: site);
            }
            return false;
        }
        return build(h, homeAt(), count: myBuildings().Count(b => b.Name == h) + 1) is "started";
    }
    /// <summary>the player's main building on the map (map coordinates), for placing buildings</summary>
    protected (int X, int Y) homeAt()
    {
        Entity? h = myBuildings().FirstOrDefault(b => b.Def != null && (b.Def.Script ?? "").Contains("CFireplace")) ?? (Entity?)myBuildings().FirstOrDefault() ?? mine().FirstOrDefault();
        return h != null ? ToMapR(h.Pos.X, h.Pos.Z) : (0, 0);
    }
    static readonly string[] RES = { "food", "wood", "stone" };
    protected static readonly Dictionary<string, double> DefaultStock = new() { ["food"] = 500, ["wood"] = 400, ["stone"] = 300 };
    /// <summary>workers gather: gather("food") sends the idle ones to food; gather({ food: 300, wood: 300, stone: 300 })
    /// sends idle workers and those gathering something already reached to what is still missing; gather() does that
    /// for a stock of 500 food, 400 wood, 300 stone (and keeps idle workers busy with what there is least of)</summary>
    protected void gather(string only) => Gather(only, null);
    protected void gather(Dictionary<string, double>? goal = null) => Gather(null, goal ?? DefaultStock);
    void Gather(string? only, Dictionary<string, double>? want)
    {
        double need(string? r) => r == null ? double.NaN : want != null ? want.GetValueOrDefault(r) - me[r] : -me[r];
        var order = only != null ? new List<string> { only } : RES.Where(r => want == null || need(r) > 0).OrderByDescending(r => need(r)).ToList();
        if (order.Count == 0) order = RES.OrderBy(r => me[r]).ToList();
        foreach (var w in workers())
        {
            var t = w.Task;
            if (t.Type != "idle" && !(want != null && t.Type == "gather" && need(t.Res) <= 0 && need(order[0]) > 0)) continue;
            foreach (var r in order)
            {
                var n = W.NearestResource(w.Pos.X, w.Pos.Z, r, 250);
                if (n != null) { Order(w, "gather", target: n); break; }
            }
        }
    }
    /// <summary>promote one of the player's units (heroes first) a level; true when one was promoted. who: only those (a
    /// hero kept out of the fights is left as he is - the skulls go to the fighters)</summary>
    protected bool promote(Func<Unit, bool>? who = null)
    {
        foreach (var u in heroes().Concat(fighters()).Where(u => who == null || who(u)).ToList()) if (u.Level < 5 && ChangeLevel(u, u.Level + 1) == null) return true;
        return false;
    }
    /// <summary>at least this much of each resource (a cheat, counted)</summary>
    protected void TopUp(Dictionary<string, double> res)
    {
        bool any = false;
        foreach (var (k, val) in res) if (me[k] < val) { me[k] = val; any = true; }
        if (any) TopUps++;
    }

    // ---------------------------------------------------------------- camera (UNTT CAME conditions)
    protected void camera(string kind)
    {
        var c = UiP;
        if (kind == "scroll") { c.CamX += 4; c.CamY += 2; } else if (kind == "zoom") c.CamDist *= 0.8; else if (kind == "pan") c.CamYaw += 0.3;
    }

    // ---------------------------------------------------------------- running
    public readonly List<bool> reached = new();
    public int? failedAt;
    public string? FailedStep;
    public int StepCount;
    public string? Exception;
    /// <summary>the Dustriders' mobile base the mission starts with waits for the player (Campaign.FreeMains): put it
    /// down where it would have stood, or the nearest free place</summary>
    protected void placeFreeMains()
    {
        foreach (var u in me.FreeMains.ToList())
        {
            for (int k = 0; k < 400; k++)
            {
                double r = k * 0.5, ang = k * 2.4, x = u.Pos.X + Math.Cos(ang) * r, z = u.Pos.Z + Math.Sin(ang) * r;
                if (C.PlaceFreeMain(u, x, z, 0)) break;
            }
        }
    }
    /// <summary>one second of game time (G.step(20, 0.05))</summary>
    protected void Second() { StepOnly(20); AfterSecond(); EverySecond?.Invoke(); }
    /// <summary>a hook for debugging a playthrough (called after every second of RunSteps)</summary>
    public Action? EverySecond;
    /// <summary>n simulation steps, the mission UI ticking with them</summary>
    protected void StepOnly(int n) { for (int i = 0; i < n; i++) { S.Step(); UiP.Tick(GameWorld.STEP); } }
    /// <summary>after every second of RunSteps (a playthrough's own bookkeeping: the JS wrapped world methods)</summary>
    protected virtual void AfterSecond() { }
    /// <summary>a unit the step needs is not there (yet): the JS caught errors that name null / undefined</summary>
    static bool Missing(Exception e) => e is NullReferenceException || e is ArgumentNullException || (e is InvalidOperationException && (e.Message.Contains("Nullable") || e.Message.StartsWith("no region")));
    protected int runSteps(List<Step> steps, RunOpts? opts = null)
    {
        opts ??= new RunOpts();
        StepCount = steps.Count;
        string? err = null;
        placeFreeMains();
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                var (name, act, done, limit) = (steps[i].Name, steps[i].Act, steps[i].Done, steps[i].Limit);
                double t0 = W.Time;
                bool okStep = false;
                while (W.Time - t0 < limit && !Over)
                {
                    if (done()) { okStep = true; break; }
                    try { act(); } catch (Exception e) when (Missing(e)) { }
                    if (opts.Cheats) Cheats(opts.CheatOpts);
                    Second();
                }
                if (!okStep && done()) okStep = true;
                reached.Add(okStep);
                var line = $"{(i + 1).ToString().PadLeft(2)}. {name} ({N(Round(W.Time - t0))} s, at {N(Round(W.Time))} s)";
                ok(okStep, line);
                Progress?.Invoke((okStep ? "ok " : "FAIL ") + line);
                if (!okStep) { failedAt = i; FailedStep = name; break; }
            }
        }
        catch (Exception e) { err = e.ToString(); Exception = e.GetType().Name + ": " + e.Message; }
        ok(err == null, "no exception" + (err != null ? ": " + err : ""));
        return reached.Count(x => x);
    }
    /// <summary>D.why(name): the trigger's conditions with their state (and what object conditions find)</summary>
    protected string Why(string name)
    {
        var t = E.Find(name);
        if (t == null) return "null";
        return $"{{name: {t.Name}, enabled: {t.Enabled}, live: {t.Live}, fired: {t.Fired}, value: {t.Evaluate()}, conditions: [" +
            string.Join("; ", t.Conds.Select((c, i) => $"{i + 1} {c.Type}={c.State} {c.Info()}")) + "]}";
    }
    protected string Report(Dictionary<int, string>? why = null)
    {
        ok(E.Errors.Count == 0, $"no action errors ({E.Errors.Count})");
        ok(E.UnknownConditions.Count + E.UnknownActions.Count == 0, "no unknown condition / action type");
        info($"tribe {me.Tribe}{(C.Swap != null ? " (mission: " + C.MissionTribe + ")" : "")}; game time {N(Round(W.Time / 60))} min; teleports {Teleports}; cheat kills {Kills}; top-ups {TopUps}");
        info($"triggers fired {E.Triggers.Count(x => x.Fired > 0)} of {E.Triggers.Count}, {E.StatFired} firings, {E.StatActions} actions, warnings {E.Warnings.Count}");
        info("quests: " + string.Join(" ", E.Quests.Select(x => (x.Name.Length > 18 ? x.Name[..18] : x.Name) + "=" + questState(x.Name))));
        info("sequences: " + string.Join(" ", UiP.Seq));
        info("player: " + string.Join(", ", mine().GroupBy(u => u.Name).Select(g => g.Key + " x" + g.Count())) + "; buildings " + string.Join(" ", myBuildings().Select(b => b.Name)) +
             "; res {" + string.Join(",", me.Res.Select(kv => $"\"{kv.Key}\":{N(Math.Round(kv.Value, 2))}")) + "}");
        foreach (var e in E.Errors.Take(10)) info($"error {e.Trigger} {e.Type} {e.Message}");
        foreach (var w in E.Warnings.Take(20)) info("warning " + w);
        if (!Over && failedAt != null)
        {
            info("log tail:\n" + E.DebugPrint(30));
            info("units of the player: " + string.Join(" ", mine().Select(u => { var (mx, my) = ToMapR(u.Pos.X, u.Pos.Z); return $"{u.Name}@{mx},{my}:{u.Task.Type}"; })));
            var live = string.Join(" | ", E.Triggers.Where(x => x.Live).Select(x => x.Name + "[" + string.Join(" ", x.Conds.Select(c => c.Type + "=" + c.State)) + "]"));
            info("live triggers: " + (live.Length > 3000 ? live[..3000] : live));
            if (why != null && why.TryGetValue(failedAt.Value + 1, out var tn)) info("why " + tn + ": " + Why(tn));
        }
        return string.Join("\n", Out);
    }
}
