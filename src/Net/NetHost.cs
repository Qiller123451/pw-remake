// The host of a networked game (docs/MULTIPLAYER.md): it listens for players, keeps the lobby, starts the game and
// then orders everybody's commands into turns - the heart of the lockstep model.
//
//   lobby    players connect (hello: name, protocol, game version, configuration, a fingerprint of the game data);
//            the host's own client proves it is the host with a token; skirmish: every human takes an open slot of the
//            setup, co-op: every human picks the mission's player it commands; ready; the host starts.
//   loading  every machine builds the world of the same GameStart (with its seats) and says "loaded".
//   running  every 50 ms the host issues the next turn: the step it is for and the commands that reached it since the
//            last one (in the order they arrived; the host's own commands too - its client is a client like the
//            others). The first InputDelay turns are issued at once, empty: a command is applied InputDelay steps
//            (plus the way to the host) after it was given, on every machine at the same step. Turns are not issued
//            while a player paused the game, while a cutscene is on show (until the players have seen or skipped it:
//            the scene rule), while a player who lost the connection is waited for, or while a machine has fallen
//            more than MaxLag steps behind.
//            Every HashEvery steps each machine reports its world's hash (and the hashes of its parts); a difference is
//            a desync: everybody is told the step, and every machine writes a dump of its world at the same later step
//            (dump-at), which the host collects, compares and writes next to its log.
//
// The host runs no simulation itself (a dedicated server - src/Server - is this class alone); in a game hosted from the
// game's menu the host's player is an ordinary client connected over localhost. Everything happens in Update(), on the
// caller's thread; the sockets' threads only queue what arrives.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;

namespace PwRemake.Net;

public sealed class NetHost : IDisposable
{
    static readonly Logger L = Log.For("Net.Host");

    sealed class Peer
    {
        public int Id;
        public NetConnection Conn = null!;
        public string? Name;                      // (null until hello)
        public bool IsHost, Spectator, Ready, Loaded, Connected = true, Dropped;
        public long AckStep;
        public double LastHeard, LostAt = -1;
        public string? Problem;
        /// <summary>the player's copy of the lobby's map differs or is missing (its "map-check"; LobbyMap)</summary>
        public string? MapProblem;
        public int Slot = -1, Player;
        /// <summary>co-op campaign mode: the seat (1..3) this human plays, 0 none</summary>
        public int Seat;
        public double PingSent = -1;
        public int Ping;
        public string Build = "";
        /// <summary>the lobby's map being sent to this player (null: none)</summary>
        public MapSend? Tx;
    }

    /// <summary>a map on its way to a player (MapTransfer): the bytes, how many were sent and acknowledged</summary>
    sealed class MapSend
    {
        public string Hash = "", Name = "";
        public byte[] Data = Array.Empty<byte>();
        public int Sent, Acked, Pct = -1;
    }

    /// <summary>the lobby's map as the bytes of a .pwmap, for a player who lacks it (map id, content hash -> bytes; null:
    /// the host cannot send it - why). The game's host sets it (its maps: MapTransfer.PackFor); a dedicated server may serve
    /// one map file (--map); null: maps are not sent.</summary>
    public Func<string, string, (byte[]? Data, string? Why)>? MapFile;
    readonly Dictionary<string, byte[]> _mapBytes = new();

    public readonly int Port;
    /// <summary>proves the host's own client (it sends it with its hello)</summary>
    public readonly string HostToken = Guid.NewGuid().ToString("N");
    /// <summary>the game's version: a client of another version is refused</summary>
    public string Build = "";
    /// <summary>the host's game data fingerprint (ContentFingerprint): a client with other data can join but not play</summary>
    public string Fingerprint = "";
    /// <summary>a dedicated server: no player of its own; it starts when Autostart players are ready</summary>
    public bool Dedicated;
    public int Autostart;
    /// <summary>where desync dumps go (null: not written)</summary>
    public string? DumpDir;
    public LobbyState Lobby = new();
    /// <summary>the game's start once it has begun (with its seats)</summary>
    public GameStart? Start { get; private set; }
    /// <summary>every turn issued: [step, commands]; index = step - 1 - BaseStep (a dropped player's rejoin, the server's
    /// replay)</summary>
    public readonly List<JsonObject> Turns = new();
    /// <summary>the step before the first turn (0; a saved game continued: its step)</summary>
    public long BaseStep;
    public long LastIssued { get; private set; }
    public bool PausedByPlayer { get; private set; }
    public string? PausedBy { get; private set; }
    public long DesyncAt { get; private set; } = -1;
    /// <summary>lines for a console (the dedicated server)</summary>
    public event Action<string>? Info;

    readonly TcpListener _listener;
    readonly Thread _acceptThread;
    readonly ConcurrentQueue<TcpClient> _incoming = new();
    readonly List<Peer> _peers = new();
    int _nextPeer = 1;
    double _now, _acc, _lastPing;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly List<JsonObject> _pending = new();
    volatile bool _disposed;
    // scenes: the cutscene on show (it holds the turns) and the votes to end each scene
    (string Kind, int Token)? _sceneHold;
    readonly Dictionary<(string Kind, int Token), (HashSet<int> Votes, bool Timeline)> _votes = new();
    readonly HashSet<(string Kind, int Token)> _scenesDone = new();
    // hashes per step and peer; dumps of a desync
    readonly SortedDictionary<long, Dictionary<int, (ulong Hash, JsonObject? Parts)>> _hashes = new();
    long _dumpStep = -1;
    readonly Dictionary<int, string> _dumps = new();
    string _waitingFor = "";
    // co-op armies (docs/COOP.md "Armies"): seat -> the army its human chose (null: the level's), who chose it, in which
    // faction, for which mission - a change of any of them drops the choice
    readonly Dictionary<int, (Army? Army, int Peer, string Tribe, int Mission)> _armies = new();
    /// <summary>seconds the state "armies" lasts at most (NetProtocol.ArmyTimeout; tests shorten it)</summary>
    public double ArmyTimeout = NetProtocol.ArmyTimeout;
    double _armyDeadline;
    JsonObject? _armyProfile;

    public NetHost(int port = NetProtocol.DefaultPort, bool loopbackOnly = false)
    {
        _listener = new TcpListener(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "net-accept" };
        _acceptThread.Start();
        L.Info($"hosting on port {Port}");
    }

    void AcceptLoop()
    {
        while (!_disposed)
        {
            try { _incoming.Enqueue(_listener.AcceptTcpClient()); }
            catch (Exception) { if (_disposed) return; Thread.Sleep(50); }
        }
    }

    void Say(string s) { L.Info(s); Info?.Invoke(s); }

    // ======================================================================================== the host's loop
    /// <summary>handles what arrived, issues the turns due (dt: real seconds since the last call)</summary>
    public void Update(double dt)
    {
        _now = _clock.Elapsed.TotalSeconds;
        while (_incoming.TryDequeue(out var tcp))
        {
            var p = new Peer { Id = _nextPeer++, Conn = new NetConnection(tcp), LastHeard = _now };
            _peers.Add(p);
        }
        foreach (var p in _peers.ToList())
        {
            while (p.Conn.Inbox.TryDequeue(out var m))
            {
                p.LastHeard = _now;
                try { Handle(p, m); }
                catch (Exception e) { L.Error($"message {m["t"]} of {p.Name ?? p.Conn.Remote}", e); }
            }
            if (p.Conn.Closed && p.Connected) Lost(p, p.Conn.Reason ?? "connection lost");
        }
        if (_now - _lastPing > 2) { _lastPing = _now; foreach (var p in Live) { p.PingSent = _now; p.Conn.Send(Msg("ping")); } }
        if (Lobby.State == "lobby" && Dedicated && Autostart > 0 && Lobby.Players.Count() >= Autostart && Lobby.Players.All(x => x.Ready) && LobbyStart.Problem(Lobby) == null) RequestStart(null);
        if (Lobby.State == "armies") ArmiesTick();
        if (Lobby.State == "lobby") PumpMaps();
        if (Lobby.State == "running") RunTurns(dt);
    }

    // ---------------------------------------------------------------- the lobby's map to a player who lacks it (MapTransfer)
    void MapRequest(Peer p, JsonObject m)
    {
        var hash = m["hash"]?.GetValue<string>() ?? "";
        var sk = Lobby.Skirmish;
        void Fail(string why) { p.Conn.Send(Msg("map-fail", ("hash", hash), ("reason", why))); p.MapProblem = "does not have the map - the host cannot send it: " + why; p.Ready = false; SyncLobby(); Say($"{p.Name}: the map cannot be sent: {why}"); }
        if (Lobby.State != "lobby" || Lobby.Mode != "skirmish" || sk == null || sk.MapHash == null || hash != sk.MapHash) return;      // (an old request)
        if (MapFile == null) { Fail("this host does not send maps (copy the host's .pwmap into your maps folder)"); return; }
        if (!_mapBytes.TryGetValue(hash, out var data))
        {
            (byte[]? Data, string? Why) r;
            try { r = MapFile(sk.Map, hash); }
            catch (Exception e) { r = (null, e.Message); }
            if (r.Data == null) { Fail(r.Why ?? "no such map"); return; }
            if (r.Data.Length > MapTransfer.MaxBytes) { Fail($"the map is too big ({r.Data.Length >> 20} MB, at most {MapTransfer.MaxBytes >> 20} MB)"); return; }
            _mapBytes.Clear();                                   // (one map at a time: the lobby's)
            _mapBytes[hash] = data = r.Data;
        }
        p.Tx = new MapSend { Hash = hash, Name = sk.MapName ?? sk.Map, Data = data, Pct = 0 };
        p.Ready = false;
        p.MapProblem = MapProgressText(p.Tx);
        Say($"sending the map {sk.Map} ({data.Length} bytes) to {p.Name}");
        SyncLobby();
    }

    static string MapProgressText(MapSend tx) =>
        $"is receiving the map \"{tx.Name}\": {100L * tx.Acked / Math.Max(1, tx.Data.Length)} % of {tx.Data.Length / 1048576.0:0.0} MB";

    /// <summary>the next chunks of the maps on their way (at most Window chunks ahead of each player's acknowledgement)</summary>
    void PumpMaps()
    {
        foreach (var p in Live)
        {
            var tx = p.Tx;
            if (tx == null) continue;
            while (tx.Sent < tx.Data.Length && tx.Sent - tx.Acked < MapTransfer.Window * MapTransfer.Chunk)
            {
                int n = Math.Min(MapTransfer.Chunk, tx.Data.Length - tx.Sent);
                p.Conn.Send(Msg("map-data", ("hash", tx.Hash), ("at", tx.Sent), ("total", tx.Data.Length), ("data", Convert.ToBase64String(tx.Data, tx.Sent, n))));
                tx.Sent += n;
            }
        }
    }

    IEnumerable<Peer> Live => _peers.Where(p => p.Connected && p.Name != null);
    IEnumerable<Peer> Players => _peers.Where(p => p.Name != null && !p.Spectator && !p.Dropped);

    static JsonObject Msg(string t, params (string K, JsonNode? V)[] kv)
    {
        var m = NetProtocol.Msg(t);
        foreach (var (k, v) in kv) m[k] = v;
        return m;
    }
    void Broadcast(JsonObject m) { foreach (var p in Live) p.Conn.Send((JsonObject)m.DeepClone()); }
    void Announce(string text) { Say(text); Broadcast(Msg("sys", ("text", text))); }

    // ======================================================================================== messages
    void Handle(Peer p, JsonObject m)
    {
        var t = m["t"]?.GetValue<string>() ?? "";
        if (p.Name == null && t != "hello") return;
        switch (t)
        {
            case "hello": Hello(p, m); break;
            case "pong": if (p.PingSent >= 0) { p.Ping = (int)((_now - p.PingSent) * 1000); var lp = Lobby.Peer(p.Id); if (lp != null) lp.Ping = p.Ping; } break;
            case "chat":
                Broadcast(Msg("chat", ("from", p.Id), ("name", p.Name), ("text", m["text"]?.DeepClone()), ("to", m["to"]?.DeepClone() ?? "all")));
                break;
            case "ready":
                if (Lobby.State != "lobby") break;
                p.Ready = m["on"]?.GetValue<bool>() ?? true;
                SyncLobby();
                break;
            case "lobby-set": if (p.IsHost && Lobby.State == "lobby") SetLobby(NetProtocol.FromNode<LobbyState>(m["lobby"])); break;
            case "slot-set": if (Lobby.State == "lobby") SetSlot(p, m); break;
            case "map-request": MapRequest(p, m); break;
            case "map-ack":
                if (p.Tx != null && m["hash"]?.GetValue<string>() == p.Tx.Hash)
                {
                    p.Tx.Acked = (int)Math.Clamp(m["got"]?.GetValue<long>() ?? 0, p.Tx.Acked, p.Tx.Sent);
                    int pct = (int)(100L * p.Tx.Acked / Math.Max(1, p.Tx.Data.Length));
                    if (pct != p.Tx.Pct) { p.Tx.Pct = pct; p.MapProblem = MapProgressText(p.Tx); SyncLobby(); }
                }
                break;
            case "map-check":
                // (a player's copy of the lobby's map: the hash it was compared with must still be the lobby's)
                if (Lobby.State == "lobby" && m["hash"]?.GetValue<string>() == (Lobby.Skirmish?.MapHash ?? ""))
                {
                    var mp = m["problem"]?.GetValue<string>();
                    if (mp == null && p.Tx != null) { Say($"{p.Name} has the map now ({p.Tx.Data.Length} bytes sent)"); p.Tx = null; }
                    if (mp != p.MapProblem) { p.MapProblem = mp; if (mp != null) p.Ready = false; SyncLobby(); }
                }
                break;
            case "coop-player":
                if (Lobby.State != "lobby") break;
                int pl = m["player"]?.GetValue<int>() ?? 0;
                if (Lobby.CoopPlayers == null || Lobby.CoopPlayers.Any(c => c.Player == pl)) { p.Player = pl; p.Ready = p.IsHost; SyncLobby(); }
                break;
            case "coop-seat":
            {
                // co-op campaign mode: a player takes a free human seat
                if (Lobby.State != "lobby" || Lobby.Coop == null) break;
                int seat = m["seat"]?.GetValue<int>() ?? 0;
                if (Lobby.Coop.Seats.Any(x => x.Seat == seat && x.Kind == "human") && !Players.Any(x => x != p && x.Seat == seat))
                {
                    // (the colour was the seat's human's: the old seat and the new one start without one)
                    foreach (var x in Lobby.Coop.Seats) if (x.Seat == seat || x.Seat == p.Seat) x.Color = null;
                    p.Seat = seat; p.Ready = p.IsHost; SyncLobby();
                }
                break;
            }
            case "coop-tribe":
            {
                // a player's faction of its own seat
                if (Lobby.State != "lobby" || Lobby.Coop == null || p.Seat <= 0) break;
                var tr = m["tribe"]?.GetValue<string>() ?? "";
                var st = Lobby.Coop.Seats.FirstOrDefault(x => x.Seat == p.Seat);
                if (st != null) { st.Tribe = tr; p.Ready = p.IsHost; SyncLobby(); }
                break;
            }
            case "coop-color":
            {
                // a player's colour of its own seat: not one of the mission's other players or of another human seat
                // (docs/CAMPAIGN.md "Your colour"); "" the game's
                if (Lobby.State != "lobby" || Lobby.Coop == null || p.Seat <= 0) break;
                var col = m["color"]?.GetValue<string>() ?? "";
                var st = Lobby.Coop.Seats.FirstOrDefault(x => x.Seat == p.Seat && x.Kind == "human");
                if (st == null) break;
                if (col.Length == 0) { st.Color = null; SyncLobby(); break; }
                if (Sim.Rules.Colors.ColorById(col) == null) break;
                var who = Sim.Campaign.MissionColors.Blocker(col, Lobby.Coop.TakenFor(p.Seat, Lobby.Players));
                if (who != null) { Say($"{p.Name}: the colour {col} is {who.Name}'s"); break; }
                st.Color = col; SyncLobby();
                break;
            }
            case "start": if (p.IsHost && Lobby.State == "lobby") RequestStart(m["profile"] as JsonObject); break;
            case "coop-army":
            {
                // a human's army for its own seat (before the start, or in the state "armies"); null: the level's army
                if ((Lobby.State != "lobby" && Lobby.State != "armies") || Lobby.Coop is not { ArmyBudget: true } c || p.Seat <= 0) break;
                var st = c.Seats.FirstOrDefault(x => x.Seat == p.Seat && x.Kind == "human");
                if (st == null || !c.BuysArmy(p.Seat)) break;
                var army = m["army"] is JsonObject ao ? NetProtocol.FromNode<Army>(ao) : null;
                if (army != null && army.Cards.Count > 200) break;            // (no sensible army)
                _armies[p.Seat] = (army, p.Id, st.Tribe, Lobby.Campaign?.Mission ?? -1);
                Say($"{p.Name}: the army of seat {p.Seat} {(army == null ? "is the level's" : $"bought ({army.Cards.Count} cards)")}");
                SyncLobby();
                break;
            }
            case "army-cancel":
                // the host goes back to the lobby from the state "armies"
                if (p.IsHost && Lobby.State == "armies") { Lobby.State = "lobby"; Lobby.ArmyWait = null; _armyProfile = null; SyncLobby(); Announce("back to the lobby"); }
                break;
            case "loaded":
                p.Loaded = true;
                p.AckStep = Math.Max(p.AckStep, m["step"]?.GetValue<long>() ?? 0);
                if (Lobby.State == "loading" && Players.All(x => x.Loaded)) Run();
                break;
            case "cmd": Command(p, m); break;
            case "ack": p.AckStep = Math.Max(p.AckStep, m["step"]?.GetValue<long>() ?? 0); break;
            case "hash": Hash(p, m); break;
            case "dump": Dump(p, m); break;
            case "pause":
                if (Lobby.State != "running") break;
                bool on = m["on"]?.GetValue<bool>() ?? true;
                if (on == PausedByPlayer) break;
                PausedByPlayer = on; PausedBy = on ? p.Name : null;
                Broadcast(Msg("pause", ("on", on), ("by", p.Name)));
                Say(on ? $"{p.Name} paused the game" : $"{p.Name} resumed the game");
                break;
            case "scene-start": SceneStart(p, m); break;
            case "scene": SceneVote(p, m); break;
            case "drop": if (p.IsHost) { var d = _peers.FirstOrDefault(x => x.Id == (m["peer"]?.GetValue<int>() ?? -1)); if (d != null && d != p) Drop(d, "dropped by the host"); } break;
            case "kick": if (p.IsHost && Lobby.State == "lobby") { var k = _peers.FirstOrDefault(x => x.Id == (m["peer"]?.GetValue<int>() ?? -1)); if (k != null && k != p) { k.Conn.Send(Msg("reject", ("reason", "The host removed you from the game."))); k.Conn.Close("kicked"); } } break;
            case "leave": p.Conn.Close("left the game"); Lost(p, "left the game", drop: true); break;
            case "end": if (p.IsHost) EndGame($"{p.Name} ended the game"); break;
        }
    }

    // ---------------------------------------------------------------- joining
    void Hello(Peer p, JsonObject m)
    {
        int proto = m["protocol"]?.GetValue<int>() ?? 0;
        string name = (m["name"]?.GetValue<string>() ?? "").Trim();
        if (name.Length == 0) name = "Player";
        if (name.Length > 24) name = name[..24];
        void Reject(string why) { p.Conn.Send(Msg("reject", ("reason", why))); p.Conn.Close("refused: " + why); _peers.Remove(p); Say($"refused {name} ({p.Conn.Remote}): {why}"); }
        if (proto != NetProtocol.Version) { Reject($"Different versions! (network protocol {proto}, the host's {NetProtocol.Version})"); return; }
        bool host = m["token"]?.GetValue<string>() == HostToken && !_peers.Any(x => x.IsHost);
        string build = m["build"]?.GetValue<string>() ?? "";
        // a player who lost the connection comes back: the same name, the game running
        if (Lobby.State is "running" or "loading")
        {
            var old = _peers.FirstOrDefault(x => x != p && x.Name == name && !x.Connected && !x.Dropped && !x.Spectator);
            if (old == null) { Reject("The game has already started."); return; }
            Rejoin(old, p);
            return;
        }
        if (Lobby.State == "armies") { Reject("The players are choosing their armies: the game is about to start."); return; }
        if (Lobby.State != "lobby") { Reject("The game is over."); return; }
        if (!host && Players.Count() >= NetProtocol.MaxPlayers) { Reject("The game is full."); return; }
        // a name of its own
        string unique = name; int k = 2;
        while (_peers.Any(x => x != p && x.Name == unique)) unique = $"{name} ({k++})";
        p.Name = unique; p.IsHost = host; p.Build = build; p.Spectator = host && Dedicated;
        if (build != Build && Build != "") p.Problem = $"another version of the game ({build}; the host has {Build})";
        else if (m["configuration"]?.GetValue<string>() is { } cfg && Lobby.Configuration != "" && cfg != Lobby.Configuration) p.Problem = $"the configuration {cfg} is chosen (the host plays {Lobby.Configuration})";
        else if (Fingerprint != "" && m["fingerprint"]?.GetValue<string>() is { } fp && fp != Fingerprint) p.Problem = "other game data than the host's (prepare the same installation and configuration)";
        if (host) p.Ready = true;
        p.Player = Lobby.CoopPlayers?.FirstOrDefault(c => c.Main)?.Player ?? 0;
        Lobby.Peers.Add(new LobbyPeer { Id = p.Id, Name = p.Name, Host = host, Spectator = p.Spectator });
        AssignSlots();
        p.Conn.Send(Msg("welcome", ("you", p.Id), ("host", host), ("lobby", NetProtocol.ToNode(Lobby))));
        SyncLobby();
        Announce($"{p.Name} joined the game{(p.Problem != null ? " - " + p.Problem : "")}");
    }

    void Rejoin(Peer old, Peer p)
    {
        _peers.Remove(p);
        old.Conn = p.Conn;
        old.Connected = true; old.LostAt = -1; old.Loaded = false; old.AckStep = 0; old.LastHeard = _now;
        int seat = Start!.Seats!.FindIndex(s => s.Peer == old.Id);
        old.Conn.Send(Msg("welcome", ("you", old.Id), ("host", false), ("lobby", NetProtocol.ToNode(Lobby))));
        old.Conn.Send(Msg("start", ("start", NetProtocol.ToNode(Start)), ("seat", seat), ("rejoin", true)));
        var all = new JsonArray();
        foreach (var turn in Turns) all.Add(turn.DeepClone());
        old.Conn.Send(Msg("turns", ("list", all)));
        Announce($"{old.Name} is back - catching up to step {LastIssued}");
    }

    // ---------------------------------------------------------------- the lobby
    /// <summary>the host's own changes (mode, map, mission, options ...) - the host's UI or a server's setup file</summary>
    public void SetLobby(LobbyState? s)
    {
        if (s == null) return;
        // (the seats' armies are the host's own state, not the lobby's sender's: SyncArmies)
        if (s.Coop != null) foreach (var st in s.Coop.Seats) { var cur = Lobby.Coop?.Seats.FirstOrDefault(x => x.Seat == st.Seat); st.Army = cur?.Army; st.ArmyDone = cur?.ArmyDone ?? false; }
        bool changed = NetProtocol.ToNode(s.Skirmish)?.ToJsonString() != NetProtocol.ToNode(Lobby.Skirmish)?.ToJsonString()
                       || NetProtocol.ToNode(s.Campaign)?.ToJsonString() != NetProtocol.ToNode(Lobby.Campaign)?.ToJsonString() || s.Mode != Lobby.Mode;
        // another map: every player compares its copy again (map-check)
        if (s.Skirmish?.Map != Lobby.Skirmish?.Map || s.Skirmish?.MapHash != Lobby.Skirmish?.MapHash) foreach (var p in _peers) { p.MapProblem = null; p.Tx = null; }
        Lobby.Mode = s.Mode; Lobby.Name = s.Name; Lobby.Configuration = s.Configuration; Lobby.Skirmish = s.Skirmish?.Clone();
        changed |= s.CoopMode != Lobby.CoopMode || NetProtocol.ToNode(s.Coop)?.ToJsonString() != NetProtocol.ToNode(Lobby.Coop)?.ToJsonString();
        Lobby.CoopMode = s.CoopMode == "campaign" ? "campaign" : "shared"; Lobby.Coop = s.Coop;
        Lobby.Campaign = s.Campaign; Lobby.CoopPlayers = s.CoopPlayers; Lobby.SceneRule = s.SceneRule is "host" ? "host" : "all";
        Lobby.InputDelay = Math.Clamp(s.InputDelay, 1, 20);
        if (changed) foreach (var p in _peers) if (!p.IsHost) p.Ready = false;      // (the setup changed: everybody looks again)
        // a co-op player that is no longer offered: back to the mission's human player
        var main = Lobby.CoopPlayers?.FirstOrDefault(c => c.Main)?.Player ?? 0;
        foreach (var p in _peers) if (Lobby.CoopPlayers != null && !Lobby.CoopPlayers.Any(c => c.Player == p.Player)) p.Player = main;
        AssignSlots();
        SyncLobby();
    }

    /// <summary>a player's own slot: tribe, team, colour, army, start position (the host's UI checks the setup)</summary>
    void SetSlot(Peer p, JsonObject m)
    {
        var setup = Lobby.Skirmish;
        if (setup == null || p.Slot < 0 || p.Slot >= setup.Slots.Count) return;
        var sl = setup.Slots[p.Slot];
        if (m["tribe"]?.GetValue<string>() is { } tr) sl.Tribe = tr;
        if (m["team"] is JsonValue tm) sl.Team = Math.Clamp(tm.GetValue<int>(), 1, NetProtocol.MaxPlayers);
        if (m["color"]?.GetValue<string>() is { } co && (co == "none" || PwRemake.Sim.Rules.Colors.ColorById(co) != null)) sl.Color = co;   // (a palette id or "#rrggbb")
        if (m["hq"] is JsonValue hq) sl.Hq = hq.GetValue<int>();
        if (m["preset"]?.GetValue<string>() is { } pr) sl.Preset = pr;
        if (m.ContainsKey("army")) { sl.Army = NetProtocol.FromNode<Army>(m["army"]); sl.ArmyTribe = m["armyTribe"]?.GetValue<string>(); sl.ArmyPoints = m["armyPoints"]?.GetValue<double>(); }
        p.Ready = false;
        SyncLobby();
    }

    /// <summary>skirmish: every player on a human slot of its own (its Owner), free human slots open again, a newcomer on
    /// the first open slot</summary>
    void AssignSlots()
    {
        AssignSeats();
        var setup = Lobby.Skirmish;
        if (setup == null) return;
        var names = Players.Select(p => p.Name).ToHashSet();
        foreach (var sl in setup.Slots) if (sl.Type == "human" && (sl.Owner == null || !names.Contains(sl.Owner))) { sl.Type = "open"; sl.Owner = null; }
        foreach (var p in Players)
        {
            int i = setup.Slots.FindIndex(sl => sl.Type == "human" && sl.Owner == p.Name);
            if (i < 0)
            {
                i = setup.Slots.FindIndex(sl => sl.Type == "open");
                if (i >= 0) { setup.Slots[i].Type = "human"; setup.Slots[i].Owner = p.Name; if (setup.Slots[i].Tribe == "Random") setup.Slots[i].Tribe = "Random"; }
            }
            p.Slot = i;
        }
    }

    /// <summary>co-op campaign mode: every player on a human seat of its own (the host first), a seat that is no longer a
    /// human's frees its player</summary>
    void AssignSeats()
    {
        var c = Lobby.Coop;
        if (c == null) { foreach (var p in _peers) p.Seat = 0; return; }
        foreach (var p in Players.OrderBy(x => x.IsHost ? 0 : 1).ThenBy(x => x.Id))
        {
            bool ok = p.Seat > 0 && c.Seats.Any(x => x.Seat == p.Seat && x.Kind == "human") && !Players.Any(o => o != p && o.Seat == p.Seat && (o.IsHost || o.Id < p.Id));
            if (ok) continue;
            p.Seat = c.Seats.Where(x => x.Kind == "human" && !Players.Any(o => o != p && o.Seat == x.Seat)).Select(x => x.Seat).FirstOrDefault();
        }
    }

    /// <summary>the seats' armies into the lobby: a choice stands while its seat is still a human seat of the same player,
    /// faction and mission</summary>
    void SyncArmies()
    {
        var c = Lobby.Coop;
        if (c == null) { _armies.Clear(); return; }
        int mission = Lobby.Campaign?.Mission ?? -1;
        foreach (var (seat, a) in _armies.ToList())
        {
            var st = c.Seats.FirstOrDefault(x => x.Seat == seat);
            var who = Players.FirstOrDefault(x => x.Seat == seat);
            if (!c.BuysArmy(seat) || st == null || st.Kind != "human" || who == null || who.Id != a.Peer || st.Tribe != a.Tribe || mission != a.Mission) _armies.Remove(seat);
        }
        foreach (var st in c.Seats)
        {
            bool has = _armies.TryGetValue(st.Seat, out var a);
            st.ArmyDone = has; st.Army = has ? a.Army : null;
        }
    }

    void SyncLobby()
    {
        SyncArmies();
        foreach (var lp in Lobby.Peers)
        {
            var p = _peers.FirstOrDefault(x => x.Id == lp.Id);
            if (p == null) continue;
            lp.Name = p.Name ?? ""; lp.Ready = p.Ready; lp.Slot = p.Slot; lp.Player = p.Player; lp.Seat = p.Seat; lp.Ping = p.Ping; lp.Problem = p.Problem ?? (Lobby.Mode == "skirmish" ? p.MapProblem : null); lp.Host = p.IsHost; lp.Spectator = p.Spectator;
            lp.MapProgress = p.Tx != null && Lobby.Mode == "skirmish" ? Math.Round((double)p.Tx.Acked / Math.Max(1, p.Tx.Data.Length), 3) : null;
        }
        Lobby.Peers.RemoveAll(lp => !_peers.Any(x => x.Id == lp.Id && x.Name != null));
        Broadcast(Msg("lobby", ("lobby", NetProtocol.ToNode(Lobby))));
    }

    // ---------------------------------------------------------------- start
    /// <summary>the host's Start: a co-op mission with an army budget first waits for the seats' armies (the state
    /// "armies": every human picks on its own machine, at most ArmyTimeout seconds), anything else starts at once</summary>
    void RequestStart(JsonObject? profile)
    {
        var problem = LobbyStart.Problem(Lobby);
        if (problem != null) { var h = _peers.FirstOrDefault(x => x.IsHost); h?.Conn.Send(Msg("sys", ("text", "Cannot start: " + problem))); return; }
        SyncArmies();
        if (Lobby.Mode == "coop" && Lobby.CoopMode == "campaign" && Lobby.Coop?.ArmiesOpen.Any() == true)
        {
            Lobby.State = "armies";
            _armyDeadline = _now + ArmyTimeout;
            _armyProfile = profile;
            Lobby.ArmyWait = (int)Math.Ceiling(ArmyTimeout);
            SyncLobby();
            Announce($"choosing the armies (seats {string.Join(", ", Lobby.Coop.ArmiesOpen.Select(x => x.Seat))}; at most {ArmyTimeout:0} s)");
            return;
        }
        BeginGame(profile);
    }

    /// <summary>the state "armies": starts when every human seat has its army or the time is up (the level's army for the
    /// seats still choosing); back to the lobby when the lobby no longer allows a start (a player left)</summary>
    void ArmiesTick()
    {
        var problem = LobbyStart.Problem(Lobby);
        if (problem != null) { Lobby.State = "lobby"; Lobby.ArmyWait = null; _armyProfile = null; SyncLobby(); Announce("back to the lobby: " + problem); return; }
        var open = Lobby.Coop?.ArmiesOpen.ToList() ?? new();
        bool timeUp = _now >= _armyDeadline;
        if (open.Count == 0 || timeUp)
        {
            if (timeUp && open.Count > 0) Announce($"time is up: seat{(open.Count > 1 ? "s" : "")} {string.Join(", ", open.Select(x => x.Seat))} play{(open.Count > 1 ? "" : "s")} the level's army");
            var profile = _armyProfile;
            Lobby.State = "lobby"; Lobby.ArmyWait = null; _armyProfile = null;
            BeginGame(profile);
            return;
        }
        int left = Math.Max(0, (int)Math.Ceiling(_armyDeadline - _now));
        if (left != Lobby.ArmyWait) { Lobby.ArmyWait = left; SyncLobby(); }
    }

    void BeginGame(JsonObject? profile)
    {
        var problem = LobbyStart.Problem(Lobby);
        if (problem != null) { var h = _peers.FirstOrDefault(x => x.IsHost); h?.Conn.Send(Msg("sys", ("text", "Cannot start: " + problem))); return; }
        Dictionary<string, string>? prof = null;
        if (profile != null) { prof = new(); foreach (var (k, v) in profile) prof[k] = v?.ToString() ?? ""; }
        Start = LobbyStart.Make(Lobby, prof);
        Lobby.State = "loading";
        foreach (var p in _peers) { p.Loaded = false; p.AckStep = BaseStep; }
        foreach (var p in Live)
        {
            int seat = Start.Seats!.FindIndex(s => s.Peer == p.Id);
            p.Conn.Send(Msg("start", ("start", NetProtocol.ToNode(Start)), ("seat", seat)));
        }
        SyncLobby();
        Say($"the game starts: {Start}");
    }

    /// <summary>everybody has loaded: the first turns (the input delay) at once, then one every step</summary>
    void Run()
    {
        Lobby.State = "running";
        LastIssued = BaseStep;
        _acc = 0;
        for (int i = 0; i < Lobby.InputDelay; i++) Issue();
        SyncLobby();
        Broadcast(Msg("go", ("step", LastIssued)));
        Say("all loaded - the game runs");
    }

    // ---------------------------------------------------------------- turns
    void Command(Peer p, JsonObject m)
    {
        if (Lobby.State != "running" && Lobby.State != "loading") return;
        if (m["c"] is not JsonObject c) return;
        int seat = Start?.Seats?.FindIndex(s => s.Peer == p.Id) ?? -1;
        if (seat < 0) return;                                                   // (a spectator gives no orders)
        int player = c["c"]?["Player"]?.GetValue<int>() ?? -1;
        string type = c["t"]?.GetValue<string>() ?? "";
        // a player commands its own player only; the scenes' ends are the host's (votes); the debug tools only with debug options
        if (player != Start!.Seats![seat].Player) { L.Warn($"{p.Name}: a command for player {player} refused (commands player {Start.Seats[seat].Player})"); return; }
        if (type == "SceneEndCommand") return;
        if (type == "DebugToolCommand" && Start.Debug == null) { L.Warn($"{p.Name}: a debug tool without debug options refused"); return; }
        // the remake's own campaigns: the briefing's begin and the command powers are the players'; win / lose / objective
        // done are debug tools
        if (type == "SequelCommand" && Start.Debug == null && c["c"]?["What"]?.GetValue<string>() is not ("begin" or "power")) { L.Warn($"{p.Name}: a sequel debug tool without debug options refused"); return; }
        if (type == "UiStateCommand" && seat != 0) return;
        _pending.Add(new JsonObject { ["p"] = p.Id, ["q"] = m["q"]?.DeepClone(), ["c"] = c.DeepClone() });
    }

    void RunTurns(double dt)
    {
        // a lost player not back in time is dropped
        foreach (var p in _peers.Where(x => !x.Connected && !x.Dropped && x.LostAt >= 0 && _now - x.LostAt > NetProtocol.ReconnectWait).ToList()) Drop(p, "did not come back in time");
        var wait = new List<string>();
        foreach (var p in Players)
            if (!p.Connected) wait.Add($"{p.Name} (connection lost, {Math.Max(0, NetProtocol.ReconnectWait - (_now - p.LostAt)):0} s)");
            else if (!p.Loaded) wait.Add($"{p.Name} (loading)");
            else if (p.AckStep < LastIssued - NetProtocol.MaxLag) wait.Add(p.Name!);
        var w = string.Join(", ", wait);
        if (w != _waitingFor) { _waitingFor = w; Broadcast(Msg("wait", ("players", w))); }
        if (PausedByPlayer || _sceneHold != null || wait.Count > 0) { _acc = 0; return; }
        _acc += dt;
        int n = 0;
        while (_acc >= Sim.World.World.STEP && n++ < 10) { _acc -= Sim.World.World.STEP; Issue(); }
        if (_acc > Sim.World.World.STEP) _acc = 0;
    }

    void Issue()
    {
        var cmds = new JsonArray();
        foreach (var c in _pending) cmds.Add(c);
        _pending.Clear();
        var turn = Msg("turn", ("s", LastIssued + 1), ("c", cmds));
        LastIssued++;
        Turns.Add(turn);
        Broadcast(turn);
    }

    /// <summary>a command of the host itself (the end of a scene) into the next turn</summary>
    void HostCommand(Sim.Commands.Command c)
    {
        _pending.Add(new JsonObject { ["p"] = 0, ["q"] = 0, ["c"] = Sim.Commands.CommandCodec.Encode(c) });
    }

    // ---------------------------------------------------------------- scenes (docs/MULTIPLAYER.md "Scenes")
    void SceneStart(Peer p, JsonObject m)
    {
        var key = (m["kind"]?.GetValue<string>() ?? "sequence", m["token"]?.GetValue<int>() ?? 0);
        if (_scenesDone.Contains(key)) return;
        if (!_votes.ContainsKey(key)) _votes[key] = (new HashSet<int>(), false);
        // a cutscene holds the game (single player: the mission UI pauses it) until it ends for everybody
        if (key.Item1 == "sequence" && _sceneHold == null) { _sceneHold = key; L.Info($"cutscene {key.Item2} on show (step {m["step"]}): the turns wait for the players"); }
    }

    void SceneVote(Peer p, JsonObject m)
    {
        var key = (m["kind"]?.GetValue<string>() ?? "sequence", m["token"]?.GetValue<int>() ?? 0);
        if (_scenesDone.Contains(key)) return;
        (HashSet<int> Votes, bool Timeline) v = _votes.TryGetValue(key, out var have) ? have : (new HashSet<int>(), false);
        v.Votes.Add(p.Id);
        if (p.IsHost || !v.Votes.Any(id => _peers.Any(x => x.Id == id && x.IsHost))) v.Timeline = v.Timeline || (m["timeline"]?.GetValue<bool>() ?? false);
        _votes[key] = v;
        CheckVotes();
    }

    void CheckVotes()
    {
        foreach (var (key, v) in _votes.ToList())
        {
            if (_scenesDone.Contains(key)) continue;
            var voters = Players.Where(p => p.Connected).ToList();
            var host = voters.FirstOrDefault(p => p.IsHost);
            bool done = Lobby.SceneRule == "host" && host != null ? v.Votes.Contains(host.Id) : voters.All(p => v.Votes.Contains(p.Id));
            if (!done) continue;
            _scenesDone.Add(key);
            _votes.Remove(key);
            int player = Start?.Seats?.FirstOrDefault()?.Player ?? 0;
            HostCommand(new Sim.Commands.SceneEndCommand { Player = player, Kind = key.Kind, Token = key.Kind == "dialog" ? key.Token : 0, Timeline = v.Timeline });
            if (_sceneHold == key) _sceneHold = null;
            L.Info($"{key.Kind} {key.Token} ends for everybody ({v.Votes.Count} of {voters.Count} done)");
            Broadcast(Msg("scene-end", ("kind", key.Kind), ("token", key.Token)));
        }
    }

    // ---------------------------------------------------------------- hashes, desyncs
    void Hash(Peer p, JsonObject m)
    {
        long step = m["step"]?.GetValue<long>() ?? -1;
        if (step < 0 || m["h"]?.GetValue<string>() is not { } hs) return;
        ulong h = ulong.Parse(hs, global::System.Globalization.NumberStyles.HexNumber, global::System.Globalization.CultureInfo.InvariantCulture);
        p.AckStep = Math.Max(p.AckStep, step);
        if (!_hashes.TryGetValue(step, out var at)) _hashes[step] = at = new();
        at[p.Id] = (h, m["parts"] as JsonObject);
        while (_hashes.Count > 64) _hashes.Remove(_hashes.Keys.First());
        if (DesyncAt >= 0) return;
        var distinct = at.Values.Select(x => x.Hash).Distinct().ToList();
        if (distinct.Count < 2) return;
        // a desync: who has which hash, which parts differ
        DesyncAt = step;
        var groups = at.GroupBy(kv => kv.Value.Hash).Select(g => string.Join(", ", g.Select(kv => _peers.FirstOrDefault(x => x.Id == kv.Key)?.Name ?? "?")) + $": {g.Key:x16}").ToList();
        var a = at.Values.First(); var b = at.Values.First(x => x.Hash != a.Hash);
        var parts = new List<string>();
        if (a.Parts != null && b.Parts != null)
            foreach (var (k, v) in a.Parts) if (b.Parts[k]?.ToString() != v?.ToString()) parts.Add(k);
        var detail = $"step {step} ({SaveHeader.Clock(step * Sim.World.World.STEP)}): {string.Join(" | ", groups)}" + (parts.Count > 0 ? $"; differing: {string.Join(", ", parts)}" : "");
        L.Error("DESYNC " + detail);
        Info?.Invoke("DESYNC " + detail);
        _dumpStep = LastIssued + 1;
        Broadcast(Msg("desync", ("step", step), ("detail", detail), ("dumpAt", _dumpStep)));
    }

    void Dump(Peer p, JsonObject m)
    {
        if (m["step"]?.GetValue<long>() != _dumpStep) return;
        _dumps[p.Id] = m["text"]?.GetValue<string>() ?? "";
        if (DumpDir != null)
            try
            {
                var dir = Path.Combine(DumpDir, $"desync_{DesyncAt}");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"peer{p.Id}_{Safe(p.Name ?? "")}.txt"), _dumps[p.Id]);
            }
            catch (Exception e) { L.Warn("desync dump: " + e.Message); }
        if (!Players.Where(x => x.Connected).All(x => _dumps.ContainsKey(x.Id))) return;
        // all dumps: the first lines that differ from the first machine's
        var refId = _dumps.Keys.First();
        var refLines = _dumps[refId].Split('\n');
        var report = new StringBuilder($"desync at step {DesyncAt}, dumps of step {_dumpStep}:\n");
        foreach (var (id, text) in _dumps)
        {
            if (id == refId) continue;
            var lines = text.Split('\n');
            int shown = 0;
            for (int i = 0; i < Math.Max(lines.Length, refLines.Length) && shown < 12; i++)
            {
                string x = i < refLines.Length ? refLines[i] : "(none)", y = i < lines.Length ? lines[i] : "(none)";
                if (x == y) continue;
                report.Append($"  {Name(refId)}: {x}\n  {Name(id)}: {y}\n");
                shown++;
            }
            if (shown == 0) report.Append($"  {Name(id)}: the dump is the same as {Name(refId)}'s\n");
        }
        var r = report.ToString();
        L.Error(r);
        Info?.Invoke(r);
        if (DumpDir != null) try { File.WriteAllText(Path.Combine(DumpDir, $"desync_{DesyncAt}", "report.txt"), r); } catch (Exception) { }
        Broadcast(Msg("desync-report", ("text", r)));
    }
    string Name(int id) => _peers.FirstOrDefault(x => x.Id == id)?.Name ?? $"peer {id}";
    static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    // ---------------------------------------------------------------- leaving
    void Lost(Peer p, string why, bool drop = false)
    {
        if (!p.Connected) { if (drop) Drop(p, why); return; }
        p.Connected = false;
        if (p.Name == null) { _peers.Remove(p); return; }
        if (Lobby.State is "lobby" or "armies")
        {
            _peers.Remove(p);
            AssignSlots();
            SyncLobby();
            Announce($"{p.Name} left the game");
            return;
        }
        if (p.IsHost && !Dedicated) { EndGame($"the host ({p.Name}) left the game"); return; }
        p.LostAt = _now;
        Broadcast(Msg("left", ("peer", p.Id), ("name", p.Name), ("why", why)));
        Say($"{p.Name}: {why}");
        if (drop) Drop(p, why);
        CheckVotes();
    }

    void Drop(Peer p, string why)
    {
        if (p.Dropped) return;
        p.Dropped = true;
        if (p.Connected) { p.Conn.Close(why); p.Connected = false; }
        Announce($"{p.Name} left the game ({why}); the game goes on without them");
        CheckVotes();
    }

    void EndGame(string why)
    {
        if (Lobby.State == "over") return;
        Lobby.State = "over";
        Broadcast(Msg("end", ("why", why)));
        Say("game over: " + why);
    }

    /// <summary>the turns issued so far as a saved game of the dedicated server (the start, every command at its step;
    /// no hashes: the server runs no world) - loading it replays the game (docs/MULTIPLAYER.md "Saves and replays")</summary>
    public SaveGame? Replay()
    {
        if (Start == null) return null;
        var s = new SaveGame { Start = Start, Flags = SimFlags.Game, Steps = LastIssued };
        foreach (var turn in Turns)
        {
            long step = turn["s"]!.GetValue<long>();
            foreach (var c in turn["c"]!.AsArray())
            {
                var cmd = (JsonObject)c!["c"]!.DeepClone();
                cmd["c"]!["Step"] = step;
                s.Commands.Add(cmd);
            }
        }
        s.Header.Mode = Start.Mode.ToString(); s.Header.Configuration = Start.Configuration; s.Header.Mission = Start.Mission; s.Header.MissionFolder = Start.MissionFolder;
        s.Header.GameTime = LastIssued * Sim.World.World.STEP; s.Header.Kind = "replay";
        s.Header.Date = DateTime.Now.ToString("s", global::System.Globalization.CultureInfo.InvariantCulture);
        return s;
    }

    public int PlayerCount => Players.Count();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener.Stop(); } catch (Exception) { }
        foreach (var p in _peers) p.Conn.Close("the host closed the game");
    }
}
