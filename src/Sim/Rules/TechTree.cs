// The original tech tree, evaluated at runtime.
//
// ParaWorld keeps (almost) all rules in Scripts/Server/settings/Techtree/_TechTree.ttree:
//   StartTT  - the initial values: /Objects/<Tribe>/<Type>/<class>, /Actions/<Tribe>/Build|Upgrades|Moves/...,
//              /Modifications/<Tribe>/<Type>/<Stat>/{tec,res,nat}_{rel,abs}
//   Filters  - named sets of "Modificators" { op, path, value } that are switched on and off by the game:
//              Filters/<Tribe>/Upgrades/<location>/<action>   when an upgrade/invention completes
//              Filters/<Tribe>/BuildObjects/<building>        while a building of that class exists
//              Filters/<Tribe>/Upgrades/<class>/Lvl<N>_...    level filters of units (FightingObj.SetLevelFilter):
//                   names containing "_Bonus" apply to the unit itself, the others to its owner
//              Filters/<Tribe>/Upgrades/<class>/Chief_Bonus   while that unit is at level 5 ("chief")
// A player's tree = StartTT + its enabled filters (applied in priority order). The tree is rebuilt whenever a
// filter is switched, and everything else reads its values from it (see Rules.cs).
//
// Values are kept as the original strings; use Num()/Bool() to read them.
//
// Ported from remake/src/game/techtree.js (pw-exporter 98c7703).
//
// C# notes: the JS works on plain JSON objects. TtNode is that object: a leaf holds a string, an object node holds
// ordered children with the key order of a JS object (integer-like keys first in ascending order, then the others
// in insertion order) - several rules depend on that order (the first weapon of equal value wins, the order of a
// filter's modificators, of an action's locations). An object's own value is its child "_value", as in the JS.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace PwRemake.Sim.Rules;

/// <summary>A node of the tech tree: a string leaf, or an object with ordered children (JS object semantics).</summary>
public sealed class TtNode
{
    /// <summary>The string of a leaf (null for an object node).</summary>
    public readonly string? Leaf;
    readonly List<string>? _keys;
    readonly Dictionary<string, TtNode>? _map;
    int _intKeys;                    // number of integer-like keys at the front of _keys
    /// <summary>a node of the shared original data (TechTreeBase: the start tree, the filters): never changed - a tree
    /// built from it shares its nodes and copies a node only where a modificator writes (TechTree.Rebuild, Thaw)</summary>
    public bool Frozen { get; private set; }

    public TtNode(string leaf) { Leaf = leaf; }
    public TtNode() { _keys = new List<string>(); _map = new Dictionary<string, TtNode>(StringComparer.Ordinal); }

    public bool IsObject => _map != null;
    public int Count => _keys?.Count ?? 0;
    /// <summary>Keys in JS order (Object.keys).</summary>
    public IReadOnlyList<string> Keys => (IReadOnlyList<string>?)_keys ?? Array.Empty<string>();

    /// <summary>child by key (null = undefined, also on a leaf: JS `"str"[key]` of a non-index key)</summary>
    public TtNode? this[string key]
    {
        get => _map != null && _map.TryGetValue(key, out var n) ? n : null;
        set
        {
            if (_map == null) throw new InvalidOperationException("TtNode: not an object");
            if (Frozen) throw new InvalidOperationException("TtNode: a node of the shared original data is never changed (Thaw it)");
            if (value == null) { Remove(key); return; }
            if (_map.ContainsKey(key)) { _map[key] = value; return; }
            _map[key] = value;
            if (IsIndexKey(key, out uint ik))
            {
                int i = 0;
                while (i < _intKeys && uint.Parse(_keys![i], CultureInfo.InvariantCulture) < ik) i++;
                _keys!.Insert(i, key);
                _intKeys++;
            }
            else _keys!.Add(key);
        }
    }

    public bool Has(string key) => _map != null && _map.ContainsKey(key);

    public bool Remove(string key)
    {
        if (Frozen && _map != null && _map.ContainsKey(key)) throw new InvalidOperationException("TtNode: a node of the shared original data is never changed (Thaw it)");
        if (_map == null || !_map.Remove(key)) return false;
        int i = _keys!.IndexOf(key);
        _keys.RemoveAt(i);
        if (i < _intKeys) _intKeys--;
        return true;
    }

    /// <summary>The (key, child) pairs in JS order.</summary>
    public IEnumerable<KeyValuePair<string, TtNode>> Entries()
    {
        if (_keys == null) yield break;
        foreach (var k in _keys) yield return new(k, _map![k]);
    }

    /// <summary>scalar(v): the string of a leaf, the "_value" of an object (null = undefined).</summary>
    public string? Scalar => IsObject ? this["_value"]?.Leaf : Leaf;

    /// <summary>this node and everything under it made read-only (the shared original data)</summary>
    public void Freeze()
    {
        if (Frozen || _map == null) return;
        Frozen = true;
        foreach (var c in _map.Values) c.Freeze();
    }

    /// <summary>a changeable copy of this one level (the children stay shared): copy on write of a frozen node</summary>
    public TtNode Thaw()
    {
        if (!Frozen) return this;
        var c = new TtNode();
        c._keys!.AddRange(_keys!);
        c._intKeys = _intKeys;
        foreach (var k in _keys!) c._map![k] = _map![k];
        return c;
    }

    /// <summary>structuredClone</summary>
    public TtNode Clone()
    {
        if (!IsObject) return this;              // leaves are immutable: shared
        var c = new TtNode();
        c._keys!.AddRange(_keys!);
        c._intKeys = _intKeys;
        foreach (var k in _keys!) c._map![k] = _map![k].Clone();
        return c;
    }

    /// <summary>A canonical array index ("0" .. "4294967294", no leading zeros): such keys come first in JS objects.</summary>
    public static bool IsIndexKey(string k, out uint v)
    {
        v = 0;
        if (k.Length == 0 || k.Length > 10) return false;
        if (k.Length > 1 && k[0] == '0') return false;
        foreach (char ch in k) if (ch < '0' || ch > '9') return false;
        if (!ulong.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u) || u >= 4294967295UL) return false;
        v = (uint)u;
        return true;
    }

    public static TtNode FromJson(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
            {
                var t = new TtNode();
                foreach (var (k, v) in o) t[k] = FromJson(v);
                return t;
            }
            case JsonArray a:
            {
                var t = new TtNode();
                for (int i = 0; i < a.Count; i++) t[i.ToString(CultureInfo.InvariantCulture)] = FromJson(a[i]);
                return t;
            }
            case JsonValue v:
                if (v.TryGetValue(out string? s)) return new TtNode(s ?? "");
                if (v.TryGetValue(out double d)) return new TtNode(TT.JsString(d));
                if (v.TryGetValue(out bool b)) return new TtNode(b ? "true" : "false");
                return new TtNode(v.ToJsonString());
            default:
                return new TtNode("null");
        }
    }

    public JsonNode ToJson()
    {
        if (!IsObject) return JsonValue.Create(Leaf)!;
        var o = new JsonObject();
        foreach (var (k, v) in Entries()) o[k] = v.ToJson();
        return o;
    }

    public override string ToString() => IsObject ? $"{{{Count} keys}}" : Leaf ?? "";
}

/// <summary>A modificator of a filter: { op, path, value }.</summary>
public readonly record struct TtMod(string Op, string Path, TtNode? Value);

/// <summary>The helpers of techtree.js: num(), bool(), nodeAt(), scalar(), applyMod(), modsOf().</summary>
public static class TT
{
    public static readonly string[] TRIBES = { "Hu", "Aje", "Ninigi", "SEAS" };

    /// <summary>num(v, d): parseFloat(v) if finite, else d.</summary>
    public static double Num(string? v, double d = 0)
    {
        if (v == null) return d;
        return ParseFloat(v, out double x) && double.IsFinite(x) ? x : d;
    }

    /// <summary>Math.round of JS: halves round up (towards +infinity).</summary>
    public static double JsRound(double x) => Math.Floor(x + 0.5);

    /// <summary>bool(v): "1" or "true".</summary>
    public static bool Bool(string? v) => v == "1" || v == "true";

    /// <summary>JS parseFloat: the longest leading decimal number (after white space), "Infinity" too.</summary>
    public static bool ParseFloat(string s, out double v)
    {
        v = double.NaN;
        int i = 0, n = s.Length;
        while (i < n && IsJsSpace(s[i])) i++;
        int st = i;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        if (string.CompareOrdinal(s, i, "Infinity", 0, 8) == 0)
        {
            v = s[st] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }
        int ds = i; bool digits = false;
        while (i < n && char.IsAsciiDigit(s[i])) { i++; digits = true; }
        if (i < n && s[i] == '.') { i++; while (i < n && char.IsAsciiDigit(s[i])) { i++; digits = true; } }
        if (!digits) return false;
        int end = i;
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < n && (s[j] == '+' || s[j] == '-')) j++;
            int ej = j;
            while (j < n && char.IsAsciiDigit(s[j])) j++;
            if (j > ej) end = j;
        }
        _ = ds;
        return double.TryParse(s.AsSpan(st, end - st), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    static bool IsJsSpace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f' || c == ' ' || c == '﻿' || char.IsWhiteSpace(c);

    /// <summary>String(x) of a JS number.</summary>
    public static string JsString(double x)
    {
        if (double.IsNaN(x)) return "NaN";
        if (double.IsPositiveInfinity(x)) return "Infinity";
        if (double.IsNegativeInfinity(x)) return "-Infinity";
        if (x == 0) return "0";
        string r = x.ToString("R", CultureInfo.InvariantCulture);
        bool neg = r[0] == '-';
        if (neg) r = r.Substring(1);
        int e = 0;
        int ei = r.IndexOfAny(new[] { 'E', 'e' });
        if (ei >= 0) { e = int.Parse(r.AsSpan(ei + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); r = r.Substring(0, ei); }
        int dot = r.IndexOf('.');
        string digits = dot >= 0 ? r.Remove(dot, 1) : r;
        int point = (dot >= 0 ? dot : r.Length) + e;          // decimal point position after the first `point` digits
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; point--; }
        digits = digits.Substring(lead).TrimEnd('0');
        if (digits.Length == 0) return "0";
        int k = digits.Length, nn = point;
        string res;
        if (k <= nn && nn <= 21) res = digits + new string('0', nn - k);
        else if (0 < nn && nn <= 21) res = digits.Substring(0, nn) + "." + digits.Substring(nn);
        else if (-6 < nn && nn <= 0) res = "0." + new string('0', -nn) + digits;
        else
        {
            int ex = nn - 1;
            res = (k == 1 ? digits : digits[0] + "." + digits.Substring(1)) + "e" + (ex >= 0 ? "+" : "-") + Math.Abs(ex).ToString(CultureInfo.InvariantCulture);
        }
        return neg ? "-" + res : res;
    }

    // (the parts of a path, kept: the rules are read with the same few thousand paths many times a frame)
    static readonly Dictionary<string, string[]> s_parts = new(StringComparer.Ordinal);
    static readonly object s_lock = new();
    public static string[] Split(string path)
    {
        lock (s_lock)
        {
            if (s_parts.TryGetValue(path, out var p)) return p;
            p = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (s_parts.Count < 200000) s_parts[path] = p;
            return p;
        }
    }

    /// <summary>nested lookup (null = undefined)</summary>
    public static TtNode? NodeAt(TtNode? root, string path)
    {
        var n = root;
        foreach (var k in Split(path))
        {
            if (n == null || !n.IsObject) return null;
            n = n[k];
        }
        return n;
    }

    static TtNode? ParentOf(TtNode root, string[] parts, bool create)
    {
        var n = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var k = parts[i];
            var c = n[k];
            if (c == null || !c.IsObject)
            {
                if (!create) return null;
                var o = new TtNode();
                if (c != null) o["_value"] = c;
                n[k] = o;
                c = o;
            }
            else if (c.Frozen) { c = c.Thaw(); n[k] = c; }     // (copy on write: the shared original stays as it is)
            n = c;
        }
        return n;
    }

    /// <summary>scalar value of a node (a node with children keeps its own value in _value)</summary>
    public static string? Scalar(TtNode? v) => v?.Scalar;

    /// <summary>apply one modificator to a tree (StartTT-shaped object whose top level holds Objects/Actions/Modifications)</summary>
    public static void ApplyMod(TtNode root, TtMod mod)
    {
        var parts = Split(mod.Path ?? "");
        if (parts.Length == 0) return;
        string last = parts[^1];
        string op = mod.Op;
        if (op == "remove") { var p0 = ParentOf(root, parts, false); p0?.Remove(last); return; }
        if (op == "invisible")
        {       // AntiActions: hide an action
            var p1 = ParentOf(root, parts, false);
            var t = p1?[last];
            if (t != null && t.IsObject) { if (t.Frozen) p1![last] = t = t.Thaw(); t["visibility"] = new TtNode("0"); }
            return;
        }
        var p = ParentOf(root, parts, true)!;
        var cur = p[last];
        var val = mod.Value;
        void SetScalar(string x)
        {
            if (cur != null && cur.IsObject) { if (cur.Frozen) p[last] = cur = cur.Thaw(); cur["_value"] = new TtNode(x); }   // (copy on write)
            else p[last] = new TtNode(x);
        }
        switch (op)
        {
            case "add": SetScalar(JsString(Num(Scalar(cur)) + Num(Scalar(val)))); break;
            case "multiply": SetScalar(JsString(Num(Scalar(cur), 0) * Num(Scalar(val), 1))); break;
            default:   // replace, append
                if (val != null && val.IsObject) p[last] = val.Clone();
                else SetScalar(val?.Leaf ?? "undefined");
                break;
        }
    }

    /// <summary>every modificator of a filter node, in declaration order</summary>
    public static List<TtMod> ModsOf(TtNode? filter)
    {
        var res = new List<TtMod>();
        var m = filter?["Modificators"];
        if (m == null || !m.IsObject) return res;
        foreach (var (_, x) in m.Entries())
        {
            if (!x.IsObject) continue;
            var path = x["path"];
            if (path == null) continue;
            string? ps = path.IsObject ? null : path.Leaf;
            if (string.IsNullOrEmpty(ps)) continue;       // (x.path truthy)
            res.Add(new TtMod(Scalar(x["op"]) ?? "", ps, x["value"]));
        }
        return res;
    }
}

/// <summary>Shared, read-only original data.</summary>
public sealed class TechTreeBase
{
    public readonly TtNode Start;
    public readonly TtNode Filters;
    readonly Dictionary<string, TtNode?> _filterCache = new(StringComparer.Ordinal);

    public TechTreeBase(TtNode json)
    {
        Start = json["StartTT"] ?? new TtNode();
        Filters = json["Filters"] ?? new TtNode();
        // read-only from now on: the players' trees share its nodes (TechTree.Rebuild)
        Start.Freeze(); Filters.Freeze();
    }

    static string Strip(string path) => path.StartsWith("/Filters/", StringComparison.Ordinal) ? path.Substring(9)
        : path.StartsWith("Filters/", StringComparison.Ordinal) ? path.Substring(8) : path;

    public TtNode? Filter(string path)
    {
        lock (_filterCache)
        {
            if (_filterCache.TryGetValue(path, out var f)) return f;
            f = TT.NodeAt(Filters, Strip(path));
            _filterCache[path] = f;
            return f;
        }
    }

    /// <summary>all filter paths under a prefix (e.g. "Hu/Upgrades/hu_warrior/Lvl3")</summary>
    public List<string> FiltersUnder(string prefix)
    {
        var parts = TT.Split(Strip(prefix));
        var res = new List<string>();
        if (parts.Length == 0) return res;
        string head = string.Join("/", parts, 0, parts.Length - 1), tail = parts[^1];
        var parent = TT.NodeAt(Filters, head);
        if (parent == null || !parent.IsObject) return res;
        foreach (var k in parent.Keys) if (k.StartsWith(tail, StringComparison.Ordinal)) res.Add(head + "/" + k);
        return res;
    }
}

/// <summary>One player's tree (or the neutral "World" tree for wild animals).</summary>
public class TechTree
{
    public readonly TechTreeBase Base;
    public readonly string Tribe;
    // filter path -> count (a building filter stays on while any such building exists); JS Map: insertion order
    protected readonly List<string> EnabledOrder = new();
    protected readonly Dictionary<string, int> Enabled = new(StringComparer.Ordinal);
    public int Version;
    readonly List<Action<TechTree>> _listeners = new();
    public TtNode Root = new();
    Dictionary<string, object?> _cache = new(StringComparer.Ordinal);
    int _held;
    bool _dirty;

    public TechTree(TechTreeBase b, string tribe)
    {
        Base = b;
        Tribe = tribe;
        Rebuild();
    }

    public void OnChange(Action<TechTree> fn) => _listeners.Add(fn);

    /// <summary>the subtrees a player can ever read: own tribe, the heroes ("Special"), the wild world</summary>
    public void Rebuild()
    {
        var P = PwRemake.Sim.World.StepProfile.Current;
        if (P == null) { Rebuild0(); return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), b0 = GC.GetAllocatedBytesForCurrentThread();
        Rebuild0();
        P.Add(this is LocalTree ? "tt:rebuild local" : "tt:rebuild", System.Diagnostics.Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - b0);
    }
    void Rebuild0()
    {
        // the local trees that follow this one are built from its filters: their shared roots are stale now (and would
        // keep megabytes of old trees alive)
        LocalRoots.Clear();
        var shared = SharedRoot();
        if (shared != null)
        {
            Root = shared;
            Version++;
            _cache = new Dictionary<string, object?>(StringComparer.Ordinal); _memo = new();
            foreach (var fn in _listeners) fn(this);
            return;
        }
        var S = Base.Start;
        TtNode Pick(string sec)
        {
            var o = new TtNode();
            var s = S[sec];
            foreach (var t in new[] { Tribe, "Special", "World" })
            {
                var x = s?[t];
                // (shared, not cloned: the modificators copy the nodes they change - ApplyMod / ParentOf. A whole copy of
                // the tribe's tree per rebuild was most of the simulation's garbage in a long game, and its collections'
                // pauses: docs/DEBUG.md "Performance")
                if (s != null && s.IsObject && x != null) o[t] = x;
            }
            return o;
        }
        var root = new TtNode();
        root["Objects"] = Pick("Objects"); root["Actions"] = Pick("Actions"); root["Modifications"] = Pick("Modifications");
        root["MiscValues"] = Pick("MiscValues"); root["TimeFactor"] = Pick("TimeFactor");
        Root = root;
        var list = new List<(string path, TtNode f)>();
        foreach (var p in AllEnabled()) { var f = Base.Filter(p); if (f != null) list.Add((p, f)); }
        // stable sort by priority (Array.prototype.sort is stable)
        var sorted = new List<(string path, TtNode f)>(list.Count);
        sorted.AddRange(System.Linq.Enumerable.OrderBy(list, x => TT.Num(TT.Scalar(x.f["priority"]), 0)));
        foreach (var (_, f) in sorted) foreach (var m in TT.ModsOf(f)) if (Relevant(m.Path)) TT.ApplyMod(Root, m);
        Built(Root);
        Version++;
        _cache = new Dictionary<string, object?>(StringComparer.Ordinal); _memo = new();
        foreach (var fn in _listeners) fn(this);
    }

    public virtual IEnumerable<string> AllEnabled() => EnabledOrder;
    /// <summary>The filters switched on in this tree itself (not a parent's), in the order they were switched on.</summary>
    public IReadOnlyList<string> OwnEnabled => EnabledOrder;
    public int EnabledCount(string path) => Enabled.TryGetValue(path, out int n) ? n : 0;

    public bool Relevant(string path)
    {
        var p = TT.Split(path);
        return p.Length >= 2 && (p[1] == Tribe || p[1] == "Special" || p[1] == "World");
    }

    public void Enable(string path)
    {
        int n = EnabledCount(path);
        Enabled[path] = n + 1;
        if (n == 0) EnabledOrder.Add(path);
        if (n == 0 && Base.Filter(path) != null) Changed();
    }

    public void Disable(string path)
    {
        int n = EnabledCount(path);
        if (n == 0) return;
        if (n == 1)
        {
            Enabled.Remove(path); EnabledOrder.Remove(path);
            if (Base.Filter(path) != null) Changed();
        }
        else Enabled[path] = n - 1;
    }

    // batch: between Suspend() and Resume() filter switches do not rebuild the tree (a campaign map places hundreds of
    // buildings and levelled units per player at once); Resume() rebuilds once if anything changed. Calls nest.
    public void Changed() { if (_held > 0) _dirty = true; else Rebuild(); }
    public void Suspend() => _held++;
    public void Resume() { if (_held > 0 && --_held == 0 && _dirty) { _dirty = false; Rebuild(); } }

    public bool Has(string path) => Enabled.ContainsKey(path);
    public virtual TtNode? Node(string path) => TT.NodeAt(Root, path);
    public string? Get(string path) => TT.Scalar(Node(path));
    public string Get(string path, string d) => Get(path) ?? d;
    public double Num(string path, double d = 0) => TT.Num(Get(path), d);
    public bool Bool(string path) => TT.Bool(Get(path));
    /// <summary>RequirementsMgr.CheckInvention</summary>
    public bool Invented(string name, string? tribe = null)
    {
        if (tribe != null && tribe != Tribe) return Get($"/Objects/{tribe}/InventObjects/{name}/invented") == "1";
        // (asked very often - the epoch of every object every step: an answer per name until the tree changes)
        Fresh();
        if (_invVer != Version) { _inv.Clear(); _invVer = Version; }
        if (_inv.TryGetValue(name, out var b)) return b;
        b = Get($"/Objects/{Tribe}/InventObjects/{name}/invented") == "1";
        _inv[name] = b;
        return b;
    }
    readonly Dictionary<string, bool> _inv = new(StringComparer.Ordinal);
    int _invVer = int.MinValue;
    /// <summary>brings the tree up to date before a cached answer is used (a local tree follows its owner's)</summary>
    protected virtual void Fresh() { }
    /// <summary>a tree built before from the same filters (local trees: LocalTree), else null = build it</summary>
    protected virtual TtNode? SharedRoot() => null;
    protected virtual void Built(TtNode root) { }
    /// <summary>the roots of the local trees that follow this one, by their whole list of filters (LocalTree): objects with
    /// the same local filters (the farms in one mode, every warrior in defensive mode) share one root - it is only read
    /// once built</summary>
    internal readonly Dictionary<string, TtNode> LocalRoots = new(StringComparer.Ordinal);
    public bool Built(string name, string? tribe = null) => Get($"/Objects/{tribe ?? Tribe}/BuildObjects/{name}/build") == "1";

    /// <summary>the key of a memoised value asked very often (Rules: Def, Stats, WeaponStatsOf ...): what (Kind), of
    /// which object / class / level - no string built and no closure per question</summary>
    public readonly record struct MemoKey(int Kind, object? A, string? B, int Level);
    Dictionary<MemoKey, object?> _memo = new();
    /// <summary>a memoised value (the tree brought up to date first, as Memo does)</summary>
    public bool TryMemo(in MemoKey k, out object? v) { Fresh(); return _memo.TryGetValue(k, out v); }
    public T SetMemo<T>(in MemoKey k, T v) { _memo[k] = v; return v; }

    /// <summary>memoised derived values, invalidated on every rebuild</summary>
    public virtual T Memo<T>(string key, Func<T> fn)
    {
        if (_cache.TryGetValue(key, out var v)) return (T)v!;
        var r = fn();
        _cache[key] = r;
        return r;
    }

    /// <summary>GetTechTreeModifier: /Modifications/&lt;tribe&gt;/&lt;TYPE&gt;/&lt;stat&gt;/&lt;caste&gt;_{rel|abs}.
    /// Buildings, animals and vehicles always read the "tec" column; characters read their own caste (res/nat/tec).</summary>
    public double Modifier(string type, string stat, bool relative, string col = "tec", string? tribe = null) =>
        Num($"/Modifications/{tribe ?? Tribe}/{type}/{stat}/{col}_{(relative ? "rel" : "abs")}", relative ? 1 : 0);

    /// <summary>value * rel + abs in one call</summary>
    public double Modify(string type, string stat, double v, string col = "tec", string? tribe = null) =>
        v * Modifier(type, stat, true, col, tribe) + Modifier(type, stat, false, col, tribe);
}

/// <summary>
/// A tree of its own for one object (the original's "unit-local" tech tree): the owner's tree plus filters that
/// were enabled for this object only - e.g. the Aje farm modes, the big tent, the Ninigi "Explode" upgrade.
/// It follows the owner's tree: whenever that changes, this one is rebuilt on the next read.
/// </summary>
public sealed class LocalTree : TechTree
{
    public readonly TechTree? Parent;
    int _parentVersion = -1;

    public LocalTree(TechTree parent) : base(parent.Base, parent.Tribe)
    {
        Parent = parent;
        Sync();
    }

    public override IEnumerable<string> AllEnabled()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var res = new List<string>();
        if (Parent != null) foreach (var k in Parent.AllEnabled()) if (seen.Add(k)) res.Add(k);
        foreach (var k in EnabledOrder) if (seen.Add(k)) res.Add(k);
        return res;
    }

    public void Sync()
    {
        if (Parent != null && _parentVersion != Parent.Version) { _parentVersion = Parent.Version; Rebuild(); }
    }

    public override TtNode? Node(string path) { Sync(); return base.Node(path); }
    protected override void Fresh() => Sync();
    // (the tree is a function of Base, the tribe and the filters in order: the same list -> the same root)
    string _key = "";
    protected override TtNode? SharedRoot()
    {
        if (Parent == null) return null;
        _key = string.Join("\n", AllEnabled());
        return Parent.LocalRoots.TryGetValue(_key, out var r) ? r : null;
    }
    protected override void Built(TtNode root)
    {
        if (Parent == null || _key.Length == 0) return;
        if (Parent.LocalRoots.Count > 16) Parent.LocalRoots.Clear();
        Parent.LocalRoots[_key] = root;
    }
    public override T Memo<T>(string key, Func<T> fn) { Sync(); return base.Memo(key, fn); }
}
