// The networked game of this machine (docs/MULTIPLAYER.md) as long as it lasts - from the multiplayer menu's Host /
// Join through the lobby, the loading screen and the game to leaving: the host (when this machine hosts: NetHost, the
// game's own port) and this machine's client (NetClient, on the host over localhost when hosting). A node under Boot,
// so it lives across the screens; it pumps both every frame before anything else (ProcessPriority), and turns the
// host's "start" into the loading screen of the game.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Net;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.UI;

namespace PwRemake.App;

public partial class NetSession : Node
{
    static readonly Logger L = Log.For("Net.Session");
    /// <summary>the networked game of this machine (null: none)</summary>
    public static NetSession? Current { get; private set; }
    public NetHost? Host { get; private set; }
    public NetClient Client { get; }
    public bool Hosting => Host != null;
    /// <summary>a line for the multiplayer menu after the session ended (refused, connection lost, the host left)</summary>
    public static string? LastError;

    NetSession(NetClient client, NetHost? host)
    {
        Client = client; Host = host; Name = "NetSession";
        ProcessPriority = -100;                          // (before the game session's frame: its turns are there)
        ProcessMode = ProcessModeEnum.Always;
        client.Started += OnStarted;
        client.Disconnected += OnDisconnected;
    }

    static string Fingerprint()
    {
        var c = GameApp.Config;
        try { return c == null ? "" : ContentFingerprint.Of(c.Data, c.MissionsDir); }
        catch (Exception e) { L.Warn("game data fingerprint: " + e.Message); return ""; }
    }

    /// <summary>hosts a game on the port (this machine plays: its client joins over localhost)</summary>
    public static NetSession StartHost(string name, int port, LobbyState lobby)
    {
        Close();
        var fp = Fingerprint();
        var host = new NetHost(port) { Build = GameApp.Version, Fingerprint = fp, DumpDir = AppPaths.LogsDir };
        lobby.Configuration = GameApp.Config?.Id ?? "";
        host.SetLobby(lobby);
        var client = new NetClient(name);
        try { client.Connect("127.0.0.1", host.Port, lobby.Configuration, GameApp.Version, fp, host.HostToken); }
        catch (Exception) { host.Dispose(); throw; }
        var s = new NetSession(client, host);
        Boot.Instance.AddChild(s);
        Current = s;
        L.Info($"hosting \"{lobby.Name}\" ({lobby.Mode}) on port {host.Port} as {name}");
        return s;
    }

    /// <summary>joins a host (connects; the lobby arrives a moment later: Client.Lobby)</summary>
    public static NetSession Join(string name, string address, int port)
    {
        Close();
        var client = new NetClient(name);
        client.Connect(address, port, GameApp.Config?.Id ?? "", GameApp.Version, Fingerprint());
        var s = new NetSession(client, null);
        Boot.Instance.AddChild(s);
        Current = s;
        L.Info($"joining {address}:{port} as {name}");
        return s;
    }

    /// <summary>leaves the networked game (the host's leaving ends it for everybody)</summary>
    public static void Close()
    {
        var s = Current;
        if (s == null) return;
        Current = null;
        try { if (s.Hosting) s.Client.HostEnd(); s.Client.Leave(); } catch (Exception) { }
        s.Host?.Dispose();
        s.QueueFree();
    }

    /// <summary>tests (--mp-autostart n): the host starts once n players are there and ready</summary>
    public int AutostartPlayers;
    /// <summary>tests (--mp-ready): this player says ready as soon as it is in the lobby</summary>
    public bool AutoReady;
    bool _readySent;

    public override void _Process(double delta)
    {
        try { Host?.Update(delta); } catch (Exception e) { L.Error("the host", e); }
        try { Client.Poll(); } catch (Exception e) { L.Error("the client", e); }
        var l = Client.Lobby;
        if (AutoReady && !_readySent && l != null && Client.MyPeer != null) { _readySent = true; Client.SetReady(true); }
        if (AutostartPlayers > 0 && Hosting && l != null && l.State == "lobby" && l.Players.Count() >= AutostartPlayers && LobbyStart.Problem(l) == null)
        {
            AutostartPlayers = 0;
            L.Info("--mp-autostart: everybody is ready - starting");
            Client.HostStart(l.Mode == "coop" ? new MissionPresenterAdapter().LoadProfile() : null);
        }
    }

    /// <summary>the host started: this machine's loading screen of the game (its GameSession joins the lockstep)</summary>
    void OnStarted(GameStart start, int seat)
    {
        L.Info($"the game starts: seat {seat} of {start}{(Client.Rejoining ? " (rejoining: the turns so far are replayed)" : "")}");
        if (start.Mode == GameMode.Skirmish) Boot.Instance.Show(LoadingScreen.ForSkirmish(start));
        else
        {
            var cfg = GameApp.Config!;
            var entry = MissionEntry.Load(cfg.MissionsDir).FirstOrDefault(m => m.Id == start.Mission);
            if (entry == null) { LastError = $"The mission {start.Mission} is not in this machine's campaign."; Close(); Boot.Instance.Show(new MultiplayerMenu()); return; }
            Boot.Instance.Show(new LoadingScreen(start, entry));
        }
    }

    void OnDisconnected(string why)
    {
        L.Info("the networked game ended: " + why);
        LastError = why;
        if (GameSession.Current != null) return;             // (the game shows it on its status line)
        if (Current == this) { Close(); Boot.Instance.Show(new MultiplayerMenu()); }
    }

    /// <summary>this machine's addresses (shown to the host to tell the others)</summary>
    public static List<string> LocalAddresses()
    {
        var res = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address)) res.Add(a.Address.ToString());
            }
        }
        catch (Exception) { }
        return res;
    }
}
