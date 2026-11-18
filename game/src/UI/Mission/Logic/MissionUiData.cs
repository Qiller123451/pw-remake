// What the mission UI shows of a mission's data: the quests' texts (the engine's QuestState carries only the flags and
// the headline), the dialogue scenes, the sequences (subtitle lines, speech, films and the timeline of the original's
// sequencer as pwexport/campaign.py seq_play writes it), the question marks' places, the texts and the mission's
// title / description. Read from a mission folder's world.json, mission.json and texts/<lang>.json (docs/LUA_MAPS.md);
// the JS read the same from G.campaign.data and G.campaign.map.
//
// No Godot types: the tests link this file (tests/PwRemake.Tests/MissionUi*.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;

namespace PwRemake.UI.Mission.Logic;

/// <summary>A dialogue scene (data.dialogs[path]: DialogScene.usl's .dlg converted).</summary>
public sealed class DialogScene
{
    public sealed class Actor
    {
        public string Name = "", Class = "", Icon = "", Display = "";
    }
    public sealed class Frame
    {
        public string Actor = "", Speaker = "", Text = "", Audio = "", Key = "";
    }
    public string Path = "", Id = "";
    public bool Mentor;
    public readonly Dictionary<string, Actor> Actors = new(StringComparer.Ordinal);
    public readonly List<Frame> Frames = new();

    public Actor? ActorOf(Frame f) => Actors.GetValueOrDefault(f.Actor);

    public static DialogScene FromJson(string path, JsonNode? d)
    {
        var s = new DialogScene { Path = path, Id = d?["id"].Str() ?? "", Mentor = d?["mentor"].Bool() ?? false };
        if (d?["actors"] is JsonObject acts)
            foreach (var (name, a) in acts)
                s.Actors[name] = new Actor { Name = name, Class = a?["class"].Str() ?? "", Icon = a?["icon"].Str() ?? "", Display = a?["display"].Str() ?? "" };
        if (d?["frames"] is JsonArray fr)
            foreach (var f in fr)
                s.Frames.Add(new Frame { Actor = f?["actor"].Str() ?? "", Speaker = f?["speaker"].Str() ?? "", Text = f?["text"].Str() ?? "", Audio = f?["audio"].Str() ?? "", Key = f?["key"].Str() ?? "" });
        return s;
    }

    /// <summary>An unknown scene (no data): no frames - it ends on the next tick (engine.js playDialog).</summary>
    public static DialogScene Unknown(string path) => new()
    {
        Path = path, Id = System.IO.Path.GetFileNameWithoutExtension(TrigUtil.FilePart(path)),
        Mentor = path.Replace('\\', '/').Contains("/mentor/", StringComparison.OrdinalIgnoreCase),
    };
}

/// <summary>A subtitle line of a sequence (data.sequences[path].lines).</summary>
public sealed class SequenceLine
{
    public string Key = "", Speaker = "", Text = "", Mp3 = "";
    /// <summary>Its place on the timeline (seconds) and the speech's length; 0 when unknown.</summary>
    public double T, Len;
}

/// <summary>One item of the sequencer's timeline (seq_play items and the actors' items).</summary>
public sealed class TimelineItem
{
    /// <summary>bars, fade, skip, sound, fx, shake, daytime (global); say, act, anim, walk ... (actors).</summary>
    public string K = "";
    public double T, Len;
    public bool On, Loop, Music;
    public string Dir = "", Path = "", Mp3 = "", Key = "";
    public double Vol = 100;
    /// <summary>Map position [x, y, z] or null.</summary>
    public double[]? Pos;
    /// <summary>The raw item (for the hooks: camera shakes, day time, the actors' items).</summary>
    public JsonNode? Raw;

    public static TimelineItem FromJson(JsonNode? n)
    {
        var it = new TimelineItem
        {
            K = n?["k"].Str() ?? "", T = n?["t"].Num() ?? 0, Len = n?["len"].Num() ?? 0, On = n?["on"].Bool() ?? false,
            Loop = n?["loop"].Bool() ?? false, Music = n?["music"].Bool() ?? false, Dir = n?["dir"].Str() ?? "",
            Path = n?["path"].Str() ?? "", Mp3 = n?["mp3"].Str() ?? "", Key = n?["key"].Str() ?? "", Raw = n,
        };
        if (n?["vol"] != null) it.Vol = n["vol"].Num(100);
        if (n?["pos"] is JsonArray p && p.Count >= 2) it.Pos = p.Select(v => v.Num()).ToArray();
        return it;
    }
}

/// <summary>The sequencer's timeline of a sequence (seq_play): start / end, global items, the actors' items. Cameras
/// and cuts stay as JSON for the camera hook (ISequenceStage).</summary>
public sealed class SequenceTimeline
{
    public double Start, End, Length;
    public readonly List<TimelineItem> Items = new();
    /// <summary>Per actor: its items (say, anim, walk ...).</summary>
    public readonly List<List<TimelineItem>> Actors = new();
    public JsonNode? Raw;

    public static SequenceTimeline? FromJson(JsonNode? p)
    {
        if (p == null) return null;
        var t = new SequenceTimeline { Start = p["start"].Num(), End = p["end"].Num(), Length = p["length"].Num(), Raw = p };
        if (p["items"] is JsonArray items) foreach (var i in items) t.Items.Add(TimelineItem.FromJson(i));
        if (p["actors"] is JsonArray acts)
            foreach (var a in acts)
            {
                var l = new List<TimelineItem>();
                if (a?["items"] is JsonArray ai) foreach (var i in ai) l.Add(TimelineItem.FromJson(i));
                t.Actors.Add(l);
            }
        return t;
    }
}

/// <summary>A sequence (data.sequences[path]).</summary>
public sealed class SequenceData
{
    public string Path = "", Id = "";
    public bool File = true;
    public readonly List<SequenceLine> Lines = new();
    /// <summary>The original's films (hs_1120.bik ...).</summary>
    public readonly List<string> Videos = new();
    /// <summary>Speech files of the lines (.lsd lip data next to the .mp3).</summary>
    public readonly List<string> Speech = new();
    public SequenceTimeline? Play;

    public static SequenceData FromJson(string path, JsonNode? s)
    {
        var q = new SequenceData { Path = path, Id = s?["id"].Str() ?? "", File = s?["file"].Bool(true) ?? true, Play = SequenceTimeline.FromJson(s?["play"]) };
        if (s?["lines"] is JsonArray ls)
            foreach (var l in ls)
                q.Lines.Add(new SequenceLine { Key = l?["key"].Str() ?? "", Speaker = l?["speaker"].Str() ?? "", Text = l?["text"].Str() ?? "", Mp3 = l?["mp3"].Str() ?? "", T = l?["t"].Num() ?? 0, Len = l?["len"].Num() ?? 0 });
        if (s?["videos"] is JsonArray vs) foreach (var v in vs) q.Videos.Add(v.Str());
        if (s?["speech"] is JsonArray sp) foreach (var v in sp) q.Speech.Add(v.Str());
        return q;
    }
}

/// <summary>The texts of a quest the log shows (data.quests[i] beyond the engine's QuestState).</summary>
public sealed class QuestText
{
    public string Guid = "", Name = "", Headline = "", Description = "", GroupTitle = "";
}

/// <summary>A question mark of the mission (world.json question_marks): its place, map coordinates.</summary>
public sealed class QuestionMarkPlace
{
    public string Guid = "", Name = "";
    public double X, Y, Z;
    public string State = "STATE_INVISIBLE";
}

public sealed class MissionUiData
{
    static readonly Logger L = Log.For("UI.Mission");

    /// <summary>Campaign mission number (mission.json id): the speech folder Level_&lt;id&gt; (0 = the tutorial).</summary>
    public int Id;
    public string Title = "", Description = "";
    public bool Tutorial;
    public readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal);
    public readonly Dictionary<string, QuestText> Quests = new(StringComparer.Ordinal);
    /// <summary>The quests in the data's order (the engine's own list is in the same order).</summary>
    public readonly List<QuestText> QuestOrder = new();
    public readonly Dictionary<string, DialogScene> Dialogs = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, SequenceData> Sequences = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<QuestionMarkPlace> QuestionMarks = new();

    /// <summary>A text of the mission, else the fallback (else the key) - mission.js text(key, fallback).</summary>
    public string Text(string key, string? fallback = null) => Texts.TryGetValue(key, out var t) ? t : GameText?.Invoke(key) ?? fallback ?? key;

    /// <summary>The installation's own string tables (GameTexts: dialogscenes_uk.ltf's actor names, playernames ...)
    /// behind the mission's texts - the prepare tool writes only the keys the mission's data names; null: not there.</summary>
    public Func<string, string?>? GameText;
    /// <summary>A text of the mission or of the installation, or null.</summary>
    public string? TextOrNull(string key) => Texts.TryGetValue(key, out var t) ? t : GameText?.Invoke(key);

    /// <summary>From a mission folder (world.json, mission.json, texts/&lt;lang&gt;.json - uk, else any).</summary>
    public static MissionUiData Load(string folder, string lang = "uk")
    {
        var world = Json.TryParseFile(System.IO.Path.Combine(folder, "world.json"));
        var mission = Json.TryParseFile(System.IO.Path.Combine(folder, "mission.json"));
        JsonNode? texts = null;
        var td = System.IO.Path.Combine(folder, "texts");
        if (Directory.Exists(td))
        {
            var f = new[] { lang, "uk" }.Select(l => System.IO.Path.Combine(td, l + ".json")).FirstOrDefault(System.IO.File.Exists)
                    ?? Directory.GetFiles(td, "*.json").OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
            if (f != null) texts = Json.TryParseFile(f);
        }
        return FromJson(world, mission, texts);
    }

    /// <summary>From the JSON a mission is loaded with (LuaMission.World / Mission and the texts).</summary>
    public static MissionUiData FromJson(JsonNode? world, JsonNode? mission, JsonNode? texts)
    {
        var d = new MissionUiData();
        if (texts is JsonObject tx) foreach (var (k, v) in tx) d.Texts[k] = v.Str();
        if (mission != null)
        {
            d.Id = mission["id"].Int();
            d.Title = mission["title"].Str(mission["name"].Str());
            d.Description = mission["description"].Str();
            d.Tutorial = mission["map_info"]?["tutorial"].Bool() ?? false;
        }
        if (world?["quests"] is JsonArray qs)
            foreach (var q in qs)
            {
                var t = new QuestText
                {
                    Guid = q?["guid"].Str() ?? "", Name = q?["name"].Str() ?? "", Headline = q?["headline"].Str() ?? "",
                    Description = q?["description"].Str() ?? "", GroupTitle = q?["group_title"].Str() ?? "",
                };
                d.QuestOrder.Add(t);
                if (t.Guid.Length > 0) d.Quests[t.Guid] = t;
                if (t.Name.Length > 0) d.Quests.TryAdd(t.Name, t);
            }
        if (world?["dialogs"] is JsonObject ds) foreach (var (k, v) in ds) d.Dialogs[Norm(k)] = DialogScene.FromJson(k, v);
        if (world?["sequences"] is JsonObject ss) foreach (var (k, v) in ss) d.Sequences[Norm(k)] = SequenceData.FromJson(k, v);
        if (world?["question_marks"] is JsonArray qm)
            foreach (var m in qm)
                d.QuestionMarks.Add(new QuestionMarkPlace
                {
                    Guid = m?["guid"].Str() ?? "", Name = m?["name"].Str() ?? "", X = m?["x"].Num() ?? 0, Y = m?["y"].Num() ?? 0, Z = m?["z"].Num() ?? 0,
                    State = m?["state"].Str("STATE_INVISIBLE") ?? "STATE_INVISIBLE",
                });
        L.Debug(() => $"mission ui data: {d.QuestOrder.Count} quests, {d.Dialogs.Count} dialogues, {d.Sequences.Count} sequences, {d.QuestionMarks.Count} question marks, {d.Texts.Count} texts");
        return d;
    }

    static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

    /// <summary>The scene of a DGSC path: by its path, else by its file name (engine.js findByFile); null = unknown.</summary>
    public DialogScene? Dialog(string path)
    {
        if (Dialogs.TryGetValue(Norm(path), out var s)) return s;
        var f = TrigUtil.FilePart(path);
        return Dialogs.Values.FirstOrDefault(x => string.Equals(TrigUtil.FilePart(x.Path), f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The sequence of a SQNZ path: by its path, else by its file name; null = unknown.</summary>
    public SequenceData? Sequence(string path)
    {
        if (Sequences.TryGetValue(Norm(path), out var s)) return s;
        var f = TrigUtil.FilePart(path);
        return Sequences.Values.FirstOrDefault(x => string.Equals(TrigUtil.FilePart(x.Path), f, StringComparison.OrdinalIgnoreCase));
    }

    public QuestText? QuestOf(QuestState q) => Quests.GetValueOrDefault(q.Guid) ?? Quests.GetValueOrDefault(q.Name);

    public QuestionMarkPlace? QuestionMark(string reference) =>
        QuestionMarks.FirstOrDefault(m => m.Guid == reference) ?? QuestionMarks.FirstOrDefault(m => m.Name == reference);
}
