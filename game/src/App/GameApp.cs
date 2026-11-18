// The app's services, set up once by Boot: settings, logging, the content folder and its game configurations, the
// chosen configuration and its asset source. Screens reach them through GameApp.* (there is one app per process).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.App;

public static class GameApp
{
    static readonly Logger L = Log.For("App");

    public static AppSettings Settings { get; private set; } = new();
    public static string ContentDir { get; private set; } = "";
    public static List<GameConfiguration> Configurations { get; private set; } = new();
    public static GameConfiguration? Config { get; private set; }
    public static AssetSource? Assets { get; private set; }
    public static GodotLogSink? GodotSink { get; private set; }
    public static string Version => "0.1.0";

    /// <summary>Logging first (everything after it can log), then settings and content.</summary>
    public static void Init()
    {
        Directory.CreateDirectory(AppPaths.UserDir);
        var logCfg = LogConfig.Load(AppPaths.LoggingFile);
        logCfg.Apply(AppPaths.LogsDir);
        if (logCfg.Sinks.Contains("godot")) { GodotSink = new GodotLogSink(); Log.AddSink(GodotSink); }
        if (OS.HasFeature("headless") && Log.Sink<ConsoleLogSink>() == null && !logCfg.Sinks.Contains("godot"))
            Log.AddSink(new ConsoleLogSink());
        Log.CatchUnhandled();

        L.Info($"ParaWorld Remake {GameApp.Version} - Godot {Engine.GetVersionInfo()["string"]}, .NET {System.Environment.Version}, " +
               $"{OS.GetName()} {OS.GetVersion()}, {OS.GetProcessorName()} x{OS.GetProcessorCount()}");
        L.Info($"user folder {AppPaths.UserDir}; log file {Log.Sink<FileLogSink>()?.FilePath}");
        L.Info($"command line: {string.Join(" ", OS.GetCmdlineArgs())} {string.Join(" ", OS.GetCmdlineUserArgs())}");

        Settings = AppSettings.Load();
        PwRemake.View.Materials.ModelMaterials.SetOriginalColours(Settings.OriginalColors);
        PwRemake.View.Materials.UnitsBehind.SetEnabled(Settings.ShowUnitsBehind);
        PwRemake.View.Materials.SettingMaterials.SetEnabled(Settings.SettingTextures);
        ReloadContent();
    }

    /// <summary>(Re)reads the configurations of the content folder - after the prepare tool ran, for example.</summary>
    public static void ReloadContent()
    {
        ContentDir = AppPaths.ContentDir(Settings.ContentRoot);
        L.Info($"content folder {ContentDir}");
        Configurations = PwRemake.Sim.Content.Configurations.Discover(ContentDir);
        foreach (var c in Configurations)
            foreach (var p in c.Problems()) L.Warn($"configuration {c.Id}: {p}");
        var pick = Configurations.FirstOrDefault(c => c.Id == Settings.Configuration && c.Problems().Count == 0)
                   ?? Configurations.FirstOrDefault(c => c.Problems().Count == 0)
                   ?? Configurations.FirstOrDefault();
        UseConfiguration(pick);
    }

    public static void UseConfiguration(GameConfiguration? c)
    {
        Config = c;
        Assets = c == null ? null : new AssetSource(c);
        PwRemake.UI.PwTheme.Init(Assets, Configurations.Where(x => x != c && x.Problems().Count == 0).Select(x => new AssetSource(x)));
        if (c != null)
        {
            Settings.Configuration = c.Id;
            L.Info($"using configuration {c}");
        }
    }

    /// <summary>Command-line options after `--` (godot ... -- --mission 11 --tribe SEAS): key -> value ("" for flags).</summary>
    public static Dictionary<string, string> UserArgs()
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var a = OS.GetCmdlineUserArgs();
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            string k = a[i].Substring(2), v = "";
            int eq = k.IndexOf('=');
            if (eq >= 0) { v = k.Substring(eq + 1); k = k.Substring(0, eq); }
            else if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) v = a[++i];
            res[k] = v;
        }
        return res;
    }
}
