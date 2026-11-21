// The in-game help book (HelpMenu.usl / HelpMenu.srf): a 765 x 515 frame window above the centre of the screen
// (95 px up), the navigation buttons Home / Back / Forward in the original's art (hud_helpnavigation: helpbtn_*_idle /
// _hover / _down / _disabled of the atlas), under them the index (270 px: the topics of index.txt with the chapters,
// sections and help items of their files) and the page (the file's chapters and help items, its links, its pictures:
// the class cards, the interface sprites, the mouse pointers), and Close at the bottom right. The texts are the game's
// own (Texts/Help/*.seml through help.json); the logic is HelpBook (Logic/HelpBook.cs).
//
// Opened from the pause menu ("Help") or with F1 (GameSession.OpenHelp: at the help item of what is hovered, placed
// or selected); the game stands still meanwhile, as the original's (CGameWrap.Pause). Esc or Close go back.
//
// Ported from remake/src/ui/helpbook.js (pw-exporter 98c7703) and Game/UI/HelpMenu.usl, HelpMenu.srf.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.UI.Hud;
using PwRemake.UI.Mission;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.Help;

public partial class HelpWindow : Control
{
    static readonly Logger L = Log.For("UI.Help");

    // the window and its boxes (HelpMenu.usl: SetSize(765, 515), 95 px above the centre; HelpMenu.srf: the rows 30 /
    // fill / 30, the index 270 px, Close 100 px)
    public const int WinW = 765, WinH = 515, Up = 95, NavH = 30, IndexW = 270, CloseW = 100;
    static readonly Color Link = HudSprites.Hex("#9fd0ff"), ChapterC = HudSprites.Hex("#ffd98a"), TitleC = HudSprites.Hex("#ffe7a8"),
        MediumC = HudSprites.Hex("#d8c79a"), BodyC = HudSprites.Hex("#f0e2bd");

    static HelpBook? s_book;
    static string? s_bookOf;

    readonly Action _onClose;
    readonly HelpHistory _hist = new();
    readonly string _start;
    float _k = 1;
    HelpBook _book = null!;
    Tree _index = null!;
    readonly Dictionary<HelpTopic, TreeItem> _items = new();
    ScrollContainer _scroll = null!;
    VBoxContainer _page = null!;
    readonly Dictionary<string, Control> _anchors = new();
    string? _file;
    bool _selecting;
    BaseButton _home = null!, _back = null!, _fwd = null!;

    /// <summary>the page shown ("file" / "file#key")</summary>
    public string? Current => _hist.Current;
    public HelpBook Book => _book;
    public HelpHistory History => _hist;

    /// <summary>start: the page to open (a "file#key" or the candidates' first match, see StartAt); null: Home</summary>
    public HelpWindow(string? start, Action onClose)
    {
        _start = start ?? "";
        _onClose = onClose;
        Name = "HelpWindow";
        MouseFilter = MouseFilterEnum.Stop;
        ProcessMode = ProcessModeEnum.Always;
    }

    /// <summary>the help book of the configuration (help.json of its data; else of another configuration's), cached</summary>
    public static HelpBook LoadBook()
    {
        var id = GameApp.Config?.Id ?? "";
        if (s_book != null && s_bookOf == id) return s_book;
        System.Text.Json.Nodes.JsonNode? json = null;
        var sources = new List<AssetSource>();
        if (GameApp.Assets != null) sources.Add(GameApp.Assets);
        foreach (var c in GameApp.Configurations) if (c != GameApp.Config) sources.Add(new AssetSource(c));
        foreach (var s in sources)
        {
            var p = s.Resolve("help.json");
            if (p == null) continue;
            try { json = Json.TryParseFile(p); } catch (Exception ex) { L.Warn($"help.json: {ex.Message}"); }
            if (json != null) { L.Info($"help book: {p}"); break; }
        }
        if (json == null) L.Warn("no help.json in the game data: the help book is empty - re-run the toolkit's \"Prepare the game data\"");
        s_book = HelpBook.Load(json);
        s_bookOf = id;
        return s_book;
    }

    /// <summary>the page F1 opens at: the first candidate the index has (StartHelpNav), else Home</summary>
    public static string StartAt(IEnumerable<string?> candidates) => LoadBook().StartPage(candidates);

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // the menus' theme with the original's scroll bars (the index's and the page's)
        var th = (Theme)PwTheme.Theme.Duplicate();
        ScrollBarStyles(th);
        Theme = th;
        if (UiAtlas.Count == 0) HudArt.Init(GameApp.Assets);
        _book = LoadBook();
        var vp = GetViewportRect().Size;
        _k = Mathf.Clamp(Mathf.Min(vp.Y / 900f, vp.X / 1600f), 1f, 2.4f);
        Build(vp);
        Navigate(_start != "" ? _start : _book.Home);
    }

    int K(float v) => (int)Math.Round(v * _k);

    void Build(Vector2 vp)
    {
        var win = new PwWindow(UiText("_UI_HelpMenu_Label_Help", "Help"), K(WinW), K(WinW) + 1) { CustomMinimumSize = new Vector2(K(WinW), K(WinH)) };
        AddChild(win);
        float x = Mathf.Max(0, (vp.X - K(WinW)) / 2), y = Mathf.Clamp((vp.Y - K(WinH)) / 2 - K(Up), 0, Mathf.Max(0, vp.Y - K(WinH)));
        win.Position = new Vector2(x, y);
        win.Size = new Vector2(K(WinW), K(WinH));
        var body = win.Body;
        body.CustomMinimumSize = Vector2.Zero;
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddThemeConstantOverride("separation", K(4));

        // ---- the navigation row
        var nav = new HBoxContainer { CustomMinimumSize = new Vector2(0, K(NavH)) };
        nav.AddThemeConstantOverride("separation", 0);
        _home = NavButton("home", 42, () => Navigate(_book.Home));
        _back = NavButton("back", 34, () => { if (_hist.Back()) Show(); });
        _fwd = NavButton("fwd", 46, () => { if (_hist.Fwd()) Show(); });
        nav.AddChild(_home); nav.AddChild(_back); nav.AddChild(_fwd);
        body.AddChild(nav);

        // ---- the index and the page
        var mid = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        mid.AddThemeConstantOverride("separation", K(6));
        body.AddChild(mid);
        _index = new Tree
        {
            CustomMinimumSize = new Vector2(K(IndexW), 0), SizeFlagsVertical = SizeFlags.ExpandFill, HideRoot = true,
            FocusMode = FocusModeEnum.None, ScrollHorizontalEnabled = false,
        };
        StyleIndex(_index);
        mid.AddChild(_index);
        var root = _index.CreateItem();
        foreach (var t in _book.Roots) AddTopic(root, t);
        _index.ItemSelected += () =>
        {
            if (_selecting) return;
            var it = _index.GetSelected();
            if (it?.GetMetadata(0).AsString() is { Length: > 0 } r) Navigate(r);
        };

        var frame = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        frame.AddThemeStyleboxOverride("panel", PageBox());
        mid.AddChild(frame);
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        frame.AddChild(_scroll);
        _page = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _page.AddThemeConstantOverride("separation", K(6));
        _scroll.AddChild(_page);

        // ---- Close (the string of the players' window, as the original: "_PlayersWindow_Button_Close")
        var bottom = new HBoxContainer { CustomMinimumSize = new Vector2(0, K(NavH)) };
        bottom.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var close = PwTheme.Btn(UiText("_PlayersWindow_Button_Close", "Close"), () => _onClose(), K(CloseW));
        close.Name = "Close";
        bottom.AddChild(close);
        body.AddChild(bottom);
    }

    static string UiText(string key, string d)
    {
        try { var r = GameSession.Current?.Rules; if (r != null) return r.Ui(key, d); } catch (Exception) { }
        return d;
    }

    BaseButton NavButton(string name, int w, Action click)
    {
        AtlasTexture? A(string state) => HudSprites.Atlas($"helpbtn_{name}_{state}", -1);
        string tip = name switch { "home" => "Home", "back" => "Back", _ => "Forward" };
        BaseButton b;
        if (A("idle") is { } idle)
            b = new TextureButton
            {
                TextureNormal = idle, TextureHover = A("hover"), TexturePressed = A("down"), TextureDisabled = A("disabled"),
                IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.Scale, CustomMinimumSize = new Vector2(K(w), K(NavH)),
            };
        else
        {
            // (no interface atlas in this configuration's data: plain buttons)
            L.WarnOnce("helpnav", "no help navigation art (atlas helpbtn_*: hud_helpnavigation.png): plain buttons");
            b = new Button { Text = tip, CustomMinimumSize = new Vector2(K(80), K(NavH)) };
        }
        b.Name = "Nav" + name;
        b.FocusMode = FocusModeEnum.None;
        b.TooltipText = tip;
        b.Pressed += click;
        return b;
    }

    void AddTopic(TreeItem parent, HelpTopic t)
    {
        var it = _index.CreateItem(parent);
        it.SetText(0, t.Text);
        it.SetMetadata(0, t.Ref);
        it.Collapsed = true;
        if (t.Kind == "topic") it.SetCustomColor(0, PwTheme.Hi);
        _items[t] = it;
        foreach (var s in t.Subs) AddTopic(it, s);
    }

    // ======================================================================================== navigation
    /// <summary>Navigate (HelpMenu.Navigate): a page "file" or "file#key"</summary>
    public void Navigate(string page)
    {
        if (string.IsNullOrEmpty(page)) return;
        _hist.Go(page);
        Show();
    }

    new void Show()
    {
        var cur = _hist.Current ?? _book.Home;
        var (file, key) = HelpBook.Split(cur);
        if (file != _file) Render(file);
        TreeSelect(cur);
        _back.Disabled = !_hist.CanBack;
        _fwd.Disabled = !_hist.CanForward;
        // to the item once the page is laid out
        CallDeferred(nameof(ScrollTo), key);
    }

    void ScrollTo(string key)
    {
        if (!IsInsideTree()) return;
        GetTree().ProcessFrame += Once;
        void Once()
        {
            GetTree().ProcessFrame -= Once;
            if (!IsInstanceValid(_scroll)) return;
            _scroll.ScrollVertical = key != "" && _anchors.TryGetValue(key, out var c) ? (int)c.Position.Y : 0;
        }
    }

    // TreeSelect: the entry of the page selected and visible (its parents opened)
    void TreeSelect(string page)
    {
        var t = _book.TopicOf(page);
        if (t == null || !_items.TryGetValue(t, out var it)) { _index.DeselectAll(); return; }
        for (var p = it.GetParent(); p != null; p = p.GetParent()) p.Collapsed = false;
        _selecting = true;
        it.Select(0);
        _selecting = false;
        _index.ScrollToItem(it);
    }

    // ======================================================================================== the page
    void Render(string file)
    {
        _file = file;
        foreach (var c in _page.GetChildren()) { _page.RemoveChild(c); c.QueueFree(); }
        _anchors.Clear();
        var doc = _book.Doc(file);
        if (doc.Count == 0) { _page.AddChild(Rich("No help text.", BodyC, 14)); return; }
        foreach (var p in doc)
        {
            Control c;
            if (p.Kind != "item")
            {
                var box = new VBoxContainer();
                box.AddThemeConstantOverride("separation", 1);
                var l = new Label { Text = p.Title, LabelSettings = HudArt.Text(K(p.Kind == "chapter" ? 17 : 15), ChapterC, true) };
                box.AddChild(l);
                if (p.Kind == "chapter") box.AddChild(new ColorRect { Color = new Color(ChapterC, 0.3f), CustomMinimumSize = new Vector2(0, 1) });
                if (_page.GetChildCount() > 0) box.AddChild(new Control { CustomMinimumSize = new Vector2(0, K(2)) });
                c = box;
            }
            else
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", K(8));
                var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                col.AddThemeConstantOverride("separation", K(3));
                if (p.Title != "") col.AddChild(new Label { Text = p.Title, LabelSettings = HudArt.Text(K(15), TitleC, true), AutowrapMode = TextServer.AutowrapMode.WordSmart });
                if (p.Medium.Trim() != "") col.AddChild(Rich(p.Medium, MediumC, 14));
                if (p.Long.Trim() != "") col.AddChild(Rich(p.Long, BodyC, 14));
                row.AddChild(col);
                var pic = Picture(HelpPicture.Parse(p.Img), true);
                if (pic != null) row.AddChild(new TextureRect
                {
                    Texture = pic.Value.Tex, CustomMinimumSize = pic.Value.Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = SizeFlags.ShrinkBegin,
                });
                c = row;
            }
            _page.AddChild(c);
            if (p.Key != "") _anchors.TryAdd(p.Key, c);
        }
    }

    RichTextLabel Rich(string text, Color color, int size)
    {
        var r = new RichTextLabel
        {
            FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, SelectionEnabled = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, MetaUnderlined = true, MouseFilter = MouseFilterEnum.Pass,
        };
        r.AddThemeFontOverride("normal_font", PwTheme.BodyFont);
        r.AddThemeFontSizeOverride("normal_font_size", K(size));
        r.AddThemeColorOverride("default_color", color);
        r.AddThemeConstantOverride("line_separation", K(2));
        foreach (var run in HelpBook.Parse(text))
        {
            switch (run.Kind)
            {
                case "link":
                    r.PushMeta(run.Ref);
                    r.PushColor(Link);
                    r.AddText(run.Text);
                    r.Pop(); r.Pop();
                    break;
                case "img":
                    var pic = Picture(HelpPicture.Parse(run.Ref), false);
                    if (pic != null) r.AddImage(pic.Value.Tex, (int)pic.Value.Size.X, (int)pic.Value.Size.Y, inlineAlign: InlineAlignment.Center);
                    break;
                default:
                    r.AddText(run.Text);
                    break;
            }
        }
        r.MetaClicked += meta => Navigate(meta.AsString());
        return r;
    }

    /// <summary>a picture of the help: a class card (64 px at the item, 40 inline), an interface sprite (its own size
    /// at the item, 22 px inline), a mouse pointer (24 px)</summary>
    (Texture2D Tex, Vector2 Size)? Picture(HelpPicture? p, bool big)
    {
        if (p == null) return null;
        var v = p.Value;
        switch (v.Kind)
        {
            case "card":
            {
                var icon = UiAtlas.IconFor(v.Name);
                var t = icon == null ? null : HudSprites.Atlas(icon, 4);
                if (t == null) { if (UiAtlas.Count == 0) L.WarnOnce("helpatlas", "help: no interface atlas (assets/ui/atlas.json): no pictures"); else L.WarnOnce("helpcard:" + v.Name, $"help: no card of {v.Name}"); return null; }
                return (t, new Vector2(K(big ? 64 : 40), K(big ? 64 : 40)));
            }
            case "sprite":
            {
                var t = HudSprites.Atlas(v.Name, v.Level);
                if (t == null) { if (UiAtlas.Count == 0) L.WarnOnce("helpatlas", "help: no interface atlas (assets/ui/atlas.json): no pictures"); else L.WarnOnce("helpsprite:" + v.Name, $"help: no sprite {v.Name}"); return null; }
                var s = t.Region.Size;
                float side = big ? Mathf.Min(64, Mathf.Max(s.X, s.Y)) : 22;
                float f = side / Mathf.Max(1, Mathf.Max(s.X, s.Y));
                return (t, new Vector2(K(s.X * f), K(s.Y * f)));
            }
            case "cursor":
            {
                var t = HudArt.Tex(v.Name);
                return t == null ? null : (t, new Vector2(K(24), K(24)));
            }
        }
        return null;
    }

    // ======================================================================================== look
    // the page's box: the menu's text box tile (HelpMenu.srf HelpText: frame, background) with the frame's line
    StyleBox PageBox()
    {
        var tile = PwTheme.Tex("controls__textbox_backtile");
        StyleBox b;
        if (tile != null)
            b = new StyleBoxTexture { Texture = tile, AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile, ModulateColor = new Color(1, 1, 1, 0.92f) };
        else b = new StyleBoxFlat { BgColor = new Color(0.125f, 0.125f, 0.125f, 0.92f) };
        b.ContentMarginLeft = K(10); b.ContentMarginRight = K(4); b.ContentMarginTop = K(6); b.ContentMarginBottom = K(6);
        return b;
    }

    void StyleIndex(Tree t)
    {
        var tile = PwTheme.Tex("controls__textbox_backtile");
        var bg = tile != null
            ? new StyleBoxTexture { Texture = tile, AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile, ModulateColor = new Color(1, 1, 1, 0.92f) }
            : (StyleBox)new StyleBoxFlat { BgColor = new Color(0.125f, 0.125f, 0.125f, 0.92f) };
        bg.ContentMarginLeft = K(3); bg.ContentMarginTop = K(3); bg.ContentMarginBottom = K(3);
        t.AddThemeStyleboxOverride("panel", bg);
        t.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var sel = new StyleBoxFlat { BgColor = new Color(0.94f, 0.78f, 0.47f, 0.22f) };
        t.AddThemeStyleboxOverride("selected", sel);
        t.AddThemeStyleboxOverride("selected_focus", sel);
        t.AddThemeStyleboxOverride("hovered", new StyleBoxFlat { BgColor = new Color(0.94f, 0.78f, 0.47f, 0.08f) });
        t.AddThemeFontOverride("font", PwTheme.BodyFont);
        t.AddThemeFontSizeOverride("font_size", K(13));
        t.AddThemeColorOverride("font_color", PwTheme.Text);
        t.AddThemeColorOverride("font_selected_color", PwTheme.Hi);
        t.AddThemeColorOverride("font_hovered_color", PwTheme.Hi);
        t.AddThemeColorOverride("guide_color", new Color(0, 0, 0, 0));
        t.AddThemeConstantOverride("item_margin", K(12));
        t.AddThemeConstantOverride("v_separation", K(1));
        t.AddThemeColorOverride("relationship_line_color", new Color(PwTheme.Dim, 0.25f));
    }

    // the menus' scroll bar: rail scrollbar_vrt_rail_idle, mover scrollbar_vrt_mover_top / _center / _bottom
    void ScrollBarStyles(Theme th)
    {
        var rail = PwTheme.Img("controls__scrollbar_vrt_rail_idle");
        var top = PwTheme.Img("controls__scrollbar_vrt_mover_top");
        var mid = PwTheme.Img("controls__scrollbar_vrt_mover_center");
        var bot = PwTheme.Img("controls__scrollbar_vrt_mover_bottom");
        if (rail == null || top == null || mid == null || bot == null) return;
        foreach (var i in new[] { top, mid, bot }) { if (i.IsCompressed()) i.Decompress(); i.Convert(Image.Format.Rgba8); }
        int w = top.GetWidth(), h = top.GetHeight() + mid.GetHeight() + bot.GetHeight();
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        img.BlitRect(top, new Rect2I(0, 0, w, top.GetHeight()), Vector2I.Zero);
        img.BlitRect(mid, new Rect2I(0, 0, w, mid.GetHeight()), new Vector2I(0, top.GetHeight()));
        img.BlitRect(bot, new Rect2I(0, 0, w, bot.GetHeight()), new Vector2I(0, top.GetHeight() + mid.GetHeight()));
        var grab = new StyleBoxTexture
        {
            Texture = ImageTexture.CreateFromImage(img), TextureMarginTop = top.GetHeight(), TextureMarginBottom = bot.GetHeight(),
            ContentMarginLeft = K(w) / 2f, ContentMarginRight = K(w) / 2f,
        };
        var hi = (StyleBoxTexture)grab.Duplicate();
        hi.ModulateColor = new Color(1.15f, 1.12f, 1.05f);
        var railBox = new StyleBoxTexture
        {
            Texture = ImageTexture.CreateFromImage(rail), AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
            ContentMarginLeft = K(rail.GetWidth()) / 2f, ContentMarginRight = K(rail.GetWidth()) / 2f,
        };
        th.SetStylebox("scroll", "VScrollBar", railBox);
        th.SetStylebox("scroll_focus", "VScrollBar", railBox);
        th.SetStylebox("grabber", "VScrollBar", grab);
        th.SetStylebox("grabber_highlight", "VScrollBar", hi);
        th.SetStylebox("grabber_pressed", "VScrollBar", hi);
    }

    // ======================================================================================== keys
    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k && k.Keycode is Key.Escape or Key.F1)
        {
            GetViewport()?.SetInputAsHandled();   // first: closing takes the window out of the tree
            if (k.Keycode == Key.Escape) _onClose();
        }
    }
}
