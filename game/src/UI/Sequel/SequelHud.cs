// The interface of the remake's own campaigns beside the HUD (docs/SEQUEL.md "Interface"): the objectives in the
// right column above the minimap (done / failed / counts / notes, the rifts being sealed), the electricity line (the
// stock, what the connected plants make, plants cut off), the command points and the powers' buttons, the radio box at
// the bottom (the speaker's portrait card from cast.json - the game's own card atlas - and the line for a reading
// time), the mission clock, and the briefing window before the mission (the game waits).
// It only reads the runtime (SequelRuntime) and sends commands (SequelCommand through the session).
// Ported from remake/src/ui/sequel_ui.js (briefing, update, power) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Sequel;
using PwRemake.UI.Hud;
using PwRemake.UI.Mission;

namespace PwRemake.UI.Sequel;

public partial class SequelHud : CanvasLayer
{
    readonly GameSession G;
    readonly SequelSession S;
    SequelRuntime R => S.Runtime;
    readonly JsonObject _cast;
    public PanelContainer Panel { get; private set; } = null!;
    public PanelContainer Radio { get; private set; } = null!;
    RichTextLabel _obj = null!;
    Label _power = null!, _cp = null!, _clock = null!, _radioName = null!, _radioText = null!;
    TextureRect _portrait = null!;
    Label _initial = null!;
    VBoxContainer _powers = null!;
    readonly Dictionary<string, Button> _powerBtns = new();
    string _objKey = "", _pwKey = "", _powKey = "";
    int _lineAt;
    double _lineT;
    SqLine? _line;
    Control? _brief;
    float _s = 1;

    public SequelHud(GameSession g, SequelSession s)
    {
        G = g; S = s; Layer = 11;
        _cast = SequelCatalog.Cast();
        if (g.Resume != null) _lineAt = s.Runtime.Lines.Count;      // (a loaded game: the lines said before are not said again)
    }

    static StyleBoxFlat Box(Color border, float alpha = 0.82f) => new()
    {
        BgColor = new Color(0.10f, 0.07f, 0.04f, alpha), BorderColor = border, BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
        CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6,
    };
    static readonly Color Gold = new(0.63f, 0.48f, 0.25f);

    public override void _Ready()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = PwTheme.Theme };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        // ---- the right column: objectives, electricity, command powers
        Panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        Panel.AddThemeStyleboxOverride("panel", Box(Gold));
        root.AddChild(Panel);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        Panel.AddChild(col);
        var head = new Label { Text = "Objectives" };
        head.AddThemeColorOverride("font_color", PwTheme.Hi);
        head.AddThemeFontOverride("font", HudSprites.SerifBold);
        head.AddThemeFontSizeOverride("font_size", 15);
        col.AddChild(head);
        _obj = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(250, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _obj.AddThemeFontSizeOverride("normal_font_size", 13);
        _obj.AddThemeFontSizeOverride("bold_font_size", 13);
        _obj.AddThemeColorOverride("default_color", PwTheme.Text);
        col.AddChild(_obj);
        col.AddChild(new HSeparator());
        _power = new Label { TooltipText = "Electricity: made by plants (workers in generating stations, generators and water wheels; windmills by themselves) and carried to your headquarters by power poles, at most 40 m apart. Spent on electric units and structures.", MouseFilter = Control.MouseFilterEnum.Pass };
        _power.AddThemeFontSizeOverride("font_size", 13);
        _power.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.45f));
        col.AddChild(_power);
        _cp = new Label { TooltipText = "Command points: earned by objectives, sealed rifts and captures", MouseFilter = Control.MouseFilterEnum.Pass };
        _cp.AddThemeFontSizeOverride("font_size", 14);
        _cp.AddThemeColorOverride("font_color", PwTheme.Hi);
        col.AddChild(_cp);
        _powers = new VBoxContainer();
        _powers.AddThemeConstantOverride("separation", 3);
        col.AddChild(_powers);
        foreach (var (id, P) in SequelRuntime.POWERS)
        {
            if (!R.PowerAllowed(id)) continue;
            var b = new Button { Text = P.Name, TooltipText = $"{P.Name} ({P.Cost} CP): {P.Text}", CustomMinimumSize = new Vector2(250, 26), FocusMode = Control.FocusModeEnum.None };
            b.AddThemeFontSizeOverride("font_size", 13);
            var pid = id;
            b.Pressed += () => { G.UseSequelPower(pid); };
            _powers.AddChild(b);
            _powerBtns[id] = b;
        }
        // (a scenario without powers: no electricity, no powers)
        bool noPow = R.Kind == "scenario" && _powerBtns.Count == 0;
        _power.Visible = !noPow; _cp.Visible = !noPow;
        // ---- the radio box
        Radio = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop, TooltipText = "Click: the next line" };
        Radio.AddThemeStyleboxOverride("panel", Box(Gold, 0.88f));
        root.AddChild(Radio);
        var rb = new HBoxContainer();
        rb.AddThemeConstantOverride("separation", 10);
        Radio.AddChild(rb);
        var pf = new Control { CustomMinimumSize = new Vector2(64, 64), MouseFilter = Control.MouseFilterEnum.Ignore };
        rb.AddChild(pf);
        _portrait = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
        _portrait.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        pf.AddChild(_portrait);
        _initial = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _initial.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _initial.AddThemeFontSizeOverride("font_size", 34);
        pf.AddChild(_initial);
        var tv = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rb.AddChild(tv);
        _radioName = new Label();
        _radioName.AddThemeFontOverride("font", HudSprites.SerifBold);
        _radioName.AddThemeFontSizeOverride("font_size", 15);
        tv.AddChild(_radioName);
        _radioText = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(440, 0) };
        _radioText.AddThemeFontSizeOverride("font_size", 14);
        _radioText.AddThemeColorOverride("font_color", PwTheme.Text);
        tv.AddChild(_radioText);
        Radio.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _lineT = 0; };
        // ---- the clock
        _clock = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _clock.AddThemeFontOverride("font", HudSprites.SerifBold);
        _clock.AddThemeFontSizeOverride("font_size", 20);
        _clock.AddThemeColorOverride("font_color", PwTheme.Hi);
        _clock.AddThemeColorOverride("font_outline_color", Colors.Black);
        _clock.AddThemeConstantOverride("outline_size", 5);
        root.AddChild(_clock);
    }

    /// <summary>where the panels go: the right column above the minimap, the radio above the command bar, the clock at the
    /// top (the HUD's scale)</summary>
    void Layout()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        _s = G.HudView?.S ?? 1;
        var sv = new Vector2(_s, _s);
        Panel.Scale = sv;
        float top = 78 * _s, bottom = vp.Y - (HudMinimap.H + 8) * _s;
        Panel.Position = new Vector2(vp.X - (Panel.Size.X + 6) * _s, top);
        Panel.Visible = !G.Cinematic && bottom - top > 80;
        Radio.Scale = sv;
        Radio.Position = new Vector2((HudInfo.W + 8) * _s, vp.Y - (59 + 12) * _s - Radio.Size.Y * _s);
        _clock.Scale = sv;
        _clock.Position = new Vector2(vp.X / 2 - _clock.Size.X * _s / 2, 70 * _s);
    }

    public override void _Process(double delta)
    {
        if (R == null) return;
        Layout();
        double dt = Math.Min(0.1, delta);
        // ---- the radio: the lines one after the other, each for a reading time
        // (not while the briefing is open or the game stands still)
        if (_brief == null && !G.Paused) _lineT -= dt;
        if (_lineT <= 0 && _brief == null)
        {
            if (_lineAt < R.Lines.Count)
            {
                var next = R.Lines[_lineAt++];
                ShowLine(next);
                _lineT = 3 + next.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 0.33;
                try { G.HudView?.Host.UiSound("UI_click"); } catch (Exception) { }
            }
            else if (_line != null) { _line = null; Radio.Visible = false; }
        }
        // ---- objectives, the rifts being sealed
        var sealing = R.Rifts.Where(r => r.Open && r.Seal > 0).Select(r => (int)Math.Floor(100 * r.Seal / r.SealTime)).ToList();
        var key = string.Join("|", R.Objectives.Select(o => $"{o.Id}{o.Done}{o.Failed}{o.Count}/{o.Of}{o.Note}{o.Hidden}")) + "#" + string.Join(",", sealing);
        if (key != _objKey)
        {
            _objKey = key;
            var sb = new StringBuilder();
            foreach (var o in R.Objectives)
            {
                if (o.Hidden) continue;
                string mark = o.Done ? "[color=#9be07a]✔[/color]" : o.Failed ? "[color=#ff8060]✘[/color]" : "•";
                string col = o.Done ? "#9be07a" : o.Failed ? "#ff9a7a" : o.Secondary ? "#c8b080" : "#ffe9c0";
                sb.Append($"{mark} [color={col}]{Esc(o.Text)}[/color]");
                if (o.Of > 0) sb.Append($" [b]{o.Count}/{o.Of}[/b]");
                if (o.Note != "" && !o.Done && !o.Failed) sb.Append($"\n   [font_size=11][color=#a89068]{Esc(o.Note)}[/color][/font_size]");
                sb.Append('\n');
            }
            foreach (var p in sealing) sb.Append($"⟳ [color=#80e0ff]Sealing the rift [b]{p}%[/b][/color]\n");
            _obj.Text = sb.ToString().TrimEnd('\n');
        }
        // ---- electricity
        var rt = R.Grid.Rates();
        int stock = (int)Math.Floor(G.Me["power"]);
        var pk = $"{stock}|{rt.On}|{rt.Cut}|{rt.Plants}";
        if (pk != _pwKey)
        {
            _pwKey = pk;
            _power.Text = $"⚡ {stock}   {(rt.Plants > 0 ? $"+{rt.On}/min" : "no plants")}" + (rt.Cut > 0 ? $"   {rt.Cut} plant{(rt.Cut > 1 ? "s" : "")} cut off" : "");
        }
        // ---- command powers
        var pwk = string.Join("|", _powerBtns.Keys.Select(id => id + R.PowerState(id) + Math.Ceiling(Math.Max(0, R.PowerReady.GetValueOrDefault(id) - R.Time)))) + "#" + R.Cp;
        if (pwk != _powKey)
        {
            _powKey = pwk;
            _cp.Text = $"{R.Cp} CP";
            foreach (var (id, b) in _powerBtns)
            {
                var P = SequelRuntime.POWERS.Get(id)!;
                var st = R.PowerState(id);
                int cd = (int)Math.Ceiling(Math.Max(0, R.PowerReady.GetValueOrDefault(id) - R.Time));
                b.Text = $"{P.Name}   ({(st == "cooldown" ? cd + " s" : P.Cost + " CP")})";
                b.Modulate = st == "ready" ? Colors.White : new Color(0.65f, 0.6f, 0.55f);
            }
        }
        // ---- the clock (a countdown the mission set: dawn, lift-off, the charge, a scenario's wave)
        if (R.ClockAt > 0 && R.Ended == null && R.Time < R.ClockAt)
        {
            double left = R.ClockAt - R.Time;
            _clock.Visible = true;
            _clock.Text = (R.ClockLabel != "" ? R.ClockLabel + " " : "") + $"{(int)(left / 60)}:{(int)(left % 60):00}";
        }
        else _clock.Visible = false;
    }

    void ShowLine(SqLine l)
    {
        _line = l;
        var c = _cast[l.Who] as JsonObject ?? _cast["radio"] as JsonObject;
        string name = c?["name"].Str() ?? l.Who;
        var col = Color.FromHtml(c?["color"].Str("#4a5a6a") ?? "#4a5a6a");
        Radio.Visible = true;
        var box = Box(col.Lightened(0.15f), 0.88f);
        Radio.AddThemeStyleboxOverride("panel", box);
        // the speaker's card from the game's portraits, else a framed initial
        var tex = HudArt.Atlas(c?["card"].Str(), 4) ?? HudArt.Atlas(c?["card"].Str());
        _portrait.Texture = tex;
        _initial.Text = tex == null ? System.Text.RegularExpressions.Regex.Replace(name, @"^\w+\.\s*", "").Substring(0, 1) : "";
        _radioName.Text = name;
        _radioName.AddThemeColorOverride("font_color", col.Lightened(0.35f));
        _radioText.Text = l.Text;
        Radio.ResetSize();
    }

    static string Esc(string s) => s.Replace("[", "[lb]");

    // ======================================================================================== the briefing
    /// <summary>the briefing before the mission starts (the game waits); Begin starts it</summary>
    public void ShowBriefing(Action begin)
    {
        var d = R.Def;
        G.SetSequelPause(true);
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(shade);
        _brief = shade;
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        shade.AddChild(center);
        var count = SequelCatalog.List(R.Kind).Count;
        var w = new PwWindow(R.Kind == "scenario" ? "Scenario" : "Containment", 640, 760) { Theme = PwTheme.Theme };
        center.AddChild(w);
        w.Body.AddChild(PwTheme.Para(R.Kind == "scenario" ? $"Scenario {R.Id} of {count} · playing as the {TribeInfo.Name(R.Tribe)}" : $"Mission {R.Id} of {count}", 12, PwTheme.Dim));
        w.Body.AddChild(PwTheme.Heading(d["title"].Str(), 24));
        var place = d["place"].Str() + (d["date"].Str() != "" ? " — " + d["date"].Str() : "");
        w.Body.AddChild(PwTheme.Para(place, 14, PwTheme.Hi));
        if (d["briefing"] is JsonArray br)
            foreach (var p in br)
            {
                var l = PwTheme.Para(p.Str(), 14, PwTheme.Text);
                l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                l.CustomMinimumSize = new Vector2(600, 0);
                w.Body.AddChild(l);
            }
        var news = R.Unlocks(true).Select(u => R.Classes.All.Get(u)?.Desc).Where(x => !string.IsNullOrEmpty(x)).ToList();
        if (news.Count > 0) w.Body.AddChild(PwTheme.Para("New: " + string.Join(", ", news), 14, PwTheme.Won));
        var c = new CenterContainer();
        c.AddChild(PwTheme.Btn("Begin", () => CloseBriefing(begin), 180));
        w.Body.AddChild(c);
    }

    void CloseBriefing(Action begin)
    {
        _brief?.QueueFree();
        _brief = null;
        G.SetSequelPause(false);
        begin();
    }
    public bool BriefingOpen => _brief != null;
}
