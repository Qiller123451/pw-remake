// The HUD top bar's places (game/src/UI/Hud/Logic/TopBarLayout.cs, docs/HUD.md "Layout and scale"): the original's
// numbers (IngameScreen.usl), and the plates' visible shapes - one top, one bottom, edge to edge - at every UI scale.
using System;
using PwRemake.UI.Hud.Logic;
using Box = PwRemake.UI.Hud.Logic.TopBarLayout.Box;

namespace PwRemake.Tests;

[TestClass]
public static class HudLayoutTests
{
    public static void OriginalPlaces(TestContext t)
    {
        t.Equal(235, TopBarLayout.Menu.X, "the menu button: 60 px left of the resource bar control");
        t.Equal(330, TopBarLayout.Bar.X, "the bar's picture centred in the 460 px control at 295");
        t.Equal(-4, TopBarLayout.Bar.Y, "the bar's picture centred in the 23 px high control");
        t.Equal(330, TopBarLayout.Menu.Right, "the menu button ends where the bar's picture begins");
        for (int i = 0; i < TopBarLayout.Cells; i++)
            t.Equal(TopBarLayout.RscX + 35 + 78 * i, TopBarLayout.Cell(i).X, $"counter {i}: CResourceProgressBar at 35 + 78 i in the control");
        t.Equal(TopBarLayout.Bar.Right, TopBarLayout.Cell(4).Right, "the five counters fill the picture");
        var all = TopBarLayout.RightButtons(true, true);
        t.Equal("quest,diplomacy,chat", string.Join(",", all.ConvertAll(b => b.Id)), "the order");
        t.Equal(720, all[0].Box.X, "quest: 35 px before the control's end");
        t.Equal(789, all[1].Box.X, "diplomacy after quest");
        t.Equal(880, all[2].Box.X, "chat after diplomacy");
        t.Equal(TopBarLayout.Bar.Right, all[0].Box.X, "the quest button begins where the bar's picture ends");
        t.Equal(960, TopBarLayout.Width(true, true), "the row's width");
    }

    public static void ShownButtonsFollowTheBar(TestContext t)
    {
        foreach (var (quest, dip) in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            var r = TopBarLayout.RightButtons(quest, dip);
            t.Equal((quest ? 1 : 0) + (dip ? 1 : 0) + 1, r.Count, $"quest {quest} diplomacy {dip}: the shown buttons");
            t.Equal(TopBarLayout.Bar.Right, r[0].Box.X, $"quest {quest} diplomacy {dip}: the first at the bar's end");
            for (int i = 1; i < r.Count; i++) t.Equal(r[i - 1].Box.Right, r[i].Box.X, $"quest {quest} diplomacy {dip}: {r[i].Id} after {r[i - 1].Id}");
            t.Equal("chat", r[^1].Id, "chat last");
        }
    }

    static Box Shape(string id) => id switch
    {
        "quest" => TopBarLayout.QuestShape, "diplomacy" => TopBarLayout.DiplomacyShape, _ => TopBarLayout.ChatShape,
    };

    public static void VisibleShapesJoin(TestContext t)
    {
        var menu = TopBarLayout.Visible(TopBarLayout.Menu, TopBarLayout.MenuShape);
        var bar = TopBarLayout.Visible(TopBarLayout.Bar, TopBarLayout.BarShape);
        t.Equal(0, bar.Y, "the bar's visible top at the screen's top");
        t.Equal(menu.Y, bar.Y, "the menu plate and the bar: one top");
        t.True(Math.Abs(menu.Bottom - bar.Bottom) <= 1, $"the menu plate and the bar: one bottom ({menu.Bottom} / {bar.Bottom})");
        t.Equal(bar.X, menu.Right, "the menu plate meets the bar");
        t.Equal(bar, TopBarLayout.BarSolid, "the bar takes the mouse where it is seen");
        var prev = bar;
        foreach (var (id, b) in TopBarLayout.RightButtons(true, true))
        {
            var v = TopBarLayout.Visible(b, Shape(id));
            t.Equal(bar.Y, v.Y, $"{id}: the bar's top");
            t.Equal(bar.Bottom, v.Bottom, $"{id}: the bar's bottom");
            t.Equal(prev.Right, v.X, $"{id}: meets the one before, no overlap, no gap");
            prev = v;
        }
    }

    // the HUD draws the row in one control scaled by S (and the quest button at the same transform): the edges that
    // meet in HUD pixels meet on the screen, at the automatic scales of the usual screens and the Options' choices
    public static void JoinsAtEveryScale(TestContext t)
    {
        foreach (var s in new[] { 1.0, 1.15, 1.25, 1.3846, 1.5, 1.6, 1.75, 2.0, 2.4, 3.0 })
        {
            double bx = TopBarLayout.Bar.Right * s;
            foreach (var (quest, dip) in new[] { (true, true), (true, false), (false, true), (false, false) })
            {
                var first = TopBarLayout.RightButtons(quest, dip)[0].Box;
                t.Near(bx, first.X * s, 1e-9, $"scale {s}: the first button at the bar's end");
            }
            t.Near(TopBarLayout.Bar.X * s, TopBarLayout.Menu.Right * s, 1e-9, $"scale {s}: menu / bar");
            t.Near(0, (TopBarLayout.Bar.Y + TopBarLayout.BarShape.Y) * s, 1e-9, $"scale {s}: the bar's visible top at 0");
        }
    }
}
