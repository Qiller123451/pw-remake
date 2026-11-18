// The trigger engine's semantics (remake/docs/spec/triggers.md §1 - §6) against the FakeMissionHost: flags, once /
// re-enable, the end-of-step queue, random, by_difficulty, folders, expressions, count expressions, variables,
// profile variables, timers, regions, DEAD / DYIN, groups, OBJP, from_condition, quests, sequences, dialogues,
// markers, info bar, players, waypoints, owners, unknown types, and two situations of the real campaign: the
// tutorial's self-looping TIME trigger and mission 15's M03a / M03b pair.
// Ported from remake/tests/campaign_engine.mjs (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Scripting;

namespace PwRemake.Tests;

/// <summary>Builders for hand-made missions (campaign_mock.mjs mission / trig).</summary>
public static class TB
{
    static int guidN;
    public static string Guid(string k) => k + "_guid_" + (++guidN);
    static Dictionary<string, string> KV(string code, string[] kv)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (code is "CVAR" or "VARS") d["local"] = "1";
        for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
        return d;
    }
    public static NodeDef C(string code, params string[] kv) => new() { Code = code, P = KV(code, kv) };
    public static NodeDef A(string code, params string[] kv) => new() { Code = code, P = KV(code, kv) };
    public static NodeDef AD(int diff, string code, params string[] kv) => new() { Code = code, P = KV(code, kv), Difficulty = diff };
    public static NodeDef Var(string name, string op, string value) => A("VARS", "varname", name, "operation", op, "value", value);
    /// <summary>flags: words of "once enabled random by_difficulty node_off"; default "once enabled".</summary>
    public static TriggerDef T(string name, NodeDef[] conds, NodeDef[] acts, string flags = "once enabled", string? guid = null, string folder = "Root", string expr = "")
    {
        var f = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new TriggerDef
        {
            Name = name, Guid = guid ?? Guid("t"), Folder = folder, Expression = expr, Conditions = conds.ToList(), Actions = acts.ToList(),
            Once = f.Contains("once"), Enabled = f.Contains("enabled"), Random = f.Contains("random"), ByDifficulty = f.Contains("by_difficulty"), NodeOff = f.Contains("node_off"),
        };
    }
    public static NodeDef[] L(params NodeDef[] n) => n;
    public static MissionScript Mission(IEnumerable<TriggerDef> ts, IEnumerable<(string, string)>? vars = null, IEnumerable<QuestState>? quests = null, Dictionary<string, string>? texts = null)
    {
        var m = new MissionScript();
        m.Triggers.AddRange(ts);
        foreach (var (k, v) in vars ?? Array.Empty<(string, string)>()) m.Variables.Add(new(k, v));
        m.Quests.AddRange(quests ?? Array.Empty<QuestState>());
        foreach (var (k, v) in texts ?? new()) m.Texts[k] = v;
        m.ConditionDefaults["TIME"] = new() { ["duration"] = "0", ["reset"] = "1", ["show"] = "0" };
        m.ConditionDefaults["PLDE"] = new() { ["check_bldgs"] = "0", ["check_producer"] = "1", ["check_none"] = "0", ["check_pyramid"] = "1" };
        m.ActionDefaults["TIMR"] = new() { ["timer_id"] = "1", ["event"] = "create", ["show"] = "1" };
        m.ActionDefaults["DELO"] = new() { ["maxobjs"] = "-1" };
        m.ActionDefaults["COBJ"] = new() { ["ignore_pyramid"] = "1" };
        return m;
    }
    public static (FakeMissionHost H, TriggerEngine E) Make(MissionScript m, Action<FakeMissionHost>? setup = null, int diff = 1)
    {
        var h = new FakeMissionHost { Diff = diff };
        setup?.Invoke(h);
        var e = new TriggerEngine(h, m);
        h.E = e;
        return (h, e);
    }
}

[TestClass]
public static class TriggersEngineTests
{
    static string? V(TriggerEngine e, string n) => e.GetVarOrNull(n);

    public static void Helpers(TestContext t)
    {
        var vars = new Dictionary<string, string> { ["a"] = "5", ["two words"] = "7" };
        string? Get(string k) => vars.GetValueOrDefault(k);
        bool C(long a, string s) => TrigUtil.Compare(a, s, Get);
        t.True(C(3, ">2") && !C(2, ">2") && C(2, ">=2") && C(1, "<2") && C(2, "<=2") && C(2, "==2") && C(2, "=2") && C(3, "!=2"), "count expressions: > >= < <= == = !=");
        t.True(C(2, "2") && C(3, "2") && !C(1, "2") && C(0, ""), "a bare number and the empty string mean >=");
        t.True(C(5, "==$(a)") && C(8, ">$(two words)") && !C(0, "<1$(nothing)") && TrigUtil.ValueString("$(nothing)", Get) == "", "$(variable) in a count expression (unknown = 0)");
        bool X(string s, params int[] st) => TriggerExpr.Compile(s, st.Length).Eval(i => st[i] != 0);
        t.True(X("1 && (2 || 3) && !4", 1, 0, 1, 0) && !X("1 && (2 || 3) && !4", 1, 0, 1, 1) && !X("1 && (2 || 3) && !4", 1, 0, 0, 0) && X("1 || 2 && 3", 1, 0, 0) && !X("", 1, 0) && X("", 1, 1) && !X(""), "expressions: && || ! ( ), precedence, empty = AND of all, none = never");
        t.True(X("1&2|3", 0, 0, 1) && X("!(1 || 2)", 0, 0), "single & | and a negated group");
        bool bad = false; try { TriggerExpr.Compile("1 &&", 1); } catch (FormatException) { bad = true; }
        t.True(bad, "a bad expression is an error");
        t.Equal(12L, TrigUtil.ToInt(" 12abc"), "toInt: the leading integer");
        t.Equal(0L, TrigUtil.ToInt("=1"), "toInt(\"=1\") = 0");
        t.Equal(new MapPos(1.5, -2, 3), TrigUtil.Vec("[1.5 -2 3]")!.Value, "vector [x y z]");
        t.Equal(new MapPos(0, 0, 0.3), TrigUtil.Vec("0.0, 0.0, 0.3")!.Value, "vector x, y, z");
        t.True(TrigUtil.Vec("5") == null, "one number is no vector");
    }

    public static void FlagsOnceTrig(TestContext t)
    {
        string self = TB.Guid("t"), b = TB.Guid("t"), c = TB.Guid("t"), chain = TB.Guid("t"), tog = TB.Guid("t");
        var m = TB.Mission(new[]
        {
            TB.T("once", TB.L(TB.C("TIME", "duration", "2")), TB.L(TB.Var("once", "+", "1"))),
            TB.T("every", TB.L(TB.C("TIME", "duration", "2")), TB.L(TB.Var("every", "+", "1")), "enabled"),
            TB.T("loop", TB.L(TB.C("TIME", "duration", "3")), TB.L(TB.Var("loop", "+", "1"), TB.A("TRIG", "guid", self, "state", "1")), guid: self),
            TB.T("start", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.A("TRIG", "guid", b, "state", "1"), TB.Var("order", "set", "start"), TB.A("TRIG", "guid", "no such trigger", "state", "1"))),
            TB.T("sub", TB.L(TB.C("TRUE")), TB.L(TB.Var("order", "set", "sub"), TB.Var("sub", "+", "1"), TB.A("TRIG", "guid", chain, "state", "1")), "once", guid: b),
            TB.T("chain", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.Var("chain", "+", "1")), "once", guid: chain),
            TB.T("again", TB.L(TB.C("TIME", "duration", "4")), TB.L(TB.A("TRIG", "guid", b, "state", "1")), "enabled"),
            TB.T("long", TB.L(TB.C("TIME", "duration", "5")), TB.L(TB.Var("long", "+", "1")), guid: c),
            TB.T("poke", TB.L(TB.C("TIME", "duration", "3")), TB.L(TB.A("TRIG", "guid", c, "state", "1"))),
            TB.T("stopped", TB.L(TB.C("TIME", "duration", "6")), TB.L(TB.Var("stopped", "+", "1")), guid: tog),
            TB.T("stopper", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.A("TRIG", "guid", tog))),
            TB.T("empty", TB.L(), TB.L(TB.Var("empty", "+", "1"))),
        });
        var (h, e) = TB.Make(m);
        h.Run(1.5);
        t.True(V(e, "order") == "sub" && V(e, "sub") == "1", "TRIG enables a TRUE trigger: it fires after the remaining actions of the enabling trigger (queue order)");
        h.Run(1);
        t.True(V(e, "once") == "1" && V(e, "every") == "1" && V(e, "chain") == "1", "TIME fires after its duration; a TIME 1 chain link one second after it was enabled");
        h.Run(8);
        t.True(V(e, "once") == "1" && V(e, "every") == "5", $"once fires one time, a trigger without once and a TIME condition every period ({V(e, "every")} in 10.5 s)");
        t.Equal("3", V(e, "loop"), "a once trigger that re-enables itself loops (the tutorial's Gda07 TIME loop)");
        t.Equal("3", V(e, "sub"), "a once trigger is not used up: every TRIG enable makes it fire again");
        t.True(V(e, "long") == "1" && e.Find("long")!.Fired == 1, "TRIG enable on an enabled trigger does not restart its timer");
        t.True(V(e, "stopped") == null, "TRIG without state disables: the timer is cancelled");
        t.True(V(e, "empty") == null, "a trigger without conditions never fires");
        t.Equal(0, e.Errors.Count, "no errors");
    }

    public static void TutorialTimeLoop(TestContext t)
    {
        // triggers.md §1.1 no. 6: once, TIME 60, its own action TRIG(self, 1): the re-enable starts a new timer that the
        // TIME condition's OnPush must not kill afterwards
        string g = TB.Guid("t");
        var (h, e) = TB.Make(TB.Mission(new[] { TB.T("Gda07#chariot_not_ready", TB.L(TB.C("TIME", "duration", "60")), TB.L(TB.Var("n", "+", "1"), TB.A("TRIG", "guid", g, "state", "1")), guid: g) }));
        h.Run(185);
        t.Equal("3", V(e, "n"), "fires every 60 s for ever");
        t.True(e.Find(g)!.Enabled, "and stays enabled");
    }

    public static void Mission15PingPair(TestContext t)
    {
        // triggers.md §8: the expression of a re-enabled trigger is evaluated once all its conditions have woken
        string a = TB.Guid("t"), b = TB.Guid("t");
        var m = TB.Mission(new[]
        {
            TB.T("M03a#mq02_ping02_set", TB.L(TB.C("CVAR", "varname", "mq02_set", "value", "1", "operation", "=="), TB.C("CVAR", "varname", "mq02_done", "value", "0", "operation", "=="), TB.C("CVAR", "varname", "ctrlpt02", "value", "0", "operation", "==")),
                TB.L(TB.A("MPNG", "pos", "[10 10 0]", "pos_objquery_flag", "1", "owner", "0", "id", "mq0202"), TB.A("TRIG", "guid", b, "state", "1")), guid: a, expr: "1 && 2 && 3"),
            TB.T("M03b#mq02_ping02_del", TB.L(TB.C("CVAR", "varname", "ctrlpt02", "value", "1", "operation", "==")),
                TB.L(TB.A("MPNG", "id", "mq0202", "add_remove", "1"), TB.A("TRIG", "guid", a, "state", "1")), "once", guid: b),
        }, vars: new[] { ("mq02_set", "1"), ("mq02_done", "0"), ("ctrlpt02", "0") });
        var (h, e) = TB.Make(m);
        h.Run(0.2);
        t.True(e.Find(a)!.Fired == 1 && h.Markers.Count == 1, "M03a sets the ping");
        e.SetVar("ctrlpt02", "set", "1");
        h.Run(0.5);
        t.True(e.Find(b)!.Fired == 1 && e.Find(a)!.Fired == 1 && h.Markers.Count == 0, $"M03b removes it and re-enables M03a, which does not fire on stale states (a {e.Find(a)!.Fired}, b {e.Find(b)!.Fired})");
        e.SetVar("ctrlpt02", "set", "0");
        h.Run(0.2);
        t.True(e.Find(a)!.Fired == 2 && h.Markers.Count == 1 && e.Errors.Count == 0, "the pair goes on as the variable changes");
    }

    public static void RandomAndDifficulty(TestContext t)
    {
        var acts = new[] { "a", "b", "c", "d", "e" }.Select(n => TB.Var(n, "+", "1")).ToArray();
        var (h, e) = TB.Make(TB.Mission(new[] { TB.T("rnd", TB.L(TB.C("TIME", "duration", "1")), acts, "enabled random") }));
        h.Run(200.5);
        var n = new[] { "a", "b", "c", "d", "e" }.Select(k => (int)TrigUtil.ToInt(V(e, k))).ToArray();
        t.True(n.Sum() == 200 && n.All(x => x > 20), $"random: exactly one action per firing, all of them come up ({string.Join(",", n)})");
        for (int d = 0; d < 3; d++)
        {
            var (h2, e2) = TB.Make(TB.Mission(new[]
            {
                TB.T("diff", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.AD(1, "VARS", "varname", "m", "operation", "+", "value", "1"), TB.AD(0, "VARS", "varname", "e", "operation", "+", "value", "1"), TB.AD(2, "VARS", "varname", "h", "operation", "+", "value", "1"), TB.AD(2, "VARS", "varname", "h", "operation", "+", "value", "1")), "once enabled by_difficulty"),
                TB.T("all", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.AD(0, "VARS", "varname", "x", "operation", "+", "value", "1"), TB.AD(2, "VARS", "varname", "x", "operation", "+", "value", "1"))),
            }), diff: d);
            h2.Run(2);
            string G(string k) => V(e2, k) ?? "0";
            t.True(G("e") == (d == 0 ? "1" : "0") && G("m") == (d == 1 ? "1" : "0") && G("h") == (d == 2 ? "2" : "0") && G("x") == "2", $"by_difficulty on difficulty {d}: only its own actions; without the flag all actions run");
        }
    }

    public static void Folders(TestContext t)
    {
        var (h, e) = TB.Make(TB.Mission(new[]
        {
            TB.T("off", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.Var("off", "+", "1")), "once enabled node_off", folder: "Root/A/Order 1"),
            TB.T("sub", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.Var("sub", "+", "1")), "enabled", folder: "Root/B/Side quest/deep"),
            TB.T("other", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.Var("other", "+", "1")), "enabled", folder: "Root/B/Side quest 2"),
            TB.T("switch on", TB.L(TB.C("TIME", "duration", "5")), TB.L(TB.A("ACND", "nodename", "Root/A/Order 1/", "deststate", "1"))),
            TB.T("switch off", TB.L(TB.C("TIME", "duration", "3.5")), TB.L(TB.A("ACND", "nodename", "Root/B/Side quest/"))),
        }));
        h.Run(4.5);
        t.True(V(e, "off") == null, "a trigger in a folder that starts inactive (node_off) does not run");
        t.True(V(e, "sub") == "3" && V(e, "other") == "4", $"ACND deststate 0 stops the triggers below the folder, not its neighbours ({V(e, "sub")}, {V(e, "other")})");
        h.Run(2);
        t.Equal("1", V(e, "off"), "ACND deststate 1 starts the enabled triggers of the folder");
        t.Equal("3", V(e, "sub"), "a deactivated folder stays off");
    }

    public static void Variables(TestContext t)
    {
        var (h, e) = TB.Make(TB.Mission(new[]
        {
            TB.T("count", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.Var("a", "+", "1")), "enabled"),
            TB.T("eq2", TB.L(TB.C("CVAR", "varname", "a", "operation", "==", "value", "2")), TB.L(TB.Var("eq2", "set", "$(a)"))),
            TB.T("gt", TB.L(TB.C("CVAR", "varname", "a", "operation", ">", "value", "$(lim)")), TB.L(TB.Var("gt", "set", "$(a)"))),
            TB.T("noop", TB.L(TB.C("CVAR", "varname", "a", "value", "5")), TB.L(TB.Var("ge", "set", "$(a)"), TB.Var("m", "set", "7"), TB.Var("m", "*", "3"), TB.Var("m", "-", "1"), TB.Var("m", "/", "3"), TB.Var("m", "/", "0"), TB.Var("slip", "set", "=1"))),
            TB.T("slip", TB.L(TB.C("CVAR", "varname", "slip", "operation", "==", "value", "1")), TB.L(TB.Var("slipped", "set", "1"))),
            TB.T("unknown", TB.L(TB.C("CVAR", "varname", "does not exist", "operation", "<", "value", "1")), TB.L(TB.Var("unk", "set", "1"))),
            TB.T("lt", TB.L(TB.C("CVAR", "varname", "a", "operation", "<", "value", "1")), TB.L(TB.Var("lt", "set", "yes"))),
        }, vars: new[] { ("a", "0"), ("lim", "3"), ("slip", "0") }));
        h.Run(0.2);
        t.Equal("yes", V(e, "lt"), "CVAR is evaluated when its trigger is enabled");
        h.Run(6);
        t.True(V(e, "eq2") == "2" && V(e, "gt") == "4" && V(e, "ge") == "5", $"CVAR ==, > $(other variable), no operation = >= ({V(e, "eq2")}, {V(e, "gt")}, {V(e, "ge")})");
        t.Equal("6", V(e, "m"), "VARS set * - / as integers, a division by 0 changes nothing");
        t.True(V(e, "slip") == "=1" && V(e, "slipped") == null, "VARS set stores the string as it is (\"=1\" never equals 1)");
        t.True(V(e, "unk") == null, "CVAR on an unknown variable is never true");
    }

    public static void ProfileVariables(TestContext t)
    {
        NodeDef P(string code, params string[] kv) { var n = TB.C(code, kv); n.P.Remove("local"); return n; }
        var (h, e) = TB.Make(TB.Mission(new[]
        {
            TB.T("first time", TB.L(P("CVAR", "varname", "Tutorial_Started", "operation", "<", "value", "1")), TB.L(TB.Var("long_intro", "set", "1"), P("VARS", "varname", "Tutorial_Started", "operation", "+", "value", "1"), P("VARS", "varname", "Not_A_Profile_Variable", "operation", "set", "value", "1"))),
            TB.T("seen", TB.L(P("CVAR", "varname", "Tutorial_Started", "operation", ">", "value", "0")), TB.L(TB.Var("short_intro", "set", "1"))),
            TB.T("unknown", TB.L(P("CVAR", "varname", "Not_A_Profile_Variable", "operation", "<", "value", "5")), TB.L(TB.Var("bad", "set", "1"))),
        }));
        h.Run(0.5);
        t.True(V(e, "long_intro") == "1" && V(e, "short_intro") == "1" && e.Profile["Tutorial_Started"] == "1" && !e.HasVar("Tutorial_Started"), "VARS / CVAR without local = 1 work on the profile variables");
        t.True(V(e, "bad") == null && !e.Profile.ContainsKey("Not_A_Profile_Variable"), "a profile variable that is not defined does not exist");
        t.Equal("1", h.Profile?["Tutorial_Started"], "the host keeps the profile");
    }

    public static void LevelTimers(TestContext t)
    {
        string late = TB.Guid("t");
        var (h, e) = TB.Make(TB.Mission(new[]
        {
            TB.T("make", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.A("TIMR", "timer_id", "0", "duration", "10", "show", "0"), TB.A("TIMR", "timer_id", "5", "duration", "20", "tooltip", "_tip"), TB.A("TIMR", "timer_id", "6", "duration", "4"))),
            TB.T("restart", TB.L(TB.C("TIME", "duration", "6")), TB.L(TB.A("TIMR", "timer_id", "0", "duration", "10", "show", "0"))),
            TB.T("pause", TB.L(TB.C("TIME", "duration", "3")), TB.L(TB.A("TIMR", "timer_id", "5", "event", "pause"))),
            TB.T("go on", TB.L(TB.C("TIME", "duration", "13")), TB.L(TB.A("TIMR", "timer_id", "5", "event", "unpause"))),
            TB.T("kill", TB.L(TB.C("TIME", "duration", "2")), TB.L(TB.A("TIMR", "timer_id", "6", "event", "kill"))),
            TB.T("t0", TB.L(TB.C("TIMR", "timer_id", "0")), TB.L(TB.Var("t0", "set", "x")), "enabled"),
            TB.T("t5", TB.L(TB.C("TIMR", "timer_id", "5")), TB.L(TB.Var("t5", "set", "x"))),
            TB.T("t6", TB.L(TB.C("TIMR", "timer_id", "6")), TB.L(TB.Var("t6", "set", "x"))),
            TB.T("late", TB.L(TB.C("TIMR", "timer_id", "0"), TB.C("CVAR", "varname", "go", "operation", "==", "value", "1")), TB.L(TB.Var("late", "set", "x")), "once", guid: late, expr: "1 && 2"),
            TB.T("later", TB.L(TB.C("TIME", "duration", "18")), TB.L(TB.A("TRIG", "guid", late, "state", "1"), TB.Var("go", "set", "1"))),
        }, texts: new() { ["_tip"] = "Time left" }));
        var T = e.Timers;
        h.Run(1.5);
        t.True(T.Count == 3 && T["5"].Show && T["5"].Label == "Time left" && !T["0"].Show && Math.Abs(T["0"].Left - 9.5) < 0.2, "TIMR create: Timers has { id, left, show, label }");
        h.Run(9.6);
        t.True(V(e, "t0") == null && T.ContainsKey("0"), "creating an existing timer restarts it");
        t.True(!T.ContainsKey("6") && V(e, "t6") == null, "kill deletes a timer without firing");
        h.Run(5.5);
        t.True(V(e, "t0") == "x" && !T.ContainsKey("0"), "the TIMR condition fires when its timer runs out; the timer is deleted");
        h.Run(4.5);
        t.True(V(e, "t5") == null && T.ContainsKey("5") && Math.Abs(T["5"].Left - 9.9) < 0.3, "pause / unpause keep the remaining time");
        t.Equal("x", V(e, "late"), "a TIMR condition is pushed while its trigger is disabled: the state stays 1");
        h.Run(11);
        t.Equal("x", V(e, "t5"), "a paused timer runs out later");
    }

    public static void ObjectsRegionsGroups(TestContext t)
    {
        string rg = TB.Guid("r"), grp = TB.Guid("o"), hero = TB.Guid("o"), w1 = TB.Guid("o"), cage = TB.Guid("o"), wave = TB.Guid("t");
        var m = TB.Mission(new[]
        {
            TB.T("hero in", TB.L(TB.C("REGN", "rgn_guid", rg, "obj_type", "CHTR|", "obj_owner", "0", "obj_class", "Cole_s0", "obj_count", ">0")), TB.L(TB.Var("in", "set", "1"))),
            TB.T("enemy gone", TB.L(TB.C("REGN", "rgn_guid", rg, "obj_type", "CHTR|ANML|", "obj_owner", "1", "obj_count", "<1")), TB.L(TB.Var("clear", "set", "1"))),
            TB.T("none left", TB.L(TB.C("REGN", "obj_type", "CHTR|", "obj_owner", "2", "obj_count", "<1"), TB.C("TIME", "duration", "3")), TB.L(TB.Var("p2", "set", "1")), expr: "1 && 2"),
            TB.T("two", TB.L(TB.C("REGN", "obj_owner", "0", "obj_type", "CHTR|", "obj_count", "2")), TB.L(TB.Var("two", "set", "1"))),
            TB.T("spawn", TB.L(TB.C("TIME", "duration", "2")), TB.L(TB.A("SPGR", "group", grp, "classes_0", "aje_warrior 2 3|aje_archer 1 1|", "owner", "1", "pos", "50 50 0"), TB.A("TRIG", "guid", wave, "state", "1"), TB.A("COBJ", "obj_name", "aje_spearman", "obj_owner", "1", "obj_pos", "60, 60, 0", "obj_level", "2"))),
            TB.T("wave dead", TB.L(TB.C("CKGR", "group_guid", grp, "check_val", "<1")), TB.L(TB.Var("wave", "set", "1")), "once", guid: wave),
            TB.T("early wave check", TB.L(TB.C("CKGR", "group_guid", grp, "check_val", "<1")), TB.L(TB.Var("early", "set", "1"))),
            TB.T("hero dead", TB.L(TB.C("DEAD", "obj_type", "CHTR|", "obj_owner", "0", "obj_class", "Cole_s0")), TB.L(TB.A("GAOV", "reason", "_GAOV_hero"))),
            TB.T("ghost dead", TB.L(TB.C("DEAD", "obj_name", "was_deleted_7", "obj_guid", "nope")), TB.L(TB.Var("ghost", "set", "1"))),
            TB.T("ghost dying", TB.L(TB.C("DYIN", "obj_name", "was_deleted_7", "obj_guid", "nope")), TB.L(TB.Var("ghost2", "set", "1"))),
            TB.T("warrior dying", TB.L(TB.C("DYIN", "obj_name", "hu_warrior_1", "obj_guid", w1)), TB.L(TB.Var("wdead", "set", "1"))),
            TB.T("cage gone", TB.L(TB.C("DEAD", "obj_name", "cage_0", "obj_guid", cage)), TB.L(TB.Var("cage", "set", "1"))),
            TB.T("hp", TB.L(TB.C("OBJP", "obj_class", "Cole_s0", "obj_owner", "0", "attrib_name", "hitpoints", "attrib_value", "<35", "attrib_max", "maxhitpoints")), TB.L(TB.Var("hurt", "set", "1"))),
            TB.T("lvl", TB.L(TB.C("OBJP", "obj_class", "Cole_s0", "obj_owner", "0", "attrib_name", "level", "attrib_value", ">1")), TB.L(TB.Var("lvl", "set", "1"))),
            TB.T("walk on", TB.L(TB.C("REGN", "rgn_guid", rg, "obj_type", "CHTR|", "obj_owner", "0", "obj_count", ">0")), TB.L(TB.A("ACDO", "from_condition", "0", "action", "WalkAction", "additional_params", "[400 400 0] | 2"))),
        }, texts: new() { ["_GAOV_hero"] = "A hero is dead!" });
        FakeObj cole = null!;
        var (h, e) = TB.Make(m, h =>
        {
            h.AddRegion(rg, "goal", 300, 300, 40, 40);
            cole = h.Add("Cole_s0_0", "Cole_s0", "CHTR", 0, 100, 100, hero); cole.Level = 2;
            h.Add("hu_warrior_1", "hu_warrior", "CHTR", 0, 104, 100, w1);
            h.Add("aje_warrior_0", "aje_warrior", "CHTR", 1, 310, 310);
            h.Add("cage_0", "hu_cage", "DCCO", -1, 200, 200, cage);
            h.AddGroup("GroupObject_0", grp);
        });
        h.Run(1);
        t.Equal("1", V(e, "early"), "CKGR <1 on an empty group is true as soon as it is enabled");
        t.True(V(e, "ghost") == "1" && V(e, "ghost2") == null, "DEAD on an object that never existed is true at once, DYIN never");
        t.True(V(e, "two") == "1" && V(e, "in") == null && V(e, "clear") == null && V(e, "p2") == null, "REGN: a bare count means >=; region and owner filters hold");
        h.Run(3);
        t.Equal("1", V(e, "p2"), "REGN on the whole map (\"no unit of player 2\") combined with a TIME that has run out");
        int g = h.FindGroup(grp);
        var made = h.GroupMembers(g).Select(h.Obj).ToList();
        t.True(made.Count == 4 && h.Objs.Count(o => o.Alive && o.Owner == 1 && o.IsUnit) == 6, $"SPGR creates the units into the group ({made.Count}), COBJ one more");
        t.True(made.Count(o => o.Level == 2) == 3 && made.Count(o => o.Level == 1) == 1 && h.Objs.First(o => o.Class == "aje_spearman").Level == 3, "levels: SPGR 1-based, COBJ 0-based");
        t.True(V(e, "wave") == null, "the \"wave is dead\" trigger enabled after the spawn waits");
        h.MoveNow(cole.Id, 320, 320);
        h.Run(1);
        t.Equal("1", V(e, "in"), "REGN: the hero walked into the region (polled)");
        t.True(h.Calls.Contains("Move Cole_s0_0 400 400"), "from_condition: the action works on the objects its REGN condition found");
        foreach (var o in made) h.KillNow(o.Id);
        h.KillNow(h.Objs.First(o => o.Class == "aje_warrior" && o.Alive).Id);
        h.Run(1);
        t.True(V(e, "wave") == "1" && h.GroupMembers(g).Count == 0, "CKGR <1: the spawned group is dead (dead members leave the group)");
        t.Equal("1", V(e, "clear"), "REGN <1: no enemy left in the region");
        cole.Hp = 30; cole.Level = 3;
        h.Run(0.5);
        t.True(V(e, "hurt") == "1" && V(e, "lvl") == "1", "OBJP: hit points in percent (attrib_max) and the 0-based level");
        h.Remove(h.ByName("cage_0")!.Id); h.KillNow(h.FindObject(w1));
        h.Run(0.2);
        t.True(V(e, "cage") == "1" && V(e, "wdead") == "1", "DEAD fires for deleted scenery, DYIN for a named unit that died");
        t.True(h.Ended == null, "nobody lost yet");
        h.KillNow(cole.Id);
        h.Run(1);
        t.True(h.Ended == null && e.Ended is { Won: false }, "DEAD on the hero class -> GAOV: the defeat comes 4 s later");
        h.Run(4);
        t.True(h.Ended is { Won: false, Text: "A hero is dead!" } && h.Calls.Contains("AiGameOver"), "GAOV: EndMission(false) with the reason text; the AI is told at once");
        t.Equal(0, e.Errors.Count, "no errors");
    }

    public static void QuestsSequencesUi(TestContext t)
    {
        string q1 = TB.Guid("q"), q2 = TB.Guid("q"), qm = TB.Guid("o");
        var quests = new[]
        {
            new QuestState { Guid = q1, Name = "MQ01", Main = true, Bonus = new double[] { 100, 200, 300 }, Headline = "First" },
            new QuestState { Guid = q2, Name = "SQ01", Bonus = new double[] { 1, 2, 3 }, Headline = "Side" },
        };
        var m = TB.Mission(new[]
        {
            TB.T("intro", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.A("SQNZ", "sequence", "Cpn/x/intro.seq", "snap_cam_back", "1"))),
            TB.T("after intro", TB.L(TB.C("SQEN", "sequence_name", "cpn/X/Intro.seq")), TB.L(TB.A("QUES", "quest_guid", q1, "dest_state", "0"), TB.A("DGSC", "scene", "Cpn/x/ds_1.dlg"), TB.A("QMRK", "questionmark", qm, "questionstate", "QM_STATE_YELLOW", "questiontooltip", "_tt"),
                TB.A("MPNG", "pos", "[100 100 0]", "pos_objquery_flag", "1", "owner", "All", "id", "0007", "colortype", "SPMainQuest", "time_to_life", "0", "num_repeats", "0", "extended", "1"), TB.A("INBA", "text", "_bar\t$(saved)\t$(max)"),
                TB.A("RSRC", "res_rsrclist", "food|=500|0:wood|+50|0:iron|=5|0"), TB.A("PLCP", "player", "1", "food", "2000", "wood", "2000", "stone", "2000"), TB.A("DIPL", "changes", "0|2|2\n2|2|0\n"), TB.A("BLSL", "open_1", "25", "open_2", "15"), TB.A("POPL", "limit", "30"), TB.A("SNFA", "player", "1", "neutral", "3"),
                TB.A("TECH", "filters", "0|1|/Filters/AntiActions/Hu/Build/CHTR/hu_archer\n0|1|/Filters/X\n0|0|/Filters/X\n"), TB.A("AIBV", "player_id", "1", "behavior", "Turtle"), TB.A("PSND", "soundname", "cheer", "player", "-1"), TB.A("FDBK", "msg_text", "debug"), TB.A("WHAT"), TB.A("ACDO", "action", "Dance"), TB.A("OCPY", "obj_owner", "7", "new_owner", "x"))),
            TB.T("after dialog", TB.L(TB.C("DSEN", "dlgscene_name", "Cpn/x/ds_1.dlg")), TB.L(TB.Var("saved", "+", "1"), TB.A("QUES", "quest_guid", q1, "dest_state", "1"), TB.A("QUES", "quest_name", "SQ01", "dest_state", "2"))),
            TB.T("q done", TB.L(TB.C("QUES", "quest_guid", q1, "dest_state", "1"), TB.C("QUES", "quest_guid", q2, "dest_state", "2"), TB.C("WHO")), TB.L(TB.A("MPNG", "id", "0007", "add_remove", "1"), TB.A("INBA"), TB.A("SQNZ", "sequence", "Cpn/x/missing.seq"), TB.A("SQNZ", "sequence", "Cpn/x/outro.seq", "quit", "1")), expr: "(1 && 2) || 3"),
            TB.T("allied", TB.L(TB.C("DIPL", "plyr1", "2", "plyr2", "0", "relation", "2")), TB.L(TB.Var("allied", "set", "1"))),
            TB.T("rich", TB.L(TB.C("PLYR", "attrib_name", "food", "attrib_value", ">=500")), TB.L(TB.Var("rich", "set", "1"))),
            TB.T("tech", TB.L(TB.C("TECH", "filter", "Filters/AntiActions/Hu/Build/CHTR/hu_archer")), TB.L(TB.Var("tech", "set", "1"))),
        }, vars: new[] { ("saved", "2"), ("max", "6") }, quests: quests, texts: new() { ["_bar"] = "Mammoths saved: %1 of %2", ["_tt"] = "Talk to him" });
        var (h, e) = TB.Make(m, h => { h.HoldSequences = true; h.HoldDialogs = true; });
        h.Run(2);
        t.True(h.Sequences.Count == 1 && TrigUtil.FilePart(h.Sequences[0].Path) == "intro.seq" && h.Sequences[0].SnapBack, "SQNZ asks the host for the sequence");
        t.True(!quests[0].Visible, "nothing goes on before the sequence has ended");
        h.SkipAll(); h.Run(0.2);
        t.True(quests[0].Visible && h.QuestLog.Contains(("MQ01", "shown")), "SQEN (compared by file name, without case) -> QUES show -> QuestChanged");
        t.True(h.Dialogs.Count == 1, "DGSC asks the host for the scene");
        t.True(h.QuestionMarks[qm] == ("QM_STATE_YELLOW", "Talk to him"), "QMRK sets state and tooltip");
        t.True(h.Markers.Count == 1 && h.Markers[0].Id == "0007" && h.Markers[0].Kind == "SPMainQuest" && h.Markers[0].Ttl == 0 && h.Markers[0].X == 100, "MPNG adds a marker (map coordinates)");
        t.Equal("Mammoths saved: 2 of 6", h.InfoBar, "INBA: text with variables");
        var p0 = h.Players[0]!;
        t.True(p0.Res["food"] == 500 && p0.Res["wood"] == 50 && p0.Res["iron"] == 5 && h.Players[1]!.Caps["food"] == "2000", "RSRC list and PLCP");
        t.True(string.Join(",", p0.Pyramid!) == "25,15,0,0,0" && p0.PopMax == 30 && h.Players[1]!.AnimalsNeutral == 3, "BLSL, POPL, SNFA");
        t.True(p0.Filters.Contains("AntiActions/Hu/Build/CHTR/hu_archer") && !p0.Filters.Contains("X"), "TECH enables and disables filters");
        t.True(h.Players[1]!.Behaviour == "Turtle" && h.Calls.Contains("Sound cheer"), "AIBV reaches the AI, PSND the audio");
        h.Run(1);
        t.True(V(e, "allied") == "1" && V(e, "rich") == "1" && V(e, "tech") == "1", "DIPL, PLYR and TECH conditions");
        t.True(e.Warnings.Any(w => w.Contains("WHAT")) && e.Warnings.Any(w => w.Contains("WHO")) && e.Warnings.Any(w => w.Contains("ACDO Dance")) && e.Errors.Count == 0, "unknown condition / action types: one warning each, nothing thrown");
        h.SkipAll(); h.Run(0.2);
        t.True(V(e, "saved") == "3" && h.InfoBar == "", "DSEN after the dialogue; INBA without text clears the bar");
        t.True(quests[0].Accomplished && quests[1].Unaccomplishable && e.Boni == 200, $"QUES accomplished adds the bonus of the difficulty ({e.Boni})");
        t.True(h.Markers.Count == 0, "MPNG add_remove = 1 removes the marker");
        t.True(h.Sequences.Count == 3 && h.Ended == null, "a sequence without a file ends at once (here: the host holds every one; the next waits)");
        h.SkipAll(); h.Run(0.2);
        t.True(h.Ended is { Won: true }, "SQNZ quit = 1: the mission is won when the sequence ends");
    }

    public static void SequenceWithoutUi(TestContext t)
    {
        var (h, e) = TB.Make(TB.Mission(new[]
        {
            TB.T("a", TB.L(TB.C("TRUE")), TB.L(TB.A("SQNZ", "sequence", "x/ms_1.seq"), TB.A("DGSC", "scene", "x/ds_1.dlg"))),
            TB.T("b", TB.L(TB.C("SQEN", "sequence_name", "ms_1.seq")), TB.L(TB.Var("seq", "set", "1"))),
            TB.T("c", TB.L(TB.C("DSEN", "dlgscene_name", "ds_1.dlg")), TB.L(TB.Var("dlg", "set", "1"))),
        }));
        h.Run(0.2);
        t.True(V(e, "seq") == "1" && V(e, "dlg") == "1", "without a UI a sequence and a dialogue end at once (the end events are mandatory, §6.8)");
    }

    public static void WaypointsOwnersAppearance(TestContext t)
    {
        string g1 = TB.Guid("o"), u1 = TB.Guid("o"), u2 = TB.Guid("o"), u3 = TB.Guid("o"), gate = TB.Guid("o");
        var m = TB.Mission(new[]
        {
            TB.T("patrol", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.A("WYPT", "obj_name", "GroupObject_0", "obj_guid", g1, "waypoints", "120 100 0|120 120 0|0.0, 0.0, 0.0|100 120 0|", "patrolmode", "0", "walkspeed", "3"))),
            TB.T("arrived", TB.L(TB.C("WAYR", "obj_type", "CHTR|", "obj_owner", "1")), TB.L(TB.Var("arrived", "+", "1")), "enabled"),
            TB.T("join", TB.L(TB.C("TIME", "duration", "2")), TB.L(TB.A("OBAP", "obj_name", "hu_worker_0", "obj_guid", u3, "flags", "7"), TB.A("OCPY", "obj_name", "hu_worker_0", "obj_guid", u3), TB.A("ACDO", "obj_name", "gate_0", "obj_guid", gate, "action", "SetAnim", "additional_params", "open | 1"),
                TB.A("SFOW", "pos", "[0 0 0]", "obj_name", "hu_worker_0", "obj_guid", u3, "radius", "30", "duration", "-1"), TB.A("SFOW", "pos", "[10 10 0]", "radius", "50", "duration", "20"))),
            TB.T("swap", TB.L(TB.C("TIME", "duration", "30")), TB.L(TB.A("REPL", "obj_name", "aje_warrior_0", "obj_guid", u1, "new_obj", "aje_archer", "obj_level", "1"), TB.A("DELO", "obj_owner", "1", "obj_class", "aje_warrior"), TB.A("ACDO", "obj_class", "aje_archer", "obj_owner", "1", "action", "SetPos", "additional_params", "[300 300 0] | 2"))),
        });
        var (h, e) = TB.Make(m, h =>
        {
            var a = h.Add("aje_warrior_0", "aje_warrior", "CHTR", 1, 100, 100, u1); var b = h.Add("aje_warrior_1", "aje_warrior", "CHTR", 1, 100, 110, u2);
            var w = h.Add("hu_worker_0", "hu_worker", "CHTR", 2, 300, 300, u3); w.Parked = true;
            h.Add("gate_0", "hc_gate", "DCCO", -1, 200, 200, gate);
            var g = h.AddGroup("GroupObject_0", g1); g.Members.AddRange(new[] { a.Id, b.Id }); a.Group = b.Group = g.Id;
        });
        h.Run(1.5);
        var a = h.ByName("aje_warrior_0")!;
        t.True(h.Patrols.Count == 2 && h.Patrols[a.Id].Pts.Count == 3 && h.Patrols[a.Id].Pts[0] == new MapPos(120, 100, 0), "WYPT sends every member of the group along the points (zero entries skipped)");
        h.Run(1);
        var w = h.ByName("hu_worker_0")!;
        t.True(w.Owner == 0 && !w.Parked, "OBAP 7 + OCPY (no new_owner = the human player): a hidden unit appears and joins");
        t.True(h.Calls.Contains("Anim gate_0 open 1"), "ACDO SetAnim on scenery");
        t.True(h.Calls.Contains("Reveal 0 300 300 r30 0s follow hu_worker_0") && h.Calls.Contains("Reveal 0 10 10 r50 20s"), "SFOW: a reveal that follows the object, and one at a position for 20 s");
        e.OnWaypointReached(a.Id); e.OnWaypointReached(h.ByName("aje_warrior_1")!.Id); e.OnWaypointReached(w.Id);
        h.Run(0.1);
        t.Equal("2", V(e, "arrived"), "WAYR fires per unit of its query (a pulse: the non-once trigger fires twice)");
        h.Run(28);
        var arch = h.Objs.FirstOrDefault(o => o.Alive && o.Class == "aje_archer");
        t.True(arch != null && arch.Level == 2 && arch.Owner == 1 && arch.X == 300 && !h.Objs.Any(o => o.Alive && o.Class == "aje_warrior"), "REPL keeps place and owner, DELO deletes, SetPos teleports");
        t.Equal(0, e.Errors.Count, "no errors");
    }

    public static void SpawnOutOfBuilding(TestContext t)
    {
        string grp = TB.Guid("o"), src = TB.Guid("o");
        var (h, e) = TB.Make(TB.Mission(new[]
        {
            TB.T("spawn", TB.L(TB.C("TRUE")), TB.L(TB.A("SPGR", "group", grp, "classes_0", "hu_warrior 2 3", "owner", "1", "pos", "50 60 0", "use_spawn_obj", "1", "spawn_delay", "2", "obj_name", "gate_0", "obj_guid", src))),
            TB.T("all out", TB.L(TB.C("CKGR", "group_guid", grp, "check_val", ">=3")), TB.L(TB.Var("out", "set", "$(t)"))),
        }), h => { h.Add("gate_0", "hc_gate", "DCCO", -1, 10, 10, src); h.AddGroup("wave", grp); });
        h.Run(0.1);
        t.True(h.Calls.Contains("Anim gate_0 open 1") && !h.Calls.Any(c => c.StartsWith("SpawnOut")), "the gate opens first (its open animation)");
        h.Run(1.5);
        t.Equal(1, h.Calls.Count(c => c.StartsWith("SpawnOut")), "the first unit comes when the open animation is over");
        h.Run(4);
        t.True(h.Calls.Count(c => c.StartsWith("SpawnOut")) == 3 && e.Find("all out")!.Fired == 1, "one unit every spawn_delay seconds, into the group");
        h.Run(2);
        t.True(h.Calls.Contains("Anim gate_0 close 1"), "the gate closes after the last");
    }

    public static void EnableDisableHooks(TestContext t)
    {
        // OnEnabled / OnDisabled: a disabled TIME keeps its rest only with reset = 0; a trigger disabled in the same
        // step in which it was queued still fires (§8 no. 20)
        string a = TB.Guid("t"), b = TB.Guid("t");
        var (h, e) = TB.Make(TB.Mission(new[]
        {
            TB.T("keep", TB.L(TB.C("TIME", "duration", "4", "reset", "0")), TB.L(TB.Var("keep", "set", "$(x)")), guid: a),
            TB.T("off", TB.L(TB.C("TIME", "duration", "1")), TB.L(TB.A("TRIG", "guid", a, "state", "0"))),
            TB.T("on", TB.L(TB.C("TIME", "duration", "5")), TB.L(TB.A("TRIG", "guid", a, "state", "2"), TB.Var("x", "set", "5"))),
            TB.T("q", TB.L(TB.C("TRUE")), TB.L(TB.Var("q", "set", "1")), guid: b),
            TB.T("q off", TB.L(TB.C("TRUE")), TB.L(TB.A("TRIG", "guid", b))),
        }));
        h.Run(0.1);
        t.Equal("1", V(e, "q"), "a trigger disabled in the step in which it was queued still fires");
        h.Run(8.5);
        t.True(V(e, "keep") == "5", "TRIG 2 toggles; TIME with reset = 0 continues with the time it had left (fires 3 s after the re-enable)");
        t.True(e.Find(a)!.LastFired > 7.9 && e.Find(a)!.LastFired < 8.1, $"at 8 s ({e.Find(a)!.LastFired})");
    }
}
