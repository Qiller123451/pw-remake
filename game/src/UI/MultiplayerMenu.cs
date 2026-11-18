// The multiplayer menu (the original's MainMenu "Multiplayer" -> MPlSelSc / MPHIPScr "Host Direct IP" / MPJIPScr
// "Join Direct IP", with their captions from the game's texts): the player's name, hosting a game on a port - a
// skirmish (PvP) or a campaign mission together (co-op) - and joining a host by its IP address and port. Hosting opens
// the lobby at once (SkirmishMenu in its network mode, CoopLobby); joining waits for the host's welcome and opens the
// lobby of what the host offers. docs/MULTIPLAYER.md.
//
// Not the original's: GameSpy's internet game list and the LAN browser (the original's servers are gone); a direct IP
// covers both (the host forwards the port in its router for an internet game).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Net;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;

namespace PwRemake.UI;

public partial class MultiplayerMenu : MenuScreen
{
    static readonly Logger L = Log.For("UI.Multiplayer");
    LineEdit _name = null!, _ip = null!;
    SpinBox _hostPort = null!, _joinPort = null!;
    OptionButton _mode = null!;
    Label _msg = null!;
    double _waitT = -1;

    public MultiplayerMenu() : base("Multiplayer", minWidth: 640) { }

    static GameTexts Tx => GameTexts.For(GameApp.Config);
    static string T(string key, string fallback) => Tx.Label(key, fallback);

    protected override void Build()
    {
        var b = Window.Body;
        var s = GameApp.Settings;
        b.AddChild(PwTheme.Heading(Tx.Get("_UI_MPlSelSc_Title_Multiplayer", "Multiplayer"), 22));
        b.AddChild(PwTheme.Para(GameApp.Config != null ? $"{GameApp.Config.Title} · {GameApp.Version}" : "", 12, PwTheme.Dim, HorizontalAlignment.Center));
        // ---- the name
        _name = new LineEdit { Text = s.PlayerName.Length > 0 ? s.PlayerName : System.Environment.UserName, MaxLength = 24, CustomMinimumSize = new Vector2(260, 0) };
        b.AddChild(Row(new Label { Text = T("_UI_MPHIPScr_Label_Name", "Name") }, _name));
        // ---- host
        var gh = Group(Tx.Get("_UI_MPHIPScr_Title_HostDirectIP", "Host Direct IP"));
        b.AddChild(gh);
        _mode = new OptionButton { CustomMinimumSize = new Vector2(260, 26), FocusMode = FocusModeEnum.None };
        _mode.AddItem(Tx.Get("_UI_MainMenu_Button_Skirmish", "Skirmish") + " (PvP)"); _mode.SetItemMetadata(0, "skirmish");
        _mode.AddItem(Tx.Get("_UI_MainMenu_Button_StartCampaign", "Campaign") + " (co-op)"); _mode.SetItemMetadata(1, "coop");
        _mode.Select(s.MpMode == "coop" ? 1 : 0);
        _hostPort = new SpinBox { MinValue = 1024, MaxValue = 65535, Value = Math.Clamp(s.MpPort, 1024, 65535), CustomMinimumSize = new Vector2(110, 0), TooltipText = Tx.Get("_UI_GamLWind_Port_TT", "") };
        gh.AddChild(Row(new Label { Text = Tx.Get("_UI_GamLWind_Label_GameType", "Game Type") }, _mode, new Label { Text = T("_UI_MPHIPScr_Label_Port", "Port") }, _hostPort));
        var ips = NetSession.LocalAddresses();
        gh.AddChild(PwTheme.Para($"{T("_UI_MPHIPScr_Label_IPAddress", "IP address")}: {(ips.Count > 0 ? string.Join(", ", ips) : "?")}  -  the others join this address and port " +
                                 "(over the internet: forward the port in the router).", 12, PwTheme.Dim));
        var host = PwTheme.Btn(Tx.Get("_UI_MPHIPScr_Button_Host", "Host"), Host, 200);
        host.TooltipText = Tx.Get("_UI_MPHIPScr_Tooltip_Host", "");
        gh.AddChild(Row(host));
        // ---- join
        var gj = Group(Tx.Get("_UI_MPJIPScr_Title_JoinDirectIP", "Join Direct IP"));
        b.AddChild(gj);
        _ip = new LineEdit { Text = s.MpAddress, CustomMinimumSize = new Vector2(220, 0), TooltipText = Tx.Get("_UI_MPJIPScr_IP_TT", "") };
        _joinPort = new SpinBox { MinValue = 1, MaxValue = 65535, Value = Math.Clamp(s.MpPort, 1, 65535), CustomMinimumSize = new Vector2(110, 0), TooltipText = Tx.Get("_UI_MPJIPScr_PORT_TT", "") };
        gj.AddChild(Row(new Label { Text = T("_UI_MPJIPScr_Label_IP", "IP") }, _ip, new Label { Text = T("_UI_MPJIPScr_Label_PORT", "Port") }, _joinPort));
        var join = PwTheme.Btn(Tx.Get("_UI_MPJIPScr_Button_Join", "Join"), Join, 200);
        join.TooltipText = Tx.Get("_UI_MPJIPScr_Tooltip_Join", "");
        gj.AddChild(Row(join));
        _msg = PwTheme.Para(NetSession.LastError ?? "", 14, PwTheme.Lost, HorizontalAlignment.Center);
        NetSession.LastError = null;
        b.AddChild(_msg);
        b.AddChild(Row(PwTheme.Btn(Tx.Get("_UI_MPHIPScr_Button_Back", "Back"), () => { NetSession.Close(); Boot.Instance.Show(new TitleScreen()); }, 160)));
    }

    string PlayerName()
    {
        var n = _name.Text.Trim();
        if (n.Length == 0) n = "Player";
        var s = GameApp.Settings;
        s.PlayerName = n; s.MpAddress = _ip.Text.Trim(); s.MpPort = (int)_hostPort.Value; s.MpMode = _mode.GetItemMetadata(_mode.Selected).AsString();
        s.Save();
        return n;
    }

    void Host()
    {
        var name = PlayerName();
        var mode = GameApp.Settings.MpMode;
        try
        {
            var lobby = mode == "coop" ? CoopLobby.InitialLobby(name) : SkirmishMenu.InitialLobby(name);
            NetSession.StartHost(name, (int)_hostPort.Value, lobby);
            Boot.Instance.Show(mode == "coop" ? new CoopLobby() : new SkirmishMenu(network: true));
        }
        catch (Exception e)
        {
            L.Warn("hosting: " + e.Message);
            _msg.Text = $"The game could not be hosted on port {(int)_hostPort.Value}: {e.Message}";
        }
    }

    void Join()
    {
        var name = PlayerName();
        GameApp.Settings.MpPort = (int)_joinPort.Value; GameApp.Settings.Save();
        _msg.Text = Tx.Get("_JoinToMultiPlayerGameWindow_Connecting", "Connect to server?").TrimEnd('?') + " …";
        try
        {
            NetSession.Join(name, _ip.Text.Trim(), (int)_joinPort.Value);
            _waitT = 10;
        }
        catch (Exception e)
        {
            NetSession.Close();
            _msg.Text = $"{Tx.Get("_UI_JoinTMPG_Button_Connectfailed", "Connection failed")}: {e.Message}";
        }
    }

    /// <summary>--host [port] [--mp-mode skirmish|coop] [--mp-name n] [--mp-autostart players] [--mp-mission id] [--players
    /// Hu,Aje,...] (skirmish: the slots - "human" for a human's open slot, a tribe for a computer player) | --join
    /// address[:port] [--mp-name n] [--mp-ready]: a networked game without the menus (tests: two instances,
    /// docs/TESTING.md)</summary>
    public static void QuickStart(System.Collections.Generic.Dictionary<string, string> args)
    {
        var name = args.GetValueOrDefault("mp-name", args.ContainsKey("host") ? "Host" : "Guest");
        try
        {
            if (args.TryGetValue("host", out var hp))
            {
                int port = int.TryParse(hp, out var pp) ? pp : NetProtocol.DefaultPort;
                bool coop = args.GetValueOrDefault("mp-mode", "skirmish") == "coop";
                var lobby = coop ? CoopLobby.InitialLobby(name) : SkirmishMenu.InitialLobby(name);
                if (coop && args.TryGetValue("mp-mission", out var mi) && int.TryParse(mi, out var mid) && lobby.Campaign != null)
                {
                    var m = MissionEntry.Load(GameApp.Config!.MissionsDir).FirstOrDefault(x => x.Id == mid);
                    if (m != null) CoopLobby.SetMission(lobby, m, lobby.Campaign.Tribe, lobby.Campaign.Difficulty);
                }
                // --coop-mode campaign --coop-seats human,ai,empty [--coop-tribes Hu,Aje,Ninigi] [--coop-difficulty 0..2]: the
                // co-op campaign mode (docs/COOP.md; two instances: docs/TESTING.md)
                if (coop && args.GetValueOrDefault("coop-mode", "shared") == "campaign" && lobby.Coop != null)
                {
                    lobby.CoopMode = "campaign";
                    var kinds = args.GetValueOrDefault("coop-seats", "human,human,empty").Split(',');
                    var tribes = args.GetValueOrDefault("coop-tribes", "").Split(',');
                    for (int i = 0; i < 3; i++)
                    {
                        lobby.Coop.Seats[i].Kind = i < kinds.Length && kinds[i] is "human" or "ai" or "empty" ? kinds[i] : i < 2 ? "human" : "empty";
                        if (i < tribes.Length) lobby.Coop.Seats[i].Tribe = tribes[i];
                    }
                    if (int.TryParse(args.GetValueOrDefault("coop-difficulty", "0"), out var cd)) lobby.Coop.Difficulty = Math.Clamp(cd, 0, 2);
                }
                if (!coop && lobby.Skirmish != null && args.TryGetValue("players", out var pl))
                {
                    var list = pl.Split(',');
                    for (int i = 0; i < lobby.Skirmish.Slots.Count; i++)
                    {
                        var sl = lobby.Skirmish.Slots[i];
                        if (i >= list.Length) { sl.Type = "closed"; continue; }
                        if (list[i] == "human") { sl.Type = "open"; sl.Tribe = "Hu"; }
                        else { sl.Type = "ai"; sl.Tribe = list[i]; }
                        sl.Team = i + 1;
                    }
                    lobby.Skirmish.Teams = list.Length;
                    if (args.ContainsKey("skirmish-random")) { lobby.Skirmish.Map = ""; lobby.Skirmish.MapName = null; }
                }
                var s = NetSession.StartHost(name, port, lobby);
                if (args.TryGetValue("mp-autostart", out var au) && int.TryParse(au, out var n)) s.AutostartPlayers = n;
                Boot.Instance.Show(coop ? new CoopLobby() : new SkirmishMenu(network: true));
            }
            else
            {
                var j = args["join"];
                int port = NetProtocol.DefaultPort;
                var k = j.LastIndexOf(':');
                if (k > 0 && int.TryParse(j[(k + 1)..], out var jp)) { port = jp; j = j[..k]; }
                var s = NetSession.Join(name, j, port);
                s.AutoReady = args.ContainsKey("mp-ready");
                var menu = new MultiplayerMenu { _waitT = 15 };
                Boot.Instance.Show(menu);
            }
        }
        catch (Exception e) { L.Error("--host / --join", e); NetSession.LastError = e.Message; Boot.Instance.Show(new MultiplayerMenu()); }
    }

    public override void _Process(double delta)
    {
        if (_waitT < 0) return;
        var n = NetSession.Current;
        if (n == null) { _waitT = -1; return; }
        if (n.Client.Error != null) { _msg.Text = n.Client.Error; NetSession.Close(); _waitT = -1; return; }
        if (n.Client.Lobby != null)
        {
            _waitT = -1;
            Boot.Instance.Show(n.Client.Lobby.Mode == "coop" ? new CoopLobby() : new SkirmishMenu(network: true));
            return;
        }
        _waitT -= delta;
        if (_waitT < 0) { _msg.Text = Tx.Get("_Game_NoServer", "Server not found!"); NetSession.Close(); }
    }
}

/// <summary>The lobby's player list and chat (the original's PlayerListWindow chat: "Chat"), for the skirmish and the
/// co-op lobby.</summary>
public partial class LobbyChat : VBoxContainer
{
    readonly NetClient _c;
    readonly RichTextLabel _log;
    readonly LineEdit _line;
    int _seen;

    public LobbyChat(NetClient c)
    {
        _c = c;
        AddThemeConstantOverride("separation", 4);
        _log = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = true, CustomMinimumSize = new Vector2(0, 110), SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true };
        _log.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color(0.05f, 0.04f, 0.03f, 0.6f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4 });
        AddChild(_log);
        _line = new LineEdit { MaxLength = 150, SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = GameTexts.For(GameApp.Config).Get("_ChatWindow_TX_Entertexthere", "Enter text here") };
        _line.TextSubmitted += _ => Send();
        var row = new HBoxContainer();
        row.AddChild(_line);
        row.AddChild(PwTheme.Btn(GameTexts.For(GameApp.Config).Get("_ChatWindow_TX_Send", "Send"), Send, 90));
        AddChild(row);
    }

    void Send()
    {
        var t = _line.Text.Trim();
        _line.Text = "";
        if (t.Length > 0) _c.SendChat(t);
    }

    public override void _Process(double delta)
    {
        for (; _seen < _c.Chat.Count; _seen++)
        {
            var l = _c.Chat[_seen];
            static string Esc(string s) => s.Replace("[", "[lb]");
            _log.AppendText(l.To == "sys" ? $"[color=#c8b890]{Esc(l.Text)}[/color]\n" : $"[color=#ffe6a0]{Esc(l.Name)}:[/color] {Esc(l.Text)}\n");
        }
    }
}
