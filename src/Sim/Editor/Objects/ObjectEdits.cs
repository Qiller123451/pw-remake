// The map editor's object edits (docs/MAP_EDITOR.md "Objects", plan phase 3): placing, deleting, moving, turning and
// changing the map's objects (MapObject: type tag, class, name, map position x / y and height z, heading, the stored
// quaternion, owner, level, attributes) as undoable commands of the EditHistory, and the operations the object tools
// build them with: a new object of a palette class, turning (the heading and the quaternion together: tilted
// landscape pieces keep their tilt and turn about the map's up axis), sitting on the ground, copy / paste / duplicate.
//
// The object's fields as the game reads them (MapSource.OriginalSource): Owner = the start slot (head quarters index)
// whose player gets it (null: nobody's); Level 0-based (MapOwned.Level = Level + 1); attributes as the original's -
// "hitpoints" the amount of a stone / bush / fish shoal, a nest's spawn_type / spawn_max / spawn_rate / advance_time,
// an item spawn's spawn_items - and the remake's "hp_percent" (hit points of a unit or building at the start, %).
// No Godot types.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

/// <summary>everything of an object an edit can change (a snapshot to undo to)</summary>
public sealed record ObjState(string Type, string Cls, string Name, double X, double Y, double Z, double Rot, double[] Q, int? Owner, int Level, Dictionary<string, string> Attr)
{
    public static ObjState Of(MapObject o) => new(o.Type, o.Cls, o.Name, o.X, o.Y, o.Z, o.Rot, (double[])o.Q.Clone(), o.Owner, o.Level, new Dictionary<string, string>(o.Attr));
    public void ApplyTo(MapObject o)
    {
        o.Type = Type; o.Cls = Cls; o.Name = Name; o.X = X; o.Y = Y; o.Z = Z; o.Rot = Rot; o.Q = (double[])Q.Clone(); o.Owner = Owner; o.Level = Level;
        o.Attr = new Dictionary<string, string>(Attr);
    }
}

/// <summary>a change of objects (move, turn, owner, level, attributes ...): each one's state before and after. A drag's
/// steps merge into one undo step (Mergeable, the same objects)</summary>
public sealed class ObjectsEdit : IEditCommand
{
    public string Name { get; }
    public readonly List<(MapObject O, ObjState Before, ObjState After)> Items = new();
    /// <summary>the steps of one drag (moving, turning with the wheel held) merge</summary>
    public bool Mergeable;

    public ObjectsEdit(string name) { Name = name; }

    /// <summary>an edit made by changing the objects with f (they are changed back: the history does it)</summary>
    public static ObjectsEdit? Make(string name, IEnumerable<MapObject> objs, Action<MapObject> f, bool mergeable = false)
    {
        var e = new ObjectsEdit(name) { Mergeable = mergeable };
        foreach (var o in objs.Distinct())
        {
            var before = ObjState.Of(o);
            f(o);
            var after = ObjState.Of(o);
            before.ApplyTo(o);
            if (!Same(before, after)) e.Items.Add((o, before, after));
        }
        return e.Items.Count > 0 ? e : null;
    }

    static bool Same(ObjState a, ObjState b) =>
        a.Type == b.Type && a.Cls == b.Cls && a.Name == b.Name && a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.Rot == b.Rot && a.Q.SequenceEqual(b.Q)
        && a.Owner == b.Owner && a.Level == b.Level && a.Attr.Count == b.Attr.Count && a.Attr.All(kv => b.Attr.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public void Do(IEditorMapDoc doc) { foreach (var (o, _, a) in Items) a.ApplyTo(o); doc.ObjectsChanged(); }
    public void Undo(IEditorMapDoc doc) { foreach (var (o, b, _) in Items) b.ApplyTo(o); doc.ObjectsChanged(); }

    public bool TryMerge(IEditCommand next)
    {
        if (next is not ObjectsEdit n || !Mergeable || !n.Mergeable || n.Name != Name) return false;
        // the same objects: the first state before, the last after (an object the next step did not change keeps its own)
        var mine = Items.Select(i => i.O).ToHashSet();
        if (!n.Items.All(i => mine.Contains(i.O))) return false;
        for (int k = 0; k < Items.Count; k++)
        {
            var it = Items[k];
            var m = n.Items.FirstOrDefault(i => i.O == it.O);
            if (m.O != null) Items[k] = (it.O, it.Before, m.After);
        }
        return true;
    }
}

/// <summary>objects put on the map or taken off it (with their places in the object list, so an undo puts them back
/// where they were)</summary>
public sealed class ObjectsAddRemove : IEditCommand
{
    public string Name { get; }
    readonly bool _add;
    /// <summary>(index in the document's list, object), ascending by index</summary>
    readonly List<(int Index, MapObject O)> _items;

    ObjectsAddRemove(string name, bool add, List<(int, MapObject)> items) { Name = name; _add = add; _items = items; }

    /// <summary>new objects, appended to the map's list</summary>
    public static ObjectsAddRemove Add(IEditorMapDoc doc, IEnumerable<MapObject> objs, string name = "Place objects")
    {
        int n = doc.Objects.Count;
        return new ObjectsAddRemove(name, true, objs.Select((o, k) => (n + k, o)).ToList());
    }

    /// <summary>objects of the map to take off</summary>
    public static ObjectsAddRemove? Remove(IEditorMapDoc doc, IEnumerable<MapObject> objs, string name = "Delete objects")
    {
        var set = objs.ToHashSet();
        var items = doc.Objects.Select((o, i) => (i, o)).Where(x => set.Contains(x.o)).ToList();
        return items.Count > 0 ? new ObjectsAddRemove(name, false, items) : null;
    }

    public IReadOnlyList<MapObject> Objects => _items.Select(i => i.O).ToList();

    void Insert(IEditorMapDoc doc) { foreach (var (i, o) in _items) doc.InsertObject(Math.Min(i, doc.Objects.Count), o); doc.ObjectsChanged(); }
    void Take(IEditorMapDoc doc) { for (int k = _items.Count - 1; k >= 0; k--) doc.RemoveObject(_items[k].O); doc.ObjectsChanged(); }

    public void Do(IEditorMapDoc doc) { if (_add) Insert(doc); else Take(doc); }
    public void Undo(IEditorMapDoc doc) { if (_add) Take(doc); else Insert(doc); }
    public bool TryMerge(IEditCommand next) => false;
}

/// <summary>the operations of the object tools</summary>
public static class ObjectOps
{
    /// <summary>the stored quaternion of a turn by rot about the map's z (MapObject.Rot = -2 atan2(qz, qw))</summary>
    public static double[] QuatOf(double rot) => new[] { 0, 0, Math.Sin(-rot / 2), Math.Cos(-rot / 2) };

    /// <summary>is the stored quaternion more than a turn about z (a tilted landscape piece)</summary>
    public static bool Tilted(MapObject o) => Math.Abs(o.Q[0]) > 1e-4 || Math.Abs(o.Q[1]) > 1e-4;

    /// <summary>sets the heading (map rot); a tilted piece is turned about the map's up axis by the difference</summary>
    public static void SetRot(MapObject o, double rot)
    {
        if (Tilted(o)) { Turn(o, rot - o.Rot); return; }
        o.Rot = Norm(rot); o.Q = QuatOf(o.Rot);
    }

    /// <summary>turns an object by d (map rot) about the map's up axis. The world orientation is A * Ry(pi) with
    /// A = (qx, qz, qy, qw) (Frame.MapQuat); turning the world heading by -d is Ry(-d) * A</summary>
    public static void Turn(MapObject o, double d)
    {
        var q = o.Q;
        double n = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        if (n == 0 || double.IsNaN(n)) { q = new double[] { 0, 0, 0, 1 }; n = 1; }
        double ax = q[0] / n, ay = q[2] / n, az = q[1] / n, aw = q[3] / n;
        double s = Math.Sin(-d / 2), c = Math.Cos(-d / 2);              // Ry(-d) = (0, s, 0, c)
        // (0, s, 0, c) * (ax, ay, az, aw)
        double bx = c * ax + s * az, by = c * ay + s * aw, bz = c * az - s * ax, bw = c * aw - s * ay;
        o.Q = new[] { bx, bz, by, bw };
        o.Rot = Norm(-2 * Math.Atan2(o.Q[2], o.Q[3]));
    }

    /// <summary>an angle in -pi .. pi</summary>
    public static double Norm(double a)
    {
        a %= 2 * Math.PI;
        if (a > Math.PI) a -= 2 * Math.PI; else if (a <= -Math.PI) a += 2 * Math.PI;
        return a;
    }

    /// <summary>a new object of a palette class at map (mx, my) on the ground (a fish shoal: in the water), turned by rot
    /// (map), for a player slot (owner; the tribes' classes - null: nobody's) at a level (1-based)</summary>
    public static MapObject Create(PaletteEntry e, IEditorMapDoc doc, double mx, double my, double rot, int? owner, int level, PwRemake.Sim.Rules.Rules? rules = null)
    {
        double z = doc.HeightAt(mx, my);
        if (e.Group == "Fish" && doc.Water is double w) z = Math.Max(z, w);
        var o = new MapObject
        {
            Type = e.Type, Cls = e.Cls, Name = e.Cls, X = mx, Y = my, Z = z, Rot = Norm(rot), Q = QuatOf(Norm(rot)),
            Owner = e.Type == "SLOC" ? owner ?? FreeSlot(doc) : e.Owned ? owner : null, Level = e.MaxLevel > 0 ? Math.Clamp(level, e.MinLevel, e.MaxLevel) - 1 : 0,
        };
        if (e.Amount > 0) o.Attr["hitpoints"] = e.Amount.ToString(CultureInfo.InvariantCulture);
        if (e.Type == "NEST")
        {
            if (rules != null && ObjectPalette.DefaultNestSpecies(rules, e.Cls) is string sp) o.Attr["spawn_type"] = sp;
            o.Attr["spawn_max"] = "3"; o.Attr["spawn_rate"] = "60";
        }
        if (e.Type == "ITSP") { o.Attr["spawn_items"] = ""; o.Attr["random_item"] = "1"; o.Attr["generate_always"] = "0"; }
        return o;
    }

    /// <summary>the first player slot (0..7) without a start location; 7 when all have one</summary>
    public static int FreeSlot(IEditorMapDoc doc)
    {
        var used = doc.Objects.Where(o => o.Type == "SLOC" && o.Owner != null).Select(o => o.Owner!.Value).ToHashSet();
        for (int i = 0; i < MapPlayerSettings.SLOTS; i++) if (!used.Contains(i)) return i;
        return MapPlayerSettings.SLOTS - 1;
    }

    /// <summary>puts an object on the ground at its place (decoration with its own height keeps its height above the ground)</summary>
    public static void MoveTo(MapObject o, IEditorMapDoc doc, double mx, double my, bool keepHeightAboveGround = true)
    {
        double above = keepHeightAboveGround ? o.Z - doc.HeightAt(o.X, o.Y) : 0;
        if (o.Cls.StartsWith("fishshoal", StringComparison.OrdinalIgnoreCase) && doc.Water is double w) above = 0;
        o.X = mx; o.Y = my;
        o.Z = doc.HeightAt(mx, my) + above;
        if (o.Cls.StartsWith("fishshoal", StringComparison.OrdinalIgnoreCase) && doc.Water is double wl) o.Z = Math.Max(o.Z, wl);
    }

    /// <summary>a point snapped to the grid (step metres; 0: none)</summary>
    public static (double X, double Y) Snap(double mx, double my, double step) =>
        step <= 0 ? (mx, my) : (Math.Round(mx / step) * step, Math.Round(my / step) * step);

    /// <summary>the class of an object's name for the triggers made unique on the map: "name", else "name_2", "name_3" ...
    /// (an object named as its class needs no unique name)</summary>
    public static string UniqueName(IEditorMapDoc doc, string name, string cls, MapObject? except = null)
    {
        if (name == cls) return name;
        var names = doc.Objects.Where(o => o != except).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(name)) return name;
        string b = System.Text.RegularExpressions.Regex.Replace(name, @"_[0-9]+\z", "");
        for (int k = 2; ; k++) if (!names.Contains($"{b}_{k}")) return $"{b}_{k}";
    }

    /// <summary>a copy of an object (not on the map)</summary>
    public static MapObject Copy(MapObject o) => new()
    {
        Type = o.Type, Name = o.Name, Cls = o.Cls, X = o.X, Y = o.Y, Z = o.Z, Rot = o.Rot, Q = (double[])o.Q.Clone(), Owner = o.Owner, Level = o.Level,
        Attr = new Dictionary<string, string>(o.Attr),
    };

    /// <summary>the hit points % of a unit or building at the start (the remake's attribute hp_percent; 100 without)</summary>
    public static double HpPercent(MapObject o) => o.Attr.TryGetValue("hp_percent", out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 100;
    public static void SetHpPercent(MapObject o, double p)
    {
        p = Math.Clamp(Math.Round(p), 1, 100);
        if (p >= 100) o.Attr.Remove("hp_percent"); else o.Attr["hp_percent"] = p.ToString(CultureInfo.InvariantCulture);
    }
    /// <summary>a stone's, bush's or fish shoal's amount (the attribute hitpoints, as the original's maps)</summary>
    public static double Amount(MapObject o, double def) => o.Attr.TryGetValue("hitpoints", out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : def;
}

/// <summary>copy / paste of objects: the copies keep their places relative to each other</summary>
public sealed class ObjectClipboard
{
    readonly List<MapObject> _items = new();
    double _cx, _cy;
    public int Count => _items.Count;

    public void Copy(IEnumerable<MapObject> objs)
    {
        _items.Clear();
        _items.AddRange(objs.Select(ObjectOps.Copy));
        if (_items.Count == 0) return;
        _cx = _items.Average(o => o.X); _cy = _items.Average(o => o.Y);
    }

    /// <summary>the copies with their centre at map (mx, my), on the ground there, names unique (not on the map yet)</summary>
    public List<MapObject> PasteAt(IEditorMapDoc doc, double mx, double my)
    {
        var outp = new List<MapObject>();
        var names = doc.Objects.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var slots = doc.Objects.Where(o => o.Type == "SLOC" && o.Owner != null).Select(o => o.Owner!.Value).ToHashSet();
        foreach (var c in _items)
        {
            var o = ObjectOps.Copy(c);
            ObjectOps.MoveTo(o, doc, mx + (c.X - _cx), my + (c.Y - _cy));
            if (o.Name != o.Cls && names.Contains(o.Name))
            {
                string b = System.Text.RegularExpressions.Regex.Replace(o.Name, @"_[0-9]+\z", "");
                for (int k = 2; ; k++) if (!names.Contains($"{b}_{k}")) { o.Name = $"{b}_{k}"; break; }
            }
            names.Add(o.Name);
            if (o.Type == "SLOC")
            {
                int s = 0;
                while (s < MapPlayerSettings.SLOTS - 1 && slots.Contains(s)) s++;
                o.Owner = s; slots.Add(s);
            }
            outp.Add(o);
        }
        return outp;
    }

    /// <summary>where the copies' centre was</summary>
    public (double X, double Y) Centre => (_cx, _cy);
}
