// What the game's input and session need of the HUD (main.js G.hud.*): the command bar's armed rows (the original's
// accelerators Q/A/Y, X, E/D/C then 1..0), the army pyramid, the message line, whether the mouse is over a HUD
// panel. The HUD (game/src/UI/Hud) implements it and registers itself: GameSession.Current.Hud = hud.
// docs/GAME_SESSION.md.
using Godot;

namespace PwRemake.UI.PlayerInput;

public interface IGameHud
{
    /// <summary>a command bar row is armed: the digits press its buttons</summary>
    bool Armed { get; }
    /// <summary>arms a row of the command bar: kind "build" | "produce" | "stances" | "special", row counted from the
    /// bottom (0..2); false when there is nothing in that row</summary>
    bool ArmRow(string kind, int row);
    void Disarm();
    /// <summary>presses button i (0..9) of the armed row</summary>
    void PressArmed(int index);
    /// <summary>Tab: the army pyramid open / closed</summary>
    void TogglePyramid();
    /// <summary>a line on the message bar: kind "" | "good" | "bad"</summary>
    void Message(string text, string kind);
    /// <summary>the screen point is over a HUD panel (no picking in the world, no edge scrolling)</summary>
    bool IsOverHud(Vector2 screen);
    /// <summary>a tooltip for a thing in the world (a question mark): title (null: none) and text at a screen rectangle
    /// (null: the tooltip window's own place); text null: that tooltip goes</summary>
    void WorldTip(string? title, string? text, Rect2? at) { }
    /// <summary>shown (hidden during cutscenes and by Backspace)</summary>
    bool Visible { get; set; }
}
