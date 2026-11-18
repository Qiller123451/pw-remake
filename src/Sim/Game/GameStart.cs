// What a game is started with - everything needed to set it up again the same way (restart, a saved game, a
// replay, the other machines of a co-op or multiplayer game later): the configuration, the mode and mission, the
// player's tribe, the difficulty, the debug options and the seed of the world's random numbers.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace PwRemake.Sim.Game;

/// <summary>Campaign: a mission of the original campaign; Skirmish; Sequel: a mission of Containment (the remake's own
/// campaign, docs/SEQUEL.md); Scenario: one of the remake's scenarios (Tribe: the tribe played)</summary>
public enum GameMode { Campaign, Skirmish, Scenario, Sequel }

/// <summary>A human of a lockstep game: the name shown, the player (World.Players id) whose orders the human gives -
/// two seats on one player share it (co-op "shared"), and the network peer it was at the start (informational).</summary>
public sealed class Seat
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("player")] public int Player { get; set; }
    [JsonPropertyName("peer")] public int Peer { get; set; }
    public override string ToString() => $"{Name} (player {Player})";
}

public sealed class GameStart
{
    [JsonPropertyName("configuration")] public string Configuration { get; set; } = "";
    [JsonPropertyName("mode")] public GameMode Mode { get; set; } = GameMode.Campaign;
    /// <summary>Campaign: the mission id (0 = tutorial).</summary>
    [JsonPropertyName("mission")] public int Mission { get; set; }
    /// <summary>Campaign: the mission's folder below the configuration's missions/.</summary>
    [JsonPropertyName("missionFolder")] public string MissionFolder { get; set; } = "";
    /// <summary>"" = the mission's own tribe, else Hu, Aje, Ninigi, SEAS (the campaign's tribe swap).</summary>
    [JsonPropertyName("tribe")] public string Tribe { get; set; } = "";
    /// <summary>0 easy, 1 medium, 2 hard, 3 nightmare.</summary>
    [JsonPropertyName("difficulty")] public int Difficulty { get; set; } = 1;
    [JsonPropertyName("debug")] public DebugOptions? Debug { get; set; }
    [JsonPropertyName("seed")] public ulong Seed { get; set; } = 1;
    /// <summary>Campaign, a mission with an army budget: the army bought on the army screen (null: the level's preset).</summary>
    [JsonPropertyName("army")] public Army? Army { get; set; }
    /// <summary>Skirmish: the setup (map, players, game type, points ...: Skirmish.cs) - the plain object a lobby fills.</summary>
    [JsonPropertyName("skirmish")] public SkirmishSetup? Skirmish { get; set; }
    /// <summary>A lockstep game over the network (co-op in a campaign mission, PvP skirmish: docs/MULTIPLAYER.md): the
    /// humans and the player each one commands, in seat order (seat 0 = the host's). Null: a single player game.
    /// Every machine builds the same world from it - the local seat is the session's business, never the world's.</summary>
    [JsonPropertyName("seats")] public List<Seat>? Seats { get; set; }
    /// <summary>a lockstep game: how its cutscenes end ("all": when every human has seen or skipped it; "host": when the
    /// host has) - docs/MULTIPLAYER.md "Scenes"</summary>
    [JsonPropertyName("sceneRule")] public string? SceneRule { get; set; }
    /// <summary>a lockstep game: the host's profile values (the mission reads them: Tutorial_Started ...), the same for
    /// every machine</summary>
    [JsonPropertyName("profile")] public Dictionary<string, string>? Profile { get; set; }
    /// <summary>a co-op campaign game (mode 2, docs/COOP.md): the seats (human / AI commander, faction, slot), the hero
    /// split, the co-op difficulty and the mission's co-op definition. Null: single player, Shared co-op, PvP. With it,
    /// Seats lists the humans, each on its seat's slot.</summary>
    [JsonPropertyName("coop")] public Coop.CoopStart? Coop { get; set; }
    /// <summary>a campaign mission (single player, co-op, Shared): MIRAGE's game options that are tech tree filters -
    /// the sixth epoch, no titans, flying units (Rules.GameplayOptions; the lobby's / campaign menu's choice). Null: MIRAGE's
    /// defaults (sixth epoch on, titans on, flying units off). Every player of the mission gets them (Campaign.Build, the
    /// co-op seats and enemies). A skirmish has them in its setup (SkirmishSetup.EpochSix / RemoveTitans / FlyingEnabled).</summary>
    [JsonPropertyName("gameplay")] public Rules.GameplayOptions? Gameplay { get; set; }

    /// <summary>the players commanded by humans in a lockstep game (distinct, in seat order); empty without seats</summary>
    public List<int> SeatPlayers() => Seats == null ? new() : Seats.Select(s => s.Player).Distinct().ToList();

    public override string ToString() => Mode == GameMode.Skirmish && Skirmish != null
        ? $"Skirmish on {(Skirmish.Map == "" ? "a random map" : Skirmish.Map)} config={Configuration} {Skirmish.Mode} points={Skirmish.Points} players=[{string.Join(", ", Game.Skirmish.ActiveSlots(Skirmish).Select(x => $"{x.Slot.Type} {x.Slot.Tribe}"))}] seed={Skirmish.Seed}" +
          (Debug != null ? " debug=[" + string.Join(", ", Debug.Summary()) + "]" : "") :
        $"{Mode} {Mission} ({MissionFolder}) config={Configuration} tribe={(Tribe == "" ? "mission's" : Tribe)} difficulty={Difficulty} seed={Seed}" +
        (Debug != null ? " debug=[" + string.Join(", ", Debug.Summary()) + "]" : "") +
        (Gameplay != null && !Gameplay.IsDefault ? " " + Gameplay : "") +
        (Seats != null ? " seats=[" + string.Join(", ", Seats) + "]" : "") + (Coop != null ? " " + Coop : "");
}

/// <summary>The seats of a lockstep game applied to its world: the same on every machine.</summary>
public static class Seats
{
    /// <summary>every seat's player knows its humans (Player.Seats, ControlledBy = Human)</summary>
    public static void Apply(World.World w, List<Seat>? seats)
    {
        if (seats == null) return;
        for (int i = 0; i < seats.Count; i++)
        {
            var p = w.Players.FirstOrDefault(x => x.Id == seats[i].Player);
            if (p == null) continue;
            p.Seats.Add(i);
            p.ControlledBy = World.ControlledBy.Human;
        }
    }
    /// <summary>the players of the seats, distinct, in seat order</summary>
    public static IEnumerable<World.Player> Players(World.World w, List<Seat> seats) =>
        seats.Select(s => s.Player).Distinct().Select(id => w.Players.FirstOrDefault(x => x.Id == id)).Where(p => p != null).Cast<World.Player>();
}
