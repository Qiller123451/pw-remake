// A saved game (docs/SAVES.md): how the game was started (GameStart, and a mode's own setup) plus everything that
// came into the simulation from outside since - the players' commands with their steps and the interface's answers
// to the mission (PresenterTap) - plus verification hashes of the world every 30 s of game time and what the
// player's view needs (camera, selection, groups, the quest log: opaque JSON the game writes and reads).
// Loading starts the same game again and runs its steps with the same inputs (GameRecorder / SaveReplay): the
// simulation is deterministic (fixed 50 ms steps, the world's own random numbers, commands), so the world is exactly
// the one that was saved; the hashes show it, step by step, or show where it went another way (a save of another
// version of the game).
//
// The file (.pwsave) is a zip archive: header.json (readable: what, when, which mission, tribe, difficulty, game time
// - the load window lists it without reading the rest), game.json (start, commands, answers, hashes, view) and
// thumb.jpg (a small screenshot). header.json "format" / "version" say what it is; a save of another format version is
// refused with a message (SaveFormatException), never half loaded.
//
// Not ported: the browser remake's game/save.js wrote the whole object graph of its running game (and ui/saves.js kept
// it in IndexedDB); docs/SAVES.md says why the port saves the inputs instead.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Game;

/// <summary>A file that is not a saved game of this version (another format version, damaged, not a save at all).</summary>
public sealed class SaveFormatException : Exception { public SaveFormatException(string m, Exception? inner = null) : base(m, inner) { } }

/// <summary>What a load window shows of a save (header.json).</summary>
public sealed class SaveHeader
{
    [JsonPropertyName("format")] public string Format { get; set; } = SaveGame.FormatName;
    [JsonPropertyName("version")] public int Version { get; set; } = SaveGame.FormatVersion;
    /// <summary>the game's version that wrote it (another one may replay it differently: the hashes tell)</summary>
    [JsonPropertyName("build")] public string Build { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>"manual" | "quick" | "auto"</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "manual";
    /// <summary>local date and time (ISO 8601)</summary>
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "Campaign";
    [JsonPropertyName("configuration")] public string Configuration { get; set; } = "";
    [JsonPropertyName("configurationTitle")] public string ConfigurationTitle { get; set; } = "";
    [JsonPropertyName("mission")] public int Mission { get; set; }
    [JsonPropertyName("missionFolder")] public string MissionFolder { get; set; } = "";
    /// <summary>the mission's (or the map's) title</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("tribe")] public string Tribe { get; set; } = "";
    [JsonPropertyName("difficulty")] public int Difficulty { get; set; }
    [JsonPropertyName("debug")] public bool Debug { get; set; }
    /// <summary>seconds of game time</summary>
    [JsonPropertyName("gameTime")] public double GameTime { get; set; }
    [JsonPropertyName("steps")] public long Steps { get; set; }
    [JsonPropertyName("commands")] public int Commands { get; set; }

    [JsonIgnore] public DateTime When => DateTime.TryParse(Date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue;
    public static string Clock(double seconds)
    {
        int s = (int)Math.Floor(seconds);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
}

/// <summary>How the simulation was run (what changes its course besides the start and the inputs): a replay runs it the
/// same way.</summary>
public sealed class SimFlags
{
    /// <summary>the world keeps the human player's fog of war (World.EnableFog): the human's units do not attack what they
    /// do not see</summary>
    [JsonPropertyName("fog")] public bool Fog { get; set; }
    /// <summary>World.TrimEvents (the world drops old events at the start of a step)</summary>
    [JsonPropertyName("trimEvents")] public bool TrimEvents { get; set; }
    /// <summary>World.AnimSounds ("animSound" events of the clips)</summary>
    [JsonPropertyName("animSounds")] public bool AnimSounds { get; set; }

    /// <summary>the flags of the game (GameSession): all on</summary>
    public static SimFlags Game => new() { Fog = true, TrimEvents = true, AnimSounds = true };

    /// <summary>applies the flags to a new world; humans: every human player of the game (a lockstep game's seats:
    /// each gets its own fog, the human first - docs/MULTIPLAYER.md; null: the human only)</summary>
    public void Apply(World.World w, World.Player? human, System.Collections.Generic.IEnumerable<World.Player>? humans = null)
    {
        w.TrimEvents = TrimEvents;
        w.AnimSounds = AnimSounds;
        if (Fog && human != null) w.EnableFog(human);
        if (Fog && humans != null) foreach (var p in humans) w.EnableFog(p);
    }
}

public sealed class SaveGame
{
    public const string FormatName = "pwremake-save";
    /// <summary>the format of game.json; a save of another version is refused (raise it with every change that an older
    /// reader cannot read or that changes what the inputs mean)</summary>
    public const int FormatVersion = 1;
    public const string Extension = ".pwsave";

    public SaveHeader Header = new();
    public GameStart Start = new();
    /// <summary>a mode's own setup besides GameStart (a skirmish's slots ...); null for a campaign mission</summary>
    public JsonNode? Setup;
    public SimFlags Flags = new();
    /// <summary>the steps run when it was saved (World.StepNo)</summary>
    public long Steps;
    /// <summary>the commands applied (and those waiting for the next steps), in order; CommandCodec JSON, each with its
    /// step (Command.Step)</summary>
    public List<JsonNode> Commands = new();
    /// <summary>the interface's answers to the mission (PresenterTap)</summary>
    public List<TapAnswer> Answers = new();
    /// <summary>(step, state hash) every GameRecorder.HashEvery steps</summary>
    public List<(long Step, ulong Hash)> Hashes = new();
    /// <summary>the player's view: camera, selection, groups, the quest log ... (written and read by the game)</summary>
    public JsonNode? View;
    /// <summary>a small screenshot (JPEG), or null</summary>
    public byte[]? Thumbnail;

    static JsonSerializerOptions JOpts => Json.Options;

    // ---------------------------------------------------------------- game.json
    JsonObject GameJson() => new()
    {
        ["format"] = FormatName,
        ["version"] = FormatVersion,
        ["start"] = JsonSerializer.SerializeToNode(Start, JOpts),
        ["setup"] = Setup?.DeepClone(),
        ["flags"] = JsonSerializer.SerializeToNode(Flags, JOpts),
        ["steps"] = Steps,
        ["commands"] = new JsonArray(Commands.Select(c => c.DeepClone()).ToArray()),
        ["answers"] = PresenterTap.ToJson(Answers),
        ["hashes"] = new JsonArray(Hashes.Select(h => (JsonNode)new JsonArray(h.Step, h.Hash.ToString("x16"))).ToArray()),
        ["view"] = View?.DeepClone(),
    };

    static void Check(JsonNode? n, string what)
    {
        if (n == null || n["format"].Str() != FormatName) throw new SaveFormatException($"This is not a saved game of the ParaWorld Remake ({what}).");
        int v = n["version"].Int(-1);
        if (v != FormatVersion)
            throw new SaveFormatException(v > FormatVersion
                ? $"This game was saved by a newer version of the remake (save format {v}; this one reads {FormatVersion})."
                : $"This game was saved by an older version of the remake (save format {v}; this one reads {FormatVersion}) and cannot be loaded any more.");
    }

    // ---------------------------------------------------------------- the file
    /// <summary>writes the save (a temporary file first, then moved over: a crash leaves the old save whole)</summary>
    public void Write(string path)
    {
        Header.Format = FormatName; Header.Version = FormatVersion;
        Header.Steps = Steps; Header.Commands = Commands.Count;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Put(string name, byte[] data, CompressionLevel lv = CompressionLevel.Optimal)
            {
                var e = zip.CreateEntry(name, lv);
                using var s = e.Open();
                s.Write(data);
            }
            Put("header.json", JsonSerializer.SerializeToUtf8Bytes(Header, JOpts));
            Put("game.json", Encoding.UTF8.GetBytes(GameJson().ToJsonString(new JsonSerializerOptions { WriteIndented = false })));
            if (Thumbnail != null) Put("thumb.jpg", Thumbnail, CompressionLevel.NoCompression);
        }
        File.Move(tmp, path, true);
    }

    static byte[]? Entry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e == null) return null;
        using var s = e.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    static ZipArchive OpenZip(string path)
    {
        try { return new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read); }
        catch (InvalidDataException e) { throw new SaveFormatException("The file is not a saved game (not readable as one).", e); }
    }

    /// <summary>the header of a save (and its thumbnail when wanted); throws SaveFormatException for another format</summary>
    public static (SaveHeader Header, byte[]? Thumb) ReadHeader(string path, bool thumb = false)
    {
        using var zip = OpenZip(path);
        var h = Entry(zip, "header.json") ?? throw new SaveFormatException("The saved game has no header.");
        JsonNode? n;
        try { n = JsonNode.Parse(h); } catch (JsonException e) { throw new SaveFormatException("The saved game's header is damaged.", e); }
        Check(n, "header");
        var hdr = n.Deserialize<SaveHeader>(JOpts) ?? throw new SaveFormatException("The saved game's header is damaged.");
        return (hdr, thumb ? Entry(zip, "thumb.jpg") : null);
    }

    /// <summary>reads a whole save; throws SaveFormatException for another format version or a damaged file</summary>
    public static SaveGame Read(string path)
    {
        var (hdr, thumb) = ReadHeader(path, true);
        using var zip = OpenZip(path);
        var g = Entry(zip, "game.json") ?? throw new SaveFormatException("The saved game has no game data.");
        JsonNode? n;
        try { n = JsonNode.Parse(g); } catch (JsonException e) { throw new SaveFormatException("The saved game is damaged.", e); }
        Check(n, "game data");
        try
        {
            var s = new SaveGame
            {
                Header = hdr, Thumbnail = thumb,
                Start = n!["start"].Deserialize<GameStart>(JOpts) ?? new GameStart(),
                Setup = n["setup"]?.DeepClone(),
                Flags = n["flags"].Deserialize<SimFlags>(JOpts) ?? new SimFlags(),
                Steps = (long)n["steps"].Num(),
                Commands = (n["commands"] as JsonArray ?? new JsonArray()).Where(c => c != null).Select(c => c!.DeepClone()).ToList(),
                Answers = PresenterTap.FromJson(n["answers"]),
                View = n["view"]?.DeepClone(),
            };
            foreach (var h in n["hashes"] as JsonArray ?? new JsonArray())
                if (h is JsonArray a && a.Count >= 2) s.Hashes.Add(((long)a[0].Num(), ulong.Parse(a[1].Str(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            return s;
        }
        catch (Exception e) when (e is not SaveFormatException) { throw new SaveFormatException("The saved game is damaged: " + e.Message, e); }
    }

    /// <summary>the saves in a folder, newest first: (file, header) - files that are no saves of this version are listed
    /// with their problem</summary>
    public static List<(string File, SaveHeader? Header, string? Problem)> List(string dir)
    {
        var res = new List<(string, SaveHeader?, string?)>();
        if (!Directory.Exists(dir)) return res;
        foreach (var f in Directory.GetFiles(dir, "*" + Extension))
        {
            try { res.Add((f, ReadHeader(f).Header, null)); }
            catch (Exception e) { res.Add((f, null, e.Message)); }
        }
        return res.OrderByDescending(x => x.Item2?.When ?? File.GetLastWriteTime(x.Item1)).ToList();
    }

    /// <summary>a save the title screen's "Continue Campaign" may take: a single player game - not a skirmish (the
    /// original's MainMenu.usl Init: the newest save whose level is not a multiplayer map; a skirmish is played on one)</summary>
    public static bool IsCampaignSave(SaveHeader h) => h.Mode != nameof(GameMode.Skirmish);

    /// <summary>"Continue Campaign" (MainMenu.usl Init / OnContinueCampaign): the newest readable single player save in a
    /// folder - newest by its date (the original: the file's last write time), null when there is none</summary>
    public static (string File, SaveHeader Header)? NewestCampaign(string dir)
    {
        foreach (var (f, h, _) in List(dir))
            if (h != null && IsCampaignSave(h)) return (f, h);
        return null;
    }

    /// <summary>a file name for a save's name: letters, digits, spaces, - and _ ("Mission 3 - the camp.pwsave")</summary>
    public static string FileName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim()) sb.Append(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' ? ch : '_');
        var s = sb.ToString().Trim();
        if (s.Length == 0) s = "save";
        if (s.Length > 60) s = s[..60].Trim();
        return s + Extension;
    }
}
