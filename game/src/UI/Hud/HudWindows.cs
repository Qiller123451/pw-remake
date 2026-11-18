// The HUD's menu windows: the statistics screen (StatisticScreen.usl; opened from the end screen) and the diplomacy
// window (DiplomacyWindow.usl; the top bar's button in skirmish games). Both in the original's window frame (PwWindow)
// over the darkened game, as the browser remake's menus.
//
// Ported from remake/src/ui/stats.js and remake/src/ui/diplomacy.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;
using PwRemake.UI.Mission;

namespace PwRemake.UI.Hud;

/// <summary>A modal menu window over the game (the darkened screen, the window centred).</summary>
public partial class HudModal : Control
{
    public readonly PwWindow Win;
    public HudModal(string caption, float minW, float maxW)
    {
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
        Win = new PwWindow(caption, minW, maxW);
        center.AddChild(Win);
    }
    public static Label Cell(string text, Color c, int size = 14, int width = 0, HorizontalAlignment al = HorizontalAlignment.Center) => new()
    {
        Text = text, HorizontalAlignment = al, CustomMinimumSize = new Vector2(width, 0), ClipText = width > 0,
        LabelSettings = HudArt.Text(size, c),
    };
}

/// <summary>The statistics screen: tabs with a row per player, and the time line graph.</summary>
public partial class HudStatsWindow : HudModal
{
    readonly StatsModel _m;
    readonly VBoxContainer _body;
    readonly HBoxContainer _tabs;
    int _tab, _graph;
    readonly HashSet<int> _shown;

    HudStatsWindow(Hud hud, IEnumerable<QuestState>? quests, Action? back) : base("", 720, 820)
    {
        Name = "Statistics";
        _m = new StatsModel(hud.World, quests);
        _shown = new HashSet<int>(_m.Players.Select(p => p.Id));
        var B = Win.Body;
        B.AddChild(PwTheme.Heading(_m.Ui("Label_GameStatistics", "Game Statistics"), 20));
        _tabs = new HBoxContainer();
        _tabs.AddThemeConstantOverride("separation", 4);
        B.AddChild(_tabs);
        _body = new VBoxContainer { CustomMinimumSize = new Vector2(668, 400) };
        B.AddChild(_body);
        var ok = PwTheme.Btn(_m.Ui("Button_OK", "OK"), () => { QueueFree(); back?.Invoke(); }, 160);
        ok.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        B.AddChild(ok);
        Draw_();
    }

    public static HudStatsWindow Open(Hud hud, IEnumerable<QuestState>? quests, Action? back)
    {
        var w = new HudStatsWindow(hud, quests, back);
        hud.AddChild(w);
        return w;
    }

    /// <summary>a table of many players (up to 16) in a scroll box of at most maxH pixels</summary>
    internal static Control Scrolled(Control table, int rows, float maxH)
    {
        if (rows <= 8) return table;
        var sc = new ScrollContainer { CustomMinimumSize = new Vector2(0, maxH), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        sc.AddChild(table);
        return sc;
    }
    static string Hex(Player p) => ((p.Color & 0xffffff)).ToString("x6");

    void Draw_()
    {
        foreach (var c in _tabs.GetChildren()) c.QueueFree();
        for (int i = 0; i < _m.Tabs.Count; i++)
        {
            int ii = i;
            var b = new Button { Text = _m.Ui("Tab_" + _m.Tabs[i].Key, _m.Tabs[i].Default), ToggleMode = true, ButtonPressed = i == _tab };
            b.AddThemeFontSizeOverride("font_size", 13);
            b.Pressed += () => { _tab = ii; Draw_(); };
            _tabs.AddChild(b);
        }
        foreach (var c in _body.GetChildren()) c.QueueFree();
        var cols = _m.Tabs[_tab].Columns;
        if (cols != null)
        {
            var g = new GridContainer { Columns = cols.Count + 1 };
            g.AddThemeConstantOverride("h_separation", 14);
            g.AddThemeConstantOverride("v_separation", 6);
            g.AddChild(HudModal.Cell("", PwTheme.Hi, 14, 140));
            foreach (var c in cols) g.AddChild(HudModal.Cell(_m.Ui(c.Key, c.Default), PwTheme.Hi, 13, 90));
            foreach (var p in _m.Players)
            {
                g.AddChild(HudModal.Cell(p.Name, HudSprites.Rgb(p.Color), 14, 140, HorizontalAlignment.Left));
                foreach (var c in cols) g.AddChild(HudModal.Cell(_m.Cell(c, p), PwTheme.Text, 14, 90));
            }
            _body.AddChild(Scrolled(g, _m.Players.Count(), 400));
            return;
        }
        // the time line: the category, a box per player, the graph
        var ctl = new HBoxContainer();
        ctl.AddThemeConstantOverride("separation", 10);
        ctl.AddChild(HudModal.Cell(_m.Ui("Label_Category", "Category:"), PwTheme.Text, 14));
        var sel = new OptionButton();
        for (int i = 0; i < StatsModel.GRAPHS.Length; i++) sel.AddItem(_m.Ui("Graph_" + StatsModel.GRAPHS[i].Key, StatsModel.GRAPHS[i].Default), i);
        sel.Selected = _graph;
        sel.ItemSelected += i => { _graph = (int)i; Draw_(); };
        ctl.AddChild(sel);
        // (the players' boxes flow into more lines: up to 16 players)
        var boxes = new HFlowContainer { CustomMinimumSize = new Vector2(668, 0) };
        boxes.AddThemeConstantOverride("h_separation", 10);
        foreach (var p in _m.Players)
        {
            var cb = new CheckBox { Text = p.Name, ButtonPressed = _shown.Contains(p.Id) };
            cb.AddThemeColorOverride("font_color", HudSprites.Rgb(p.Color));
            int id = p.Id;
            cb.Toggled += on => { if (on) _shown.Add(id); else _shown.Remove(id); Draw_(); };
            boxes.AddChild(cb);
        }
        _body.AddChild(ctl);
        _body.AddChild(boxes);
        _body.AddChild(new StatsGraph(_m, _graph, _shown) { CustomMinimumSize = new Vector2(668, 372) });
    }

    /// <summary>the graph (SS:16-365): 4 vertical grid lines, 5 scale values, a line per player in its colour</summary>
    sealed partial class StatsGraph : Control
    {
        readonly StatsModel _m; readonly int _g; readonly HashSet<int> _shown;
        public StatsGraph(StatsModel m, int g, HashSet<int> shown) { _m = m; _g = g; _shown = shown; }
        public override void _Draw()
        {
            float w = Size.X, h = Size.Y, Lm = 44, B = 22;
            DrawRect(new Rect2(0, 0, w, h), new Color(12 / 255f, 10 / 255f, 7 / 255f, 0.85f));
            var (t0, t1, max, lines) = _m.Series(_g, _shown);
            var f = HudSprites.Sans; var tc = HudSprites.Hex("#c8b48a");
            if (t1 <= t0) { DrawString(f, new Vector2(w / 2, h / 2), "-", HorizontalAlignment.Left, -1, 13, HudSprites.Hex("#e0c890")); return; }
            float X(double t) => (float)(Lm + (w - Lm - 8) * (t - t0) / Math.Max(1, t1 - t0));
            float Y(double v) => (float)(h - B - (h - B - 8) * Math.Max(0, v) / max);
            for (int i = 0; i <= 4; i++)
            {
                float x = Lm + (w - Lm - 8) * i / 4;
                DrawLine(new Vector2(x, 8), new Vector2(x, h - B), new Color(224 / 255f, 185 / 255f, 120 / 255f, 0.25f));
                int t = (int)Math.Round(t0 + (t1 - t0) * i / 4);
                DrawString(f, new Vector2(x - 12, h - 6), $"{t / 60}:{t % 60:00}", HorizontalAlignment.Left, -1, 11, tc);
                int v = (int)Math.Round(max * i / 4);
                DrawString(f, new Vector2(4, Y(v) + 4), v.ToString(), HorizontalAlignment.Left, -1, 11, tc);
            }
            foreach (var p in _m.Players)
            {
                if (!lines.TryGetValue(p.Id, out var l) || l.Count < 2) continue;
                DrawPolyline(l.Select(s => new Vector2(X(s.T), Y(s.V))).ToArray(), HudSprites.Rgb(p.Color), 2);
            }
        }
    }
}

/// <summary>The diplomacy window: a row per other player - name, team, colour, my opinion of them (Friend / Neutral /
/// Enemy), their opinion of me (Status) and the tribute (food, wood or stone, an amount, Send; Request asks a computer
/// player). The opinions are locked with "Lock diplomacy settings", in Domination and Defender. The game runs on.</summary>
public partial class HudDiplomacyWindow : HudModal
{
    static readonly string[] REL = { "Enemy", "Neutral", "Friend" };
    static readonly Color[] STATUS = { HudSprites.Hex("#ff7a5a"), HudSprites.Hex("#ffffff"), HudSprites.Hex("#8de07a") };
    readonly Hud _hud;

    HudDiplomacyWindow(Hud hud) : base("", 640, 900)
    {
        _hud = hud;
        Name = "Diplomacy";
        Build();
    }
    public static HudDiplomacyWindow Open(Hud hud) { var w = new HudDiplomacyWindow(hud); hud.AddChild(w); return w; }

    void Build()
    {
        var W = _hud.World; var D = W.Data; var me = _hud.Host.Me;
        string Ui(string k, string d) => D.Ui(k, d);
        var B = Win.Body;
        foreach (var c in B.GetChildren()) c.QueueFree();
        bool locked = W.Opts.DiplomacyLocked || W.Go?.Mode == "domination" || W.Go?.Mode == "defender";
        // a co-op campaign game: the seats only, for the tribute (the mission's diplomacy is its triggers')
        var coop = (W.Campaign as PwRemake.Sim.Campaign.Campaign)?.Coop;
        if (coop != null) locked = true;
        B.AddChild(PwTheme.Heading(Ui("_PlayersWindow_Title_Diplomacy", "Diplomacy"), 20));
        var g = new GridContainer { Columns = 11 };
        g.AddThemeConstantOverride("h_separation", 10);
        g.AddThemeConstantOverride("v_separation", 6);
        foreach (var (k, d) in new[] { ("_PlayersWindow_Player", "Player"), ("_PlayersWindow_Team", "Team"), ("_PlayersWindow_Color", "Color"), ("_PlayersWindow_Label_Friend", "Friend"),
                     ("_PlayersWindow_Label_Neutral", "Neutral"), ("_PlayersWindow_Label_Enemy", "Enemy"), ("_PlayersWindow_Label_Status", "Status"), ("_PlayersWindow_Label_Tribute", "Tribute"), ("", ""), ("", ""), ("", "") })
            g.AddChild(HudModal.Cell(k.Length > 0 ? Ui(k, d) : "", PwTheme.Hi, 13));
        foreach (var p in W.Players.Where(p => p != me && (coop == null || coop.IsSeatSlot(p.Id))))
        {
            bool gone = p.Defeated;
            int mine = me.Relation(p), theirs = p.Relation(me);
            g.AddChild(HudModal.Cell(p.Name, HudSprites.Rgb(p.Color), 14, 120, HorizontalAlignment.Left));
            g.AddChild(HudModal.Cell(p.Team != 0 ? p.Team.ToString() : "-", PwTheme.Text));
            g.AddChild(new ColorRect { Color = HudSprites.Rgb(p.Color), CustomMinimumSize = new Vector2(18, 14), SizeFlagsVertical = SizeFlags.ShrinkCenter });
            foreach (var r in new[] { 2, 1, 0 })
            {
                var cb = new CheckBox { ButtonPressed = mine == r, Disabled = locked || gone };
                int rr = r; var pp = p;
                // my opinion of them (SetMyOpinion)
                cb.Pressed += () => { _hud.Model.G.Send(new DiplomacyCommand { Other = pp.Id, Relation = rr }); _hud.Host.UiSound("UI_click"); CallDeferred(nameof(Rebuild)); };
                g.AddChild(cb);
            }
            g.AddChild(HudModal.Cell(Ui("_UI_DiplWind_Option_" + REL[theirs], REL[theirs]), STATUS[Math.Clamp(theirs, 0, 2)]));
            var res = new OptionButton { Disabled = gone };
            foreach (var r in new[] { "food", "wood", "stone" }) res.AddItem(Ui("_UI_DiplWind_" + r, r));
            g.AddChild(res);
            // amounts: digits only, at most 5 (DW:90-100)
            var n = new LineEdit { Text = "0", MaxLength = 5, CustomMinimumSize = new Vector2(60, 0), Editable = !gone };
            n.TextChanged += t => { var d = new string(t.Where(char.IsDigit).ToArray()); if (d != t) { n.Text = d; n.CaretColumn = d.Length; } };
            g.AddChild(n);
            var send = new Button { Text = "Send", Disabled = gone };
            var p2 = p;
            // Send: the resources go over at once (DW:72-88)
            send.Pressed += () =>
            {
                int.TryParse(n.Text, out var amt);
                if (amt > 0) _hud.Model.G.Send(new TributeCommand { To = p2.Id, Res = new[] { "food", "wood", "stone" }[Math.Max(0, res.Selected)], Amount = amt });
                // a computer player thanks for it (ai/chat.js "gift")
                if (amt > 0 && W.Ai?.Get(p2.Id) != null) _hud.Model.G.Send(new AiRequestCommand { Slot = p2.Id, Word = "gift" });
                n.Text = "0";
            };
            g.AddChild(send);
            // Request: a computer player sends 100 of it when it can (the amount is not read, AIC:683-720)
            var req = new Button { Text = "Request", Disabled = gone || W.Ai?.Get(p.Id) == null };
            req.Pressed += () => _hud.Model.G.Send(new AiRequestCommand { Slot = p2.Id, Word = new[] { "food", "wood", "stone" }[Math.Max(0, res.Selected)] });
            g.AddChild(req);
        }
        B.AddChild(HudStatsWindow.Scrolled(g, W.Players.Count - 1, 520));
        var close = PwTheme.Btn(Ui("_PlayersWindow_Button_Close", "Close"), () => QueueFree(), 160);
        close.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        B.AddChild(close);
    }

    void Rebuild() => Build();
}
