// The keys and mouse of a game in one table (the browser remake's "Controls" window, ui/menu.js help): opened from the
// pause menu. The keys are the original's (Keys.srf, GameInputController.usl) and the remake's own (N, S, R, Ctrl+U,
// J, Home, Alt, F9, F10, Ctrl+Shift+R) as game/src/UI/Input/GameInput.cs and View/World/RtsCamera.cs have them.
// Ported from remake/src/ui/menu.js help (pw-exporter 98c7703).
using Godot;

namespace PwRemake.UI.Help;

public static class ControlsList
{
    public static readonly (string Keys, string What)[] Rows =
    {
        ("Left click / drag", "Select / box select (Shift adds - buildings too, Ctrl adds or removes) · double-click: all of that kind on screen (Ctrl: of that type, Shift: everything)"),
        ("F1", "The help book, at what is under the mouse, being placed or selected"),
        ("Right click", "Move, attack, gather, build, repair, deliver, heal (healers), enter a bunker / transport, set rally point"),
        ("Right drag", "Draw an arrow: the group lines up there facing the arrow, the longer the arrow the wider the ranks"),
        ("Shift / Ctrl + right click", "Add a waypoint · patrol between here and there"),
        ("Q · A (F) · Y", "Build menu (workers) or produce menu: top · middle · bottom row, then 1 – 0 for its buttons (with units selected A attacks: the middle row is on F)"),
        ("E · D · C · X", "Special moves: top · middle · bottom row · stances (then 1 hold, 2 defensive, 3 aggressive)"),
        ("A / V · B · N · S", "Attack · move · attack-move · stop"),
        ("G", "Place the last placed building again (a worker selected)"),
        ("H · T Z U I O P L K", "Headquarters · next HQ, barracks, farm, machine maker, harbour, temple, weapon smith, tavern"),
        ("Walls", "Click the start point, then the end point: a line of segments is placed"),
        ("Arrows · screen edge · wheel", "Scroll · zoom · PgUp/PgDn zoom · End reset"),
        ("Middle drag · Alt + left / middle + right drag", "Pan · turn the camera (Alt + right drag: pan)"),
        ("Army pyramid", "Drag a card onto a free place of a higher row to level the unit up (the skulls of every level on the way); right-click a card: one level up"),
        ("Ctrl+U · right click on a card", "Level up by one"),
        ("Ctrl+0–9 · 0–9", "Assign / select group (double tap centres)"),
        ("W · Shift+W · Ctrl+W · .", "Next idle worker · add one · all idle workers · next idle worker"),
        ("Space · Home", "Last alert · centre on selection"),
        ("R · Del · Shift+Del", "Rally point · destroy the first selected · destroy all selected"),
        ("Enter · Ctrl+Enter · Shift+Enter", "Chat to all · to the allies · to the enemies (\"AI1: food\" asks computer player 1; also help, wood, stone)"),
        ("J", "Quest log (campaign) · cutscenes: click or Space = next line, Esc = skip"),
        ("Ctrl+A · F2 · F3", "All your units on screen · camera follows the selected unit · free camera"),
        ("Ctrl+Shift+R", "Attack ranges of your towers, turrets, cannons and bunkers and the range markers of the selected objects shown / hidden (Options > Gameplay; the selected building and the one being placed show their range always)"),
        ("Numpad + − × ÷", "Game speed faster · slower · normal · pause"),
        ("Tab · Backspace", "Army pyramid open / closed · hide the interface"),
        ("Pause · F9 · F10 · F11 · F12 · Esc", "Pause · performance line · log window · fullscreen · screenshot · menu"),
    };

    /// <summary>the table into a window's body (its two columns this wide)</summary>
    public static void Fill(Container body, float keysWidth = 230, float whatWidth = 440)
    {
        var g = new GridContainer { Columns = 2 };
        g.AddThemeConstantOverride("h_separation", 14);
        g.AddThemeConstantOverride("v_separation", 3);
        foreach (var (k, w) in Rows)
        {
            g.AddChild(new Label { Text = k, CustomMinimumSize = new Vector2(keysWidth, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart, LabelSettings = new LabelSettings { Font = PwTheme.BodyFont, FontSize = 13, FontColor = PwTheme.Hi } });
            g.AddChild(new Label { Text = w, CustomMinimumSize = new Vector2(whatWidth, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart, LabelSettings = new LabelSettings { Font = PwTheme.BodyFont, FontSize = 13, FontColor = PwTheme.Text } });
        }
        body.AddChild(g);
    }
}
