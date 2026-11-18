// The end of a skirmish game (Server/misc/GameOverMgr.usl; DominationFlag.usl; docs/spec/ui_groups7_9.md §9):
// only on multiplayer maps (Base/MapType "multiplayer": every skirmish map), not in campaign missions.
//
//   * A player is out when it has no unit that counts in the unit limit and no building that produces units
//     (GOM:330-351) - or when its main building (hu_fireplace, ninigi_fireplace, aje_resource_collector,
//     seas_headquarters) is gone and the head quarter timer (5 min) runs out before a new one is finished (GOM:353-410;
//     not on maps with Base/LastManStanding = 1).
//   * The warp gate (systems/buildings.js): its countdown makes its owner and the owner's friends win.
//   * Domination: the flags (DOFL) go to who stands alone at them - a flag area of 20 m, every 2 s; a flag lowers and
//     rises in 10 s (DominationUpDownTimer) when it changes hands (DominationFlag.usl Tick / ChangeDominationOwner).
//     All flags of one team start the domination timer (10 min); flags of several teams pause it, none kill it; at 0
//     that team wins (GOM:676-805). Units in a flag area are unveiled. In the other game types the flags are removed.
//   * Defender: the player on head quarter 1 defends (team 1 against team 2) until the setup's minutes are over; when
//     all its defender objects are destroyed it loses and the others win (GOM:807-879).
//   * Who is out loses: its units that count in the limit and its producing buildings die (OnPlayerLose); the last
//     player - or the last team - standing wins (GOM:575-611; needs two players / teams at the start).
//   Timers (GoTimers) are shown by the HUD as the original's ObjTime icons (hud_timers: "timer_<icon>").
//
// Ported from remake/src/game/systems/gameover.js (pw-exporter 98c7703). The flag's model is the view's (Flag.Model,
// its clip in Flag.Anim).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PwRemake.Sim.World;

/// <summary>a domination flag of the map (DOFL): name, place, model</summary>
public sealed class FlagSpec
{
    public string Name = "";
    public double X, Z, Rot;
    public string? Model;
}

/// <summary>A domination flag: owner, the owners to come (stack: null = down), its direction, when the move ends.</summary>
public sealed class Flag
{
    public string Name = "", Model = "domination_flag";
    public double X, Z, Rot;
    public Player? Owner;
    public List<Player?> Stack = new();
    /// <summary>"up" | "down"</summary>
    public string Dir = "down";
    public double End;
    public double? Timer;
    /// <summary>the flag's animation (up / down), null: the model has no clips</summary>
    public AnimCtl? Anim;
}

/// <summary>A timer for the HUD (ObjTime): icon, owner, seconds left, paused, tooltip key.</summary>
public sealed record GoTimer(string Icon, Player? Owner, double Left, bool Paused, string Tip);

public sealed partial class GameOverState
{
    public bool On = true;
    public string Mode = "deathmatch";
    /// <summary>Base/LastManStanding: no head quarter rule</summary>
    public bool Lms;
    /// <summary>player -> alive | killed | win | out</summary>
    public readonly OrderedMap<Player, string> State = new();
    /// <summary>head quarter timers: player -> seconds left</summary>
    public readonly OrderedMap<Player, double> Hq = new();
    public readonly List<Flag> Flags = new();
    public DomState? Dom;
    public DefState? Def;
    public double T, FlagT;
    public List<Player> Started = new();
    public HashSet<int> Teams = new();

    public sealed class DomState { public int Team; public Player Owner = null!; public double Left; public bool Paused; }
    public sealed class DefState { public Player Player = null!; public double Left; public bool? Objects; }
}

public sealed partial class World
{
    static readonly Regex HQ = new("^(hu_fireplace|ninigi_fireplace|aje_resource_collector|seas_headquarters)$", RegexOptions.Compiled);
    const double HQ_MIN = 5, DOM_MIN = 10, FLAG_R = 20, FLAG_UPDOWN = 10;

    /// <summary>the skirmish's end rules (main.js: not in a campaign): lastManStanding = Base/LastManStanding, flags = the
    /// map's domination flags, defenderMin = the setup's minutes</summary>
    public void GameOverInit(bool lastManStanding = false, IEnumerable<FlagSpec>? flags = null, double? defenderMin = null)
    {
        if (!Opts.Multiplayer) return;
        var mode = string.IsNullOrEmpty(Opts.Mode) ? "deathmatch" : Opts.Mode;
        var G = Go = new GameOverState { On = true, Mode = mode, Lms = lastManStanding, T = 0, FlagT = 0 };
        foreach (var p in Players) if (GoUnits(p) > 0 || GoProducer(p)) G.State.Set(p, "alive");
        G.Started = G.State.Keys.ToList();
        G.Teams = new HashSet<int>(G.Started.Select(p => p.Team));
        if (mode == "domination") foreach (var f in flags ?? Enumerable.Empty<FlagSpec>()) AddFlag(f);
        if (mode == "defender")
        {
            var d = Players.Find(p => p.Hq == 0);
            double min = defenderMin is > 0 ? defenderMin.Value : Opts.DefenderMin > 0 ? Opts.DefenderMin : 45;
            G.Def = d != null ? new GameOverState.DefState { Player = d, Left = Math.Max(1, min) * 60 } : null;
        }
    }
    public int GoUnits(Player p) { int n = 0; foreach (var u in Units) if (u.Alive && u.Owner == p && u.CountsInPop) n++; return n; }
    /// <summary>a building that produces units (CPlayer.CanProduceUnit)</summary>
    public bool GoProducer(Player p)
    {
        foreach (var b in Buildings)
        {
            if (!b.Alive || b.Owner != p || !b.Built) continue;
            b.Producer ??= Data.ActionsOf(p, b.Name, "building", b.Def?.Type ?? b.Cls).Any(a => a.Kind == "Build" && a.Cat != "Build/BLDG" && a.Results.Count > 0);
            if (b.Producer == true) return true;
        }
        return false;
    }

    partial void GameOverUpdate(double dt)
    {
        var G = Go;
        if (G == null || !G.On) return;
        // timers
        foreach (var (p, left) in G.Hq) { double l = left - dt; G.Hq.Set(p, l); if (l <= 0) { G.Hq.Delete(p); if (G.State.Get(p) == "alive") G.State.Set(p, "killed"); } }
        if (G.Dom != null && !G.Dom.Paused) { G.Dom.Left -= dt; if (G.Dom.Left <= 0) DominationWon(); }
        if (G.Def != null && G.Def.Left > 0) { G.Def.Left -= dt; if (G.Def.Left <= 0) foreach (var (p, s) in G.State) if (s == "alive") G.State.Set(p, p == G.Def.Player ? "win" : "killed"); }
        if ((G.FlagT -= dt) <= 0 && G.Flags.Count > 0) { G.FlagT = 2; foreach (var f in G.Flags) FlagTick(f); CheckDomination(); }
        foreach (var f in G.Flags) FlagUpdate(f, dt);
        if ((G.T -= dt) > 0) return;
        G.T = 1;
        // (one pass over the units and buildings for every player - up to 16 -, then the players in their order)
        var hq = new Dictionary<Player, (bool Any, bool Done, int Units)>();
        foreach (var b in Buildings)
            if (b.Alive && b.Owner is { } bo && HQ.IsMatch(b.Name)) { var h = hq.GetValueOrDefault(bo); hq[bo] = (true, h.Done || b.Built, h.Units); }
        foreach (var u in Units)
        {
            if (!u.Alive || u.Owner is not { } uo) continue;
            bool isHq = HQ.IsMatch(u.Name);
            if (!isHq && !u.CountsInPop) continue;
            var h = hq.GetValueOrDefault(uo);
            hq[uo] = (h.Any || isHq, h.Done || isHq, h.Units + (u.CountsInPop ? 1 : 0));
        }
        foreach (var (p, s) in G.State)
        {
            if (s != "alive") continue;
            var h = hq.GetValueOrDefault(p);
            // the head quarter (GOM:353-410): none of its class left -> the timer; a finished one again -> no timer
            if (!G.Lms)
            {
                if (h.Any) p.HadHQ = true;
                if (!h.Any && !G.Hq.Has(p) && p.HadHQ) G.Hq.Set(p, HQ_MIN * 60);
                if (h.Done) G.Hq.Delete(p);
            }
            if (h.Units == 0 && !GoProducer(p)) G.State.Set(p, "killed");
        }
        // defender objects (CheckDefenderObjs)
        if (G.Def != null && !Buildings.Any(b => b.Alive && b.Name == "defender_object"))
        {
            if (G.Def.Objects != false) foreach (var (p, s) in G.State) if (s == "alive") G.State.Set(p, p == G.Def.Player ? "killed" : "win");
        }
        else if (G.Def != null) G.Def.Objects = true;
        // a warp gate's countdown (systems/buildings.js W.Victory) decided
        foreach (var p in Players) { if (p.Won && G.State.Get(p) == "alive") G.State.Set(p, "win"); if (p.Defeated && G.State.Get(p) == "alive") G.State.Set(p, "killed"); }
        GoResolve();
    }
    void GoResolve()
    {
        var G = Go!; var alive = new List<Player>();
        foreach (var (p, s) in G.State)
        {
            if (s == "killed") { PlayerLost(p); G.State.Set(p, "out"); }
            else if (s == "win") { PlayerWon(p); G.State.Set(p, "out"); }
            else if (s == "alive") alive.Add(p);
        }
        if (!G.On) return;
        if (alive.Count == 1 && G.Started.Count > 1) { PlayerWon(alive[0]); return; }
        var teams = new HashSet<int>(alive.Select(p => p.Team));
        if (alive.Count > 0 && teams.Count == 1 && G.Teams.Count > 1) foreach (var p in alive) PlayerWon(p);
    }
    /// <summary>OnPlayerWin: the game is decided (m_bEnabled = false)</summary>
    public void PlayerWon(Player p)
    {
        p.Won = true;
        var G = Go!;
        G.On = false;
        foreach (var (q, s) in G.State) if (s == "alive" && !p.IsFriend(q) && q.Team != p.Team) { q.Defeated = true; G.State.Set(q, "out"); }
        Emit("gameOver", new Args { ["player"] = p, ["won"] = true });
    }
    /// <summary>OnPlayerLose: its units that count in the limit and its producing buildings die; everybody is told</summary>
    public void PlayerLost(Player p)
    {
        p.Defeated = true;
        Go!.Hq.Delete(p);
        foreach (var o in Players) Emit("msg", new Args { ["player"] = o, ["key"] = "_NT_DiplPlayerKilled", ["text"] = "%1 has been defeated", ["arg"] = p.Name, ["kind"] = "bad" });
        foreach (var u in Units.ToList()) if (u.Alive && u.Owner == p && u.CountsInPop) { u.InvulnT = 0; Kill(u, null); }
        foreach (var b in Buildings.ToList()) if (b.Alive && b.Owner == p && b.Built && GoProducer(p) && b.Producer == true) Kill(b, null);
        Emit("gameOver", new Args { ["player"] = p, ["won"] = false });
    }

    // ------------------------------------------------------------------ domination flags (DominationFlag.usl)
    public Flag AddFlag(FlagSpec f)
    {
        var flag = new Flag { Name = f.Name, X = f.X, Z = f.Z, Rot = f.Rot, Model = f.Model ?? "domination_flag", Owner = null, Dir = "down", End = Time, Timer = null };
        FlagLook(flag);
        Go!.Flags.Add(flag);
        return flag;
    }
    /// <summary>the flag's animation (made again after a saved game is loaded)</summary>
    public void FlagLook(Flag flag)
    {
        var clips = Models.Has(flag.Model) ? Models.Clips(flag.Model) : null;
        flag.Anim = clips != null && clips.Count > 0 ? new AnimCtl(clips, null) : null;
        flag.Anim?.Play(flag.Dir, new AnimOpts { Loop = false, Restart = true, Ts = 100 });
    }
    /// <summary>who stands in the flag's area: units counting in the unit limit (not OTHR)</summary>
    (List<Player> Owners, HashSet<int> Teams, Player? Most) FlagContent(Flag f)
    {
        var n = new OrderedMap<Player, int>();
        UHash.Query(f.X, f.Z, FLAG_R + 4, u =>
        {
            if (!u.Alive || u.Inside || u.Owner == null || !u.CountsInPop || Hypot(u.Pos.X - f.X, u.Pos.Z - f.Z) > FLAG_R) return;
            n.Set(u.Owner, n.Get(u.Owner) + 1);
            if (u.St.Camo.Count > 0) u.St.RevealT = Time + 2.5;               // (FindTraces: Reveal)
        });
        Player? most = null; int m = 0;
        foreach (var (p, c) in n) if (c > m) { m = c; most = p; }
        var owners = n.Keys.ToList();
        return (owners, new HashSet<int>(owners.Select(p => p.Team)), most);
    }
    void FlagTick(Flag f)
    {
        var c = FlagContent(f);
        // the owner who led the change left early: down again (DominationFlag.Tick)
        if (f.Timer != null && f.Stack.Count > 0 && f.Stack[0] != null && !c.Owners.Contains(f.Stack[0]!)) { f.Stack = new List<Player?> { null }; FlagStack(f); return; }
        if (c.Owners.Count == 0) return;
        if (f.Owner == null)
        {
            if (c.Owners.Count == 1) FlagChange(f, c.Owners[0]);
            else if (c.Teams.Count == 1) FlagChange(f, c.Most);
            return;
        }
        if (c.Owners.Contains(f.Owner) || c.Teams.Contains(f.Owner.Team)) return;
        if (c.Teams.Count == 1) FlagChange(f, c.Most);
    }
    void FlagChange(Flag f, Player? p)
    {
        var s = f.Stack;
        if (s.Count == 0 && f.Owner == p) return;
        if (s.Count == 1 && s[0] == p) return;
        if (s.Count == 2 && p != null && s[0] == null && s[1] == p) return;
        f.Stack = new List<Player?> { null };
        if (p != null) f.Stack.Add(p);
        FlagStack(f);
    }
    /// <summary>the flag goes down (to nobody) or up (to the next owner) over up to 10 s, by how far it is (CheckOwnerStack)</summary>
    void FlagStack(Flag f)
    {
        if (f.Stack.Count == 0) { f.Timer = null; return; }
        double now = Time;
        double w;
        if (now > f.End) w = f.Dir == "up" ? 1 : 0;
        else { w = Math.Max(0, Math.Min(1, (f.End - now) / FLAG_UPDOWN)); if (f.Dir == "up") w = 1 - w; }
        var next = f.Stack[0]; double dur = next == null ? FLAG_UPDOWN * w : FLAG_UPDOWN * (1 - w);
        f.Dir = next == null ? "down" : "up";
        f.End = now + dur;
        f.Timer = now + dur;
        var a = f.Anim;
        if (a != null && a.Has(f.Dir)) a.Play(f.Dir, new AnimOpts { Loop = false, Restart = true, Ts = Or(a.Duration(f.Dir), 1) / Math.Max(0.2, Or(dur, 0.2)) });
    }
    void FlagUpdate(Flag f, double dt)
    {
        f.Anim?.Update(dt);
        if (f.Timer == null || Time < f.Timer) return;
        f.Timer = null;
        bool any = f.Stack.Count > 0;
        Player? next = any ? f.Stack[0] : null;
        if (any) f.Stack.RemoveAt(0);
        var last = f.Owner;
        if (any && next != last) { f.Owner = next; FlagFeedback(f, last, next); CheckDomination(); }
        if (f.Stack.Count > 0) FlagStack(f);
    }
    /// <summary>the news of a flag (DominationFlag.DoFeedback)</summary>
    void FlagFeedback(Flag f, Player? last, Player? now)
    {
        int? lt = last?.Team, nt = now?.Team;
        Emit("flag", new Args { ["flag"] = f, ["owner"] = now, ["x"] = f.X, ["z"] = f.Z });
        if (lt == nt) return;
        bool all = Go!.Flags.All(g => g.Owner != null && g.Owner.Team == nt);
        foreach (var p in Players)
        {
            if (nt != null && p.Team == nt) Emit("msg", new Args { ["player"] = p, ["key"] = all ? "_NT_Flag_WeAll" : "_NT_Flag_Captured", ["text"] = all ? "We hold all flags!" : "Flag captured", ["kind"] = "good", ["x"] = f.X, ["z"] = f.Z });
            else
            {
                if (lt != null && p.Team == lt) Emit("msg", new Args { ["player"] = p, ["key"] = "_NT_Flag_Lost", ["text"] = "Flag lost", ["kind"] = "bad", ["x"] = f.X, ["z"] = f.Z });
                if (all) Emit("msg", new Args { ["player"] = p, ["key"] = "_NT_Flag_EnemyAll", ["text"] = "The enemy holds all flags!", ["kind"] = "bad" });
                else if (nt != null) Emit("msg", new Args { ["player"] = p, ["key"] = "_NT_Flag_Enemy", ["text"] = "The enemy captured a flag", ["kind"] = "bad", ["x"] = f.X, ["z"] = f.Z });
            }
        }
    }
    /// <summary>CheckDominationPoints: all flags of one team -> its timer runs; several teams -> paused; none -> gone</summary>
    void CheckDomination()
    {
        var G = Go!; var owners = G.Flags.Select(f => f.Owner).ToList();
        var teams = new HashSet<int>(owners.Where(p => p != null).Select(p => p!.Team));
        bool one = owners.Count > 0 && owners.All(p => p != null) && teams.Count == 1;
        if (one)
        {
            var cnt = new OrderedMap<Player, int>();
            foreach (var p in owners) cnt.Set(p!, cnt.Get(p!) + 1);
            var most = cnt.OrderByDescending(kv => kv.Value).First().Key;      // (a stable sort: the first of the most)
            int team = most.Team;
            if (G.Dom != null && G.Dom.Team == team) { G.Dom.Paused = false; G.Dom.Owner = most; }
            else G.Dom = new GameOverState.DomState { Team = team, Owner = most, Left = DOM_MIN * 60, Paused = false };
        }
        else if (teams.Count == 0) G.Dom = null;
        else if (G.Dom != null) { if (!teams.Contains(G.Dom.Team)) G.Dom = null; else G.Dom.Paused = true; }
    }
    void DominationWon()
    {
        var G = Go!; int team = G.Dom!.Team;
        G.Dom = null;
        foreach (var (p, s) in G.State) if (s == "alive") G.State.Set(p, p.Team == team ? "win" : "killed");
        GoResolve();
    }
    /// <summary>the timers the HUD shows (ObjTime: icon, owner, seconds left, paused)</summary>
    public List<GoTimer> GoTimers()
    {
        var G = Go; var o = new List<GoTimer>();
        if (G == null) return o;
        foreach (var (p, left) in G.Hq) o.Add(new GoTimer("headquater", p, left, false, "_Timer_TT_HeadQuater"));
        foreach (var b in Buildings) if (b.Alive && b.WarpT != null && !b.WarpDone) o.Add(new GoTimer("warpgate", b.Owner, b.WarpT.Value, false, "_Timer_TT_WarpGate"));
        if (G.Dom != null) o.Add(new GoTimer("domination", G.Dom.Owner, G.Dom.Left, G.Dom.Paused, "_Timer_TT_Domination"));
        if (G.Def != null && G.Def.Left > 0) o.Add(new GoTimer("defender", G.Def.Player, G.Def.Left, false, "_Timer_TT_Defender"));
        return o;
    }
}
