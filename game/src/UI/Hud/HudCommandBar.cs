// The command bar right of the info window (CommandBar.usl): the menu buttons (53 x 59: build, produce, special,
// stances, slots - hud/action_menubuttons with their symbols), the spacer, the action buttons (45 x 59: walk, attack,
// aggressive walk, stop, unload, repair, level up, rally point, kill) and the end piece; and the flyout above it with
// the open menu's rows (Flyout.usl: build / produce / upgrade / move cards in their menu positions, the epoch cards,
// the level a unit comes out at, the queue counts, cooldowns, the digits of the keyboard's armed row; grey what the
// requirements forbid, dimmed what can't be done now) or the passengers' menu.
//
// Ported from remake/src/ui/hud.js refreshCommands / refreshFlyout / slotsFlyout and the .cmdbar / .flyout styles of
// remake/index.src.html (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;

namespace PwRemake.UI.Hud;

public partial class HudCommandBar : HudPanel
{
    List<CmdButton> _buttons = new();
    string? _pressed;
    public float BarWidth { get; private set; }

    public HudCommandBar(Hud hud) : base(hud) { Name = "CommandBar"; Size = new Vector2(600, 59); }

    public void Refresh()
    {
        _buttons = Hud.Model.CommandBar();
        QueueRedraw();
    }

    protected override void Paint()
    {
        float x = 0;
        bool any = false, spacer = false;
        foreach (var b in _buttons)
        {
            if (!b.Menu && !spacer && any) { HudSprites.Draw(this, "menubtn_back_leftspacer", null, new Rect2(x, 0, 7, 59)); x += 7; spacer = true; }
            if (b.Menu) any = true;
            float w = b.Menu ? 53 : 45;
            var r = new Rect2(x, 0, w, 59);
            var id = b.Id + "@" + x;
            string back = b.Menu ? (b.On || _pressed == id ? "menubtn_back_down" : IsHover(id) ? "menubtn_back_hover" : "menubtn_back_idle")
                                 : (_pressed == id ? "actbtn_back_down" : IsHover(id) ? "actbtn_back_hover" : "actbtn_back_idle");
            HudSprites.Draw(this, back, null, r);
            HudSprites.Draw(this, b.Sym, null, r);
            var bb = b;
            var h = AddHit(r, id, () => bb.Tip());
            h.Click = (_, _, _) => { bb.Press(); Hud.RefreshCommands(); };
            x += w;
        }
        if (_buttons.Count > 0) { HudSprites.Draw(this, "actbtn_back_endpiece", null, new Rect2(x, 0, 45, 59)); Solid.Add(new Rect2(x, 0, 45, 59)); x += 45; }
        BarWidth = x;
    }

    protected override void OnLeftDown(InputEventMouseButton e, Hit? h) { _pressed = h?.Id; QueueRedraw(); }
    protected override bool OnLeftUp(InputEventMouseButton e, Hit? h) { _pressed = null; QueueRedraw(); return false; }
}

/// <summary>The flyout above the command bar: the open menu's rows of cards, or the passengers' menu.</summary>
public partial class HudFlyout : HudPanel
{
    static readonly Color TITLE = HudSprites.Hex("#e8cf98"), ARMED = HudSprites.Hex("#fff6c8"), BORDER = HudSprites.Hex("#a07a40"), BORDER_HOV = HudSprites.Hex("#f0d080"),
        EMPTY = HudSprites.Hex("#5a4630"), DARK = HudSprites.Hex("#1b130c"), ON = HudSprites.Hex("#9cf06a"), DIGIT = HudSprites.Hex("#ffe070"), CD = HudSprites.Hex("#ffe7a0");
    const int PAD_L = 10, PAD_T = 8, PAD_B = 6, TITLE_W = 64, GAP = 4, CW = 44, CH = 43, ROW_H = 49;

    List<FlyoutRow> _rows = new();
    List<(Entity T, List<Unit> Pax, int Cap)>? _slots;
    string? _key;

    public HudFlyout(Hud hud) : base(hud) { Name = "Flyout"; Visible = false; }

    /// <summary>re-reads the open menu (4 times a second); hidden without a menu</summary>
    public void Refresh(bool force = false)
    {
        var M = Hud.Model;
        if (M.Menu == null) { Visible = false; _key = null; return; }
        var sel = M.G.OwnSelection();
        string key;
        if (M.Menu == "slots")
        {
            _slots = M.SlotRows();
            key = "slots|" + string.Join("/", _slots.Select(t => t.T.Id + ":" + string.Join(",", t.Pax.Select(p => p.Id + "." + p.Level))));
        }
        else
        {
            _slots = null;
            _rows = M.MenuRows();
            key = M.Menu + "|" + M.Armed + "|" + string.Join("/", _rows.Select(g => string.Join(",", g.Cards.Select(a => a.Id + ":" + (a.Stance != null ? "" : (M.G.Why(a, sel) ?? "") + HudGame.QueueCount(sel.FirstOrDefault(), a.A!) + (M.G.Why(a, sel) == "cooldown" ? Math.Ceiling(M.G.MoveCooldown(sel, a.A!)).ToString() : "")))))) +
                "|" + string.Join(",", sel.OfType<Unit>().Select(u => u.Stance));
        }
        Visible = (_slots?.Count ?? _rows.Count) > 0;
        if (!force && key == _key) return;
        _key = key;
        QueueRedraw();
    }

    Vector2 Measure()
    {
        int rows, cols;
        if (_slots != null) { rows = _slots.Count; cols = _slots.Count == 0 ? 0 : _slots.Max(t => Math.Max(t.Cap, t.Pax.Count)); }
        else { rows = _rows.Count; cols = _rows.Count == 0 ? 0 : _rows.Max(r => r.Cards.Count); }
        float w = PAD_L + TITLE_W + GAP + cols * (CW + 2 + GAP) - GAP + PAD_L;
        return new Vector2(Math.Max(150, w), PAD_T + rows * ROW_H + PAD_B);
    }

    protected override void Paint()
    {
        Size = Measure();
        var full = new Rect2(Vector2.Zero, Size);
        Frame(full);
        Solid.Add(full);
        var M = Hud.Model; var D = Hud.Data;
        var sel = M.G.OwnSelection();
        float y = PAD_T + 2;
        if (_slots != null)
        {
            foreach (var (t, pax, cap) in _slots)
            {
                RowTitle(D.Text(t.Name).Name is { Length: > 0 } n ? n : t.Name, y, false);
                float x = PAD_L + TITLE_W + GAP;
                foreach (var p in pax)
                {
                    var r = new Rect2(x + 1, y + 1, CW, CH);
                    var id = "pax" + p.Id;
                    CardFrame(r, IsHover(id));
                    HudSprites.Draw(this, PwRemake.UI.Mission.Logic.UiAtlas.IconFor(p.Name), 3, r);
                    Corner(r, Math.Max(1, p.Level).ToString());
                    var tt = t; var pp = p;
                    var h = AddHit(r, id, () =>
                    {
                        var tx = D.Text(pp.Name);
                        // CommandBar.usl:1162-1171: the passenger's name and "Level: N"
                        return new Tip { Title = tx.Name, Cls = tx.Cls, Body = D.Ui("_UI_CommandBar_Level", "Level: %1").Replace("%1", Math.Max(1, pp.Level).ToString()) };
                    });
                    h.Click = (_, _, _) => { Hud.HideTip(); M.G.Send(new UnloadCommand { Transport = tt.Id, Passenger = pp.Id }); };
                    x += CW + 2 + GAP;
                }
                for (int i = pax.Count; i < cap; i++)
                {
                    var r = new Rect2(x + 1, y + 1, CW, 49);
                    DrawRect(r.Grow(1), EMPTY, false, 1);
                    HudSprites.Draw(this, "slot_background", null, r);
                    x += CW + 2 + GAP;
                }
                y += ROW_H;
            }
            return;
        }
        for (int ri = 0; ri < _rows.Count; ri++)
        {
            var row = _rows[ri];
            bool armed = M.Armed != null && M.Armed.Value.Row == _rows.Count - 1 - ri;      // (rows count from the bottom)
            RowTitle(row.Title, y, armed);
            float x = PAD_L + TITLE_W + GAP;
            for (int ci = 0; ci < row.Cards.Count; ci++)
            {
                var a = row.Cards[ci];
                var r = new Rect2(x + 1, y + 1, CW, CH);
                var id = "c" + ri + "_" + ci + a.Id;
                if (a.Stance != null)
                {
                    // the stance cards: the symbol 44 x 49, framed green when every selected unit has it
                    var rs = new Rect2(x + 1, y - 2, CW, 49);
                    bool on = sel.OfType<Unit>().Any() && sel.OfType<Unit>().All(u => u.Stance == a.Stance);
                    DrawRect(rs.Grow(1), DARK);
                    HudSprites.Draw(this, a.IconOverride, null, rs);
                    DrawRect(rs.Grow(1), on ? ON : IsHover(id) ? BORDER_HOV : BORDER, false, on ? 2 : 1);
                    var st = a.Stance.Value;
                    var hs = AddHit(rs, id, () => Tooltips.ButtonTip(D, "_stance_" + st));
                    hs.Click = (_, _, _) => M.G.SetStance(sel, st);
                    if (armed && ci < 10) Digit(rs, ci);
                    x += CW + 2 + GAP;
                    continue;
                }
                var why = M.G.Why(a, sel);
                var (locked, dim) = HudModel.CardState(a, why);
                CardFrame(r, IsHover(id));
                var (icon, lv) = M.CardIcon(a);
                if (icon != null)
                {
                    var ir = lv == null ? new Rect2(r.Position, new Vector2(CW, 49)) : r;
                    if (locked) HudSprites.DrawGray(this, icon, lv, ir, 0.5f, 1f);
                    else if (dim) HudSprites.DrawGray(this, icon, lv, ir, 0.75f, 0.4f);
                    else HudSprites.Draw(this, icon, lv, ir);
                }
                // a unit's card: the level it comes out at (menucmd_lvl<n>_ovl, the pyramid's level marks)
                var cl = M.CardLevel(a);
                if (cl != null) HudSprites.Draw(this, "menucmd_lvl" + cl + "_ovl", 2, r);
                if (why == "cooldown")
                {
                    DrawRect(r, new Color(0, 0, 0, 0.55f));
                    HudSprites.Text(this, HudSprites.SerifBold, new Vector2(r.Position.X, r.Position.Y + 27), Math.Ceiling(M.G.MoveCooldown(sel, a.A!)).ToString(), 15, CD, HorizontalAlignment.Center, CW);
                }
                int n = HudGame.QueueCount(sel.FirstOrDefault(), a.A!);
                if (n > 0) Corner(r, n.ToString());
                if (armed && ci < 10) Digit(r, ci);
                var aa = a;
                var h = AddHit(r, id, () => Tooltips.ActionTip(Hud.World, Hud.Host.Me, aa.A!, M.G.Why(aa, M.G.OwnSelection()), M.G.OwnSelection()));
                h.Click = (shift, _, _) => { M.G.UseAction(aa, M.G.OwnSelection(), shift); _key = null; };
                h.Right = (_, _, _) => { M.G.Unqueue(M.G.OwnSelection().FirstOrDefault(), aa.A!); _key = null; };
                x += CW + 2 + GAP;
            }
            y += ROW_H;
        }
    }

    void RowTitle(string t, float y, bool armed)
    {
        var f = HudSprites.Serif;
        var s = HudSprites.Fit(f, t, 11, TITLE_W);
        if (armed) { DrawString(f, new Vector2(PAD_L, y + 26), s, HorizontalAlignment.Left, -1, 11, new Color(0.94f, 0.69f, 0.25f, 0.7f)); }
        HudSprites.Text(this, f, new Vector2(PAD_L, y + 25), s, 11, armed ? ARMED : TITLE);
    }
    void CardFrame(Rect2 r, bool hover)
    {
        DrawRect(r.Grow(1), DARK);
        DrawRect(r.Grow(1), hover ? BORDER_HOV : BORDER, false, 1);
    }
    void Corner(Rect2 r, string s) => HudSprites.Text(this, HudSprites.SerifBold, new Vector2(r.Position.X, r.End.Y - 2), s, 12, Colors.White, HorizontalAlignment.Right, r.Size.X - 1, 1);
    void Digit(Rect2 r, int ci) => HudSprites.Text(this, HudSprites.SerifBold, new Vector2(r.Position.X + 2, r.Position.Y + 12), ((ci + 1) % 10).ToString(), 12, DIGIT, HorizontalAlignment.Left, -1, 1);

    /// <summary>the flyout window's frame (hud/flyoutwin_*: corners and edges, the centre stretched)</summary>
    void Frame(Rect2 r)
    {
        var box = HudSprites.FlyoutBox();
        if (box != null) DrawStyleBox(box, r);
        else { DrawRect(r, HudSprites.Hex("#2a2015")); DrawRect(r, HudSprites.Hex("#7a5a2e"), false, 1); }
    }
}
