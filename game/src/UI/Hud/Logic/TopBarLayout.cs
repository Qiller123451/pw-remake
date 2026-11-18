// The top bar's places in HUD pixels (× the HUD scale on screen), as IngameScreen.usl and TopBar.usl lay it out - no
// Godot types, so the tests check it (tests/PwRemake.Tests/HudLayoutTests.cs).
//
// The original: the resource bar control (CRessourceBar) at x 295, 460 × 23, with hud/resource_bar.tga (390 × 32) as
// its bitmap - the engine draws a static control's bitmap centred in it, so the picture is at 295 + (460 - 390) / 2 =
// 330 and (23 - 32) / 2 = -4 (its top 4 rows, the end caps' tips, are above the screen); the five counters
// (CResourceProgressBar, 78 × 32) at 35 + 78 i in the control, i.e. 330 + 78 i, one per field of the picture. The menu
// button (95 × 32) at the control's x - 60 = 235 ends where the picture begins (330); the quest button (69 × 32) at the
// control's x + width - 35 = 720 begins where it ends; diplomacy (91) and chat (80) follow, plate after plate. All
// buttons at y 0.
//
// The pictures' visible shapes (their opaque pixels; the rest of each 32 px high texture is transparent):
//   menu_menubutton   x 5..94 (the left end slanted), y 0..22
//   resource_bar      x 0..389, y 4..25 (y 0..3: only the end caps' tips) - drawn at y -4: 0..21 on screen
//   menu_questbutton  x 0..68,  y 0..21 (its left edge hollowed round the bar's right cap)
//   menu_alliesbutton x 0..90,  y 0..21
//   menu_chatbutton   x 0..74,  y 0..21 (the right end slanted)
// so with the original's numbers the plates and the bar have one top (0) and one bottom (21) and join edge to edge.
// The remake drew the picture at the control's place (295, 0): 4 px lower than the plates, under the menu plate's
// right end, and the plates after it moved 35 px left onto its cap.
//
// The remake shows the quest button only in a campaign mission and diplomacy only in a game with other humans or
// co-op seats (the original had them all, disabled where they do nothing): the shown plates follow each other from
// the bar's right end.
using System.Collections.Generic;

namespace PwRemake.UI.Hud.Logic;

public static class TopBarLayout
{
    /// <summary>a place in HUD pixels</summary>
    public readonly record struct Box(int X, int Y, int W, int H)
    {
        public int Right => X + W;
        public int Bottom => Y + H;
    }

    /// <summary>the resource bar control of IngameScreen.usl (its place; the picture is centred in it)</summary>
    public const int RscX = 295, RscY = 0, RscW = 460, RscH = 23;
    /// <summary>hud/resource_bar.tga</summary>
    public const int BarPicW = 390, BarPicH = 32;
    /// <summary>the picture's place: centred in the control (330, -4)</summary>
    public const int BarX = RscX + (RscW - BarPicW) / 2, BarY = RscY + (RscH - BarPicH) / 2;
    /// <summary>a counter's cell (CResourceProgressBar: 78 × 32 at 35 + 78 i in the control = one field of the picture)</summary>
    public const int CellW = 78, Cells = 5;
    /// <summary>the buttons' height (iMenuButtonHeight) and widths (their pictures)</summary>
    public const int ButtonH = 32, MenuW = 95, QuestW = 69, DiplomacyW = 91, ChatW = 80;
    /// <summary>the menu button: 60 px left of the control (235)</summary>
    public const int MenuX = RscX - 60;
    /// <summary>the quest button: 35 px before the control's right end (720)</summary>
    public const int QuestX = RscX + RscW - 35;
    /// <summary>the group bar's buttons (GroupBar.usl): 20 × 26 from x 11</summary>
    public const int GroupX = 11, GroupW = 20, GroupH = 26;

    /// <summary>the opaque part of each picture in its own pixels (measured on the toolkit's PNGs of the .tga files)</summary>
    public static readonly Box MenuShape = new(5, 0, 90, 23), BarShape = new(0, 4, 390, 22), QuestShape = new(0, 0, 69, 22),
        DiplomacyShape = new(0, 0, 91, 22), ChatShape = new(0, 0, 75, 22);

    /// <summary>the resource bar's picture</summary>
    public static Box Bar => new(BarX, BarY, BarPicW, BarPicH);
    /// <summary>the part of the bar that takes the mouse (what is visible of it)</summary>
    public static Box BarSolid => new(BarX, BarY + BarShape.Y, BarPicW, BarShape.H);
    /// <summary>counter i (0 units, 1 skulls, 2 food, 3 wood, 4 stone): its field of the picture, from the screen's top
    /// to the bar's visible bottom</summary>
    public static Box Cell(int i) => new(BarX + i * CellW, 0, CellW, BarY + BarShape.Bottom);
    public static Box Menu => new(MenuX, 0, MenuW, ButtonH);

    /// <summary>the buttons right of the bar, the shown ones plate after plate from the quest button's place: "quest"
    /// (a campaign mission), "diplomacy" (other humans / co-op seats), "chat"</summary>
    public static List<(string Id, Box Box)> RightButtons(bool quest, bool diplomacy)
    {
        var r = new List<(string, Box)>();
        int x = QuestX;
        if (quest) { r.Add(("quest", new Box(x, 0, QuestW, ButtonH))); x += QuestW; }
        if (diplomacy) { r.Add(("diplomacy", new Box(x, 0, DiplomacyW, ButtonH))); x += DiplomacyW; }
        r.Add(("chat", new Box(x, 0, ChatW, ButtonH)));
        return r;
    }

    /// <summary>the width of the whole row (group bar to the chat button's end)</summary>
    public static int Width(bool quest, bool diplomacy) => RightButtons(quest, diplomacy)[^1].Box.Right;

    /// <summary>a picture's visible shape on screen when it is drawn into box (its texture's own size)</summary>
    public static Box Visible(Box box, Box shape) => new(box.X + shape.X, box.Y + shape.Y, shape.W, shape.H);
}
