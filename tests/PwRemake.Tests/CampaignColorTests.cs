// "Your colour" of a campaign mission (docs/CAMPAIGN.md "Your colour"; src/Sim/Campaign/MissionColors.cs): the human
// player gets the colour chosen (GameStart.Color) unless another player of the mission has it - then the mission's own;
// co-op seats (CoopSeat.Color) the same, also against the other humans; the colour is part of the start (saves,
// replays) and of the state hash only when chosen.
//
//   dotnet run --project tests/PwRemake.Tests -- CampaignColor
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Coop;
using PwRemake.Sim.Game;
using PwRemake.Sim.Rules;

namespace PwRemake.Tests;

[TestClass]
public static class CampaignColorTests
{
    /// <summary>mission players as map.lua's reader gives them (parsed JSON)</summary>
    static JsonArray Players(params (int Id, string Name, int Color, string Type)[] ps) =>
        (JsonArray)JsonNode.Parse("[" + string.Join(",", ps.Select(p => $"{{\"id\":{p.Id},\"name\":\"{p.Name}\",\"present\":true,\"color\":{p.Color},\"type\":\"{p.Type}\"}}")) + "]")!;

    public static void CampaignColorRules(TestContext t)
    {
        // the human (0, yellow), an enemy (1, red), an ally (2, cyan), a player without a palette colour (3, -1)
        var pl = Players((0, "You", 0, "human"), (1, "Pirates", 1, "ai"), (2, "Ally", 2, "ai"), (3, "Animals", -1, "ai"));
        var used = MissionColors.Used(pl, new[] { 0 });
        t.Equal("1,2", string.Join(",", used.Select(u => u.Player)), "the other players with a colour (not the human, not the colourless)");
        t.Equal("Pirates", MissionColors.Blocker("red", used)?.Name, "the enemy's colour is blocked");
        t.Equal("Ally", MissionColors.Blocker("cyan", used)?.Name, "the ally's colour is blocked");
        t.True(MissionColors.Blocker("yellow", used) == null, "the human's own mission colour is free");
        t.True(MissionColors.Blocker("blue", used) == null, "a colour nobody has is free");
        // a custom colour close to the enemy's red is blocked, one far from every colour is free
        t.Equal("Pirates", MissionColors.Blocker("#e25656", used)?.Name, "a custom colour next to red");
        t.True(MissionColors.Blocker("#102030", used) == null, "a dark custom colour is free");
        t.Equal(null, MissionColors.Resolve("red", used), "a taken colour resolves to the mission's own (null)");
        t.Equal("blue", MissionColors.Resolve("blue", used), "a free colour stays");
        t.Equal(null, MissionColors.Resolve("no-such-colour", used), "an unknown id: the mission's");
        // co-op seats: the mission's players but the seat's own, and the other humans' choices
        var slots = new[] { new MissionSlot(0, "Hu", true, 1, null, 0, "You"), new MissionSlot(1, "Aje", false, 2, null, 1, "Pirates") };
        var s2 = MissionColors.ForSeat(slots, null, 2, new[] { (1, "Host", (string?)"blue"), (2, "Me", (string?)"green") });
        t.True(MissionColors.Blocker("blue", s2) != null && MissionColors.Blocker("red", s2) != null && MissionColors.Blocker("yellow", s2) != null, "seat 2: seat 1's choice, the enemy and the mission's human player are taken");
        t.True(MissionColors.Blocker("green", s2) == null, "seat 2: its own choice is free");
        var s1 = MissionColors.ForSeat(slots, null, 1, new (int, string, string?)[0]);
        t.True(MissionColors.Blocker("yellow", s1) == null && MissionColors.Blocker("red", s1) != null, "seat 1: its own mission colour free, the enemy's not");
        // the start keeps the colour (saves, replays, the other machines)
        var start = new GameStart { Mission = 3, Color = "#123456", Coop = new CoopStart { Seats = { new CoopSeat { Seat = 2, Color = "teal" } } } };
        var back = JsonSerializer.Deserialize<GameStart>(JsonSerializer.Serialize(start))!;
        t.Equal("#123456", back.Color, "GameStart.Color round trip");
        t.Equal("teal", back.Coop!.Seats[0].Color, "CoopSeat.Color round trip");
    }

    /// <summary>a mission start with a chosen colour gives the human that colour; an enemy's colour is refused (the
    /// mission's own instead)</summary>
    public static void CampaignColorMissionStart(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("base configuration or its data not found");
        int mission = 3;
        var folder = MissionSession.MissionFolderOf(new GameStart { Configuration = "base", Mission = mission }, c!.Config);
        if (folder == null) t.Skip($"mission {mission} not in base");
        var taken = MissionColors.Used(folder!);
        t.True(taken.Count > 0, "the mission has other players with colours");
        var free = Colors.PLAYER_COLORS.First(p => MissionColors.Blocker(p.Id, taken) == null && p.Id != "yellow");
        var enemy = taken.First();

        var plain = CampaignTests.Session(t, "base", mission, color: null);
        var dflt = plain.Human!;
        t.True(dflt.ColorPicked == null, "no colour chosen: none picked");

        var chosen = CampaignTests.Session(t, "base", mission, color: free.Id);
        var h = chosen.Human!;
        t.Equal(Colors.IndexOf(free.Id), h.ColorSlot, $"the human plays {free.Name}");
        t.Equal(Colors.Hex(free.Light), h.PartyColor ?? -1, "its models' party colour");
        t.Equal(Colors.Hex(free.Dark), h.Color, "its minimap / ring colour");
        t.Equal(free.Id, h.ColorPicked, "picked");
        t.True(chosen.World.Players.Where(p => p != h).All(p => p.ColorSlot != h.ColorSlot), "nobody else has it");
        // the colour is in the hash only when chosen: the same start without it hashes as before
        t.True(chosen.StateHash() != plain.StateHash(), "a chosen colour changes the state hash");
        t.Equal(plain.StateHash(), CampaignTests.Session(t, "base", mission, color: null).StateHash(), "no colour: the hash as before");

        var refused = CampaignTests.Session(t, "base", mission, color: Colors.PLAYER_COLORS[enemy.Slot].Id);
        var r = refused.Human!;
        t.Equal(dflt.ColorSlot, r.ColorSlot, $"{enemy.Name}'s colour refused: the mission's own slot");
        t.Equal(dflt.Color, r.Color, "the mission's own minimap colour");
        t.Equal(dflt.PartyColor, r.PartyColor, "the mission's own party colour");
        t.True(r.ColorPicked == null, "nothing picked");
        t.Equal(plain.StateHash(), refused.StateHash(), "a refused colour: the same world as without one");
    }

    /// <summary>co-op: each human seat its own colour; a colour seat 1 took (or an enemy's) is refused for seat 2</summary>
    public static void CampaignColorCoopSeats(TestContext t)
    {
        var start = CoopTests.CoopStart(t, "base", 3, new[] { ("human", "Hu"), ("human", "Aje"), ("ai", "Ninigi") });
        start.Coop!.Seats[0].Color = "blue";
        start.Coop.Seats[1].Color = "#40e0a0";
        var s = CoopTests.Session(t, start);
        var co = s.Campaign.Coop!;
        t.Equal("blue", co.Seats[0].Player.ColorPicked, "seat 1's colour");
        t.Equal(Colors.IndexOf("blue"), co.Seats[0].Player.ColorSlot, "seat 1 blue");
        t.Equal("#40e0a0", co.Seats[1].Player.ColorPicked, "seat 2's custom colour");
        t.Equal(0x40e0a0, co.Seats[1].Player.PartyColor ?? -1, "seat 2's party colour as picked");
        t.True(co.Seats[2].Player.ColorPicked == null && co.Seats[2].Player.ColorSlot != Colors.IndexOf("blue"), "the AI commander's own colour, not seat 1's");

        var clash = CoopTests.CoopStart(t, "base", 3, new[] { ("human", "Hu"), ("human", "Aje"), ("ai", "Ninigi") });
        clash.Coop!.Seats[0].Color = "blue";
        clash.Coop.Seats[1].Color = "blue";
        var s2 = CoopTests.Session(t, clash);
        var p2 = s2.Campaign.Coop!.Seats[1].Player;
        t.True(p2.ColorPicked == null && p2.ColorSlot != Colors.IndexOf("blue"), "seat 2 can't take seat 1's colour: its own instead");
    }
}
