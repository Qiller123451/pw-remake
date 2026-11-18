// The in-game log window (F10): the newest lines of the memory log, filtered by level and category, with buttons to
// copy them, open the log folder and change a category's level while the game runs. docs/LOGGING.md.
using System.Linq;
using System.Text;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;

namespace PwRemake.UI;

public partial class LogWindow : PanelContainer
{
    RichTextLabel _text = null!;
    OptionButton _level = null!;
    LineEdit _cat = null!, _cmd = null!;
    long _seen = -1;
    double _t;

    public override void _Ready()
    {
        Theme = PwTheme.Theme;
        AnchorLeft = 0.05f; AnchorRight = 0.95f; AnchorTop = 0.05f; AnchorBottom = 0.7f;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.85f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 });
        var v = new VBoxContainer();
        AddChild(v);
        var bar = new HBoxContainer();
        v.AddChild(bar);
        bar.AddChild(new Label { Text = "Log (F10)" });
        _level = new OptionButton();
        foreach (var l in new[] { "Trace", "Debug", "Info", "Warn", "Error" }) _level.AddItem(l);
        _level.Select(2);
        _level.ItemSelected += _ => _seen = -1;
        bar.AddChild(_level);
        _cat = new LineEdit { PlaceholderText = "category filter", CustomMinimumSize = new Vector2(180, 0) };
        _cat.TextChanged += _ => _seen = -1;
        bar.AddChild(_cat);
        bar.AddChild(PwTheme.Btn("Copy", Copy, 80));
        bar.AddChild(PwTheme.Btn("Log folder", () => OS.ShellOpen(AppPaths.LogsDir), 110));
        _cmd = new LineEdit { PlaceholderText = "log <category> <level>   e.g. log Campaign.Triggers debug", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _cmd.TextSubmitted += Command;
        bar.AddChild(_cmd);
        _text = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, ScrollFollowing = true, SelectionEnabled = true, BbcodeEnabled = true };
        _text.AddThemeFontSizeOverride("normal_font_size", 12);
        _text.AddThemeFontOverride("normal_font", new SystemFont { FontNames = new[] { "Consolas", "DejaVu Sans Mono", "monospace" } });
        v.AddChild(_text);
    }

    void Command(string s)
    {
        var p = s.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 3 && p[0] == "log" && LogConfig.TryParse(p[2], out var lvl))
        {
            Log.SetLevel(p[1], lvl);
            Log.For("App").Info($"log level of {p[1]} set to {lvl}");
        }
        else if (p.Length == 2 && p[0] == "log" && LogConfig.TryParse(p[1], out lvl))
        {
            Log.DefaultLevel = lvl;
            Log.For("App").Info($"default log level set to {lvl}");
        }
        else Log.For("App").Warn($"unknown command: {s}");
        _cmd.Text = "";
        _seen = -1;
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _t += delta;
        if (_t < 0.25) return;
        _t = 0;
        var mem = Log.Sink<MemoryLogSink>();
        if (mem == null) { _text.Text = "(the memory log is off in logging.json)"; return; }
        if (mem.Total == _seen) return;
        _seen = mem.Total;
        var lines = mem.Snapshot((LogLevel)_level.Selected, _cat.Text.Trim() == "" ? null : _cat.Text.Trim(), 600);
        var sb = new StringBuilder();
        foreach (var e in lines)
        {
            string col = e.Level switch { LogLevel.Error or LogLevel.Fatal => "#ff8a7a", LogLevel.Warn => "#ffd27a", LogLevel.Debug or LogLevel.Trace => "#9a9a9a", _ => "#e0d0b0" };
            sb.Append("[color=").Append(col).Append(']').Append(Escape(e.Format())).Append("[/color]\n");
        }
        _text.Text = sb.ToString();
    }

    static string Escape(string s) => s.Replace("[", "[lb]");

    void Copy()
    {
        var mem = Log.Sink<MemoryLogSink>();
        if (mem == null) return;
        DisplayServer.ClipboardSet(string.Join("\n", mem.Snapshot((LogLevel)_level.Selected).Select(e => e.Format())));
    }
}

/// <summary>
/// A banner at the top when an error is logged: the message and the count; a click copies the error report (the
/// game, the configuration, the last 300 log lines) to the clipboard - for bug reports (the browser remake's
/// diagnostics banner).
/// </summary>
public partial class ErrorBanner : Button
{
    int _count;
    string _last = "";

    public override void _Ready()
    {
        Visible = false;
        AnchorLeft = 0.25f; AnchorRight = 0.75f; OffsetTop = 6; OffsetBottom = 36;
        AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color(0.45f, 0.08f, 0.05f, 0.92f), ContentMarginLeft = 10, ContentMarginRight = 10 });
        AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = new Color(0.55f, 0.1f, 0.06f, 0.95f), ContentMarginLeft = 10, ContentMarginRight = 10 });
        AddThemeColorOverride("font_color", Colors.White);
        ClipText = true;
        Log.ErrorLogged += e => CallDeferred(nameof(OnError), e.Category, e.Message);
        Pressed += Copy;
    }

    void OnError(string category, string message)
    {
        _count++;
        _last = $"[{category}] {message}";
        Text = $"{_count} error{(_count > 1 ? "s" : "")}: {_last}   (click: copy the report)";
        Visible = true;
    }

    /// <summary>a problem the player should see that is no logged error (diagnostics.js report: software rendering)</summary>
    public void Report(string text)
    {
        _last = text;
        Text = text + "   (click: copy the report)";
        TooltipText = text;
        Visible = true;
    }

    void Copy()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ParaWorld Remake {GameApp.Version}, Godot {Engine.GetVersionInfo()["string"]}, {OS.GetName()} {OS.GetVersion()}");
        sb.AppendLine($"configuration: {GameApp.Config}");
        sb.AppendLine($"log file: {Log.Sink<FileLogSink>()?.FilePath}");
        sb.AppendLine("---");
        var mem = Log.Sink<MemoryLogSink>();
        if (mem != null) foreach (var e in mem.Snapshot(LogLevel.Debug, null, 300)) sb.AppendLine(e.Format());
        DisplayServer.ClipboardSet(sb.ToString());
        Text = "The report is in the clipboard.";
        Visible = false;
    }
}
