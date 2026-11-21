// Where the game keeps its things on disk.
//
//   user folder (Godot's user://, on Windows %APPDATA%\Godot\app_userdata\ParaWorld Remake):
//     settings.json, logging.json, logs/, saves/, maps/ (the user's own maps: the map editor's saves, imported .pwmap
//     files - MapFiles.UserDir), campaigns/ (the user's campaigns: UserCampaign.Dir), cache/ (custom maps prepared as missions)
//   content folder: content/ in the repository root (the editor: game/..; an exported game: found from the
//     executable's folder upwards, else next to the executable), or what settings.json "contentRoot" says - the configurations made by the prepare tool (docs/CONTENT.md)
using System.IO;
using Godot;

namespace PwRemake.App;

public static class AppPaths
{
    public static string UserDir => ProjectSettings.GlobalizePath("user://");
    public static string LogsDir => Path.Combine(UserDir, "logs");
    public static string SavesDir => Path.Combine(UserDir, "saves");
    public static string SettingsFile => Path.Combine(UserDir, "settings.json");
    public static string LoggingFile => Path.Combine(UserDir, "logging.json");
    /// <summary>the user's own maps (docs/MAP_EDITOR.md "Saving and sharing"; ids "User/&lt;name&gt;")</summary>
    public static string MapsDir => Path.Combine(UserDir, "maps");
    /// <summary>things made from other files that can be made again (custom maps prepared as missions)</summary>
    public static string CacheDir => Path.Combine(UserDir, "cache");
    /// <summary>the user's campaigns: one folder per campaign with its campaign.lua (docs/CAMPAIGN.md "Custom campaigns")</summary>
    public static string CampaignsDir => Path.Combine(UserDir, "campaigns");

    /// <summary>The folder of the Godot project (editor) or of the executable (exported game).</summary>
    public static string GameDir => OS.HasFeature("editor")
        ? ProjectSettings.GlobalizePath("res://")
        : Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".";

    static string? s_root;
    /// <summary>The repository root: game/.. in the editor. An exported game looks for it from the executable's folder
    /// upwards - the folder with content/, tools/prepare or game/project.godot - so an executable exported into game\,
    /// into the root or into a build folder below it finds the same content and prepare tool as the editor; with none
    /// found (an executable copied elsewhere) its own folder.</summary>
    public static string RootDir => s_root ??= FindRoot();

    static string FindRoot()
    {
        if (OS.HasFeature("editor")) return Path.GetFullPath(Path.Combine(GameDir, ".."));
        for (var d = new DirectoryInfo(GameDir); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tools", "prepare", "prepare.py"))
                || File.Exists(Path.Combine(d.FullName, "game", "project.godot"))
                || Directory.Exists(Path.Combine(d.FullName, "content", "configs")))
                return d.FullName;
        return GameDir;
    }

    /// <summary>The content folder: settings override, else &lt;root&gt;/content.</summary>
    public static string ContentDir(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath!;
        var env = System.Environment.GetEnvironmentVariable("PW_CONTENT");
        if (!string.IsNullOrWhiteSpace(env)) return env!;
        return Path.Combine(RootDir, "content");
    }

    /// <summary>The Lua library every map gets (game/lua).</summary>
    public static string LuaStdDir => OS.HasFeature("editor")
        ? ProjectSettings.GlobalizePath("res://lua")
        : FirstDir(Path.Combine(GameDir, "lua"), Path.Combine(RootDir, "game", "lua"), Path.Combine(RootDir, "lua"));

    static string FirstDir(params string[] dirs)
    {
        foreach (var d in dirs) if (Directory.Exists(d)) return d;
        return dirs[0];
    }
}
