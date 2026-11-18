// The top bar as IngameScreen.usl puts it (the places: Logic/TopBarLayout.cs): the group bar at x 11 (GroupBar.usl),
// the menu button at 235, the resource bar's picture at 330 (hud/resource_bar.tga, centred in the original's 460 x 23
// control at 295 - so 4 px above the screen's top: its visible part has the plates' top and bottom) with the original
// counters: units, skulls, food, wood, stone - light red when the storage is full, the income per minute under it with
// the option; then the quest (the mission UI's button, Hud.Layout), diplomacy (games with other humans or co-op seats)
// and chat buttons from 720 on, the shown ones plate after plate; the epoch line under the bar. Top right: the game's
// timers (the engine's ObjTime icons, hud_timers "timer_<icon>" 65 x 25: head quarter, warp gate, domination, defender).
//
// Ported from remake/src/ui/hud.js buildTop / buildTopButtons / refreshGroups / refreshTimers / update (pw-exporter 98c7703).
using System;
using Godot;
using PwRemake.UI.Hud.Logic;

namespace PwRemake.UI.Hud;

public partial class HudTopBar : HudPanel
{
    public const int H = 32;
    static readonly Color VALUE = HudSprites.Hex("#f7e7c3"), FULL = HudSprites.Hex("#ff9a6a"), INCOME = HudSprites.Hex("#a8e07a"),
        EPOCH = HudSprites.Hex("#f3d58c"), LABEL = HudSprites.Hex("#f3dca6");
    string? _pressed;

    public HudTopBar(Hud hud) : base(hud) { Size = new Vector2(TopBarLayout.Width(true, true), 60); Name = "TopBar"; }

    static Rect2 R(TopBarLayout.Box b) => new(b.X, b.Y, b.W, b.H);

    protected override void Paint()
    {
        var M = Hud.Model; var host = Hud.Host;
        // ---- the group bar: a button per assigned group and the next free one
        int gi = 0;
        foreach (var (k, shown, label, pressed) in M.Groups())
        {
            var r = new Rect2(TopBarLayout.GroupX + gi * TopBarLayout.GroupW, 0, TopBarLayout.GroupW, TopBarLayout.GroupH);
            gi++;
            if (!shown) continue;
            var id = "grp" + k;
            HudSprites.Pic(this, pressed || _pressed == id ? "hud_groupbutton_down.png" : IsHover(id) ? "hud_groupbutton_hover.png" : "hud_groupbutton.png", r);
            if (label.Length > 0) HudSprites.Text(this, HudSprites.SerifBold, new Vector2(r.Position.X, 17), label, 12, LABEL, HorizontalAlignment.Center, TopBarLayout.GroupW);
            int kk = k;
            var h = AddHit(r, id);
            h.Click = (_, _, _) => host.GroupButton(kk, "click");
            h.Right = (_, _, _) => host.GroupButton(kk, "assign");
            h.Double = (_, _, _) => host.GroupButton(kk, "double");
        }
        // ---- the resource bar: the picture centred in the original's control (4 px above the screen's top)
        {
            var bar = R(TopBarLayout.Bar);
            if (!HudSprites.Pic(this, "hud_resource_bar.png", bar)) DrawRect(R(TopBarLayout.BarSolid), new Color(0.1f, 0.07f, 0.04f, 0.85f));
            Solid.Add(R(TopBarLayout.BarSolid));
            int i = 0;
            foreach (var (key, value, extra, full) in M.Resources())
            {
                // (a counter's field of the picture: its icon, then the black field 18 .. 72 px of its 78 px; the number at
                // its right end, the income line under it)
                var cell = TopBarLayout.Cell(i++);
                float fx = cell.X, by = TopBarLayout.BarY;
                var col = full ? FULL : VALUE;
                float y = by + (extra.Length > 0 ? 15 : 21);
                HudSprites.Text(this, HudSprites.SerifBold, new Vector2(fx + 20, y), value, 13, col, HorizontalAlignment.Right, 48);
                if (extra.Length > 0) HudSprites.Text(this, HudSprites.Serif, new Vector2(fx + 20, by + 26), extra, 10, INCOME, HorizontalAlignment.Right, 48);
                var k = key;
                AddHit(new Rect2(cell.X, cell.Y, cell.W - 2, cell.H), "res_" + key, () => M.ResTip(k));
            }
        }
        // ---- the menu button (its plate's left edge is slanted: the label right of it); its right end meets the bar
        {
            var r = R(TopBarLayout.Menu);
            var face = _pressed == "menu" ? "_down" : IsHover("menu") ? "_hover" : "";
            if (!HudSprites.Pic(this, $"hud_menu_menubutton{face}.png", r)) HudSprites.Pic(this, "hud_menu_menubutton.png", r);
            HudSprites.Text(this, HudSprites.SerifBold, new Vector2(r.Position.X + 12, 15), Hud.Data.Ui("_UI_IngaScre_Button_Menu", "Menu"), 12, LABEL, HorizontalAlignment.Center, r.Size.X - 16);
            var h = AddHit(r, "menu", () => Tooltips.ButtonTip(Hud.Data, "_tb_menu"));
            h.Click = (_, _, _) => host.OpenMenu();
        }
        // ---- the epoch line
        HudSprites.Text(this, HudSprites.Serif, new Vector2(8, 34 + 12), M.EpochLine(), 12, EPOCH, HorizontalAlignment.Left, -1, 1);
        // ---- diplomacy and chat after the quest button (the mission UI's, Hud.Layout puts it at the bar's right end)
        // (IngameScreen.usl:327-358: the chat button's right click opens the history)
        foreach (var (id, b) in TopBarLayout.RightButtons(host.Mission != null, host.Multiplayer || host.CoopSeats))
        {
            if (id == "diplomacy") TopButton(id, b, "hud_menu_alliesbutton", Hud.Data.Ui("_UI_IngaScre_Button_Diplomacy", "Diplomacy"), "_UI_IngaScre_Button_Diplomacy_TT", () => Hud.ShowDiplomacy(), null);
            else if (id == "chat") TopButton(id, b, "hud_menu_chatbutton", Hud.Data.Ui("_UI_IngaScre_Button_Chat", "Chat"), "_UI_IngaScre_Button_Chat_TT", () => host.OpenChat(false), () => host.OpenChat(true));
        }
    }

    void TopButton(string id, TopBarLayout.Box b, string tex, string label, string tip, Action click, Action? right)
    {
        var r = R(b);
        float x = b.X, w = b.W;
        var face = _pressed == id ? "_down" : IsHover(id) ? "_hover" : "";
        if (!HudSprites.Pic(this, $"{tex}{face}.png", r)) HudSprites.Pic(this, tex + ".png", r);
        // (the chat plate's right edge is slanted)
        HudSprites.Text(this, HudSprites.SerifBold, new Vector2(x, 15), label, 12, LABEL, HorizontalAlignment.Center, id == "chat" ? w - 12 : w);
        var h = AddHit(r, id, () => new Tip { Title = Hud.Data.Ui(tip, label) });
        h.Click = (_, _, _) => click();
        if (right != null) h.Right = (_, _, _) => right();
    }

    protected override void OnLeftDown(InputEventMouseButton e, Hit? h) { _pressed = h?.Id; QueueRedraw(); }
    protected override bool OnLeftUp(InputEventMouseButton e, Hit? h) { _pressed = null; QueueRedraw(); return false; }
}

/// <summary>The game's timers top right (gameover.js goTimers): one pill under the other, the owner's colour.</summary>
public partial class HudTimers : HudPanel
{
    public HudTimers(Hud hud) : base(hud) { Name = "Timers"; Size = new Vector2(65, 200); }
    protected override void Paint()
    {
        int i = 0;
        foreach (var (icon, text, color, paused, tip) in Hud.Model.Timers())
        {
            var r = new Rect2(0, i * 28, 65, 25);
            i++;
            var mod = paused ? new Color(1, 1, 1, 0.55f) : Colors.White;
            if (!HudSprites.Draw(this, icon, -1, r, mod)) HudSprites.Draw(this, "timer_default", -1, r, mod);
            var col = color != null ? HudSprites.Rgb(color.Value) : HudSprites.Hex("#e0b978");
            HudSprites.Text(this, HudSprites.Sans, new Vector2(r.Position.X, r.Position.Y + 17), text, 13, paused ? col * new Color(1, 1, 1, 0.55f) : col, HorizontalAlignment.Right, 59);
            var t = tip;
            AddHit(r, "tm" + i, () => new Tip { Title = t });
        }
    }
}
