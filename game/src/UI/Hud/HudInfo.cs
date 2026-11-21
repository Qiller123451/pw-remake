// The info window at the bottom left (hud/infowin_bg_open.tga, 209 x 197; Info.usl CInfo): for one object its
// portrait card (64 x 61 at 33, 38) with its hit point bar in the card's bottom and "hp/max" under it (33, 101), the class
// icon (33, 7), the name beside it (51, 7), the owner's name (33, 19), the six values in the frame's rows (117, 34 + 17 i:
// attack, ranged attack, shield, armour, range, skulls; "-" for none - an object without any: passengers, gate, the load
// it carries), its buffs in the frame's five circles (31 + 17 i, 121), the item it carries (the slot at 167, 1), a
// building site's progress, the production queue with its progress (a click cancels an entry); for a group the members'
// cards with their hit points; for a resource its amount and gatherers; for an item its help text; for a healing well
// what it holds. Every part has its help item as tooltip (_iw_hitpoints, _iw_attack ..., the object's own).
//
// Ported from remake/src/ui/hud.js refreshInfo and the .info styles of remake/index.src.html (pw-exporter 98c7703).
using System;
using Godot;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;

namespace PwRemake.UI.Hud;

public partial class HudInfo : HudPanel
{
    public const int W = 209, H = 197;
    static readonly Color NAME = HudSprites.Hex("#f3d58c"), SUB = HudSprites.Hex("#d8c49a"), FRAME = HudSprites.Hex("#8a6a38"), DARK = HudSprites.Hex("#1b130c"),
        BAR_FRAME = HudSprites.Hex("#6d5430"), TEXT = HudSprites.Hex("#f0e2c0");
    InfoModel _m = new();
    string? _key;

    public HudInfo(Hud hud) : base(hud) { Name = "Info"; Size = new Vector2(W, H); }

    /// <summary>re-reads the selection (4 times a second, or at once after a selection change)</summary>
    public void Refresh(bool force = false)
    {
        var k = Hud.Model.InfoKey();
        if (!force && k == _key) return;
        _key = k;
        _m = Hud.Model.Info();
        QueueRedraw();
    }

    protected override void Paint()
    {
        var full = new Rect2(0, 0, W, H);
        if (!HudSprites.Pic(this, "hud_infowin_bg_open.png", full)) DrawRect(full, new Color(0.12f, 0.08f, 0.05f, 0.9f));
        Solid.Add(full);
        var m = _m; var D = Hud.Data;
        switch (m.Kind)
        {
            case "none": return;
            case "group": PaintGroup(m); break;
            case "item":
                Portrait(m.Portrait, 4, m.ObjName, null);
                Name_(m.Name, 33, 165);
                Wrapped(m.Hint, new Vector2(104, 38), 94, 11, SUB);
                break;
            case "well":
                Name_(m.Name, 33, 165);
                Bar(new Rect2(32, 136, 165, 8), m.MaxHp > 0 ? m.Hp / m.MaxHp : 0, $"{Math.Round(m.Hp)} / {Math.Round(m.MaxHp)}", null);
                break;
            case "res":
            {
                Name_(m.Name, 33, 165);
                // (.carry.big: 18, 44; 14 px, the 20 px icon)
                HudSprites.Draw(this, m.ResIcon, null, new Rect2(18, 44, 20, 20));
                HudSprites.Text(this, HudSprites.SerifBold, new Vector2(43, 59), m.Infinite ? "∞" : Sim.Rules.TT.JsString(m.Amount), m.Infinite ? 20 : 14, TEXT);
                AddHit(new Rect2(18, 44, 80, 20), "amount", () => Tooltips.ButtonTip(D, "_iw_resourceamount"));
                Wrapped(m.Hint, new Vector2(104, 38), 94, 11, SUB);
                break;
            }
            default: PaintObject(m); break;
        }
        // its buffs (Info.usl CBuffIcon: five 16 x 16 icons at 31 + 17 i, 121 - the frame's circles - with the buff's help as tooltip)
        for (int i = 0; i < Math.Min(5, m.Buffs.Count); i++)
        {
            var b = m.Buffs[i];
            var r = new Rect2(31 + 17 * i, 121, 16, 16);
            HudSprites.Draw(this, b.Icon, -1, r);
            var tip = b.Tip;
            AddHit(r, "buff" + i, () => Tooltips.ButtonTip(D, tip));
        }
    }

    void Name_(string s, float x, float w) => HudSprites.Text(this, HudSprites.SerifBold, new Vector2(x, 17), HudSprites.Fit(HudSprites.SerifBold, s, 13, w), 13, NAME);

    void Portrait(string? icon, int level, string? obj, Player? owner)
    {
        var r = new Rect2(33, 38, 64, 61);
        // (the card: its background, the picture, its clear frame - Info.usl "4, layer info_crdbg_std, layer <class>,
        // layer info_crdframe_clear")
        if (!HudSprites.Draw(this, "info_crdbg_std", level, r)) DrawRect(r, DARK);
        HudSprites.Draw(this, icon, level, r);
        HudSprites.Draw(this, "info_crdframe_clear", level, r);
        if (obj != null) AddHit(r, "portrait", () => Tooltips.ObjectTip(Hud.Data, obj, owner));
    }

    void Wrapped(string s, Vector2 pos, float w, int size, Color c)
    {
        if (string.IsNullOrEmpty(s)) return;
        var f = HudSprites.Serif;
        DrawMultilineString(f, pos + new Vector2(0, size + 1), s.Trim(), HorizontalAlignment.Left, w, size, 7, new Color(0, 0, 0, 0.8f));
        DrawMultilineString(f, pos + new Vector2(0, size), s.Trim(), HorizontalAlignment.Left, w, size, 7, c);
    }

    /// <summary>a bar with a frame and a centred text (.hp, .prog)</summary>
    void Bar(Rect2 r, double frac, string text, string? tipKey, bool gold = false, int fontSize = 8)
    {
        DrawRect(r.Grow(1), BAR_FRAME);
        DrawRect(r, DARK);
        float w = (float)(r.Size.X * Math.Clamp(frac, 0, 1));
        if (w > 0)
        {
            // (a vertical gradient: #9ce05a -> #4d9a1f, the construction's #f0d070 -> #a07820)
            Color top = gold ? HudSprites.Hex("#f0d070") : HudSprites.Hex("#9ce05a"), bot = gold ? HudSprites.Hex("#a07820") : HudSprites.Hex("#4d9a1f");
            var pts = new[] { r.Position, r.Position + new Vector2(w, 0), r.Position + new Vector2(w, r.Size.Y), r.Position + new Vector2(0, r.Size.Y) };
            DrawPolygon(pts, new[] { top, top, bot, bot });
        }
        HudSprites.Text(this, HudSprites.Serif, new Vector2(r.Position.X, r.Position.Y + r.Size.Y / 2 + fontSize * 0.36f), text, fontSize, Colors.White, HorizontalAlignment.Center, r.Size.X, 1);
        if (tipKey != null) AddHit(r.Grow(2), "bar_" + tipKey, () => Tooltips.ButtonTip(Hud.Data, tipKey));
    }

    static readonly Color VALUE = new(205 / 255f, 205 / 255f, 150 / 255f), NAME_C = new(1f, 1f, 200 / 255f);

    void PaintObject(InfoModel m)
    {
        var D = Hud.Data;
        // Info.usl CInfo: the class icon (16 x 19 at 33, 7), the name beside it (51, 7), the owner's name (33, 19)
        if (m.TypeIcon != null)
        {
            var tr = new Rect2(33, 8, 16, 16);
            HudSprites.Draw(this, m.TypeIcon, -1, tr);
            var tk = "_iw_ut_" + m.TypeIcon.Replace("tt_class_", "");
            AddHit(tr, "type", () => Tooltips.ButtonTip(D, tk));
        }
        HudSprites.Text(this, HudSprites.SerifBold, new Vector2(51, 18), HudSprites.Fit(HudSprites.SerifBold, m.Name, 12, m.Item != null ? 114 : 146), 12, NAME_C);
        if (m.Line.Length > 0) HudSprites.Text(this, HudSprites.Serif, new Vector2(33, 31), HudSprites.Fit(HudSprites.Serif, m.Line, 10, 165), 10, VALUE);
        // CUnitIcon: the card (64 x 61 at 33, 38) with the hit point bar in its bottom (the card's layers of 64 x 65:
        // HudHpBar, Info.usl 890-925), "hp/max" under it (33, 101)
        Portrait(m.Portrait, 4, m.ObjName, m.Owner);
        if (m.MaxHp > 0) CardHp(new Rect2(33, 38, 64, 65), m);
        if (m.MaxHp > 0)
        {
            var hr = new Rect2(33, 103, 64, 12);
            HudSprites.Text(this, HudSprites.Serif, new Vector2(33, 113), $"{Math.Ceiling(m.Hp)}/{Math.Round(m.MaxHp)}", 10, VALUE);
            AddHit(hr, "hp", () => Tooltips.ButtonTip(D, "_iw_hitpoints"));
        }
        // CAttribIcon: the values, one per row of the frame (117, 34 + 17 i): the 20 x 20 icon, the value 24 px right of it
        for (int i = 0; i < m.Stats.Count; i++)
        {
            var (icon, value, key) = m.Stats[i];
            var y = 34 + 17 * i;
            HudSprites.Draw(this, icon, null, new Rect2(117, y, 20, 20));
            HudSprites.Text(this, HudSprites.Serif, new Vector2(141, y + 14), HudSprites.Fit(HudSprites.Serif, value, 11, 58), 11, VALUE);
            m.StatLines.TryGetValue(i, out var lines);
            if (key != null) { var k = key; AddHit(new Rect2(117, y, 82, 17), "st" + i, () => Tooltips.ButtonTip(D, k, lines == null ? null : t => { foreach (var s in lines) t.Lines.Add((s, false)); })); }
        }
        // what a fighter carries: under the values (the original shows it on the worker; the remake keeps the number)
        if (m.Carry is { } cy)
        {
            HudSprites.Draw(this, cy.Icon, null, new Rect2(117, 144, 16, 16));
            HudSprites.Text(this, HudSprites.Serif, new Vector2(137, 157), cy.Amount, 11, VALUE);
        }
        // the item it carries (Info.usl m_pxItemslot)
        if (m.Item != null)
        {
            var r = new Rect2(167, 1, 20, 20);
            HudSprites.Draw(this, m.Item, 2, r);
            var it = m.Item;
            AddHit(r, "item", () => Tooltips.ObjectTip(D, it));
        }
        if (m.Progress != null)
            Bar(new Rect2(32, 146, 165, 12), m.Progress.Value, $"Under construction {Math.Round(m.Progress.Value * 100)}%", null, true, 9);
        // the production queue (CProdQueue at 33, 144: 31 x 30 cards 3 px apart, the part still to do darkened; a click
        // cancels the entry)
        for (int i = 0; i < m.Queue.Count; i++)
        {
            var (icon, done, idx) = m.Queue[i];
            var r = new Rect2(33 + i * 34, 144, 31, 30);
            if (r.End.X > W - 4) break;
            DrawRect(r.Grow(1), DARK);
            DrawRect(r.Grow(1), FRAME, false, 1);
            HudSprites.Draw(this, icon, 1, r);
            float dh = (float)((1 - done) * r.Size.Y);
            if (dh > 0) DrawRect(new Rect2(r.Position.X, r.End.Y - dh, r.Size.X, dh), new Color(0, 0, 0, 0.55f));
            var e = m.Entity!;
            int ii = idx;
            var h = AddHit(r, "q" + i);
            h.Click = (_, _, _) => Hud.Model.G.CancelQueueAt(e, ii);
        }
    }

    /// <summary>the card's hit point bar (Info.usl 906-925): with the option AlternativeColoringACHP
    /// (settings.cardHpHealth, the remake's default) "layer ovl_hpbar_bg, hslicehp ovl_hpbar hp%" - green .. yellow ..
    /// red by the health left, for every owner; off: "layer ovl_hpbar_bg, hslicebar ovl_hpbar 100 0 0 0 256, hslicebar
    /// ovl_hpbar hp% dark" - black under the owner's ACColors "dark" (HpBarRule)</summary>
    void CardHp(Rect2 r, InfoModel m)
    {
        int p = HpBarRule.Slice(m.Hp, m.MaxHp);
        if (PwRemake.App.GameApp.Settings.CardHpHealth) { HudHpBar.Draw(this, r, 4, p, null, null); return; }
        var e = m.Entity;
        int rgb = e?.Owner is { } o ? HpBarRule.OwnerRgb(o.ColorSlot, o.Color, false, 0)
            : HpBarRule.OwnerRgb(null, 0, e is ResNode, e != null && e.Cls == "ANML" ? (int)(e.Def?.Aggressive ?? 0) : 0);
        HudHpBar.Draw(this, r, 4, p, rgb, 0x000000);
    }

    void PaintGroup(InfoModel m)
    {
        // (.grp: 14, 14, 184 wide, 2 px gaps; .gi 29 x 31 + frame: the card's top left 27 x 26, a 3 px hit point bar)
        for (int i = 0; i < m.Group.Count; i++)
        {
            var (e, icon, hp) = m.Group[i];
            int col = i % 5, row = i / 5;
            var r = new Rect2(15 + col * 33, 15 + row * 35, 29, 31);
            DrawRect(r.Grow(1), BAR_FRAME);
            DrawRect(r, DARK);
            var en = UiAtlas_Entry(icon, 1);
            if (en != null)
            {
                var tex = PwRemake.UI.Mission.HudArt.Tex(en.Value.File);
                if (tex != null) DrawTextureRectRegion(tex, new Rect2(r.Position, new Vector2(27, 26)), new Rect2(en.Value.X, en.Value.Y, 27, 26));
            }
            // (the remake's own view - the original's info window shows one object: its bar in the cards' colour,
            // hslicehp's green .. yellow .. red, HpBarRule)
            int pg = e.MaxHp > 0 ? HpBarRule.Slice(e.Hp, e.MaxHp) : 100;
            DrawRect(new Rect2(r.Position.X, r.End.Y - 3, (float)(r.Size.X * Math.Clamp(hp, 0, 1)), 3), HudSprites.Rgb(HpBarRule.HealthRgb(pg)));
            var ent = e;
            var h = AddHit(r, "gi" + e.Id, () => Tooltips.ObjectTip(Hud.Data, ent.Name, ent.Owner));
            h.Click = (shift, _, _) => { if (shift) Hud.Model.G.Deselect(ent); else Hud.Model.G.Select(new[] { ent }); };
        }
        HudSprites.Text(this, HudSprites.Serif, new Vector2(16, H - 14), $"{m.GroupCount} selected", 11, SUB);
    }

    static PwRemake.UI.Mission.Logic.AtlasEntry? UiAtlas_Entry(string? n, int lv) => PwRemake.UI.Mission.Logic.UiAtlas.Entry(n, lv);
}
