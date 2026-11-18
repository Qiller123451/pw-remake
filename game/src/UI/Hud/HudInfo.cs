// The info window at the bottom left (hud/infowin_bg_open.tga, 209 x 197; Info.usl CInfo): for one object its
// portrait card (64 x 61 at 33, 38), name (33, 7) and level / owner line (33, 19), the hit points on the line under all,
// the values in the frame's six rows (117, 34 + 17 i: attack, range, armour, shield, passengers, gate, skulls, the load
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
                HudSprites.Text(this, HudSprites.SerifBold, new Vector2(43, 59), Sim.Rules.TT.JsString(m.Amount), 14, TEXT);
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
        DrawRect(r.Grow(1), DARK);
        DrawRect(r.Grow(1), FRAME, false, 1);
        HudSprites.Draw(this, icon, level, r);
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

    void PaintObject(InfoModel m)
    {
        var D = Hud.Data;
        Portrait(m.Portrait, 4, m.ObjName, m.Owner);
        Name_(m.Name, 33, m.Item != null ? 132 : 165);
        HudSprites.Text(this, HudSprites.Serif, new Vector2(33, 30), HudSprites.Fit(HudSprites.Serif, m.Line, 10, 165), 10, SUB);
        Bar(new Rect2(32, 136, 165, 8), m.MaxHp > 0 ? m.Hp / m.MaxHp : 0, $"{Math.Ceiling(m.Hp)} / {Math.Round(m.MaxHp)}", "_iw_hitpoints");
        // the values: one per row of the frame (117, 34 + 17 i), icon then value
        for (int i = 0; i < m.Stats.Count; i++)
        {
            var (icon, value, key) = m.Stats[i];
            var y = 34 + 17 * i;
            HudSprites.Draw(this, icon, null, new Rect2(119, y, 16, 16));
            HudSprites.Text(this, HudSprites.Serif, new Vector2(140, y + 12), HudSprites.Fit(HudSprites.Serif, value, 11, 58), 11, TEXT);
            m.StatLines.TryGetValue(i, out var lines);
            if (key != null) { var k = key; AddHit(new Rect2(119, y, 79, 17), "st" + i, () => Tooltips.ButtonTip(D, k, lines == null ? null : t => { foreach (var s in lines) t.Lines.Add((s, false)); })); }
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
            Bar(new Rect2(32, 118, 165, 12), m.Progress.Value, $"Under construction {Math.Round(m.Progress.Value * 100)}%", null, true, 9);
        // the production queue (.queue: 33, 146; 31 x 30 cards, the part still to do darkened; a click cancels the entry)
        for (int i = 0; i < m.Queue.Count; i++)
        {
            var (icon, done, idx) = m.Queue[i];
            var r = new Rect2(34 + i * 35, 147, 31, 30);
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
            var c = hp > 0.6 ? HudSprites.Hex("#77cc33") : hp > 0.3 ? HudSprites.Hex("#ddcc33") : HudSprites.Hex("#dd4422");
            DrawRect(new Rect2(r.Position.X, r.End.Y - 3, (float)(r.Size.X * Math.Clamp(hp, 0, 1)), 3), c);
            var ent = e;
            var h = AddHit(r, "gi" + e.Id, () => Tooltips.ObjectTip(Hud.Data, ent.Name, ent.Owner));
            h.Click = (shift, _, _) => { if (shift) Hud.Model.G.Deselect(ent); else Hud.Model.G.Select(new[] { ent }); };
        }
        HudSprites.Text(this, HudSprites.Serif, new Vector2(16, H - 14), $"{m.GroupCount} selected", 11, SUB);
    }

    static PwRemake.UI.Mission.Logic.AtlasEntry? UiAtlas_Entry(string? n, int lv) => PwRemake.UI.Mission.Logic.UiAtlas.Entry(n, lv);
}
