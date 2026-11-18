// The Options window (the original's OptionsMenu.srf / .usl: a tab control - Graphics, Advanced, Sound, Gameplay,
// Controls - and Back with the version beside it), with the settings of the browser remake's ui/menu.js settings().
// It is the same window on the title screen (OptionsScreen) and in the game's menu (PauseMenu). Every change is applied
// at once (SettingsApply: sound, window, the running game) and settings.json is written when the window closes.
// Captions and tooltips are the original's (Data/locale/<lang>/Texts: _UI_OptionsM_*, GameTexts), the remake's own
// options (window mode, render resolution, frame cap, interface size, income per minute, game speed ...) are English.
//
//   Graphics   graphics card (shown), window, render resolution, frame rate limit, vertical sync, antialiasing,
//              interface size, original colours (no player colours), performance line (F9)
//   Advanced   shadows, grass, particles, weather; Restore default
//   Sound      master, music, ambient, interface, cutscene (speech), 3D (effects) volumes; Mute; Reset
//   Gameplay   rotate minimap, player colours, subtitles, mentor texts, start films, autosave + interval, income per minute,
//              game speed, the build menu opening by itself, tooltips next to the mouse
//   Controls   scroll at the screen's edges; the keys (the help's ControlsList)
//
// Not here (the original has them, the browser remake has not): screen resolution and gamma, texture / geometry
// detail, viewing distance, water, clouds, EAX, unit feedback, long tooltips, flat shades, hit point colouring, the AI
// assistance, camera speeds and rotation options, profiles; key bindings (the keys are fixed, as in the JS).
// Ported from remake/src/ui/menu.js settings() (pw-exporter 98c7703); layout and texts: OptionsMenu.srf / .usl.
using System;
using System.Globalization;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Content;

namespace PwRemake.UI;

public partial class OptionsPanel : VBoxContainer
{
    readonly Action _back;
    readonly GameTexts _tx;
    bool _dirty;
    /// <summary>the tab control (tests: --options-tab)</summary>
    public PwTabs Tabs { get; } = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
    static int s_lastTab;

    public OptionsPanel(Action back, int? tab = null)
    {
        _back = back;
        _tx = GameTexts.For(GameApp.Config);
        Name = "OptionsPanel";
        AddThemeConstantOverride("separation", 10);
        CustomMinimumSize = new Vector2(600, 0);
        Tabs.CustomMinimumSize = new Vector2(0, 420);
        AddChild(Tabs);
        Tabs.AddTab(T("_UI_OptionsM_Button_Graphics", "Graphics"), T("_UI_OptionsM_Tooltip_Graphics", ""), Graphics);
        Tabs.AddTab(T("_UI_OptionsM_Button_Advanced", "Advanced"), T("_UI_OptionsM_Tooltip_Advanced", ""), Advanced);
        Tabs.AddTab(T("_UI_OptionsM_Button_Sound", "Sound"), T("_UI_OptionsM_Tooltip_Sound", ""), Sound);
        Tabs.AddTab(T("_UI_OptionsM_Button_Gameplay", "Gameplay"), T("_UI_OptionsM_Tooltip_Gameplay", ""), Gameplay);
        Tabs.AddTab(T("_UI_OptionsM_Button_Controls", "Controls"), T("_UI_OptionsM_Tooltip_Controls", ""), Controls);
        Tabs.Changed = i => s_lastTab = i;
        Tabs.Select(Math.Clamp(tab ?? s_lastTab, 0, 4));
        // Back, and the version on the right (OptionsMenu.srf: Back / Version)
        var row = new HBoxContainer();
        var b = PwTheme.Btn(T("_UI_OptionsM_Button_Back", "Back"), Close, 160);
        b.TooltipText = T("_UI_OptionsM_Tooltip_Back", "");
        row.AddChild(b);
        row.AddChild(new Label { Text = $"ParaWorld Remake {GameApp.Version}{(GameApp.Config != null ? " · " + GameApp.Config.Title : "")}", SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Modulate = new Color(1, 1, 1, 0.7f) });
        AddChild(row);
    }

    /// <summary>the window's title (OptionsMenu.usl SetWindowTitle)</summary>
    public static string Title => GameTexts.For(GameApp.Config).Get("_UI_OptionsM_WindowTitle", "Options");

    string T(string key, string fallback) => _tx.Get(key, fallback).Trim();
    string L(string key, string fallback) => _tx.Label(key, fallback);

    void Close()
    {
        Save();
        _back();
    }

    void Save()
    {
        if (!_dirty) return;
        _dirty = false;
        GameApp.Settings.Save();
    }

    public override void _ExitTree() => Save();       // (Esc in the game's menu closes it without Back)

    /// <summary>a setting changed: applied at once, written when the window closes</summary>
    void Changed()
    {
        _dirty = true;
        SettingsApply.All();
    }

    // ---------------------------------------------------------------- pages
    static AppSettings S => GameApp.Settings;

    Control Page()
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 7);
        return v;
    }

    Control Graphics()
    {
        var p = Page();
        p.AddChild(Row(L("_UI_OptionsM_Label_GraphicCard", "Graphics card"), new Label { Text = RenderingServer.GetVideoAdapterName(), ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill }, T("_UI_OptionsM_Label_GraphicCard_TT", "")));
        p.AddChild(Choice("Window", "Windowed, maximized (the whole screen with the task bar), fullscreen or exclusive fullscreen. F11 / Alt+Enter switch fullscreen.",
            new[] { ("Windowed", "windowed"), ("Maximized", "maximized"), (T("_UI_OptionsM_Checkbox_FullScreen", "Fullscreen"), "fullscreen"), ("Exclusive fullscreen", "exclusive") },
            () => S.WindowMode, v => { S.Window = v; S.Fullscreen = v == "fullscreen"; }));
        p.AddChild(Choice("Render resolution", "The 3D world drawn at this share of the window's resolution (less is faster).",
            new[] { ("50 %", "0.5"), ("67 %", "0.67"), ("75 %", "0.75"), ("100 %", "1"), ("125 %", "1.25") },
            () => Num(S.RenderScale), v => S.RenderScale = ParseD(v)));
        p.AddChild(Choice("Frame rate limit", "", new[] { ("Unlimited", "0"), ("30 fps", "30"), ("60 fps", "60"), ("120 fps", "120"), ("144 fps", "144"), ("165 fps", "165") },
            () => S.FpsCap.ToString(CultureInfo.InvariantCulture), v => S.FpsCap = int.Parse(v, CultureInfo.InvariantCulture)));
        p.AddChild(Row("", Check("Vertical sync", "Waits for the screen's refresh (no tearing).", () => S.VSync, v => S.VSync = v), ""));
        p.AddChild(Row("", Check("Anti-aliasing", "Smooths the edges of the 3D world (FXAA).", () => S.Antialias, v => S.Antialias = v), ""));
        p.AddChild(Choice("Interface size", "The size of the game's interface (automatic: by the window's height).",
            new[] { ("Automatic", "0"), ("100 %", "1"), ("125 %", "1.25"), ("150 %", "1.5"), ("175 %", "1.75") },
            () => Num(S.UiScale), v => S.UiScale = ParseD(v)));
        p.AddChild(Row("", Check("Original colours (no player colours)", "The models in their own colours: no player colour on units and buildings (the cloth, banners and shields of all tribes, the paint of the SEAS). Applied at once.",
            () => S.OriginalColors, v => S.OriginalColors = v), ""));
        p.AddChild(Row("", Check("Show units behind buildings", "A unit covered by a building is drawn over it as a flat silhouette in its owner's colour (the original's \"Show Flatshades\"). Applied at once.",
            () => S.ShowUnitsBehind, v => S.ShowUnitsBehind = v), ""));
        p.AddChild(Row("", Check("Setting textures on buildings", "Buildings, walls and built scenery take the original's textures of the landscape they stand in (snow in the Icewaste and Frost, sand in the Savanna, moss in the Jungle, soot in the Ashvalley; on a mixed map the biome where they stand). Off: the plain textures everywhere, no snow. Applied at once.",
            () => S.SettingTextures, v => S.SettingTextures = v), ""));
        p.AddChild(Row("", Check("Show performance (F9)", "The frame rate and the frame's times in the top left corner.", () => S.Perf, v => S.Perf = v), ""));
        return p;
    }

    Control Advanced()
    {
        var p = Page();
        string Off = T("_UI_OptionsM_Option_Off", "Off"), On = T("_UI_OptionsM_Option_On", "On"), Low = T("_UI_OptionsM_Option_Low", "Low"),
            Med = T("_UI_OptionsM_Option_Medium", "Medium"), High = T("_UI_OptionsM_Option_High", "High");
        p.AddChild(Choice(L("_UI_OptionsM_Label_Shadows", "Shadows"), T("_UI_OptionsM_Advanced_Shadows_TT", ""), new[] { (Off, "off"), (Med, "on"), (High, "high") },
            () => S.Shadows is "off" or "high" ? S.Shadows : "on", v => S.Shadows = v));
        p.AddChild(Choice(L("_UI_OptionsM_Label_Grass", "Grass"), T("_UI_OptionsM_Advanced_Grass_TT", ""), new[] { (Off, "off"), (Med, "on"), (High, "dense") },
            () => S.Grass is "off" or "dense" ? S.Grass : "on", v => S.Grass = v));
        p.AddChild(Choice(L("_UI_OptionsM_Label_Particles", "Particles"), T("_UI_OptionsM_Advanced_Particles_TT", ""), new[] { (Off, "off"), (Low, "low"), (Med, "on"), (High, "high") },
            () => S.Particles is "off" or "low" or "high" ? S.Particles : "on", v => S.Particles = v));
        p.AddChild(Choice(L("_UI_OptionsM_Label_Weather", "Weather"), T("_UI_OptionsM_Advanced_Weather_TT", ""), new[] { (Off, "off"), (On, "on") },
            () => S.Weather ? "on" : "off", v => S.Weather = v == "on"));
        p.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        var r = PwTheme.Btn(T("_UI_OptionsM_Restore_Default", "Restore default"), () => { S.ResetGraphics(); Changed(); Rebuild(1); }, 200);
        r.TooltipText = T("_UI_OptionsM_Restore_Default_TT", "");
        p.AddChild(Right(r));
        return p;
    }

    Control Sound()
    {
        var p = Page();
        p.AddChild(Slider(L("_UI_OptionsM_Label_MasterVolume", "Master Volume"), T("_UI_OptionsM_Sound_MasterVolume_TT", ""), () => S.VolMaster, v => S.VolMaster = v));
        p.AddChild(Slider(L("_UI_OptionsM_Label_MusicVolume", "Music Volume"), T("_UI_OptionsM_Sound_MusicVolume_TT", ""), () => S.VolMusic, v => S.VolMusic = v));
        p.AddChild(Slider(L("_UI_OptionsM_Label_AmbientVolume", "Ambient Volume"), T("_UI_OptionsM_Sound_AmbientVolume_TT", ""), () => S.VolAmbience, v => S.VolAmbience = v));
        p.AddChild(Slider(L("_UI_OptionsM_Label_UISoundVolume", "UI Sound Volume"), T("_UI_OptionsM_Sound_UISoundVolume_TT", ""), () => S.VolUi, v => S.VolUi = v));
        p.AddChild(Slider(L("_UI_OptionsM_Label_CutsceneVolume", "Cutscene Volume"), T("_UI_OptionsM_Sound_CutsceneVolume_TT", "") + " (the speech)", () => S.VolSpeech, v => S.VolSpeech = v));
        p.AddChild(Slider(L("_UI_OptionsM_Label_3DSoundVolume", "3D Sound Volume"), T("_UI_OptionsM_Sound_3DSoundVolume_TT", ""), () => S.VolEffects, v => S.VolEffects = v));
        p.AddChild(Slider("Unit Sounds", "What units say and do: their replies, battle cries, death cries and the sounds of their movements", () => S.VolUnits, v => S.VolUnits = v));
        p.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 10);
        var mute = PwTheme.Btn(T("_UI_OptionsM_Button_Mute", "Mute"), () => { }, 140);
        mute.ToggleMode = true; mute.ButtonPressed = !S.Sound;
        mute.TooltipText = T("_UI_OptionsM_Button_Mute_TT", "");
        mute.Toggled += on => { S.Sound = !on; Changed(); };
        row.AddChild(mute);
        var reset = PwTheme.Btn(T("_UI_OptionsM_Button_Reset", "Reset"), () => { S.ResetVolumes(); Changed(); Rebuild(2); }, 140);
        reset.TooltipText = T("_UI_OptionsM_Button_Reset_TT", "");
        row.AddChild(reset);
        p.AddChild(row);
        return p;
    }

    Control Gameplay()
    {
        var p = Page();
        p.AddChild(Check(T("_UI_OptionsM_CheckBox_RotateMinimap", "Rotate Minimap"), T("_UI_OptionsM_CheckBox_RotateMinimap_TT", ""), () => S.MinimapRotate, v => S.MinimapRotate = v));
        p.AddChild(Check(T("_UI_OptionsM_CheckBox_UsePlayerColors", "Use Player Colors"), T("_UI_OptionsM_CheckBox_UsePlayerColors_TT", ""), () => S.MinimapPlayerColors, v => S.MinimapPlayerColors = v));
        p.AddChild(Choice("Health bar colour", "The health bars over units and buildings: coloured by the health left (red .. green, as the original's alternative colouring) or in the owner's player colour.",
            new[] { ("Own units by health, others by colour", "health"), ("Player colour", "player") }, () => S.HpBarColors == "player" ? "player" : "health", v => S.HpBarColors = v));
        p.AddChild(Check(T("_UI_OptionsM_CheckBox_ShowSubtitles", "Show Subtitles"), T("_UI_OptionsM_CheckBox_ShowSubtitles_TT", ""), () => S.Subtitles, v => S.Subtitles = v));
        p.AddChild(Check("Cutscene black bars", "The cutscenes letterboxed with black bars as in the original. Off: full screen, the subtitles over the picture.", () => S.CutsceneBars, v => S.CutsceneBars = v));
        p.AddChild(Check(T("_UI_OptionsM_CheckBox_ShowMentorTexts", "Show mentor texts"), T("_UI_OptionsM_CheckBox_ShowMentorTexts_TT", ""), () => S.MentorTexts, v => S.MentorTexts = v));
        // (the remake's own: the original has no menu option for it - its Global/PlayIntro config value)
        p.AddChild(Check("Play the start films", "The publisher's and SEK's logos and the intro film when the game starts (Esc, a click or Space skips them).", () => S.IntroFilms, v => S.IntroFilms = v));
        var auto = new HBoxContainer();
        auto.AddThemeConstantOverride("separation", 12);
        auto.AddChild(Check(T("_UI_OptionsM_AutoSave", "Enable autosave"), T("_UI_OptionsM_AutoSave_TT", ""), () => S.Autosave, v => S.Autosave = v));
        var every = Options(new[] { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60 }, m => $"{m} min", () => S.AutosaveMinutes, v => S.AutosaveMinutes = v);
        every.TooltipText = T("_UI_OptionsM_AutoSave_Edit_TT", "");
        auto.AddChild(every);
        p.AddChild(auto);
        p.AddChild(Check("Show resource income per minute", "Under the resources in the top bar (the remake's own).", () => S.IncomePerMinute, v => S.IncomePerMinute = v));
        p.AddChild(Check("Open the build / produce / special menu when selecting", "", () => S.AutoMenu, v => S.AutoMenu = v));
        p.AddChild(Check("Show attack ranges", "A circle round each of your towers, turrets, cannons and bunkers: how far it shoots (and, dashed, how near it cannot). The selected one and the building being placed show theirs always. Also the range markers of the selected units and buildings: attack (red), alarm (violet), sight (cyan), healing (yellow), aura (green). Ctrl+Shift+R in the game.", () => S.ShowRanges, v => S.ShowRanges = v));
        p.AddChild(Check("Tooltips next to what the mouse is over", "Off: the original's tooltip window above the minimap.", () => S.TipNear, v => S.TipNear = v));
        p.AddChild(Choice("Game speed", "Numpad + / - / * change it in the game.", new[] { ("Slow", "0.5"), ("Normal", "1"), ("Fast", "1.5"), ("Very fast", "2") },
            () => Num(S.GameSpeed), v => S.GameSpeed = ParseD(v)));
        return p;
    }

    Control Controls()
    {
        var p = Page();
        p.AddChild(Check("Scroll at the screen's edges", "The camera moves when the mouse touches the edge of the screen.", () => S.EdgeScroll, v => S.EdgeScroll = v));
        var keys = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Help.ControlsList.Fill(keys, 190, 330);
        var sc = new ScrollContainer { CustomMinimumSize = new Vector2(0, 330), SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        sc.AddChild(keys);
        p.AddChild(sc);
        return p;
    }

    /// <summary>a page built again (after Restore default / Reset: its controls show the new values)</summary>
    void Rebuild(int tab)
    {
        var p = new OptionsPanel(_back, tab) { _dirty = true };
        var parent = GetParent();
        int at = GetIndex();
        _dirty = false;
        parent.RemoveChild(this);
        parent.AddChild(p);
        parent.MoveChild(p, at);
        QueueFree();
    }

    // ---------------------------------------------------------------- controls
    static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
    static double ParseD(string v) => double.Parse(v, CultureInfo.InvariantCulture);

    static Control Right(Control c) { var h = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; h.AddChild(c); return h; }

    /// <summary>a label (right-aligned, as the original's) and its control</summary>
    static Control Row(string label, Control c, string tooltip)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        var l = new Label { Text = label, CustomMinimumSize = new Vector2(190, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TooltipText = tooltip, MouseFilter = MouseFilterEnum.Pass };
        row.AddChild(l);
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (tooltip.Length > 0) c.TooltipText = tooltip;
        row.AddChild(c);
        return row;
    }

    Control Check(string label, string tooltip, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Text = label, ButtonPressed = get(), TooltipText = tooltip, FocusMode = FocusModeEnum.None };
        cb.Toggled += on => { set(on); Changed(); };
        return cb;
    }

    Control Slider(string label, string tooltip, Func<double> get, Action<double> set)
    {
        var sl = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.01, Value = get(), CustomMinimumSize = new Vector2(260, 21), FocusMode = FocusModeEnum.None };
        var pct = new Label { Text = $"{get() * 100:0} %", CustomMinimumSize = new Vector2(52, 0), HorizontalAlignment = HorizontalAlignment.Right };
        sl.ValueChanged += v => { set(v); pct.Text = $"{v * 100:0} %"; Changed(); };
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        h.AddChild(sl); h.AddChild(pct);
        sl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return Row(label, h, tooltip);
    }

    Control Choice(string label, string tooltip, (string Text, string Value)[] opts, Func<string> get, Action<string> set)
    {
        var ob = new OptionButton { FocusMode = FocusModeEnum.None };
        var cur = get();
        for (int i = 0; i < opts.Length; i++) { ob.AddItem(opts[i].Text, i); if (opts[i].Value == cur) ob.Selected = i; }
        ob.ItemSelected += i => { set(opts[i].Value); Changed(); };
        return Row(label, ob, tooltip);
    }

    OptionButton Options(int[] values, Func<int, string> text, Func<int> get, Action<int> set)
    {
        var ob = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(110, 0) };
        int cur = get(), best = 0;
        for (int i = 0; i < values.Length; i++) { ob.AddItem(text(values[i]), i); if (Math.Abs(values[i] - cur) < Math.Abs(values[best] - cur)) best = i; }
        ob.Selected = best;
        ob.ItemSelected += i => { set(values[i]); Changed(); };
        return ob;
    }
}

/// <summary>The Options window on the title screen (MainMenu.usl OnOptions: STATE_OPTIONSMENU).</summary>
public partial class OptionsScreen : MenuScreen
{
    readonly Func<Godot.Node> _back;
    readonly int? _tab;
    public OptionsScreen(Func<Godot.Node>? back = null, int? tab = null) : base(OptionsPanel.Title, minWidth: 680) { _back = back ?? (() => new TitleScreen()); _tab = tab; }
    protected override void Build() => Window.Body.AddChild(new OptionsPanel(() => Boot.Instance.Show(_back()), _tab));
}
