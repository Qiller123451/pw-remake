// Small helpers shared by the trigger engine, its conditions and its actions (remake/docs/spec/triggers.md §1.2,
// §2.3, §3): count expressions, $(variable) substitution, the trigger expression, vectors of trigger parameters.
// Ported from remake/src/game/campaign/trigutil.js (pw-exporter 98c7703); Vec from campaign/setup.js vec().
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Scripting;

/// <summary>A position of a trigger parameter in MAP coordinates (x east, y south, z up; metres from the map's
/// north-west corner) - the coordinates of the mission data. The host converts to game coordinates.</summary>
public readonly record struct MapPos(double X, double Y, double Z)
{
    /// <summary>"[0 0 0]": the editor's "no position".</summary>
    public bool IsZero => X == 0 && Y == 0 && Z == 0;
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"[{X} {Y} {Z}]");
}

public static class TrigUtil
{
    /// <summary>CHelper.GetValueString (ConditionFactory.usl:2715-2744): "$(name)" anywhere in the string = the
    /// variable's value (it replaces the whole string); an unknown variable gives "".</summary>
    public static string ValueString(string? s, Func<string, string?> getVar)
    {
        s ??= "";
        int a = s.IndexOf("$(", StringComparison.Ordinal);
        if (a < 0) return s;
        int b = s.IndexOf(')');
        if (b < a + 2) return s;
        return getVar(s.Substring(a + 2, b - a - 2)) ?? "";
    }

    /// <summary>String.ToInt of the scripts = JS parseInt(String(s).trim(), 10): the leading integer, anything else 0.</summary>
    public static long ToInt(string? s)
    {
        if (s == null) return 0;
        s = s.Trim();
        int i = 0;
        bool neg = false;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) { neg = s[i] == '-'; i++; }
        int start = i;
        long v = 0;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            if (v < long.MaxValue / 10) v = v * 10 + (s[i] - '0');
            i++;
        }
        if (i == start) return 0;
        return neg ? -v : v;
    }

    /// <summary>JS parseFloat with a default for NaN.</summary>
    public static double Num(string? s, double def = 0) => Json.TryNum(s, out var d) && double.IsFinite(d) ? d : def;

    /// <summary>JS `on(v, d)` of actions.js: missing / empty = d, else "1" / "true" = true.</summary>
    public static bool On(string? v, bool def = false) => string.IsNullOrEmpty(v) ? def : v == "1" || v == "true";

    /// <summary>CHelper.Compare (ConditionFactory.usl:2754-2781): a bare number (or empty = 0) means >=.</summary>
    public static bool Compare(long a, string? s, Func<string, string?> getVar)
    {
        s = (s ?? "").Trim();
        long Val(string r) => ToInt(ValueString(r, getVar));
        if (s.StartsWith(">=", StringComparison.Ordinal)) return a >= Val(s[2..]);
        if (s.StartsWith('>')) return a > Val(s[1..]);
        if (s.StartsWith("<=", StringComparison.Ordinal)) return a <= Val(s[2..]);
        if (s.StartsWith('<')) return a < Val(s[1..]);
        if (s.StartsWith("==", StringComparison.Ordinal)) return a == Val(s[2..]);
        if (s.StartsWith("!=", StringComparison.Ordinal)) return a != Val(s[2..]);
        if (s.StartsWith('=')) return a == Val(s[1..]);
        return a >= Val(s);
    }

    /// <summary>File part of a sequence / dialogue path, compared without case.</summary>
    public static string FilePart(string? p)
    {
        var s = (p ?? "").Replace('\\', '/');
        int i = s.LastIndexOf('/');
        return (i >= 0 ? s[(i + 1)..] : s).ToLowerInvariant();
    }

    static readonly Regex NumRx = new(@"-?\d+(?:\.\d+)?(?:e-?\d+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"[x y z]", "x y z" or "x, y, z" of a trigger parameter -> map position, or null (fewer than two
    /// numbers). setup.js vec().</summary>
    public static MapPos? Vec(string? s)
    {
        var m = NumRx.Matches(s ?? "");
        if (m.Count < 2) return null;
        double P(int i) => double.Parse(m[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return new MapPos(P(0), P(1), m.Count > 2 ? P(2) : 0);
    }

    /// <summary>A number as the JS engine writes it into a string (String(n)): integers without a decimal point.</summary>
    public static string NumStr(double d)
    {
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Lines of a list parameter ("a\nb;c"): trimmed, empty and "NA" dropped (actions.js lines()).</summary>
    public static List<string> Lines(string? s)
    {
        var o = new List<string>();
        foreach (var x in (s ?? "").Split('\n', ';')) { var t = x.Trim(); if (t.Length > 0 && t != "NA") o.Add(t); }
        return o;
    }

    /// <summary>GUID lists (one per line, or separated by blanks , ; |): actions.js guids().</summary>
    public static List<string> Guids(string? s)
    {
        var o = new List<string>();
        foreach (var x in Regex.Split(s ?? "", @"[\s,;|]+")) if (x.Length > 0 && x != "NA") o.Add(x);
        return o;
    }
}

/// <summary>The trigger expression: condition numbers (1-based), && || ! and parentheses (also single & |).
/// Empty = the AND of all conditions (§1.2); no condition at all = never. Compiled once into a tree.</summary>
public abstract class TriggerExpr
{
    public abstract bool Eval(Func<int, bool> state);

    sealed class Never : TriggerExpr { public override bool Eval(Func<int, bool> s) => false; }
    sealed class All : TriggerExpr { public int N; public override bool Eval(Func<int, bool> s) { for (int i = 0; i < N; i++) if (!s(i)) return false; return true; } }
    sealed class Ref : TriggerExpr { public int K; public int N; public override bool Eval(Func<int, bool> s) => K >= 0 && K < N && s(K); }
    sealed class Not : TriggerExpr { public TriggerExpr A = null!; public override bool Eval(Func<int, bool> s) => !A.Eval(s); }
    sealed class And : TriggerExpr { public TriggerExpr A = null!, B = null!; public override bool Eval(Func<int, bool> s) => A.Eval(s) && B.Eval(s); }
    sealed class Or : TriggerExpr { public TriggerExpr A = null!, B = null!; public override bool Eval(Func<int, bool> s) => A.Eval(s) || B.Eval(s); }

    static readonly Regex Tok = new(@"\d+|&&|\|\||!|\(|\)|&|\|", RegexOptions.CultureInvariant);

    /// <summary>Compiles; throws FormatException for a bad expression (trigutil.js compileExpression).</summary>
    public static TriggerExpr Compile(string? src, int n)
    {
        src = (src ?? "").Trim();
        if (n == 0) return new Never();
        if (src.Length == 0) return new All { N = n };
        var tok = new List<string>();
        foreach (Match m in Tok.Matches(src)) tok.Add(m.Value);
        int i = 0;
        TriggerExpr Prim()
        {
            string? t = i < tok.Count ? tok[i] : null;
            i++;
            if (t == "!") return new Not { A = Prim() };
            if (t == "(") { var f = OrE(); if (i < tok.Count && tok[i] == ")") i++; return f; }
            if (t == null || !char.IsDigit(t[0])) throw new FormatException($"bad expression \"{src}\"");
            return new Ref { K = (int)TrigUtil.ToInt(t) - 1, N = n };
        }
        TriggerExpr AndE()
        {
            var f = Prim();
            while (i < tok.Count && (tok[i] == "&&" || tok[i] == "&")) { i++; f = new And { A = f, B = Prim() }; }
            return f;
        }
        TriggerExpr OrE()
        {
            var f = AndE();
            while (i < tok.Count && (tok[i] == "||" || tok[i] == "|")) { i++; f = new Or { A = f, B = AndE() }; }
            return f;
        }
        var r = OrE();
        if (i < tok.Count) throw new FormatException($"bad expression \"{src}\"");
        return r;
    }
}

/// <summary>An insertion-ordered set (JS Set semantics): iteration in the order of adding, which keeps the order
/// in which conditions hear an event deterministic.</summary>
public sealed class OrderedSet<T> where T : notnull
{
    readonly Dictionary<T, LinkedListNode<T>> map = new();
    readonly LinkedList<T> list = new();
    public int Count => map.Count;
    public bool Add(T x) { if (map.ContainsKey(x)) return false; map[x] = list.AddLast(x); return true; }
    public bool Remove(T x) { if (!map.Remove(x, out var n)) return false; list.Remove(n); return true; }
    public bool Contains(T x) => map.ContainsKey(x);
    public void Clear() { map.Clear(); list.Clear(); }
    /// <summary>A copy to iterate over while the set may change ([...set] in the JS).</summary>
    public T[] Snapshot() { var a = new T[list.Count]; list.CopyTo(a, 0); return a; }
    /// <summary>The same copy into a list the caller keeps (no allocation per call).</summary>
    public List<T> SnapshotInto(List<T> into) { into.Clear(); into.AddRange(list); return into; }
}
