// The army pyramid on the left (IngameScreen.usl: the tribe view at x 32, between the resource bar and the info
// window; NewPyramid.usl, PyramidCard.usl): hud/pyramid_open.tga with the player's units as cards in their level's
// places (the bigger cards higher up), their hit points, what they are doing (the task layers card_task_*), a mark when
// the skulls pay a level up, the units being trained greyed; the debug options' extra places beside the pyramid and the
// compact rows. Left of it the epoch bar (hud/pyrabutton_all: the levels open in this epoch, the next epoch's orange,
// the rest dark): a click opens / closes the pyramid (also Tab).
//
// Mouse (PyramidCard.usl OnClick, NewPyramid.usl): a click selects the unit (Shift adds, Ctrl adds or removes; buildings
// leave the selection), a double click centres the camera on it (Alt: every unit of its class on its row), a right
// click levels it up (Alt: the selection's right-click order on it); a card dragged onto a free place of a higher row
// levels it up to that row (the skulls of every level passed; never down, no swaps); a box dragged beside the cards
// selects the cards in it (without Shift the selection is cleared first).
//
// Ported from remake/src/ui/hud.js buildPyramid / togglePyramid / refreshEpochBar / refreshPyramid / cardClick /
// pyrBoxStart / cardDragStart (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;
using PwRemake.UI.Mission;

namespace PwRemake.UI.Hud;

public partial class HudPyramid : HudPanel
{
    public const int W = 32 + HudModel.PYR_W, H = HudModel.PYR_H + 2;
    const float PX = 32;          // the pyramid's x (right of the epoch bar)
    static readonly Color HEAD = HudSprites.Hex("#f3d58c"), SEL = HudSprites.Hex("#9cf06a"), UP = HudSprites.Hex("#ffd850");

    public bool Closed { get; private set; }
    List<PyrCard> _cards = new();
    List<(int Level, PyrRect R)> _free = new(), _compact = new();
    string? _key;
    (int Blend, int NoAvail) _ep;
    bool _epHover;
    // dragging a card / a box
    PyrCard? _dragCard;
    Vector2 _dragStart, _dragPos;
    bool _dragging;
    Vector2? _boxStart;
    Rect2 _box;

    public HudPyramid(Hud hud) : base(hud) { Name = "Pyramid"; Size = new Vector2(W, H); }

    /// <summary>opens (true) / closes (false) / toggles (null) the pyramid (CInGameScreen.TogglePyramidState)</summary>
    public void Toggle(bool? open = null)
    {
        bool want = open ?? Closed;
        if (want == !Closed) return;
        Closed = !want;
        Hud.Host.UiSound(Closed ? "ui_pyramid_close" : "ui_pyramid_open");
        _key = null;
        QueueRedraw();
    }

    public void Refresh()
    {
        var M = Hud.Model;
        var cards = M.PyramidCards(out var free, out var compact);
        var key = string.Join(",", M.RowN) + "|" + string.Join(",", cards.Select(c => (c.E?.Id ?? -1) + (c.Selected ? "s" : "") + Math.Round(c.Hp * 20) + ":" + c.Task + (c.CanLevelUp ? "^" : "") + c.Index)) + "|" + M.PyramidHead() + "|" + Closed;
        if (key == _key) return;
        _key = key;
        _cards = cards; _free = free; _compact = compact;
        QueueRedraw();
    }
    public void RefreshEpochBar() { var e = Hud.Model.EpochBar(); if (e != _ep) { _ep = e; QueueRedraw(); } }

    Rect2 CardRect(PyrRect r) => new(PX + r.X, r.Y, r.W, r.H);

    // ---- the panel behind the places. The original has no art of its own for a place: every place is a CPyrSlot whose
    // bitmap is the level's "slot_background" card layer (fully transparent), so what shows is pyramid_open.tga - the
    // translucent panel with a thin grey frame (64,64,64 at alpha 105) around each of its own places. More places
    // (a debug option's bigger pyramid: the compact rows, the places right of the pyramid) get the same look: the
    // picture without its frames (each frame pixel replaced by the panel of a row without frames), widened by repeating
    // a column of its inside (the separators run on, their knobs and the rounded edge move right) as far as the places
    // reach, and every place's frame drawn into it like the picture's own (X .. X+W, Y .. Y+H).
    ImageTexture? _panel;
    string? _panelKey;
    int _panelW = HudModel.PYR_W;
    static Image? s_clean;
    const int SPLIT = 150, REF_Y = 275, EDGE = HudModel.PYR_W - 168;   // the picture: a clean column, a row without frames, the margin right of its right-most frame
    static readonly Color FRAME = new(64 / 255f, 64 / 255f, 64 / 255f, 105 / 255f);

    void UpdatePanel()
    {
        var rects = Enumerable.Range(0, 5).SelectMany(lv => HudModel.SlotRects(lv, Hud.Model.RowN[lv], out _)).ToList();
        var key = string.Join(";", rects.Select(r => r.X + "," + r.Y + "," + r.W + "," + r.H));
        if (key == _panelKey) return;
        _panelKey = key;
        if (s_clean == null)
        {
            var src = HudArt.Tex("hud_pyramid_open.png")?.GetImage();
            if (src == null || src.GetWidth() != HudModel.PYR_W || src.GetHeight() != HudModel.PYR_H) { _panel = null; return; }
            s_clean = (Image)src.Duplicate();
            if (s_clean.IsCompressed()) s_clean.Decompress();
            s_clean.Convert(Image.Format.Rgba8);
            var f8 = FRAME.ToRgba32();
            for (int y = 0; y < HudModel.PYR_H; y++)
                for (int x = 0; x < HudModel.PYR_W; x++)
                    if (s_clean.GetPixel(x, y).ToRgba32() == f8) s_clean.SetPixel(x, y, s_clean.GetPixel(x, REF_Y));
        }
        int w = Math.Max(HudModel.PYR_W, rects.Max(r => r.X + r.W) + EDGE), right = HudModel.PYR_W - SPLIT;
        var img = Image.CreateEmpty(w, HudModel.PYR_H, false, Image.Format.Rgba8);
        img.BlitRect(s_clean, new Rect2I(0, 0, SPLIT, HudModel.PYR_H), Vector2I.Zero);
        for (int x = SPLIT; x < w - right; x++) img.BlitRect(s_clean, new Rect2I(SPLIT, 0, 1, HudModel.PYR_H), new Vector2I(x, 0));
        img.BlitRect(s_clean, new Rect2I(SPLIT, 0, right, HudModel.PYR_H), new Vector2I(w - right, 0));
        foreach (var r in rects)
        {
            for (int x = r.X; x <= r.X + r.W && x < w; x++) { Px(img, x, r.Y); Px(img, x, r.Y + r.H); }
            for (int y = r.Y; y <= r.Y + r.H; y++) { Px(img, r.X, y); Px(img, r.X + r.W, y); }
        }
        _panel = ImageTexture.CreateFromImage(img);
        _panelW = w;
        Size = new Vector2(Math.Max(W, PX + w), H);
    }
    static void Px(Image img, int x, int y) { if (x >= 0 && y >= 0 && x < img.GetWidth() && y < img.GetHeight()) img.SetPixel(x, y, FRAME); }

    protected override void Paint()
    {
        var M = Hud.Model;
        // ---- the epoch bar (four layers of hud_pyrabutton_all.png, 545 px)
        var eb = new Rect2(0, 2, 32, 545);
        var v = _epHover ? "_hover" : "";
        HudSprites.DrawTop(this, "pyramid_bg_avail" + v, 16, eb, 1);
        HudSprites.DrawTop(this, "pyramid_bg_nextage" + v, 16, eb, _ep.NoAvail / 100.0);
        HudSprites.DrawTop(this, "pyramid_bg_noavail" + v, 16, eb, _ep.Blend / 100.0);
        HudSprites.DrawTop(this, "pyramid_ovl_levelupable", 16, eb, 1);
        var bh = AddHit(eb, "epbar");
        bh.Click = (_, _, _) => Toggle();
        bh.Right = (_, _, _) => Toggle();
        // ---- the pyramid
        if (!Closed) UpdatePanel();
        var pr = new Rect2(PX, 0, Closed ? 11 : _panel != null ? _panelW : HudModel.PYR_W, HudModel.PYR_H);
        if (Closed || _panel == null) HudSprites.Pic(this, Closed ? "hud_pyramid_closed.png" : "hud_pyramid_open.png", pr);
        else DrawTextureRect(_panel, pr, false);
        Solid.Add(pr);
        if (Closed) return;
        HudSprites.Text(this, HudSprites.Serif, new Vector2(PX, -4), M.PyramidHead(), 12, HEAD, HorizontalAlignment.Center, HudModel.PYR_W);
        foreach (var c in _cards)
        {
            var r = CardRect(c.R);
            int lvIcon = HudModel.ICON_LVL[c.Level];
            var id = "card" + (c.E?.Id ?? -c.Index - 1000 * c.Level);
            bool hov = IsHover(id) && c.E != null;
            if (c.Queued != null) HudSprites.DrawGray(this, c.Icon, lvIcon, r, 1f, 0.7f, new Color(1, 1, 1, 0.45f));
            else HudSprites.Draw(this, c.Icon, lvIcon, r, hov ? new Color(1.3f, 1.3f, 1.3f) : null);
            if (c.E != null)
            {
                // hit points on every card, the card level's layers as the original's (MIRAGE PyramidCard.usl 391-433):
                // the option AlternativeColoringACHP (settings.cardHpHealth) "layer ovl_hpbar_bg, hslicehp ovl_hpbar
                // hp%" - green .. yellow .. red; off: the player's ACColors "counter" under the bar in its "dark"
                // (HudHpBar, HpBarRule)
                int pc = c.HpSlice;
                int slot = c.E.Owner?.ColorSlot ?? -1, counter = HpBarRule.CounterRgb(slot);
                if (PwRemake.App.GameApp.Settings.CardHpHealth || counter < 0) HudHpBar.Draw(this, r, lvIcon, pc, null, null);
                else HudHpBar.Draw(this, r, lvIcon, pc, HpBarRule.OwnerRgb(slot, c.E.Owner!.Color, false, 0), counter);
                if (c.Task != null && c.Task != "power") HudSprites.Draw(this, "card_task_" + c.Task, lvIcon, r);
                if (c.CanLevelUp)
                {
                    float x1 = r.End.X - 1, y0 = r.Position.Y + 1;
                    DrawColoredPolygon(new[] { new Vector2(x1 - 10, y0 + 7), new Vector2(x1 - 5, y0), new Vector2(x1, y0 + 7) }, new Color(0, 0, 0, 0.6f));
                    DrawColoredPolygon(new[] { new Vector2(x1 - 9.5f, y0 + 6.5f), new Vector2(x1 - 5, y0 + 0.5f), new Vector2(x1 - 0.5f, y0 + 6.5f) }, UP);
                }
                if (c.Selected) DrawRect(r.Grow(0), SEL, false, 2);
                var cc = c;
                var h = AddHit(r, id, () => Tooltips.ObjectTip(Hud.Data, cc.E!.Name, cc.E.Owner));
                // (the tooltip right of the whole pyramid, at the card's height)
                h.TipAnchor = new Rect2(PX, r.Position.Y, HudModel.PYR_W, r.Size.Y);
                h.Click = (s, ctl, _) => CardClick(cc.E!, s, ctl);
                h.Double = (_, _, alt) =>
                {
                    if (alt) Hud.Model.G.Select(_cards.Where(x => x.Level == cc.Level && x.E is { Alive: true } && x.E.Name == cc.E!.Name).Select(x => x.E!));
                    else Hud.Host.MoveCamera(cc.E!.Pos.X, cc.E.Pos.Z);
                };
                h.Right = (s, ctl, alt) =>
                {
                    if (alt) Hud.Host.OrderOn(cc.E!, s, ctl);
                    else if (cc.E is Unit u) Hud.Model.G.LevelUp(u);
                };
                h.Down = _ => { if (cc.E is Unit) { _dragCard = cc; _dragging = false; } };
            }
        }
        // ---- the dragged card and the selection box
        if (_dragging && _dragCard != null)
        {
            var g = new Rect2(_dragPos - new Vector2(14, 14), CardRect(_dragCard.R).Size);
            var t = DragTarget();
            bool bad = t != null && !t.Value.Ok;
            if (bad) HudSprites.DrawGray(this, _dragCard.Icon, HudModel.ICON_LVL[_dragCard.Level], g, 1f, 0.6f, new Color(1, 1, 1, 0.85f));
            else HudSprites.Draw(this, _dragCard.Icon, HudModel.ICON_LVL[_dragCard.Level], g, new Color(1, 1, 1, 0.85f));
            DrawRect(g, bad ? HudSprites.Hex("#ff5a3a") : HudSprites.Hex("#f3d58c"), false, 2);
            if (t != null && t.Value.Tip.Length > 0) HudSprites.Text(this, HudSprites.Serif, g.Position + new Vector2(g.Size.X + 6, 14), t.Value.Tip, 12, Colors.White, HorizontalAlignment.Left, -1, 1);
        }
        if (_boxStart != null)
        {
            DrawRect(_box, new Color(156 / 255f, 240 / 255f, 106 / 255f, 0.12f));
            DrawRect(_box, SEL, false, 1);
        }
    }

    /// <summary>a click on a card (PyramidCard.usl OnClick): buildings leave the selection; Shift adds the unit, Ctrl adds or
    /// removes it, a plain click selects it alone</summary>
    void CardClick(Entity u, bool shift, bool ctrl)
    {
        var host = Hud.Host;
        host.UiSound("ui_click_pyramid");
        if (!shift && !ctrl) { Hud.Model.G.Select(new[] { u }); return; }
        var sel = host.Selection.Where(x => !(x is Building && x.Name != "aje_floating_harbour")).ToList();
        if (shift) { if (!sel.Contains(u)) sel.Add(u); }
        else if (sel.Contains(u)) sel.Remove(u); else sel.Add(u);
        host.SetSelection(sel);
    }

    (bool Ok, string Tip, (int Lv, int I) Slot)? DragTarget()
    {
        if (_dragCard?.E is not Unit u) return null;
        var t = Hud.Model.PyramidSlotAt(_dragPos.X - PX, _dragPos.Y);
        if (t == null) return null;
        if (t.Value.Lv + 1 == u.Level) return (true, "", t.Value);
        var other = _cards.FirstOrDefault(c => c.Level == t.Value.Lv && c.Index == t.Value.I);
        var (ok, tip) = Hud.Model.DropCheck(u, t.Value, other?.E ?? (other?.Queued != null ? u : null));
        return (ok, tip, t.Value);
    }

    protected override void OnLeftDown(InputEventMouseButton e, Hit? h)
    {
        _dragStart = _dragPos = e.Position;
        if (h == null && !Closed && new Rect2(PX, 0, _panelW, HudModel.PYR_H).HasPoint(e.Position))
        {
            // a box beside the cards: without Shift the selection is cleared first
            if (!e.ShiftPressed) Hud.Host.SetSelection(Array.Empty<Entity>(), true);
            _boxStart = e.Position; _box = new Rect2(e.Position, Vector2.One);
        }
    }

    protected override void OnMotion(InputEventMouseMotion e)
    {
        if (_dragCard != null && (e.ButtonMask & MouseButtonMask.Left) != 0)
        {
            _dragPos = e.Position;
            if (!_dragging && _dragPos.DistanceTo(_dragStart) >= 6) { _dragging = true; Hud.HideTip(); }
            if (_dragging) QueueRedraw();
        }
        if (_boxStart != null)
        {
            var a = _boxStart.Value; var b = e.Position;
            _box = new Rect2(new Vector2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Vector2(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));
            QueueRedraw();
        }
    }

    protected override bool OnLeftUp(InputEventMouseButton e, Hit? h)
    {
        if (_boxStart != null)
        {
            _boxStart = null;
            var hit = _cards.Where(c => c.E is { Alive: true } && CardRect(c.R).Intersects(_box)).Select(c => c.E!).ToList();
            if (hit.Count > 0)
            {
                var sel = Hud.Host.Selection.ToList();
                foreach (var x in hit) if (!sel.Contains(x)) sel.Add(x);
                Hud.Host.SetSelection(sel);
            }
            QueueRedraw();
            return true;
        }
        var card = _dragCard; bool dragged = _dragging;
        _dragCard = null; _dragging = false;
        if (!dragged || card?.E is not Unit u) return false;
        _dragPos = e.Position;
        QueueRedraw();
        var t = Hud.Model.PyramidSlotAt(e.Position.X - PX, e.Position.Y);
        if (t == null || t.Value.Lv + 1 == u.Level) return true;
        var other = _cards.FirstOrDefault(c => c.Level == t.Value.Lv && c.Index == t.Value.I);
        if (other != null && other.E != u)
        {
            if (t.Value.Lv + 1 > u.Level) { Hud.Message("That slot is taken: drop the card on a free slot", "bad"); Hud.Host.ErrorSound(); }
            else if (t.Value.Lv + 1 < u.Level) Hud.Model.G.ChangeLevel(u, t.Value.Lv + 1);
            return true;
        }
        Hud.Model.G.ChangeLevel(u, t.Value.Lv + 1);
        return true;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion m)
        {
            bool over = new Rect2(0, 2, 32, 545).HasPoint(m.Position);
            if (over != _epHover) { _epHover = over; QueueRedraw(); }
        }
        base._GuiInput(e);
    }
}
