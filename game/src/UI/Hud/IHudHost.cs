// What the HUD needs of the running game (the game session implements it; the HUD test scene has a fake one).
//
// The browser remake's HUD reached into the global game object G (main.js: G.world, G.me, G.sel, G.input, G.rtscam,
// G.fow, G.mission, G.feedback, G.settings ...). Here the session hands the HUD exactly this: the world and the
// human player, the selection, a command sink, the camera, the fog of war, the input modes, the control groups,
// the mission UI's markers and the sounds of the interface. Everything the HUD orders goes through Submit (a
// command, docs/ARCHITECTURE.md "Commands") - the HUD never changes the world itself.
//
// The Godot-free part is Logic/IHudContext.cs (what the HUD's logic classes use); this adds the mission UI.
// docs/HUD.md has the contract and what the session calls on the HUD.
using PwRemake.UI.Mission;

namespace PwRemake.UI.Hud;

public interface IHudHost : Logic.IHudContext
{
    /// <summary>the mission UI (minimap markers, the quest button) or null outside a campaign</summary>
    MissionPresenter? Mission { get; }
    /// <summary>a chat line of the player ("all" | "allies" | "enemies"): true when a networked game sent it to the other
    /// players (their lines come back as messages); false: no other humans (docs/MULTIPLAYER.md)</summary>
    bool Chat(string to, string text) => false;
}
