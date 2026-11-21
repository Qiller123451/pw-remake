// What a co-op campaign game (mode 2, docs/COOP.md) is started with - part of GameStart, so a networked game, a saved
// game and a replay build the same world: the seats (human or AI commander, faction, the mission player's slot each
// one plays), the hero assignment, the co-op difficulty and the mission's co-op definition itself.
//
// CoopPlan makes it from a lobby's choices without a world (the lobby, the offline co-op menu, the dedicated server,
// the tests): which slot each seat takes, the default hero split.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Coop;

/// <summary>One seat of a co-op campaign game.</summary>
public sealed class CoopSeat
{
    /// <summary>1..3; seat 1 is the mission's own human player</summary>
    [JsonPropertyName("seat")] public int Seat { get; set; } = 1;
    /// <summary>"human" | "ai" (an AI commander)</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "human";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Hu, Aje, Ninigi, SEAS; "" = the mission's tribe (seat 1) / seat 1's tribe (the others)</summary>
    [JsonPropertyName("tribe")] public string Tribe { get; set; } = "";
    /// <summary>the player id (World.Players / the mission's slot) the seat plays - resolved by CoopPlan.Resolve</summary>
    [JsonPropertyName("slot")] public int Slot { get; set; } = -1;
    /// <summary>a human's network peer (informational; -1: an AI commander or offline)</summary>
    [JsonPropertyName("peer")] public int Peer { get; set; } = -1;
    /// <summary>an AI commander's aggression at the start: 0 defensive, 1 normal, 2 aggressive</summary>
    [JsonPropertyName("aggression")] public int Aggression { get; set; } = 1;
    /// <summary>a mission with an army budget: the army this seat's human bought on the army screen (online: on the
    /// human's own machine, sent to the host - docs/COOP.md "Armies"). Null: the seat's `army` of the definition (seat 1:
    /// the level's preset). Seat 1's is GameStart.Army as well (the campaign places it).</summary>
    [JsonPropertyName("army")] public Game.Army? Army { get; set; }
    /// <summary>a human seat's colour (palette id or "#rrggbb", each human's own choice in the lobby; null: the game's -
    /// seat 1 the mission's, the others the first free palette colour). A colour of a mission player or of another seat
    /// before it gives the default instead (CoopRuntime.AddPlayers, Campaign.MissionColors).</summary>
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonIgnore] public bool Ai => Kind == "ai";
    public override string ToString() => $"seat {Seat} {Kind} {Name} {Tribe} slot {Slot}" + (Color != null ? " color " + Color : "");
}

public sealed class CoopStart
{
    /// <summary>always "campaign" (mode 2; Shared mode is a lockstep game with every seat on the mission's player and no
    /// CoopStart)</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "campaign";
    /// <summary>the host's co-op difficulty: 0 Normal, 1 Hard, 2 Brutal (CoopScaling.DifficultyFactor)</summary>
    [JsonPropertyName("difficulty")] public int Difficulty { get; set; }
    [JsonPropertyName("seats")] public List<CoopSeat> Seats { get; set; } = new();
    /// <summary>story hero class -> seat number (only human seats)</summary>
    [JsonPropertyName("heroes")] public Dictionary<string, int> Heroes { get; set; } = new();
    /// <summary>the mission's co-op definition (CoopDefinition.Raw)</summary>
    [JsonPropertyName("definition")] public JsonObject? Definition { get; set; }

    public CoopSeat? SeatOf(int seat) => Seats.FirstOrDefault(s => s.Seat == seat);
    public IEnumerable<CoopSeat> Humans => Seats.Where(s => !s.Ai);
    public CoopDefinition Def() => CoopDefinition.FromJson(Definition ?? new JsonObject());
    public override string ToString() => $"co-op {Seats.Count} seats [{string.Join(", ", Seats)}] difficulty {CoopScaling.DifficultyNames[Math.Clamp(Difficulty, 0, 2)]}";
}

/// <summary>A mission player as the planning sees it (mission.json players[]); Color: its palette index (-1 none), Name
/// its name - the co-op lobby's "Your colour" greys those out (MissionColors.ForSeat)</summary>
public sealed record MissionSlot(int Id, string Tribe, bool Human, int Team, int[]? Diplomacy, int Color = -1, string Name = "");

/// <summary>Planning a co-op game without a world.</summary>
public static class CoopPlan
{
    /// <summary>the most players a campaign world holds (ids 0..MaxSlots-1): a mission uses up to 8, seats beyond them take
    /// 8 and up</summary>
    public const int MaxSlots = 12;

    /// <summary>the present players of a mission folder (map.lua's mission table)</summary>
    public static List<MissionSlot> MissionSlots(string missionFolder)
    {
        var res = new List<MissionSlot>();
        var j = Maps.PwMapReader.MissionOf(missionFolder);
        foreach (var p in j?["players"]?.AsArray() ?? new JsonArray())
        {
            if (p == null || !p["present"].Bool()) continue;
            res.Add(new MissionSlot(p["id"].Int(), p["tribe"].Str(), p["type"].Str() == "human", p["team"].Int(), p["diplomacy"] is JsonArray d ? d.Select(x => x.Int(1)).ToArray() : null,
                p["color"] is JsonValue ? p["color"].Int(-1) : -1, p["name"].Str()));
        }
        return res;
    }

    /// <summary>the mission's human player (Campaign.Plan's rule: the first present human, else the first present)</summary>
    public static int HumanSlot(List<MissionSlot> slots) => (slots.FirstOrDefault(s => s.Human) ?? slots.FirstOrDefault())?.Id ?? 0;

    /// <summary>gives every seat its slot: seat 1 the mission's human player, a seat with an ally the ally's slot, the
    /// others the first ids no mission player uses (8 and up when all eight are taken). Null: fine, else the problem.</summary>
    public static string? Resolve(CoopStart s, CoopDefinition def, List<MissionSlot> slots)
    {
        int human = HumanSlot(slots);
        var used = new HashSet<int>(slots.Select(x => x.Id));
        s.Seats = s.Seats.OrderBy(x => x.Seat).ToList();
        if (s.Seats.Count < 2 || s.Seats.Count > 3) return "Co-op needs 2 or 3 seats.";
        if (s.Seats.Select(x => x.Seat).Distinct().Count() != s.Seats.Count || s.Seats[0].Seat != 1 || s.Seats[^1].Seat != s.Seats.Count) return "The seats must be 1..n.";
        if (!s.Seats.Any(x => !x.Ai)) return "At least one seat must be a human.";
        if (!def.Supports(s.Seats.Count)) return $"The mission's co-op definition has room for {def.MaxSeats} seats.";
        foreach (var seat in s.Seats)
        {
            if (seat.Seat == 1) { seat.Slot = human; continue; }
            var sd = def.Seats[seat.Seat];
            if (sd.Ally is int a)
            {
                if (!slots.Any(x => x.Id == a)) return $"Seat {seat.Seat}: the ally {a} is not in the mission.";
                seat.Slot = a;
                continue;
            }
            int id = 0;
            while (used.Contains(id) || s.Seats.Any(o => o != seat && o.Slot == id)) id++;
            if (id >= MaxSlots) return "No free player slot for a seat.";
            seat.Slot = id;
            used.Add(id);
        }
        return null;
    }

    /// <summary>the default hero split: the story heroes in the definition's order go round the human seats (seat 1
    /// first; with 2 humans and 3 heroes seat 1 gets two - the host changes it in the lobby). AI commanders get none.</summary>
    public static Dictionary<string, int> DefaultHeroes(CoopDefinition def, IEnumerable<CoopSeat> seats)
    {
        var humans = seats.Where(x => !x.Ai).OrderBy(x => x.Seat).Select(x => x.Seat).ToList();
        var res = new Dictionary<string, int>(StringComparer.Ordinal);
        if (humans.Count == 0) return res;
        for (int i = 0; i < def.Heroes.Count; i++) res[def.Heroes[i]] = humans[i % humans.Count];
        return res;
    }

    /// <summary>the hero split checked: every story hero on a human seat (a hero of an AI seat or of no seat goes to the
    /// first human)</summary>
    public static Dictionary<string, int> FixHeroes(CoopDefinition def, IEnumerable<CoopSeat> seats, Dictionary<string, int>? want)
    {
        var list = seats.ToList();
        var humans = list.Where(x => !x.Ai).Select(x => x.Seat).ToHashSet();
        var dflt = DefaultHeroes(def, list);
        var res = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in def.Heroes)
            res[h] = want != null && want.TryGetValue(h, out var s) && humans.Contains(s) ? s : dflt.GetValueOrDefault(h, humans.DefaultIfEmpty(1).Min());
        return res;
    }

    /// <summary>a complete CoopStart from the choices (seats in order: kind, name, tribe), the definition and the
    /// mission folder; throws with the problem</summary>
    public static CoopStart Make(CoopDefinition def, string missionFolder, IEnumerable<CoopSeat> seats, int difficulty, Dictionary<string, int>? heroes = null)
    {
        var s = new CoopStart { Difficulty = Math.Clamp(difficulty, 0, 2), Seats = seats.ToList(), Definition = (JsonObject)def.Raw.DeepClone() };
        var why = Resolve(s, def, MissionSlots(missionFolder));
        if (why != null) throw new InvalidOperationException(why);
        s.Heroes = FixHeroes(def, s.Seats, heroes);
        return s;
    }
}
