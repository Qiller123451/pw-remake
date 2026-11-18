// A player's side of a networked game (docs/MULTIPLAYER.md): the connection to the host, the lobby as the host sends
// it, the chat, and in the game the turns - the commands of every player for each step, in the host's order. The game
// (or a test, or a bot) calls Poll() regularly on its thread; LockstepGame runs the world on the turns.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;

namespace PwRemake.Net;

/// <summary>A line of the chat: who, what, to whom ("all" | "allies" | "enemies"), or a line of the host ("sys").</summary>
public sealed record ChatLine(int From, string Name, string Text, string To);

public sealed class NetClient : IDisposable
{
    static readonly Logger L = Log.For("Net.Client");
    public readonly string Name;
    NetConnection? _conn;
    /// <summary>this machine's peer id at the host (-1: not welcomed yet)</summary>
    public int Me { get; private set; } = -1;
    public bool IsHost { get; private set; }
    /// <summary>the lobby as the host last sent it</summary>
    public LobbyState? Lobby { get; private set; }
    public bool Connected => _conn != null && !_conn.Closed;
    /// <summary>why the host refused this machine or the connection ended (null: fine)</summary>
    public string? Error { get; private set; }
    public double Ping { get; private set; }

    // ---- the game
    public GameStart? Start { get; private set; }
    /// <summary>this machine's seat (GameStart.Seats index; -1: a spectator)</summary>
    public int Seat { get; private set; } = -1;
    /// <summary>this machine lost its connection during the game and was let in again: it replays the turns so far</summary>
    public bool Rejoining { get; private set; }
    /// <summary>the game runs (every machine has loaded)</summary>
    public bool Running { get; private set; }
    readonly Dictionary<long, JsonArray> _turns = new();
    /// <summary>the last step whose turn arrived (every turn up to it is here)</summary>
    public long LastTurn { get; private set; }
    long _seq;
    readonly Dictionary<long, Command> _sent = new();
    public bool HostPaused { get; private set; }
    public string? PausedBy { get; private set; }
    /// <summary>whom the host waits for ("" nobody)</summary>
    public string Waiting { get; private set; } = "";
    /// <summary>a desync: the step, what differed, the step every machine dumps its world at (-1: none)</summary>
    public long DesyncStep { get; private set; } = -1;
    public string? DesyncDetail { get; private set; }
    public long DumpAt { get; private set; } = -1;
    public string? DesyncReport { get; private set; }
    public bool Ended { get; private set; }
    public string? EndReason { get; private set; }
    public readonly List<ChatLine> Chat = new();

    public event Action<LobbyState>? LobbyChanged;
    public event Action<ChatLine>? ChatReceived;
    /// <summary>the host started the game: build the world of Start, then call Loaded()</summary>
    public event Action<GameStart, int>? Started;
    public event Action? Go;
    public event Action<string>? Disconnected;
    public event Action<string>? DesyncDetected;
    public event Action<string, int>? SceneEnded;

    public NetClient(string name) { Name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim(); }

    /// <summary>connects and says hello (blocking up to the timeout); throws with a readable message</summary>
    public void Connect(string host, int port, string configuration, string build, string fingerprint, string? hostToken = null)
    {
        _conn = NetConnection.Connect(host, port);
        var m = NetProtocol.Msg("hello");
        m["name"] = Name; m["protocol"] = NetProtocol.Version; m["build"] = build; m["configuration"] = configuration; m["fingerprint"] = fingerprint;
        if (hostToken != null) m["token"] = hostToken;
        _conn.Send(m);
    }

    void Send(JsonObject m) => _conn?.Send(m);
    static JsonObject Msg(string t, params (string K, JsonNode? V)[] kv)
    {
        var m = NetProtocol.Msg(t);
        foreach (var (k, v) in kv) m[k] = v;
        return m;
    }

    // ======================================================================================== messages
    public void Poll()
    {
        if (_conn == null) return;
        while (_conn.Inbox.TryDequeue(out var m))
        {
            try { Handle(m); }
            catch (Exception e) { L.Error($"message {m["t"]}", e); }
        }
        if (_conn.Closed && Error == null && !Ended)
        {
            Error = _conn.Reason ?? "connection lost";
            Disconnected?.Invoke(Error);
        }
    }

    void Handle(JsonObject m)
    {
        switch (m["t"]?.GetValue<string>())
        {
            case "welcome":
                Me = m["you"]!.GetValue<int>(); IsHost = m["host"]?.GetValue<bool>() ?? false;
                SetLobby(m["lobby"]);
                break;
            case "reject":
                Error = m["reason"]?.GetValue<string>() ?? "refused";
                _conn?.Close(Error);
                Disconnected?.Invoke(Error);
                break;
            case "lobby": SetLobby(m["lobby"]); break;
            case "ping": Send(Msg("pong")); break;
            case "chat":
            {
                var line = new ChatLine(m["from"]?.GetValue<int>() ?? 0, m["name"]?.GetValue<string>() ?? "", m["text"]?.GetValue<string>() ?? "", m["to"]?.GetValue<string>() ?? "all");
                Chat.Add(line);
                ChatReceived?.Invoke(line);
                break;
            }
            case "sys":
            {
                var line = new ChatLine(0, "", m["text"]?.GetValue<string>() ?? "", "sys");
                Chat.Add(line);
                ChatReceived?.Invoke(line);
                break;
            }
            case "start":
                Start = NetProtocol.FromNode<GameStart>(m["start"]);
                Seat = m["seat"]?.GetValue<int>() ?? -1;
                Rejoining = m["rejoin"]?.GetValue<bool>() ?? false;
                _turns.Clear(); LastTurn = 0; Running = Rejoining;
                if (Start != null) Started?.Invoke(Start, Seat);
                break;
            case "go": Running = true; Go?.Invoke(); break;
            case "turn": AddTurn(m); break;
            case "turns": foreach (var t in m["list"]!.AsArray()) AddTurn((JsonObject)t!); break;
            case "pause": HostPaused = m["on"]?.GetValue<bool>() ?? false; PausedBy = HostPaused ? m["by"]?.GetValue<string>() : null; break;
            case "wait": Waiting = m["players"]?.GetValue<string>() ?? ""; break;
            case "desync":
                DesyncStep = m["step"]!.GetValue<long>(); DesyncDetail = m["detail"]?.GetValue<string>(); DumpAt = m["dumpAt"]?.GetValue<long>() ?? -1;
                L.Error("DESYNC " + DesyncDetail);
                DesyncDetected?.Invoke(DesyncDetail ?? "");
                break;
            case "desync-report": DesyncReport = m["text"]?.GetValue<string>(); break;
            case "scene-end": SceneEnded?.Invoke(m["kind"]?.GetValue<string>() ?? "", m["token"]?.GetValue<int>() ?? 0); break;
            case "left":
            {
                var line = new ChatLine(0, "", $"{m["name"]} - {m["why"]}", "sys");
                Chat.Add(line); ChatReceived?.Invoke(line);
                break;
            }
            case "end":
                Ended = true; EndReason = m["why"]?.GetValue<string>();
                Disconnected?.Invoke(EndReason ?? "the game is over");
                break;
        }
    }

    void SetLobby(JsonNode? n)
    {
        var l = NetProtocol.FromNode<LobbyState>(n);
        if (l == null) return;
        Lobby = l;
        LobbyChanged?.Invoke(l);
    }

    void AddTurn(JsonObject m)
    {
        long s = m["s"]!.GetValue<long>();
        if (s <= LastTurn) return;
        _turns[s] = m["c"] as JsonArray ?? new JsonArray();
        while (_turns.ContainsKey(LastTurn + 1)) LastTurn++;
    }

    // ======================================================================================== the lobby
    public LobbyPeer? MyPeer => Lobby?.Peer(Me);
    public void SetReady(bool on) => Send(Msg("ready", ("on", on)));
    public void SendChat(string text, string to = "all") => Send(Msg("chat", ("text", text), ("to", to)));
    /// <summary>this player's slot (skirmish): tribe, team, color, hq, preset, army / armyTribe / armyPoints</summary>
    public void SetSlot(JsonObject fields) { var m = Msg("slot-set"); foreach (var (k, v) in fields) m[k] = v?.DeepClone(); Send(m); }
    public void SetCoopPlayer(int player) => Send(Msg("coop-player", ("player", player)));
    /// <summary>co-op campaign mode: take a free human seat (1..3)</summary>
    public void SetCoopSeat(int seat) => Send(Msg("coop-seat", ("seat", seat)));
    /// <summary>co-op campaign mode: the faction of this player's seat</summary>
    public void SetCoopTribe(string tribe) => Send(Msg("coop-tribe", ("tribe", tribe)));
    /// <summary>the host: the whole lobby (mode, setup, mission, options)</summary>
    public void HostSetLobby(LobbyState s) => Send(Msg("lobby-set", ("lobby", NetProtocol.ToNode(s))));
    public void HostStart(IReadOnlyDictionary<string, string>? profile)
    {
        var p = new JsonObject();
        if (profile != null) foreach (var (k, v) in profile) p[k] = v;
        Send(Msg("start", ("profile", p)));
    }
    public void Drop(int peer) => Send(Msg("drop", ("peer", peer)));
    public void Kick(int peer) => Send(Msg("kick", ("peer", peer)));
    public void HostEnd() => Send(Msg("end"));
    public void Leave() { Send(Msg("leave")); _conn?.Close("left the game"); Ended = true; }

    // ======================================================================================== the game
    /// <summary>this machine has built the world (step: where it stands - a rejoin replays from 0)</summary>
    public void Loaded(long step = 0) => Send(Msg("loaded", ("step", step)));

    /// <summary>sends a command to the host; it comes back in a turn (Delivered: the decoded copy)</summary>
    public long SubmitCommand(Command c)
    {
        long q = ++_seq;
        _sent[q] = c;
        if (_sent.Count > 2000) foreach (var k in _sent.Keys.Where(k => k < q - 1000).ToList()) _sent.Remove(k);
        Send(Msg("cmd", ("q", q), ("c", CommandCodec.Encode(c))));
        return q;
    }

    /// <summary>the commands of a step's turn in the host's order, with this machine's own original command object where it
    /// is one of them (its result goes back to the interface); null: the turn has not arrived</summary>
    public List<(Command Cmd, Command? Mine)>? TakeTurn(long step)
    {
        if (!_turns.Remove(step, out var arr)) return step <= LastTurn ? new() : null;
        var res = new List<(Command, Command?)>();
        foreach (var n in arr)
        {
            try
            {
                var c = CommandCodec.Decode(n!["c"]!);
                c.Step = step;
                Command? mine = null;
                if (n["p"]?.GetValue<int>() == Me && n["q"]?.GetValue<long>() is long q && _sent.Remove(q, out var orig)) mine = orig;
                res.Add((c, mine));
            }
            catch (Exception e) { L.Error($"a command of turn {step} could not be read (another version?)", e); }
        }
        return res;
    }

    public void Ack(long step) => Send(Msg("ack", ("step", step)));
    public void SendHash(long step, ulong hash, IEnumerable<(string Part, ulong Hash)> parts)
    {
        var p = new JsonObject();
        foreach (var (k, v) in parts) p[k] = v.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        Send(Msg("hash", ("step", step), ("h", hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)), ("parts", p)));
    }
    public void SendDump(long step, string text) => Send(Msg("dump", ("step", step), ("text", text)));
    public void RequestPause(bool on) => Send(Msg("pause", ("on", on)));
    public void SceneStart(string kind, int token, long step) => Send(Msg("scene-start", ("kind", kind), ("token", token), ("step", step)));
    public void SceneVote(string kind, int token, bool timeline) => Send(Msg("scene", ("kind", kind), ("token", token), ("timeline", timeline)));

    public void Dispose() { _conn?.Close("closed"); }
}
