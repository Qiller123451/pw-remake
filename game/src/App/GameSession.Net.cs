// The game session in a networked game (docs/MULTIPLAYER.md): the world runs on the host's turns (LockstepGame) instead
// of the local clock - the session hands its step's own work (the entity views' previous positions, the mission UI's
// tick) to the runner; the player's commands go to the host (they come back in a turn; their results reach the
// interface through the copy that was applied); a cutscene's or dialogue's end is a vote, not a command; the Pause key
// asks the host to pause everybody; the pause menu, the quest log and cutscenes do not stop the world (the host holds
// the turns while a cutscene is on show); chat goes to the other players; a status line shows "Paused by", "Waiting
// for", a lost connection and a desync. The host's seat sends its camera and selection for the tutorial's conditions
// (UiStateCommand). Saving is the host's; loading is not offered in a networked game.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Net;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Game;
using PwRemake.Sim.World;

namespace PwRemake.App;

public partial class GameSession
{
    /// <summary>the networked game's runner (null: single player)</summary>
    public LockstepGame? Net { get; private set; }
    /// <summary>the session of the networked game this session belongs to (null: single player)</summary>
    public NetSession? NetLink { get; private set; }
    /// <summary>the mission's answers in a lockstep game (and when a saved lockstep game is loaded)</summary>
    LockstepPresenter? _lockstep;
    Label? _netStatus;
    double _uiStateT;
    string _uiStateSent = "";
    int _chatSeen;

    /// <summary>is this session the game a networked game started (its start object came from the host)</summary>
    static NetSession? NetOf(GameStart start) => NetSession.Current is { } n && ReferenceEquals(n.Client.Start, start) ? n : null;

    /// <summary>this machine's seat of a start with seats: the networked game's, else seat 0 (a saved networked game
    /// loaded alone: the host's player)</summary>
    int SeatOf() => NetOf(Start) is { } n ? n.Client.Seat : 0;

    /// <summary>the human player of this machine</summary>
    Player MyPlayer()
    {
        if (Start.Seats is { Count: > 0 } seats)
        {
            int seat = Math.Clamp(SeatOf(), 0, seats.Count - 1);
            var p = World.Players.FirstOrDefault(x => x.Id == seats[seat].Player);
            if (p != null) return p;
        }
        return Sim.Human ?? World.Players.First();
    }

    /// <summary>where this machine's camera starts when its player is not the game's first human (a skirmish's other human:
    /// its home; a co-op ally taken over: its main building, else its first unit); null: the game's start camera</summary>
    (double X, double Z)? MyStart()
    {
        if (Start.Seats == null || Me == Sim.Human) return null;
        Entity? home = Skirmish?.Homes.FirstOrDefault(h => h.Owner == Me)
            ?? (Entity?)World.Buildings.FirstOrDefault(b => b.Owner == Me && b.Alive) ?? World.Units.FirstOrDefault(u => u.Owner == Me && u.Alive);
        return home == null ? null : (home.Pos.X, home.Pos.Z);
    }

    /// <summary>the tap of a lockstep start: the mission's questions answered by LockstepPresenter (docs/MULTIPLAYER.md)</summary>
    PresenterTap MakeTap()
    {
        if (Start.Seats == null) return new PresenterTap(PresenterLink);
        var lp = _lockstep = new LockstepPresenter(Start, PresenterLink);
        var tap = new PresenterTap(lp) { DeterministicInner = true };
        lp.Quiet = () => tap.Replaying;
        return tap;
    }

    /// <summary>in the tree: join the networked game (the runner, the command sink, "loaded" to the host)</summary>
    void NetReady()
    {
        if (_lockstep != null && Mission != null) _lockstep.C = Mission.Campaign;
        var link = NetOf(Start);
        if (link == null) return;
        NetLink = link;
        var c = link.Client;
        Net = new LockstepGame(c, Sim, Start, c.Seat, _lockstep) { DumpDir = AppPaths.LogsDir };
        Net.Delivered += (mine, applied) => { if (_waiting.Remove(mine, out var cb)) _waiting[applied] = cb; };
        Commands = new NetSink(Net);
        _chatSeen = c.Chat.Count;
        c.DesyncDetected += d => Message("The games are no longer the same (desync): " + d + " - a dump of the world is written to the log folder.", "bad");
        var layer = new CanvasLayer { Layer = 40, Name = "NetStatus" };
        _netStatus = new Label { HorizontalAlignment = HorizontalAlignment.Center, AnchorLeft = 0, AnchorRight = 1, OffsetTop = 64, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _netStatus.AddThemeFontSizeOverride("font_size", 22);
        _netStatus.AddThemeColorOverride("font_color", new Color(1f, 0.93f, 0.7f));
        _netStatus.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _netStatus.AddThemeConstantOverride("shadow_offset_x", 2); _netStatus.AddThemeConstantOverride("shadow_offset_y", 2);
        layer.AddChild(_netStatus);
        AddChild(layer);
        c.Loaded(World.StepNo);
        L.Info($"networked game: seat {c.Seat} ({Me.Name}, player {Me.Id}) of {Start.Seats?.Count} seats; host {(link.Hosting ? "here" : "elsewhere")}");
    }

    /// <summary>the frame's steps on the host's turns; the status line; incoming chat; the host seat's view for the mission</summary>
    void NetFrame(double dt)
    {
        var N = Net!;
        _stepsLastFrame = N.Advance(dt, () => Entities.BeforeStep(), () => Presenter.Tick(StepSeconds), _maxSteps);
        Alpha = N.Fraction;
        var c = N.Net;
        string? status = N.Status;
        if (c.DesyncStep >= 0) status = (status != null ? status + "\n" : "") + $"Desync at {PwRemake.Sim.Game.SaveHeader.Clock(c.DesyncStep * StepSeconds)} - see the log";
        if (_netStatus != null) { _netStatus.Visible = status != null; _netStatus.Text = status ?? ""; }
        for (; _chatSeen < c.Chat.Count; _chatSeen++) ShowChat(c.Chat[_chatSeen]);
        // the host's seat: its camera and selection for the mission's tutorial conditions (UNTT), when they changed, at most once a second
        _uiStateT -= dt;
        if (N.Seat == 0 && Mission != null && _uiStateT <= 0)
        {
            _uiStateT = 1;
            var cam = PresenterLink.Camera;
            var sel = Selection.Items.Select(e => e.Id).OrderBy(i => i).ToArray();
            var cmd = new UiStateCommand { Selected = sel };
            if (cam is var (x, y, d, w)) { cmd.X = Math.Round(x, 1); cmd.Y = Math.Round(y, 1); cmd.Dist = Math.Round(d, 1); cmd.Yaw = Math.Round(w, 2); }
            var key = $"{cmd.X},{cmd.Y},{cmd.Dist},{cmd.Yaw}|{string.Join(",", sel)}";
            if (key != _uiStateSent && MissionWatchesView()) { _uiStateSent = key; Submit(cmd); }
        }
    }

    bool? _watchesView;
    /// <summary>does the mission have tutorial conditions on the camera / selection (UNTT): only then the view is sent</summary>
    bool MissionWatchesView() => _watchesView ??= Mission?.Lua.Script.Triggers.Any(t => t.Conditions.Any(c => c.Code == "UNTT")) ?? false;

    void ShowChat(ChatLine l)
    {
        if (l.To == "sys") { Message(l.Text, "warn"); return; }
        var from = Start.Seats?.FirstOrDefault(s => s.Peer == l.From);
        var p = from != null ? World.Players.FirstOrDefault(x => x.Id == from.Player) : null;
        bool forMe = l.To switch
        {
            "allies" => p == null || p == Me || (Me.IsFriend(p) && p.IsFriend(Me)),
            "enemies" => p == null || p == Me || Me.IsEnemy(p),
            _ => true,
        };
        if (!forMe) return;
        string tag = l.To == "allies" ? " (to allies)" : l.To == "enemies" ? " (to enemies)" : "";
        Message($"{l.Name}{tag}: {l.Text}", PwRemake.UI.Hud.Hud.ChatKind(p));
    }

    /// <summary>the HUD's chat line: to the other players (true) in a networked game</summary>
    public bool SendChat(string to, string text)
    {
        if (Net == null) return false;
        Net.Net.SendChat(text, to);
        return true;
    }

    /// <summary>the session's command sink in a networked game: to the host</summary>
    sealed class NetSink : ICommandSink
    {
        readonly LockstepGame _g;
        public NetSink(LockstepGame g) { _g = g; }
        public void Submit(Command c) => _g.Submit(c);
    }
}
