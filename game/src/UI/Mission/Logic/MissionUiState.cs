// Mission UI of the campaign - its logic: quest log and news ticker, dialogue scenes, cutscenes (sequences), timers,
// the info bar, minimap markers and the question marks over quest givers. The trigger engine (src/Sim/Scripting)
// owns the STATE (which quest is visible, how long a timer has left ...) and tells the mission UI what changed; this
// class keeps what is on screen, the queues and their timing. The Godot side (MissionPresenter and its views) draws
// it and forwards input; sounds, pausing and films go through IMissionUiOutput. No Godot types: the tests link this
// file (tests/PwRemake.Tests/MissionUi*.cs).
//
// CONTRACT (mission.js header; the C# names are IMissionHost's, MissionPresenter forwards them here):
//   QuestChanged(quest, change)   change = shown | done | failed | hidden; the engine has set the flags before the call
//   InfoBar(text)                 the resolved info bar line (TriggerEngine.InfoBarText: %n and $(var)); "" hides it
//   timers                        read every frame from the engine's Timers (LevelTimer: Left, Show, Label, Paused)
//   Marker(m) / RemoveMarker(id)  minimap markers (data for the minimap / world view: Markers)
//   PlayDialog(scene, onEnd)      queued; one scene at a time on GAME time (Tick); frame length = its sound, at least
//                                 0.25 s per vowel group; onEnd exactly once
//   PlaySequence(seq, opts, onEnd) a cutscene; the game is paused while it shows; onEnd exactly once, also when skipped
//   Busy                          true while a sequence waits or shows (the engine does not start the next one)
//   Tick(dt)                      game time (dialogue timing, markers)
//   Update(dt)                    real time, once per drawn frame (cutscenes, ticker, title card, timers)
//   SkipAll()                     tests: end every running / queued scene and sequence now
// Nothing is ended inside PlayDialog / PlaySequence: scenes start and end in Tick / Update, so an onEnd never runs
// inside the engine's own action.
//
// Originals: Game/UI/QuestWindow.usl (QW), NewsTicker.usl (NT), DialogScene.usl (DS), TimerWnd.usl (TW),
// MiniMap2.usl (MM), IngameScreen.usl (IS), Server/classes/misc/QuestionMark.usl; behaviour: docs/spec/triggers.md §5.2, §6.
// Ported from remake/src/ui/mission.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;

namespace PwRemake.UI.Mission.Logic;

/// <summary>A speech or sound file playing for the mission UI.</summary>
public interface IUiSound
{
    /// <summary>Seconds of the whole file.</summary>
    double Duration { get; }
    bool Ended { get; }
    void Stop();
}

/// <summary>What the mission UI needs of the game around it: sounds, the pause, the music, films. MissionPresenter
/// implements it with Sfx / GameAudio; the tests record it.</summary>
public interface IMissionUiOutput
{
    /// <summary>An interface sound event (ui_message, ui_quest_new ...): audio.playFirst([name]).</summary>
    void Sound(string name);
    /// <summary>A file below assets/snd/ ("../SeqSounds/Level_1/1020_Cole_01.mp3"): voice = the speech channel, else
    /// effects; offset = seconds into it. Null = missing file or no sound.</summary>
    IUiSound? PlayFile(string file, double vol, bool voice, double offset, bool loop);
    /// <summary>The file of a sound event of sounds.json (its first sound), or null.</summary>
    string? EventFile(string name);
    /// <summary>Sound and music turned down while somebody speaks (DS:164-166, 50 %).</summary>
    void Duck(bool on);
    /// <summary>The music's Sequence switch while a cutscene or film shows (engine/music.js sequence).</summary>
    void MusicSequence(bool on);
    bool Paused { get; set; }
    /// <summary>The view can play this film (hs_1120.bik): a converted file in a format the engine plays.</summary>
    bool FilmPlayable(string film);
}

/// <summary>Options of a cutscene (engine.js playSequence opts).</summary>
public sealed class SequenceOptions
{
    public MapPos? Camera;
    public bool SnapBack;
    public string Title = "";
    /// <summary>The engine's request (camera hook, reveal).</summary>
    public SequenceRequest? Request;
}

/// <summary>A line in the news ticker.</summary>
public sealed class NewsLine
{
    /// <summary>"" | new | good | bad | story.</summary>
    public string Kind = "";
    /// <summary>The bold first line (quest news: "New quest") or "".</summary>
    public string Title = "";
    public string Text = "";
    /// <summary>Atlas name of its icon (nticon_*).</summary>
    public string Icon = "nticon_info_normal";
    /// <summary>The quest it points to (a click opens the log there), or null.</summary>
    public string? Quest;
    /// <summary>Seconds left (real time); it fades in the last second.</summary>
    public double T;
    public bool Fading => T < 1;
}

/// <summary>A minimap marker (MM): data for the minimap and the world view.</summary>
public sealed class MarkerState
{
    public string Id = "", Kind = "FixedColor";
    /// <summary>Map position (follows Obj while it exists).</summary>
    public double X, Y;
    public int Obj = -1;
    public int Color;
    public bool Extended;
    public double Ttl, Interval;
    public int Repeats;
    /// <summary>Game seconds since it was set.</summary>
    public double Age;
    /// <summary>The UI's real clock when it was set (the ring's phase).</summary>
    public double Born;
}

/// <summary>A question mark over a quest giver (QuestionMark.usl): data for the world view.</summary>
public sealed class QuestionMarkState
{
    public string Guid = "", Name = "", State = "STATE_INVISIBLE";
    public string? Tooltip;
    public double X, Y, Z;
    /// <summary>"?" or "!".</summary>
    public string Glyph = "?";
    /// <summary>Fill and dark colour (0xRRGGBB) of the drawn mark.</summary>
    public int Fill, Dark;
    /// <summary>The game's own effect model (questionmark_red ...), when the view has it.</summary>
    public string Model = "";
    public bool Visible => Model.Length > 0;
    /// <summary>
    /// its tooltip: the text the trigger's QMRK action gave it (questiontooltip, a key of the mission's string tables -
    /// e.g. "_TTQ_G_01" - already translated; IngameScreen.usl OnTick shows it for the hovered mark), with the title a
    /// selected mark gets (Info.usl: "_UI_IngaScre_Button_Quest"); null: a hidden mark or one without a text
    /// </summary>
    public (string? Title, string Text)? Tip(string? title = null) =>
        Visible && !string.IsNullOrWhiteSpace(Tooltip) && !Tooltip!.StartsWith('_') ? (title, Tooltip) : null;
}

/// <summary>A level timer as the top of the screen shows it (TW).</summary>
public sealed class TimerView
{
    public string Id = "", Clock = "", Label = "";
    public bool Low, Paused;
}

/// <summary>A quest group of the log: title, quests, points got / possible.</summary>
public sealed class QuestGroup
{
    public string Key = "", Title = "";
    public readonly List<QuestState> Quests = new();
    public double Got, Max;
}

/// <summary>The end of mission screen (menu.js end for a mission).</summary>
public sealed class EndScreenInfo
{
    public bool Won;
    public string Result = "", Title = "", Text = "";
    public readonly List<(QuestState Quest, string State, double Points)> Quests = new();
    public double Earned, Max;
    public double? Points;
    public string PointsLine = "";
    /// <summary>a skirmish's end (menu.js end without a mission): caption "Skirmish", no quests, the epoch reached,
    /// "Keep playing"</summary>
    public bool Skirmish;
    /// <summary>the epoch the player reached (skirmish)</summary>
    public string Epoch = "";
    /// <summary>"View Statistics" is offered (the world keeps statistics)</summary>
    public bool HasStats = true;
}

/// <summary>The dialogue scene playing.</summary>
public sealed class DialogPlay
{
    public DialogScene Scene = null!;
    public Action? OnEnd;
    public int N;
    public int I = -1;
    public double T, Len;
    public bool Hidden;
    /// <summary>waits for the cutscene on screen to end (MissionUiState.HoldScene)</summary>
    public bool Held;
    public IUiSound? Speech;
    public bool Playing => Speech != null && !Speech.Ended;
    public DialogScene.Frame? Frame => I >= 0 && I < Scene.Frames.Count ? Scene.Frames[I] : null;
    /// <summary>What the box shows: name (with the separator), text, portrait icon (atlas name or "" = initial).</summary>
    public string Name = "", Text = "", Portrait = "";
}

public enum CineKind { Lines, Timeline, Film, FilmCard }

/// <summary>The cutscene on screen.</summary>
public sealed class CinePlay
{
    public SequenceData? Seq;
    public SequenceOptions Opts = new();
    public Action? OnEnd;
    public int N;
    public CineKind Kind;
    public List<SequenceLine> Lines = new();
    public int I = -1;
    public double T, Len;
    public IUiSound? Speech;
    public bool Playing => Speech != null && !Speech.Ended;
    public bool WasPaused;
    /// <summary>Films: the list and the one playing.</summary>
    public List<string> Films = new();
    public int FilmIndex = -1;
    public CutsceneTimeline? Player;
    /// <summary>What is shown: subtitle speaker (with the separator) and text, portrait (atlas name or ""), the upper
    /// bar's title, the letterbox bars, the black of a fade (0..1), whether click / Space goes to the next line.</summary>
    public string Speaker = "", Text = "", Portrait = "", Title = "";
    public bool Bars = true, CanNext = true;
    public double Fade;
    /// <summary>A film that cannot be played here: its name (the view says so on the card).</summary>
    public string MissingFilm = "";
}

public sealed class MissionUiState
{
    static readonly Logger L = PwRemake.Sim.Core.Log.For("UI.Mission");

    // minimap marker colours by type (MM:238-262); 'FixedColor' carries its own
    public static readonly Dictionary<string, int> MarkColor = new() { ["Attack"] = 0xff0000, ["SPMainQuest"] = 0xffc864, ["SPOptQuest"] = 0xc8c8c8, ["SPHint"] = 0x007800 };
    // question marks: state -> model, glyph and colours (QuestionMark.usl: questionmark_red / _green / _yellow, exclamation_yellow)
    public static readonly Dictionary<string, (string Model, string Glyph, int Fill, int Dark)> QMark = new()
    {
        ["QM_STATE_RED"] = ("questionmark_red", "?", 0xe8402a, 0x5a0c04), ["QM_STATE_GREEN"] = ("questionmark_green", "?", 0x58d840, 0x0c4a08),
        ["QM_STATE_YELLOW"] = ("questionmark_yellow", "?", 0xffd428, 0x6a4a00), ["EC_STATE_YELLOW"] = ("exclamation_yellow", "!", 0xffd428, 0x6a4a00),
    };
    // news ticker: text key, fallback, icon, sound and kind of a quest change (QW:492-507)
    public static readonly Dictionary<string, (string Key, string Fallback, string Icon, string Sound, string Kind)> QuestNews = new()
    {
        ["shown"] = ("_NT_QuestNew", "New quest", "nticon_quest_new_normal", "ui_quest_new", "new"),
        ["done"] = ("_NT_QuestAccomplished", "Quest accomplished", "nticon_quest_accomplished_normal", "ui_quest_accomplished", "good"),
        ["failed"] = ("_NT_QuestUnaccomplishable", "Quest unaccomplishable", "nticon_quest_unaccomplishable_normal", "ui_quest_unaccomplishable", "bad"),
    };
    public const double NewsTime = 12;        // seconds a ticker line stays (real time)
    public const double TitleTime = 7;        // the mission title card
    public const double CamEase = 1.6;        // seconds the camera takes to the cutscene position
    /// <summary>A film that cannot be played: its card stays this long (the remake's own rule; the JS played the
    /// timeline or the lines instead).</summary>
    public const double FilmCardTime = 4;

    public readonly MissionUiData Data;
    public readonly IMissionUiOutput Out;
    /// <summary>The quests with the engine's flags (TriggerEngine.Quests), in the data's order.</summary>
    public Func<IEnumerable<QuestState>> Quests = () => Array.Empty<QuestState>();
    /// <summary>The engine's level timers (TriggerEngine.Timers.Values), read every Update.</summary>
    public Func<IEnumerable<LevelTimer>> Timers = () => Array.Empty<LevelTimer>();
    /// <summary>Where an object is (map x, y) while it exists - markers follow it; null = gone.</summary>
    public Func<int, (double X, double Y)?> ObjectPos = _ => null;
    /// <summary>The sequencer's camera, actors and effects (out of scope for now: a hook).</summary>
    public ISequenceStage? Stage;
    /// <summary>0 easy, 1 medium, 2 hard (3 = the remake's Nightmare: the log counts no points, the end screen hard's).</summary>
    public int Difficulty = 1;
    /// <summary>The option "Show the mentor's hints in campaign missions" (AppSettings.MentorTexts).</summary>
    public bool MentorTexts = true;
    /// <summary>The option "Show Subtitles" (AppSettings.Subtitles): the timeline scenes' and the films' lines.</summary>
    public bool Subtitles = true;
    /// <summary>The option "Cutscene black bars" (AppSettings.CutsceneBars): off, a timeline scene is full screen - no
    /// letterbox, the subtitles over the picture at the bottom (CutsceneView).</summary>
    public bool CinemaBars;
    /// <summary>Test mode (?manual): no cutscene presentation, every sequence ends on the next tick.</summary>
    public bool Manual;
    /// <summary>The ticker keeps at most this many lines (the JS dropped the oldest when the column was full).</summary>
    public int MaxNews = 7;
    /// <summary>Another menu is open (the cutscenes wait, the log does not open).</summary>
    public bool MenuOpen;

    public readonly List<MarkerState> Markers = new();
    public readonly Dictionary<string, QuestionMarkState> QuestionMarks = new(StringComparer.Ordinal);
    public readonly List<NewsLine> News = new();
    public readonly List<TimerView> TimerViews = new();
    /// <summary>What was asked, for tests: [kind, id, ...] (mission.js log).</summary>
    public readonly List<string[]> LogLines = new();
    public string Bar = "";
    public DialogPlay? Scene;
    public CinePlay? Cine;
    public bool Unread;
    public bool LogOpen;
    public string? LogSel;
    public HashSet<string> Fresh = new(StringComparer.Ordinal);
    public EndScreenInfo? End;
    public double TitleT;
    /// <summary>Real seconds (animations).</summary>
    public double Time;
    /// <summary>A quest-log refresh is due (the view rebuilds the log).</summary>
    public bool LogDirty;

    readonly List<(DialogScene Scene, Action? OnEnd, int N)> scenes = new();     // dialogue scenes: [0] is the one playing
    readonly List<(SequenceData? Seq, SequenceOptions Opts, Action? OnEnd, int N, bool NoFilm)> seqs = new();   // cutscenes: [0] is on screen
    readonly Dictionary<string, string> read = new(StringComparer.Ordinal);      // quest guid -> state seen in the log (QW "ReadStates")
    int seqN;
    bool musSeq, logWasPaused;

    public MissionUiState(MissionUiData data, IMissionUiOutput output)
    {
        Data = data;
        Out = output;
        foreach (var m in data.QuestionMarks)
            QuestionMarks[m.Guid] = new QuestionMarkState { Guid = m.Guid, Name = m.Name, X = m.X, Y = m.Y, Z = m.Z, State = m.State };
        foreach (var m in QuestionMarks.Values) ApplyMark(m);
    }

    public string Text(string key, string? fallback = null) => Data.Text(key, fallback);
    void Log(params string[] l) => LogLines.Add(l);
    static string Clean(string s) => Regex.Replace(s ?? "", @"\s*\n\s*", " ");

    /// <summary>Dialogue scenes and cutscenes waiting or running.</summary>
    public int Queued => scenes.Count + seqs.Count;

    // ------------------------------------------------------------------ helpers
    /// <summary>"vowel groups" of the original's frame length rule (DS:419-436): a vowel counts when it differs from
    /// the last vowel counted, so "banana" is 1 and "Fight now" is 2.</summary>
    public static int VowelGroups(string? text)
    {
        int n = 0; char last = '\0';
        foreach (var ch in (text ?? "").ToLowerInvariant())
            if ("aeiou".IndexOf(ch) >= 0 && ch != last) { last = ch; n++; }
        return n;
    }

    /// <summary>DS:419-465: a frame lasts as long as its sound, at least 0.25 s per vowel group. Without the sound
    /// file the spoken length is estimated from the text (0.6 s + 0.065 s per letter fits the game's voice files: 46
    /// letters = 3.4 s, 16 letters = 1.6 s; at least 1.5 s), so that the mission keeps the pace of the original and
    /// the line can be read.</summary>
    public static double FrameLength(string? text, double sound = 0)
    {
        double rule = 0.25 * VowelGroups(text);
        // (JS string length: UTF-16 code units, as C#'s)
        return sound > 0 ? Math.Max(sound, rule) : Math.Max(Math.Max(rule, 1.5), 0.6 + 0.065 * (text ?? "").Length);
    }

    /// <summary>The reading time of a cutscene line without speech: max(2.4 s, 1.2 s + 0.062 s per letter).</summary>
    public static double LineLength(string text) => Math.Max(2.4, 1.2 + 0.062 * text.Length);

    /// <summary>mm:ss (h:mm:ss) of the seconds left, rounded up.</summary>
    public static string Clock(double s)
    {
        long v = (long)Math.Max(0, Math.Ceiling(s));
        long h = v / 3600, m = v / 60 % 60;
        return (h > 0 ? h + ":" : "") + m.ToString("00") + ":" + (v % 60).ToString("00");
    }

    static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : "";
    /// <summary>The name shown before a line (mission.js speakerName: the original's actor name "_ds_ACTOR_&lt;Speaker&gt;").
    /// A cutscene's speaker is the middle of its line's text key (the export: "_seq_single_16_stina_4" -> "_seq_single_16_stina",
    /// "_seq_7050_babbit_01" -> "babbit"), often in German or as a class: the key's prefix goes, the original's names are
    /// looked up (dialogscenes_uk.ltf, playernames_uk.ltf through MissionUiData.GameText), and what has none is shown as
    /// words ("trader_barry" -> "Trader Barry") - never as a code name.</summary>
    public string SpeakerName(string? sp)
    {
        var raw = (sp ?? "").Trim();
        if (raw.Length == 0) return "";
        var c = Cap(raw);
        if (Data.TextOrNull("_ds_ACTOR_" + c) is { Length: > 0 } direct) return direct;
        var n = Regex.Replace(raw, @"^_?seq_(single_\d+|add|\d+)_", "", RegexOptions.IgnoreCase).Trim('_').ToLowerInvariant();
        if (n.Length == 0) return "";
        if (SpeakerAliases.TryGetValue(n, out var alias))
        {
            if (alias.Length == 0) return "";                       // not a speaker (a line named after its words)
            if (alias.StartsWith("_")) { if (Data.TextOrNull(alias) is { Length: > 0 } a) return a; }
            else return alias;
        }
        var parts = n.Split('_', StringSplitOptions.RemoveEmptyEntries);
        // a speaker already written as a name ("Dragon Clan", "Arch druid": the dialogues' own) stays as it is
        bool named = raw.IndexOf('_') < 0 && raw != raw.ToLowerInvariant();
        foreach (var key in new[] { "_ds_ACTOR_" + Cap(n), "_ds_ACTOR_" + string.Join("_", parts.Select(Cap)), "_ds_ACTOR_" + string.Concat(parts.Select(Cap)) })
            if (Data.TextOrNull(key) is { Length: > 0 } t) return t;
        return named ? raw : string.Join(" ", parts.Select(Cap));
    }

    /// <summary>cutscene speakers whose name the original keeps under another key (or none): the export's speaker
    /// (lower case, without "_seq_..._") -> a text key of the installation, a literal name, or "" for no name</summary>
    static readonly Dictionary<string, string> SpeakerAliases = new(StringComparer.Ordinal)
    {
        ["babbit"] = "_PN_SP16_BABBIT",                // Babbit (playernames_uk.ltf)
        ["taslow"] = "_ds_ACTOR_Taslov",
        ["nordvolk"] = "_ds_ACTOR_Nordmann",           // German: the Norse people -> Norsemen
        ["norsemen"] = "_ds_ACTOR_Hu",
        ["templeguard"] = "_ds_ACTOR_Guard",
        ["haendler_larry"] = "_ds_ACTOR_Larry",        // German: trader Larry
        ["trader_barry"] = "Barry",                     // the trader of mission 3 ("Barry and the Amazons")
        ["amazonen_koenigin"] = "Amazon Queen",         // German: the Amazons' queen (no English name in the tables)
        ["seas_soldier"] = "SEAS Soldier",
        ["pencil_head"] = "",                           // "_seq_1060_pencil_head": the line's own name, not its speaker
    };
    public string Separator => Text("_NT_ACTOR_TEXT_SEPARATOR", ":");

    // ------------------------------------------------------------------ quests: news ticker + log
    public void QuestChanged(QuestState? quest, string change)
    {
        if (quest == null) return;
        Log("quest", quest.Name, change);
        if (QuestNews.TryGetValue(change, out var n))
        {
            News_(Text(n.Key, n.Fallback), quest.Headline.Length > 0 ? quest.Headline : quest.Name, n.Kind, n.Icon, n.Sound, quest.Guid);
            Unread = true;                    // the quest button flashes until the log is opened (QW:509-523)
        }
        if (LogOpen) LogDirty = true;
    }

    /// <summary>A line in the news ticker; kind: "" | new | good | bad | story. sound null = the default
    /// (ui_message), "" = none.</summary>
    public NewsLine News_(string title, string text, string kind = "", string? icon = null, string? sound = null, string? quest = null, double time = 0)
    {
        Log("news", (title.Length > 0 ? title + " " + text : text).Trim(), kind);
        if (sound != "") Out.Sound(sound ?? "ui_message");     // NT:86
        var l = new NewsLine { Title = title, Text = text, Kind = kind, Icon = icon ?? "nticon_info_normal", Quest = quest, T = time > 0 ? time : NewsTime };
        News.Add(l);
        while (News.Count > Math.Max(1, MaxNews)) News.RemoveAt(0);         // the oldest lines go (layout)
        return l;
    }
    public void DropNews(NewsLine n) => News.Remove(n);
    /// <summary>NT:230-262 a click on a line goes where it points (the quest log), then it goes.</summary>
    public void ClickNews(NewsLine n) { if (n.Quest != null) OpenLog(n.Quest); DropNews(n); }

    public static string QuestStateOf(QuestState q) => q.Accomplished ? "done" : q.Unaccomplishable ? "failed" : "open";
    public double Bonus(QuestState q) => Difficulty >= 0 && Difficulty < 3 ? q.Bonus[Difficulty] : 0;

    /// <summary>The visible quests as the log lists them (QW:138-262): the groups of the main quests in order of
    /// appearance, main quests without a group, then the side quests under "Subquests". Each group with its points
    /// got / possible.</summary>
    public List<QuestGroup> QuestGroups()
    {
        var all = Quests().Where(q => q.Visible).ToList();
        var groups = new List<QuestGroup>();
        var by = new Dictionary<string, QuestGroup>(StringComparer.Ordinal);
        void Add(string key, string title, QuestState q)
        {
            if (!by.TryGetValue(key, out var g)) { g = new QuestGroup { Key = key, Title = title }; by[key] = g; groups.Add(g); }
            g.Quests.Add(q); g.Max += Bonus(q); if (q.Accomplished) g.Got += Bonus(q);
        }
        foreach (var q in all)
            if (q.Main && q.Group.Length > 0)
            {
                var gt = Data.QuestOf(q)?.GroupTitle ?? "";
                Add(q.Group, gt.Length > 0 ? gt : Text("_" + q.Group, q.Group), q);
            }
        foreach (var q in all) if (q.Main && q.Group.Length == 0) Add("", "", q);
        foreach (var q in all) if (!q.Main) Add("SubQuestGroup", Text("_SubQuestGroup", "Subquests"), q);
        return groups;
    }

    public void ToggleLog() { if (LogOpen) CloseLog(); else OpenLog(); }
    /// <summary>The quest log: a modal window that pauses the game (QW:121-127); no log during a cutscene or after the
    /// end (IS:790-793). select = a quest guid to show.</summary>
    public bool OpenLog(string? select = null)
    {
        if (Cine != null || End != null || MenuOpen) return false;
        if (select != null) LogSel = select;
        if (!LogOpen)
        {
            LogOpen = true;
            logWasPaused = Out.Paused;
            Out.Paused = true;
            // what the player has not seen yet is shown as new (QW:300-318); opening the log marks it read
            var flat = QuestGroups().SelectMany(g => g.Quests).ToList();
            Fresh = new HashSet<string>(flat.Where(q => read.GetValueOrDefault(q.Guid) != QuestStateOf(q)).Select(q => q.Guid), StringComparer.Ordinal);
        }
        MarkRead();
        Unread = false;
        LogDirty = true;
        return true;
    }
    public void CloseLog()
    {
        if (!LogOpen) return;
        LogOpen = false;
        Out.Paused = logWasPaused;
    }
    void MarkRead() { foreach (var q in QuestGroups().SelectMany(g => g.Quests)) read[q.Guid] = QuestStateOf(q); }

    /// <summary>The quest the log shows: the chosen one, else the first new open one, else the first open, else the first.</summary>
    public QuestState? SelectedQuest()
    {
        var flat = QuestGroups().SelectMany(g => g.Quests).ToList();
        return flat.FirstOrDefault(q => q.Guid == LogSel) ?? flat.FirstOrDefault(q => Fresh.Contains(q.Guid) && QuestStateOf(q) == "open")
               ?? flat.FirstOrDefault(q => QuestStateOf(q) == "open") ?? flat.FirstOrDefault();
    }
    /// <summary>The log's refresh: what it shows is read (questChanged while the log is open).</summary>
    public void LogShown() { MarkRead(); LogDirty = false; }
    public string QuestDescription(QuestState q) => Data.QuestOf(q)?.Description ?? "";
    public string QuestHeadline(QuestState q) => q.Headline.Length > 0 ? q.Headline : Data.QuestOf(q)?.Headline is { Length: > 0 } h ? h : q.Name;

    // ------------------------------------------------------------------ a saved game (docs/SAVES.md; the JS saved MissionUI read, unread, bar, markers)
    /// <summary>what a saved game keeps of the mission UI: the quests read in the log, the flashing quest button, the
    /// quest chosen in the log, the minimap markers (the info bar and the quests are the engine's)</summary>
    public System.Text.Json.Nodes.JsonObject SaveView()
    {
        var rd = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in read) rd[k] = v;
        var mk = new System.Text.Json.Nodes.JsonArray();
        foreach (var m in Markers)
            mk.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = m.Id, ["kind"] = m.Kind, ["x"] = m.X, ["y"] = m.Y, ["obj"] = m.Obj, ["color"] = m.Color, ["extended"] = m.Extended,
                ["ttl"] = m.Ttl, ["interval"] = m.Interval, ["repeats"] = m.Repeats, ["age"] = m.Age, ["born"] = m.Born - Time,
            });
        return new System.Text.Json.Nodes.JsonObject { ["read"] = rd, ["unread"] = Unread, ["logSel"] = LogSel, ["markers"] = mk };
    }
    /// <summary>a saved game's SaveView() back</summary>
    public void RestoreView(System.Text.Json.Nodes.JsonNode? v)
    {
        if (v == null) return;
        read.Clear();
        if (v["read"] is System.Text.Json.Nodes.JsonObject rd) foreach (var (k, x) in rd) read[k] = x.Str();
        Unread = v["unread"].Bool();
        LogSel = v["logSel"] is System.Text.Json.Nodes.JsonValue ls ? ls.Str() : null;
        if (v["markers"] is System.Text.Json.Nodes.JsonArray mk)
        {
            Markers.Clear();
            foreach (var m in mk)
                if (m != null)
                    Markers.Add(new MarkerState
                    {
                        Id = m["id"].Str(), Kind = m["kind"].Str("FixedColor"), X = m["x"].Num(), Y = m["y"].Num(), Obj = m["obj"].Int(-1), Color = m["color"].Int(),
                        Extended = m["extended"].Bool(), Ttl = m["ttl"].Num(), Interval = m["interval"].Num(), Repeats = m["repeats"].Int(), Age = m["age"].Num(), Born = Time + m["born"].Num(),
                    });
        }
    }

    // ------------------------------------------------------------------ info bar, timers
    public void InfoBar(string? text)
    {
        Bar = text ?? "";
        Log("infobar", Bar);
    }

    /// <summary>The engine's timers that are to be seen: a pill with the time and the label under it (TW).</summary>
    public void ShowTimers()
    {
        TimerViews.Clear();
        foreach (var t in Timers())
        {
            if (t == null || !t.Show) continue;
            TimerViews.Add(new TimerView { Id = t.Id, Clock = Clock(t.Left), Label = t.Label ?? "", Low = t.Left <= 10, Paused = t.Paused });
        }
    }

    // ------------------------------------------------------------------ minimap markers
    public void Marker(MapMarker? m)
    {
        if (m == null) return;
        Log("marker", m.Id, m.Kind);
        var pos = m.Obj >= 0 ? ObjectPos(m.Obj) : null;
        Markers.Add(new MarkerState
        {
            Id = m.Id, Kind = m.Kind, Obj = m.Obj, X = pos?.X ?? m.X, Y = pos?.Y ?? m.Y, Extended = m.Extended,
            Ttl = m.Ttl, Repeats = m.Repeats, Interval = m.Interval, Born = Time,
            Color = MarkColor.TryGetValue(m.Kind, out var c) ? c : m.Color,
        });
    }
    public void RemoveMarker(string id) { Log("unmarker", id); Markers.RemoveAll(m => m.Id == id); }
    /// <summary>Is the marker lit now? ttl 0: always. Else it shows for ttl seconds, `repeats` more times with
    /// `interval` seconds of darkness between (MM:224 time_to_life, num_repeats, ms_between), then it is gone.</summary>
    public static bool MarkerOn(MarkerState m)
    {
        if (!(m.Ttl > 0)) return true;
        double cyc = m.Ttl + Math.Max(0, m.Interval);
        return m.Age % cyc < m.Ttl;
    }
    public static bool MarkerDone(MarkerState m) => m.Ttl > 0 && m.Age >= m.Ttl + Math.Max(0, m.Repeats) * (m.Ttl + Math.Max(0, m.Interval));
    /// <summary>The minimap ring running outwards: 0..1 (MM: frequency 0.7; mission.js drawMinimap 0.85 per second).</summary>
    public double RingPhase(MarkerState m, double speed = 0.85) { double k = (Time - m.Born) * speed % 1; return k < 0 ? k + 1 : k; }

    // ------------------------------------------------------------------ question marks
    /// <summary>QMRK: state and tooltip of a question mark (by guid or name). An unknown state hides it.</summary>
    public void SetQuestionMark(string reference, string state, string? tooltip)
    {
        var m = QuestionMarks.GetValueOrDefault(reference) ?? QuestionMarks.Values.FirstOrDefault(x => x.Name == reference);
        if (m == null)
        {
            L.WarnOnce("qm:" + reference, $"question mark {reference} is not in the mission's data");
            m = new QuestionMarkState { Guid = reference, Name = reference };
            QuestionMarks[reference] = m;
        }
        m.State = state;
        if (tooltip != null) m.Tooltip = tooltip;
        Log("questionmark", m.Name, state);
        ApplyMark(m);
    }
    static void ApplyMark(QuestionMarkState m)
    {
        if (QMark.TryGetValue(m.State, out var d)) { m.Model = d.Model; m.Glyph = d.Glyph; m.Fill = d.Fill; m.Dark = d.Dark; }
        else m.Model = "";
    }

    // ------------------------------------------------------------------ dialogue scenes
    public void PlayDialog(DialogScene scene, Action? onEnd)
    {
        Log("dialog", scene.Id);
        scenes.Add((scene, onEnd, seqN++));
    }
    bool MentorShown() => MentorTexts || Data.Tutorial;
    void StartScene()
    {
        var q = scenes[0];
        // mentor hints can be switched off, except in the tutorial; the scene still runs to its end (DS:396-403)
        Scene = new DialogPlay { Scene = q.Scene, OnEnd = q.OnEnd, N = q.N, Hidden = q.Scene.Mentor && !MentorShown() };
        Out.Duck(true);
        NextFrame();
    }
    void NextFrame()
    {
        var S = Scene!;
        StopSound(S.Speech); S.Speech = null;
        S.I++;
        if (S.I >= S.Scene.Frames.Count) { EndScene(); return; }
        var f = S.Scene.Frames[S.I];
        S.T = 0;
        if (S.Hidden) { S.Len = 0.5; return; }          // DS:440: a frame that is not shown is over at the next check (0.5 s)
        S.Len = FrameLength(f.Text);
        var actor = S.Scene.ActorOf(f);
        var name = f.Speaker.Length > 0 ? f.Speaker : actor != null && actor.Display.Length > 0 ? actor.Display : f.Actor;
        Log("frame", S.Scene.Id, S.I.ToString());
        Out.Sound("ui_message");                     // NT:113
        if (TitleT > 1.2) TitleT = 1.2;              // the title card makes room for the first line
        S.Name = name + Separator;
        S.Text = f.Text;
        S.Portrait = UiAtlas.IconFor(actor?.Icon) ?? UiAtlas.IconFor(actor?.Class) ?? "";
        // the frame lasts as long as its speech, at least 0.25 s per vowel group (DS:419-465)
        var file = FrameSound(S.Scene, f);
        if (file != null)
        {
            var snd = Out.PlayFile(file, 100, true, 0, false);
            if (snd != null) { S.Speech = snd; S.Len = Math.Max(snd.Duration, 0.25 * VowelGroups(f.Text)); }
        }
    }
    /// <summary>The sound file of a dialogue frame, relative to Audio/Sound: the sound event `audio` of
    /// dialogsounds.txt if the built sounds.json has it, else the rule that table follows
    /// ('../SeqSounds/Level_&lt;n&gt;/&lt;scene&gt;_&lt;Actor&gt;_&lt;nn&gt;.mp3', mentor hints '../SeqSounds/Mentor/&lt;L01B01&gt;.mp3').</summary>
    public string? FrameSound(DialogScene sc, DialogScene.Frame f)
    {
        var a = f.Audio.Length > 0 ? f.Audio : sc.Mentor && sc.Id.Length > 0 ? sc.Id : "";
        if (a.Length == 0) return null;
        var ev = Out.EventFile(a.ToLowerInvariant());
        if (ev != null) return ev;
        a = Regex.Replace(a, "^ds_", "", RegexOptions.IgnoreCase);
        var folder = sc.Mentor || Regex.IsMatch(a, @"^(l\d+b\d+|counselor_)", RegexOptions.IgnoreCase) ? "Mentor" : Data.Id > 0 ? "Level_" + Data.Id : "0_Tut_Level";
        return $"../SeqSounds/{folder}/{a}.mp3";
    }
    void EndScene()
    {
        var S = Scene;
        if (S == null) return;
        StopSound(S.Speech); S.Speech = null;
        Scene = null;
        int i = scenes.FindIndex(x => x.N == S.N); if (i >= 0) scenes.RemoveAt(i);
        if (scenes.Count == 0) Out.Duck(false);
        Log("dialogEnd", S.Scene.Id);
        S.OnEnd?.Invoke();
    }
    /// <summary>A cutscene begins: the dialogue scene that is speaking falls silent and waits (its frame said again at
    /// the cutscene's end: ResumeScene).</summary>
    void HoldScene()
    {
        var S = Scene;
        if (S == null || S.Held) return;
        S.Held = true;
        StopSound(S.Speech); S.Speech = null;
        Log("dialogHeld", S.Scene.Id);
    }
    /// <summary>The cutscene is over: the held scene goes on with the frame it was on, from its start.</summary>
    void ResumeScene()
    {
        var S = Scene;
        if (S == null || !S.Held || Cine != null) return;
        S.Held = false;
        Out.Duck(true);
        Log("dialogResumed", S.Scene.Id);
        S.I--;
        NextFrame();
    }
    /// <summary>The close button of the dialogue box: the scene is over (DS:340-352 OnClose).</summary>
    public void CloseDialog() { if (Scene != null) EndScene(); }
    static void StopSound(IUiSound? s) { try { s?.Stop(); } catch (Exception) { /* already over */ } }

    // ------------------------------------------------------------------ cutscenes
    public void PlaySequence(SequenceData? seq, SequenceOptions? opts, Action? onEnd)
    {
        Log("sequence", seq?.Id ?? "");
        seqs.Add((seq, opts ?? new SequenceOptions(), onEnd, seqN++, false));
    }
    public bool Busy => seqs.Count > 0;

    /// <summary>Starts the cutscenes that wait. One without lines (or without a file) is over at once; in Manual
    /// mode every one is.</summary>
    void Pump()
    {
        while (Cine == null && seqs.Count > 0)
        {
            var q = seqs[0];
            var lines = q.Seq?.Lines.Where(l => l.Text.Length > 0).ToList() ?? new List<SequenceLine>();
            var films = q.NoFilm || q.Seq == null ? new List<string>() : q.Seq.Videos.ToList();
            var play = q.Seq?.Play is { End: > 0 } p && films.Count == 0 ? p : null;
            if (Manual || (lines.Count == 0 && films.Count == 0 && play == null))
            {
                seqs.RemoveAt(0); Log("sequenceEnd", q.Seq?.Id ?? ""); q.OnEnd?.Invoke(); continue;
            }
            if (MenuOpen || LogOpen || End != null) return;            // not under an open menu
            var c = new CinePlay { Seq = q.Seq, Opts = q.Opts, OnEnd = q.OnEnd, N = q.N, Lines = lines, WasPaused = Out.Paused, Title = q.Opts.Title };
            if (films.Count > 0)
            {
                // a sequence that is a film of the original (CBink: hs_2560.bik ...): the films one after the other,
                // the sequence's lines spread over them as subtitles. Films this engine cannot play: the sequence is
                // shown with its lines instead (mission.js filmMissing), a film without lines as a card for a moment.
                var playable = films.Where(Out.FilmPlayable).ToList();
                if (playable.Count == 0)
                {
                    Log("filmMissing", q.Seq!.Id);
                    seqs[0] = (q.Seq, q.Opts, q.OnEnd, q.N, true);
                    if (lines.Count == 0)
                    {
                        c.Kind = CineKind.FilmCard; c.MissingFilm = string.Join(", ", films); c.Len = FilmCardTime; c.CanNext = false;
                        Begin(c);
                        return;
                    }
                    continue;          // (pumped again: lines, or the timeline)
                }
                c.Kind = CineKind.Film; c.Films = playable; c.CanNext = false;
                Begin(c);
                NextFilm();
                return;
            }
            if (play != null)
            {
                c.Kind = CineKind.Timeline; c.CanNext = false;
                c.Player = new CutsceneTimeline(this, c, play);
                Begin(c);
                Log("timeline", q.Seq!.Id);
                try { c.Player.Start(); } catch (Exception e) { L.Warn($"cutscene {q.Seq.Id}", e); EndCine(); }
                return;
            }
            c.Kind = CineKind.Lines;
            Begin(c);
            NextLine();
        }
    }
    void Begin(CinePlay c)
    {
        HoldScene();
        Cine = c;
        Out.Paused = true;
        Out.Duck(true);
        Stage?.Begin(c);
    }
    void NextLine()
    {
        var c = Cine!;
        StopSound(c.Speech); c.Speech = null;
        c.I++;
        if (c.I >= c.Lines.Count) { EndCine(); return; }
        var l = c.Lines[c.I];
        var text = Clean(l.Text);
        c.T = 0;
        c.Len = LineLength(text);          // reading time; replaced by the speech length if there is a file
        Log("line", c.Seq?.Id ?? "", c.I.ToString());
        c.Portrait = UiAtlas.IconFor(l.Speaker) ?? "";
        c.Speaker = l.Speaker.Length > 0 ? SpeakerName(l.Speaker) + Separator : "";
        c.Text = text;
        var file = c.Seq != null ? LineSound(c.Seq, l) : null;
        if (file != null)
        {
            var s = Out.PlayFile(file, 100, true, 0, false);
            if (s != null) { c.Speech = s; c.Len = s.Duration + 0.4; }
        }
    }
    /// <summary>The speech file of a subtitle line: the line's own mp3 (the converter writes it), else the sequence's
    /// speech entry '&lt;...&gt;/SeqSounds/Level_1/1040/1040_Stina_01.lsd' (lip data, the .mp3 lies next to it) whose
    /// name is the line's key '_seq_1040_stina_01'.</summary>
    public static string? LineSound(SequenceData seq, SequenceLine l)
    {
        if (l.Mp3.Length > 0) return "../SeqSounds/" + l.Mp3;
        var key = Regex.Replace(l.Key.ToLowerInvariant(), "^_seq_", "");
        if (key.Length == 0) return null;
        foreach (var p in seq.Speech)
        {
            var m = Regex.Match(p.Replace('\\', '/'), @"SeqSounds/(.*)\.(lsd|mp3|wav)$", RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Split('/').Last().ToLowerInvariant() == key) return $"../SeqSounds/{m.Groups[1].Value}.mp3";
        }
        return null;
    }
    // ---- films
    void NextFilm()
    {
        var c = Cine!;
        c.FilmIndex++;
        if (c.FilmIndex >= c.Films.Count) { EndCine(); return; }
        Log("film", c.Seq?.Id ?? "", c.Films[c.FilmIndex]);
        FilmSubtitle(0, 0);
    }
    /// <summary>The view: the film playing has ended (or failed).</summary>
    public void FilmEnded() { if (Cine?.Kind == CineKind.Film) NextFilm(); }
    /// <summary>The share of the subtitle lines that belongs to the current film, spread evenly over its length.</summary>
    public void FilmSubtitle(double time, double duration)
    {
        var c = Cine;
        if (c == null || c.Kind != CineKind.Film) return;
        int n = c.Films.Count, Ln = c.Lines.Count;
        int a = c.FilmIndex * Ln / n, b = (c.FilmIndex + 1) * Ln / n;
        int i = duration > 0 && b > a ? a + Math.Min(b - a - 1, (int)Math.Floor(time / duration * (b - a))) : -1;
        var l = i >= 0 && Subtitles ? c.Lines[i] : null;      // (the option "Show Subtitles" covers the films' lines too)
        c.Speaker = l != null && l.Speaker.Length > 0 ? SpeakerName(l.Speaker) + Separator : "";
        c.Text = l != null ? Clean(l.Text) : "";
    }
    /// <summary>Cutscene: the next line (click, Space).</summary>
    public void Next() { if (Cine is { Kind: CineKind.Lines }) { Out.Sound("ui_click"); NextLine(); } }
    /// <summary>Cutscene: its end (Esc).</summary>
    public void Skip() { if (Cine != null) EndCine(); }
    void EndCine()
    {
        var c = Cine;
        if (c == null) return;
        StopSound(c.Speech); c.Speech = null;
        c.Player?.Stop();
        Cine = null;
        int i = seqs.FindIndex(x => x.N == c.N); if (i >= 0) seqs.RemoveAt(i);
        // G:1835-1853: the camera returns to where it was (snap_cam_back) or stays at the scene's place
        Stage?.End(c);
        if (scenes.Count == 0) Out.Duck(false);
        if (seqs.Count == 0) ResumeScene();        // (another cutscene waiting: the scene waits for it too)
        Out.Paused = c.WasPaused;
        if (LogOpen) logWasPaused = c.WasPaused;
        Log("sequenceEnd", c.Seq?.Id ?? "");
        c.OnEnd?.Invoke();
    }
    void UpdateCine(double dt)
    {
        var c = Cine;
        if (c == null || MenuOpen || c.Kind == CineKind.Film) return;
        if (c.Kind == CineKind.Timeline)
        {
            try { c.Player!.Update(Math.Min(dt, 0.1)); }
            catch (Exception e) { L.Warn("cutscene", e); c.Player!.Done = true; }
            if (c.Player!.Done) EndCine();
            return;
        }
        Stage?.Update(c, dt);
        c.T += dt;
        if (c.T >= c.Len && !(c.Playing && c.T < c.Len + 20))
        {
            if (c.Kind == CineKind.FilmCard) EndCine(); else NextLine();
        }
    }

    // ------------------------------------------------------------------ title card, end screen
    public void ShowTitle()
    {
        if (Data.Title.Length == 0) return;
        TitleT = TitleTime;
        Log("title", Data.Title);
    }

    /// <summary>The end screen (menu.js end for a mission): the game's result line, the mission title, the reason of
    /// a defeat, the visible quests with their result and the bonus points (points null: the sum of the accomplished
    /// quests "of" the possible).</summary>
    public EndScreenInfo EndMission(bool won, string text, double? points = null)
    {
        var e = new EndScreenInfo
        {
            Won = won, Text = text ?? "", Points = points, Title = Data.Title,
            Result = won ? Text("_GAOV_Accomplished", "Mission successful!") : Text("_GAOV_Failed", "Mission has failed."),
        };
        int key = Math.Min(Math.Max(Difficulty, 0), 2);
        foreach (var q in Quests().Where(q => q.Visible))
        {
            double p = q.Bonus[key];
            e.Quests.Add((q, QuestStateOf(q), p));
            e.Max += p;
            if (q.Accomplished) e.Earned += p;
        }
        if (points != null || e.Max > 0) e.PointsLine = $"Bonus points: {Num(points ?? e.Earned)}" + (points == null && e.Max > 0 ? $" of {Num(e.Max)}" : "");
        End = e;
        if (LogOpen) CloseLog();
        Log("end", won ? "won" : "lost", e.Text);
        return e;
    }
    static string Num(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ time
    /// <summary>Game time, from the simulation step.</summary>
    public void Tick(double dt)
    {
        Pump();
        // dialogue scenes: one at a time, in the order asked for (DS:19-45); none while a cutscene shows - the original
        // hides the dialogue scenes for a sequence and lets them go on after it (MusicMgr.usl:386-417 OnSequenceStart
        // "stop Dialogs" / OnSequenceEnd "continue Dialogs": DialogSceneMgr.SetVisible), so a mentor hint or a scene a
        // trigger starts during the cutscene waits for its end, and a scene that was speaking stops and says its line
        // again afterwards (HoldScene / ResumeScene). (The game is paused under a cutscene, but a speech already playing
        // went on over it - the mentor talking in mission 1's sc_1050 - and a networked game's tick may come meanwhile.)
        if (Cine == null) TickScenes(dt);
        if (Markers.Count > 0)
        {
            bool gone = false;
            foreach (var m in Markers)
            {
                m.Age += dt;
                if (m.Obj >= 0 && ObjectPos(m.Obj) is { } p) { m.X = p.X; m.Y = p.Y; }
                if (MarkerDone(m)) gone = true;
            }
            if (gone) Markers.RemoveAll(MarkerDone);
        }
    }

    void TickScenes(double dt)
    {
        if (Scene is { Held: true }) ResumeScene();
        int guard = 0;
        while (Scene == null && scenes.Count > 0 && guard++ < 64) StartScene();
        var S = Scene;
        if (S == null) return;
        S.T += dt;
        // the frame is over when its time is up and its sound has ended (DS:372-375); a sound that never reports its end
        // does not hold the scene for ever
        if (S.T >= S.Len && !(S.Playing && S.T < S.Len + 20)) NextFrame();
    }

    /// <summary>Real time, once per drawn frame.</summary>
    public void Update(double dt)
    {
        Time += dt;
        Pump();                                    // also while the game is paused
        // the music's Sequence switch while a cutscene or film shows (engine/music.js sequence)
        bool seq = Cine != null;
        if (seq != musSeq) { musSeq = seq; Out.MusicSequence(seq); }
        UpdateCine(dt);
        for (int i = News.Count - 1; i >= 0; i--)
        {
            News[i].T -= dt;
            if (News[i].T <= 0) News.RemoveAt(i);
        }
        if (TitleT > 0) TitleT = Math.Max(0, TitleT - dt);
        ShowTimers();
    }

    /// <summary>Tests: end every running / queued scene and sequence now, in the order they were asked for.</summary>
    public void SkipAll()
    {
        int guard = 0;
        while ((scenes.Count > 0 || seqs.Count > 0) && guard++ < 1000)
        {
            bool hasD = scenes.Count > 0, hasS = seqs.Count > 0;
            if (hasD && (!hasS || scenes[0].N < seqs[0].N))
            {
                var d = scenes[0];
                if (Scene != null && Scene.N == d.N) EndScene();
                else { scenes.RemoveAt(0); Log("dialogEnd", d.Scene.Id); d.OnEnd?.Invoke(); }
            }
            else if (Cine != null && Cine.N == seqs[0].N) EndCine();
            else { var s = seqs[0]; seqs.RemoveAt(0); Log("sequenceEnd", s.Seq?.Id ?? ""); s.OnEnd?.Invoke(); }
        }
    }
}
