// The condition types of the campaign triggers (remake/docs/spec/triggers.md §4; original: Server/misc/
// ConditionFactory.usl = "CF"). A condition has a state 0 / 1 and tells its trigger when the state may have changed
// (Inv() -> engine.Invalidate). OnEnabled() / OnDisabled() are called when its trigger starts / stops listening.
//
// How each type hears about the world (the engine's channels, TriggerEngine.cs):
//   tick       TIME, RTME                    own countdown on game time
//   push       TRUE (on enable), TIMR (level timer ran out), SQEN, DSEN, QUES, CVAR (variables changed), CKGR (group
//              changed), DIPL, ISFG (attack event), WAYR (waypoint path finished), ITEM (item taken), PLDE (object lost)
//   objects    REGN, DEAD, DYIN, BLDG        re-evaluated when the set of objects changed (spawn, removal, owner,
//                                            group, appearance) - and, where a region is named, polled twice a
//                                            second because walking raises no event
//   poll       OBJP (0.25 s), PLYR, TECH (0.5 s), SGHT (2 s, as in the original), UNTT, CHCK (Lua)
//
// Every type is one class, named by its 4-letter code; the readable Lua names are in TriggerTypes.cs.
// Ported from remake/src/game/campaign/conditions.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PwRemake.Sim.Scripting;

/// <summary>Base of every condition type (conditions.js Cond).</summary>
public abstract class Condition
{
    public TriggerEngine E = null!;
    public Trigger T = null!;
    public NodeDef Node = null!;
    public string Type = "";
    public TriggerParams P = null!;
    public int Index;
    public int State;
    public bool Live;
    internal bool PulseOnEnable;
    internal double NextPoll, Every;

    internal void Setup(TriggerEngine e, Trigger t, NodeDef n, TriggerParams p, int i) { E = e; T = t; Node = n; Type = n.Code; P = p; Index = i; }
    protected IMissionHost H => E.Host;

    public virtual void Init() { }
    public virtual void OnEnabled() { }
    public virtual void OnDisabled() { }
    public virtual void Tick(double dt) { }
    public virtual void Poll() { }
    public virtual void OnObjects() { }
    public virtual void OnVars() { }
    public virtual void OnRemoved(int obj) { }
    public virtual void OnLost(int obj, int owner) { }
    public virtual void OnGroup(int group) { }
    public virtual void OnDiplomacy() { }
    public virtual void OnAttacked(int victim, int attacker) { }
    public virtual void OnBoarded(int unit, int transport) { }
    public virtual void OnItem(string cls, int taker) { }
    public virtual void OnWaypoint(int obj) { }
    public virtual void OnQuest(QuestState q) { }
    public virtual void OnTimer(string id) { }
    public virtual void OnSequence(string file) { }
    public virtual void OnDialog(string file) { }
    /// <summary>What the debug view shows next to the state.</summary>
    public virtual string Info() => "";

    protected void Inv() => E.Invalidate(T);
    /// <summary>SetState + Invalidate.</summary>
    protected void Set(bool s) { State = s ? 1 : 0; Inv(); }
    /// <summary>Set and invalidate only if something changed (polled conditions: the poll itself is not an event).</summary>
    protected void Change(bool s) { int v = s ? 1 : 0; if (v != State) { State = v; Inv(); } }
    /// <summary>The objects of the query block with that prefix (§2).</summary>
    public IReadOnlyList<int> Sel(string prefix = "") => H.Select(P, prefix);
    protected Func<string, string?> Vars => E.VarLookup;
    protected void StartPoll(double every, double? first = null) { Every = every; NextPoll = E.Time + (first ?? every); E.Polled.Add(this); }
    protected void StopPoll() => E.Polled.Remove(this);
    protected int PlayerSlot(string key = "player_id") => (int)P.I(key);
    protected bool Regional(string prefix = "") { var r = P[prefix + "rgn_guid"]; return !string.IsNullOrEmpty(r) && !H.IsWorldRegion(r); }
    // the query asks for the human player's objects of a class that the tribe the mission is played with has nothing
    // of (campaign/tribes.js missing(): no age-2 archer carrier for the Dragon Clan): such a condition is met
    protected bool MissingClass(string prefix = "")
    {
        var cls = P[prefix + "obj_class"];
        var owner = P[prefix + "obj_owner"];
        return !string.IsNullOrEmpty(cls) && cls != "NA" && !string.IsNullOrEmpty(owner) && TrigUtil.ToInt(owner) == H.HumanSlot && H.IsMissingClass(cls, H.HumanSlot);
    }
}

public static class Conditions
{
    /// <summary>An unknown type: never true.</summary>
    public sealed class NONE : Condition { }

    // ------------------------------------------------------------------------------------------------ 4.1 time
    /// <summary>CF:81-232. One-shot countdown from the moment the trigger is enabled; a trigger that is not `once`
    /// restarts it after firing (= fires every `duration` seconds). reset = 0: a disabled timer keeps the time it
    /// had left. show = 1: a countdown on the HUD (Timers "time:&lt;guid&gt;:&lt;n&gt;").</summary>
    public sealed class TIME : Condition
    {
        double duration; double? left; bool reset, show;
        public override void Init() { duration = Math.Max(0, P.N("duration", 0)); left = null; reset = P.S("reset") != "0"; show = P.S("show") == "1"; }
        string Key => "time:" + T.Guid + ":" + Index;
        public override void OnEnabled()
        {
            State = 0;
            if (left == null || reset || left <= 0) left = duration;
            E.Ticking.Add(this);
            if (show) E.SetCondTimer(new LevelTimer { Id = Key, Left = left.Value, Total = duration, Show = true, Cond = true, Countup = P.S("countup") == "1" });
        }
        void Stop() { E.Ticking.Remove(this); if (show) E.RemoveTimer(Key); }
        public override void OnDisabled() => Stop();
        public override void Tick(double dt)
        {
            left -= dt;
            if (show && E.Timers.TryGetValue(Key, out var tm)) tm.Left = Math.Max(0, left ?? 0);
            if (left > 0) return;
            Stop();
            left = null;
            State = 1;
            bool once = T.Once;
            Inv();
            // CF:112-125: not once -> the timer starts again (and the state is 0 again after the evaluation)
            if (!once && T.Live) OnEnabled();
        }
        public override string Info() => $"left {left:0.0}";
    }

    /// <summary>CF:3741-3806: max(Min, random % Max) seconds; restarts when the trigger is not once.</summary>
    public sealed class RTME : Condition
    {
        double left;
        public override void OnEnabled()
        {
            State = 0;
            long min = P.I("Min"), max = Math.Max(1, P.I("Max"));
            left = Math.Max(min, Math.Floor(H.Random() * max));
            E.Ticking.Add(this);
        }
        public override void OnDisabled() => E.Ticking.Remove(this);
        public override void Tick(double dt)
        {
            left -= dt;
            if (left > 0) return;
            E.Ticking.Remove(this);
            State = 1;
            bool once = T.Once;
            Inv();
            if (!once && T.Live) OnEnabled();
        }
        public override string Info() => $"left {left:0.0}";
    }

    /// <summary>CF:234-270: "fire as soon as this trigger is enabled".</summary>
    public sealed class TRUE : Condition { public override void OnEnabled() { State = 0; Set(true); } }

    /// <summary>CF:272-324, ObjTime.usl:161-180: pushed when the level timer with this id runs out - also while the
    /// trigger is disabled (the state then stays 1). Nothing happens on enable.</summary>
    public sealed class TIMR : Condition
    {
        string id = "0";
        public override void Init() { id = P.SE("timer_id", "0"); E.Forever(Channel.Timer, this); }
        public override void OnTimer(string t) { if (t != id) return; State = 0; Set(true); }
    }

    // ------------------------------------------------------------------------------------------------ 4.2 objects
    /// <summary>CF:850-1033. Count test on the query result; "the whole group is in the region" for obj_type GROU.</summary>
    public sealed class REGN : Condition
    {
        string count = ""; bool usesVars, group; string sig = "";
        /// <summary>The objects found at the last check (from_condition of actions, §2.5).</summary>
        public IReadOnlyList<int> Result = Array.Empty<int>();
        public override void Init() { count = P.S("obj_count"); usesVars = count.Contains("$("); group = P.S("obj_type") == "GROU"; }
        public override void OnEnabled()
        {
            E.Sub(Channel.Objects, this);
            if (usesVars) E.Sub(Channel.Vars, this);
            if (Regional()) StartPoll(0.5, 0.25 + (T.Index % 5) * 0.05);
            Check(true);
        }
        public override void OnDisabled() { E.Unsub(Channel.Objects, this); E.Unsub(Channel.Vars, this); StopPoll(); State = 0; }
        public override void OnObjects() => Check(false);
        public override void OnVars() => Check(false);
        public override void Poll() => Check(false);
        void Check(bool force)
        {
            IReadOnlyList<int> res;
            bool s;
            if (group)
            {
                // CF:873-917: every member of the group is among the result (the query runs without the type filter; by
                // name that is the group's members inside the region). An empty group or an empty result: the count test.
                int g = H.FindGroup(P["obj_guid"]);
                if (g < 0) g = H.FindGroup(P["obj_name"]);
                var members = g >= 0 ? H.GroupMembers(g) : Array.Empty<int>();
                res = H.Select(P.With("obj_type", "All"), "");
                if (members.Count > 0 && res.Count > 0) { var set = new HashSet<int>(res); s = members.All(set.Contains); }
                else s = TrigUtil.Compare(res.Count, count, Vars);
            }
            else
            {
                res = Sel();
                s = MissingClass() || TrigUtil.Compare(res.Count, count, Vars);
            }
            Result = res;
            // the original evaluates on every push of the region; here: when the result set changed
            var sb = new System.Text.StringBuilder().Append(res.Count).Append(':');
            if (res.Count <= 24) foreach (var r in res) sb.Append(r).Append(',');
            var nsig = sb.ToString();
            bool changed = nsig != sig || (s ? 1 : 0) != State;
            sig = nsig;
            State = s ? 1 : 0;
            if (force || changed) Inv();
        }
        public override string Info() => $"found {Result.Count}, test {count}";
    }

    /// <summary>CF:1349-1472 / 1478-1596. Both watch the objects the query finds; a deletion sets the state. DEAD is
    /// also true when a refresh finds nothing at all ("already dead or never existed") and false again when it finds
    /// something; DYIN stays 1 once set and is never true for an empty query. The remake has one moment for both:
    /// the death.</summary>
    public class DEAD : Condition
    {
        protected HashSet<int> Watch = new();
        protected bool Dying;
        bool first;
        public override void OnEnabled()
        {
            E.Sub(Channel.Removed, this); E.Sub(Channel.Objects, this);
            Refresh();
            first = true;
            // the first "region push" of the original comes with the next object event: here the next step. A named
            // region is then polled (objects walk in and out without an event).
            StartPoll(Regional() ? 1 : 1e9, 0.05);
        }
        public override void OnDisabled() { E.Unsub(Channel.Removed, this); E.Unsub(Channel.Objects, this); StopPoll(); }
        void Refresh() => Watch = new HashSet<int>(Sel());
        public override void OnRemoved(int obj) { if (Watch.Remove(obj)) Set(true); }
        public override void OnObjects() => Poll();
        public override void Poll()
        {
            Refresh();
            if (Dying) { if (State != 0) Inv(); return; }
            int s = Watch.Count > 0 ? 0 : 1;
            bool f = first; first = false;
            if (s != State || (f && s != 0)) Set(s != 0);
        }
        public override string Info() => $"watching {Watch.Count}";
    }
    public sealed class DYIN : DEAD
    {
        public override void Init() { Dying = true; }
        public override void OnRemoved(int obj) { if (Watch.Remove(obj)) Set(true); else if (State != 0) Inv(); }
    }

    /// <summary>CF:1165-1343: an attribute of the objects of the query; the state follows the object that changed
    /// last (on enable all are checked in order, the last one wins). Attributes: hitpoints (with attrib_max =
    /// maxhitpoints: percent), level (0-based), GateState, CurTask, else the object's data attribute.</summary>
    public sealed class OBJP : Condition
    {
        string op = "==", val = "", attr = "", max = "";
        Dictionary<int, string> seen = new();
        public override void Init()
        {
            var v = P.S("attrib_value").Trim();
            var m = Regex.Match(v, @"^(<=|>=|<|>|==|=)?\s*(.*)$", RegexOptions.Singleline);
            op = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "=="; val = m.Groups[2].Value;
            attr = P.S("attrib_name").ToLowerInvariant();
            max = P.S("attrib_max").ToLowerInvariant();
        }
        string? Value(int obj, ObjectInfo o)
        {
            switch (attr)
            {
                case "hitpoints":
                    if (!o.HasEntity) return null;
                    if (max == "maxhitpoints" && o.MaxHp > 0) return TrigUtil.NumStr(Math.Truncate(Math.Max(0, o.Hp) * 100 / o.MaxHp));
                    return TrigUtil.NumStr(Math.Max(0, Math.Round(o.Hp)));
                case "maxhitpoints": return o.HasEntity ? TrigUtil.NumStr(Math.Round(o.MaxHp)) : null;
                case "level": return o.HasEntity && o.IsUnit ? (o.Level - 1).ToString() : null;
                case "gatestate": return o.HasEntity ? o.GateState : null;
                case "curtask": return o.HasEntity ? o.Task : null;
                default: return H.DataAttribute(obj, P.S("attrib_name"));
            }
        }
        bool Test(string v)
        {
            if (!Regex.IsMatch(v, @"^-?\d+$")) return op == "==" || op == "=" ? string.Equals(v, val, StringComparison.OrdinalIgnoreCase) : false;
            long a = TrigUtil.ToInt(v), b = TrigUtil.ToInt(TrigUtil.ValueString(val, Vars));
            return op switch { "<" => a < b, ">" => a > b, "<=" => a <= b, ">=" => a >= b, _ => a == b };
        }
        public override void OnEnabled() { seen = new Dictionary<int, string>(); StartPoll(0.25, 0); }
        public override void OnDisabled() => StopPoll();
        public override void Poll()
        {
            int s = State; bool any = false;
            foreach (var obj in Sel())
            {
                var o = H.Info(obj);
                if (o == null) continue;
                var v = Value(obj, o);
                if (v == null) continue;
                if (seen.TryGetValue(obj, out var old) && old == v) continue;
                seen[obj] = v;
                s = Test(v) ? 1 : 0; any = true;
            }
            // an object that died since the last look: its hit points went to 0 before it was deleted
            if (attr == "hitpoints")
                foreach (var (obj, v) in seen.ToArray())
                    if (H.Info(obj) is not { Alive: true } && v != "0") { seen[obj] = "0"; s = Test("0") ? 1 : 0; any = true; }
            if (!any) return;
            if (s != State || s != 0) Set(s != 0);
        }
    }

    /// <summary>CF:2813-2977: an item of class item_class was taken by an object of the query (item_whatever = any
    /// item); also true on enable if such an object already carries one. A pulse.</summary>
    public sealed class ITEM : Condition
    {
        bool Match(string? cls) { var want = P.SE("item_class", "item_whatever").ToLowerInvariant(); return want == "item_whatever" || want == (cls ?? "").ToLowerInvariant(); }
        public override void OnEnabled()
        {
            E.Sub(Channel.Item, this);
            State = 0;
            // already carried: true for the evaluation that ends the enabling (engine Listen), then 0 again
            foreach (var obj in Sel())
            {
                var o = H.Info(obj);
                if (o != null && o.CarriedItem != null && Match(o.CarriedItem)) { State = 1; PulseOnEnable = true; Inv(); return; }
            }
        }
        public override void OnDisabled() => E.Unsub(Channel.Item, this);
        void Pulse() { State = 1; Inv(); State = 0; }
        public override void OnItem(string cls, int taker) { if (Match(cls) && Sel().Contains(taker)) Pulse(); }
    }

    /// <summary>CF:2983-3149: finished objects of `class` and `owner` in the query's region; optionally written to a
    /// variable.</summary>
    public sealed class BLDG : Condition
    {
        string value = "", cls = ""; int owner; long n = -1; bool met;
        public override void Init()
        {
            value = P.S("value"); cls = P.S("class").ToLowerInvariant(); owner = (int)P.I("owner");
            // played with another tribe that has nothing of this role (no houses for the Dragon Clan): met at once
            met = owner == H.HumanSlot && cls.Length > 0 && H.IsMissingClass(cls, owner);
        }
        public override void OnEnabled() { E.Sub(Channel.Objects, this); if (value.Contains("$(")) E.Sub(Channel.Vars, this); StartPoll(1, 1); Check(true); }
        public override void OnDisabled() { E.Unsub(Channel.Objects, this); E.Unsub(Channel.Vars, this); StopPoll(); }
        public override void OnObjects() => Check(false);
        public override void OnVars() => Check(false);
        public override void Poll() => Check(false);
        void Check(bool force)
        {
            long c = H.CountFinished(cls, owner, P["rgn_guid"]);
            // met at once (the tribe has nothing of the role): the counter it writes shows the goal reached too - a
            // quest that waits on the variable rather than on this condition (mission 13: "count Dilophonests" > 7 in
            // CBL_L02b) would otherwise stay open with "0 / 8" on the screen
            if (met && !TrigUtil.Compare(c, value, Vars)) c = Reaching(value);
            int s = met || TrigUtil.Compare(c, value, Vars) ? 1 : 0;
            bool changed = c != n || s != State;
            n = c; State = s;
            var variable = P["variable"];
            if (changed && !string.IsNullOrEmpty(variable) && E.GetVarOrNull(variable) != c.ToString()) { E.SetVarQuiet(variable, c.ToString()); E.VarsChanged(); }
            if (force || changed) Inv();
        }
        public override string Info() => $"count {n}";
        // the smallest count >= 0 that meets the comparison `value` (">7" -> 8)
        long Reaching(string v)
        {
            long k = TrigUtil.ToInt(TrigUtil.ValueString(System.Text.RegularExpressions.Regex.Replace(v.Trim(), "^[<>=!]+", ""), Vars));
            foreach (var x in new[] { Math.Max(0, k), k + 1, 0 }) if (TrigUtil.Compare(x, v, Vars)) return x;
            return Math.Max(0, k);
        }
    }

    /// <summary>CF:3243-3326: polled every 2 s: an object of A sees an object of B (inside its sight range; the
    /// fog-of-war state of the players is ignored, §8 no. 9).</summary>
    public sealed class SGHT : Condition
    {
        public override void OnEnabled() => StartPoll(2, 0);
        public override void OnDisabled() => StopPoll();
        public override void Poll()
        {
            var A = Sel(""); var B = Sel("B_");
            var bs = B.Select(H.Info).ToList();
            bool s = false;
            foreach (var a in A)
            {
                var e = H.Info(a);
                if (e == null || e.Parked) continue;
                double sight = e.Sight > 0 ? e.Sight : 25;
                for (int i = 0; i < B.Count && !s; i++)
                {
                    var b = bs[i];
                    if (B[i] == a || b == null || b.Parked) continue;
                    if (Math.Sqrt((e.X - b.X) * (e.X - b.X) + (e.Y - b.Y) * (e.Y - b.Y)) <= sight + b.Radius) s = true;
                }
                if (s) break;
            }
            Change(s);
        }
    }

    /// <summary>CF:3653-3739: at every attack event (victim, attacker): A contains the attacker and B the victim
    /// (membership test, §2.2: a group named in B does not match its members).</summary>
    public sealed class ISFG : Condition
    {
        int? owner;
        public override void Init() { var o = P["B_obj_owner"]; owner = string.IsNullOrEmpty(o) || TrigUtil.ToInt(o) < -1 ? null : (int)TrigUtil.ToInt(o); }
        public override void OnEnabled() => E.Sub(Channel.Attacked, this);
        public override void OnDisabled() => E.Unsub(Channel.Attacked, this);
        public override void OnAttacked(int victim, int attacker)
        {
            if (victim < 0 || attacker < 0) return;
            if (owner != null) { var v = H.Info(victim); if (v == null || !H.OwnerMatches(v.Owner, owner.Value)) return; }
            bool s = H.Matches(attacker, P, "") && H.Matches(victim, P, "B_");
            if (s || (s ? 1 : 0) != State) Set(s);
        }
    }

    /// <summary>CF:3328-3395: an object of the query reached the end of its waypoint path (WYPT mode 0). A pulse.</summary>
    public sealed class WAYR : Condition
    {
        public override void OnEnabled() { E.Sub(Channel.Wayr, this); State = 0; }
        public override void OnDisabled() => E.Unsub(Channel.Wayr, this);
        public override void OnWaypoint(int obj) { if (obj < 0 || !Sel().Contains(obj)) return; State = 1; Inv(); State = 0; }
    }

    /// <summary>CF:3399-3508: member count of a group, on every change of the group and on enable.</summary>
    public sealed class CKGR : Condition
    {
        string val = "";
        public override void Init() { val = P.S("check_val"); }
        int Group() { int g = H.FindGroup(P["group_guid"]); return g >= 0 ? g : H.FindGroup(P["group_name"]); }
        public override void OnEnabled() { E.Sub(Channel.Group, this); if (val.Contains("$(")) E.Sub(Channel.Vars, this); Check(); }
        public override void OnDisabled() { E.Unsub(Channel.Group, this); E.Unsub(Channel.Vars, this); }
        public override void OnGroup(int g) { if (g == Group()) Check(); }
        public override void OnVars() => Check();
        void Check() { int g = Group(); if (g < 0) return; Set(TrigUtil.Compare(H.GroupMembers(g).Count, val, Vars)); }
        public override string Info() { int g = Group(); return g < 0 ? "no group" : $"size {H.GroupMembers(g).Count}"; }
    }

    /// <summary>MIRAGE (ConditionFactory.usl CConditionGroupRegion): the members of the group that the query also
    /// finds, counted against obj_count; re-checked when the group or the objects change and twice a second.</summary>
    public sealed class GREG : Condition
    {
        string val = "";
        public override void Init() { val = P.S("obj_count"); }
        public override void OnEnabled() { E.Sub(Channel.Group, this); E.Sub(Channel.Objects, this); if (val.Contains("$(")) E.Sub(Channel.Vars, this); StartPoll(0.5, 0.5); Check(); }
        public override void OnDisabled() { E.Unsub(Channel.Group, this); E.Unsub(Channel.Objects, this); E.Unsub(Channel.Vars, this); StopPoll(); }
        public override void OnGroup(int g) => Check();
        public override void OnObjects() => Check();
        public override void OnVars() => Check();
        public override void Poll() => Check();
        void Check()
        {
            int g = H.FindGroup(P["group"]);
            if (g < 0) return;
            var found = new HashSet<int>(Sel());
            int n = H.GroupMembers(g).Count(found.Contains);
            Change(TrigUtil.Compare(n, val, Vars));
        }
    }

    /// <summary>UniversalTutorialCondition.usl: the player did something in the interface. CAME camera moved (scroll /
    /// zoom / pan), TRMO a unit of B boarded a transporter of A, USEL a unit of the query is selected. Pulses.</summary>
    public sealed class UNTT : Condition
    {
        string kind = ""; (double X, double Y, double Dist, double Yaw)? cam;
        public override void OnEnabled()
        {
            kind = P.S("condition_type").ToUpperInvariant();
            State = 0;
            if (kind == "TRMO") { E.Sub(Channel.Boarded, this); if (MissingClass()) Pulse(); }
            else if (kind == "CAME" || kind == "USEL") { cam = null; StartPoll(0.3, 0.3); }
            else E.Unknown("condition", "UNTT " + kind);
        }
        public override void OnDisabled() { E.Unsub(Channel.Boarded, this); StopPoll(); }
        void Pulse() { State = 1; Inv(); }
        public override void OnBoarded(int u, int t) { if (u < 0 || t < 0) return; if (Sel("").Contains(t) && Sel("B_").Contains(u)) Pulse(); }
        public override void Poll()
        {
            if (kind == "USEL") { foreach (var o in Sel()) if (H.IsSelected(o)) { Pulse(); return; } return; }
            var now = H.Camera;
            if (now == null) return;
            var was = cam; cam = now;
            if (was == null) return;
            var n = now.Value; var w = was.Value;
            var ev = P.S("camera_event").ToLowerInvariant();
            bool moved = Math.Sqrt((n.X - w.X) * (n.X - w.X) + (n.Y - w.Y) * (n.Y - w.Y)) > 0.5, zoomed = Math.Abs(n.Dist - w.Dist) > 0.5, turned = Math.Abs(n.Yaw - w.Yaw) > 0.02;
            bool hit = ev.Contains("zoom") ? zoomed : Regex.IsMatch(ev, "pan|rot") ? turned : Regex.IsMatch(ev, "scroll|move") ? moved : moved || zoomed || turned;
            if (hit) Pulse();
        }
    }

    // ------------------------------------------------------------------------------------------------ 4.3 players, level state
    /// <summary>CF:2455-2634: "==" compares the strings, everything else Compare(int(variable), operation + value).
    /// local = 1: a level variable; else a variable of the player's profile (only the defined ones exist).</summary>
    public sealed class CVAR : Condition
    {
        public override void OnEnabled() { E.Sub(Channel.Vars, this); Check(); }
        public override void OnDisabled() => E.Unsub(Channel.Vars, this);
        public override void OnVars() => Check();
        void Check()
        {
            var name = P.S("varname");
            string? v = P.S("local") == "1" ? E.GetVarOrNull(name) : E.Profile.GetValueOrDefault(name);
            if (v == null) return;                                   // an unknown variable leaves the state alone
            var chk = P.S("value");
            if (chk.StartsWith('$')) chk = TrigUtil.ValueString(chk, Vars);
            var op = P.S("operation");
            Set(op == "==" ? v == chk : TrigUtil.Compare(TrigUtil.ToInt(v), op + chk, Vars));
        }
        public override string Info() => "value " + (E.GetVarOrNull(P.S("varname")) ?? "-");
    }

    /// <summary>CF:2193-2296: 0 visible, 1 accomplished, 2 unaccomplishable, 3 none of them; once true it stops
    /// listening.</summary>
    public sealed class QUES : Condition
    {
        public override void OnEnabled() { E.Sub(Channel.Quest, this); Check(); }
        public override void OnDisabled() => E.Unsub(Channel.Quest, this);
        public override void OnQuest(QuestState q) => Check();
        void Check()
        {
            var q = E.Quest(P);
            long d = P.I("dest_state");
            if (q == null) { Set(false); return; }
            bool ok = d switch { 0 => q.Visible, 1 => q.Accomplished, 2 => q.Unaccomplishable, 3 => !q.Accomplished && !q.Unaccomplishable && !q.Visible, _ => false };
            if (ok) E.Unsub(Channel.Quest, this);
            Set(ok);
        }
    }

    /// <summary>CF:1039-1159: a player attribute against a count expression (polled).</summary>
    public sealed class PLYR : Condition
    {
        string val = "";
        public override void Init() { val = P.S("attrib_value"); }
        public override void OnEnabled() { if (val.Contains("$(")) E.Sub(Channel.Vars, this); StartPoll(0.5, 0); }
        public override void OnDisabled() { E.Unsub(Channel.Vars, this); StopPoll(); }
        public override void OnVars() => Poll();
        public override void Poll()
        {
            int slot = PlayerSlot();
            if (!H.PlayerExists(slot)) return;
            var v = H.PlayerAttribute(slot, P.S("attrib_name").ToLowerInvariant());
            if (v == null) return;
            Change(TrigUtil.Compare((long)Math.Floor(v.Value), val, Vars));
        }
    }

    /// <summary>CF:330-405: the player's tech tree has the filter. Not checked on enable but on the player's next push
    /// (polled here).</summary>
    public sealed class TECH : Condition
    {
        public override void OnEnabled() => StartPoll(0.5, 0.1);
        public override void OnDisabled() => StopPoll();
        public override void Poll()
        {
            var has = H.HasTechFilter(PlayerSlot("player"), P.S("filter"));
            if (has == null) return;
            Change(has.Value);
        }
    }

    /// <summary>CF:1701-2026: "the player is defeated": when it loses an object and has nothing left that counts in the
    /// unit limit (check_pyramid) and - check_producer: nothing that can produce units / check_bldgs: no building
    /// except walls, gates, traps / check_none: nothing more to check.</summary>
    public sealed class PLDE : Condition
    {
        public override void OnEnabled() => E.Sub(Channel.Lost, this);
        public override void OnDisabled() => E.Unsub(Channel.Lost, this);
        bool Flag(string k, string d) => (P[k] ?? d) == "1";
        public override void OnLost(int obj, int owner)
        {
            int slot = PlayerSlot();
            if (!H.PlayerExists(slot) || !H.OwnerMatches(owner, slot)) return;
            var mine = H.PlayerObjects(slot).Where(o => o != obj).Select(H.Info).Where(o => o != null && o.Alive).ToList();
            var units = mine.Where(o => o!.IsUnit && !o.Parked).ToList();
            var blds = mine.Where(o => o!.IsBuilding).ToList();
            if (Flag("check_pyramid", "1") && units.Any(u => u!.CountsInPop)) return;
            if (Flag("check_producer", "1"))
            {
                if (blds.Any(b => b!.Built && b.Produces) || units.Any(u => u!.Produces)) return;
            }
            else if (Flag("check_bldgs", "0"))
            {
                if (blds.Any(b => !b!.WallLike)) return;
            }
            Set(true);
        }
    }

    /// <summary>CF:2637-2711: relation of plyr1 towards plyr2 (0 hostile, 1 neutral, 2 friendly).</summary>
    public sealed class DIPL : Condition
    {
        public override void OnEnabled() { E.Sub(Channel.Diplomacy, this); Check(); }
        public override void OnDisabled() => E.Unsub(Channel.Diplomacy, this);
        public override void OnDiplomacy() => Check();
        void Check()
        {
            var r = H.Relation((int)P.I("plyr1"), (int)P.I("plyr2"));
            if (r == null) return;
            Set(r.Value == P.I("relation"));
        }
    }

    /// <summary>CF:2301-2380: subscribed for the whole level; true from the end of that sequence until the end of
    /// another one; enabling the trigger resets it.</summary>
    public sealed class SQEN : Condition
    {
        string file = "";
        public override void Init() { file = TrigUtil.FilePart(P.S("sequence_name")); E.Forever(Channel.Sequence, this); }
        // CF CConditionSequenceEnd: enabling resets the state - but the original pushes the end of a sequence through its
        // subscriber list while the triggers it fires run, and enabling re-subscribes a condition at the end of that list:
        // a condition enabled by a trigger that this very end fired still gets the push. Mission 16 depends on it: the
        // end of sc_7052 fires "01_L16MQ03_vis", which enables "01_activate_skorpion" (SQEN sc_7052 -> JumpOffWall).
        // So: true when enabled while that end is being handled.
        public override void OnEnabled() { var s = E.SeqEnd; Set(s != null && s.Value.File == file && s.Value.Round == E.FlushRound); }
        public override void OnSequence(string f) => Set(f == file);
    }

    /// <summary>CF:2382-2449: 1 when the dialogue scene has ended, for the rest of the level.</summary>
    public sealed class DSEN : Condition
    {
        string file = "";
        public override void Init() { file = TrigUtil.FilePart(P.S("dlgscene_name")); E.Forever(Channel.Dialog, this); }
        public override void OnDialog(string f) { if (f == file) Set(true); }
    }

    // ------------------------------------------------------------------------------------------------ Lua
    /// <summary>Check(function(api) return bool end): a condition written in Lua (not in the original). A level
    /// condition, evaluated when the trigger is enabled and then every `Every` seconds (default 0.5); its trigger
    /// re-evaluates when the result changes. An error in the function counts as false (logged).</summary>
    public sealed class CHCK : Condition
    {
        public override void OnEnabled() { State = 0; Set(Call()); StartPoll(Node.Every > 0 ? Node.Every : 0.5); }
        public override void OnDisabled() => StopPoll();
        public override void Poll() => Change(Call());
        bool Call()
        {
            if (Node.Lua == null || Node.Function == null) return false;
            return Node.Lua.CallCheck(Node.Function, $"Check in \"{T.Name}\"");
        }
    }

    /// <summary>The remake's own: the map's day-night cycle (map.lua day_night, World/DayNight.cs) is in a phase -
    /// `phase` day / dusk / night / dawn, and `night` (optional, a count expression: `>=3`, `==1`) the night number
    /// (how many dusks have begun). A trigger without `once` fires each time the phase begins. False on a map without
    /// a cycle.</summary>
    public sealed class DAYN : Condition
    {
        PwRemake.Sim.World.DayPhase? phase; string night = "";
        public override void Init() { phase = PwRemake.Sim.World.DayNightState.Parse(P.S("phase")); night = P.S("night"); }
        public override void OnEnabled() { State = 0; Check(); StartPoll(0.25, 0); }
        public override void OnDisabled() => StopPoll();
        public override void Poll() => Check();
        void Check()
        {
            if (H.DayNight is not { } s) { Change(false); return; }
            bool ok = (phase == null || s.Phase == phase) && (night.Length == 0 || TrigUtil.Compare(s.NightNo, night, Vars));
            Change(ok);
        }
    }
}
