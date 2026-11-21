// The in-game interface (the HUD): a CanvasLayer over the world with the original's layout (IngameScreen.usl) - the
// top bar (group bar, menu, resources, quest / diplomacy / chat buttons, the epoch line), the timers top right, the
// army pyramid with the epoch bar on the left, the info window and the command bar with its flyout at the bottom left,
// the minimap in the bottom right corner with the tooltip window above it, the message lines in the middle. All of
// it scales with the screen (AppSettings UiScale; 0 = automatic: 1 at 780 px of height up to 1.6, and on screens higher
// than 1440 px as a browser at 150 - 200 % zoom would, so 4K gets about 2.4).
//
// The game session makes one Hud per game, gives it an IHudHost, the map for the minimap and the world's events each
// frame (docs/HUD.md "What the session calls"). The HUD reads the world and the selection; everything it orders goes
// through the host as commands.
//
// Ported from remake/src/ui/hud.js (HUD: constructor, scale, update, message, showTip / hideTip) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;

namespace PwRemake.UI.Hud;

public partial class Hud : CanvasLayer, PwRemake.UI.PlayerInput.IGameHud
{
    static readonly Logger L = Log.For("UI.Hud");
    public readonly IHudHost Host;
    public readonly HudModel Model;
    public readonly NewsFeed News;

    readonly Control _root;
    readonly HudTopBar _top;
    readonly HudTimers _timers;
    readonly HudPyramid _pyramid;
    readonly HudInfo _info;
    readonly HudCommandBar _cmd;
    readonly HudFlyout _flyout;
    readonly HudMinimap _minimap;
    readonly HudTipWindow _tip;
    readonly HudMessages _msgs;
    /// <summary>the chat bar (Enter) and the message lines' history (the chat button's right click)</summary>
    public HudChat Chat { get; private set; } = null!;
    Control? _tipClosed;

    // ------------------------------------------------------------------ options (the JS Options menu; the session maps its settings onto them)
    /// <summary>the UI scale (AppSettings UiScale): 0 = automatic</summary>
    public double UiScale;
    /// <summary>tooltips next to what the mouse is over (true, the browser remake's default) or in the original's window
    /// above the minimap</summary>
    public bool TipNear = true;
    /// <summary>the scale in use</summary>
    public float S { get; private set; } = 1;

    double _hoverT, _epT;
    Vector2 _lastSize;
    /// <summary>lay the HUD out again on the next frame (UiScale changed)</summary>
    public void Relayout() => _lastSize = default;

    public Hud(IHudHost host)
    {
        Host = host;
        Layer = 10;          // (the session: HUD 10, mission UI 20, pause menu 30)
        Name = "Hud";
        Model = new HudModel(host);
        News = new NewsFeed(host);
        // the card name the tech tree gives a class: its "description" text key without the underscore (the Dragon Clan's
        // main building ninigi_fireplace has the card ninigi_fireplace_1 ...) - main.js setCardNames
        var rules = host.World.Data;
        PwRemake.UI.Mission.Logic.UiAtlas.CardName = name =>
        {
            var d = rules.Exists(name) ? rules.Def(name, null) : null;
            return d != null && !string.IsNullOrEmpty(d.Desc) ? (d.Desc[0] == '_' ? d.Desc[1..] : d.Desc) : null;
        };
        Model.G.Message = Message;
        News.Message = Message;
        News.Marker = (type, x, z) => _minimap?.Model?.AddMarker(type, x, z);
        News.Lost = e => { if (Host.Selection.Contains(e)) Host.SetSelection(Host.Selection.Where(x => x != e).ToList(), true); };
        _root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        _top = new HudTopBar(this);
        _timers = new HudTimers(this);
        _pyramid = new HudPyramid(this);
        _info = new HudInfo(this);
        _cmd = new HudCommandBar(this);
        _flyout = new HudFlyout(this);
        _minimap = new HudMinimap(this);
        _msgs = new HudMessages(this);
        _tip = new HudTipWindow();
        foreach (var c in new Control[] { _pyramid, _top, _timers, _info, _cmd, _minimap, _flyout, _msgs, _tip }) _root.AddChild(c);
        Chat = new HudChat(this);                     // the chat bar and the history of the message lines (HudChat.cs)
        _root.AddChild(Chat);
        _tipClosed = new TextureRect { Name = "TipClosed", Texture = PwRemake.UI.Mission.HudArt.Tex("hud_tooltip_closed.png"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(38, 27) };
        _root.AddChild(_tipClosed);
    }

    public World World => Host.World;
    public Rules Data => Host.World.Data;
    public MinimapModel? Minimap => _minimap.Model;
    public HudPyramid Pyramid => _pyramid;

    // ------------------------------------------------------------------ what the session calls
    /// <summary>the minimap's map and the setting's colour table (main.js: new Minimap(...), buildBackground); the table:
    /// MinimapModel.LoadTable(assets/terrain/&lt;Setting&gt;/setting.json)</summary>
    public void SetMap(MinimapSource src, int[][][]? table) => _minimap.Init(src, table);

    /// <summary>the world's events of this frame (World.DrainEvents): messages, minimap markers, the selection</summary>
    public void OnEvents(IEnumerable<WorldEvent> events) => News.Handle(events);

    /// <summary>the selection changed (G.selChanged): the info window and the command bar follow at once</summary>
    /// <summary>silent: by itself (a death, a unit lost, a game loaded), not by the player - the menu stays as it is</summary>
    public void SelectionChanged(bool silent = false)
    {
        _hoverT = 0;
        Model.SelectionChanged(silent);
    }

    /// <summary>a message line in the middle of the screen (hud.message): kind "" | good | bad | warn | chat | "chat:rrggbb"
    /// (a chat line "Name: text", the name in its player's colour: ChatKind)</summary>
    public void Message(string text, string kind = "") => _msgs.Add(text, kind);
    /// <summary>the kind of a chat line whose sender is player p (its name drawn in the player's light colour)</summary>
    public static string ChatKind(PwRemake.Sim.World.Player? p) => p == null ? "chat" : "chat:" + ((p.PartyColor ?? p.Color) & 0xffffff).ToString("x6");
    /// <summary>a chat kind's name colour (null: none)</summary>
    public static Color? ChatNameColor(string kind) => kind.StartsWith("chat:") && kind.Length == 11 ? HudSprites.Hex("#" + kind[5..]) : null;

    /// <summary>the minimap's ping (MiniMap2.usl: CGameWrap.SendMapPing; the player and the allies see it)</summary>
    public void MapPing(double x, double z)
    {
        var m = _minimap.Model;
        if (m?.Table != null) m.AddMarker("Ping", x, z, m.OwnerColor(Host.Me, 2, null));
        Host.UiSound("ui_minimap_ping_delivered");
    }

    // (the members of the session's IGameHud - game/src/UI/Input/IGameHud.cs on the session's branch - have its
    // signatures: after the merge `public partial class Hud : CanvasLayer, IGameHud` is all it takes)
    /// <summary>a command bar row is armed: the digits press its buttons</summary>
    public bool Armed => Model.Armed != null;
    /// <summary>keyboard (Keys.srf CmdBar_Accel_*): a menu (build | produce | stances | special) and its row counted from
    /// the bottom; then PressArmed(0..9). false: nothing there</summary>
    public bool ArmRow(string kind, int row) { var ok = Model.ArmRow(kind, row); RefreshCommands(); return ok; }
    /// <summary>presses button index (0..9) of the armed row</summary>
    public void PressArmed(int index) => PressArmedRow(index, Input.IsKeyPressed(Key.Shift));
    public bool PressArmedRow(int n, bool shift) { var ok = Model.PressArmed(n, shift); RefreshCommands(); return ok; }
    public void Disarm() { Model.Disarm(); RefreshCommands(); }
    /// <summary>Tab: the pyramid open / closed</summary>
    public void TogglePyramid() => _pyramid.Toggle();
    public void SetPyramidOpen(bool open) => _pyramid.Toggle(open);
    /// <summary>panels of a mode's own interface beside the HUD (the sequel's: SequelHud) - no picking under them</summary>
    public readonly List<Control> Overlays = new();
    /// <summary>a mode's own minimap markers (the sequel's: x, z, colour) - drawn as pulsing rings</summary>
    public Func<IEnumerable<(double X, double Z, int Color)>>? MinimapExtra { get => _minimap.Extra; set => _minimap.Extra = value; }
    /// <summary>the screen point is over a HUD panel (no picking in the world, no edge scrolling there)</summary>
    public bool IsOverHud(Vector2 screen)
    {
        if (!Visible) return false;
        foreach (var c in new Control[] { _top, _timers, _pyramid, _info, _cmd, _flyout, _minimap })
            if (c.Visible && c._HasPoint(c.GetGlobalTransformWithCanvas().AffineInverse() * screen)) return true;
        foreach (var c in Overlays)
            if (GodotObject.IsInstanceValid(c) && c.IsVisibleInTree() && c.GetGlobalRect().HasPoint(screen)) return true;
        return false;
    }
    /// <summary>the statistics screen (the end screen's "View Statistics"); back: when it closes</summary>
    public void ShowStats(Action? back = null, IEnumerable<Sim.Scripting.QuestState>? quests = null) => HudStatsWindow.Open(this, quests, back);
    /// <summary>the diplomacy window (the top bar's button in skirmish games)</summary>
    public void ShowDiplomacy() => HudDiplomacyWindow.Open(this);

    // ------------------------------------------------------------------ tooltips
    /// <summary>a tooltip for a thing at a screen rectangle (null: the original's window above the minimap)</summary>
    public void ShowTip(Tip t, Rect2? at)
    {
        _tip.Show(t);
        _tip.Scale = new Vector2(S, S);
        if (TipNear && at != null) Float(at.Value);
        else Dock();
        _tipClosed!.Visible = TipNear && at != null;
    }
    public void HideTip() { _tip.HideTip(); if (_tipClosed != null) _tipClosed.Visible = true; }
    // the world's tooltip (a question mark under the mouse or picked: GameInput.Marks); a panel's tooltip replaces it
    Tip? _worldTip;
    public void WorldTip(string? title, string? text, Rect2? at)
    {
        if (text == null) { if (_worldTip != null && _tip.Current == _worldTip) HideTip(); _worldTip = null; return; }
        _worldTip = new Tip { Title = title, Body = text };
        ShowTip(_worldTip, at);
    }
    public Tip? CurrentTip => _tip.Current;

    // the original's place: its bottom left corner 35 px right of the minimap's left edge, 241 px above the corner's bottom
    void Dock()
    {
        var mm = _minimap.Position;
        _tip.Position = new Vector2(mm.X + 35 * S, mm.Y + (HudMinimap.H - 241) * S - _tip.Size.Y * S);
    }

    // next to the element: below the top bar's things, above the bottom panels', right of what is on the left half of the
    // screen (the army pyramid), left of what is on the right half; always on screen
    void Float(Rect2 r)
    {
        var vp = _root.Size;
        float g = 6, w = _tip.Size.X * S, h = _tip.Size.Y * S, x, y;
        float cx = r.Position.X + r.Size.X / 2, cy = r.Position.Y + r.Size.Y / 2;
        if (r.End.Y < 80 * S) { x = cx - w / 2; y = r.End.Y + g; }
        else if (r.Position.Y > vp.Y - 215 * S) { x = cx - w / 2; y = r.Position.Y - g - h; }
        else if (cx < vp.X / 2) { x = r.End.X + g; y = cy - h / 2; }
        else { x = r.Position.X - g - w; y = cy - h / 2; }
        _tip.Position = new Vector2(Mathf.Round(Math.Max(4, Math.Min(vp.X - w - 4, x))), Mathf.Round(Math.Max(4, Math.Min(vp.Y - h - 4, y))));
    }

    // ------------------------------------------------------------------ layout (hud.js scale)
    float AutoScale(float vh)
    {
        if (UiScale > 0) return (float)UiScale;
        float s = Math.Max(1, Math.Min(1.6f, vh / 780f));
        return vh > 1440 ? s * vh / 1440f : s;
    }

    void Layout()
    {
        var vp = _root.Size;
        _lastSize = vp;
        float s = S = AutoScale(vp.Y);
        var sv = new Vector2(s, s);
        _top.Scale = sv; _top.Position = Vector2.Zero;
        _timers.Scale = sv; _timers.Position = new Vector2(vp.X - (65 + 5) * s, 5 * s);
        // the army pyramid on the left (IngameScreen.usl: the tribe view at x 32), between the resource bar and the info window
        float ps = Math.Min(s, (vp.Y - 74 - 205 * s) / 560f);
        _pyramid.Scale = new Vector2(ps, ps); _pyramid.Position = new Vector2(0, 74);
        // the bottom left: the info window, the command bar right of it (4 px in, 4 px up)
        _info.Scale = sv; _info.Position = new Vector2(0, vp.Y - HudInfo.H * s);
        _cmd.Scale = sv; _cmd.Position = new Vector2(HudInfo.W * s, vp.Y - 59 * s);      // (CommandBar.usl POS_X 209, the screen's bottom - 59)
        _minimap.Scale = sv; _minimap.Position = new Vector2(vp.X - (HudMinimap.W + 1) * s, vp.Y - (HudMinimap.H + 1) * s);
        _flyout.Scale = sv;
        _msgs.Scale = sv; _msgs.Position = new Vector2(vp.X / 2, 44 * s);
        if (_tipClosed != null)
        {
            _tipClosed.Scale = sv;
            _tipClosed.Position = _minimap.Position + new Vector2(232 * s, (HudMinimap.H - 251 - 27) * s);
        }
        // the mission UI follows the scale; its quest button goes into the top bar where the resource bar's picture ends
        // (TopBarLayout.RightButtons: the original's 720; the top bar's own transform - at 0, 0 scaled by s - so it
        // lines up with the bar and the plates after it at any scale)
        if (Host.Mission is { } mp)
        {
            mp.UiScale = s;
            var q = mp.QuestButtonControl;
            var qb = TopBarLayout.RightButtons(true, false)[0].Box;
            q.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            q.Scale = sv;
            q.Position = _top.Position + new Vector2(qb.X, qb.Y) * s;
            q.Size = new Vector2(qb.W, qb.H);
            // the objectives (info bar, timers) hang under the top bar's right end: the last button's (Chat) right edge
            var last = TopBarLayout.RightButtons(true, Host.Multiplayer || Host.CoopSeats)[^1].Box;
            mp.ObjectivesAnchor = _top.Position + new Vector2(last.Right, last.Bottom) * s;
        }
        PlaceFlyout();
    }

    void PlaceFlyout()
    {
        // right above the command bar, 3 px right of its left edge (Flyout.usl: x 212, the screen's bottom - (59 + height))
        _flyout.Position = new Vector2(_cmd.Position.X + 3 * S, _cmd.Position.Y - _flyout.Size.Y * S);
    }

    // ------------------------------------------------------------------ per frame
    public void RefreshCommands()
    {
        _cmd.Refresh();
        _flyout.Refresh(true);
        _flyout.QueueRedraw();
        PlaceFlyout();
    }

    public override void _Process(double delta)
    {
        var P = PwRemake.View.PerfProbe.P;
        long t0 = P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0, b0 = P != null ? GC.GetAllocatedBytesForCurrentThread() : 0;
        Process(delta);
        P?.Add("hud:process", System.Diagnostics.Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - b0);
    }
    void Process(double delta)
    {
        var P = PwRemake.View.PerfProbe.P;
        long t0 = P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (_root.Size != _lastSize) Layout();
        Model.G.Poll();
        if (P != null) { long t = System.Diagnostics.Stopwatch.GetTimestamp(); P.Add("hud:poll", t - t0, 0); t0 = t; }
        _top.QueueRedraw();
        _msgs.Tick(delta);
        _minimap.Tick(delta);
        if (P != null) { long t = System.Diagnostics.Stopwatch.GetTimestamp(); P.Add("hud:minimap tick", t - t0, 0); t0 = t; }
        _hoverT -= delta;
        if (_hoverT <= 0)
        {
            _hoverT = 0.25;
            _info.Refresh();
            _pyramid.Refresh();
            _cmd.Refresh();
            _flyout.Refresh();
            _timers.QueueRedraw();
            _epT -= 0.25;
            if (_epT <= 0) { _epT = 1; _pyramid.RefreshEpochBar(); }
            // the tooltip under the mouse follows the game (costs that became affordable ...)
            if (_tip.Shown) _tip.QueueRedraw();
            if (P != null) { long t = System.Diagnostics.Stopwatch.GetTimestamp(); P.Add("hud:refresh (4/s)", t - t0, 0); t0 = t; }
        }
        PlaceFlyout();
    }

    /// <summary>everything at once (tests, screenshots)</summary>
    public void RefreshAll()
    {
        Layout();
        _info.Refresh(true);
        _pyramid.Refresh();
        _pyramid.RefreshEpochBar();
        RefreshCommands();
        _timers.QueueRedraw();
        _top.QueueRedraw();
        _minimap.Tick(1);
    }
}

/// <summary>The message lines in the middle of the screen (hud.message): at most 4, each fades after 3.5 s.</summary>
public partial class HudMessages : Control
{
    readonly Hud _hud;
    readonly List<(string Text, string Kind, double T)> _lines = new();
    public HudMessages(Hud hud) { _hud = hud; Name = "Messages"; MouseFilter = MouseFilterEnum.Ignore; }

    public void Add(string text, string kind)
    {
        _hud.Chat?.Log(text, kind);
        _lines.Add((text, kind, 0));
        while (_lines.Count > 4) _lines.RemoveAt(0);
        QueueRedraw();
    }
    public IReadOnlyList<(string Text, string Kind, double T)> Lines => _lines;

    public void Tick(double dt)
    {
        if (_lines.Count == 0) return;
        for (int i = 0; i < _lines.Count; i++) _lines[i] = (_lines[i].Text, _lines[i].Kind, _lines[i].T + dt);
        _lines.RemoveAll(l => l.T >= 4.5);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var f = HudSprites.Serif;
        float y = 0;
        foreach (var (text, kind, t) in _lines)
        {
            // (.msg: 14 px, padding 3 px 50 px, a background fading out to the sides; .fade after 3.5 s: opacity in 0.9 s)
            float a = t < 3.5 ? 1 : (float)Math.Max(0, 1 - (t - 3.5) / 0.9);
            var sz = f.GetStringSize(text, HorizontalAlignment.Left, -1, 14);
            float w = sz.X + 100, h = 22, x = -w / 2;
            var bg = new Color(30 / 255f, 20 / 255f, 10 / 255f, 0.78f * a); var clear = new Color(bg, 0);
            float e = w * 0.15f;
            DrawPolygon(new[] { new Vector2(x, y), new Vector2(x + e, y), new Vector2(x + e, y + h), new Vector2(x, y + h) }, new[] { clear, bg, bg, clear });
            DrawRect(new Rect2(x + e, y, w - 2 * e, h), bg);
            DrawPolygon(new[] { new Vector2(x + w - e, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x + w - e, y + h) }, new[] { bg, clear, clear, bg });
            var col = HudSprites.Hex(kind == "bad" ? "#ffb090" : kind == "good" ? "#c8f0a0" : kind == "warn" ? "#ffd070" : "#f0dcae");
            int colon = text.IndexOf(": ", StringComparison.Ordinal);
            if (Hud.ChatNameColor(kind) is { } nc && colon > 0)
            {
                // a chat line: the sender's name in the player's colour
                string name = text[..(colon + 1)];
                HudSprites.Text(this, f, new Vector2(x + 50, y + 16), name, 14, new Color(nc, a), HorizontalAlignment.Left, -1, 0.9f * a);
                HudSprites.Text(this, f, new Vector2(x + 50 + f.GetStringSize(name + " ", HorizontalAlignment.Left, -1, 14).X, y + 16), text[(colon + 2)..], 14, new Color(col, a), HorizontalAlignment.Left, -1, 0.9f * a);
            }
            else HudSprites.Text(this, f, new Vector2(x + 50, y + 16), text, 14, new Color(col, a), HorizontalAlignment.Left, -1, 0.9f * a);
            y += h + 3;
        }
    }
}
