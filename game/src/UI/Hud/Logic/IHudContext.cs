// The Godot-free part of what the HUD needs of the running game (IHudHost.cs adds the mission UI). The HUD's logic
// classes (Logic/*.cs) use only this.
using System.Collections.Generic;
using PwRemake.Sim.Commands;
using PwRemake.Sim.World;

namespace PwRemake.UI.Hud.Logic;

public interface IHudContext
{
    // ------------------------------------------------------------------ the game
    /// <summary>the simulation (read only: orders go through Submit)</summary>
    World World { get; }
    /// <summary>the human player of this machine (G.me)</summary>
    Player Me { get; }
    /// <summary>a campaign mission (G.campaign): owner names in the info window, shared vision of allies</summary>
    bool Campaign { get; }
    /// <summary>a skirmish / multiplayer game with the diplomacy button (G.world.rules.multiplayer &amp;&amp; G.setup)</summary>
    bool Multiplayer { get; }
    /// <summary>a co-op campaign game (docs/COOP.md): the diplomacy window offers the seats' tribute</summary>
    bool CoopSeats => false;

    /// <summary>a command of the human player (the session's ICommandSink; the HUD sets Command.Player)</summary>
    void Submit(Command c);

    // ------------------------------------------------------------------ selection (G.sel, in selection order)
    IReadOnlyList<Entity> Selection { get; }
    /// <summary>replaces the selection as it is (no filtering: the HUD applies G.select's rules itself); silent: no
    /// selection sound (G.selChanged(silent))</summary>
    void SetSelection(IReadOnlyList<Entity> list, bool silent = false);

    // ------------------------------------------------------------------ camera and view
    /// <summary>the camera's ground point and turn (G.rtscam x, z, yaw)</summary>
    (double X, double Z, double Yaw) Camera { get; }
    /// <summary>moves the camera's ground point there (G.centerOn, the minimap's click and drag)</summary>
    void MoveCamera(double x, double z);
    /// <summary>the camera's ground footprint (input.js viewCorners: the screen's corners on the ground - top left, top
    /// right, bottom right, bottom left; null when unknown): the minimap's trapezoid</summary>
    IReadOnlyList<(double X, double Z)>? ViewCorners();
    /// <summary>the human player's fog of war (G.fow: Visible / Explored; null = everything visible)</summary>
    IFogOfWar? Fog { get; }
    /// <summary>changes whenever the fog changes (the minimap repaints its cells then; FogGrid.Version)</summary>
    int FogVersion { get; }

    // ------------------------------------------------------------------ input (ui/input.js)
    /// <summary>the armed mouse mode (move, attack, amove, repair, rally, place, target, lay, mapping) or null</summary>
    string? InputMode { get; }
    /// <summary>arms a mouse mode (G.input.setMode): the next click on the world gives the order; a = the action of
    /// "place" (a building), "target" (a special move) and "lay" (a mine); null ends it</summary>
    void SetInputMode(string? mode, Sim.Rules.ActionDef? a = null);
    /// <summary>the control groups: the members of group k (0..9; null or empty: unassigned)</summary>
    IReadOnlyList<Entity>? Group(int k);
    /// <summary>the group the selection was made from ("1" .. "0") or null (GroupBar.usl: that button stays pressed)</summary>
    string? SelectedGroup { get; }
    /// <summary>a group bar button (G.input.groupButton): how = click | assign | double</summary>
    void GroupButton(int k, string how);
    /// <summary>Alt + right click on a pyramid card: the selection's right-click order on that unit (input.orderOn)</summary>
    void OrderOn(Entity target, bool shift, bool ctrl);

    // ------------------------------------------------------------------ the rest of the interface
    /// <summary>map coordinates of the mission UI's markers -> game x, z (Campaign.ToGame)</summary>
    (double X, double Z) MapToWorld(double mx, double my);
    /// <summary>an interface sound, the first of the names that exists (feedback.js ui: "UI_click",
    /// "ui_click_pyramid", "ui_pyramid_open" ...): the game's sound events</summary>
    void UiSound(params string[] names);
    /// <summary>the error sound and voice (feedback.js error)</summary>
    void ErrorSound();
    /// <summary>the acknowledgement of an order (feedback.js ordered: the unit's voice)</summary>
    void Ordered(string type, IReadOnlyList<Entity> units);
    /// <summary>the top bar's buttons: the main menu (G.openMenu), the chat (open / its history)</summary>
    void OpenMenu();
    void OpenChat(bool history);
}
