// The quest log (QuestWindow.usl, 765 x 515): a menu window in the original's frame, modal over the darkened game
// (the game is paused while it is open - MissionUiState.OpenLog). Left: the visible quests by group with the points
// got / possible (the groups of the main quests, main quests without a group, the side quests under "Subquests"),
// each with its mark (menue__controls__questchecks: open, accomplished, failed, new) in the colours of
// QuestWindow.usl:66-70 (new yellow, open grey-brown, accomplished green, failed red and crossed out). Right: the
// chosen quest's headline, state, description and points. Close / Esc / J.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Scripting;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.Mission;

public partial class QuestLogWindow : Control
{
    readonly MissionUiState S;
    readonly VBoxContainer _list;
    readonly RichTextLabel _text;
    readonly Label _total;
    readonly Dictionary<string, PanelContainer> _rows = new();

    public QuestLogWindow(MissionUiState s, Action close)
    {
        S = s;
        Name = "QuestLog";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Theme = PwTheme.Theme;
        var dark = PwTheme.Tex("windows__darken_overlay");
        Control shade = dark != null
            ? new TextureRect { Texture = dark, StretchMode = TextureRect.StretchModeEnum.Tile, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize }
            : new ColorRect { Color = new Color(0, 0, 0, 0.55f) };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var win = new PwWindow(s.Text("_UI_QuestWin_Title_Quests", "Quests"), 700, 760);
        center.AddChild(win);
        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 12);
        win.Body.AddChild(cols);
        // the list: a dark field with the menu's thin frame
        var listPanel = new PanelContainer { CustomMinimumSize = new Vector2(270, 330) };
        var lp = new StyleBoxFlat { BgColor = new Color(0x0c / 255f, 0x0a / 255f, 0x07 / 255f), BorderColor = Col.Open };
        lp.SetBorderWidthAll(1);
        listPanel.AddThemeStyleboxOverride("panel", lp);
        var ls = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        listPanel.AddChild(ls);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 0);
        ls.AddChild(_list);
        cols.AddChild(listPanel);
        var ts = new ScrollContainer { CustomMinimumSize = new Vector2(350, 330), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _text = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _text.AddThemeFontOverride("normal_font", HudArt.Sans);
        _text.AddThemeFontOverride("bold_font", HudArt.SansBold);
        _text.AddThemeFontSizeOverride("normal_font_size", 14);
        _text.AddThemeFontSizeOverride("bold_font_size", 14);
        _text.AddThemeColorOverride("default_color", new Color(0xe8 / 255f, 0xd6 / 255f, 0xb0 / 255f));
        _text.AddThemeConstantOverride("line_separation", 3);
        ts.AddChild(_text);
        cols.AddChild(ts);
        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 10);
        win.Body.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        win.Body.AddChild(foot);
        _total = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center, LabelSettings = HudArt.Text(15, PwTheme.Text, true) };
        foot.AddChild(_total);
        foot.AddChild(PwTheme.Btn(s.Text("_UI_QuestWin_Button_Close", "Close"), close, 120));
        Fill();
    }

    /// <summary>QW:66-70 colours and the mark of a quest (questchecks: 0 open box, 1 check, 2 cross, 3 new).</summary>
    static (Color Color, int Mark) Look(string st, bool fresh) => st switch
    {
        "done" => (Col.Done, 1),
        "failed" => (Col.Failed, 2),
        _ => fresh ? (Col.New, 3) : (Col.Open, 0),
    };

    public static Texture2D? Check(int i)
    {
        var t = PwTheme.Tex("controls__questchecks");
        return t == null ? null : new AtlasTexture { Atlas = t, Region = new Rect2(16 * i, 0, 16, 16) };
    }

    public void Fill()
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        _rows.Clear();
        var groups = S.QuestGroups();
        double got = 0, max = 0;
        foreach (var g in groups)
        {
            got += g.Got; max += g.Max;
            if (g.Title.Length > 0)
            {
                var gh = new HBoxContainer();
                var gm = new MarginContainer();
                gm.AddThemeConstantOverride("margin_left", 8); gm.AddThemeConstantOverride("margin_right", 8); gm.AddThemeConstantOverride("margin_top", 6); gm.AddThemeConstantOverride("margin_bottom", 3);
                gm.AddChild(gh);
                gh.AddChild(new Label { Text = g.Title, SizeFlagsHorizontal = SizeFlags.ExpandFill, LabelSettings = HudArt.Text(15, PwTheme.Text, true) });
                gh.AddChild(new Label { Text = $"{g.Got}/{g.Max}", LabelSettings = HudArt.Text(12, new Color(PwTheme.Text, 0.85f)) });
                _list.AddChild(gm);
                _list.AddChild(new ColorRect { Color = new Color(Col.Open, 0.4f), CustomMinimumSize = new Vector2(0, 1) });
            }
            foreach (var q in g.Quests) _list.AddChild(Row(q));
        }
        if (groups.Count == 0) _list.AddChild(new Label { Text = "No quests yet.", LabelSettings = HudArt.Text(14, Col.Open) });
        _total.Text = groups.Count > 0 ? $"Points: {got} / {max}" : "";
        Show(S.SelectedQuest());
        S.LogShown();
    }

    PanelContainer Row(QuestState q)
    {
        var st = MissionUiState.QuestStateOf(q);
        var (color, mark) = Look(st, S.Fresh.Contains(q.Guid) && st == "open");
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
        p.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), ContentMarginLeft = 12, ContentMarginRight = 8, ContentMarginTop = 3, ContentMarginBottom = 3 });
        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", 6);
        p.AddChild(h);
        h.AddChild(new TextureRect { Texture = Check(mark), CustomMinimumSize = new Vector2(16, 16), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore });
        var name = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.Off, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        name.AddThemeFontOverride("normal_font", HudArt.Sans);
        name.AddThemeFontSizeOverride("normal_font_size", 14);
        name.AddThemeColorOverride("default_color", color);
        var head = NewsTickerView.Esc(S.QuestHeadline(q));
        name.Text = st == "failed" ? $"[s]{head}[/s]" : head;
        h.AddChild(name);
        double b = S.Bonus(q);
        h.AddChild(new Label { Text = b != 0 ? b.ToString() : "", LabelSettings = HudArt.Text(12, new Color(color, 0.85f)), MouseFilter = MouseFilterEnum.Ignore });
        p.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                S.LogSel = q.Guid;
                Show(q);
                PwRemake.View.Audio.GameAudio.Current?.Feedback.Ui("ui_click");
            }
        };
        p.MouseEntered += () => { if (S.LogSel != q.Guid) Hl(p, 0.15f); };
        p.MouseExited += () => Hl(p, S.LogSel == q.Guid ? 0.3f : 0);
        _rows[q.Guid] = p;
        return p;
    }

    static void Hl(PanelContainer p, float a)
    {
        if (p.GetThemeStylebox("panel") is StyleBoxFlat f) f.BgColor = new Color(224 / 255f, 185 / 255f, 120 / 255f, a);
    }

    void Show(QuestState? q)
    {
        foreach (var (g, p) in _rows) Hl(p, q != null && g == q.Guid ? 0.3f : 0);
        if (q == null) { _text.Text = ""; return; }
        S.LogSel = q.Guid;
        var st = MissionUiState.QuestStateOf(q);
        double b = S.Bonus(q);
        string state = st switch
        {
            "done" => $"[color={Col.Hex(Col.Done)}][b]{S.Text("_NT_QuestAccomplished", "Quest accomplished")}[/b][/color]\n",
            "failed" => $"[color={Col.Hex(Col.Failed)}][b]{S.Text("_NT_QuestUnaccomplishable", "Quest unaccomplishable")}[/b][/color]\n",
            _ => "",
        };
        _text.Text = $"[font_size=17][b][color={Col.Hex(PwTheme.Hi)}]{NewsTickerView.Esc(S.QuestHeadline(q))}[/color][/b][/font_size]\n" + state +
                     NewsTickerView.Esc(S.QuestDescription(q)) +
                     (b != 0 ? $"\n\n[font_size=12][color={Col.Hex(PwTheme.Dim)}]{(st == "done" ? "Points earned" : "Points")}: {b}[/color][/font_size]" : "");
    }
}
