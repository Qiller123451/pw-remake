// The HUD's host (game/src/UI/Hud/IHudHost.cs, docs/HUD.md) on the game session: the world, the human player, the
// selection, the commands, the camera, the fog of war, the input's modes and control groups, the mission UI, the
// interface sounds and the menu. The HUD orders only through Submit (the session's command sink).
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.UI.Hud;
using PwRemake.UI.Mission;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.App;

public sealed class SessionHudHost : IHudHost
{
    readonly GameSession S;
    public SessionHudHost(GameSession s) { S = s; }

    public GameWorld World => S.World;
    public Player Me => S.Me;
    public bool Campaign => S.Mission != null;
    /// <summary>a skirmish: the diplomacy button (hud.js: G.setup)</summary>
    public bool Multiplayer => S.Skirmish != null;
    public bool CoopSeats => S.Coop != null;
    // (through the session: the command is the human's - Player = Me - and goes to the network in a networked game)
    public void Submit(Command c) => S.Submit(c);
    /// <summary>a networked game: the chat line goes to the other players (true); single player: false</summary>
    public bool Chat(string to, string text) => S.SendChat(to, text);
    public IReadOnlyList<Entity> Selection => S.Selection.Items;
    public void SetSelection(IReadOnlyList<Entity> list, bool silent = false) => S.Selection.Replace(list, silent);
    public (double X, double Z, double Yaw) Camera => (S.Camera.X, S.Camera.Z, S.Camera.Yaw);
    public void MoveCamera(double x, double z) => S.LookAt(x, z);
    public IReadOnlyList<(double X, double Z)>? ViewCorners() => S.Input.ViewCorners();
    public IFogOfWar? Fog => S.Fog;
    public int FogVersion => S.Fog.Version;
    public string? InputMode => S.Input.Mode;
    public void SetInputMode(string? mode, ActionDef? a = null) => S.Input.SetMode(mode, a);
    public IReadOnlyList<Entity>? Group(int k) => S.Input.Groups.GetValueOrDefault((k % 10).ToString());
    public string? SelectedGroup => S.Input.SelGroup;
    public void GroupButton(int k, string how) => S.Input.GroupButton((k % 10).ToString(), how == "click" ? "select" : how);
    public void OrderOn(Entity target, bool shift, bool ctrl) => S.Input.OrderOn(target, null, shift, ctrl);
    public MissionPresenter? Mission => S.Presenter;
    public (double X, double Z) MapToWorld(double mx, double my) => S.Mission?.Campaign.ToGame(mx, my) ?? (mx, my);
    public void UiSound(params string[] names) { try { PwRemake.View.Audio.GameAudio.Current?.Core.PlayFirst(names); } catch (System.Exception) { } }
    public void ErrorSound() => S.PlayError();
    public void Ordered(string type, IReadOnlyList<Entity> units) => S.PlayOrdered(type, units);
    public void OpenMenu() => S.OpenMenu();
    public void OpenChat(bool history) { if (history) S.HudView.Chat.ShowHistory(); else S.HudView.Chat.Open("all"); }
}
