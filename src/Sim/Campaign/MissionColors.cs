// The colour the human picks for a campaign mission (docs/CAMPAIGN.md "Your colour"): the skirmish palette (the
// original's eight party colours, the remake's eight) or any RGB colour ("#rrggbb", Colors.CustomColor) - but none
// that another player of the mission already has (an enemy, an ally, a neutral or computer player of the map's
// players). The menus grey those out ("used by <name>"); the world checks again when it is built (Campaign.Build,
// CoopRuntime.AddPlayers), so a colour that collides - a remembered choice, a save of another mission version - gives
// the mission's own colour instead, the same on every machine.
//
// The chosen colour is part of the GameStart (GameStart.Color; co-op: CoopSeat.Color), so saved games, replays and the
// other machines of a lockstep game build the same players; a player with a chosen colour mixes it into the world's
// state hash (Player.ColorPicked, World.StateHash).
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Campaign;

/// <summary>a colour a player of the mission has: its player id, name, light colour (0xRRGGBB) and palette index
/// (-1: a colour of its own)</summary>
public sealed record TakenColor(int Player, string Name, int Light, int Slot);

public static class MissionColors
{
    /// <summary>the colours of a mission's present players (map.lua mission.players[]) except those of `except` (the
    /// human seats' players); a player without a colour of the palette is drawn grey and blocks nothing</summary>
    public static List<TakenColor> Used(JsonArray? players, ICollection<int> except)
    {
        var res = new List<TakenColor>();
        foreach (var pd in players ?? new JsonArray())
        {
            if (pd == null || !pd["present"].Bool()) continue;
            int id = pd["id"].Int();
            if (except.Contains(id)) continue;
            int ci = pd["color"] is JsonValue ? pd["color"].Int(-1) : -1;
            if (ci < 0 || ci >= Colors.PLAYER_COLORS.Count) continue;
            res.Add(new TakenColor(id, pd["name"].Str() is { Length: > 0 } n ? n : "Player " + id, Colors.Hex(Colors.PLAYER_COLORS[ci].Light), ci));
        }
        return res;
    }

    /// <summary>the colours of a mission folder's players except the human player's (and those of `except`)</summary>
    public static List<TakenColor> Used(string missionFolder, IEnumerable<int>? except = null)
    {
        var mission = Maps.PwMapReader.MissionOf(missionFolder);
        var skip = new HashSet<int>(except ?? Enumerable.Empty<int>()) { Coop.CoopPlan.HumanSlot(Coop.CoopPlan.MissionSlots(missionFolder)) };
        return Used(mission?["players"] as JsonArray, skip);
    }

    /// <summary>the palette colour (index) a player of a mission folder has (-1: none - drawn grey)</summary>
    public static int ColorSlotOf(string missionFolder, int player)
    {
        foreach (var pd in Maps.PwMapReader.MissionOf(missionFolder)?["players"] as JsonArray ?? new JsonArray())
            if (pd != null && pd["id"].Int() == player)
                return pd["color"] is JsonValue && pd["color"].Int(-1) is int ci && ci >= 0 && ci < Colors.PLAYER_COLORS.Count ? ci : -1;
        return -1;
    }

    /// <summary>the colours a co-op seat's human can't take (the lobby, the host's check): the mission's players' but the
    /// seat's own slot's (seat 1: the mission's human player, a seat on an ally: the ally) and the other human seats'
    /// chosen ones (others: seat, name, colour id)</summary>
    public static List<TakenColor> ForSeat(IReadOnlyList<Coop.MissionSlot>? slots, Coop.CoopDefinition? def, int seat, IEnumerable<(int Seat, string Name, string? Color)> others)
    {
        var list = slots?.ToList() ?? new List<Coop.MissionSlot>();
        int own = seat == 1 ? Coop.CoopPlan.HumanSlot(list) : def != null && def.Seats.TryGetValue(seat, out var sd) && sd.Ally is int a ? a : -1;
        var res = list.Where(x => x.Id != own && x.Color >= 0 && x.Color < Colors.PLAYER_COLORS.Count)
            .Select(x => new TakenColor(x.Id, x.Name.Length > 0 ? x.Name : "Player " + x.Id, Colors.Hex(Colors.PLAYER_COLORS[x.Color].Light), x.Color)).ToList();
        foreach (var o in others)
            if (o.Seat != seat && Colors.ColorById(o.Color) is { } c) res.Add(new TakenColor(-o.Seat, o.Name, Colors.Hex(c.Light), Colors.IndexOf(o.Color)));
        return res;
    }

    /// <summary>the colours of the world's players (those with a party colour) other than `except`</summary>
    public static List<TakenColor> Used(IEnumerable<Player> players, IEnumerable<Player> except)
    {
        var skip = except.ToHashSet();
        return players.Where(p => !skip.Contains(p) && p.PartyColor != null).Select(p => new TakenColor(p.Id, p.Name, p.PartyColor!.Value, p.ColorSlot)).ToList();
    }

    /// <summary>the player whose colour colour id `id` would take (the same palette colour, or one closer than
    /// Colors.NEAR_DISTANCE), null when it is free</summary>
    public static TakenColor? Blocker(string? id, IEnumerable<TakenColor> used)
    {
        var c = Colors.ColorById(id);
        if (c == null) return null;
        int k = Colors.IndexOf(id), light = Colors.Hex(c.Light);
        foreach (var u in used)
            if ((k >= 0 && u.Slot == k) || Colors.Distance(light, u.Light) < Colors.NEAR_DISTANCE) return u;
        return null;
    }

    /// <summary>the colour a mission gives the human: the wanted id when it is a colour and free, else null (the mission's
    /// own colour)</summary>
    public static string? Resolve(string? id, IEnumerable<TakenColor> used) =>
        id is { Length: > 0 } && Colors.ColorById(id) != null && Blocker(id, used) == null ? id : null;

    /// <summary>gives player p the colour id (palette or "#rrggbb"): minimap / ring colour, model tint, palette slot</summary>
    public static void Apply(Player p, string id)
    {
        var c = Colors.ColorById(id)!;
        p.Color = Colors.Hex(c.Dark);
        p.PartyColor = Colors.Hex(c.Light);
        p.ColorSlot = Colors.IndexOf(id);
        p.ColorPicked = id;
    }
}
