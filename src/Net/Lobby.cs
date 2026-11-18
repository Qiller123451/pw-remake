// The lobby of a networked game (docs/MULTIPLAYER.md "Lobby"): what the host offers - a skirmish setup (PvP) or a
// campaign mission (co-op) - and who has joined, which slot or player each one takes, who is ready. The host keeps the
// truth and sends the whole state to everybody on every change (a few hundred bytes); the clients show it and ask for
// changes of their own slot. When the host starts, the lobby becomes a GameStart with its seats (GameStart.Seats):
// the one object every machine builds the same world from.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PwRemake.Sim.Coop;
using PwRemake.Sim.Game;

namespace PwRemake.Net;

/// <summary>The protocol's constants.</summary>
public static class NetProtocol
{
    /// <summary>raised with every change of the messages or of their meaning (a client of another version is refused)</summary>
    public const int Version = 4;          // 2: the co-op campaign mode (LobbyState.CoopMode, Coop; docs/COOP.md)
                                           // 3: up to 16 players (16 slots, teams 1..16), colours "#rrggbb" (docs/SKIRMISH.md)
                                           // 4: MIRAGE's game options of a mission (GameStart.Gameplay: sixth epoch, no titans, flying units)
    /// <summary>the original's default port (_UI_GamLWind_Port_TT: "All other values will be defaulted to 1905")</summary>
    public const int DefaultPort = 1905;
    /// <summary>steps a command waits before it is applied (4 x 50 ms = 200 ms + the network's delay)</summary>
    public const int DefaultInputDelay = 4;
    /// <summary>steps between two hash comparisons (5 s of game time)</summary>
    public const int HashEvery = 100;
    /// <summary>how far (steps) a machine may fall behind the host's turns before the host waits for it (2 s)</summary>
    public const int MaxLag = 40;
    /// <summary>seconds the game waits for a player who lost the connection before the host may drop the player
    /// (the host can drop at once; the game goes on without the player then)</summary>
    public const double ReconnectWait = 60;
    /// <summary>the most humans in a game (= the skirmish setup's slots)</summary>
    public const int MaxPlayers = Sim.Game.Skirmish.MAX_SLOTS;

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    public static JsonNode? ToNode<T>(T v) => JsonSerializer.SerializeToNode(v, Json);
    public static T? FromNode<T>(JsonNode? n) where T : class { try { return n?.Deserialize<T>(Json); } catch (JsonException) { return null; } }
    public static JsonObject Msg(string type) => new() { ["t"] = type };
}

/// <summary>Somebody in the lobby.</summary>
public sealed class LobbyPeer
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("host")] public bool Host { get; set; }
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    /// <summary>skirmish: the slot of the setup this human plays (-1: none yet)</summary>
    [JsonPropertyName("slot")] public int Slot { get; set; } = -1;
    /// <summary>co-op: the mission's player this human commands (the human player = shared; an allied computer
    /// player = taken over)</summary>
    [JsonPropertyName("player")] public int Player { get; set; }
    /// <summary>round trip time to the host (ms)</summary>
    [JsonPropertyName("ping")] public int Ping { get; set; }
    /// <summary>why this machine cannot play (another game version, other game data, a missing map)</summary>
    [JsonPropertyName("problem")] public string? Problem { get; set; }
    /// <summary>a dedicated server's own entry is no player</summary>
    [JsonPropertyName("spectator")] public bool Spectator { get; set; }
    /// <summary>co-op campaign mode: the seat (1..3) this human plays, 0 none yet</summary>
    [JsonPropertyName("seat")] public int Seat { get; set; }
}

/// <summary>A seat of the co-op campaign lobby.</summary>
public sealed class CoopLobbySeat
{
    [JsonPropertyName("seat")] public int Seat { get; set; }
    /// <summary>"human" (a human's - the peer whose LobbyPeer.Seat it is, or open), "ai" (an AI commander), "empty"
    /// (seat 3 only: a game of two seats)</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "human";
    /// <summary>Hu, Aje, Ninigi, SEAS ("" = the mission's tribe)</summary>
    [JsonPropertyName("tribe")] public string Tribe { get; set; } = "";
    /// <summary>an AI commander's aggression at the start (0 defensive, 1 normal, 2 aggressive)</summary>
    [JsonPropertyName("aggression")] public int Aggression { get; set; } = 1;
}

/// <summary>The co-op campaign lobby (mode 2, docs/COOP.md): seats, factions, heroes, co-op difficulty; the host fills
/// the mission's definition and players (the lobby needs no game data to make the start: dedicated server).</summary>
public sealed class CoopLobbyState
{
    /// <summary>0 Normal, 1 Hard, 2 Brutal</summary>
    [JsonPropertyName("difficulty")] public int Difficulty { get; set; }
    [JsonPropertyName("seats")] public List<CoopLobbySeat> Seats { get; set; } = new() { new() { Seat = 1 }, new() { Seat = 2 }, new() { Seat = 3, Kind = "empty" } };
    /// <summary>story hero class -> seat (the host's choice; CoopPlan.FixHeroes keeps it on humans)</summary>
    [JsonPropertyName("heroes")] public Dictionary<string, int> Heroes { get; set; } = new();
    /// <summary>the mission's co-op definition (null: the mission has none - mode 2 not possible)</summary>
    [JsonPropertyName("definition")] public System.Text.Json.Nodes.JsonObject? Definition { get; set; }
    /// <summary>the mission's players (mission.json): the seats' slots are resolved from them</summary>
    [JsonPropertyName("missionPlayers")] public List<MissionSlot>? MissionPlayers { get; set; }

    /// <summary>the seats in use (seat 3 "empty": two)</summary>
    public IEnumerable<CoopLobbySeat> InUse => Seats.Where(s => s.Kind != "empty").OrderBy(s => s.Seat);
    public CoopDefinition? Def() => Definition == null ? null : CoopDefinition.FromJson(Definition);
    /// <summary>the CoopSeats of the game (names from the peers on them)</summary>
    public List<CoopSeat> GameSeats(IEnumerable<LobbyPeer> peers) => InUse.Select(s =>
    {
        var p = s.Kind == "human" ? peers.FirstOrDefault(x => x.Seat == s.Seat && !x.Spectator) : null;
        return new CoopSeat { Seat = s.Seat, Kind = s.Kind == "ai" ? "ai" : "human", Tribe = s.Tribe, Aggression = s.Aggression, Name = p?.Name ?? $"AI commander {s.Seat}", Peer = p?.Id ?? -1 };
    }).ToList();
    /// <summary>the hero split the game will have (shown in the lobby)</summary>
    public Dictionary<string, int> HeroSplit(IEnumerable<LobbyPeer> peers) => Def() is { } d ? CoopPlan.FixHeroes(d, GameSeats(peers), Heroes) : new();
}

/// <summary>What the lobby offers and who is in it.</summary>
public sealed class LobbyState
{
    /// <summary>"skirmish" (PvP, the skirmish setup's human slots) | "coop" (a campaign mission)</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "skirmish";
    [JsonPropertyName("name")] public string Name { get; set; } = "ParaWorld";
    /// <summary>the game configuration (base / mirage): every machine must have the same</summary>
    [JsonPropertyName("configuration")] public string Configuration { get; set; } = "";
    [JsonPropertyName("skirmish")] public SkirmishSetup? Skirmish { get; set; }
    /// <summary>co-op: the mission, tribe, difficulty, debug options, MIRAGE's game options (Gameplay: sixth epoch, no
    /// titans - the host's) and seed (GameStart without seats)</summary>
    [JsonPropertyName("campaign")] public GameStart? Campaign { get; set; }
    /// <summary>co-op: the mission's players a human may take: [player id, name] - the human player first, then the
    /// computer players allied with it both ways (filled by the host from mission.json)</summary>
    [JsonPropertyName("coopPlayers")] public List<CoopChoice>? CoopPlayers { get; set; }
    /// <summary>how a cutscene ends: "all" (every human has seen or skipped it) | "host"</summary>
    [JsonPropertyName("sceneRule")] public string SceneRule { get; set; } = "all";
    [JsonPropertyName("inputDelay")] public int InputDelay { get; set; } = NetProtocol.DefaultInputDelay;
    [JsonPropertyName("peers")] public List<LobbyPeer> Peers { get; set; } = new();
    /// <summary>co-op: "shared" (mode 1: every human on the mission's player) | "campaign" (mode 2: seats of their own)</summary>
    [JsonPropertyName("coopMode")] public string CoopMode { get; set; } = "shared";
    /// <summary>co-op campaign mode: seats, factions, heroes, difficulty, the definition</summary>
    [JsonPropertyName("coop")] public CoopLobbyState? Coop { get; set; }
    /// <summary>"lobby" | "loading" | "running" | "over"</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "lobby";

    public LobbyState Clone() => NetProtocol.FromNode<LobbyState>(NetProtocol.ToNode(this))!;
    public LobbyPeer? Peer(int id) => Peers.FirstOrDefault(p => p.Id == id);
    public IEnumerable<LobbyPeer> Players => Peers.Where(p => !p.Spectator);
}

public sealed class CoopChoice
{
    [JsonPropertyName("player")] public int Player { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tribe")] public string Tribe { get; set; } = "";
    /// <summary>the mission's human player (shared by everyone who picks it)</summary>
    [JsonPropertyName("main")] public bool Main { get; set; }
}

/// <summary>Turning a lobby into the GameStart of the game.</summary>
public static class LobbyStart
{
    /// <summary>why the host cannot start now (null: it can)</summary>
    public static string? Problem(LobbyState s)
    {
        var players = s.Players.ToList();
        if (players.Count < 1) return "Nobody is in the game.";
        if (players.FirstOrDefault(p => p.Problem != null) is { } bad) return $"{bad.Name}: {bad.Problem}";
        if (players.FirstOrDefault(p => !p.Ready && !p.Host) is { } nr) return $"{nr.Name} is not ready.";
        if (s.Mode == "skirmish")
        {
            if (s.Skirmish == null) return "No skirmish set up.";
            foreach (var p in players)
                if (p.Slot < 0 || p.Slot >= s.Skirmish.Slots.Count || s.Skirmish.Slots[p.Slot].Type != "human") return $"{p.Name} has no slot.";
            if (players.Select(p => p.Slot).Distinct().Count() != players.Count) return "Two players on one slot.";
            if (s.Skirmish.Slots.Any(x => x.Type == "open")) return "An open slot is waiting for a player (close it or make it a computer player).";
            if (Skirmish.ActiveSlots(s.Skirmish).Count < 2) return "A skirmish needs at least two players.";
        }
        else if (s.CoopMode == "campaign")
        {
            if (s.Campaign == null) return "No mission chosen.";
            var c = s.Coop;
            var def = c?.Def();
            if (c == null || def == null) return "This mission has no co-op campaign definition yet (Shared mode only).";
            var used = c.InUse.ToList();
            if (used.Count < 2 || used.Any(x => x.Seat == 2 && x.Kind == "empty") || c.Seats.Any(x => x.Seat < 3 && x.Kind == "empty")) return "Seats 1 and 2 need a human or an AI commander.";
            if (!def.Supports(used.Count)) return $"The mission's co-op definition has room for {def.MaxSeats} seats.";
            foreach (var x in used.Where(x => x.Kind == "human"))
            {
                int n = players.Count(p => p.Seat == x.Seat);
                if (n == 0) return $"Seat {x.Seat} waits for a player (or make it an AI commander).";
                if (n > 1) return $"Two players on seat {x.Seat}.";
            }
            foreach (var p in players) if (!used.Any(x => x.Seat == p.Seat && x.Kind == "human")) return $"{p.Name} has no seat.";
            if (c.MissionPlayers == null) return "The mission's players are not known.";
            var probe = new CoopStart { Seats = c.GameSeats(players) };
            var why = CoopPlan.Resolve(probe, def, c.MissionPlayers);
            if (why != null) return why;
        }
        else
        {
            if (s.Campaign == null) return "No mission chosen.";
            var allowed = s.CoopPlayers?.Select(c => c.Player).ToHashSet() ?? new HashSet<int>();
            foreach (var p in players) if (allowed.Count > 0 && !allowed.Contains(p.Player)) return $"{p.Name} commands no player of this mission.";
            // Shared mode: everybody on the mission's player (taking over an ally is the co-op campaign's: docs/COOP.md)
            if (s.CoopPlayers?.FirstOrDefault(c => c.Main) is { } main && players.Any(p => p.Player != main.Player)) return "Shared: every player commands the mission's player.";
        }
        return null;
    }

    /// <summary>the game's start: the setup with the seats (seat 0: the host), the host's profile values for a mission</summary>
    public static GameStart Make(LobbyState s, Dictionary<string, string>? profile)
    {
        var players = s.Players.OrderBy(p => p.Host ? 0 : 1).ThenBy(p => p.Id).ToList();
        GameStart start;
        if (s.Mode == "skirmish")
        {
            var setup = s.Skirmish!.Clone();
            // the player ids of a skirmish: the active slots in order (SkirmishSession.PlacePlayers)
            var active = Skirmish.ActiveSlots(setup);
            foreach (var p in players) setup.Slots[p.Slot].Owner = p.Name;
            start = new GameStart { Configuration = s.Configuration, Mode = GameMode.Skirmish, Skirmish = setup, Seed = setup.Seed, Debug = setup.Debug };
            start.Seats = players.Select(p => new Seat { Name = p.Name, Peer = p.Id, Player = active.FindIndex(a => a.Index == p.Slot) }).ToList();
        }
        else
        {
            var c = s.Campaign!;
            start = new GameStart
            {
                Configuration = s.Configuration, Mode = GameMode.Campaign, Mission = c.Mission, MissionFolder = c.MissionFolder, Tribe = c.Tribe,
                Difficulty = c.Difficulty, Debug = c.Debug, Seed = c.Seed, Army = c.Army, Gameplay = c.Gameplay?.Clone(),
                Profile = profile != null ? new Dictionary<string, string>(profile) : null,
            };
            if (s.CoopMode == "campaign" && s.Coop?.Def() is { } def && s.Coop.MissionPlayers != null)
            {
                // mode 2: the seats, each human on its seat's slot (CoopPlan resolves them from the mission's players)
                var cs = new CoopStart { Difficulty = Math.Clamp(s.Coop.Difficulty, 0, 2), Seats = s.Coop.GameSeats(players), Definition = (System.Text.Json.Nodes.JsonObject)s.Coop.Definition!.DeepClone() };
                CoopPlan.Resolve(cs, def, s.Coop.MissionPlayers);
                cs.Heroes = CoopPlan.FixHeroes(def, cs.Seats, s.Coop.Heroes);
                start.Coop = cs;
                if (cs.SeatOf(1) is { Tribe.Length: > 0 } s1) start.Tribe = s1.Tribe;
                start.Seats = players.OrderBy(p => p.Seat).Select(p => new Seat { Name = p.Name, Peer = p.Id, Player = cs.SeatOf(p.Seat)!.Slot }).ToList();
                // (seat 0 of GameStart.Seats is the host's machine's: the host first)
                var hostSeat = start.Seats.FindIndex(x => players.First(p => p.Id == x.Peer).Host);
                if (hostSeat > 0) { var hs = start.Seats[hostSeat]; start.Seats.RemoveAt(hostSeat); start.Seats.Insert(0, hs); }
            }
            else start.Seats = players.Select(p => new Seat { Name = p.Name, Peer = p.Id, Player = p.Player }).ToList();
        }
        start.SceneRule = s.SceneRule;
        return start;
    }
}
