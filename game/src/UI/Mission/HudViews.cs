// The mission UI's parts of the HUD, drawn from MissionUiState every frame (Sync): the news ticker (right column,
// NewsTicker.usl: 235 px at x desktop - 235, y 37), the dialogue box (bottom left right of the army pyramid,
// DialogScene.usl:143: x 212, y desktop - (198 + its height)), the info bar and the timers (upper right under the
// top bar's Quest / Chat buttons, hideable), the mission title card and the quest button (IngameScreen.usl:309-326).
// The look follows the browser remake's CSS (index.src.html "campaign: mission UI") with the original HUD art.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.Mission;

static class Col
{
    public static readonly Color Gold = new(0xf3 / 255f, 0xd5 / 255f, 0x8c / 255f);        // #f3d58c names, titles
    public static readonly Color Speech = new(1, 250 / 255f, 180 / 255f);                   // rgb(255,250,180) spoken text
    public static readonly Color Ticker = new(0xf0 / 255f, 0xdc / 255f, 0xae / 255f);      // #f0dcae
    public static readonly Color New = new(1, 204 / 255f, 0);                               // QW:66-70 new
    public static readonly Color Open = new(152 / 255f, 133 / 255f, 99 / 255f);            // open
    public static readonly Color Done = new(11 / 255f, 220 / 255f, 11 / 255f);             // accomplished
    public static readonly Color Failed = new(1, 40 / 255f, 0);                            // failed
    public static readonly Color Good = new(0x8e / 255f, 0xe8 / 255f, 0x6a / 255f);
    public static readonly Color Bad = new(1, 0x80 / 255f, 0x60 / 255f);
    public static readonly Color Mentor = new(0xb8 / 255f, 0xdc / 255f, 0xf0 / 255f);
    public static readonly Color Bar = new(1, 0xf2 / 255f, 0xc8 / 255f);                   // #fff2c8 info bar
    public static string Hex(Color c) => "#" + c.ToHtml(false);
}

/// <summary>The news ticker: lines with the original's strip background, an icon, a bold first line.</summary>
public partial class NewsTickerView : VBoxContainer
{
    readonly MissionUiState S;
    readonly Dictionary<NewsLine, PanelContainer> _lines = new();
    public const float Width = 235;

    public NewsTickerView(MissionUiState s)
    {
        S = s;
        Name = "NewsTicker";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.TopRight);
        Position = new Vector2(-Width, 37);
        CustomMinimumSize = new Vector2(Width, 0);
        AddThemeConstantOverride("separation", 3);
    }

    public void Sync()
    {
        // (anchored at the right: keep the column's left edge at desktop - 235 x scale)
        OffsetLeft = -Width * Scale.X; OffsetRight = 0;
        foreach (var n in S.News)
            if (!_lines.ContainsKey(n)) { var p = Make(n); _lines[n] = p; AddChild(p); }
        var gone = new List<NewsLine>();
        foreach (var (n, p) in _lines)
        {
            if (!S.News.Contains(n)) { gone.Add(n); p.QueueFree(); continue; }
            p.Modulate = new Color(1, 1, 1, (float)Math.Clamp(n.T, 0, 1));       // .nt.fade: 0.9 s
        }
        foreach (var n in gone) _lines.Remove(n);
    }

    PanelContainer Make(NewsLine n)
    {
        var p = new PanelContainer { CustomMinimumSize = new Vector2(Width, 0), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
        p.AddThemeStyleboxOverride("panel", HudArt.Ticker());
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 6);
        p.AddChild(h);
        var icon = new TextureRect { Texture = HudArt.Atlas(n.Icon), CustomMinimumSize = new Vector2(16, 16), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = SizeFlags.ShrinkBegin, MouseFilter = MouseFilterEnum.Ignore };
        h.AddChild(icon);
        var t = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        t.AddThemeFontOverride("normal_font", HudArt.Sans);
        t.AddThemeFontOverride("bold_font", HudArt.SansBold);
        t.AddThemeFontSizeOverride("normal_font_size", 12);
        t.AddThemeFontSizeOverride("bold_font_size", 12);
        t.AddThemeColorOverride("default_color", Col.Ticker);
        t.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
        t.AddThemeConstantOverride("shadow_offset_x", 1);
        t.AddThemeConstantOverride("shadow_offset_y", 1);
        var tc = n.Kind switch { "new" => Col.New, "good" => Col.Good, "bad" => Col.Bad, _ => Col.Gold };
        t.Text = (n.Title.Length > 0 ? $"[b][color={Col.Hex(tc)}]{Esc(n.Title)}[/color][/b]\n" : "") + Esc(n.Text);
        h.AddChild(t);
        // NT:230-262 a click goes where the message points, a right click removes it
        p.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true } mb)
            {
                if (mb.ButtonIndex == MouseButton.Left) S.ClickNews(n);
                else if (mb.ButtonIndex == MouseButton.Right) S.DropNews(n);
                p.AcceptEvent();
            }
        };
        p.MouseEntered += () => p.AddThemeStyleboxOverride("panel", HudArt.Ticker(true));
        p.MouseExited += () => p.AddThemeStyleboxOverride("panel", HudArt.Ticker());
        return p;
    }

    public static string Esc(string s) => (s ?? "").Replace("[", "[lb]");
}

/// <summary>The dialogue box: portrait, speaker and text in the HUD's tooltip frame, the close button.</summary>
public partial class DialogBoxView : Control
{
    readonly MissionUiState S;
    readonly PanelContainer _box;
    readonly TextureRect _por;
    readonly Label _initial, _name, _text;
    readonly Panel _porFrame;
    DialogPlay? _shown;
    int _shownI = -1;

    public DialogBoxView(MissionUiState s)
    {
        S = s;
        Name = "DialogBox";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.BottomLeft);
        _box = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        _box.AddThemeStyleboxOverride("panel", HudArt.Tooltip());
        AddChild(_box);
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 9);
        _box.AddChild(h);
        _porFrame = new Panel { CustomMinimumSize = new Vector2(64, 61), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        var pf = new StyleBoxFlat { BgColor = new Color(0x1b / 255f, 0x13 / 255f, 0x0c / 255f), BorderColor = new Color(0x8a / 255f, 0x6a / 255f, 0x38 / 255f), ExpandMarginLeft = 1, ExpandMarginRight = 1, ExpandMarginTop = 1, ExpandMarginBottom = 1 };
        pf.SetBorderWidthAll(1);
        _porFrame.AddThemeStyleboxOverride("panel", pf);
        h.AddChild(_porFrame);
        _por = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, AnchorRight = 1, AnchorBottom = 1 };
        _porFrame.AddChild(_por);
        _initial = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, AnchorRight = 1, AnchorBottom = 1, LabelSettings = new LabelSettings { Font = PwTheme.BodyFont, FontSize = 34, FontColor = new Color(0xa0 / 255f, 0x8a / 255f, 0x60 / 255f) } };
        _porFrame.AddChild(_initial);
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(290, 0) };
        v.AddThemeConstantOverride("separation", 2);
        h.AddChild(v);
        _name = new Label { LabelSettings = HudArt.Text(13, Col.Gold, true) };
        _text = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(290, 0), LabelSettings = HudArt.Text(13, Col.Speech) };
        v.AddChild(_name);
        v.AddChild(_text);
        // the × closes the scene (DS:340-352 OnClose)
        var close = new TextureButton { TextureNormal = HudArt.Tex("hud_closebutton.png"), Position = new Vector2(0, 0), FocusMode = FocusModeEnum.None, TooltipText = "Close" };
        close.Pressed += () => S.CloseDialog();
        close.MouseEntered += () => close.Modulate = new Color(1.4f, 1.4f, 1.4f);
        close.MouseExited += () => close.Modulate = Colors.White;
        AddChild(close);
        _close = close;
        _box.Visible = close.Visible = false;
    }
    readonly TextureButton _close;

    public void Sync()
    {
        var sc = S.Scene;
        bool on = sc != null && !sc.Hidden && sc.Frame != null;
        _box.Visible = _close.Visible = on;
        if (!on) { _shown = null; return; }
        if (sc != _shown || sc!.I != _shownI)
        {
            _shown = sc; _shownI = sc!.I;
            var tex = HudArt.Atlas(sc.Portrait, 4);
            _por.Texture = tex;
            _initial.Text = tex == null ? (sc.Name.Length > 0 ? sc.Name.Substring(0, 1).ToUpperInvariant() : "?") : "";
            _name.Text = sc.Name;
            _text.Text = sc.Text;
            bool mentor = sc.Scene.Mentor;
            _name.LabelSettings = HudArt.Text(13, mentor ? Col.Mentor : Col.Gold, true);
            _box.SelfModulate = mentor ? new Color(0.75f, 0.9f, 1.15f) : Colors.White;
            _box.ResetSize();
        }
        // x 212, 198 above the bottom (DialogScene.usl:143) - the box grows upwards
        var size = _box.GetCombinedMinimumSize();
        _box.Size = size;
        _box.Position = new Vector2(212, -198 - size.Y);
        _close.Position = _box.Position + new Vector2(size.X - 22, 6);
    }
}

/// <summary>Upper right, under the top bar's Quest / Chat buttons: the mission's objectives - the info bar (the
/// counters: "Nests built correctly: 0 / 8") and the level timers (TW: a pill hud_timers timer_default over
/// timer_background with the time, the label under it; red in the last 10 s, dimmed while paused) - right-aligned to
/// the Chat button's right end (Anchor, set by the HUD). A small toggle left of the bar folds it away to the toggle
/// alone (Hidden: MissionPresenter keeps it in the settings, Shift+J toggles it too).
/// The original (IngameScreen.usl) drew the info bar in its count-down window at the resource bar (335, 2) and the
/// level timers in rows from (268, 30) under the Menu button, the sequences' counter (CTimerWnd) in the screen's top
/// right corner; Kacper asked for the objectives in the upper right under Quest / Chat, out of the way of the view.</summary>
public partial class TopStackView : VBoxContainer
{
    readonly MissionUiState S;
    readonly PanelContainer _bar;
    readonly Label _barText;
    readonly HBoxContainer _row, _timers;
    readonly Button _toggle;
    readonly Dictionary<string, (VBoxContainer Box, Label Clock, Label Label)> _timerEls = new();
    /// <summary>the top right corner of the panel on screen (the right end of the top bar's last button, under it);
    /// null: the screen's top right under the top row</summary>
    public new Vector2? Anchor;
    /// <summary>folded away: only the toggle shows</summary>
    public new bool Hidden;
    /// <summary>the toggle was clicked</summary>
    public Action? OnToggle;

    public TopStackView(MissionUiState s)
    {
        S = s;
        Name = "TopStack";
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeConstantOverride("separation", 4);
        _row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkEnd, MouseFilter = MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 2);
        AddChild(_row);
        _toggle = new Button { Name = "ObjectivesToggle", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(20, 22), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Stop };
        foreach (var (state, a) in new[] { ("normal", 0.78f), ("hover", 0.95f), ("pressed", 1f) })
            _toggle.AddThemeStyleboxOverride(state, new StyleBoxFlat
            {
                BgColor = new Color(30 / 255f, 20 / 255f, 10 / 255f, a), BorderColor = new Color(224 / 255f, 185 / 255f, 120 / 255f, 0.7f),
                BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            });
        _toggle.AddThemeFontOverride("font", HudArt.SansBold);
        _toggle.AddThemeFontSizeOverride("font_size", 14);
        foreach (var c in new[] { "font_color", "font_hover_color", "font_pressed_color" }) _toggle.AddThemeColorOverride(c, Col.Bar);
        _toggle.Pressed += () => OnToggle?.Invoke();
        _row.AddChild(_toggle);
        _bar = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        _bar.AddThemeStyleboxOverride("panel", new StyleBoxTexture { Texture = BarTexture(), ContentMarginLeft = 34, ContentMarginRight = 34, ContentMarginTop = 4, ContentMarginBottom = 5 });
        _barText = new Label { LabelSettings = HudArt.Text(15, Col.Bar), HorizontalAlignment = HorizontalAlignment.Center };
        _bar.AddChild(_barText);
        _row.AddChild(_bar);
        _timers = new HBoxContainer { Alignment = AlignmentMode.End, SizeFlagsHorizontal = SizeFlags.ShrinkEnd, MouseFilter = MouseFilterEnum.Ignore };
        _timers.AddThemeConstantOverride("separation", 8);
        AddChild(_timers);
    }

    // the bar: a dark band fading out at both ends, gold hairlines above and below (.ibar)
    static Texture2D BarTexture()
    {
        const int w = 256, h = 24;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int x = 0; x < w; x++)
        {
            double u = x / (double)(w - 1);
            double a = Math.Min(1, Math.Min(u, 1 - u) / 0.14) * 0.86;
            double g = Math.Min(1, Math.Min(u, 1 - u) / 0.2) * 0.7;
            for (int y = 0; y < h; y++)
            {
                bool line = y == 0 || y == h - 1;
                img.SetPixel(x, y, line ? new Color(224 / 255f, 185 / 255f, 120 / 255f, (float)g) : new Color(30 / 255f, 20 / 255f, 10 / 255f, (float)a));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    public void Sync()
    {
        bool any = S.Bar.Length > 0 || S.TimerViews.Count > 0;
        _toggle.Visible = any;
        _toggle.Text = Hidden ? "+" : "\u2013";
        _toggle.TooltipText = Hidden ? "Show the objectives (Shift+J)" : "Hide the objectives (Shift+J)";
        _bar.Visible = !Hidden && S.Bar.Length > 0;
        _timers.Visible = !Hidden;
        if (_barText.Text != S.Bar) { _barText.Text = S.Bar; _bar.ResetSize(); }
        var seen = new HashSet<string>();
        foreach (var t in S.TimerViews)
        {
            seen.Add(t.Id);
            if (!_timerEls.TryGetValue(t.Id, out var e)) { e = MakeTimer(); _timerEls[t.Id] = e; _timers.AddChild(e.Box); }
            e.Clock.Text = t.Clock;
            e.Label.Text = t.Label;
            e.Label.Visible = t.Label.Length > 0;
            e.Clock.LabelSettings.FontColor = t.Low ? new Color(1, 90 / 255f, 58 / 255f) : new Color(225 / 255f, 225 / 255f, 225 / 255f);
            e.Clock.Modulate = new Color(1, 1, 1, t.Paused ? 0.5f : 1);
        }
        foreach (var id in new List<string>(_timerEls.Keys))
            if (!seen.Contains(id)) { _timerEls[id].Box.QueueFree(); _timerEls.Remove(id); }
        // right-aligned under the top bar's right end (the HUD's Anchor), else under the top row at the screen's right
        var vp = GetViewportRect().Size;
        var size = GetCombinedMinimumSize();
        Size = size;
        var at = Anchor ?? new Vector2(vp.X - 8 * Scale.X, 38 * Scale.Y);
        Position = new Vector2(Math.Max(0, at.X - size.X * Scale.X), at.Y + 4 * Scale.Y);
    }

    (VBoxContainer, Label, Label) MakeTimer()
    {
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(100, 0), MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        var pill = new Control { CustomMinimumSize = new Vector2(65, 25), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        var bg = HudArt.Atlas("timer_background");
        var fg = HudArt.Atlas("timer_default");
        if (bg != null) pill.AddChild(new TextureRect { Texture = bg, MouseFilter = MouseFilterEnum.Ignore });
        if (fg != null) pill.AddChild(new TextureRect { Texture = fg, MouseFilter = MouseFilterEnum.Ignore });
        var clock = new Label
        {
            Position = new Vector2(22, 0), Size = new Vector2(40, 25), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            LabelSettings = HudArt.Text(12, new Color(225 / 255f, 225 / 255f, 225 / 255f), true),
        };
        pill.AddChild(clock);
        box.AddChild(pill);
        var label = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(100, 0), LabelSettings = HudArt.Text(12, Col.Bar, false, 1) };
        box.AddChild(label);
        return (box, clock, label);
    }
}

/// <summary>The mission's title card: title and description in the upper third for 7 s, fading (earlier when a
/// dialogue line shows).</summary>
public partial class TitleCardView : Control
{
    readonly MissionUiState S;
    readonly Label _h, _p;
    readonly VBoxContainer _box;

    public TitleCardView(MissionUiState s)
    {
        S = s;
        Name = "TitleCard";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        var bg = new TextureRect { Texture = Radial(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(bg);
        _bg = bg;
        _box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _box.AddThemeConstantOverride("separation", 8);
        AddChild(_box);
        _h = new Label { HorizontalAlignment = HorizontalAlignment.Center, LabelSettings = new LabelSettings { Font = HudArt.SansBold, FontSize = 36, FontColor = new Color(1, 1, 200 / 255f), OutlineColor = Colors.Black, OutlineSize = 4, ShadowColor = new Color(0, 0, 0, 0.8f), ShadowSize = 8 } };
        _p = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, LabelSettings = new LabelSettings { Font = HudArt.Sans, FontSize = 15, LineSpacing = 4, FontColor = new Color(0xf0 / 255f, 0xe2 / 255f, 0xc0 / 255f), ShadowColor = new Color(0, 0, 0, 1), ShadowSize = 5, ShadowOffset = new Vector2(0, 1) } };
        _box.AddChild(_h);
        _box.AddChild(_p);
    }
    readonly TextureRect _bg;

    static Texture2D Radial() => new GradientTexture2D
    {
        Width = 256, Height = 128, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f),
        Gradient = new Gradient { Offsets = new[] { 0f, 0.45f, 0.72f, 1f }, Colors = new[] { new Color(0, 0, 0, 0.7f), new Color(0, 0, 0, 0.45f), new Color(0, 0, 0, 0f), new Color(0, 0, 0, 0f) } },
    };

    public void Sync()
    {
        Visible = S.TitleT > 0;
        if (!Visible) return;
        _h.Text = S.Data.Title;
        _p.Text = S.Data.Description;
        var vp = GetViewportRect().Size;
        float w = Math.Min(760, vp.X * 0.88f);
        _p.CustomMinimumSize = new Vector2(w - 80, 0);
        var sz = _box.GetCombinedMinimumSize();
        _box.Size = sz;
        _box.Position = new Vector2((vp.X - sz.X) / 2, vp.Y * 0.15f + 18);
        _bg.Position = new Vector2((vp.X - w) / 2 - 40, vp.Y * 0.15f - 30);
        _bg.Size = new Vector2(w + 80, sz.Y + 90);
        // .mtitle.out: the last 1.2 s fade
        Modulate = new Color(1, 1, 1, (float)Math.Min(1, S.TitleT / 1.2));
    }
}

/// <summary>The quest button (hud_menu_questbutton, 69 px); flashes after a quest change until the log is opened.</summary>
public partial class QuestButton : TextureButton
{
    readonly MissionUiState S;
    readonly Label _label;

    public QuestButton(MissionUiState s, Action toggle)
    {
        S = s;
        Name = "QuestButton";
        FocusMode = FocusModeEnum.None;
        TextureNormal = HudArt.Tex("hud_menu_questbutton.png");
        TextureHover = HudArt.Tex("hud_menu_questbutton_hover.png");
        TexturePressed = HudArt.Tex("hud_menu_questbutton_down.png");
        CustomMinimumSize = new Vector2(69, 32);
        TooltipText = "Quests (J)";
        _label = new Label
        {
            // (.mbtn > span: the upper 22 px, 12 px bold #f3dca6)
            Text = s.Text("_UI_IngaScre_Button_Quest", "Quests"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            AnchorRight = 1, OffsetBottom = 22, MouseFilter = MouseFilterEnum.Ignore, LabelSettings = HudArt.Text(12, new Color(0xf3 / 255f, 0xdc / 255f, 0xa6 / 255f), true),
        };
        AddChild(_label);
        Pressed += toggle;
    }

    public void Sync()
    {
        // .qbtn.new: brightness 1 .. 1.75 in 1.1 s
        float k = S.Unread ? (float)(0.5 - 0.5 * Math.Cos(S.Time * Math.PI * 2 / 1.1)) : 0;
        SelfModulate = new Color(1 + 0.75f * k, 1 + 0.75f * k, 1 + 0.6f * k);
    }
}
