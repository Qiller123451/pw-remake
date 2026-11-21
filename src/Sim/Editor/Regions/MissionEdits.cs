// The commands that change the mission data (MissionDoc, docs/MAP_EDITOR.md "Regions and mission data"): every edit of
// the region tool, the mission data panel and the trigger editor is one of these, run through the document's
// EditHistory, so it can be undone and redone. A list edit keeps copies of the entry before and after (an entry is
// small: a region, a quest, one trigger), never the whole data; a drag (moving a region's shape) merges its steps
// into one undo step. No Godot types.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Editor.Triggers;

namespace PwRemake.Sim.Editor.Regions;

/// <summary>an insert, removal or change of one entry of a list of the mission data</summary>
public sealed class ListEdit<T> : IEditCommand where T : class
{
    readonly int _index;
    readonly T? _before;
    T? _after;
    readonly string? _merge;
    public string Name { get; }

    /// <summary>before null: insert after at index; after null: remove the entry at index; both: replace it</summary>
    public ListEdit(string name, int index, T? before, T? after, string? merge = null)
    {
        Name = name; _index = index; _before = before == null ? null : MissionEdits.Copy(before); _after = after == null ? null : MissionEdits.Copy(after); _merge = merge;
    }

    void Apply(IEditorMapDoc doc, T? from, T? to)
    {
        var m = doc.Mission;
        var list = MissionEdits.ListOf<T>(m);
        if (from == null) list.Insert(Math.Min(_index, list.Count), MissionEdits.Copy(to!));
        else if (to == null) list.RemoveAt(_index);
        else list[_index] = MissionEdits.Copy(to);
        m.RaiseChanged(MissionEdits.PartOf<T>());
    }

    public void Do(IEditorMapDoc doc) => Apply(doc, _before, _after);
    public void Undo(IEditorMapDoc doc) => Apply(doc, _after, _before);

    public bool TryMerge(IEditCommand next)
    {
        if (_merge == null || next is not ListEdit<T> n || n._merge != _merge || n._index != _index || _before == null || _after == null || n._after == null) return false;
        _after = n._after;
        return true;
    }
}

/// <summary>several commands as one undo step (a rename that also renames the references)</summary>
public sealed class CompositeEdit : IEditCommand
{
    readonly List<IEditCommand> _cs;
    public string Name { get; }
    public CompositeEdit(string name, IEnumerable<IEditCommand> cs) { Name = name; _cs = cs.ToList(); }
    public void Do(IEditorMapDoc doc) { foreach (var c in _cs) c.Do(doc); }
    public void Undo(IEditorMapDoc doc) { for (int i = _cs.Count - 1; i >= 0; i--) _cs[i].Undo(doc); }
    public bool TryMerge(IEditCommand next) => false;
    public int Count => _cs.Count;
}

/// <summary>a change of a single value of the mission data (custom.lua, a text, the start camera, the trigger file's header)</summary>
public sealed class ValueEdit<TV> : IEditCommand
{
    readonly Func<MissionDoc, TV> _get;
    readonly Action<MissionDoc, TV> _set;
    readonly TV _before;
    TV _after;
    readonly MissionPart _part;
    readonly string? _merge;
    public string Name { get; }
    public ValueEdit(string name, MissionDoc m, MissionPart part, Func<MissionDoc, TV> get, Action<MissionDoc, TV> set, TV after, string? merge = null)
    {
        Name = name; _get = get; _set = set; _before = get(m); _after = after; _part = part; _merge = merge;
    }
    public void Do(IEditorMapDoc doc) { _set(doc.Mission, _after); doc.Mission.RaiseChanged(_part); }
    public void Undo(IEditorMapDoc doc) { _set(doc.Mission, _before); doc.Mission.RaiseChanged(_part); }
    public bool TryMerge(IEditCommand next)
    {
        if (_merge == null || next is not ValueEdit<TV> n || n._merge != _merge) return false;
        _after = n._after;
        return true;
    }
}

/// <summary>gives a placed object a GUID of its own (the attribute "guid": world.lua's id the triggers name it by)</summary>
public sealed class ObjectGuidEdit : IEditCommand
{
    readonly Maps.MapObject _o;
    readonly string _guid;
    public string Name => "Name an object for the triggers";
    public ObjectGuidEdit(Maps.MapObject o, string guid) { _o = o; _guid = guid; }
    public void Do(IEditorMapDoc doc) { _o.Attr[MissionDoc.GuidAttr] = _guid; doc.Mission.RaiseChanged(MissionPart.Groups); }
    public void Undo(IEditorMapDoc doc) { _o.Attr.Remove(MissionDoc.GuidAttr); doc.Mission.RaiseChanged(MissionPart.Groups); }
    public bool TryMerge(IEditCommand next) => false;
}

/// <summary>the commands of the mission data, made from the document's current state</summary>
public static class MissionEdits
{
    // ------------------------------------------------------------------ the lists by entry type
    public static List<T> ListOf<T>(MissionDoc m) where T : class => (typeof(T).Name switch
    {
        nameof(EdRegion) => (object)m.Regions,
        nameof(EdGroup) => m.Groups,
        nameof(EdVariable) => m.Variables,
        nameof(EdQuest) => m.Quests,
        nameof(EdQuestionMark) => m.QuestionMarks,
        nameof(EdCamera) => m.Cameras,
        nameof(TriggerItem) => m.Triggers.Items,
        _ => throw new ArgumentException($"no list of {typeof(T).Name}"),
    }) as List<T> ?? throw new ArgumentException(typeof(T).Name);

    public static MissionPart PartOf<T>() => typeof(T).Name switch
    {
        nameof(EdRegion) => MissionPart.Regions, nameof(EdGroup) => MissionPart.Groups, nameof(EdVariable) => MissionPart.Variables,
        nameof(EdQuest) => MissionPart.Quests, nameof(EdQuestionMark) => MissionPart.QuestionMarks, nameof(EdCamera) => MissionPart.Cameras,
        _ => MissionPart.Triggers,
    };

    public static T Copy<T>(T x) where T : class => x switch
    {
        EdRegion r => (T)(object)r.Clone(), EdGroup g => (T)(object)g.Clone(), EdVariable v => (T)(object)v.Clone(), EdQuest q => (T)(object)q.Clone(),
        EdQuestionMark q => (T)(object)q.Clone(), EdCamera c => (T)(object)c.Clone(), TriggerItem t => (T)(object)t.Copy(),
        _ => throw new ArgumentException(typeof(T).Name),
    };

    // ------------------------------------------------------------------ generic
    public static IEditCommand Add<T>(MissionDoc m, T item, int? at = null, string? name = null) where T : class =>
        new ListEdit<T>(name ?? $"Add {Kind<T>()}", at ?? ListOf<T>(m).Count, null, item);

    public static IEditCommand Remove<T>(MissionDoc m, int index, string? name = null) where T : class =>
        new ListEdit<T>(name ?? $"Delete {Kind<T>()}", index, ListOf<T>(m)[index], null);

    /// <summary>a change of entry index: f changes a copy; merge: a key under which consecutive changes of the same
    /// entry merge into one undo step (a drag)</summary>
    public static IEditCommand Change<T>(MissionDoc m, int index, Action<T> f, string? name = null, string? merge = null) where T : class
    {
        var before = ListOf<T>(m)[index];
        var after = Copy(before);
        f(after);
        return new ListEdit<T>(name ?? $"Change {Kind<T>()}", index, before, after, merge);
    }

    /// <summary>moves an entry to another place of its list</summary>
    public static IEditCommand Move<T>(MissionDoc m, int from, int to, string? name = null) where T : class
    {
        var item = ListOf<T>(m)[from];
        return new CompositeEdit(name ?? $"Move {Kind<T>()}", new IEditCommand[] { new ListEdit<T>("", from, item, null), new ListEdit<T>("", to, null, item) });
    }

    static string Kind<T>() => typeof(T).Name switch
    {
        nameof(EdRegion) => "region", nameof(EdGroup) => "group", nameof(EdVariable) => "variable", nameof(EdQuest) => "quest",
        nameof(EdQuestionMark) => "question mark", nameof(EdCamera) => "camera point", _ => "trigger",
    };

    /// <summary>the GUID a trigger or a group names a placed object by: its own (world.lua's, or given before), else a
    /// new one - then the command that gives it (run it with the reference's edit; null: it had one)</summary>
    public static (IEditCommand? Cmd, string Guid) EnsureGuid(Maps.MapObject o) =>
        o.Attr.TryGetValue(MissionDoc.GuidAttr, out var g) && g.Length > 0 ? (null, g) : (new ObjectGuidEdit(o, EdGuid.Of(o.Name)), EdGuid.Of(o.Name));

    // ------------------------------------------------------------------ the regions
    /// <summary>a new region with one shape (map box); its name a free Region_n unless given</summary>
    public static IEditCommand NewRegion(MissionDoc m, bool oval, double x, double y, double w, double h, double z = 0, string? name = null)
    {
        var r = new EdRegion
        {
            Guid = EdGuid.New(), Name = name ?? MissionDoc.FreeName("Region", m.Regions.Select(q => q.Name)),
            Shapes = { new EdShape { Oval = oval, X = x, Y = y, W = Math.Max(1, w), H = Math.Max(1, h), Z = z } },
        };
        return Add(m, r, null, "New region");
    }

    /// <summary>moves a region's shape (shape -1: every shape) by (dx, dy); a drag merges</summary>
    public static IEditCommand MoveShape(MissionDoc m, int region, int shape, double dx, double dy, string? merge = "move") =>
        Change<EdRegion>(m, region, r => { foreach (var s in shape < 0 ? r.Shapes : new List<EdShape> { r.Shapes[shape] }) { s.X += dx; s.Y += dy; } }, "Move region", merge);

    /// <summary>sets a shape's box (a resize handle's drag)</summary>
    public static IEditCommand SetShapeBox(MissionDoc m, int region, int shape, double x, double y, double w, double h, string? merge = "resize") =>
        Change<EdRegion>(m, region, r => { var s = r.Shapes[shape]; s.X = x; s.Y = y; s.W = Math.Max(1, w); s.H = Math.Max(1, h); }, "Resize region", merge);

    public static IEditCommand AddShape(MissionDoc m, int region, bool oval, double x, double y, double w, double h, double z = 0) =>
        Change<EdRegion>(m, region, r => r.Shapes.Add(new EdShape { Oval = oval, X = x, Y = y, W = Math.Max(1, w), H = Math.Max(1, h), Z = z }), "Add a shape");

    public static IEditCommand RemoveShape(MissionDoc m, int region, int shape) =>
        Change<EdRegion>(m, region, r => r.Shapes.RemoveAt(shape), "Delete a shape");

    // ------------------------------------------------------------------ renames that follow the references
    /// <summary>renames a variable, a quest or a trigger and the trigger parameters that name it (Var / SetVar varname,
    /// quest_name, SetTrigger trigger, InfoBar's $(var)) - one undo step</summary>
    public static IEditCommand Rename<T>(MissionDoc m, int index, string newName) where T : class
    {
        var cs = new List<IEditCommand>();
        string old = ListOf<T>(m)[index] switch { EdVariable v => v.Name, EdQuest q => q.Name, EdRegion r => r.Name, EdGroup g => g.Name, EdQuestionMark q => q.Name, EdCamera c => c.Name, EdTrigger t => t.Name, _ => "" };
        cs.Add(Change<T>(m, index, x =>
        {
            switch (x) { case EdVariable v: v.Name = newName; break; case EdQuest q: q.Name = newName; break; case EdRegion r: r.Name = newName; break; case EdGroup g: g.Name = newName; break; case EdQuestionMark q: q.Name = newName; break; case EdCamera c: c.Name = newName; break; case EdTrigger t: t.Name = newName; break; }
        }, $"Rename {Kind<T>()}"));
        var types = typeof(T).Name switch
        {
            nameof(EdVariable) => new[] { PType.Variable },
            nameof(EdQuest) => new[] { PType.QuestName },
            nameof(TriggerItem) => new[] { PType.TriggerName },
            nameof(EdGroup) or nameof(EdRegion) => new[] { PType.ObjectName, PType.Group, PType.Region },
            _ => Array.Empty<PType>(),
        };
        if (types.Length > 0 && old.Length > 0 && old != newName)
        {
            var items = m.Triggers.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not EdTrigger t) continue;
                bool hit = false;
                var copy = (EdTrigger)t.Copy();
                foreach (var cond in new[] { true, false })
                    foreach (var n in copy.List(cond))
                    {
                        var ts = TriggerCatalog.Find(n.Code, cond);
                        foreach (var p in n.Params)
                        {
                            if (p.Value.Kind == ParamKind.Expr) continue;
                            var pt = ts?.Param(p.Key)?.Type;
                            if (pt != null && types.Contains(pt.Value) && p.Value.Value == old) { p.Value = ParamValue.Of(newName); hit = true; }
                            else if (typeof(T) == typeof(EdVariable) && p.Value.Kind == ParamKind.String && p.Value.Text.Contains($"$({old})"))
                            { p.Value = ParamValue.Str(p.Value.Text.Replace($"$({old})", $"$({newName})")); hit = true; }
                        }
                    }
                if (hit) cs.Add(new ListEdit<TriggerItem>("", i, t, copy));
            }
        }
        return cs.Count == 1 ? cs[0] : new CompositeEdit($"Rename {Kind<T>()} (and {cs.Count - 1} triggers)", cs);
    }

    // ------------------------------------------------------------------ texts, custom.lua, the start camera
    /// <summary>sets a text of a language (null removes it)</summary>
    public static IEditCommand SetText(MissionDoc m, string lang, string key, string? text, string? merge = null) =>
        new ValueEdit<string?>($"Text {key}", m, MissionPart.Texts,
            d => d.Texts.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v) ? v : null,
            (d, v) =>
            {
                if (!d.Texts.TryGetValue(lang, out var t)) d.Texts[lang] = t = new SortedDictionary<string, string>(StringComparer.Ordinal);
                if (v == null) t.Remove(key); else t[key] = v;
            }, text, merge);

    public static IEditCommand SetCustom(MissionDoc m, string? code) =>
        new ValueEdit<string?>("Edit custom.lua", m, MissionPart.Custom, d => d.Custom, (d, v) => d.Custom = v, code);

    public static IEditCommand SetStartCamera(MissionDoc m, double angle, double? x, double? y) =>
        new ValueEdit<(double, double?, double?)>("Start camera", m, MissionPart.Cameras, d => (d.StartAngle, d.StartX, d.StartY),
            (d, v) => { d.StartAngle = v.Item1; d.StartX = v.Item2; d.StartY = v.Item3; }, (angle, x, y));

    /// <summary>a quest's headline / description / group title: the quest and its text key in the editor's language</summary>
    public static IEditCommand SetQuestText(MissionDoc m, int index, string field, string text)
    {
        var q = m.Quests[index];
        string key = field switch { "headline" => q.KeyHeadline, "description" => q.KeyDescription, _ => q.KeyGroup };
        var cs = new List<IEditCommand>
        {
            Change<EdQuest>(m, index, x => { if (field == "headline") x.Headline = text; else if (field == "description") x.Description = text; else x.GroupTitle = text; }, "Quest text", "qtext:" + field),
        };
        if (key.Length > 0) cs.Add(SetText(m, m.Lang, key, text));
        return new CompositeEdit("Quest text", cs);
    }

    /// <summary>a new quest: its name, text keys _&lt;name&gt;_Headline / _Description, hidden at the start</summary>
    public static IEditCommand NewQuest(MissionDoc m, string? name = null)
    {
        var n = name ?? MissionDoc.FreeName("Quest", m.Quests.Select(q => q.Name));
        var q = new EdQuest
        {
            Guid = EdGuid.New(), Name = n, Group = n, Main = true, Headline = n, Description = "",
            KeyHeadline = $"_{n}_Headline", KeyDescription = $"_{n}_Description", KeyGroup = $"_{n}_Group", GroupTitle = n,
        };
        return new CompositeEdit("New quest", new[] { Add(m, q), SetText(m, m.Lang, q.KeyHeadline, n), SetText(m, m.Lang, q.KeyDescription, ""), SetText(m, m.Lang, q.KeyGroup, n) });
    }

    // ------------------------------------------------------------------ triggers
    /// <summary>a new trigger in a folder: enabled, once, no conditions or actions</summary>
    public static IEditCommand NewTrigger(MissionDoc m, string folder, string? name = null, int? at = null)
    {
        var t = new EdTrigger
        {
            Name = name ?? MissionDoc.FreeName("Trigger", m.Triggers.Triggers.Select(x => x.Name)), Guid = EdGuid.New(), Folder = folder, Enabled = true, Once = true,
        };
        // (after the folder's last trigger, so the file keeps the folder together)
        int pos = at ?? (m.Triggers.Items.FindLastIndex(i => i is EdTrigger x && x.Folder == folder) is int k and >= 0 ? k + 1 : m.Triggers.Items.Count);
        return Add<TriggerItem>(m, t, pos, "New trigger");
    }

    /// <summary>a change of trigger item index (f changes a copy); merge as Change</summary>
    public static IEditCommand ChangeTrigger(MissionDoc m, int index, Action<EdTrigger> f, string name = "Change trigger", string? merge = null) =>
        Change<TriggerItem>(m, index, x => f((EdTrigger)x), name, merge);
}
