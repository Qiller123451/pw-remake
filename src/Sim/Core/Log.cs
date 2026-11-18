// Logging for the whole game: one static entry point, named categories, pluggable sinks.
//
//   static readonly Logger L = Log.For("Triggers");
//   L.Info("mission 11 loaded: 73 triggers");
//   L.Debug(() => $"expensive {Describe(x)}");      // the lambda only runs when Debug is on for "Triggers"
//   L.Error("action failed", ex);
//
// Levels are set per category in logging.json (user folder; see docs/LOGGING.md) and can be changed while the game
// runs (debug console: `log Triggers debug`). Every line carries the wall clock, the game time (Log.GameClock, set by
// the simulation) and the category. Sinks: a rolling file in the user folder (always on), the in-memory ring buffer
// behind the in-game log window and the error report, stdout (tests, headless) and the Godot output (added by the
// game project). This file has no Godot dependency: the simulation and the tests use it as it is.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace PwRemake.Sim.Core;

public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4, Fatal = 5, Off = 6 }

/// <summary>One log line, as the sinks receive it.</summary>
public readonly record struct LogEntry(DateTime Time, double GameTime, LogLevel Level, string Category, string Message,
                                       Exception? Exception, int Thread)
{
    public string Format()
    {
        string gt = double.IsNaN(GameTime) ? "        " : $"{GameTime,8:0.00}";
        string s = $"{Time:HH:mm:ss.fff} {gt} {LevelTag(Level)} [{Category}] {Message}";
        return Exception == null ? s : s + Environment.NewLine + Exception;
    }

    public static string LevelTag(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Info => "INF",
        LogLevel.Warn => "WRN", LogLevel.Error => "ERR", LogLevel.Fatal => "FTL", _ => "???",
    };
}

/// <summary>Where log lines go. Implementations must be thread safe (Write can come from any thread).</summary>
public interface ILogSink : IDisposable
{
    string Name { get; }
    void Write(in LogEntry e);
    void Flush() { }
}

/// <summary>A named source of log lines. Cheap: keep one per class in a static field.</summary>
public sealed class Logger
{
    public string Category { get; }
    internal Logger(string category) { Category = category; }

    public bool IsOn(LogLevel l) => Log.IsOn(Category, l);

    public void Trace(string m) => Log.Write(Category, LogLevel.Trace, m, null);
    public void Debug(string m) => Log.Write(Category, LogLevel.Debug, m, null);
    public void Info(string m) => Log.Write(Category, LogLevel.Info, m, null);
    public void Warn(string m, Exception? ex = null) => Log.Write(Category, LogLevel.Warn, m, ex);
    public void Error(string m, Exception? ex = null) => Log.Write(Category, LogLevel.Error, m, ex);
    public void Fatal(string m, Exception? ex = null) => Log.Write(Category, LogLevel.Fatal, m, ex);

    // lazy variants: the message is only built when the level is on
    public void Trace(Func<string> m) { if (IsOn(LogLevel.Trace)) Trace(m()); }
    public void Debug(Func<string> m) { if (IsOn(LogLevel.Debug)) Debug(m()); }

    /// <summary>Warn once per key for the whole session (a missing model, an unknown trigger parameter ...).</summary>
    public void WarnOnce(string key, string m)
    {
        if (Log.FirstTime(Category + "|" + key)) Warn(m);
    }
}

public static class Log
{
    static readonly ConcurrentDictionary<string, Logger> s_loggers = new();
    static readonly ConcurrentDictionary<string, byte> s_once = new();
    static readonly object s_lock = new();
    static ILogSink[] s_sinks = Array.Empty<ILogSink>();
    static Dictionary<string, LogLevel> s_levels = new(StringComparer.OrdinalIgnoreCase);
    static LogLevel s_default = LogLevel.Info;

    /// <summary>The game time shown in every line (seconds since the game started); NaN outside a game.</summary>
    public static Func<double> GameClock { get; set; } = () => double.NaN;

    /// <summary>Counts per level since start (the HUD's warning / error badge, the error report).</summary>
    public static int Warnings, Errors;

    /// <summary>Raised for every Error / Fatal line (the in-game error banner listens).</summary>
    public static event Action<LogEntry>? ErrorLogged;

    public static Logger For(string category) => s_loggers.GetOrAdd(category, c => new Logger(c));

    public static LogLevel DefaultLevel { get => s_default; set => s_default = value; }

    public static void SetLevel(string category, LogLevel level)
    {
        lock (s_lock) { var d = new Dictionary<string, LogLevel>(s_levels, StringComparer.OrdinalIgnoreCase) { [category] = level }; s_levels = d; }
    }

    public static IReadOnlyDictionary<string, LogLevel> Levels => s_levels;

    public static void ClearLevels() { lock (s_lock) s_levels = new(StringComparer.OrdinalIgnoreCase); }

    public static IEnumerable<string> Categories => s_loggers.Keys;

    public static bool IsOn(string category, LogLevel level)
    {
        if (s_sinks.Length == 0) return false;
        // "Campaign.Triggers" falls back to "Campaign", then to the default
        var levels = s_levels;
        string c = category;
        while (true)
        {
            if (levels.TryGetValue(c, out var l)) return level >= l;
            int dot = c.LastIndexOf('.');
            if (dot < 0) return level >= s_default;
            c = c.Substring(0, dot);
        }
    }

    public static void AddSink(ILogSink sink)
    {
        lock (s_lock) { var a = new List<ILogSink>(s_sinks) { sink }; s_sinks = a.ToArray(); }
    }

    public static void RemoveSink(string name)
    {
        lock (s_lock)
        {
            var a = new List<ILogSink>();
            foreach (var s in s_sinks) { if (s.Name == name) s.Dispose(); else a.Add(s); }
            s_sinks = a.ToArray();
        }
    }

    public static T? Sink<T>() where T : class, ILogSink
    {
        foreach (var s in s_sinks) if (s is T t) return t;
        return null;
    }

    public static IReadOnlyList<ILogSink> Sinks => s_sinks;

    internal static bool FirstTime(string key) => s_once.TryAdd(key, 0);

    public static void Write(string category, LogLevel level, string message, Exception? ex)
    {
        if (!IsOn(category, level)) return;
        double gt;
        try { gt = GameClock(); } catch { gt = double.NaN; }
        var e = new LogEntry(DateTime.Now, gt, level, category, message, ex, Environment.CurrentManagedThreadId);
        if (level == LogLevel.Warn) Interlocked.Increment(ref Warnings);
        if (level >= LogLevel.Error) Interlocked.Increment(ref Errors);
        foreach (var s in s_sinks)
        {
            try { s.Write(e); } catch { /* a broken sink must not break the game */ }
        }
        if (level >= LogLevel.Error)
        {
            try { ErrorLogged?.Invoke(e); } catch { }
        }
    }

    public static void Flush()
    {
        foreach (var s in s_sinks) { try { s.Flush(); } catch { } }
    }

    public static void Shutdown()
    {
        lock (s_lock)
        {
            foreach (var s in s_sinks) { try { s.Flush(); s.Dispose(); } catch { } }
            s_sinks = Array.Empty<ILogSink>();
        }
    }

    /// <summary>Hooks unhandled exceptions of the .NET runtime into the log (call once at start).</summary>
    public static void CatchUnhandled()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            Write("Crash", LogLevel.Fatal, "unhandled exception", a.ExceptionObject as Exception);
            Flush();
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Write("Crash", LogLevel.Error, "unobserved task exception", a.Exception);
            a.SetObserved();
        };
    }
}
