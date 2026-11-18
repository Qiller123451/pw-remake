// The journal entry of a campaign mission's loading screen, as the original shows it (Scripts/Game/UI/LoadScreen.usl,
// CLoadScreen.GetLoadingScreenDataFromDB / PrepareInfoData / OnTimer / OnContinue): no Godot types, compiled into the
// tests too.
//
//   * Scripts/Game/misc/LoadingScreens.txt (a PropDB: Root { Levels { single_01 { image, title, text, audio } } ... })
//     names per level (the map's LevelName, lower case, blanks -> '_': single_01 ... single_16, single_b41,
//     single_b41_seas, single_seas_02 ...) the text keys of its journal entry and the sound event that reads it aloud.
//     Base has single_01 - 16 (the tutorial's entry is empty); BoosterPack 1 / 3 add single_b41 - b43 (texts, no
//     voice); MIRAGE adds the _seas variants and single_seas_01 / 02. The game's file system takes the file of the last
//     data folder that has one - GameConfiguration.GameFile does the same.
//   * the texts: the string tables (GameTexts): _ds_JOURNAL_1070_title "JOURNAL ENTRY (COLE)", _ds_JOURNAL_1070_text
//     (lines broken by \{br}); the add-ons' _db_JOURNAL_b41_*, MIRAGE's _SMLST_ / _SMLS_single_seas_*.
//   * the voice: a sound event of Scripts/Server/init/dialogsounds.txt (ds_1070_JOURNAL_ENTRY_(COLE)_01 ->
//     wav '../SeqSounds/Journal_Entry/1070_JOURNAL_ENTRY_(COLE)_01.mp3', relative to Audio/Sound); a localised
//     installation has its own speech below Data/locale/<lang>/Audio, which comes first.
//
// When (LoadScreen.usl): a single player level shows its entry in the info window (caption = the title; hidden when
// the text is empty) and plays the voice once - not for a saved game ("suppress audio on loading game because there
// is no continue button"); a multiplayer game shows the map's info instead. When loading is done, a single player
// level that is no saved game shows the ready button on the loading display and waits for the player (OnContinue
// stops the voice); everything else starts at once. The remake: a co-op / networked campaign mission shows the entry
// but has no button and so - by the original's own rule - no voice: one player reading must not hold the lockstep
// start of the others (docs/MENUS.md "Loading screen").
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PwRemake.Sim.Content;

namespace PwRemake.UI.Loading;

/// <summary>A level's line of LoadingScreens.txt: the picture, the text keys of its journal entry, its sound event.</summary>
public sealed record JournalEntry(string Level, string Image, string TitleKey, string TextKey, string Audio);

/// <summary>How the mission is loaded - what the original's load screen decides by (CLoadScreenInfo.m_iGameType,
/// CLevelInfo.IsMultiplayer / IsSaveGame).</summary>
public enum LoadKind
{
    /// <summary>a campaign mission started anew, single player</summary>
    Campaign,
    /// <summary>a campaign mission from a saved game</summary>
    SavedGame,
    /// <summary>a campaign mission of a networked (co-op) game</summary>
    Network,
    /// <summary>skirmish, the remake's own campaigns: no journal (the screen keeps its hints)</summary>
    Other,
}

/// <summary>What the loading screen shows of the entry: the info window's caption and text (BBCode-safe plain text,
/// paragraphs by '\n') and the sound event to play (null: none).</summary>
public sealed record JournalShow(string Caption, string Text, string? Voice);

/// <summary>A node of a PropDB text file (the original's Root { name { key = 'value' } } tables).</summary>
public sealed class PropNode
{
    public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, PropNode> Children = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>the children in file order</summary>
    public readonly List<(string Name, PropNode Node)> Order = new();

    public PropNode? this[string name] => Children.TryGetValue(name, out var n) ? n : null;
    public string Value(string key) => Values.TryGetValue(key, out var v) ? v : "";

    /// <summary>Reads a PropDB text: `name {` ... `}` nodes and `key = 'value'` lines (quotes ' or ", or a bare word).</summary>
    public static PropNode Parse(string text)
    {
        var root = new PropNode();
        var stack = new Stack<PropNode>();
        stack.Push(root);
        var toks = Tokens(text).ToList();
        for (int i = 0; i < toks.Count; i++)
        {
            var (t, quoted) = toks[i];
            if (!quoted && t == "}") { if (stack.Count > 1) stack.Pop(); continue; }
            if (!quoted && t == "{") { stack.Push(new PropNode()); continue; }   // (a block without a name: read, not kept)
            if (i + 1 < toks.Count && !toks[i + 1].Quoted && toks[i + 1].Text == "{")
            {
                var n = new PropNode();
                stack.Peek().Children[t] = n;
                stack.Peek().Order.Add((t, n));
                stack.Push(n);
                i++;
                continue;
            }
            if (i + 2 < toks.Count && !toks[i + 1].Quoted && toks[i + 1].Text == "=")
            {
                stack.Peek().Values[t] = toks[i + 2].Text;
                i += 2;
            }
        }
        return root;
    }

    static IEnumerable<(string Text, bool Quoted)> Tokens(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c is '{' or '}' or '=') { yield return (c.ToString(), false); i++; continue; }
            if (c is '\'' or '"')
            {
                int e = s.IndexOf(c, i + 1);
                if (e < 0) e = s.Length;
                yield return (s.Substring(i + 1, e - i - 1), true);
                i = e + 1;
                continue;
            }
            int b = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '=' or '\'' or '"')) i++;
            yield return (s[b..i], false);
        }
    }
}

public static class Journal
{
    /// <summary>the loading screens' table, below Data/&lt;folder&gt;/</summary>
    public const string ScreensFile = "Scripts/Game/misc/LoadingScreens.txt";
    /// <summary>the speech's sound events, below Data/&lt;folder&gt;/</summary>
    public const string SoundsFile = "Scripts/Server/init/dialogsounds.txt";

    static readonly ConcurrentDictionary<string, PropNode> s_files = new(StringComparer.Ordinal);

    /// <summary>a level's name as the original's CLoadScreenInfo.Set makes it: lower case, blanks -> '_'</summary>
    public static string LevelName(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace(' ', '_');

    /// <summary>the names a remake mission may have in LoadingScreens.txt: its key (single_01, single_b41_seas ...), then
    /// its map's file name (Base/Maps/Cpn_single_001/single_01.ula -> single_01)</summary>
    public static List<string> LevelNames(string? key, string? map)
    {
        var res = new List<string>();
        void Add(string n) { n = LevelName(n); if (n != "" && !res.Contains(n)) res.Add(n); }
        Add(key ?? "");
        Add(Path.GetFileNameWithoutExtension((map ?? "").Replace('\\', '/')));
        return res;
    }

    /// <summary>the Levels of a LoadingScreens.txt</summary>
    public static Dictionary<string, JournalEntry> Levels(PropNode root)
    {
        var res = new Dictionary<string, JournalEntry>(StringComparer.OrdinalIgnoreCase);
        if (root["Root"]?["Levels"] is not { } levels) return res;
        foreach (var (name, n) in levels.Order)
            res[LevelName(name)] = new JournalEntry(LevelName(name), n.Value("image"), n.Value("title"), n.Value("text"), n.Value("audio"));
        return res;
    }

    /// <summary>the entry of the first of the names that the table has (null: none)</summary>
    public static JournalEntry? Find(IReadOnlyDictionary<string, JournalEntry> levels, IEnumerable<string> names)
    {
        foreach (var n in names)
            if (levels.TryGetValue(LevelName(n), out var e)) return e;
        return null;
    }

    /// <summary>The entry of a mission in a configuration's installation (null: the installation or its table is not
    /// there, or the level has no line).</summary>
    public static JournalEntry? ForMission(GameConfiguration? cfg, string? key, string? map)
    {
        var path = cfg?.GameFile(ScreensFile);
        var root = path == null ? null : ReadProps(path);
        return root == null ? null : Find(Levels(root), LevelNames(key, map));
    }

    /// <summary>What the screen shows of an entry: null for no entry, an empty text (the original hides its info window
    /// then: the tutorial), or a kind of game that never has one. tr(key, fallback): the game's texts.</summary>
    public static JournalShow? Plan(JournalEntry? e, LoadKind kind, Func<string, string, string> tr)
    {
        if (e == null || kind == LoadKind.Other || e.TextKey == "") return null;
        var text = Text(LoadingInfo.Clean(tr(e.TextKey, "")));
        if (text == "") return null;
        var caption = e.TitleKey != "" ? LoadingInfo.Clean(tr(e.TitleKey, "")) : "";
        if (caption == "") caption = LoadingInfo.Clean(tr("_LoadScreen_LoadingGame", "Loading Game..."));
        // the voice only where the player is waited for (a saved game: "no continue button"; co-op: none either)
        var voice = e.Audio.Trim() != "" && WaitForStart(kind) ? e.Audio.Trim() : null;
        return new JournalShow(caption, text, voice);
    }

    /// <summary>The original's ready button: a single player level that is no saved game waits for the player when it
    /// has loaded (LoadScreen.usl OnTimer: !bIsMultiplayer &amp;&amp; !bIsSaveGame) - every campaign mission, the tutorial too.</summary>
    public static bool WaitForStart(LoadKind kind) => kind == LoadKind.Campaign;

    /// <summary>A text of the string tables as plain paragraphs: \{br} a line break, \{p} a paragraph, the other
    /// format tags (\{cogiw_1} ...) dropped; no trailing breaks.</summary>
    public static string Text(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s, @"\\\{br\}", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\\\{p\}", "\n\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\\\{[^}]*\}", "");
        var lines = s.Replace("\r", "").Split('\n').Select(l => Regex.Replace(l, @"[ \t]+", " ").Trim());
        return string.Join("\n", lines).Trim('\n', ' ');
    }

    /// <summary>The file of a speech sound event, relative to Audio/Sound ('../SeqSounds/Journal_Entry/x.mp3'): its
    /// dialogsounds.txt entry (sound_1's wav), else the rule that table follows for a journal entry; null when neither
    /// knows it (MIRAGE's cep_loading_01 has no sound).</summary>
    public static string? SoundFile(PropNode? dialogSounds, string ev)
    {
        if (string.IsNullOrWhiteSpace(ev)) return null;
        var n = dialogSounds?["Root"]?[ev.Trim()];
        if (n != null)
        {
            var snd = n["sound_1"] ?? n.Order.Select(x => x.Node).FirstOrDefault(x => x.Value("wav") != "");
            var wav = snd?.Value("wav") ?? "";
            if (wav != "") return wav.Replace('\\', '/');
        }
        var m = Regex.Match(ev.Trim(), @"^ds_(.*journal.*)$", RegexOptions.IgnoreCase);
        return m.Success ? $"../SeqSounds/Journal_Entry/{m.Groups[1].Value}.mp3" : null;
    }

    /// <summary>The voice's file in the installation: the language's own speech (Data/locale/&lt;lang&gt;/Audio), then the
    /// configuration's data folders (the last that has it); null when missing.</summary>
    public static string? VoicePath(GameConfiguration cfg, string ev)
    {
        var tablePath = cfg.GameFile(SoundsFile);
        var wav = SoundFile(tablePath == null ? null : ReadProps(tablePath), ev);
        if (wav == null) return null;
        var lang = string.IsNullOrEmpty(cfg.Language) ? "uk" : cfg.Language;
        if (!string.IsNullOrEmpty(cfg.Install))
        {
            var loc = PathUtil.FindCaseInsensitive(Path.Combine(cfg.Install, "Data"), $"locale/{lang}/Audio/Sound/{wav}");
            if (loc != null) return loc;
        }
        return cfg.GameFile("Audio/Sound/" + wav);
    }

    /// <summary>a PropDB file, read once per path (the original's text files: Windows-1252 or UTF-8)</summary>
    public static PropNode? ReadProps(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            return s_files.GetOrAdd(path + "|" + stamp, _ =>
            {
                var bytes = File.ReadAllBytes(path);
                string text;
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
                return PropNode.Parse(text);
            });
        }
        catch (Exception) { return null; }
    }
}
