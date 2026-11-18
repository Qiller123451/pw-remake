// Loose data of the simulation that the JS kept in plain objects: the arguments of delayed calls ({ u, name }), the
// state of periodic effects, the fields of world events ({ type, unit, pos ... }). Args is that object: string keys,
// values that are numbers, strings, booleans, vectors, entities, players or lists of them - data only (no functions),
// so a saved game can write it (entities and players by id).
//
// OrderedSet is the JS Set: iteration in insertion order. The simulation never iterates a .NET HashSet of strings or
// objects where the order matters - their order changes from run to run (string hashes are randomised per process),
// and determinism needs the same order on every machine (docs/ARCHITECTURE.md "Determinism").
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public sealed class Args : IEnumerable<KeyValuePair<string, object?>>
{
    readonly Dictionary<string, object?> _d = new(StringComparer.Ordinal);
    public static Args Empty => new();

    public object? this[string key]
    {
        get => _d.TryGetValue(key, out var v) ? v : null;
        set => _d[key] = value;
    }

    public bool Has(string key) => _d.ContainsKey(key);
    public void Add(string key, object? value) => _d[key] = value;
    public bool Remove(string key) => _d.Remove(key);
    public int Count => _d.Count;

    public T? Get<T>(string key) where T : class => _d.TryGetValue(key, out var v) ? v as T : null;
    public Entity? E(string key) => Get<Entity>(key);
    public Unit? U(string key) => Get<Unit>(key);
    public Building? B(string key) => Get<Building>(key);
    public Player? P(string key) => Get<Player>(key);
    public string? Str(string key) => _d.TryGetValue(key, out var v) ? v as string ?? (v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v?.ToString()) : null;
    public double Num(string key, double d = 0) => _d.TryGetValue(key, out var v) ? v switch { double x => x, int i => i, long l => l, float f => f, bool b => b ? 1 : 0, _ => d } : d;
    public int Int(string key, int d = 0) => (int)Num(key, d);
    public bool Bool(string key) => _d.TryGetValue(key, out var v) && v switch { bool b => b, double x => x != 0, int i => i != 0, null => false, string s => s.Length > 0, _ => true };
    public V3 V(string key) => _d.TryGetValue(key, out var v) && v is V3 p ? p : V3.Zero;

    public Args Clone() { var a = new Args(); foreach (var (k, v) in _d) a._d[k] = v; return a; }
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _d.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _d.GetEnumerator();
    public override string ToString() => string.Join(", ", System.Linq.Enumerable.Select(_d, kv => $"{kv.Key}={kv.Value}"));
}

/// <summary>A set that iterates in insertion order (the JS Set).</summary>
public sealed class OrderedSet<T> : IEnumerable<T> where T : notnull
{
    readonly List<T> _list = new();
    readonly HashSet<T> _set;

    public OrderedSet() { _set = new HashSet<T>(); }
    public OrderedSet(IEqualityComparer<T> cmp) { _set = new HashSet<T>(cmp); }
    public OrderedSet(IEnumerable<T> items) : this() { foreach (var x in items) Add(x); }

    public int Count => _list.Count;
    /// <summary>JS Set.size</summary>
    public int Size => _list.Count;
    public bool Has(T x) => _set.Contains(x);
    public bool Contains(T x) => _set.Contains(x);
    public bool Add(T x) { if (!_set.Add(x)) return false; _list.Add(x); return true; }
    public bool Delete(T x) { if (!_set.Remove(x)) return false; _list.Remove(x); return true; }
    public bool Remove(T x) => Delete(x);
    public void Clear() { _set.Clear(); _list.Clear(); }
    public T this[int i] => _list[i];
    /// <summary>a copy of the items (safe to change the set while going through it)</summary>
    public List<T> ToList() => new(_list);
    public List<T>.Enumerator GetEnumerator() => _list.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _list.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();
}

/// <summary>A map that iterates in insertion order (the JS Map).</summary>
public sealed class OrderedMap<TK, TV> : IEnumerable<KeyValuePair<TK, TV>> where TK : notnull
{
    readonly List<TK> _keys = new();
    readonly Dictionary<TK, TV> _d = new();

    public int Count => _keys.Count;
    public bool Has(TK k) => _d.ContainsKey(k);
    public bool TryGetValue(TK k, out TV v) => _d.TryGetValue(k, out v!);
    public TV? Get(TK k) => _d.TryGetValue(k, out var v) ? v : default;
    public void Set(TK k, TV v) { if (!_d.ContainsKey(k)) _keys.Add(k); _d[k] = v; }
    public bool Delete(TK k) { if (!_d.Remove(k)) return false; _keys.Remove(k); return true; }
    public void Clear() { _keys.Clear(); _d.Clear(); }
    public IReadOnlyList<TK> Keys => _keys;
    public IEnumerable<TV> Values { get { foreach (var k in _keys) yield return _d[k]; } }
    public IEnumerator<KeyValuePair<TK, TV>> GetEnumerator() { foreach (var k in _keys.ToArray()) if (_d.TryGetValue(k, out var v)) yield return new(k, v); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
