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
        // CommandBar.usl InitMenuCommands: the left spacer (7 px) first, the menu buttons, with two or more of them an
        // empty disabled menu button as the gap to the action buttons, the action buttons, the end piece
        float x = 0;
        int menus = _buttons.Count(b => b.Menu);
        if (_buttons.Count > 0) { HudSprites.Draw(this, "menubtn_back_leftspacer", null, new Rect2(0, 0, 7, 59)); Solid.Add(new Rect2(0, 0, 7, 59)); x = 7; }
        bool gap = false;
        foreach (var b in _buttons)
        {
            if (!b.Menu && !gap && menus >= 2)
            {
                var g = new Rect2(x, 0, 53, 59);
                if (!HudSprites.Draw(this, "menubtn_back_empty", null, g)) HudSprites.Draw(this, "menubtn_back_idle", null, g, new Color(0.6f, 0.6f, 0.6f));
                Solid.Add(g);
                x += 53;
            }
            if (!b.Menu) gap = true;
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
/// <remarks>
/// The original's layout (Flyout.usl CFlyout.UpdateCommands; MIRAGE the same): 37 x 36 cards, 4 px apart, from 8 + 37 + 4
/// px in; a row every 40 px from 9 px down; the row's category picture (actmenu_cat_UNIT / ECON / COMB / DEFE / UPGR /
/// UMGR ...: 37 x 36) at the left, none in the special menu; the window as wide as its longest row: (cards x 41) + 55,
/// as high as rows x 40 + 18. Its frame is hud/flyoutwin_*: the centre picture (410 x 40) is one row of the empty slot
/// squares, drawn under every row - so a shorter row shows the empty squares up to the window's width. The digits of
/// the armed row (CmdButton.ShowAccelerator) stand at the card's top right in (255, 223, 15).
/// </remarks>
public partial class HudFlyout : HudPanel
{
    static readonly Color BORDER_HOV = HudSprites.Hex("#f0d080"), DARK = HudSprites.Hex("#1b130c"), ON = HudSprites.Hex("#9cf06a"),
        DIGIT = new(1f, 223 / 255f, 15 / 255f), CD = HudSprites.Hex("#ffe7a0");
    const int M_TOP = 9, M_BOTTOM = 9, M_LEFT = 8, M_RIGHT = 10, GAP = 4, CW = 37, CH = 36, ROW_H = CH + GAP, X0 = M_LEFT + CW + GAP;

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
        cols = Math.Max(1, cols);
        return new Vector2(cols * (CW + GAP) + M_LEFT + M_RIGHT + CW, rows * ROW_H + M_TOP + M_BOTTOM);
    }

    /// <summary>the place of a card: column, row</summary>
    static Rect2 Card(int col, int row) => new(X0 + col * (CW + GAP), M_TOP + row * ROW_H, CW, CH);

    protected override void Paint()
    {
        Size = Measure();
        var full = new Rect2(Vector2.Zero, Size);
        int nrows = _slots?.Count ?? _rows.Count;
        Frame(full, nrows);
        Solid.Add(full);
        var M = Hud.Model; var D = Hud.Data;
        var sel = M.G.OwnSelection();
        if (_slots != null)
        {
            for (int ri = 0; ri < _slots.Count; ri++)
            {
                var (t, pax, cap) = _slots[ri];
                var tname = D.Text(t.Name).Name is { Length: > 0 } n ? n : t.Name;
                CategoryCell(ri, null, tname);
                for (int ci = 0; ci < pax.Count; ci++)
                {
                    var p = pax[ci];
                    var r = Card(ci, ri);
                    var id = "pax" + p.Id;
                    HudSprites.Draw(this, PwRemake.UI.Mission.Logic.UiAtlas.IconFor(p.Name), 2, r);
                    if (IsHover(id)) DrawRect(r, BORDER_HOV, false, 1);
                    Corner(r, Math.Max(1, p.Level).ToString());
                    var tt = t; var pp = p;
                    var h = AddHit(r, id, () =>
                    {
                        var tx = D.Text(pp.Name);
                        // CommandBar.usl:1162-1171: the passenger's name and "Level: N"
                        return new Tip { Title = tx.Name, Cls = tx.Cls, Body = D.Ui("_UI_CommandBar_Level", "Level: %1").Replace("%1", Math.Max(1, pp.Level).ToString()) };
                    });
                    h.Click = (_, _, _) => { Hud.HideTip(); M.G.Send(new UnloadCommand { Transport = tt.Id, Passenger = pp.Id }); };
                }
            }
            return;
        }
        for (int ri = 0; ri < _rows.Count; ri++)
        {
            var row = _rows[ri];
            bool armed = M.Armed != null && M.Armed.Value.Row == _rows.Count - 1 - ri;      // (rows count from the bottom)
            CategoryCell(ri, row.Cat, row.Title, armed);
            for (int ci = 0; ci < row.Cards.Count; ci++)
            {
                var a = row.Cards[ci];
                var r = Card(ci, ri);
                var id = "c" + ri + "_" + ci + a.Id;
                if (a.Stance != null)
                {
                    // the stance cards: the symbol, framed green when every selected unit has it
                    bool on = sel.OfType<Unit>().Any() && sel.OfType<Unit>().All(u => u.Stance == a.Stance);
                    DrawRect(r, DARK);
                    // (the stance symbols are the menu buttons' 53 x 59: kept upright in the card)
                    HudSprites.Draw(this, a.IconOverride, null, new Rect2(r.Position.X + 2, r.Position.Y, 32, CH));
                    if (on || IsHover(id)) DrawRect(r, on ? ON : BORDER_HOV, false, on ? 2 : 1);
                    var st = a.Stance.Value;
                    var hs = AddHit(r, id, () => Tooltips.ButtonTip(D, "_stance_" + st));
                    hs.Click = (_, _, _) => M.G.SetStance(sel, st);
                    if (armed && ci < 10) Digit(r, ci);
                    continue;
                }
                var why = M.G.Why(a, sel);
                var (locked, dim) = HudModel.CardState(a, why);
                var (icon, lv) = M.CardIcon(a);
                if (icon != null)
                {
                    // (the cards at the flyout's size: atlas level 2, 37 x 36; a symbol at its own)
                    int? l2 = lv == null ? null : 2;
                    if (locked) HudSprites.DrawGray(this, icon, l2, r, 0.5f, 1f);
                    else if (dim) HudSprites.DrawGray(this, icon, l2, r, 0.75f, 0.4f);
                    else HudSprites.Draw(this, icon, l2, r);
                }
                if (IsHover(id)) DrawRect(r, BORDER_HOV, false, 1);
                // a unit's card: the level it comes out at (menucmd_lvl<n>_ovl, the pyramid's level marks)
                var cl = M.CardLevel(a);
                if (cl != null) HudSprites.Draw(this, "menucmd_lvl" + cl + "_ovl", 2, r);
                if (why == "cooldown")
                {
                    DrawRect(r, new Color(0, 0, 0, 0.55f));
                    HudSprites.Text(this, HudSprites.SerifBold, new Vector2(r.Position.X, r.Position.Y + 23), Math.Ceiling(M.G.MoveCooldown(sel, a.A!)).ToString(), 13, CD, HorizontalAlignment.Center, CW);
                }
                int n = HudGame.QueueCount(sel.FirstOrDefault(), a.A!);
                if (n > 0) Corner(r, n.ToString());
                if (armed && ci < 10) Digit(r, ci);
                var aa = a;
                var h = AddHit(r, id, () => Tooltips.ActionTip(Hud.World, Hud.Host.Me, aa.A!, M.G.Why(aa, M.G.OwnSelection()), M.G.OwnSelection()));
                h.Click = (shift, _, _) => { M.G.UseAction(aa, M.G.OwnSelection(), shift); _key = null; };
                h.Right = (_, _, _) => { M.G.Unqueue(M.G.OwnSelection().FirstOrDefault(), aa.A!); _key = null; };
            }
        }
    }

    /// <summary>the row's category picture (Flyout.usl SetCategoryBitmap: actmenu_cat_*, brighter while the row is armed;
    /// none for a category without one, as the special menu's) with the row's name as its tooltip</summary>
    void CategoryCell(int ri, string? cat, string title, bool armed = false)
    {
        var r = new Rect2(M_LEFT, M_TOP + ri * ROW_H, CW, CH);
        var pic = CatPicture(cat);
        if (pic != null) HudSprites.Draw(this, pic, 2, r, armed ? new Color(1.25f, 1.25f, 1.25f) : (Color?)null);
        if (!string.IsNullOrEmpty(title)) AddHit(r, "cat" + ri, () => new Tip { Title = title });
    }

    /// <summary>Flyout.usl SetCategoryBitmap (MIRAGE's mapping: EXTE / RESE as UPGR, ENTY / CAVA as UNIT, CONV as UMGR,
    /// HERO as STAR)</summary>
    static string? CatPicture(string? cat)
    {
        if (string.IsNullOrEmpty(cat)) return null;
        var c = cat switch { "EXTE" or "RESE" => "UPGR", "ENTY" or "CAVA" => "UNIT", "CONV" => "UMGR", "HERO" => "STAR", _ => cat };
        var n = "actmenu_cat_" + c;
        return HudSprites.Has(n, 2) || HudSprites.Has(n) ? n : null;
    }

    void Corner(Rect2 r, string s) => HudSprites.Text(this, HudSprites.SerifBold, new Vector2(r.Position.X, r.End.Y - 2), s, 11, Colors.White, HorizontalAlignment.Right, r.Size.X - 1, 1);
    // (CmdButton: the accelerator's text box at 12, -10 in the card, 37 x 36, centred: its middle at the card's top right)
    void Digit(Rect2 r, int ci) => HudSprites.Text(this, HudSprites.SerifBold, new Vector2(r.Position.X + 24, r.Position.Y + 12), ((ci + 1) % 10).ToString(), 12, DIGIT, HorizontalAlignment.Center, 12, 1);

    /// <summary>the flyout window (hud/flyoutwin_*): the corners, the edges stretched, and the centre picture - a row of
    /// slot squares - under every row, cut at the window's right edge (a row longer than its 9 squares goes on with
    /// more of them)</summary>
    void Frame(Rect2 r, int rows)
    {
        Texture2D? T(string n) => PwRemake.UI.Mission.HudArt.Tex("hud_flyoutwin_" + n + ".png");
        var tl = T("topleft"); var tc = T("topcenter"); var tr = T("topright");
        var cl = T("centerleft"); var cc = T("centercenter"); var cr = T("centerright");
        var bl = T("bottomleft"); var bc = T("bottomcenter"); var br = T("bottomright");
        if (tl == null || tc == null || tr == null || cl == null || cc == null || cr == null || bl == null || bc == null || br == null)
        {
            DrawRect(r, HudSprites.Hex("#2a2015")); DrawRect(r, HudSprites.Hex("#7a5a2e"), false, 1);
            return;
        }
        float W = r.Size.X, H = r.Size.Y;
        const float L = 11, R = 11, TOP = 10, BOT = 12;
        DrawTextureRect(tl, new Rect2(0, 0, L, TOP), false);
        DrawTextureRect(tc, new Rect2(L, 0, W - L - R, TOP), false);
        DrawTextureRect(tr, new Rect2(W - R, 0, R, TOP), false);
        DrawTextureRect(cl, new Rect2(0, TOP, L, H - TOP - BOT), false);
        DrawTextureRect(cr, new Rect2(W - R, TOP, R, H - TOP - BOT), false);
        DrawTextureRect(bl, new Rect2(0, H - BOT, L, BOT), false);
        DrawTextureRect(bc, new Rect2(L, H - BOT, W - L - R, BOT), false);
        DrawTextureRect(br, new Rect2(W - R, H - BOT, R, BOT), false);
        // the centre: one 40 px strip of it per row (its squares 1 px inside the cards), cut at the bottom edge
        float cw = W - L - R, ch = H - TOP - BOT, sw = cc.GetWidth(), sh = cc.GetHeight();
        int strips = Math.Max(rows, (int)Math.Ceiling(ch / sh));
        for (int i = 0; i < strips; i++)
        {
            float y = TOP + i * sh, h = Math.Min(sh, TOP + ch - y);
            if (h <= 0) break;
            // its first 406 px, then the period of one square (x 37 .. 78) again and again
            float x = 0, first = Math.Min(Math.Min(406, sw), cw);
            DrawTextureRectRegion(cc, new Rect2(L, y, first, h), new Rect2(0, 0, first, h));
            x = first;
            while (x < cw && sw >= 78)
            {
                float w = Math.Min(41, cw - x);
                DrawTextureRectRegion(cc, new Rect2(L + x, y, w, h), new Rect2(37, 0, w, h));
                x += w;
            }
        }
    }
}
