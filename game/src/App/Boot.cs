// The first node of the game (scenes/Boot.tscn): sets up the app (logging, settings, content), keeps the overlays
// every screen shares (log window F10, error banner, performance line F9) and switches the screens: title ->
// campaign menu -> game -> back. A screen is a Control (menus) or a Node3D scene (the game) added under Screen.
//
// Command line (after --): --config <id> picks the configuration, --mission <n> [--tribe T] [--difficulty d]
// [--debug] starts a mission at once (tests, quick starts), --skirmish [map] [--players ...] a skirmish (SkirmishMenu),
// --editor [--map <name>] the map editor (docs/MAP_EDITOR.md), --quit-after <seconds> ends the run (tests),
// --screenshot <file> saves a picture before quitting; --no-intro skips the start films (--intro plays them in a test run).
using System;
using System.Linq;
using Godot;
using PwRemake.Sim.Core;
using PwRemake.UI;

namespace PwRemake.App;

public partial class Boot : Node
{
    static readonly Logger L = Log.For("App");
    public static Boot Instance { get; private set; } = null!;

    Node _screenRoot = null!;
    CanvasLayer _overlay = null!;
    LogWindow _logWindow = null!;
    ErrorBanner _banner = null!;
    Label _perf = null!;
    double _quitAt = -1, _clock;
    string? _shotFile;

    public override void _EnterTree()
    {
        Instance = this;
        PwRemake.Sim.Core.SimCulture.Use();          // (numbers as text the same on every machine: docs/MULTIPLAYER.md "Determinism")
        GameApp.Init();
    }

    public override void _Ready()
    {
        GetWindow().Title = "ParaWorld Remake";
        GetTree().AutoAcceptQuit = false;                // (closing the window asks first when the map editor has unsaved changes: RequestQuit)
        _screenRoot = new Node { Name = "Screen" };
        AddChild(_screenRoot);
        _overlay = new CanvasLayer { Name = "Overlay", Layer = 100 };
        AddChild(_overlay);
        _banner = new ErrorBanner();
        _overlay.AddChild(_banner);
        _logWindow = new LogWindow { Visible = false };
        _overlay.AddChild(_logWindow);
        _perf = new Label { Visible = false, Position = new Vector2(8, 4) };
        _perf.AddThemeColorOverride("font_color", new Color(0.8f, 1f, 0.6f));
        _perf.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _overlay.AddChild(_perf);
        ApplyDisplaySettings();
        CheckRenderer();

        var args = GameApp.UserArgs();
        if (args.TryGetValue("quit-after", out var qa) && double.TryParse(qa, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q)) _quitAt = q;
        if (args.TryGetValue("screenshot", out var shot)) _shotFile = shot;
        if (args.TryGetValue("config", out var cid))
        {
            var c = GameApp.Configurations.Find(x => x.Id == cid);
            if (c != null) GameApp.UseConfiguration(c); else L.Warn($"--config {cid}: no such configuration");
        }

        AddChild(new PwRemake.View.Audio.GameAudio());          // the game's sound lives as long as the app
        void MenuMusic()
        {
            if (GameApp.Config != null)
                Callable.From(() => { try { PwRemake.View.Audio.GameAudio.I.Menu(); } catch (Exception ex) { L.Error("menu music", ex); } }).CallDeferred();
        }
        // the start films (IntroScreen: the original's logos and intro) before the title screen - not when the command
        // line starts something else, not in test runs (--quit-after, --screenshot) unless --intro asks for them
        string[] starts = { "screen", "editor", "custom-map", "mission", "skirmish", "sequel", "scenario", "army-mission", "load", "continue", "host", "join" };
        bool test = args.ContainsKey("quit-after") || args.ContainsKey("screenshot") || args.ContainsKey("shots");
        var intro = GameApp.Configurations.Count > 0 && !args.ContainsKey("no-intro") && GameApp.Settings.IntroFilms
                    && !starts.Any(args.ContainsKey) && (!test || args.ContainsKey("intro"))
            ? IntroScreen.Films(GameApp.Assets) : new System.Collections.Generic.List<string>();
        if (GameApp.Configurations.Count == 0) { MenuMusic(); Show(new SetupScreen()); }
        else if (intro.Count > 0) Show(new IntroScreen(intro, () => { MenuMusic(); Show(new TitleScreen()); }));
        else { MenuMusic(); Show(new TitleScreen()); }

        if (args.TryGetValue("screen", out var scr))          // tests and screenshots of single menus
            Show(scr switch { "campaign" => new CampaignMenu(), "sequel" => new PwRemake.UI.Sequel.SequelMenu(), "scenarios" => new PwRemake.UI.Sequel.ScenarioMenu(), "skirmish" => new SkirmishMenu(), "army" => SkirmishMenu.ArmyOf(0), "debug" => new DebugOptionsWindow(() => new TitleScreen()), "setup" => new SetupScreen(), "load" => new LoadGameScreen(),
                "options" => new OptionsScreen(tab: args.TryGetValue("options-tab", out var ot) && int.TryParse(ot, out var oti) ? oti : 0), "controls" => new ControlsScreen(), "multiplayer" => new MultiplayerMenu(), "custom" => new CampaignMenu(1), _ => new TitleScreen() });
        if (args.ContainsKey("editor") && GameApp.Config != null) Show(new PwRemake.Editor.MapEditor());       // the map editor (docs/MAP_EDITOR.md)
        else if (args.TryGetValue("custom-map", out var cm) && GameApp.Config != null)                  // a user's map with triggers as a mission
        {
            var path = System.IO.Path.IsPathRooted(cm) ? cm : PwRemake.Sim.Maps.MapFiles.Find(GameApp.Config, cm.Contains('/') ? cm : "User/" + cm);
            if (path != null) CustomPlay.Play(path); else L.Error($"--custom-map {cm}: no such map in My maps");
        }
        else if (args.ContainsKey("mission")) CampaignMenu.QuickStart(args);
        else if (args.ContainsKey("skirmish")) SkirmishMenu.QuickStart(args);
        else if (args.ContainsKey("sequel") || args.ContainsKey("scenario")) PwRemake.UI.Sequel.SequelMenu.QuickStart(args);   // (the remake's own campaigns: docs/SEQUEL.md)
        else if (args.TryGetValue("army-mission", out var am)) CampaignMenu.ArmyFor(am);
        else if (args.TryGetValue("load", out var lf) && lf.Length > 0) GameSession.LoadGame(lf);       // a saved game (docs/SAVES.md)
        else if (args.ContainsKey("host") || args.ContainsKey("join")) MultiplayerMenu.QuickStart(args); // a networked game (docs/MULTIPLAYER.md)
        else if (args.ContainsKey("continue") && PwRemake.Sim.Game.SaveGame.NewestCampaign(AppPaths.SavesDir) is { } cs)
            GameSession.LoadGame(cs.File);                                  // the title screen's Continue Campaign
    }

    public void ApplyDisplaySettings()
    {
        var s = GameApp.Settings;
        Engine.MaxFps = s.FpsCap;
        DisplayServer.WindowSetVsyncMode(s.VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        if (!OS.HasFeature("headless") && DisplayServer.GetName() != "headless") ApplyWindowMode(s.WindowMode);
        GetViewport().Scaling3DScale = (float)Math.Clamp(s.RenderScale, 0.25, 2.0);
        if (_perf != null) _perf.Visible = s.Perf;
    }

    /// <summary>Replace the current screen.</summary>
    public void Show(Node screen)
    {
        foreach (var c in _screenRoot.GetChildren()) { _screenRoot.RemoveChild(c); c.QueueFree(); }
        _screenRoot.AddChild(screen);
        L.Info($"screen: {screen.GetType().Name}");
    }

    public override void _Process(double delta)
    {
        GameApp.GodotSink?.Pump();
        _clock += delta;
        if (_perf.Visible)
            _perf.Text = $"{Engine.GetFramesPerSecond()} fps · {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:0.0} ms process · " +
                         $"{Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} draws · {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576:0} MB" +
                         (GameSession.Current is { } gs ? "\n" + gs.PerfLine : "");
        if (_quitAt >= 0 && _clock >= _quitAt)
        {
            _quitAt = -1;
            if (_shotFile != null) SaveScreenshot(_shotFile);
            L.Info("--quit-after reached");
            Quit();
        }
    }

    public void SaveScreenshot(string file)
    {
        var img = GetViewport().GetTexture().GetImage();
        var err = img.SavePng(file);
        L.Info($"screenshot {file}: {err}");
    }

    string? _windowApplied;
    /// <summary>the window mode of the settings (AppSettings.Window); only a change is applied (no flicker on every
    /// options "Back"); the windowed size is remembered</summary>
    public void ApplyWindowMode(string mode)
    {
        if (mode == _windowApplied) return;
        var s = GameApp.Settings;
        if (_windowApplied == "windowed")
        {
            var sz = DisplayServer.WindowGetSize();
            s.WindowWidth = sz.X; s.WindowHeight = sz.Y;
        }
        _windowApplied = mode;
        switch (mode)
        {
            case "fullscreen": DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen); break;
            case "exclusive": DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen); break;
            case "windowed":
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                if (s.WindowWidth >= 640 && s.WindowHeight >= 400)
                {
                    var scr = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
                    var size = new Vector2I(Math.Min(s.WindowWidth, scr.Size.X), Math.Min(s.WindowHeight, scr.Size.Y));
                    DisplayServer.WindowSetSize(size);
                    DisplayServer.WindowSetPosition(scr.Position + (scr.Size - size) / 2);
                }
                break;
            default: DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized); break;
        }
        L.Info($"window: {mode} ({DisplayServer.WindowGetSize().X} x {DisplayServer.WindowGetSize().Y})");
    }
    string _lastNotFull = "maximized";
    /// <summary>the game drawn without the graphics card (a software renderer: Mesa llvmpipe / softpipe, Microsoft Basic
    /// Render Driver, SwiftShader) is extremely slow: the banner says so (ui/diagnostics.js watchRenderer); not in test
    /// runs (--quit-after, --screenshot, --shots), which render in software on purpose</summary>
    void CheckRenderer()
    {
        var name = $"{RenderingServer.GetVideoAdapterName()} ({RenderingServer.GetVideoAdapterVendor()})";
        L.Info($"renderer: {name}, {RenderingServer.GetVideoAdapterApiVersion()}");
        var args = GameApp.UserArgs();
        if (args.ContainsKey("quit-after") || args.ContainsKey("screenshot") || args.ContainsKey("shots")) return;
        if (System.Text.RegularExpressions.Regex.IsMatch(name, "llvmpipe|softpipe|swiftshader|software|basic render", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var msg = $"The game is drawn without the graphics card ({name}): it will be extremely slow. Update the graphics driver, or start the game on the graphics card (Windows: Settings > Display > Graphics).";
            L.Warn(msg);
            _banner.Report(msg);
        }
    }

    /// <summary>F11 / Alt+Enter: fullscreen on / off</summary>
    void ToggleFullscreen()
    {
        var s = GameApp.Settings;
        var cur = s.WindowMode;
        if (cur is "fullscreen" or "exclusive") s.Window = _lastNotFull;
        else { _lastNotFull = cur; s.Window = "fullscreen"; }
        s.Fullscreen = s.Window == "fullscreen";
        ApplyWindowMode(s.WindowMode);
        s.Save();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            if (k.Keycode == Key.F11 || (k.Keycode == Key.Enter && k.AltPressed)) { ToggleFullscreen(); GetViewport().SetInputAsHandled(); return; }
            if (k.Keycode == Key.F10) { _logWindow.Visible = !_logWindow.Visible; GetViewport().SetInputAsHandled(); }
            else if (k.Keycode == Key.F9) { _perf.Visible = GameApp.Settings.Perf = !_perf.Visible; GetViewport().SetInputAsHandled(); }
            else if (k.Keycode == Key.F12) { SaveScreenshot(System.IO.Path.Combine(AppPaths.UserDir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png")); GetViewport().SetInputAsHandled(); }
        }
    }

    /// <summary>the player wants to quit (the title screen's Quit, the game's Exit, closing the window): when the map
    /// editor's map has unsaved changes the editor's question first (Save - where the map has a place of its own in My
    /// maps -, back to the editor, quit anyway, cancel); else Quit</summary>
    public void RequestQuit()
    {
        if (!PwRemake.Editor.EditorSession.Unsaved || _quitAsked) { Quit(); return; }
        _quitAsked = true;
        var layer = new CanvasLayer { Name = "QuitQuestion", Layer = 120 };
        AddChild(layer);
        void Close() { _quitAsked = false; layer.QueueFree(); }
        var d = PwRemake.Editor.EditorQuit.Dialog(() => { Close(); Quit(); }, () => { Close(); Show(new PwRemake.Editor.MapEditor()); }, Close);
        layer.AddChild(d);
    }
    bool _quitAsked;

    public void Quit()
    {
        if (_windowApplied == "windowed" && DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Windowed)
        { var sz = DisplayServer.WindowGetSize(); GameApp.Settings.WindowWidth = sz.X; GameApp.Settings.WindowHeight = sz.Y; }
        GameApp.Settings.Save();
        L.Info("exit");
        Log.Flush();
        GetTree().Quit();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) { GameApp.Settings.Save(); Log.Flush(); RequestQuit(); }
        if (what == NotificationCrash) { Log.Write("Crash", LogLevel.Fatal, "the engine crashed", null); Log.Flush(); }
    }
}
