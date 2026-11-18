// Special building behaviours, chosen by the building's script class (classes/buildings/*_buildings.txt).
//
// Reference: docs/spec/buildings.md (Building.usl CGate, CTrap family, CKennel, CNinigi_Dilophosaurus_Nest,
// CImpResinField, CWarpGate, CGrowingField ...).
//
// To give a building class new behaviour add an entry to BEHAVIOURS (a BuildingBehaviour):
//   Init(b, W)        when the building object is created
//   Built(b, W)       when construction is finished
//   Update(b, W, dt)  every simulation step while the building is finished and alive
//   Destroyed(b, W)   when it dies
//   Hit(b, A, W)      when an attacker's weapon hits it (TakeDmg: combat calls World.BehaviourHit)
// Buildings without an entry are plain buildings (production, storage, housing and towers are handled elsewhere).
//
// Ported from remake/src/game/systems/buildings.js (pw-exporter 98c7703). The resin field's fire particles are the
// view's (it follows Building.Burning).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>a map region the systems ask (campaign/regions.js Region: lakes, the Scorpion's attacks): the campaign's
/// regions implement it (World.RegionOf returns them)</summary>
public interface IMapRegion
{
    bool Enabled { get; }
    /// <summary>a circle around the region (null: empty)</summary>
    (double X, double Z, double R)? Bounds();
    bool Contains(double x, double z);
}

/// <summary>A behaviour of a building class (systems/buildings.js BEHAVIOURS entry): one shared object per class, the
/// state lives on the building.</summary>
public abstract class BuildingBehaviour : IBuildingBehaviour
{
    public virtual void Init(Building b, World w) { }
    public virtual void Built(Building b, World w) { }
    public virtual void Update(Building b, World w, double dt) { }
    public virtual void Destroyed(Building b, World w) { }
    public virtual void Hit(Building b, Entity attacker, World w) { }
    /// <summary>its weapon is not fired like a tower's (systems/combat.js)</summary>
    public virtual bool NoTower => false;
    /// <summary>the site's construction went on (SetProcess: the launch pads' build-up decorations) - World.SiteProgressed</summary>
    public virtual void SiteProgress(Building b, World w) { }
    /// <summary>models the class's script hangs on the building besides the composites table (MIRAGE's CMirageDeco
    /// objects: the special box's lemur and sloth, the sensor tower's guard, the launch pads' build-ups); null: none.
    /// Building.UpdateTurret attaches them; a change of the list (its key) rebuilds the parts.</summary>
    public virtual IReadOnlyList<ExtraPart>? ExtraParts(Building b, World w) => null;
}

/// <summary>a model a building's script links to it (CMirageDeco: LinkAction(building, link, offset)) with its own
/// state: destruction level (SetDestructLevel), script visibility flags (SetRndInvMaskSingleFlagInv) and the clip it
/// loops. A link the building's model lacks ("NONE", "NOPE") hangs it at the model's origin (ModelInstance.LinkOrOrigin);
/// the offset is metres in the link's frame, GSF axes (as the composites' offsets).</summary>
public sealed record ExtraPart(string Gfx, string Link, double[]? Offset = null, int Dmg = 0, (int Bit, bool On)[]? Flags = null, string? Clip = null)
{
    public string Key => $"{Gfx}@{Link}/{Dmg}/{(Flags == null ? "" : string.Join(".", Flags.Select(f => f.Bit + (f.On ? "+" : "-"))))}/{Clip}";
}

// ---------------------------------------------------------------------------------------------- state on the building
public sealed partial class Building
{
    // gates
    public bool GateOpen;
    public double AutoT;
    public int FormerState;
    // traps
    public TrapState? TrapS;
    public Unit? Held;
    // resin field
    public BurnState? Burning;
    public double RehideT, BurnCD;
    // nests
    public NestState? Nest;
    // warp gate
    public bool WarpInert, WarpDone;
    public double? WarpT;
    public List<Reveal?>? WarpSight;
    // walls (falling stones)
    public double StonesT;
    // the Scorpion and its panels
    public ScorpState? Scorp;
    public bool PanelOpen;
    public string? PanelPose;
}

public sealed class TrapState { public bool Armed = true; public readonly OrderedSet<Unit> Ignore = new(); public double RearmT; }
public sealed class BurnState { public double T, Tick; public bool Spread; }
public sealed class NestState { public string Spawn = ""; public double T; public List<Unit> Members = new(); }
public sealed class ScorpState
{
    public bool Active, Change;
    public int State = 3, Hit, Prep, Attack = -1;
    public double BusyT;
    public double? DmgT, EndT, OpenT, CloseT;
}

public sealed partial class Unit
{
}

public sealed partial class World
{
    public static class GATE { public const int OPEN = 0, CLOSED = 1, AUTO = 2; }

    // ------------------------------------------------------------------------------------------ gates
    sealed class GateB : BuildingBehaviour
    {
        const double AUTO_CLOSE = 8;            // AUTO gates close 8 s after the last unit passed
        public override void Init(Building b, World W) { b.GateState = GATE.AUTO; b.GateOpen = false; }
        public override void Built(Building b, World W) => W.SetGateCells(b);
        public override void Update(Building b, World W, double dt)
        {
            // AUTO: a gate opens for its owner's (and allies') units and closes again after 8 s
            if (b.GateState == GATE.AUTO)
            {
                bool pass = false;
                W.UHash.Query(b.Pos.X, b.Pos.Z, b.Radius + 6, u => { if (!pass && u.Alive && u.Owner != null && (b.Owner == null || b.Owner.IsFriend(u.Owner)) && b.SurfDist(u.Pos.X, u.Pos.Z) < 3) pass = true; });
                if (pass) { b.AutoT = AUTO_CLOSE; if (!b.GateOpen) SetGateAnim(b, true); }
                else if (b.GateOpen && (b.AutoT -= dt) <= 0) SetGateAnim(b, false);
            }
            // lock picked (ninja): forced open for 30 s, then closed again if it was closed before
            if (b.BrokenT > 0 && (b.BrokenT -= dt) <= 0 && b.FormerState == GATE.CLOSED) W.SetGate(b, GATE.CLOSED);
        }
        public override void Destroyed(Building b, World W) => W.ClearGateCells(b);
    }
    static void SetGateAnim(Building b, bool open)
    {
        b.GateOpen = open;
        if (b.Anim != null) { var a = b.Anim.Pick(open ? "open" : "close"); if (a != null) b.Anim.Play(a, new AnimOpts { Loop = false, Restart = true }); }
        b.LookChanged();
    }

    // ------------------------------------------------------------------------------------------ traps (CTrap)
    // radius: trigger radius, types: victim classes, once: one victim then re-arm, rearm: seconds
    sealed class TrapDef
    {
        public double Radius, Rearm;
        public string[] Types = { "CHTR", "ANML", "VHCL", "SHIP" };
        public bool Once;
        /// <summary>a trap that caught someone is shown to that player (OnReveal); MIRAGE's mud pit stays hidden</summary>
        public bool Reveal = true;
        public Action<Building, Unit, World> Fire = (_, _, _) => { };
    }
    static readonly Dictionary<string, TrapDef> TRAPS = new()
    {
        ["CPitfall"] = new TrapDef
        {
            Radius = 5, Once = true, Rearm = 21,
            Fire = (b, v, W) => { W.TakeDmg(b, v, 1, 0); b.Anim?.Play(b.Anim.Pick("attack_front") ?? "attack_front", new AnimOpts { Loop = false, Restart = true }); },
        },
        ["CMinefield"] = new TrapDef
        {
            Radius = 3, Once = true, Rearm = 20,
            Fire = (b, v, W) =>
            {
                var cs = b.Cs();
                W.AreaDamage(b, b.Pos, cs != null ? cs.Splash : 10, cs != null ? cs.Dmg : 350, cs != null ? cs.EndDmg : 200);
                W.Emit("impact", new Args { ["pos"] = b.Pos, ["weapon"] = b.Weapons.Long, ["splash"] = true });
            },
        },
        ["CSnareTrap"] = new TrapDef
        {
            Radius = 3, Types = new[] { "CHTR" }, Once = true, Rearm = 1,
            Fire = (b, v, W) => { b.Held = v; W.Trap(v, 1e6, "snare"); SnareCatch(b, v); },
        },
        ["CPoisonDung"] = new TrapDef { Radius = 5, Once = false, Fire = (b, v, W) => W.TakeDmg(b, v, 1, 0) },
    };

    sealed class TrapB : BuildingBehaviour
    {
        readonly TrapDef T;
        public TrapB(TrapDef t) { T = t; }
        public override void Init(Building b, World W) { b.St.Camo.Add("trap"); b.Untargetable = false; b.TrapS = new TrapState(); }
        public override void Update(Building b, World W, double dt)
        {
            var s = b.TrapS!;
            // the snare holds its victim until it (or the trap) dies: 3 damage per second
            if (b.Held != null)
            {
                if (!b.Held.Alive) { b.Held = null; s.RearmT = T.Rearm; s.Armed = false; }
                else { W.TakeDirectDmg(b.Held, 3 * dt, 99, b.Owner, false, b); b.Held.Path = new NavPath(); b.Held.Vel = V3.Zero; }
                return;
            }
            // found by an enemy (the Dustriders' sensor tower: TrapFound + StartHiddenTimer(20)): hidden again after 20 s
            if (b.RehideT > 0 && (b.RehideT -= dt) <= 0 && s.Armed) b.St.Camo.Add("trap");
            if (!s.Armed) { if ((s.RearmT -= dt) <= 0) { s.Armed = true; s.Ignore.Clear(); b.St.Camo.Add("trap"); } return; }
            var inside = new OrderedSet<Unit>();
            W.UHash.Query(b.Pos.X, b.Pos.Z, T.Radius + 4, v =>
            {
                // (CTrapQuery.OnPush: flying units are never trapped)
                if (!v.Alive || v.Inside || v.Flying || v.Owner == null || b.Owner == null || !b.Owner.IsEnemy(v.Owner) || !T.Types.Contains(v.Cls)) return;
                if (Math.Sqrt((v.Pos.X - b.Pos.X) * (v.Pos.X - b.Pos.X) + (v.Pos.Z - b.Pos.Z) * (v.Pos.Z - b.Pos.Z)) > T.Radius + v.Radius * 0.5) return;
                if (v.Task.Target == b) return;
                inside.Add(v);
            });
            foreach (var v in s.Ignore.ToList()) if (!inside.Has(v)) s.Ignore.Delete(v);
            foreach (var v in inside)
            {
                if (s.Ignore.Has(v)) continue;
                s.Ignore.Add(v);
                T.Fire(b, v, W);
                // a trap that caught someone is revealed to that player
                if (T.Reveal) b.St.Camo.Delete("trap");
                W.Emit("trap", new Args { ["building"] = b, ["victim"] = v });
                if (T.Once) { s.Armed = false; s.RearmT = T.Rearm; break; }
            }
        }
        public override void Destroyed(Building b, World W)
        {
            if (b.Held == null || !b.Held.Alive) return;
            b.Held.St.T["trapped"] = W.Time;
            if (ReferenceEquals(T, TRAPS["CSnareTrap"])) SnareRelease(b.Held);
        }
    }

    // ---- the snare trap's victim (Building.usl CSnareTrap, B:4945-5103; the clips are the characters' own, all_characters.gsf)
    /// <summary>CSnareTrap.OnHoldTrap (B:5044-5080): the victim's action ends, it is held (SetTrapped), its weapons hidden,
    /// it plays "all_snar_trap_catching" once; the trap its "catching" (AnimAction). When the trap's catching ends
    /// (OnActionEnd, B:4986-5002) the trap loops "hanging" and the victim "all_snar_trap_hanging" (SNARE_HANG). Not
    /// taken: the victim linked to the trap's "we" and turned (LinkAction, SetRot) - it hangs where it was caught.</summary>
    static void SnareCatch(Building b, Unit v)
    {
        if (v.Anim is { } va)
        {
            var c = va.Pick("all_snar_trap_catching");
            // (without the trap's clip the victim's own catching clip ends the catch)
            if (c != null) va.Play(c, new AnimOpts { Loop = false, Restart = true, Cut = true, OnDone = b.Anim?.Pick("catching") == null ? new AnimDone(SNARE_HANG) : null });
            else if (va.Pick("all_snar_trap_hanging") is { } h) va.Play(h, new AnimOpts { Restart = true });
        }
        v.WeaponsVisible = false; v.LookChanged();
        if (b.Anim?.Pick("catching") is { } tc) b.Anim.Play(tc, new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone(SNARE_HANG) });
    }

    const string SNARE_HANG = "snareHang";
    /// <summary>CSnareTrap.OnActionEnd (state 0): the trap's "hanging" and its victim's "all_snar_trap_hanging", looping
    /// (SetAnim(.., 3)); called by the end of the trap's catching clip, or of the victim's when the trap has none</summary>
    [AnimDone(SNARE_HANG)]
    static void SnareHang(World W, Entity e, Args? a)
    {
        Building? b = e as Building;
        if (b == null && e is Unit u0) b = W.Buildings.FirstOrDefault(x => x.Alive && x.Held == u0);
        if (b == null) return;
        if (b.Anim?.Pick("hanging") is { } th) b.Anim.Play(th, new AnimOpts { Restart = true });
        if (b.Held is { Alive: true } v && v.Anim?.Pick("all_snar_trap_hanging") is { } h) v.Anim.Play(h, new AnimOpts { Restart = true });
    }

    /// <summary>CSnareTrap.OnHoldRelease (B:5025-5042): let go (the trap destroyed), the victim shows its weapons again and
    /// plays "all_snar_trap_end" once (SetAnim(.., 1)) before it goes on</summary>
    static void SnareRelease(Unit v)
    {
        v.WeaponsVisible = true; v.LookChanged();
        if (v.Anim?.Pick("all_snar_trap_end") is not { } c) return;
        v.BusyAnim = true;
        v.Anim.Play(c, new AnimOpts { Loop = false, Restart = true, Cut = true, OnDone = new AnimDone("busy") });
    }

    // ------------------------------------------------------------------------------------------ resin field (Burn)
    sealed class ResinB : BuildingBehaviour
    {
        public override void Init(Building b, World W) => b.St.Camo.Add("trap");
        public override void Update(Building b, World W, double dt)
        {
            var f = b.Burning;
            if (f == null) return;
            {
                f.T += dt; f.Tick -= dt;
                if (f.Tick <= 0)
                {
                    f.Tick += 1;
                    // first second: the fire spreads to own resin fields within 13 m
                    if (!f.Spread) { f.Spread = true; foreach (var o in W.Buildings.ToList()) if (o != b && o.Alive && o.Built && o.Owner == b.Owner && o.Name == b.Name && o.DistTo(b) < 13) W.BurnResin(o); }
                    var cs = b.Cs();
                    double R = cs != null && cs.Splash != 0 ? cs.Splash : 6;
                    foreach (var v in W.UHash.Near(b.Pos.X, b.Pos.Z, R + 8))
                        if (v.Alive && !v.Inside && v.Owner != null && b.Owner!.IsEnemy(v.Owner) && Math.Sqrt((v.Pos.X - b.Pos.X) * (v.Pos.X - b.Pos.X) + (v.Pos.Z - b.Pos.Z) * (v.Pos.Z - b.Pos.Z)) - v.Radius < R) W.TakeDmg(b, v, 1, 0);
                    // (the fire particles: the view, while Burning)
                }
                if (f.T >= 30) { b.Burning = null; b.RehideT = 20; b.LookChanged(); }
            }
            if (b.RehideT > 0 && (b.RehideT -= dt) <= 0) b.St.Camo.Add("trap");
        }
    }

    // ------------------------------------------------------------------------------------------ nests (kennel, dilophosaurus nest)
    // two autonomous animals, one every 20 s, kept within 50 m of the building (Nest.usl)
    sealed class NestB : BuildingBehaviour
    {
        readonly string _spawn;
        public NestB(string spawn) { _spawn = spawn; }
        public override void Built(Building b, World W) { b.Nest = new NestState { Spawn = _spawn, T = 20 }; }
        public override void Update(Building b, World W, double dt)
        {
            var n = b.Nest; if (n == null) return;
            n.Members = n.Members.Where(u => u.Alive).ToList();
            if (n.Members.Count < 2 && (n.T -= dt) <= 0)
            {
                n.T = 20;
                var (x, z) = b.ExitPoint(b.Pos.X + 5, b.Pos.Z + 5);
                var u = W.SpawnUnit(n.Spawn, b.Owner, x, z, 1, 0, new UnitOpts { Autonomous = true });
                if (u != null) { u.HomeX = b.Pos.X; u.HomeZ = b.Pos.Z; u.HomeRadius = 50; n.Members.Add(u); }
            }
            foreach (var u in n.Members) if (u.Task.Type == "idle" && Math.Sqrt((u.Pos.X - b.Pos.X) * (u.Pos.X - b.Pos.X) + (u.Pos.Z - b.Pos.Z) * (u.Pos.Z - b.Pos.Z)) > 50) W.Order(u, new Order("move") { X = b.Pos.X, Z = b.Pos.Z });
        }
        public override void Destroyed(Building b, World W) { if (b.Nest != null) foreach (var u in b.Nest.Members.ToList()) if (u.Alive) W.Kill(u, null); }
    }

    // ------------------------------------------------------------------------------------------ warp gate
    // When finished a countdown starts (MPSettings WarpGateTimer, default 10 minutes); if the gate survives its
    // owner wins. Only on maps / games that allow it (DimGateAvailable - a skirmish option here): elsewhere the gate
    // deletes itself as soon as it is placed, its cost lost (CWarpGate.OnInit: CWarpMgr.Register fails -> Delete). A
    // placed gate is shown to every player: a 20 m sight object of each player at the gate (ShowFOW_Obj, SetFOWRange 20).
    sealed class WarpGateB : BuildingBehaviour
    {
        public override void Init(Building b, World W)
        {
            // a campaign map's own gate (mission 4: the portal the SEAS come out of, with its vortex) stands, without a
            // countdown (the user saw it in the game); elsewhere without the option it deletes itself
            if (!W.Opts.Warpgate && !W.Opts.Multiplayer) { b.WarpInert = true; return; }
            if (!W.Opts.Warpgate) { W.Later(0.05, "deleteBuilding", new Args { ["b"] = b }); return; }
            b.WarpSight = W.Players.Select(p => W.RevealArea(p, b.Pos.X, b.Pos.Z, 20)).ToList();
            W.Emit("warpgate", new Args { ["building"] = b, ["state"] = "started" });
        }
        public override void Built(Building b, World W) { if (!b.Alive || b.WarpInert) return; b.WarpT = (W.Opts.WarpgateMinutes != 0 ? W.Opts.WarpgateMinutes : 10) * 60; W.Emit("warpgate", new Args { ["building"] = b, ["state"] = "finished" }); }
        public override void Update(Building b, World W, double dt)
        {
            if (b.WarpT == null) return;
            b.WarpT -= dt;
            if (b.WarpT <= 0 && !b.WarpDone) { b.WarpDone = true; W.Emit("warpgate", new Args { ["building"] = b, ["state"] = "won" }); W.Victory(b.Owner!); }
        }
        public override void Destroyed(Building b, World W)
        {
            foreach (var h in b.WarpSight ?? new List<Reveal?>()) W.HideArea(h);
            if (b.WarpT != null && !b.WarpDone) W.Emit("warpgate", new Args { ["building"] = b, ["state"] = "destroyed" });
        }
    }
    // a building that deletes itself (Delete(): no ruin, no refund)
    [Delayed("deleteBuilding")]
    static void DeleteBuilding(World W, Args a) { var b = a.B("b"); if (b != null && b.Alive) { b.SilentDeath = true; W.KillBuilding(b, null); } }

    // ------------------------------------------------------------------------------------------ fields
    // CGrowingField / CCornfield: the grow animation follows the workers' progress (GetCorn.usl)
    sealed class FieldB : BuildingBehaviour
    {
        public override void Built(Building b, World W)
        {
            b.GrowStep = 0;
            if (b.Anim != null && b.Anim.Has("grow")) { b.Anim.Play("grow", new AnimOpts { Loop = false }); if (b.Anim.Cur != null) { b.Anim.Cur.Paused = true; b.LookChanged(); } }
        }
    }

    // ------------------------------------------------------------------------------------------ walls (CWall)
    // "falling stones" (Norse palisade upgrade; Building.usl CWall.TakeDmg): a finished wall that is hit drops stones -
    // its weapon hu_falling_stones (500 in 20 m) around itself, again after the weapon's duration (10 s)
    sealed class WallB : BuildingBehaviour
    {
        public override void Hit(Building b, Entity A, World W)
        {
            if (!b.Built || b.Owner == null || b.StonesT > W.Time || !W.Data.Invented(b.Owner, "hu_falling_stones", b.Def!.Tribe)) return;
            var w = b.Weapons.Long; var cs = w != null ? b.Cs(w) : null;
            if (cs == null || !(cs.Splash > 0)) return;
            b.StonesT = W.Time + W.AttackDuration(b, cs);
            W.AreaDamage(b, b.Pos, cs.Splash, cs.Dmg, cs.EndDmg, new AreaOpts { Weapon = w });
            W.Emit("fxAt", new Args { ["pos"] = b.Pos, ["gfx"] = "hu_wallstones", ["until"] = W.Time + 0.8, ["yaw"] = SMath.Atan2(A.Pos.X - b.Pos.X, A.Pos.Z - b.Pos.Z) });
        }
    }

    // ------------------------------------------------------------------------------------------ the Scorpion
    // Mission 16's end boss (Building.usl CScorpionBoss, CScorpionPanel). It stands still and harmless until the mission
    // wakes it (ACDO "JumpOffWall", after the scorpion sequence): then one of its three control panels opens for 25 s -
    // only an open panel can be hit, the Scorpion itself never -, 10 s after it closes another opens. With an enemy within
    // 150 m it attacks in rounds: prepare, wait, prepare again, strike - legs, flamethrower or scissors, chosen at random
    // among the first <state> of them - and its blow hurts everything in the map's region of that attack (melee damage
    // that armour does not stop: TakeDirectMeleeDmg). Every destroyed panel costs it a state (3 -> 2 -> 1 -> dead) with
    // its destruction animation.
    sealed record ScorpAttack(string Region, string Prepare, string Anim, double Start, double End, double Dmg);
    static readonly ScorpAttack[] SCORP =
    {
        new("scorpion_legs", "prepare_3", "attack_3", 2.8, 3.2, 250),
        new("scorpion_flamethrower", "prepare_2", "attack_2", 5.2, 11.0, 50),
        new("scorpion_scissors", "prepare_1", "attack_1", 1.0, 1.4, 250),
    };
    const double SCORP_RANGE = 150;
    static double ScorpPlay(Building b, string? name, bool loop = false)
    {
        if (b.Anim == null || !b.Anim.Has(name)) return 2;
        b.Anim.Play(name, new AnimOpts { Loop = loop, Restart = true });
        double d = b.Anim.Duration(name);
        return d != 0 ? d : 2;
    }
    List<Building> ScorpPanels(Building b) => Buildings.Where(p => p.Alive && p.Def!.Script == "CScorpionPanel" && p.Owner == b.Owner).ToList();
    void ScorpOpenPanel(Building b)
    {
        var S = b.Scorp!; var list = ScorpPanels(b);
        S.OpenT = null;
        if (list.Count == 0) return;
        var p = list[(int)Math.Floor(Rng.Next() * list.Count)];
        // OpenRandomPanel: hitable + selectable, SetAnim("open", 1) - it rises out of the ground
        p.Untargetable = false; p.Unselectable = false; p.PanelOpen = true; p.PanelPose = "opening";
        ScorpPlay(p, "open");
        S.CloseT = 25;
    }
    void ScorpClosePanels(Building b)
    {
        var S = b.Scorp!;
        // ClosePanel: not hitable, not selectable, SetAnim("open", 2) = the opening played backwards (there is no "close")
        foreach (var p in ScorpPanels(b)) if (p.PanelOpen)
            {
                p.PanelOpen = false; p.Untargetable = true; p.Unselectable = true; p.PanelPose = "closing";
                PanelReverse(p);
            }
        S.CloseT = null; S.OpenT = 10;
    }
    // the panel's "open" clip from its end back to its start (SetAnim mode 2), held at the start when done
    static void PanelReverse(Building p)
    {
        var A = p.Anim;
        if (A == null || !A.Has("open")) return;
        var a = A.Play("open", new AnimOpts { Loop = false, Restart = true, Fade = 0 });
        if (a != null) { a.Time = A.Duration("open"); a.TimeScale = -1; a.Paused = false; p.LookChanged(); }
    }
    void ScorpDamage(Building b, ScorpAttack at)
    {
        if (RegionOf?.Invoke(at.Region) is not IMapRegion R) return;
        var hit = new List<Entity>();
        foreach (var e in Units.Cast<Entity>().Concat(Buildings))
        {
            if (!e.Alive || e == b || e.Inside || e.Def?.Script == "CScorpionPanel" || !R.Contains(e.Pos.X, e.Pos.Z)) continue;
            hit.Add(e);
        }
        foreach (var e in hit) TakeDirectDmg(e, at.Dmg, 0, b.Owner, false, b);
    }

    sealed class ScorpionB : BuildingBehaviour
    {
        public override bool NoTower => true;              // its weapon is not fired like a tower's (systems/combat.js)
        public override void Init(Building b, World W) { b.Scorp = new ScorpState(); b.Untargetable = true; }
        // ACDO JumpOffWall
        public void Activate(Building b, World W)
        {
            var S = b.Scorp!;
            // it rises out of its huts, then the fight (state 4: standanim)
            if (!S.Active) { W.ScorpOpenPanel(b); S.State = 4; S.BusyT = ScorpPlay(b, "stand_up"); }
            S.Change = false; S.Active = true;
        }
        // a panel destroyed (CScorpionBoss.UpdateState)
        public void PanelLost(Building b, World W)
        {
            var S = b.Scorp!;
            S.State--; S.Change = true;
            if (S.State <= 0) { W.KillBuilding(b, null); return; }
            S.OpenT = null; S.CloseT = null;
            W.ScorpClosePanels(b);
        }
        public override void Update(Building b, World W, double dt)
        {
            var S = b.Scorp;
            if (S == null) return;
            // asleep (OnInit SetAnim "stand_up", 0 loops): crouched in its huts at the first frame of standing up, out of
            // sight until the mission wakes it
            if (!S.Active)
            {
                if (b.Anim != null && b.Anim.Has("stand_up") && (b.Anim.CurName != "stand_up" || b.Anim.Cur!.TimeScale != 0))
                {
                    var a = b.Anim.Play("stand_up", new AnimOpts { Loop = false, Restart = true, Fade = 0 });
                    if (a != null) { a.Time = 0; a.TimeScale = 0; }
                }
                return;
            }
            if (S.OpenT != null && (S.OpenT -= dt) <= 0) W.ScorpOpenPanel(b);
            if (S.CloseT != null && (S.CloseT -= dt) <= 0) W.ScorpClosePanels(b);
            // the blow of the attack under way: at its start, then every second until its end
            var at = S.Attack >= 0 && S.Attack < SCORP.Length ? SCORP[S.Attack] : null;
            if (S.DmgT != null && (S.DmgT -= dt) <= 0) { if (at != null) W.ScorpDamage(b, at); S.DmgT = S.EndT != null && S.EndT > 1 ? 1 : null; }
            if (S.EndT != null && (S.EndT -= dt) <= 0) { S.EndT = null; S.DmgT = null; }
            if ((S.BusyT -= dt) > 0) return;
            S.BusyT = Step(b, W);
        }
        // AttackEnemy, called again whenever the animation it started is over: returns its length
        double Step(Building b, World W)
        {
            var S = b.Scorp!;
            if (S.Change)
            {
                S.Change = false; S.Attack = 4; S.Hit = 0;
                b.DmgStage = S.State == 2 ? 1 : S.State == 1 ? 2 : b.DmgStage;
                return ScorpPlay(b, S.State == 3 ? "standanim" : S.State == 2 ? "destroy_1" : S.State == 1 ? "destroy_2" : "dying");
            }
            if (S.State == 4) { S.State = 3; return ScorpPlay(b, "standanim"); }
            bool enemy = false;
            W.UHash.Query(b.Pos.X, b.Pos.Z, SCORP_RANGE, u => { if (!enemy && u.Alive && u.Owner != null && b.Owner != null && b.Owner.IsEnemy(u.Owner) && !u.Inside && Math.Sqrt((u.Pos.X - b.Pos.X) * (u.Pos.X - b.Pos.X) + (u.Pos.Z - b.Pos.Z) * (u.Pos.Z - b.Pos.Z)) < SCORP_RANGE) enemy = true; });
            if (!enemy) return 1;
            double Prepare() { S.Prep--; S.Hit++; return ScorpPlay(b, S.Attack >= 0 && S.Attack < SCORP.Length ? SCORP[S.Attack].Prepare : null); }
            if (S.Prep > 0) return Prepare();
            if (S.Hit == 0) { S.Attack = (int)Math.Floor(W.Rng.Next() * Math.Max(S.State, 1)); S.Prep = 1; return Prepare(); }
            if (S.Hit == 1) { S.Hit = 2; return ScorpPlay(b, "standanim"); }
            if (S.Hit == 2) { S.Prep = 1; return Prepare(); }
            S.Hit = 0;
            var at = S.Attack >= 0 && S.Attack < SCORP.Length ? SCORP[S.Attack] : null;
            if (at == null) return 1;
            S.DmgT = at.Start; S.EndT = at.End;
            return Math.Max(ScorpPlay(b, at.Anim), at.End);
        }
    }

    sealed class ScorpionPanelB : BuildingBehaviour
    {
        public override bool NoTower => true;
        // OnInit: not hitable, not selectable, SetAnim("open", 0, 0) = held at the clip's first frame: shut, flat in the
        // ground until the Scorpion opens it
        public override void Init(Building b, World W) { b.Untargetable = true; b.Unselectable = true; b.PanelOpen = false; b.PanelPose = "shut"; }
        public override void Update(Building b, World W, double dt)
        {
            var A = b.Anim;
            if (b.PanelPose != "shut" || A == null || !A.Has("open")) return;
            if (A.CurName != "open" || A.Cur == null || A.Cur.TimeScale != 0 || A.Cur.Time != 0)
            {
                var a = A.Play("open", new AnimOpts { Loop = false, Restart = true, Fade = 0 });
                if (a != null) { a.Time = 0; a.TimeScale = 0; }
            }
        }
        public override void Destroyed(Building b, World W)
        {
            var boss = W.Buildings.FirstOrDefault(x => x.Alive && x.Def!.Script == "CScorpionBoss" && x.Owner == b.Owner);
            if (boss?.Behaviour is ScorpionB sb) sb.PanelLost(boss, W);
        }
    }

    static readonly BuildingBehaviour GateBehaviour = new GateB();
    static readonly Dictionary<string, BuildingBehaviour> BEHAVIOURS = new()
    {
        ["CWall"] = new WallB(),
        ["CGate"] = GateBehaviour, ["CGateFOWVIsible"] = GateBehaviour, ["CNinigi_Defense_Skewer_Gate"] = GateBehaviour,
        ["CPitfall"] = new TrapB(TRAPS["CPitfall"]), ["CMinefield"] = new TrapB(TRAPS["CMinefield"]),
        ["CSnareTrap"] = new TrapB(TRAPS["CSnareTrap"]), ["CPoisonDung"] = new TrapB(TRAPS["CPoisonDung"]),
        ["CImpResinField"] = new ResinB(),
        ["CKennel"] = new NestB("hu_kennel_eusmilus"), ["CNinigi_Dilophosaurus_Nest"] = new NestB("ninigi_dilophosaurus"),
        ["CWarpGate"] = new WarpGateB(),
        ["CGrowingField"] = new FieldB(), ["CCornfield"] = new FieldB(),
        ["CScorpionBoss"] = new ScorpionB(), ["CScorpionPanel"] = new ScorpionPanelB(),
        // MIRAGE (MirageBuildings.cs): the SEAS healing well, the titans' launch pads
        ["CSeasWell"] = new SeasWellB(),
        ["CSeasScorpionLaunchpad"] = new LaunchpadB("seas_scorpion", 1, true, null),
        ["CHuAvatarLaunchpad"] = new LaunchpadB("hu_colossi", 0, false, null, "colossus"),
        ["CNinigiPirateBossLaunchpad"] = new LaunchpadB("ninigi_pirate_boss_ship", 0, false, "Bl_1", "pirate"),
        // MIRAGE's SEAS buildings (MirageSeasBuildings.cs); the portal terminus (CPortalTerminus, which the SEAS one
        // inherits) and the Norse / Dustrider / shared classes are in MIRAGE_CLASSES (MirageClasses.cs)
        ["CSeasWarpGate"] = new WarpGateB(),
        ["CSeasPsiDisruptor"] = new PsiDisruptorB(), ["CSeasSpeaker"] = new SpeakerB(), ["CSeasStygiNest"] = new StygiNestB(),
        ["CTeslaMachine"] = new TeslaMachineB(),
    };

    // ------------------------------------------------------------------------------------------ world mixin
    /// <summary>the behaviour of a building by its script class (gates, traps, nests, warp gate ...)</summary>
    public IBuildingBehaviour? BehaviourOf(Building b) => b.Def?.Script == null ? null
        : BEHAVIOURS.TryGetValue(b.Def.Script, out var x) ? x
        : MIRAGE_CLASSES.TryGetValue(b.Def.Script, out var m) ? m : InheritedBehaviour(b.Name);     // (MirageClasses.cs, PortalTerminus.cs)
    /// <summary>an attacker's weapon hit the building (combat.js takeDmg: behaviour.hit)</summary>
    public void BehaviourHit(Building b, Entity attacker) => (b.Behaviour as BuildingBehaviour)?.Hit(b, attacker, this);
    /// <summary>its weapon is not fired like a tower's (the Scorpion and its panels)</summary>
    public bool BehaviourNoTower(Building b) => (b.Behaviour as BuildingBehaviour)?.NoTower ?? false;
    /// <summary>ACDO "JumpOffWall": the Scorpion wakes up (campaign actions)</summary>
    /// <summary>ACDO JumpOffWall (actions.js: u.behaviour.activate); false: no such behaviour</summary>
    public bool ScorpionActivate(Building b) { if (b.Behaviour is not ScorpionB s) return false; s.Activate(b, this); return true; }

    /// <summary>gates: path finding lets the owner's team through an AUTO gate, everybody through an OPEN one, nobody
    /// through a CLOSED one</summary>
    // (GatesChanged: Version++ for the searches, the walk grid's blocks unchanged - the coarse graph keeps its nodes)
    public void SetGateCells(Building b) { foreach (var k in b.Cells) Nav.GateAt[k] = b; Nav.GatesChanged(); }
    public void ClearGateCells(Building b) { foreach (var k in b.Cells) if (Nav.GateAt.TryGetValue(k, out var g) && g == b) Nav.GateAt.Remove(k); Nav.GatesChanged(); }
    public void SetGate(Building b, int state)
    {
        if (!b.Def!.Gate) return;
        b.GateState = state;
        SetGateAnim(b, state == GATE.OPEN);
        Nav.GatesChanged();
    }
    /// <summary>ninja lock picking: the gate is forced open for 30 s</summary>
    public void OpenViolently(Building b)
    {
        if (b.GateState == GATE.OPEN || b.BrokenT > 0) return;
        b.FormerState = b.GateState; b.BrokenT = 30;
        SetGate(b, GATE.OPEN);
    }
    /// <summary>the resin field's Burn: false = on its cool-down (40 s) or not ready</summary>
    public bool BurnResin(Building b)
    {
        if (!b.Alive || !b.Built || b.Burning != null) return false;
        if (b.BurnCD > Time) return false;
        b.BurnCD = Time + 40;
        b.Burning = new BurnState();
        b.St.Camo.Delete("trap");
        b.LookChanged();
        return true;
    }
}
