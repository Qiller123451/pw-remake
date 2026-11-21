// The loading screen of a mission (or a skirmish: ForSkirmish - the map's name and description, the setting's
// multiplayer picture loadbg_mp_<setting>), as the original shows it: the mission's own loading picture (the menu art's
// loadbg_singleNN.jpg, 1024 x 768 with its painted map frame - LoadingInfo picks it), whole and centred (the sides of
// a wide window: the same picture blurred and darkened), the mission's title and description over its top, a hint of
// the game's help book over its bottom, the original loading display (LoadingBar: loading_display_off / _on) and
// what is being done ("Loading models 120 / 202").
//
// A mission of the original campaign shows, instead of the hint, the journal entry the original shows there (one of
// the heroes' journal, LoadingScreens.txt + the string tables: Logic/Journal.cs) in the info window on the right - the
// original's 340 x 680 frame window with the entry's title as its caption - and plays its voice (the Speech volume; the
// menu music pauses for it, MusicMgr.OnStartJournalSound). When the mission has loaded it waits, as the original, with
// its ready button ("Start Mission", loading_display_startbutton on the loading display; Enter / Space too) so the
// entry can be heard to its end; the button stops the voice. A saved game shows the entry without voice and starts at
// once (the original: "no continue button"); a co-op game neither waits nor reads aloud (the lockstep start is not
// held by one player); test runs (--quit-after, --screenshot, --shots, --autoplay) go on by themselves, as the
// original's macros did (--journal-wait: wait anyway).
//
// GameSession.Load runs as a coroutine: one MoveNext per frame, each at most 50-250 ms of work on the main thread
// (the rules, the mission and its world are made on a worker thread meanwhile), so the window stays responsive and the
// bar shows the real stages: rules, mission and map, models n / m, terrain, objects. When the session is ready it
// replaces this screen.
//
// Layout: everything is anchored inside the picture's 4:3 rectangle (AspectRatioContainer), the font sizes follow
// its height - the screen looks the same at 1600 x 900, 2560 x 1080 or any window size.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.UI.Loading;
using PwRemake.View.Audio;

namespace PwRemake.UI;

public partial class LoadingScreen : Control
{
    static readonly Logger L = Log.For("UI.Loading");
    readonly GameStart _start;
    readonly MissionEntry _mission;
    Control _stage = null!;
    Label _title = null!, _desc = null!, _status = null!;
    RichTextLabel _hint = null!;
    LoadingBar _bar = null!;
    Button? _back;
    PwRemake.App.GameSession? _session;
    IEnumerator<(double, string)>? _steps;
    string _what = "";
    double _clock, _whatSince;

    string? _setting;
    readonly SaveGame? _resume;

    // the journal entry (null: none - the hint shows), its window and voice, the ready button
    JournalShow? _journal;
    RichTextLabel? _journalText;
    bool _wait, _waiting;
    Control? _ready;
    Button? _startBtn;
    ISoundHandle? _voice;
    bool _voiceMusic;
    float _journalFit = 1;               // the journal text's size factor: smaller while a long entry does not fit
    int _fitTries;

    /// <summary>a mission's loading screen; resume: a saved game of it, replayed while it loads (docs/SAVES.md)</summary>
    public LoadingScreen(GameStart start, MissionEntry mission, SaveGame? resume = null) { _start = start; _mission = mission; _resume = resume; }

    /// <summary>the loading screen of a skirmish: the map's name, its description, the picture of its setting</summary>
    public static LoadingScreen ForSkirmish(GameStart start, SaveGame? resume = null)
    {
        var s = start.Skirmish!;
        string title = s.MapName ?? "", desc = "", setting = "Jungle", map = s.Map;
        try
        {
            var path = GameApp.Config == null ? null : SkirmishMaps.Resolve(GameApp.Config, s.Map, s.MapHash);
            if (path != null)
            {
                var h = SkirmishMaps.Header(path);
                (title, desc, _) = SkirmishMaps.Describe(h, SkirmishMaps.MapTexts(GameApp.Config!));
                setting = h.SettingName;
            }
            else if (s.Map == "") { title = "Random jungle"; desc = "A new jungle for every seed."; }          // (its size: MapGen.SizeFor the players)
        }
        catch (Exception ex) { L.Warn($"skirmish map {s.Map}: {ex.Message}"); }
        var names = Skirmish.ActiveSlots(s).Select(x => x.Slot.Tribe == "Random" ? "Random" : TribeInfo.Name(x.Slot.Tribe)).ToList();
        // (more than eight players: each tribe once with its count)
        var vs = names.Count <= 8 ? string.Join(" · ", names) : $"{names.Count} players: " + string.Join(" · ", names.GroupBy(n => n).Select(g => $"{g.Key} × {g.Count()}"));
        var entry = new MissionEntry(-1, "", "", title, $"{Skirmish.ModeName(s.Mode)} · {vs}" + (desc != "" ? "\n" + desc : ""), "", false, 0, map);
        return new LoadingScreen(start, entry, resume) { _setting = setting };
    }
    /// <summary>the loading screen of a mission of the remake's own campaigns: its title, place and date, briefing, the
    /// picture of its setting</summary>
    public static LoadingScreen ForSequel(GameStart start, PwRemake.Sim.Sequel.SequelEntry m, SaveGame? resume = null)
    {
        var def = PwRemake.Sim.Sequel.SequelCatalog.Definition(m, start.Tribe, start.Difficulty);
        var setting = def["map"]?["setting"].Str() is { Length: > 0 } st ? st : "Savanna";
        var head = m.Kind == "scenario" ? $"Scenario {m.Id} · playing as the {TribeInfo.Name(start.Tribe)}" : $"Containment · mission {m.Id}";
        var entry = new MissionEntry(-1, "", "", m.Title, $"{head}\n{m.Place}{(m.Date != "" ? " — " + m.Date : "")}" + (m.Briefing.Count > 0 ? "\n" + m.Briefing[0] : ""), "", false, 0, "");
        return new LoadingScreen(start, entry, resume) { _setting = setting };
    }
    bool IsSkirmish => _start.Mode == GameMode.Skirmish;

    /// <summary>how this mission is loaded (what the original's journal and ready button go by)</summary>
    LoadKind Kind => IsSkirmish || _start.Mode != GameMode.Campaign || PwRemake.Sim.Editor.Triggers.TestMission.IsTestFolder(_start.MissionFolder) ? LoadKind.Other   // (the map editor's Test map: no ready button)
        : _resume != null ? LoadKind.SavedGame
        : _start.Seats != null ? LoadKind.Network
        : LoadKind.Campaign;

    /// <summary>a test run goes on without the player (the original: OnContinue when a macro runs)</summary>
    static bool AutoContinue()
    {
        var a = GameApp.UserArgs();
        if (a.ContainsKey("journal-wait")) return false;
        // (a measuring or picture-taking run that quits by itself: --perf, --outbreak-shots, --save-at, --end-at sat at the
        // ready button for good - a "load" of the Outbreak mission that never ended)
        return new[] { "quit-after", "screenshot", "shots", "autoplay", "quit-when-done", "editor-walkthrough", "perf", "outbreak-shots", "save-at", "end-at" }.Any(a.ContainsKey);
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Theme = PwTheme.Theme;
        MouseFilter = MouseFilterEnum.Stop;
        var sources = Sources();
        var mj = ReadMissionJson();
        var setting = _setting ?? mj?["map_info"]?["setting"].Str();
        var mapName = mj?["map_info"]?["name"].Str() ?? mj?["name"].Str();
        var texts = GameTexts.For(GameApp.Config);
        var kind = Kind;
        JournalEntry? entry = null;
        try
        {
            entry = kind == LoadKind.Other ? null : Journal.ForMission(GameApp.Config, _mission.Key, _mission.Map);
            _journal = Journal.Plan(entry, kind, texts.Get);
            if (kind != LoadKind.Other)
                L.Info($"mission {_mission.Id} ({_mission.Key}, {kind}): journal entry {(entry == null ? "none" : entry.Level + (_journal == null ? " (no text)" : $" \"{_journal.Caption}\", voice {_journal.Voice ?? "none"}"))}");
        }
        catch (Exception ex) { L.Warn($"journal entry of mission {_mission.Id}: {ex.Message}"); }
        _wait = Journal.WaitForStart(kind) && !AutoContinue();

        // ---- the picture, and the wide window's sides
        AddChild(new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore });
        ((Control)GetChild(0)).SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // (the level's own picture of LoadingScreens.txt first, as the original: single_seas_02 -> loadbg_seas2)
        var (pic, picPath) = LoadFirst(sources, LoadingInfo.ArtOf(entry?.Image).Concat(LoadingInfo.PictureCandidates(_mission.Id, _mission.Map, setting)));
        L.Info($"{(IsSkirmish ? "skirmish" : "mission " + _mission.Id)}: loading picture {(picPath == null ? "(none: the data has no loading art)" : picPath)}");
        if (pic != null)
        {
            var side = new TextureRect
            {
                Texture = Blurred(pic), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, Modulate = new Color(0.32f, 0.30f, 0.28f),
                MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.Linear,
            };
            AddChild(side);
            side.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }
        var aspect = new AspectRatioContainer
        {
            Ratio = pic != null ? (float)pic.GetWidth() / pic.GetHeight() : 4f / 3f,
            StretchMode = AspectRatioContainer.StretchModeEnum.Fit, MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(aspect);
        aspect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _stage = new Control { MouseFilter = MouseFilterEnum.Ignore, ClipContents = true };
        aspect.AddChild(_stage);
        if (pic != null)
            Place(new TextureRect { Texture = pic, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.Linear }, 0, 0, 1, 1);
        else
            Place(new ColorRect { Color = new Color(0.07f, 0.05f, 0.03f) }, 0, 0, 1, 1);

        // ---- shades behind the texts (inside the painted frame)
        Place(Shade(top: true), 0.035f, 0.035f, 0.965f, 0.30f);
        Place(Shade(top: false), 0.035f, 0.64f, 0.965f, 0.955f);

        // ---- title, description, hint
        _title = MakeLabel(PwTheme.Hi, HorizontalAlignment.Center, outline: true);
        _title.Text = LoadingInfo.Title(_mission.Title, mapName, _mission.Id);
        _desc = MakeLabel(PwTheme.Text, HorizontalAlignment.Center);
        _desc.Text = LoadingInfo.Clean(_mission.Description);
        if (_journal == null)
        {
            Place(_title, 0.06f, 0.055f, 0.94f, 0.12f);
            Place(_desc, 0.11f, 0.125f, 0.89f, 0.27f);
        }
        else
        {
            // (left of the journal window)
            Place(_title, 0.05f, 0.055f, 0.68f, 0.12f);
            Place(_desc, 0.07f, 0.125f, 0.66f, 0.30f);
        }
        // the hint: its title in the headings' colour, the text under it
        _hint = new RichTextLabel { BbcodeEnabled = true, FitContent = false, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, VerticalAlignment = VerticalAlignment.Bottom };
        _hint.AddThemeFontOverride("normal_font", PwTheme.BodyFont);
        _hint.AddThemeColorOverride("default_color", PwTheme.Text);
        _hint.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
        var hint = _journal != null ? null : LoadingInfo.Pick(LoadingInfo.Hints(ReadHelp(sources)), Random.Shared.Next());
        if (hint != null)
            _hint.Text = $"[center][color=#{PwTheme.Hi.ToHtml(false)}]Hint: {Esc(hint.Title)}[/color]\n{Esc(hint.Text)}[/center]";
        Place(_hint, 0.12f, 0.655f, 0.88f, 0.79f);

        // ---- the loading display and the line under it
        _bar = new LoadingBar();
        var (off, _) = LoadFirst(sources, new[] { LoadingInfo.MenuArt + "loading_display_off.png" });
        var (on, _) = LoadFirst(sources, new[] { LoadingInfo.MenuArt + "loading_display_on.png" });
        if (off != null && on != null) { _bar.Off = off; _bar.On = on; }
        else L.Warn("no loading display art (assets/ui/menu/menue__decoration__loading_display_off/_on.png): no bar, only the progress line - re-run the toolkit's \"Prepare the game data\"");
        // (609 x 66 of 1024 x 768 in the browser remake's CSS: centred, its bottom 8 % above the screen's)
        Place(_bar, 0.5f - 609f / 1024 / 2, 0.80f, 0.5f + 609f / 1024 / 2, 0.80f + 66f / 768);
        _status = MakeLabel(PwTheme.Text, HorizontalAlignment.Center);
        _status.Text = "Loading";
        Place(_status, 0.15f, 0.89f, 0.85f, 0.94f);

        // ---- the journal entry: the original's info window (CLoadInfoWindow, 340 x 680 at a 1024 px high screen),
        // 5 % from the right edge - here from the top of the picture's frame down to the loading display, which it
        // would cover centred in a 4:3 picture
        if (_journal != null)
        {
            var holder = new Control();
            Place(holder, 0.95f - 340f / 1024 * 0.75f, 0.06f, 0.95f, 0.785f);
            var win = new PwWindow(_journal.Caption, minWidth: 100, maxWidth: 100000);
            win.AddThemeStyleboxOverride("panel", new StyleBoxEmpty { ContentMarginLeft = 30, ContentMarginRight = 28, ContentMarginTop = 38, ContentMarginBottom = 26 });
            holder.AddChild(win);
            win.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _journalText = new RichTextLabel
            {
                BbcodeEnabled = false, Text = _journal.Text, FitContent = false, ScrollActive = true,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsVertical = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Pass,
            };
            _journalText.AddThemeFontOverride("normal_font", PwTheme.BodyFont);
            _journalText.AddThemeColorOverride("default_color", PwTheme.Text);
            _journalText.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
            _journalText.AddThemeConstantOverride("line_separation", 1);
            win.Body.AddChild(_journalText);
            if (_journal.Voice != null) StartVoice(_journal.Voice);
        }

        // ---- the ready button: loading_display_startbutton (146 x 33) on the loading display at 231 px, the button
        // (120 x 30, "Start Mission") inside it (LoadScreen.srf ProgressBar/ButtonBackground/ReadyButton)
        if (_wait)
        {
            var (art, _) = LoadFirst(sources, new[] { LoadingInfo.MenuArt + "loading_display_startbutton.png" });
            var holder = art != null
                ? new TextureRect { Texture = art, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.Linear }
                : new Control();
            float bl = 0.5f - 609f / 1024 / 2;
            Place(holder, bl + 231f / 1024, 0.80f, bl + 377f / 1024, 0.80f + 33f / 768);
            _startBtn = new Button { Text = texts.Get("_CampaignMapWnd_StartBtn", "Start Mission"), FocusMode = FocusModeEnum.None };
            holder.AddChild(_startBtn);
            _startBtn.AnchorLeft = 13f / 146; _startBtn.AnchorRight = 133f / 146; _startBtn.AnchorTop = 1.5f / 33; _startBtn.AnchorBottom = 31.5f / 33;
            _startBtn.OffsetLeft = _startBtn.OffsetTop = _startBtn.OffsetRight = _startBtn.OffsetBottom = 0;
            _startBtn.Pressed += Continue;
            holder.Visible = false;
            _ready = holder;
        }
        _stage.Resized += () => { _journalFit = 1; _fitTries = 0; Scale(); };
        Scale();

        try
        {
            _session = new PwRemake.App.GameSession(_start, _resume);
            _steps = _session.Load().GetEnumerator();
        }
        catch (Exception ex) { Fail(ex); }
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_voice != null && _voice.Ended) StopVoice();       // (the entry was read to its end: the music comes back)
        FitJournal();
        if (_steps == null) return;
        try
        {
            if (_steps.MoveNext())
            {
                var (k, text) = _steps.Current;
                _bar.Value = k;
                if (text != _what) { _what = text; _whatSince = _clock; }
                // a stage that takes a while (the world on its thread): the dots move, the screen is alive
                int dots = _clock - _whatSince > 0.6 ? (int)(_clock * 2.5) % 4 : 0;
                _status.Text = text + new string('.', dots);
                return;
            }
            _steps = null;
            L.Info($"mission {_mission.Id} loaded");
            if (_wait) { ShowReady(); return; }
            Go();
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>loaded: the ready button, the player starts the mission (LoadScreen.usl OnTimer)</summary>
    void ShowReady()
    {
        _waiting = true;
        _bar.Value = 1;
        _status.Text = "";
        if (_ready != null) _ready.Visible = true;
        _stage.MouseFilter = MouseFilterEnum.Pass;
        L.Info($"mission {_mission.Id}: waiting for the player (the ready button)");
    }

    /// <summary>the ready button (Enter / Space): the voice stops, the mission starts (LoadScreen.usl OnContinue)</summary>
    void Continue()
    {
        if (!_waiting) return;
        _waiting = false;
        Go();
    }

    void Go()
    {
        StopVoice();
        Boot.Instance.Show(_session!);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_waiting && e is InputEventKey { Pressed: true, Echo: false } k && !k.AltPressed && k.Keycode is Key.Enter or Key.KpEnter or Key.Space)
        {
            GetViewport().SetInputAsHandled();
            Continue();
        }
    }

    public override void _ExitTree() => StopVoice();

    // ------------------------------------------------------------------------------------------------ the voice

    /// <summary>the entry read aloud once (LoadScreen.usl ActivateJournalSound: a global sound; here the Speech bus),
    /// the menu music silent meanwhile</summary>
    void StartVoice(string ev)
    {
        try
        {
            var cfg = GameApp.Config;
            var path = cfg == null ? null : Journal.VoicePath(cfg, ev);
            if (path == null) { L.Info($"journal voice {ev}: no file in the installation (silent)"); return; }
            var ga = GameAudio.Current;
            if (ga == null || !ga.Core.Ready()) return;
            var buf = AudioStreams.FromBytes(path, File.ReadAllBytes(path));
            if (buf == null) { L.Warn($"journal voice {path}: not readable"); return; }
            _voice = ga.Out.Start(buf, AudioChannel.Voice, 1, 1, null, 0, false);
            if (_voice == null) return;
            ga.Core.Mus.Journal(true);
            _voiceMusic = true;
            L.Info($"journal voice {path} ({buf.Duration:0.0} s)");
        }
        catch (Exception ex) { L.Warn($"journal voice {ev}: {ex.Message}"); }
    }

    /// <summary>the voice stops (the screen closes, the ready button, its end) - LoadScreen.usl DeactivateJournalSound</summary>
    void StopVoice()
    {
        var v = _voice;
        _voice = null;
        try { if (v != null && !v.Ended) v.Stop(); } catch (Exception) { /* already over */ }
        if (!_voiceMusic) return;
        _voiceMusic = false;
        try { GameAudio.Current?.Core.Mus.Journal(false); } catch (Exception ex) { L.Warn($"music after the journal: {ex.Message}"); }
    }

    void Fail(Exception ex)
    {
        _steps = null;
        L.Error($"loading mission {_mission.Id} failed", ex);
        _status.Text = $"Loading failed: {ex.Message}\n(F10: log)";
        _status.LabelSettings.FontColor = PwTheme.Lost;
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hint.Text = "";
        if (_back != null) return;
        _back = IsSkirmish ? PwTheme.Btn("Back to the skirmish menu", () => Boot.Instance.Show(new SkirmishMenu()))
            : PwTheme.Btn("Back to the campaign menu", () => Boot.Instance.Show(new CampaignMenu()));
        var c = new CenterContainer();
        c.AddChild(_back);
        Place(c, 0.2f, 0.70f, 0.8f, 0.78f);
        _stage.MouseFilter = MouseFilterEnum.Pass;
    }

    // ------------------------------------------------------------------------------------------------ layout

    void Place(Control c, float l, float t, float r, float b)
    {
        _stage.AddChild(c);
        c.AnchorLeft = l; c.AnchorTop = t; c.AnchorRight = r; c.AnchorBottom = b;
        c.OffsetLeft = c.OffsetTop = c.OffsetRight = c.OffsetBottom = 0;
        if (c is not Button && c is not CenterContainer) c.MouseFilter = MouseFilterEnum.Ignore;
    }

    static Label MakeLabel(Color color, HorizontalAlignment align, bool outline = false) => new()
    {
        HorizontalAlignment = align, AutowrapMode = TextServer.AutowrapMode.WordSmart,
        LabelSettings = new LabelSettings
        {
            Font = PwTheme.BodyFont, FontColor = color, ShadowColor = new Color(0, 0, 0, 0.9f), ShadowOffset = new Vector2(1, 1),
            OutlineColor = new Color(0.08f, 0.04f, 0, 0.85f), OutlineSize = outline ? 4 : 0,
        },
    };

    /// <summary>the font sizes and outlines follow the picture's height (768 px: the original's size)</summary>
    new void Scale()
    {
        float h = Math.Max(240, _stage.Size.Y), k = h / 768f;
        _title.LabelSettings.FontSize = (int)Math.Round(34 * k);
        _title.LabelSettings.OutlineSize = (int)Math.Max(2, 5 * k);
        _title.LabelSettings.ShadowOffset = new Vector2(2, 2) * k;
        _desc.LabelSettings.FontSize = (int)Math.Round(17 * k);
        _status.LabelSettings.FontSize = (int)Math.Round(16 * k);
        foreach (var l in new[] { _desc, _status }) l.LabelSettings.ShadowOffset = new Vector2(1, 1) * Math.Max(1, k);
        _hint.AddThemeFontSizeOverride("normal_font_size", (int)Math.Round(15 * k));
        // the journal's text: 14 px at a 1024 px high screen (smaller for a long entry), at least 10 px
        _journalText?.AddThemeFontSizeOverride("normal_font_size", (int)Math.Max(10, Math.Round(14 * h / 1024f * _journalFit)));
        _startBtn?.AddThemeFontSizeOverride("font_size", (int)Math.Max(10, Math.Round(12 * k)));
        _hint.AddThemeConstantOverride("shadow_offset_x", (int)Math.Max(1, k));
        _hint.AddThemeConstantOverride("shadow_offset_y", (int)Math.Max(1, k));
    }

    /// <summary>a long entry (the longest have 1200 letters) that does not fit its window: the text a little smaller,
    /// a step per frame (down to 70 %), then the window scrolls</summary>
    void FitJournal()
    {
        var j = _journalText;
        if (j == null || _fitTries >= 10 || !j.IsVisibleInTree() || j.Size.Y < 20) return;
        int h = j.GetContentHeight();
        if (h <= 0) return;                                   // (not laid out yet)
        if (h <= j.Size.Y) { _fitTries = 10; return; }
        _fitTries++;
        _journalFit = Math.Max(0.7f, _journalFit - 0.05f);
        Scale();
    }

    static string Esc(string s) => s.Replace("[", "[lb]");

    /// <summary>a dark gradient behind the texts: strongest at the edge of the picture, none towards its middle</summary>
    static TextureRect Shade(bool top)
    {
        var g = new Gradient();
        g.SetColor(0, new Color(0, 0, 0, top ? 0.72f : 0f));
        g.SetColor(1, new Color(0, 0, 0, top ? 0f : 0.78f));
        g.AddPoint(top ? 0.55f : 0.45f, new Color(0, 0, 0, top ? 0.45f : 0.5f));
        var tex = new GradientTexture2D { Gradient = g, Width = 4, Height = 64, FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 1) };
        return new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
    }

    // ------------------------------------------------------------------------------------------------ data

    /// <summary>where the art is looked up: this configuration first, then the others (the original's menu art is the
    /// same in every one; a mod's data folder may have been built without it)</summary>
    static List<AssetSource> Sources()
    {
        var res = new List<AssetSource>();
        if (GameApp.Assets != null) res.Add(GameApp.Assets);
        foreach (var c in GameApp.Configurations)
            if (c != GameApp.Config) res.Add(new AssetSource(c));
        return res;
    }

    static (Texture2D?, string?) LoadFirst(List<AssetSource> sources, IEnumerable<string> candidates)
    {
        foreach (var rel in candidates)
            foreach (var s in sources)
            {
                var p = s.Resolve(rel);
                if (p == null) continue;
                var img = Image.LoadFromFile(p);
                if (img == null || img.IsEmpty()) { L.WarnOnce(p, $"cannot read image {p}"); continue; }
                return (ImageTexture.CreateFromImage(img), p);
            }
        return (null, null);
    }

    /// <summary>a small copy of the picture, scaled up smoothly: soft, for the sides of a wide window</summary>
    static Texture2D Blurred(Texture2D t)
    {
        var img = t.GetImage();
        img.Resize(Math.Max(1, img.GetWidth() / 32), Math.Max(1, img.GetHeight() / 32), Image.Interpolation.Lanczos);
        img.Resize(img.GetWidth() * 4, img.GetHeight() * 4, Image.Interpolation.Cubic);
        return ImageTexture.CreateFromImage(img);
    }

    System.Text.Json.Nodes.JsonNode? ReadMissionJson()
    {
        try
        {
            var dir = GameApp.Config?.MissionsDir;
            if (IsSkirmish) return null;
            return dir == null ? null : PwRemake.Sim.Maps.PwMapReader.MissionOf(Path.Combine(dir, _mission.Folder));
        }
        catch (Exception ex) { L.Warn($"the mission data of {_mission.Folder}: {ex.Message}"); return null; }
    }

    static System.Text.Json.Nodes.JsonNode? ReadHelp(List<AssetSource> sources)
    {
        foreach (var s in sources)
        {
            var p = s.Resolve("help.json");
            if (p == null) continue;
            try { return Json.TryParseFile(p); }
            catch (Exception ex) { L.Warn($"help.json: {ex.Message}"); }
        }
        return null;
    }
}
