// The trigger editor's check of a map's triggers (docs/MAP_EDITOR.md "Triggers" - the Problems list, a click jumps to
// the place): node types the engine does not know or in the wrong list, expressions that do not compile, Lua syntax
// errors (Script / Check code, custom script blocks, custom.lua), references to regions, objects, groups, variables,
// quests, triggers, folders, text keys, sequences, dialogues and question marks the map does not have, two triggers
// with one GUID - and, with the standard library at hand, whether the whole file loads as the game loads it. No Godot.
using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Scripting;

namespace PwRemake.Sim.Editor.Triggers;

public enum Severity { Error, Warning }

/// <summary>a problem: where (the trigger item, condition / action list, node, parameter; Item -1: custom.lua or the
/// whole file) and what</summary>
public sealed record Issue(Severity Severity, string Message, int Item = -1, bool? Conditions = null, int Node = -1, string? Param = null)
{
    public override string ToString() => $"{(Severity == Severity.Error ? "error" : "warning")}: {Message}";
}

public static class TriggerValidation
{
    /// <summary>the Lua syntax error of a chunk ("line 3: ..."), null when it compiles; expression: the code is an
    /// expression (a node: Script(function ... end))</summary>
    public static string? LuaSyntax(string code, bool expression = false)
    {
        try
        {
            var s = new Script(CoreModules.None);
            s.LoadString(expression ? "return " + code : code, null, "code");
            return null;
        }
        catch (SyntaxErrorException e) { return e.DecoratedMessage ?? e.Message; }
        catch (InterpreterException e) { return e.DecoratedMessage ?? e.Message; }
    }

    static bool None(string? v) => string.IsNullOrEmpty(v) || v == "NA";

    /// <summary>every problem of the map's triggers; stdDir: game/lua/std (null: no load check)</summary>
    public static List<Issue> Check(MissionDoc m, string? stdDir = null)
    {
        var res = new List<Issue>();
        var items = m.Triggers.Items;
        var objs = m.Objects();
        var objGuids = new HashSet<string>(objs.Select(o => o.Guid), StringComparer.Ordinal);
        var objNames = new HashSet<string>(objs.Select(o => o.Name), StringComparer.Ordinal);
        var folders = new HashSet<string>(m.Triggers.Folders(), StringComparer.Ordinal);
        var texts = new HashSet<string>(m.TextKeys, StringComparer.Ordinal);
        string Norm(string p) => p.Replace('\\', '/').Trim('/').ToLowerInvariant();
        var seqs = new HashSet<string>(m.Sequences.Select(Norm));
        var dlgs = new HashSet<string>(m.Dialogs.Select(Norm));
        var setVars = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in m.Triggers.Triggers) foreach (var n in t.Actions) if (n.Code is "VARS" && n.Value("varname") is { } vn) setVars.Add(vn);
        foreach (var t in m.Triggers.Triggers) foreach (var n in t.Conditions) if (n.Code is "BLDG" && n.Value("variable") is { Length: > 0 } vn) setVars.Add(vn);
        var guids = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is EdCode code)
            {
                if (LuaSyntax(code.Source) is { } err) res.Add(new Issue(Severity.Error, $"{code.Title}: Lua syntax: {err}", i));
                continue;
            }
            var t = (EdTrigger)items[i];
            string W = $"\"{t.Name}\"";
            if (t.Guid.Length > 0)
            {
                if (guids.TryGetValue(t.Guid, out var other)) res.Add(new Issue(Severity.Error, $"{W}: the GUID of \"{((EdTrigger)items[other]).Name}\" again", i));
                else guids[t.Guid] = i;
            }
            if (names.TryGetValue(t.Name, out var o2)) res.Add(new Issue(Severity.Warning, $"{W}: a second trigger of this name (SetTrigger by name finds the first)", i));
            else names[t.Name] = i;
            try { TriggerExpr.Compile(t.Expr, t.Conditions.Count); }
            catch (FormatException e) { res.Add(new Issue(Severity.Error, $"{W}: the expression \"{t.Expr}\": {e.Message}", i)); }
            foreach (System.Text.RegularExpressions.Match em in System.Text.RegularExpressions.Regex.Matches(t.Expr, @"\d+"))
                if (int.Parse(em.Value, System.Globalization.CultureInfo.InvariantCulture) is var en && (en < 1 || en > t.Conditions.Count))
                    res.Add(new Issue(Severity.Warning, $"{W}: the expression \"{t.Expr}\" names condition {en}, the trigger has {t.Conditions.Count} (always false)", i));
            if (t.Conditions.Count == 0) res.Add(new Issue(Severity.Warning, $"{W}: no conditions - it never fires", i, true));
            foreach (var cond in new[] { true, false })
            {
                var list = t.List(cond);
                for (int k = 0; k < list.Count; k++)
                {
                    var n = list[k];
                    string N = $"{W} {(cond ? "condition" : "action")} {k + 1}";
                    void Add(Severity s, string msg, string? p = null) => res.Add(new Issue(s, $"{N}: {msg}", i, cond, k, p));
                    if (n.IsCode)
                    {
                        if (n.Form == NodeForm.Script && cond) Add(Severity.Error, "Script is an action, not a condition");
                        if (n.Form == NodeForm.Check && !cond) Add(Severity.Error, "Check is a condition, not an action");
                        if (LuaSyntax(n.Source, true) is { } err) Add(Severity.Error, "Lua syntax: " + err);
                        continue;
                    }
                    var ts = TriggerCatalog.Find(n.Code, cond);
                    if (ts == null)
                    {
                        Add(Severity.Error, TriggerCatalog.Find(n.Code, !cond) is { } wrong
                            ? $"{wrong.Name} is {(cond ? "an action" : "a condition")}, not {(cond ? "a condition" : "an action")}"
                            : $"unknown type {n.Ctor} (the engine ignores it)");
                        continue;
                    }
                    foreach (var p in n.Params)
                    {
                        if (p.Key is "note") continue;
                        var spec = ts.Param(p.Key);
                        if (spec == null) { if (!(p.Key == "difficulty" && !cond)) Add(Severity.Warning, $"{ts.Name} has no parameter {p.Key}", p.Key); continue; }
                        var v = p.Value.Value;
                        if (v == null || v.Contains("$(")) continue;          // (an expression or a variable: known when it runs)
                        string? miss = spec.Type switch
                        {
                            PType.Region when !None(v) && v != "UniqueWorldRegion" && m.Region(v) == null => $"no region {v} on the map (the whole map is used)",
                            PType.Object when !None(v) && !objGuids.Contains(v) && m.Group(v) == null && m.Region(v) == null => $"no object or group {v} on the map",
                            PType.ObjectName when !None(v) && !objNames.Contains(v) && m.Group(v) == null => $"no object or group named {v}",
                            PType.Group when !None(v) && m.Group(v) == null => $"no group {v}",
                            PType.Variable when cond && v.Length > 0 && !m.Variables.Any(x => x.Name == v) && !setVars.Contains(v) && n.Value("local") is "1" or null => $"no variable {v} (not declared, no SetVar sets it)",
                            PType.Quest or PType.QuestName when !None(v) && m.Quest(v) == null => $"no quest {v}",
                            PType.Trigger or PType.TriggerName when v.Length > 0 && m.Triggers.Find(v) == null => $"no trigger {v} (SetTrigger does nothing)",
                            PType.Folder when v.Length > 0 && !folders.Contains(v.TrimEnd('/')) => $"no trigger folder {v}",
                            PType.TextKey when v.Length > 0 && !texts.Contains(v.Split('\t')[0]) && v.Split('\t')[0].StartsWith('_') => $"no text {v.Split('\t')[0]} in the map's texts",
                            PType.Sequence when v.Length > 0 && seqs.Count > 0 && !seqs.Contains(Norm(v)) => $"no sequence {v} in the mission",
                            PType.Dialog when v.Length > 0 && dlgs.Count > 0 && !dlgs.Contains(Norm(v)) => $"no dialogue {v} in the mission",
                            PType.QuestionMark when !None(v) && !m.QuestionMarks.Any(q => q.Guid == v || q.Name == v) && !objGuids.Contains(v) && !objNames.Contains(v) => $"no question mark {v}",
                            _ => null,
                        };
                        if (miss != null) Add(Severity.Warning, $"{p.Key}: {miss}", p.Key);
                    }
                }
            }
        }
        if (m.Custom != null && LuaSyntax(m.Custom) is { } ce) res.Add(new Issue(Severity.Error, "custom.lua: Lua syntax: " + ce));
        // the whole file as the game loads it (the standard library's constructors, the trigger checks of LuaMission)
        if (stdDir != null && !res.Any(x => x.Severity == Severity.Error))
            foreach (var e in LoadErrors(m.Triggers.Write() + "\n" + (m.Custom ?? ""), stdDir)) res.Add(new Issue(Severity.Error, "the triggers do not load: " + e));
        return res;
    }

    /// <summary>the check of the map editor - the same everywhere (the trigger editor's list and Check button, the
    /// Triggers group's panel, the check before Test map): every problem, and whether the whole file loads as the game
    /// loads it (the standard library at LuaMission.DefaultStdDir)</summary>
    public static List<Issue> CheckAll(MissionDoc m) => Check(m, LuaMission.DefaultStdDir());

    // the load check of the last texts (loading a big mission's triggers.lua takes a moment; the panels ask often - the
    // same text is loaded once)
    static readonly object s_lock = new();
    static string? s_lastSource, s_lastStd;
    static List<string> s_lastErrors = new();

    /// <summary>the errors of loading triggers.lua + custom.lua as the game does (LuaMission.FromSource); empty: it loads</summary>
    public static List<string> LoadErrors(string source, string stdDir)
    {
        lock (s_lock) if (s_lastSource == source && s_lastStd == stdDir) return s_lastErrors.ToList();
        var lm = LuaMission.FromSource(source, stdDir);
        var errs = lm.Ok ? new List<string>() : lm.Lua.Errors.Take(5).ToList();
        if (!lm.Ok && errs.Count == 0) errs.Add("the file does not load (the log has the reason)");
        lock (s_lock) { s_lastSource = source; s_lastStd = stdDir; s_lastErrors = errs; }
        return errs.ToList();
    }
}
