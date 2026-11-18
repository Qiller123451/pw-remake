// The standard log sinks (no Godot): a rolling file, an in-memory ring buffer, stdout. The game project adds a sink
// for the Godot output panel (game/src/App/GodotLogSink.cs). Configuration: LogConfig (logging.json).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PwRemake.Sim.Core;

/// <summary>Writes every line to logs/game_&lt;date&gt;_&lt;time&gt;.log; keeps the newest <c>Keep</c> files.</summary>
public sealed class FileLogSink : ILogSink
{
    public string Name => "file";
    public string FilePath { get; }
    readonly StreamWriter _w;
    readonly object _lock = new();
    int _sinceFlush;

    public FileLogSink(string folder, int keep = 10, string prefix = "game")
    {
        Directory.CreateDirectory(folder);
        // drop old logs first
        var old = new DirectoryInfo(folder).GetFiles(prefix + "_*.log").OrderByDescending(f => f.Name).Skip(Math.Max(0, keep - 1));
        foreach (var f in old) { try { f.Delete(); } catch { } }
        FilePath = Path.Combine(folder, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        _w = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
    }

    public void Write(in LogEntry e)
    {
        string line = e.Format();
        lock (_lock)
        {
            _w.WriteLine(line);
            // errors are flushed at once (a crash right after must not lose them), the rest in batches
            if (e.Level >= LogLevel.Warn || ++_sinceFlush >= 50) { _w.Flush(); _sinceFlush = 0; }
        }
    }

    public void Flush() { lock (_lock) _w.Flush(); }
    public void Dispose() { lock (_lock) { _w.Flush(); _w.Dispose(); } }
}

/// <summary>The last <c>Capacity</c> lines in memory: the in-game log window and the error report read them.</summary>
public sealed class MemoryLogSink : ILogSink
{
    public string Name => "memory";
    public int Capacity { get; }
    readonly LogEntry[] _ring;
    int _next, _count;
    long _total;
    readonly object _lock = new();

    public MemoryLogSink(int capacity = 4000) { Capacity = capacity; _ring = new LogEntry[capacity]; }

    /// <summary>Lines written so far (a viewer polls this to see whether something new came).</summary>
    public long Total { get { lock (_lock) return _total; } }

    public void Write(in LogEntry e)
    {
        lock (_lock)
        {
            _ring[_next] = e;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
            _total++;
        }
    }

    /// <summary>The newest lines (oldest first), filtered.</summary>
    public List<LogEntry> Snapshot(LogLevel min = LogLevel.Trace, string? category = null, int max = int.MaxValue)
    {
        var res = new List<LogEntry>();
        lock (_lock)
        {
            int start = (_next - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
            {
                var e = _ring[(start + i) % Capacity];
                if (e.Level < min) continue;
                if (category != null && !e.Category.StartsWith(category, StringComparison.OrdinalIgnoreCase)) continue;
                res.Add(e);
            }
        }
        return res.Count > max ? res.GetRange(res.Count - max, max) : res;
    }

    public void Dispose() { }
}

/// <summary>stdout (stderr for warnings and errors): tests, headless runs, the dedicated server later.</summary>
public sealed class ConsoleLogSink : ILogSink
{
    public string Name => "console";
    public void Write(in LogEntry e)
    {
        if (e.Level >= LogLevel.Warn) Console.Error.WriteLine(e.Format());
        else Console.WriteLine(e.Format());
    }
    public void Dispose() { }
}

/// <summary>logging.json: levels per category and which sinks are on. Missing file = the defaults below.</summary>
public sealed class LogConfig
{
    [JsonPropertyName("level")] public string Level { get; set; } = "Info";
    /// <summary>"Campaign.Triggers": "Debug" ... a category's level also applies to its sub-categories.</summary>
    [JsonPropertyName("categories")] public Dictionary<string, string> Categories { get; set; } = new();
    [JsonPropertyName("sinks")] public List<string> Sinks { get; set; } = new() { "file", "memory", "godot" };
    [JsonPropertyName("keepFiles")] public int KeepFiles { get; set; } = 10;
    [JsonPropertyName("memoryLines")] public int MemoryLines { get; set; } = 4000;

    public static LogConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<LogConfig>(File.ReadAllText(path), Json.Options) ?? new LogConfig();
        }
        catch (Exception ex) { Console.Error.WriteLine($"logging.json unreadable, using defaults: {ex.Message}"); }
        var cfg = new LogConfig();
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(cfg, Json.Options)); } catch { }
        return cfg;
    }

    public static bool TryParse(string s, out LogLevel l) => Enum.TryParse(s, true, out l);

    /// <summary>Sets up the levels and the sinks that need no engine ("file", "memory", "console").</summary>
    public void Apply(string logFolder)
    {
        Log.DefaultLevel = TryParse(Level, out var d) ? d : LogLevel.Info;
        Log.ClearLevels();
        foreach (var (k, v) in Categories)
            if (TryParse(v, out var l)) Log.SetLevel(k, l);
        if (Sinks.Contains("file") && Log.Sink<FileLogSink>() == null) Log.AddSink(new FileLogSink(logFolder, KeepFiles));
        if (Sinks.Contains("memory") && Log.Sink<MemoryLogSink>() == null) Log.AddSink(new MemoryLogSink(MemoryLines));
        if (Sinks.Contains("console") && Log.Sink<ConsoleLogSink>() == null) Log.AddSink(new ConsoleLogSink());
    }
}
