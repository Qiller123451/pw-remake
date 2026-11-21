// The map editor's mission data (docs/MAP_EDITOR.md "Regions and mission data"): what world.lua holds besides the
// objects and what the triggers reference - regions (named sets of rectangles and ovals, each shape switchable),
// groups (named sets of placed objects), variables, quests, question marks, camera points and the start camera - with
// the triggers (triggers.lua: TriggerScript), custom.lua (text, never changed by the editor's tools) and the texts
// per language. MissionDoc holds them for a document (IEditorMapDoc.Mission); every change goes through a command
// (MissionEdits.cs) so it can be undone.
//
// The entries keep the fields the editor does not show (the original's flags, colours, the quest's attributes ...),
// so a mission's world.lua read and written again is the same data (EditorTriggersTests). No Godot types.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor.Triggers;

namespace PwRemake.Sim.Editor.Regions;

/// <summary>one shape of a region: the box X .. X + W, Y .. Y + H (map metres) or the ellipse in it</summary>
public sealed class EdShape
{
    public bool Oval;
    public double X, Y, W, H, Z;
    /// <summary>false: switched off at the start (ActivateRegion switches it on)</summary>
    public bool Enabled = true;
    /// <summary>the original's fields (flags ...)</summary>
    public JsonObject? Extra;
    /// <summary>the box as read (world.lua's numbers: millimetres) and regions.lua's box of the same shape (32-bit floats,
    /// the original's own values): an unchanged shape is written to regions.lua with the floats it was read with, so an
    /// unchanged mission saves the same regions.lua (world.lua's rounded numbers would move it by up to 0.5 mm). Not
    /// public: no part of what the shape is.</summary>
    internal (double X, double Y, double W, double H, bool Oval, Maps.RegionShape Map)? MapBox;

    public EdShape Clone() => new() { Oval = Oval, X = X, Y = Y, W = W, H = H, Z = Z, Enabled = Enabled, Extra = (JsonObject?)Extra?.DeepClone(), MapBox = MapBox };

    /// <summary>regions.lua's box of this shape: the floats it was read with while the shape is as read, else its numbers
    /// as floats</summary>
    internal (float X, float Y, float W, float H) MapFloats() =>
        MapBox is { } k && k.X == X && k.Y == Y && k.W == W && k.H == H && k.Oval == Oval
            ? ((float)k.Map.X, (float)k.Map.Y, (float)k.Map.W, (float)k.Map.H)
            : ((float)X, (float)Y, (float)W, (float)H);

    public bool Contains(double mx, double my)
    {
        if (mx < X || my < Y || mx > X + W || my > Y + H) return false;
        if (!Oval) return true;
        double u = (mx - X) / W * 2 - 1, v = (my - Y) / H * 2 - 1;
        return u * u + v * v <= 1;
    }
    public bool SameAs(EdShape o) => Oval == o.Oval && X == o.X && Y == o.Y && W == o.W && H == o.H && Z == o.Z && Enabled == o.Enabled;
}

public sealed class EdRegion
{
    public string Guid = "", Name = "";
    public List<EdShape> Shapes = new();
    public JsonObject? Extra;

    public EdRegion Clone() => new() { Guid = Guid, Name = Name, Shapes = Shapes.Select(s => s.Clone()).ToList(), Extra = (JsonObject?)Extra?.DeepClone() };
    public bool Contains(double mx, double my) => Shapes.Any(s => s.Enabled && s.Contains(mx, my));
    /// <summary>[x0, y0, x1, y1] of all shapes; null without shapes</summary>
    public double[]? Box()
    {
        if (Shapes.Count == 0) return null;
        return new[] { Shapes.Min(s => s.X), Shapes.Min(s => s.Y), Shapes.Max(s => s.X + s.W), Shapes.Max(s => s.Y + s.H) };
    }
    public override string ToString() => Name;
}

public sealed class EdGroup
{
    public string Guid = "", Name = "";
    /// <summary>the members: GUIDs of placed objects</summary>
    public List<string> Members = new();
    public double X, Y, Z;
    public JsonObject? Extra;
    public EdGroup Clone() => new() { Guid = Guid, Name = Name, Members = Members.ToList(), X = X, Y = Y, Z = Z, Extra = (JsonObject?)Extra?.DeepClone() };
    public override string ToString() => Name;
}

public sealed class EdVariable
{
    public string Name = "", Type = "int", Value = "0";
    public EdVariable Clone() => new() { Name = Name, Type = Type, Value = Value };
    public override string ToString() => Name;
}

public sealed class EdQuest
{
    public string Guid = "", Name = "", Group = "";
    public bool Main = true;
    /// <summary>bonus points by difficulty (easy, medium, hard)</summary>
    public double[] Bonus = new double[3];
    /// <summary>the state at the start: visible (false = hidden until a Quest action shows it), accomplished, failed</summary>
    public bool Visible, Accomplished, Unaccomplishable;
    /// <summary>the headline, the description and the group's title (shown by the quest book) and their text keys
    /// (texts/&lt;lang&gt;.lua: the editor writes both)</summary>
    public string Headline = "", Description = "", GroupTitle = "";
    public string KeyHeadline = "", KeyDescription = "", KeyGroup = "";
    public JsonObject? Extra;
    public EdQuest Clone() => new()
    {
        Guid = Guid, Name = Name, Group = Group, Main = Main, Bonus = (double[])Bonus.Clone(), Visible = Visible, Accomplished = Accomplished,
        Unaccomplishable = Unaccomplishable, Headline = Headline, Description = Description, GroupTitle = GroupTitle, KeyHeadline = KeyHeadline, KeyDescription = KeyDescription, KeyGroup = KeyGroup, Extra = (JsonObject?)Extra?.DeepClone(),
    };
    public override string ToString() => Name;
}

public sealed class EdQuestionMark
{
    public string Guid = "", Name = "", State = "STATE_INVISIBLE";
    public double X, Y, Z;
    public JsonObject? Extra;
    public EdQuestionMark Clone() => new() { Guid = Guid, Name = Name, State = State, X = X, Y = Y, Z = Z, Extra = (JsonObject?)Extra?.DeepClone() };
    public override string ToString() => Name;
}

/// <summary>a camera point: where the camera looks (map X, Y), its turn (Yaw, radians, the map's default_camera
/// convention), tilt and distance - for sequences, the start camera and the scripts (world.lua cameras)</summary>
public sealed class EdCamera
{
    public string Name = "";
    public double X, Y, Angle = 0.785, Pitch = 0.9, Dist = 60;
    public EdCamera Clone() => new() { Name = Name, X = X, Y = Y, Angle = Angle, Pitch = Pitch, Dist = Dist };
    public override string ToString() => Name;
}

/// <summary>a placed object as the region / trigger tools see it (picking, groups, references)</summary>
public sealed record EdObject(string Guid, string Name, string Class, string Type, double X, double Y, int? Owner);

/// <summary>what changed in the mission data</summary>
public enum MissionPart { Regions, Groups, Variables, Quests, QuestionMarks, Cameras, Triggers, Texts, Custom, Settings, All }

public static class EdGuid
{
    /// <summary>a new GUID in the original editor's form (32 letters a - p)</summary>
    public static string New() => Letters(System.Guid.NewGuid().ToByteArray());
    /// <summary>a stable GUID made from a text (an object of a map without GUIDs: its name)</summary>
    public static string Of(string text) => Letters(SHA1.HashData(Encoding.UTF8.GetBytes("pw-editor:" + text)).Take(16).ToArray());
    public static string Letters(byte[] b) => string.Concat(b.Select(c => $"{(char)('a' + (c & 15))}{(char)('a' + (c >> 4))}"));
    public static bool Is(string s) => s.Length == 32 && s.All(c => c >= 'a' && c <= 'p');
}

/// <summary>the mission data of a map</summary>
public sealed class MissionDoc
{
    public List<EdRegion> Regions = new();
    public List<EdGroup> Groups = new();
    public List<EdVariable> Variables = new();
    public List<EdQuest> Quests = new();
    public List<EdQuestionMark> QuestionMarks = new();
    public List<EdCamera> Cameras = new();
    /// <summary>the start camera: map_info.default_camera (the angle) and the place it looks at (null: the human's
    /// start location, as before)</summary>
    public double StartAngle = 0.785;
    public double? StartX, StartY;
    public TriggerScript Triggers = new();
    /// <summary>custom.lua (null: none) - only the code box changes it</summary>
    public string? Custom;
    /// <summary>language -> key -> text</summary>
    public SortedDictionary<string, SortedDictionary<string, string>> Texts = new(StringComparer.Ordinal);
    /// <summary>the language the editor writes new texts in (uk = English)</summary>
    public string Lang = "uk";
    /// <summary>map.lua's mission table (a campaign mission's); null on a plain map</summary>
    public JsonObject? Mission;
    /// <summary>world.lua as read (the fields the editor does not change: objects, dialogs, sequences, refs, trigger_tree ...)</summary>
    public JsonObject World = new();
    /// <summary>the placed objects (the document gives them: picking, groups, references)</summary>
    public Func<IReadOnlyList<EdObject>> Objects = () => Array.Empty<EdObject>();
    /// <summary>the document's object list (one list for the editor: world.lua's objects are in it with their GUID in
    /// the attribute "guid" - BindObjects); null: world.lua's objects are written as read</summary>
    public Func<IReadOnlyList<Maps.MapObject>>? MapObjects;

    /// <summary>the attribute of a map object that holds its world.lua GUID (the triggers' id); "mission_only": the
    /// object is world.lua's alone (not written to objects.lua)</summary>
    public const string GuidAttr = "guid", MissionOnlyAttr = "mission_only";
    /// <summary>the GUIDs of world.lua's objects that were put into the document's list, with their state then</summary>
    Dictionary<string, string> _bound = new(StringComparer.Ordinal);

    /// <summary>raised after every change (the part changed; the region overlay, the panels follow it)</summary>
    public event Action<MissionPart>? Changed;
    public void RaiseChanged(MissionPart p) => Changed?.Invoke(p);

    /// <summary>does the map have triggers (the trigger file or custom.lua has something) - Test map then plays it as a mission</summary>
    public bool HasTriggers => Triggers.Items.Count > 0 || !string.IsNullOrWhiteSpace(Custom);
    public bool IsMission => Mission != null;

    // ------------------------------------------------------------------ lookups
    public EdRegion? Region(string guidOrName) => Regions.FirstOrDefault(r => r.Guid == guidOrName) ?? Regions.FirstOrDefault(r => r.Name == guidOrName);
    public EdGroup? Group(string guidOrName) => Groups.FirstOrDefault(r => r.Guid == guidOrName) ?? Groups.FirstOrDefault(r => r.Name == guidOrName);
    public EdQuest? Quest(string guidOrName) => Quests.FirstOrDefault(r => r.Guid == guidOrName) ?? Quests.FirstOrDefault(r => r.Name == guidOrName);
    public EdObject? Object(string guidOrName) { var os = Objects(); return os.FirstOrDefault(o => o.Guid == guidOrName) ?? os.FirstOrDefault(o => o.Name == guidOrName); }
    /// <summary>the dialogue scenes and sequences the mission has (world.lua dialogs / sequences: their paths)</summary>
    public IEnumerable<string> Dialogs => (World["dialogs"] as JsonObject)?.Select(kv => kv.Key) ?? Array.Empty<string>();
    public IEnumerable<string> Sequences => (World["sequences"] as JsonObject)?.Select(kv => kv.Key) ?? Array.Empty<string>();
    /// <summary>the text of a key in the editor's language (else English, else any)</summary>
    public string? Text(string key)
    {
        foreach (var l in new[] { Lang, "uk" }) if (Texts.TryGetValue(l, out var d) && d.TryGetValue(key, out var v)) return v;
        foreach (var d in Texts.Values) if (d.TryGetValue(key, out var v)) return v;
        return null;
    }
    public IEnumerable<string> TextKeys => Texts.Values.SelectMany(d => d.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal);

    /// <summary>a name not used yet: "Region_1", "Region_2" ...</summary>
    public static string FreeName(string stem, IEnumerable<string> used)
    {
        var u = new HashSet<string>(used, StringComparer.Ordinal);
        for (int k = 1; ; k++) if (!u.Contains($"{stem}_{k}")) return $"{stem}_{k}";
    }

    // ================================================================== reading
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>the mission data of a map: world.lua (null: a plain map), map.lua's mission table, triggers.lua,
    /// custom.lua, the texts; the map's regions (regions.lua) when world.lua has none</summary>
    public static MissionDoc From(JsonNode? world, JsonObject? mission, string? triggers, string? custom, IReadOnlyDictionary<string, JsonObject>? texts,
        IEnumerable<Maps.MapRegion>? mapRegions)
    {
        var m = new MissionDoc { Mission = (JsonObject?)mission?.DeepClone(), Custom = custom };
        if (world is JsonObject w) m.World = (JsonObject)w.DeepClone();
        var W = m.World;
        if (W["regions"] is JsonArray ra)
        {
            // regions.lua has the same regions (the converter writes both from the original map: the same order, the
            // same shapes) with the original's floats; world.lua's are rounded to millimetres. Each shape keeps the
            // floats of its regions.lua twin (EdShape.MapBox) - matched by the place in the list, else by the name
            var mr = mapRegions?.ToList() ?? new List<Maps.MapRegion>();
            var byName = new Dictionary<string, Queue<Maps.MapRegion>>(StringComparer.Ordinal);
            foreach (var x in mr) { if (!byName.TryGetValue(x.Name, out var q)) byName[x.Name] = q = new Queue<Maps.MapRegion>(); q.Enqueue(x); }
            var used = new HashSet<Maps.MapRegion>();
            int ri = 0;
            foreach (var r in ra.OfType<JsonObject>())
            {
                var reg = new EdRegion
                {
                    Guid = r["guid"].Str(), Name = r["name"].Str(), Extra = r,
                    Shapes = (r["shapes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(s => new EdShape
                    {
                        Oval = s["type"].Str() == "oval", X = s["x"].Num(), Y = s["y"].Num(), W = s["w"].Num(), H = s["h"].Num(), Z = s["z"].Num(),
                        Enabled = !(s["enabled"] is JsonValue ev && ev.TryGetValue(out bool b) && !b), Extra = s,
                    }).ToList(),
                };
                Maps.MapRegion? twin = ri < mr.Count && mr[ri].Name == reg.Name && !used.Contains(mr[ri]) ? mr[ri] : null;
                if (twin == null && byName.TryGetValue(reg.Name, out var qn)) while (qn.Count > 0 && twin == null) { var c = qn.Dequeue(); if (!used.Contains(c)) twin = c; }
                ri++;
                if (twin != null)
                {
                    used.Add(twin);
                    static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-3 + 1e-6 * Math.Abs(a);
                    for (int j = 0; j < reg.Shapes.Count && j < twin.Shapes.Count; j++)
                    {
                        var s = reg.Shapes[j]; var t = twin.Shapes[j];
                        if (t.Oval == s.Oval && Near(t.X, s.X) && Near(t.Y, s.Y) && Near(t.W, s.W) && Near(t.H, s.H)) s.MapBox = (s.X, s.Y, s.W, s.H, s.Oval, t);
                    }
                }
                m.Regions.Add(reg);
            }
        }
        else if (mapRegions != null)
            foreach (var r in mapRegions)
                m.Regions.Add(new EdRegion
                {
                    Guid = EdGuid.Of("region:" + r.Name), Name = r.Name,
                    Shapes = r.Shapes.Select(s => new EdShape { Oval = s.Oval, X = s.X, Y = s.Y, W = s.W, H = s.H, Enabled = s.Enabled }).ToList(),
                });
        foreach (var g in (W["groups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            m.Groups.Add(new EdGroup { Guid = g["guid"].Str(), Name = g["name"].Str(), X = g["x"].Num(), Y = g["y"].Num(), Z = g["z"].Num(), Members = (g["members"] as JsonArray ?? new JsonArray()).Select(x => x.Str()).ToList(), Extra = g });
        if (W["variables"] is JsonObject vs)
            foreach (var (k, v) in vs) m.Variables.Add(new EdVariable { Name = k, Type = v?["type"].Str("int") ?? "int", Value = v?["value"].Str("0") ?? "0" });
        foreach (var q in (W["quests"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var b = q["bonus"];
            m.Quests.Add(new EdQuest
            {
                Guid = q["guid"].Str(), Name = q["name"].Str(), Group = q["group"].Str(), Main = q["main"].Bool(),
                Bonus = new[] { b?["easy"].Num() ?? 0, b?["medium"].Num() ?? 0, b?["hard"].Num() ?? 0 },
                Visible = q["visible"].Bool(), Accomplished = q["accomplished"].Bool(), Unaccomplishable = q["unaccomplishable"].Bool(),
                Headline = q["headline"].Str(), Description = q["description"].Str(), GroupTitle = q["group_title"].Str(),
                KeyHeadline = q["key_headline"].Str(), KeyDescription = q["key_description"].Str(), KeyGroup = q["key_group"].Str(), Extra = q,
            });
        }
        foreach (var q in (W["question_marks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            m.QuestionMarks.Add(new EdQuestionMark { Guid = q["guid"].Str(), Name = q["name"].Str(), State = q["state"].Str("STATE_INVISIBLE"), X = q["x"].Num(), Y = q["y"].Num(), Z = q["z"].Num(), Extra = q });
        foreach (var c in (W["cameras"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            m.Cameras.Add(new EdCamera { Name = c["name"].Str(), X = c["x"].Num(), Y = c["y"].Num(), Angle = c["angle"].Num(0.785), Pitch = c["pitch"].Num(0.9), Dist = c["dist"].Num(60) });
        var mi = m.Mission?["map_info"];
        if (mi?["default_camera"] is JsonValue dc) m.StartAngle = dc.Num(0.785);
        if (mi?["start_camera"] is JsonObject sc) { m.StartX = sc["x"].Num(); m.StartY = sc["y"].Num(); }
        m.Triggers = triggers != null ? TriggerScript.Parse(triggers) : new TriggerScript();
        if (texts != null)
            foreach (var (lang, o) in texts)
            {
                var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in o) d[k] = v.Str();
                m.Texts[lang] = d;
            }
        if (m.Mission?["lang"].Str() is { Length: > 0 } lg) m.Lang = lg;
        return m;
    }

    /// <summary>the mission data of a map document of the new format (PwMap: world, mission, triggers, custom, texts)</summary>
    public static MissionDoc From(Maps.PwMap map) => From(map.World, map.Mission, map.Triggers, map.Custom, map.Texts, map.Regions);

    // ================================================================== writing
    /// <summary>world.lua's data: the fields read, with the editor's lists written over them</summary>
    public JsonObject ToWorld()
    {
        var w = (JsonObject)World.DeepClone();
        if (ObjectsJson() is { } objs) Put(w, "objects", objs);
        var regs = new JsonArray();
        foreach (var r in Regions) regs.Add(RegionJson(r));
        // (a field the original has keeps its place; a new one is added at the end)
        Put(w, "regions", regs);
        var tree = w["region_tree"] as JsonObject;
        if (tree != null || Regions.Count > 0)
        {
            tree ??= new JsonObject { ["name"] = "Root", ["guid"] = EdGuid.Of("region_tree"), ["regions"] = new JsonArray(), ["folders"] = new JsonArray() };
            var have = new HashSet<string>(StringComparer.Ordinal);
            var guids = new HashSet<string>(Regions.Select(r => r.Guid), StringComparer.Ordinal);
            void Prune(JsonObject f)
            {
                if (f["regions"] is JsonArray a)
                {
                    for (int i = a.Count - 1; i >= 0; i--) if (!guids.Contains(a[i].Str())) a.RemoveAt(i); else have.Add(a[i].Str());
                }
                foreach (var s in (f["folders"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Prune(s);
            }
            Prune(tree);
            if (tree["regions"] is not JsonArray root) tree["regions"] = root = new JsonArray();
            foreach (var r in Regions) if (!have.Contains(r.Guid)) root.Add(r.Guid);
            Put(w, "region_tree", tree);
        }
        Put(w, "groups", new JsonArray(Groups.Select(GroupJson).ToArray<JsonNode?>()));
        var vars = new JsonObject();
        foreach (var v in Variables) vars[v.Name] = new JsonObject { ["type"] = v.Type, ["value"] = v.Value };
        Put(w, "variables", vars);
        Put(w, "quests", new JsonArray(Quests.Select(QuestJson).ToArray<JsonNode?>()));
        Put(w, "question_marks", new JsonArray(QuestionMarks.Select(QmJson).ToArray<JsonNode?>()));
        if (Cameras.Count > 0 || w["cameras"] != null)
            Put(w, "cameras", new JsonArray(Cameras.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["x"] = c.X, ["y"] = c.Y, ["angle"] = c.Angle, ["pitch"] = c.Pitch, ["dist"] = c.Dist }).ToArray()));
        if (w["trigger_defaults"] == null && HasTriggers) w["trigger_defaults"] = TriggerCatalog.DefaultsJson();
        if (w["schema"] == null) { var o = new JsonObject { ["schema"] = "pw-world/1" }; foreach (var (k, v) in w.ToList()) { w.Remove(k); o[k] = v; } w = o; }
        return w;
    }

    static void Put(JsonObject o, string key, JsonNode value) => o[key] = value;

    /// <summary>a number written as the original had it when it did not change (keeps its text), else the value</summary>
    static JsonNode Num(JsonObject? orig, string key, double v) => orig?[key] is JsonValue jv && jv.TryGetValue(out double d) && d == v ? jv.DeepClone() : JsonValue.Create(v)!;

    JsonObject RegionJson(EdRegion r)
    {
        var o = r.Extra != null ? (JsonObject)r.Extra.DeepClone() : new JsonObject { ["guid"] = r.Guid, ["name"] = r.Name, ["flags"] = 67, ["color"] = "ffffffff", ["note"] = "" };
        o["guid"] = r.Guid; o["name"] = r.Name;
        var shapes = new JsonArray();
        bool changed = r.Extra == null || (r.Extra["shapes"] as JsonArray)?.Count != r.Shapes.Count;
        foreach (var s in r.Shapes)
        {
            var so = s.Extra != null ? (JsonObject)s.Extra.DeepClone() : new JsonObject { ["type"] = "rect", ["enabled"] = true, ["flags"] = 224 };
            // (a type the original wrote otherwise - "3" on mission 6's nests: a rectangle to the game - stays as it was)
            bool sameType = s.Extra != null && (s.Extra["type"].Str() == "oval") == s.Oval;
            if (!sameType || s.Extra!["x"].Num() != s.X || s.Extra["y"].Num() != s.Y || s.Extra["w"].Num() != s.W || s.Extra["h"].Num() != s.H) changed = true;
            so["type"] = sameType ? s.Extra!["type"]!.DeepClone() : s.Oval ? "oval" : "rect";
            so["enabled"] = s.Enabled;
            so["x"] = Num(s.Extra, "x", s.X); so["y"] = Num(s.Extra, "y", s.Y); so["z"] = Num(s.Extra, "z", s.Z);
            so["w"] = Num(s.Extra, "w", s.W); so["h"] = Num(s.Extra, "h", s.H);
            shapes.Add(so);
        }
        o["shapes"] = shapes;
        if (changed && r.Box() is { } b) o["bbox"] = new JsonArray(Round(b[0]), Round(b[1]), Round(b[2]), Round(b[3]));
        return o;
    }
    static double Round(double v) => Math.Round(v, 3);

    static JsonNode? GroupJson(EdGroup g)
    {
        var o = g.Extra != null ? (JsonObject)g.Extra.DeepClone() : new JsonObject { ["guid"] = g.Guid, ["name"] = g.Name, ["index"] = -1 };
        o["guid"] = g.Guid; o["name"] = g.Name;
        o["x"] = Num(g.Extra, "x", g.X); o["y"] = Num(g.Extra, "y", g.Y); o["z"] = Num(g.Extra, "z", g.Z);
        o["members"] = new JsonArray(g.Members.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        return o;
    }

    static JsonNode? QuestJson(EdQuest q)
    {
        var o = q.Extra != null ? (JsonObject)q.Extra.DeepClone() : new JsonObject();
        o["guid"] = q.Guid; o["name"] = q.Name; o["group"] = q.Group; o["main"] = q.Main;
        var ob = q.Extra?["bonus"] as JsonObject;
        o["bonus"] = new JsonObject { ["easy"] = Num(ob, "easy", q.Bonus[0]), ["medium"] = Num(ob, "medium", q.Bonus[1]), ["hard"] = Num(ob, "hard", q.Bonus[2]) };
        o["visible"] = q.Visible; o["accomplished"] = q.Accomplished; o["unaccomplishable"] = q.Unaccomplishable;
        // the texts as the export has them (headline, description, group title in the mission's language)
        o["headline"] = q.Headline; o["description"] = q.Description;
        if (q.Extra == null) o["info"] = "";
        o["group_title"] = q.GroupTitle;
        o["key_headline"] = q.KeyHeadline; o["key_description"] = q.KeyDescription; o["key_group"] = q.KeyGroup;
        if (o["attr"] == null) o["attr"] = new JsonObject();
        return o;
    }

    static JsonNode? QmJson(EdQuestionMark q)
    {
        var o = q.Extra != null ? (JsonObject)q.Extra.DeepClone() : new JsonObject { ["guid"] = q.Guid, ["name"] = q.Name, ["index"] = -1 };
        o["guid"] = q.Guid; o["name"] = q.Name;
        o["x"] = Num(q.Extra, "x", q.X); o["y"] = Num(q.Extra, "y", q.Y); o["z"] = Num(q.Extra, "z", q.Z);
        o["state"] = q.State;
        return o;
    }

    /// <summary>the map's regions (regions.lua) of the editor's regions</summary>
    public List<Maps.MapRegion> ToMapRegions() => Regions.Select(r => new Maps.MapRegion
    {
        Name = r.Name,
        Shapes = r.Shapes.Select(s => { var (x, y, w, h) = s.MapFloats(); return new Maps.RegionShape { Oval = s.Oval, Enabled = s.Enabled, X = x, Y = y, W = w, H = h }; }).ToList(),
    }).ToList();

    /// <summary>map.lua's mission table with the start camera; null on a plain map</summary>
    public JsonObject? ToMission()
    {
        if (Mission == null) return null;
        var m = (JsonObject)Mission.DeepClone();
        if (m["map_info"] is not JsonObject mi) m["map_info"] = mi = new JsonObject();
        mi["default_camera"] = Num(Mission["map_info"] as JsonObject, "default_camera", StartAngle);
        if (StartX is double x && StartY is double y) mi["start_camera"] = new JsonObject { ["x"] = x, ["y"] = y };
        else mi.Remove("start_camera");
        return m;
    }

    /// <summary>the texts as the map document holds them</summary>
    public SortedDictionary<string, JsonObject> ToTexts()
    {
        var res = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (lang, d) in Texts)
        {
            var o = new JsonObject();
            foreach (var (k, v) in d) o[k] = v;
            res[lang] = o;
        }
        return res;
    }

    /// <summary>puts the mission data into a map document of the new format (the editor's save, Test map's folder):
    /// world, mission, triggers.lua, custom.lua, texts and regions.lua</summary>
    public void ApplyTo(Maps.PwMap map)
    {
        bool any = HasTriggers || Mission != null || World.Count > 0 || Regions.Count > 0 || Groups.Count > 0 || Variables.Count > 0 || Quests.Count > 0;
        map.Regions = ToMapRegions();
        if (!any) return;
        map.World = ToWorld();
        map.Mission = ToMission() ?? map.Mission;
        map.Triggers = Triggers.Items.Count > 0 || Triggers.Header.Count > 0 ? Triggers.Write() : null;
        map.Custom = Custom;
        foreach (var (k, v) in ToTexts()) map.Texts[k] = v;
    }

    /// <summary>a deep copy (Test map's snapshot)</summary>
    public MissionDoc Clone() => new()
    {
        Regions = Regions.Select(r => r.Clone()).ToList(), Groups = Groups.Select(g => g.Clone()).ToList(), Variables = Variables.Select(v => v.Clone()).ToList(),
        Quests = Quests.Select(q => q.Clone()).ToList(), QuestionMarks = QuestionMarks.Select(q => q.Clone()).ToList(), Cameras = Cameras.Select(c => c.Clone()).ToList(),
        StartAngle = StartAngle, StartX = StartX, StartY = StartY, Triggers = Triggers.Clone(), Custom = Custom, Lang = Lang,
        Texts = new SortedDictionary<string, SortedDictionary<string, string>>(Texts.ToDictionary(kv => kv.Key, kv => new SortedDictionary<string, string>(kv.Value, StringComparer.Ordinal)), StringComparer.Ordinal),
        Mission = (JsonObject?)Mission?.DeepClone(), World = (JsonObject)World.DeepClone(), Objects = Objects, MapObjects = MapObjects,
        _bound = new Dictionary<string, string>(_bound, StringComparer.Ordinal),
    };

    // ================================================================== world.lua's objects in the editor's one list
    static readonly string[] EditorAttrs = { GuidAttr, MissionOnlyAttr };

    /// <summary>the state of a map object as world.lua has it (a change of it rewrites the world record)</summary>
    static string Sig(Maps.MapObject o) => string.Join("|", o.Type, o.Cls, o.Name, o.Owner?.ToString(Inv) ?? "-", o.X.ToString("R", Inv), o.Y.ToString("R", Inv), o.Z.ToString("R", Inv),
        o.Rot.ToString("R", Inv), string.Join(",", o.Q.Select(q => q.ToString("R", Inv))), o.Level.ToString(Inv),
        string.Join(",", o.Attr.Where(kv => !EditorAttrs.Contains(kv.Key)).Select(kv => kv.Key + "=" + kv.Value)));

    /// <summary>a mission's world.lua objects into the document's list: each map object of the same name gets the GUID
    /// (attribute "guid"); a world object the map does not have (a converter that leaves them out of objects.lua) is
    /// added, marked "mission_only". Objects without a type (the original's helpers) stay world.lua's alone, unchanged.</summary>
    public void BindObjects(List<Maps.MapObject> list)
    {
        if (World["objects"] is not JsonArray wo) return;
        var byName = new Dictionary<string, Maps.MapObject>(StringComparer.Ordinal);
        foreach (var o in list) if (!o.Attr.ContainsKey(GuidAttr)) byName.TryAdd(o.Name, o);
        int added = 0;
        foreach (var r in wo.OfType<JsonObject>())
        {
            var guid = r["guid"].Str();
            if (guid.Length == 0 || r["type"].Str().Length == 0) continue;
            if (byName.Remove(r["name"].Str(), out var mo)) mo.Attr[GuidAttr] = guid;
            else
            {
                mo = new Maps.MapObject
                {
                    Type = r["type"].Str(), Name = r["name"].Str(), Cls = r["class"].Str(), X = r["x"].Num(), Y = r["y"].Num(), Z = r["z"].Num(), Rot = r["rot"].Num(),
                    Q = (r["q"] as JsonArray ?? new JsonArray(0, 0, 0, 1)).Select(x => x.Num()).ToArray(), Owner = r["owner"] is JsonValue ? r["owner"].Int() : null, Level = r["level"].Int(),
                };
                foreach (var (k, v) in r["attr"] as JsonObject ?? new JsonObject()) mo.Attr[k] = v.Str();
                mo.Attr[GuidAttr] = guid; mo.Attr[MissionOnlyAttr] = "1";
                list.Add(mo); added++;
            }
            _bound[guid] = Sig(mo);
        }
        if (added > 0) Log.For("Editor").Info($"{added} objects of world.lua that the map's objects lack were added to the editor's list");
    }

    /// <summary>after a save (MapDataDoc.Save): world.lua as it was written and read back becomes the world as read, its
    /// objects bound again to the document's objects of their GUIDs (as BindObjects does when the map is opened) - so
    /// the document is the map as opened from the file</summary>
    public void Saved(JsonNode? world)
    {
        if (world is not JsonObject w) return;
        World = (JsonObject)w.DeepClone();
        _bound.Clear();
        if (World["objects"] is not JsonArray wo || MapObjects == null) return;
        var byGuid = new Dictionary<string, Maps.MapObject>(StringComparer.Ordinal);
        foreach (var o in MapObjects()) if (o.Attr.TryGetValue(GuidAttr, out var g) && g.Length > 0) byGuid.TryAdd(g, o);
        foreach (var r in wo.OfType<JsonObject>())
        {
            var guid = r["guid"].Str();
            if (guid.Length == 0 || r["type"].Str().Length == 0) continue;
            if (byGuid.TryGetValue(guid, out var mo)) _bound[guid] = Sig(mo);
        }
    }

    /// <summary>world.lua's objects of the document's list: the records read, in their order, each with the editor's
    /// changes (a moved, turned, renamed, re-owned object; a deleted one is gone), then the objects that got a GUID in the
    /// editor (the triggers name them)</summary>
    JsonArray? ObjectsJson()
    {
        if (MapObjects == null) return null;
        var list = MapObjects();
        var byGuid = new Dictionary<string, Maps.MapObject>(StringComparer.Ordinal);
        foreach (var o in list) if (o.Attr.TryGetValue(GuidAttr, out var g) && g.Length > 0) byGuid.TryAdd(g, o);
        var orig = World["objects"] as JsonArray;
        if (orig == null && byGuid.Count == 0) return null;
        var res = new JsonArray();
        var done = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var r in (orig ?? new JsonArray()).OfType<JsonObject>())
        {
            var guid = r["guid"].Str();
            if (_bound.TryGetValue(guid, out var sig))
            {
                if (!byGuid.TryGetValue(guid, out var mo)) continue;          // (deleted in the editor)
                done.Add(guid);
                if (Sig(mo) == sig) { res.Add(r.DeepClone()); index = Math.Max(index, r["index"].Int() + 1); continue; }
                res.Add(Record(mo, (JsonObject)r.DeepClone(), guid));
            }
            else res.Add(r.DeepClone());
            index = Math.Max(index, r["index"].Int() + 1);
        }
        foreach (var (guid, mo) in byGuid)
            if (!done.Contains(guid) && !_bound.ContainsKey(guid)) { var rec = Record(mo, new JsonObject { ["index"] = index++ }, guid); res.Add(rec); }
        return res;
    }

    static JsonObject Record(Maps.MapObject o, JsonObject r, string guid)
    {
        r["guid"] = guid; r["name"] = o.Name; r["class"] = o.Cls; r["type"] = o.Type;
        r["owner"] = o.Owner is int ow ? JsonValue.Create(ow) : null;
        // (the export's precision: millimetres, 1/10000 rad - the map's floats are single precision)
        r["x"] = Num(r, "x", Round(o.X)); r["y"] = Num(r, "y", Round(o.Y)); r["z"] = Num(r, "z", Round(o.Z)); r["rot"] = Num(r, "rot", Math.Round(o.Rot, 4));
        r["q"] = new JsonArray(o.Q.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        r["level"] = o.Level;
        if (r["visible"] == null) r["visible"] = true;
        var a = new JsonObject();
        foreach (var (k, v) in o.Attr) if (!EditorAttrs.Contains(k)) a[k] = v;
        r["attr"] = a;
        return r;
    }

    /// <summary>the map objects as objects.lua has them: without the objects only world.lua has, without the editor's attributes</summary>
    public static List<Maps.MapObject> MapFileObjects(IEnumerable<Maps.MapObject> list) => list
        .Where(o => !o.Attr.ContainsKey(MissionOnlyAttr))
        .Select(o => o.Attr.Keys.Any(EditorAttrs.Contains)
            ? new Maps.MapObject { Type = o.Type, Name = o.Name, Cls = o.Cls, X = o.X, Y = o.Y, Z = o.Z, Rot = o.Rot, Q = (double[])o.Q.Clone(), Owner = o.Owner, At = o.At, Level = o.Level, Attr = o.Attr.Where(kv => !EditorAttrs.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) }
            : o).ToList();
}
