// The action types of the campaign triggers (remake/docs/spec/triggers.md §5; original: Server/misc/
// ActionFactory.usl = "AF"). An action is created from its parameters (the editor's defaults applied) when the
// mission loads and run by the engine when its trigger fires. Actions work on the world through IMissionHost (map
// coordinates, player slots, object ids). An action that finds nothing to work on does nothing; an exception is
// caught by the engine and recorded in TriggerEngine.Errors.
//
// Every type is one class, named by its 4-letter code; the readable Lua names are in TriggerTypes.cs.
// Ported from remake/src/game/campaign/actions.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;

namespace PwRemake.Sim.Scripting;

/// <summary>Base of every action type.</summary>
public abstract class TriggerAction
{
    public string Type = "";
    public TriggerParams P = null!;
    /// <summary>0 easy, 1 medium, 2 hard (§1.4).</summary>
    public int Difficulty = 1;
    public NodeDef Node = null!;
    internal void Setup(NodeDef n, TriggerParams p) { Node = n; Type = n.Code; P = p; Difficulty = n.Difficulty; }
    public abstract void Run(TriggerEngine E, Trigger t);

    // ---- helpers of actions.js
    /// <summary>The objects of the action's query (§2); from_condition = i: the result of condition i of the same
    /// trigger (REGN, §2.5).</summary>
    protected List<int> Objs(TriggerEngine E, Trigger? t, string prefix = "")
    {
        long fc = P[prefix + "from_condition"] is { } f ? TrigUtil.ToInt(f) : -1;
        if (fc >= 0 && t != null && fc < t.Conds.Count && t.Conds[(int)fc] is Conditions.REGN r)
            return r.Result.Where(o => E.Host.Info(o) is { Alive: true }).ToList();
        return E.Host.Select(P, prefix).ToList();
    }
    /// <summary>Live world entities (units, buildings) among the objects.</summary>
    protected static List<(int Id, ObjectInfo I)> Ents(TriggerEngine E, IEnumerable<int> list) =>
        list.Select(o => (o, E.Host.Info(o))).Where(x => x.Item2 is { HasEntity: true, Alive: true }).Select(x => (x.o, x.Item2!)).ToList();
    protected static List<(int Id, ObjectInfo I)> Units(TriggerEngine E, IEnumerable<int> list) => Ents(E, list).Where(x => x.I.IsUnit).ToList();
    protected static readonly int[] StdPyramid = { 25, 15, 8, 3, 1 };
}

/// <summary>An unknown type: does nothing (one warning).</summary>
public sealed class UnknownAction : TriggerAction { public override void Run(TriggerEngine E, Trigger t) => E.Unknown("action", Type); }

public static class Actions
{
    // ------------------------------------------------------------------------------------------- 5.1 flow
    /// <summary>AF:2203-2253: 1 enable, 0 disable, 2 toggle; a trigger that does not exist (not compiled): nothing.</summary>
    public sealed class TRIG : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            // (Lua maps may name the trigger instead: trigger = "name"; state "on" / "off" / "toggle")
            var x = E.ByGuid(P["guid"]) ?? (P["guid"] == null && P["trigger"] is { } nm ? E.Find(nm) : null);
            if (x == null) return;
            long s = P.S("state") switch { "on" => 1, "off" => 0, "toggle" => 2, var v => TrigUtil.ToInt(v) };
            if (s == 1 || (s == 2 && !x.Enabled)) E.Enable(x); else E.Disable(x);
        }
    }
    /// <summary>AF:2869-2914: a folder on / off (§1.5).</summary>
    public sealed class ACND : TriggerAction { public override void Run(TriggerEngine E, Trigger t) => E.SetNode(P["nodename"], P.I("deststate") == 1); }
    /// <summary>AF:518-557: the mission is won.</summary>
    public sealed class QUIT : TriggerAction { public override void Run(TriggerEngine E, Trigger t) => E.Win(); }
    /// <summary>AF:2466-2539: the mission is lost (4 s later, with the reason text).</summary>
    public sealed class GAOV : TriggerAction { public override void Run(TriggerEngine E, Trigger t) => E.Lose(P["reason"], t); }
    /// <summary>AF:562-608: points to BoniTotal.</summary>
    public sealed class BONI : TriggerAction { public override void Run(TriggerEngine E, Trigger t) => E.Boni += P.I("points"); }
    /// <summary>Debug texts of the designers: only shown with Server/Trigger/EnableFDBK (AF:2168-2171): logged at debug level.</summary>
    public sealed class FDBK : TriggerAction
    {
        static readonly PwRemake.Sim.Core.Logger L = PwRemake.Sim.Core.Log.For("Campaign.Feedback");
        public override void Run(TriggerEngine E, Trigger t) => L.Debug(() => $"{t.Name}: {P.S("msg_text")}");
    }
    /// <summary>AF:1976-2095: queue a sequence (§6.8).</summary>
    public sealed class SQNZ : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t) => E.PlaySequence(new SequenceRequest
        {
            Path = P.S("sequence"), Quit = P.On("quit"), Reason = P.S("reason"), SnapBack = P.On("snap_cam_back"), SnapActors = P.On("snap_actors_back"),
            Camera = TrigUtil.Vec(P["camera_data"]), Fow = P.On("disable_fow"), FowPos = TrigUtil.Vec(P["fow_pos"]), FowRadius = P.N("fow_radius", 30), Trigger = t.Guid.Length > 0 ? t.Guid : t.Name,
        });
    }
    /// <summary>AF:2098-2139: a dialogue scene (§6.5).</summary>
    public sealed class DGSC : TriggerAction { public override void Run(TriggerEngine E, Trigger t) => E.PlayDialog(P.S("scene"), t); }

    // ------------------------------------------------------------------------------------------- 5.2 quests, markers, UI
    /// <summary>AF:2544-2642: 0 show, 1 accomplished (+ bonus), 2 failed, 3 hide.</summary>
    public sealed class QUES : TriggerAction { public override void Run(TriggerEngine E, Trigger t) => E.SetQuest(E.Quest(P), P.I("dest_state")); }
    /// <summary>AF:5894-5949: state and tooltip of a question mark.</summary>
    public sealed class QMRK : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var tip = P["questiontooltip"];
            E.Host.SetQuestionMark(P.S("questionmark"), P.SE("questionstate", "STATE_INVISIBLE"), string.IsNullOrEmpty(tip) ? null : E.Text(tip));
        }
    }
    /// <summary>AF:2919-3062: a marker at `pos` (pos_objquery_flag = 1 and pos not zero) or one per object of the
    /// query; only markers for the human player are shown.</summary>
    public sealed class MPNG : TriggerAction
    {
        static readonly Dictionary<string, int> Colors = new() { ["SPMainQuest"] = 0xffd23c, ["SPOptQuest"] = 0x4cc3ff, ["SPHint"] = 0x6ee06e, ["Attack"] = 0xff3a2a };
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            var id = P.S("id");
            if (P.On("add_remove")) { E.Markers.Remove(id); H.RemoveMarker(id); return; }
            var owner = P.S("owner", "All");
            int human = H.HumanSlot;
            var kind = P.SE("colortype", "FixedColor");
            var rgb = P.SE("fixedcolor", "255:0:0").Split(':').Select(x => (int)(TrigUtil.ToInt(x) & 255)).ToArray();
            int color = Colors.TryGetValue(kind, out var c) ? c : (rgb[0] << 16) | ((rgb.Length > 1 ? rgb[1] : 0) << 8) | (rgb.Length > 2 ? rgb[2] : 0);
            MapMarker Make(double x, double y, int obj) => new()
            {
                Id = id, Color = color, Kind = kind, Extended = P.On("extended"), Ttl = P.N("time_to_life", 5000) / 1000, Repeats = (int)P.I("num_repeats"),
                Interval = P.N("ms_between", 500) / 1000, X = x, Y = y, Obj = obj,
            };
            void Add(MapMarker m) { E.Markers[id] = E.Markers.GetValueOrDefault(id) + 1; H.ShowMarker(m); }
            var v = TrigUtil.Vec(P["pos"]);
            if (P.On("pos_objquery_flag") && v is { IsZero: false } pv)
            {
                if (owner == "All" || owner == "trigger obj" || TrigUtil.ToInt(owner) == human) Add(Make(pv.X, pv.Y, -1));
                return;
            }
            foreach (var o in Objs(E, t))
            {
                var i = H.Info(o);
                if (i == null) continue;
                if (owner == "trigger obj" ? !H.OwnerMatches(i.Owner, human) : owner != "All" && TrigUtil.ToInt(owner) != human) continue;
                Add(Make(i.X, i.Y, i.HasEntity ? o : -1));
            }
        }
    }
    /// <summary>AF:5351-5419: the info bar line (§6.4).</summary>
    public sealed class INBA : TriggerAction { public override void Run(TriggerEngine E, Trigger t) => E.SetInfoBar(P["text"]); }
    /// <summary>AF:6006-6106: level timers create / pause / unpause / kill (§6.3).</summary>
    public sealed class TIMR : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var tip = P["tooltip"];
            E.Timer(P.SE("timer_id", "1"), P.S("event", "create"), TrigUtil.Num(TrigUtil.ValueString(P["duration"], E.VarLookup), 0), P.On("show", true), string.IsNullOrEmpty(tip) ? "" : E.Text(tip));
        }
    }
    /// <summary>AF:4219-4274: a sound event (player -1 = everybody; only the human player's sounds are played).</summary>
    public sealed class PSND : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            int pl = P.Slot("player", 0);
            if (!(pl == -1 || E.Host.IsHumanSide(pl))) return;
            var v = TrigUtil.Vec(P["position"]);
            E.Host.PlaySound(P.S("soundname"), v is { IsZero: false } ? v : null);
        }
    }
    /// <summary>CActionMusic: a track for every player, once or looped.</summary>
    public sealed class MUSI : TriggerAction { public override void Run(TriggerEngine E, Trigger t) { var f = P["musicfile"]; if (!string.IsNullOrEmpty(f)) E.Host.PlayMusic(f, P.I("loop") == 1); } }
    /// <summary>CActionWeather: chgtype != 0 = the next track of the running weather program; a program file (0) is not
    /// supported (no mission uses it).</summary>
    public sealed class WTHR : TriggerAction { public override void Run(TriggerEngine E, Trigger t) { if (P.I("chgtype") != 0) E.Host.NextWeather(); else E.Unknown("action", "WTHR program " + P.S("progpath")); } }

    // ------------------------------------------------------------------------------------------- 5.3 objects
    /// <summary>AF:1112-1692: every actor of the query does `action`; additional_params split at '|'.</summary>
    public sealed class ACDO : TriggerAction
    {
        public static readonly string[] Known = { "WalkAction", "Aggressive Walk", "Attack", "SetPos", "RotateTo", "SetAnim", "Invulnerability", "WeaponRemove", "LevelUp", "BuildDown", "entrench", "PrepareUnboard", "AbortTask", "Stop", "Kill", "FullHeal", "Open Gate", "Close Gate", "Auto Gate", "SetRallyPoint", "GiveItem", "BuildUp", "JumpOffWall" };
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            var act = P.S("action").Trim();
            if (Array.IndexOf(Known, act) < 0) { E.Unknown("action", "ACDO " + act); return; }
            var prm = P.S("additional_params").Split('|').Select(s => s.Trim()).ToArray();
            var actors = Objs(E, t);
            // Invulnerability by region: the campaign protects "everything in the fight area" until its scene starts and
            // lifts it with the same region query. The remake's wild animals roam further than the original's (which keep
            // to their nest's area), so one that walked out in between would stay invulnerable for good - and "kill all
            // Smilodons" could never be fulfilled. A Disable therefore also reaches what the same region's Enable protected.
            var rg = P["rgn_guid"];
            if (act == "Invulnerability" && !H.IsWorldRegion(rg))
            {
                bool on = System.Text.RegularExpressions.Regex.IsMatch(prm[0], "^(enable|timer)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (on) { foreach (var o in actors) E.InvulnRegion[o] = rg!; }
                else
                {
                    var seen = new HashSet<int>(actors);
                    var q = P.With("rgn_guid", "UniqueWorldRegion");
                    foreach (var (o, key) in E.InvulnRegion.ToArray())
                        if (key == rg && !seen.Contains(o) && H.Info(o) is { Alive: true } && H.Matches(o, q, "")) actors.Add(o);
                    foreach (var o in actors) E.InvulnRegion.Remove(o);
                }
            }
            bool needTargets = act == "Attack" || act == "GiveItem" || act == "BuildUp";
            var targets = needTargets ? Objs(E, t, "dst_") : new List<int>();
            foreach (var o in actors) E.Guard(t, "ACDO " + act, () => Do(E, act, o, prm, targets));
        }

        static void Do(TriggerEngine E, string act, int obj, string[] prm, List<int> targets)
        {
            var H = E.Host;
            var i = H.Info(obj);
            var u = i is { HasEntity: true, Alive: true } ? i : null;           // the world entity, if any
            bool unit = u != null && u.IsUnit;
            switch (act)
            {
                // "[x y z] | speed": a plain walk; a unit inside a transporter gets out first (AF:1183-1192, 1424-1426)
                case "WalkAction": { var v = TrigUtil.Vec(prm[0]); if (v == null || prm.Length < 2 || !unit) return; H.OrderMove(obj, v.Value, TrigUtil.Num(prm[1], 2), false); return; }
                // attack-move to the point (AF:1196-1197); the number and the dst query are not used
                case "Aggressive Walk": { var v = TrigUtil.Vec(prm[0]); if (v == null || !unit) return; H.OrderMove(obj, v.Value, 4, true); return; }
                // the target of the dst query nearest to the actor (AF:1440-1450)
                case "Attack":
                {
                    if (!unit) return;
                    int best = -1; double bd = double.PositiveInfinity;
                    foreach (var r in targets)
                    {
                        var e = H.Info(r);
                        if (e is not { HasEntity: true, Alive: true } || r == obj || e.Parked) continue;
                        double d = Math.Sqrt((e.X - u!.X) * (e.X - u.X) + (e.Y - u.Y) * (e.Y - u.Y));
                        if (d < bd) { bd = d; best = r; }
                    }
                    if (best >= 0) H.OrderAttack(obj, best);
                    return;
                }
                // teleport (AF:1207-1217)
                case "SetPos": { var v = TrigUtil.Vec(prm[0]); if (v == null || i == null) return; H.Teleport(obj, v.Value.X, v.Value.Y); E.Touch(); return; }
                // break the task and face the point (AF:1198-1206)
                case "RotateTo": { var v = TrigUtil.Vec(prm[0]); if (v == null || !unit) return; H.FaceTo(obj, v.Value.X, v.Value.Y); return; }
                // "name | loops" (AF:1452-1472): gates of the scenery (open / close), poses
                case "SetAnim": { var name = prm[0]; if (name.Length == 0 || i == null) return; H.PlayAnim(obj, name, (int)Math.Max(1, TrigUtil.ToInt(prm.Length > 1 ? prm[1] : ""))); return; }
                // "Enable" | "Disable" | "Timer: seconds" (AF:1496-1514)
                case "Invulnerability":
                case "WeaponRemove":
                {
                    if (u == null) return;
                    var kv = prm[0].Split(':').Select(s => s.Trim()).ToArray();
                    bool timer = kv[0].StartsWith("timer", StringComparison.OrdinalIgnoreCase);
                    bool on = timer || kv[0].StartsWith("enable", StringComparison.OrdinalIgnoreCase);
                    double sec = timer ? TrigUtil.Num(kv.Length > 1 ? kv[1] : "", 0) : 0;
                    // MIRAGE WeaponRemove (ActionFactory DoWeaponRemove): "Enable" disarms - a tower stops shooting
                    if (act == "Invulnerability") H.SetInvulnerable(obj, on, sec); else H.SetDisarmed(obj, on, sec);
                    return;
                }
                // MIRAGE: LevelUp (free), BuildDown (an Aje building is taken down), entrench (dig in), JumpOffWall: the
                // Scorpion of mission 16 wakes up (Building.usl CScorpionBoss "/JumpOffWall")
                case "LevelUp": if (unit && u!.Level < 5) H.SetLevel(obj, u.Level + 1); return;
                case "BuildDown": if (u != null && u.IsBuilding) H.Command(obj, "BuildDown"); return;
                case "entrench": if (unit) H.Command(obj, "entrench"); return;
                case "JumpOffWall": if (u != null) H.Command(obj, "JumpOffWall"); return;
                // PrepareUnboard "[x y z]": a transporter sails to the point and lets its passengers off there
                case "PrepareUnboard": { var v = TrigUtil.Vec(prm[0]); if (!unit || v == null) return; H.PrepareUnboard(obj, v.Value); return; }
                case "AbortTask": case "Stop": if (unit) H.OrderStop(obj); return;
                // a normal death (AF:1474-1480); scenery is just removed
                case "Kill": if (i != null) H.Kill(obj); return;
                case "FullHeal": if (u != null) H.SetHitpoints(obj, u.MaxHp); return;
                case "Open Gate": if (i != null) H.SetGate(obj, 0); return;
                case "Close Gate": if (i != null) H.SetGate(obj, 1); return;
                case "Auto Gate": if (i != null) H.SetGate(obj, 2); return;
                case "SetRallyPoint": { var v = TrigUtil.Vec(prm[0]); if (u != null && v is { IsZero: false } p) H.SetRallyPoint(obj, p.X, p.Y); return; }
                // the actor receives the first object of the dst query as an item; the next actor gets the next (AF:1390-1395;
                // FightingObj.AddItem: one that carries an item already gets none)
                case "GiveItem":
                {
                    int k = targets.FindIndex(r => H.Info(r) is { IsItem: true });
                    if (k < 0) return;
                    int it = targets[k]; targets.RemoveAt(k);
                    if (!unit || u!.CarriedItem != null) return;
                    H.GiveItem(obj, it);
                    return;
                }
                // a worker is sent to build / finish the first building of the dst query
                case "BuildUp":
                {
                    var b = Ents(E, targets).FirstOrDefault(x => x.I.IsBuilding);
                    if (unit && b.I != null) H.OrderBuild(obj, b.Id);
                    return;
                }
            }
        }
    }

    /// <summary>AF:1769-1970: every object walks the points (map coordinates "x y z|..."; 0 0 0 entries are skipped).</summary>
    public sealed class WYPT : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var pts = new List<MapPos>();
            foreach (var s in P.S("waypoints").Split('|')) { var v = TrigUtil.Vec(s); if (v is { IsZero: false } p) pts.Add(p); }
            if (pts.Count == 0) return;
            int mode = (int)P.I("patrolmode"); double speed = P.N("walkspeed", 2); bool straight = P.On("straightwalk");
            foreach (var (o, _) in Units(E, Objs(E, t))) E.Host.StartPatrol(o, pts, mode, speed, straight);
        }
    }

    /// <summary>AF:745-896: one object of class obj_name; obj_owner missing = 0 (the editor does not store 0).</summary>
    public sealed class COBJ : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            var v = TrigUtil.Vec(P["obj_pos"]);
            var rot = TrigUtil.Vec(P["obj_rot"]);
            double heading = rot != null ? rot.Value.Z : P.N("obj_rot", 0);
            if (P.On("pos_from_obj"))
            {
                var r = Objs(E, t, "trgt_").Select(H.Info).FirstOrDefault(x => x != null);
                if (r != null) { v = new MapPos(r.X, r.Y, 0); heading = r.Rot; }
            }
            if (v == null) return;
            int slot = P.Slot("obj_owner", 0);
            int level0 = (int)Math.Max(0, P.I("obj_level"));
            if (!P.On("ignore_pyramid", true) && !H.UnitSlotFree(slot, level0 + 1)) return;
            int id = H.Spawn(P.S("obj_name"), slot, v.Value.X, v.Value.Y, new SpawnOptions { Level = level0 + 1, Rot = heading });
            if (id < 0) E.Warn($"{t.Name}: COBJ {P.S("obj_name")} could not be created");
            else if (!string.IsNullOrEmpty(P["buildup"])) H.ApplyBuildup(id, P.S("buildup").Trim());
        }
    }

    /// <summary>AF:661-738: new owner (missing = the human player 0, -1 = nobody); chk_highlight marks what the player
    /// gets for highlight_time s.</summary>
    public sealed class OCPY : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            int slot = P.Slot("new_owner", 0);
            double hl = P.On("chk_highlight") ? Math.Floor(P.N("highlight_time")) : 0;
            E.Host.GiftBatch(true);
            try { foreach (var o in Objs(E, t)) E.Host.SetOwner(o, slot, hl); }
            finally { E.Host.GiftBatch(false); }
            E.Touch();
        }
    }

    /// <summary>AF:227-328: an integer attribute of every object (only hitpoints occurs): "+n", "-n", "=n" or "n".</summary>
    public sealed class UNIT : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var name = P.S("attrib_name").ToLowerInvariant(); var mod = P.S("attrib_mod").Trim();
            if (name != "hitpoints") { E.Unknown("action", "UNIT " + P.S("attrib_name")); return; }
            var s = mod.Length > 0 && (mod[0] == '+' || mod[0] == '=') ? mod[1..] : mod;
            double n = TrigUtil.Num(s, 0);
            foreach (var (o, i) in Ents(E, Objs(E, t))) E.Host.SetHitpoints(o, mod.StartsWith('+') || mod.StartsWith('-') ? i.Hp + n : n);
        }
    }

    /// <summary>AF:4766-5023: spawn units into a group, at `pos` or out of a building (§5.3).</summary>
    public sealed class SPGR : TriggerAction
    {
        public sealed record Entry(string Cls, string? Buildup, int Level, int Count);
        /// <summary>"&lt;class&gt;[§&lt;buildup&gt;] &lt;level&gt; &lt;count&gt;|..." (level 1-based, AF:4814-4818).</summary>
        public static List<Entry> ParseClasses(string? s)
        {
            var o = new List<Entry>();
            foreach (var e in (s ?? "").Split('|'))
            {
                var w = e.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (w.Length == 0) continue;
                var cb = w[0].Split('§');
                o.Add(new Entry(cb[0], cb.Length > 1 ? cb[1] : null, (int)Math.Max(1, TrigUtil.ToInt(w.Length > 1 ? w[1] : "1")), (int)Math.Max(1, w.Length > 2 ? TrigUtil.ToInt(w[2]) : 1)));
            }
            return o;
        }
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            int slot = P.Slot("owner", 0);
            int group = H.FindGroup(P["group"]);
            var v = TrigUtil.Vec(P["pos"]);
            var list = new List<Entry>();
            for (int i = 0; i < 5; i++)
                foreach (var e in ParseClasses(P["classes_" + i]))
                {
                    // checkpyramid: skip a class when the owner has the standard maximum of characters of that level (AF:4838-4842)
                    if (P.On("checkpyramid") && H.PlayerExists(slot) && e.Level <= 5 && H.CountCharacters(slot, e.Level) >= StdPyramid[e.Level - 1]) continue;
                    for (int k = 0; k < e.Count; k++) list.Add(e);
                }
            if (list.Count == 0) return;
            int src = P.On("use_spawn_obj") ? Objs(E, t).FirstOrDefault(-1) : -1;
            if (src < 0)
            {
                if (v == null) return;
                H.GiftBatch(true);
                try
                {
                    foreach (var e in list)
                    {
                        int r = H.Spawn(e.Cls, slot, v.Value.X, v.Value.Y, new SpawnOptions { Level = e.Level, Group = group });
                        if (r < 0) E.Warn($"{t.Name}: {e.Cls} could not be created");
                        else if (e.Buildup != null) H.ApplyBuildup(r, e.Buildup);
                    }
                }
                finally { H.GiftBatch(false); }
                return;
            }
            // out of a building (MiscObj.usl CDelayedArmySpawn 2400-2478): it opens (its "open" clip, if it has one: the first
            // unit comes when the clip is over), then one unit every spawn_delay seconds at its link Spwn; it closes one
            // delay after the last. The host sends them out through Ex_1 to `pos` (also the building's rally point).
            double delay = Math.Max(0, P.N("spawn_delay", 0)), step = Math.Max(delay, 0.05);
            MapPos? target = v is { IsZero: false } ? v : null;
            double open = H.AnimLength(src, "open"), t0 = open > 0 ? open : 0;
            if (t0 > 0) H.PlayAnim(src, "open", 1);
            if (target != null) H.SetRallyPoint(src, target.Value.X, target.Value.Y);
            for (int i = 0; i < list.Count; i++)
                E.After(t0 + i * step, new TriggerEngine.Job { Kind = TriggerEngine.JobKind.SpawnOut, T = t, Src = src, Target = target, Cls = list[i].Cls + (list[i].Buildup != null ? "§" + list[i].Buildup : ""), Level = list[i].Level, Slot = slot, Group = group });
            if (H.AnimLength(src, "close") >= 0) E.After(t0 + list.Count * step, new TriggerEngine.Job { Kind = TriggerEngine.JobKind.CloseSpawner, T = t, Src = src });
        }
        /// <summary>The delayed part: one unit leaves the source.</summary>
        internal static void SpawnOut(TriggerEngine E, TriggerEngine.Job j)
        {
            var cb = j.Cls.Split('§');
            int r = E.Host.SpawnOutOf(j.Src, cb[0], j.Level, j.Slot, j.Group, j.Target);
            if (r < 0) E.Warn($"{j.T?.Name ?? "trigger"}: {cb[0]} could not be created");
            else if (cb.Length > 1) E.Host.ApplyBuildup(r, cb[1]);
        }
    }

    /// <summary>AF:2644-2865: a transporter with passengers "class/level|..." (levels 1-based); obj_owner missing = 0.</summary>
    public sealed class CPLX : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            var v = TrigUtil.Vec(P["obj_pos"]);
            if (v == null) return;
            int slot = P.Slot("obj_owner", 0);
            int lvl = (int)Math.Max(1, TrigUtil.ToInt(P.SE("cptlvl", "1")));
            if (!P.On("ignore_pyramid", true) && !H.UnitSlotFree(slot, lvl)) return;
            int tr = H.Spawn(P.S("obj_name"), slot, v.Value.X, v.Value.Y, new SpawnOptions { Level = lvl });
            if (tr < 0 || H.Info(tr) is not { HasEntity: true }) { E.Warn($"{t.Name}: CPLX {P.S("obj_name")} could not be created"); return; }
            if (!string.IsNullOrEmpty(P["buildup"])) H.ApplyBuildup(tr, P.S("buildup").Trim());
            E.After(0.2, new TriggerEngine.Job { Kind = TriggerEngine.JobKind.LoadPassengers, T = t, Src = tr, Passengers = P.S("passengers"), Slot = slot });
        }
        /// <summary>The passengers are created and board the transporter until it is full.</summary>
        internal static void LoadPassengers(TriggerEngine E, TriggerEngine.Job j)
        {
            var H = E.Host;
            var T = H.Info(j.Src);
            if (T is not { HasEntity: true, Alive: true }) return;
            foreach (var e in j.Passengers.Split('|'))
            {
                var cl = e.Trim().Split('/');
                if (cl[0].Length == 0) continue;
                T = H.Info(j.Src);
                if (T == null || T.Passengers >= T.Capacity) break;
                int r = H.Spawn(cl[0], j.Slot, T.X, T.Y, new SpawnOptions { Level = (int)Math.Max(1, TrigUtil.ToInt(cl.Length > 1 ? cl[1] : "1")), Exact = true, NoScale = true });
                if (r < 0 || H.Info(r) is not { HasEntity: true }) { E.Warn($"{j.T?.Name}: CPLX passenger {cl[0]} could not be created"); continue; }
                if (H.Info(r)!.IsUnit) H.EnterTransport(r, j.Src);
            }
        }
    }

    /// <summary>AF:4334-4406: the FIRST object of the query becomes an object of class new_obj (same place, rotation,
    /// owner, group).</summary>
    public sealed class REPL : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            var o = Objs(E, t).FirstOrDefault(-1);
            var i = o >= 0 ? H.Info(o) : null;
            if (i == null) return;
            int group = H.GroupOf(o);
            H.Remove(o);
            int n = H.Spawn(P.S("new_obj"), i.Owner, i.X, i.Y, new SpawnOptions { Level = (int)Math.Max(0, P.I("obj_level")) + 1, Rot = i.Rot, Exact = !(i.HasEntity && i.IsUnit), Height = i.HasEntity ? null : i.Z, Replace = true });
            if (n < 0) E.Warn($"{t.Name}: REPL {P.S("new_obj")} could not be created");
            else if (group >= 0) H.AddToGroup(group, n);
        }
    }

    /// <summary>AF:4276-4332: deletes the objects of the query; maxobjs &gt;= 0 limits the number (0 deletes nothing).</summary>
    public sealed class DELO : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            long max = string.IsNullOrEmpty(P["maxobjs"]) ? -1 : P.I("maxobjs");
            int n = 0;
            foreach (var o in Objs(E, t)) { if (max >= 0 && n >= max) break; E.Host.Remove(o); n++; }
        }
    }

    /// <summary>AF:5026-5115: the query's objects (selector_enabled) and the listed GUIDs join the group.</summary>
    public sealed class ADGR : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            int g = H.FindGroup(P["group"]);
            if (g < 0) return;
            if (P.On("selector_enabled", true)) foreach (var o in Objs(E, t)) if (H.Info(o)?.Type != "GROU") H.AddToGroup(g, o);
            foreach (var id in TrigUtil.Guids(P["add_to_group_units"])) { int o = H.FindObject(id); if (o >= 0) H.AddToGroup(g, o); }
        }
    }

    /// <summary>MIRAGE (ActionFactory.usl CActionRemoveFromGroup): the objects of the query and the listed ones leave
    /// the group.</summary>
    public sealed class RMGR : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            int g = H.FindGroup(P["group"]);
            if (g < 0) return;
            var list = (P.On("selector_enabled", true) ? Objs(E, t) : new List<int>()).Concat(TrigUtil.Guids(P["remove_from_group_units"]).Select(H.FindObject).Where(o => o >= 0)).ToList();
            foreach (var o in list) if (H.GroupOf(o) == g) H.RemoveFromGroup(o);
        }
    }

    /// <summary>AF:5117-5301: mount = 1: transporters (sub_ query) take the nearest passengers (query); 0: transporters
    /// of the first query unload.</summary>
    public sealed class TRSP : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            var a = P.On("enable_objsel", true) ? Objs(E, t) : new List<int>();
            foreach (var id in TrigUtil.Guids(P["objects_units"])) { int o = H.FindObject(id); if (o >= 0) a.Add(o); }
            if (P.On("mount"))
            {
                var b = P.On("enable_subsel", true) ? Objs(E, t, "sub_") : new List<int>();
                foreach (var id in TrigUtil.Guids(P["subjects_units"])) { int o = H.FindObject(id); if (o >= 0) b.Add(o); }
                var free = Units(E, a).Where(x => x.I.Inside < 0).Select(x => x.Id).Distinct().ToList();
                foreach (var (T, ti) in Ents(E, b))
                {
                    int room = ti.Capacity - ti.Passengers;
                    if (room <= 0) continue;
                    free.Remove(T);
                    var near = free.Where(u => H.CanBoard(u, T)).Select(u => (u, H.Info(u)!)).OrderBy(x => Math.Sqrt((x.Item2.X - ti.X) * (x.Item2.X - ti.X) + (x.Item2.Y - ti.Y) * (x.Item2.Y - ti.Y))).ToList();
                    foreach (var (u, _) in near) { if (room-- <= 0) break; free.Remove(u); H.OrderBoard(u, T); }
                }
                return;
            }
            foreach (var (T, ti) in Ents(E, a)) if (ti.Passengers > 0) { H.UnloadAll(T); E.Touch(); }
        }
    }

    /// <summary>AF:6108-6182: an effect at pos, or at the first object of the query.</summary>
    public sealed class EFCT : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            MapPos? at = null;
            if (P.On("use_objquery")) { var r = Objs(E, t).Select(E.Host.Info).FirstOrDefault(x => x != null); if (r != null) at = new MapPos(r.X, r.Y, 0); }
            at ??= TrigUtil.Vec(P["pos"]);
            if (at != null) E.Host.PlayEffect(P.S("effect_path"), at.Value.X, at.Value.Y, (int)P.I("loop_value"));
        }
    }

    /// <summary>AF:2258-2337: all appearance bits at once.</summary>
    public sealed class OBAP : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t) { int f = (int)P.I("flags"); foreach (var o in Objs(E, t)) E.Host.SetAppearance(o, f); E.Touch(); }
    }

    // ------------------------------------------------------------------------------------------- 5.4 players, world
    /// <summary>AF:3955-4114; local = 1: a level variable, else one of the player's profile.</summary>
    public sealed class VARS : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var name = P["varname"];
            if (string.IsNullOrEmpty(name)) return;
            if (P.S("local") == "1") E.SetVar(name, P.SE("operation", "set"), P.S("value"));
            else E.SetProfileVar(name, P.SE("operation", "set"), P.S("value"));
        }
    }
    /// <summary>AF:334-511: the list "name|mod|cap:..." wins over the single parameters.</summary>
    public sealed class RSRC : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            int pl = P.Slot("player_id", 0);
            if (!E.Host.PlayerExists(pl)) return;
            var list = P.S("res_rsrclist").Split(':').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (list.Count > 0)
                foreach (var e in list) { var x = e.Split('|'); if (x[0].Length > 0 && x.Length > 1) E.Host.ChangeResource(pl, x[0], x[1], TrigUtil.On(x.Length > 2 ? x[2] : null)); }
            else if (!string.IsNullOrEmpty(P["res_name"])) E.Host.ChangeResource(pl, P.S("res_name"), P.S("res_mod", "0"), P.On("res_cap"));
        }
    }
    /// <summary>AF:2343-2461: lines "player|1 enable / 0 disable|path".</summary>
    public sealed class TECH : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            E.Host.TechBatch(true);
            try
            {
                foreach (var l in TrigUtil.Lines(P["filters"]))
                {
                    var x = l.Split('|');
                    int pl = (int)TrigUtil.ToInt(x[0]);
                    if (x.Length < 3 || x[2].Trim().Length == 0 || !E.Host.PlayerExists(pl)) continue;
                    E.Host.SetTechFilter(pl, x[2].Trim(), TrigUtil.ToInt(x[1]) == 1);
                }
            }
            finally { E.Host.TechBatch(false); }
        }
    }
    /// <summary>AF:3246-3334: lines "plyr1|relation|plyr2", one direction each (0 hostile, 1 neutral, 2 friendly).</summary>
    public sealed class DIPL : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            foreach (var l in TrigUtil.Lines(P["changes"]))
            {
                var x = l.Split('|').Select(s => (int)TrigUtil.ToInt(s)).ToArray();
                if (x.Length < 3) continue;
                if (E.Host.PlayerExists(x[0]) && E.Host.PlayerExists(x[2]) && x[0] != x[2]) E.Host.SetRelation(x[0], x[2], x[1]);
            }
            // the world reports the change with its events; conditions should see it within this step too
            E.OnDiplomacyChanged();
        }
    }
    /// <summary>AF:3069-3137: the population limit.</summary>
    public sealed class POPL : TriggerAction { public override void Run(TriggerEngine E, Trigger t) { int s = P.Slot("player_id", 0); if (E.Host.PlayerExists(s)) E.Host.SetPopulationLimit(s, (int)P.I("limit")); } }
    /// <summary>AF:3143-3241: unit slots per level; a missing open_i = 0.</summary>
    public sealed class BLSL : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t) { int s = P.Slot("player_id", 0); if (E.Host.PlayerExists(s)) E.Host.SetUnitLimits(s, Enumerable.Range(1, 5).Select(i => (int)TrigUtil.ToInt(P.SE("open_" + i, "0"))).ToArray()); }
    }
    /// <summary>AF:5595-5650: storage limits.</summary>
    public sealed class PLCP : TriggerAction { public override void Run(TriggerEngine E, Trigger t) { int s = P.Slot("player", 0); if (E.Host.PlayerExists(s)) E.Host.SetResourceCaps(s, P.S("food"), P.S("wood"), P.S("stone")); } }
    /// <summary>AF:5520-5592: 1 / 3 nest animals ignore the player, 2 / 3 its units ignore wild animals.</summary>
    public sealed class SNFA : TriggerAction { public override void Run(TriggerEngine E, Trigger t) { int s = P.Slot("player", 0); if (E.Host.PlayerExists(s)) E.Host.SetNeutralToAnimals(s, (int)(P.I("neutral") & 3)); } }
    /// <summary>AF:5652-5706: a region or one of its shapes on / off.</summary>
    public sealed class ARGN : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t) { if (E.Host.SetRegionEnabled(P.S("rgn_guid"), P.I("dest_state") == 1, string.IsNullOrEmpty(P["sub_idx"]) ? -1 : (int)P.I("sub_idx"))) E.Touch(); }
    }
    /// <summary>AF:5470-5518: moves the region to the position.</summary>
    public sealed class MRGN : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t) { var v = TrigUtil.Vec(P["pos"]); if (v != null && E.Host.MoveRegion(P.S("rgn_guid"), v.Value.X, v.Value.Y)) E.Touch(); }
    }

    // ------------------------------------------------------------------------------------------- 5.5 fog of war
    /// <summary>AF:901-1044: a circle at pos, or - pos [0 0 0] - one per object of the query, following it.</summary>
    public sealed class SFOW : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            int pl = P.Slot("owner", 0);
            if (!H.PlayerExists(pl)) return;
            var v = TrigUtil.Vec(P["pos"]);
            double r = P.N("radius", 20), d = P.N("duration", 0);
            if (v is { IsZero: false } p) { E.Reveals.Add(H.RevealArea(pl, p.X, p.Y, r, d > 0 ? d : 0, -1)); return; }
            foreach (var o in Objs(E, t))
            {
                var i = H.Info(o);
                if (i != null) E.Reveals.Add(H.RevealArea(pl, i.X, i.Y, r, d > 0 ? d : 0, i.HasEntity ? o : -1));
            }
        }
    }

    // ------------------------------------------------------------------------------------------- 5.6 AI (docs/spec/ai.md §10, §13.1)
    /// <summary>AF:3536-3613: SetModuleBehavior(player, module, behavior). AIST is the old name.</summary>
    public sealed class AIBV : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            int s = P.Slot("player_id", 0);
            if (!E.Host.AiBehavior(s, P.S("behavior"), P.SE("module", "CTRL"))) E.Warn($"{t.Name}: {Type} for player {s}: no brain");
        }
    }
    /// <summary>AF:4463-4641: an attack order; with no target object nothing happens.</summary>
    public sealed class AIFT : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var H = E.Host;
            int s = P.Slot("player_id", 0);
            var targets = Ents(E, Objs(E, t)).Select(x => x.Id).ToList();
            if (targets.Count == 0) return;
            MapPos? Pos(string k) { var v = TrigUtil.Vec(P[k]); return v is { IsZero: false } ? v : null; }
            if (!P.On("custom_attack")) { H.AiAutoAttack(s, targets); return; }
            bool ok = H.AiAttack(s, new AiAttackOrder
            {
                Type = P.S("attack_type"), Targets = targets, Position = Pos("position_edit"), AttackOnTheWay = P.On("all_the_way"), Spawn = P.On("attack_with_all"),
                SpawnPosition = Pos("spawn_position"), IgnoreLocations = P.On("ignore_locations"), TargetOnly = P.On("target_obj"), Ship = P.On("ship"), ShipLand = P.On("ship_land"),
                Behaviour = P.S("attack_behavior"),
            });
            if (!ok) E.Warn($"{t.Name}: AIFT for player {s}: no brain");
        }
    }
    /// <summary>AF:3700-3774: AddDefenseArea id pos radius max_units (max 0 removes it).</summary>
    public sealed class AIDA : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t) => E.Host.AiDefendArea(P.Slot("player_id", 0), P.S("id"), TrigUtil.Vec(P["position"]) ?? new MapPos(0, 0, 0), P.N("radius", 0), (int)TrigUtil.ToInt(P.SE("max_units", "0")));
    }
    /// <summary>AF:3780-3949: lock / unlock units for the AI (the query runs when the action fires: also units spawned
    /// since the level loaded).</summary>
    public sealed class AILU : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var list = P.On("enable_objsel", true) ? Objs(E, t) : new List<int>();
            foreach (var id in TrigUtil.Guids(P["units"])) { int o = E.Host.FindObject(id); if (o >= 0) list.Add(o); }
            E.Host.AiLockUnits(P.Slot("player_id", 0), Units(E, list).Select(x => x.Id).ToList(), P.On("lock", true));
        }
    }
    /// <summary>AF:4647-4764: /AggroState_n for every object.</summary>
    public sealed class AIAM : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            var list = P.On("selector_enabled", true) ? Objs(E, t) : new List<int>();
            foreach (var id in TrigUtil.Guids(P["aggro_state_units"])) { int o = E.Host.FindObject(id); if (o >= 0) list.Add(o); }
            E.Host.SetAggression(Units(E, list).Select(x => x.Id).ToList(), (int)TrigUtil.ToInt(P.SE("aggro_state", "0")));
        }
    }
    /// <summary>AF:3339-3529: a weight for a named region in an AI influence map.</summary>
    public sealed class AIRG : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t) => E.Host.AiRegionMap(P.On("all_players") ? -1 : P.Slot("player_id", 0), P.S("map_name"), P.S("region_name"), P.N("value", 0), P.On("add_edit", true));
    }
    /// <summary>Call a module command of the AI (not used by the campaign).</summary>
    public sealed class AICM : TriggerAction { public override void Run(TriggerEngine E, Trigger t) => E.Host.AiModuleCommand(P.Slot("player_id", 0), P.S("module"), P.S("command")); }

    // ------------------------------------------------------------------------------------------- Lua
    /// <summary>Script(function(api) ... end): an action written in Lua (not in the original). Errors are logged; the
    /// other actions still run.</summary>
    public sealed class SCRP : TriggerAction
    {
        public override void Run(TriggerEngine E, Trigger t)
        {
            if (Node.Lua == null || Node.Function == null) return;
            Node.Lua.CallAction(Node.Function, $"Script in \"{t.Name}\"");
        }
    }
}
