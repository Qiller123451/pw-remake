// World methods for mission scripting (the trigger engine of the campaign calls these; nothing here is specific to
// one trigger). Game coordinates, Player objects and 1-based levels like everywhere in the world code; the campaign
// layer (src/Sim/Campaign: Campaign.cs, MissionHost.cs) wraps them for map coordinates, player slots and 0-based
// levels.
//
// Ported from remake/src/game/scripting.js (pw-exporter 98c7703). attackAllowed stays with combat (World.Stubs:
// AttackAllowed); unitModel is in the core (World.UnitModel).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>options of World.Spawn (scripting.js spawn opts)</summary>
public sealed class SpawnOpts
{
    /// <summary>1-based level; null = the class's lowest</summary>
    public int? Level;
    public double Heading;
    /// <summary>a building finished (default) or a construction site</summary>
    public bool Built = true;
    /// <summary>options of the unit (the zeppelin's height ...)</summary>
    public UnitOpts? Unit;
}

public sealed partial class World
{
    static readonly Logger LSc = Log.For("World.Scripting");
    static readonly Dictionary<string, string> RES_NAME = new(StringComparer.Ordinal) { ["food"] = "food", ["wood"] = "wood", ["stone"] = "stone", ["iron"] = "skulls", ["skulls"] = "skulls" };

    // ------------------------------------------------------------------ diplomacy (CDiplomacySrvMgr)
    /// <summary>relation of player a towards player b: 0 hostile, 1 neutral, 2 friendly. One direction per call, as the
    /// DIPL action does it (ActionFactory.usl:3291-3295); mutual = both directions.</summary>
    public void SetDiplomacy(Player a, Player b, int rel, bool mutual)
    {
        if (a == null || b == null || a == b) return;
        int was = a.Relation(b);
        a.SetRelation(b, rel);
        if (mutual) b.SetRelation(a, rel);
        if (was != rel || mutual) Emit("diplomacy", new Args { ["a"] = a, ["b"] = b, ["rel"] = rel, ["mutual"] = mutual });
    }

    /// <summary>a player's opinion of another set in the diplomacy window (SetMyOpinion, DiplomacyWindow.usl:155-172), told
    /// to everybody (ServerApp.usl:710-737: _NT_DiplChangeEnemy / Neutral / Ally)</summary>
    public bool ChangeOpinion(Player a, Player b, int rel)
    {
        if (a == null || b == null || a == b || a.Relation(b) == rel) return false;
        SetDiplomacy(a, b, rel, false);
        var key = new[] { "_NT_DiplChangeEnemy", "_NT_DiplChangeNeutral", "_NT_DiplChangeAlly" }[Math.Clamp(rel, 0, 2)];
        foreach (var p in Players) Emit("msg", new Args { ["player"] = p, ["key"] = key, ["arg"] = new object[] { a.Name, b.Name }, ["kind"] = rel == 0 ? "bad" : "" });
        return true;
    }

    /// <summary>resources sent to another player (ServerApp.usl TransferTribute 796-836): food, wood or stone, at most what
    /// the sender has, to anybody (enemies too), no fee, and the receiver's storage limit does not apply</summary>
    public void Tribute(Player from, Player to, string res, double n) => TributeN(from, to, res, n);

    /// <summary>Tribute; returns the amount sent</summary>
    public double TributeN(Player from, Player to, string res, double n)
    {
        if (from == null || to == null || from == to || !(res == "food" || res == "wood" || res == "stone")) return 0;
        n = Math.Min(double.IsFinite(n) ? Math.Floor(n) : 0, Math.Floor(from[res]));
        if (n <= 0) return 0;
        from.Res[res] = from[res] - n;
        to.Res[res] = to[res] + n;
        Emit("tribute", new Args { ["from"] = from, ["to"] = to, ["res"] = res, ["n"] = n });
        Emit("msg", new Args { ["player"] = to, ["key"] = "_NT_DiplTribute", ["arg"] = new object[] { Data.Ui("_UI_DiplWind_" + res, res), n, from.Name } });
        return n;
    }

    /// <summary>an attack order on a non-hostile player's object: both sides become hostile (FightingObj.usl:6613-6614)</summary>
    partial void DeclareWar(Player a, Player b) => DeclareWarNow(a, b);

    /// <summary>DeclareWar for callers outside World (the commands, the campaign); true = the relation changed</summary>
    public bool DeclareWarNow(Player a, Player b)
    {
        if (a == null || b == null || a == b || (a.IsEnemy(b) && b.IsEnemy(a))) return false;
        a.SetRelation(b, 0); b.SetRelation(a, 0);
        Emit("diplomacy", new Args { ["a"] = a, ["b"] = b, ["rel"] = 0, ["mutual"] = true, ["attacked"] = true });
        // a skirmish: everybody hears of it (_NT_DiplChangeEnemyAttacked, FightingObj.usl:6613-6618)
        if (Opts.Multiplayer) foreach (var p in Players) Emit("msg", new Args { ["player"] = p, ["key"] = "_NT_DiplChangeEnemyAttacked", ["arg"] = new object[] { a.Name, b.Name }, ["kind"] = "bad" });
        return true;
    }

    // ------------------------------------------------------------------ objects
    /// <summary>create an object by class name: a unit (level 1-based, default the class's lowest) or a finished building.
    /// Returns the entity, or null (unknown class, model not loaded, a second unique hero).</summary>
    public Entity? Spawn(string cls, Player? owner, double x, double z, SpawnOpts? opts = null)
    {
        opts ??= new SpawnOpts();
        var ix = Data.Info(cls);
        if (ix == null) { LSc.WarnOnce("spawn|" + cls, "spawn: unknown class " + cls); return null; }
        if (ix.Type == "BLDG") return PlaceBuilding(cls, owner, x, z, opts.Heading, opts.Built);
        var u = SpawnUnit(cls, owner, x, z, opts.Level is int l && l > 0 ? l : null, opts.Heading, opts.Unit);
        if (u != null && owner == null && ix.Type != "ANML") { u.Wild = false; u.Stance = 3; }      // an ownerless soldier / ship just stands there
        return u;
    }

    /// <summary>a free ground (or water) cell near (x, z)</summary>
    public (double X, double Z) FreeSpot(double x, double z, bool water = false, bool amphib = false, int radius = 40)
    {
        var nav = water ? (WaterNav ?? Nav) : amphib ? NavFor(false, true) : Nav;
        if (nav.IsFree(x, z)) return (x, z);
        return nav.Center(nav.NearestFree(nav.Idx(x, z), radius));
    }

    /// <summary>delete without death: no animation, no corpse, no spirit, no skulls, not counted as a loss. Emits 'removed'.</summary>
    public bool RemoveEntity(Entity e)
    {
        if (e == null || !e.Alive) return false;
        if (e is Unit iu && iu.Inside) LeaveTransport(iu);
        e.SilentDeath = true;
        int n = Events.Count, lost = e.Owner?.Lost ?? 0;
        Kill(e, null);
        for (int i = Events.Count - 1; i >= n; i--) { var ev = Events[i]; if ((ev.Type == "died" || ev.Type == "destroyed") && ev.E("entity") == e) Events.RemoveAt(i); }
        if (e.Owner != null) e.Owner.Lost = lost;
        if (e is Unit cu && cu.CorpseNode != null && cu.CorpseNode.Alive) DepleteNode(cu.CorpseNode);
        Emit("removed", new Args { ["entity"] = e });
        return true;
    }

    /// <summary>change the owner of a unit or building (null = nobody). Population, pyramid, heroes, tech tree filters and
    /// storage follow; the model is rebuilt in the new party colour; orders and production are dropped.</summary>
    public bool SetOwner(Entity e, Player? p)
    {
        if (e == null || !e.Alive || e.Owner == p) return false;
        var from = e.Owner;
        if (e is Unit u)
        {
            ReleaseTask(u);
            DropStates(u);
            DropQueue(u);
            if (from != null)
            {
                EntityFilters(u, false);
                if (u.CountsInPop) { from.Units--; from.AtLevel[u.Level - 1]--; }
                if (u.Def!.Unique) from.Heroes.Delete(u.Name);
            }
            u.Owner = p; u.OwnerChanged();
            u.Wild = p == null && u.Cls == "ANML";
            if (p == null && u.Cls != "ANML") u.Stance = 3;
            if (p != null)
            {
                if (u.CountsInPop) { p.Units++; p.AtLevel[u.Level - 1]++; }
                if (u.Def!.Unique) p.Heroes.Add(u.Name);
                EntityFilters(u, true);
            }
            double ratio = u.Hp / u.MaxHp;
            u.ReadRules();
            u.Hp = Math.Max(1, Math.Min(u.MaxHp, ratio * u.MaxHp));
            u.BuildModel();
            u.Task = new UnitTask(u.Inside ? "inside" : "idle");
            u.Path = new NavPath(); u.Attackers.Clear(); u.AutoMovesCache = null;
            u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z;
            foreach (var q in u.Passengers?.ToList() ?? new List<Unit>()) SetOwner(q, p);
        }
        else if (e is Building b)
        {
            DropQueue(b);
            if (from != null && b.Built) EntityFilters(b, false);
            b.Owner = p; b.OwnerChanged();
            if (p != null && b.Built) EntityFilters(b, true);
            b.RefreshRules();
            b.SwapModel(b.Gfx ?? "");
            // (the tower's forced target and current target belong to combat: it drops them on the 'owner' event)
        }
        else return false;
        RecomputeCaps(from); RecomputeCaps(p);
        Emit("owner", new Args { ["entity"] = e, ["from"] = from, ["to"] = p });
        return true;
    }

    // a changed owner refunds what was queued (scripting.js setOwner: refund + the queued pyramid places; the
    // production port's CancelQueue does both)
    void DropQueue(Entity e)
    {
        for (int i = e.Queue.Count - 1; i >= 0; i--) CancelQueue(e, i);
        e.Queue.Clear();
    }

    /// <summary>hit points: an absolute value (clamped to 1 .. maximum); 0 or less kills</summary>
    public void SetHp(Entity e, double hp)
    {
        if (e == null || !e.Alive) return;
        if (hp <= 0) { Kill(e, null); return; }
        e.Hp = Math.Max(1, Math.Min(e.MaxHp, hp));
        if (e is Building b) b.UpdateVisual();
    }

    /// <summary>the level designer's invulnerability (SetLDInvulnerable): on / off, or on for `seconds`</summary>
    public void SetInvulnerable(Entity e, bool on, double seconds = 0)
    {
        if (e == null) return;
        double v = on ? 1e12 : 0;
        if (e is Unit u) u.InvulnT = v; else if (e is Building b) b.InvulnT = v;
        e.InvulnUntil = on && seconds > 0 ? Time + seconds : 0;
        if (e.InvulnUntil != 0) Later(seconds, "invulnEnd", new Args { ["e"] = e });
    }

    /// <summary>MIRAGE's WeaponRemove (FightingObj.RemoveAllWeapons): on = no attacks (towers don't shoot, units don't
    /// fight), off = armed again; on for `seconds` when given (TIMER_WEAPON_REMOVE)</summary>
    public void SetDisarmed(Entity e, bool on, double seconds = 0)
    {
        if (e == null) return;
        e.Disarmed = on;
        e.DisarmUntil = on && seconds > 0 ? Time + seconds : 0;
        if (e.DisarmUntil != 0) Later(seconds, "disarmEnd", new Args { ["e"] = e });
        if (on && e is Unit u && u.Task.Type == "attack") Order(u, new Order("stop"));
        // (a tower's targets: combat checks Disarmed before it shoots)
    }

    /// <summary>set a unit's level (1..5) without paying skulls or playing the level-up (SetLevelClean); full hit points</summary>
    public bool SetLevel(Unit u, int level)
    {
        if (u == null || !u.Alive) return false;
        int nl = Math.Max(1, Math.Min(5, level));
        while (nl > 1 && !(Data.Stats(u.Name, nl, u.Owner)?.Exists ?? false)) nl--;
        nl = Math.Max(nl, Data.MinLevel(u.Name, u.Owner));
        if (nl == u.Level) return true;
        var p = u.Owner; int ol = u.Level;
        if (p != null && u.CountsInPop) { p.AtLevel[ol - 1]--; p.AtLevel[nl - 1]++; }
        LevelFilters(u, ol, false);
        var task = u.Task;
        u.ApplyLevel(nl);
        u.Hp = u.MaxHp;
        LevelFilters(u, nl, true);
        u.Task = task;
        return true;
    }

    /// <summary>move a unit to (x, z) at once: the current order ends, it leaves its transport</summary>
    public bool Teleport(Entity e, double x, double z, double? heading = null)
    {
        if (e == null || !e.Alive || e is not Unit u) return false;
        if (u.Inside) LeaveTransport(u);
        ReleaseTask(u);
        DropStates(u);
        var nav = NavFor(u);
        if (!nav.IsFree(x, z)) (x, z) = nav.Center(nav.NearestFree(nav.Idx(x, z)));
        u.Task = UnitTask.Idle(); u.Path = new NavPath(); u.Vel = V3.Zero; u.BusyAnim = false;
        u.Pos = new V3(x, 0, z); u.Pos.Y = GroundY(u);
        if (heading != null) u.Heading = heading.Value;
        u.AnchorX = x; u.AnchorZ = z; u.HomeX = x; u.HomeZ = z;
        u.LookChanged();
        if (!u.Parked) UHash.Move(u);
        return true;
    }

    /// <summary>take an object out of the world without deleting it (OBAP without the "visible" flag: hidden
    /// reinforcements, scenery that appears later): not drawn, not found, not hit, does nothing. parked = false brings it
    /// back.</summary>
    public void SetParked(Entity e, bool parked)
    {
        if (e == null || !e.Alive || e.Parked == parked) return;
        e.Parked = parked;
        if (e is Unit u)
        {
            if (parked) { ReleaseTask(u); DropStates(u); u.Task = UnitTask.Idle(); u.Path = new NavPath(); u.Vel = V3.Zero; if (!u.Inside) UHash.Remove(u); }
            else if (!u.Inside) { UHash.Insert(u); u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z; }
            // (a trailer - the Dustriders' collector wagon - is drawn apart from its unit: the view hides it with it and
            // hitches it again where the unit comes back)
        }
        else if (e is Building b)
        {
            if (parked) { BHash.Remove(b); if (b.Built) b.SetBlocking(false); }
            else { BHash.Insert(b); if (b.Built) b.SetBlocking(true); }
        }
        e.LookChanged();
    }

    // ------------------------------------------------------------------ players
    /// <summary>RSRC: res = food | wood | stone | iron (= skulls); mod = '+n' | '-n' | '=n' | 'n' (set). cap: limit the
    /// result to the storage (not for skulls); never below 0 (ActionFactory.usl:334-511).</summary>
    public void SetResource(Player p, string res, string mod, bool cap = false)
    {
        if (p == null || !RES_NAME.TryGetValue((res ?? "").ToLowerInvariant(), out var r)) return;
        var m = (mod ?? "").Trim();
        double n = TrigInt(m.Length > 0 && (m[0] == '+' || m[0] == '=') ? m.Substring(1) : m);
        double v = m.StartsWith('+') ? p[r] + n : m.StartsWith('-') ? p[r] + n : n;
        if (cap && r != "skulls") v = Math.Min(v, p.Caps[r]);
        p.Res[r] = Math.Max(0, v);
    }
    // parseInt(s, 10) || 0
    static double TrigInt(string s) => PwRemake.Sim.Scripting.TrigUtil.ToInt(s);

    /// <summary>PLCP: fixed storage limits (rescap_food / wood / stone); buildings no longer change them</summary>
    public void SetCaps(Player p, double? food, double? wood, double? stone)
    {
        if (p == null) return;
        if (food is double f && double.IsFinite(f)) p.Caps["food"] = f;
        if (wood is double w && double.IsFinite(w)) p.Caps["wood"] = w;
        if (stone is double s && double.IsFinite(s)) p.Caps["stone"] = s;
        p.CapsFixed = true;
        if (p.Cheat?.Storage is double st) foreach (var r in Rules.Rules.RES) p.Caps[r] = Math.Max(p.Caps[r], st);    // (debug option)
    }

    /// <summary>tech tree filters of a player (TECH action, the players' start filters). path as in the data:
    /// "/Filters/AntiActions/Hu/Build/CHTR/hu_archer" - an enabled AntiAction FORBIDS that build / upgrade.
    /// A filter is on or off (no counting): enabling twice and disabling once leaves it off.</summary>
    public bool SetFilter(Player p, string path, bool on)
    {
        if (p?.Tt == null || string.IsNullOrEmpty(path)) return false;
        var f = StripF(path);
        var key = "script:" + f;
        if (on && p.UnlockAll && f.StartsWith("AntiActions/", StringComparison.Ordinal)) return true;   // (debug option "no mission bans": game/cheats.js)
        if (on && !p.ScriptFilters.Has(key)) { p.ScriptFilters.Add(key); p.Tt.Enable(f); }
        else if (!on && p.ScriptFilters.Has(key)) { p.ScriptFilters.Delete(key); p.Tt.Disable(f); }
        return true;
    }
    public bool HasFilter(Player p, string path) => p?.Tt != null && p.Tt.Has(StripF(path));
    // String(path).replace(/^\/?Filters\//, '')
    static string StripF(string path) => path.StartsWith("/Filters/", StringComparison.Ordinal) ? path.Substring(9) : path.StartsWith("Filters/", StringComparison.Ordinal) ? path.Substring(8) : path;

    // ------------------------------------------------------------------ fog of war (SFOW: ShowFOW_Obj)
    /// <summary>player p sees a circle of radius r around (x, z) for `seconds` (0 or less = until HideArea), or around the
    /// entity `follow` for as long as it lives. Returns a handle for HideArea.</summary>
    public Reveal RevealArea(Player? p, double x, double z, double r, double seconds = 0, Entity? follow = null)
    {
        var h = new Reveal { Player = p, X = x, Z = z, R = r, Until = seconds > 0 ? Time + seconds : double.PositiveInfinity, Follow = follow };
        Reveals.Add(h);
        FogDirty = true;                 // (the fog of war shows it at the end of this step: FogOfWar.cs)
        return h;
    }
    public void HideArea(Reveal? h) { if (h != null) { h.Alive = false; FogDirty = true; } }

    /// <summary>the reveal circles a viewer set sees now: (x, z, r) each; sees(player) -> bool</summary>
    public List<(double X, double Z, double R)> RevealsFor(Func<Player?, bool> sees)
    {
        var res = new List<(double, double, double)>();
        bool dead = false;
        foreach (var h in Reveals)
        {
            if (!h.Alive || h.Until <= Time || (h.Follow != null && !h.Follow.Alive)) { h.Alive = false; dead = true; continue; }
            if (!sees(h.Player)) continue;
            var f = h.Follow;
            res.Add(f != null ? (f.Pos.X, f.Pos.Z, h.R) : (h.X, h.Z, h.R));
        }
        if (dead) Reveals = Reveals.Where(h => h.Alive).ToList();
        return res;
    }

    // ------------------------------------------------------------------ delayed ends (DELAYED)
    // the end of a timed invulnerability (a later call for the same object may have extended it)
    [Delayed("invulnEnd")]
    static void InvulnEnd(World W, Args a)
    {
        var e = a.E("e");
        if (e == null || e.InvulnUntil == 0 || W.Time < e.InvulnUntil - 1e-6) return;
        if (e is Unit u) u.InvulnT = 0; else if (e is Building b) b.InvulnT = 0;
        e.InvulnUntil = 0;
    }
    [Delayed("disarmEnd")]
    static void DisarmEnd(World W, Args a)
    {
        var e = a.E("e");
        if (e == null || e.DisarmUntil == 0 || W.Time < e.DisarmUntil - 1e-6) return;
        e.Disarmed = false; e.DisarmUntil = 0;
    }

    // ------------------------------------------------------------------ wrappers of system hooks for the campaign
    /// <summary>economy's recomputeCaps for callers outside World</summary>
    /// <summary>economy's depleteNode for callers outside World (a building clears the trees under it)</summary>
    public void DepleteNodeOf(ResNode n) => DepleteNode(n);
    /// <summary>transport's leaveTransport for callers outside World</summary>
    public void LeaveTransportOf(Unit u) => LeaveTransport(u);
    /// <summary>transport's unloadAll for callers outside World</summary>
    public void UnloadAllOf(Unit t, bool here) => UnloadAll(t, here);

    // ------------------------------------------------------------------ the campaign's turn in the step
    partial void CampaignUpdate(double dt) => Campaign?.Update(dt);
}
