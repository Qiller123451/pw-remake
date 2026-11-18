// MIRAGE's game options "sixth stage" (MaxEpoch 6) and "no titans" (RemoveTitans) in every way a game starts (Kacper's
// decision 2026-11-13: the setting in the multiplayer, campaign and co-op lobbies, default on as MIRAGE's): the
// multiplayer skirmish lobby (LobbyState.Skirmish -> GameStart.Skirmish), a campaign mission (GameStart.Gameplay ->
// CampaignConfig.Gameplay -> every player), the co-op lobby (LobbyState.Campaign.Gameplay -> LobbyStart.Make ->
// the seats' new players, the co-op enemies, an ally a seat takes over in another tribe - CoopRuntime's tree swap).
// Each path: final_stage offered at epoch 5 with the option, not without it (Rules.GameplayOptions, the xenage filters).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PwRemake.Net;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Coop;
using PwRemake.Sim.Game;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class MirageGameOptionsPathsTests
{
    static readonly Dictionary<string, string> BASE = new() { ["SEAS"] = "seas_headquarters", ["Hu"] = "hu_fireplace", ["Aje"] = "aje_resource_collector", ["Ninigi"] = "ninigi_fireplace" };

    /// <summary>the player's tree has the option filters as the options say, and final_stage is offered at epoch 5 (or
    /// hidden without the sixth stage)</summary>
    static void Check(TestContext t, W_ w, Player p, bool epochSix, bool noTitans, string what, bool offer = true)
    {
        if (!BASE.ContainsKey(p.Tribe)) return;
        t.Equal(epochSix, (p.Gameplay ?? GameplayOptions.Default).EpochSix, $"{what}: player {p.Id} ({p.Tribe}) sixth stage option");
        t.Equal(noTitans, (p.Gameplay ?? GameplayOptions.Default).RemoveTitans, $"{what}: player {p.Id} no titans option");
        t.Equal(epochSix, p.Tt!.EnabledCount($"{p.Tribe}/Upgrades/xenage/epoch_six_enabled") > 0, $"{what}: player {p.Id} tree: epoch_six_enabled");
        t.Equal(noTitans, p.Tt!.EnabledCount($"{p.Tribe}/Upgrades/xenage/titans_disabled") > 0, $"{what}: player {p.Id} tree: titans_disabled");
        if (!offer) return;
        w.RaiseEpoch(p, 5);
        var path = $"/Actions/{p.Tribe}/Upgrades/{BASE[p.Tribe]}/final_stage";
        var fs = w.Data.Actions(p).FirstOrDefault(a => a.Path == path);
        t.True(fs != null, $"{what}: {path}");
        bool dbg = p.Debug; p.Debug = true;          // (the buildings it needs are not asked for)
        var res = w.Data.Check(p, fs!);
        p.Debug = dbg;
        if (epochSix) t.Equal(null, res, $"{what}: player {p.Id} ({p.Tribe}): final_stage offered at epoch 5");
        else t.Equal("hidden", res, $"{what}: player {p.Id} ({p.Tribe}): final_stage hidden without the sixth stage");
    }

    static CampaignTests.Cfg Cfg(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("mirage");
        if (c == null) t.Skip("configuration mirage not found");
        return c!;
    }

    // ======================================================================================== multiplayer skirmish
    /// <summary>the host's lobby (sent to every machine as JSON) -> GameStart.Skirmish -> SkirmishSession: every player's
    /// tree; the default setup (MIRAGE's: on)</summary>
    public static void MultiplayerSkirmish(TestContext t)
    {
        var c = Cfg(t);
        foreach (var (six, not) in new[] { (false, true), (true, false) })
        {
            var s = Skirmish.DefaultSetup("SEAS", "Hu");
            s.Map = ""; s.Seed = 3; s.Points = -1; s.EpochSix = six; s.RemoveTitans = not;
            s.Slots[0].Type = "human"; s.Slots[0].Owner = "Host"; s.Slots[1].Type = "human"; s.Slots[1].Owner = "Guest"; s.Slots[1].Tribe = "Hu";
            for (int i = 2; i < s.Slots.Count; i++) s.Slots[i].Type = "closed";
            var lobby = new LobbyState { Mode = "skirmish", Configuration = "mirage", Skirmish = s };
            lobby.Peers.Add(new LobbyPeer { Id = 0, Name = "Host", Host = true, Slot = 0 });
            lobby.Peers.Add(new LobbyPeer { Id = 1, Name = "Guest", Ready = true, Slot = 1 });
            // what a client sees: the lobby as the host sent it
            var seen = NetProtocol.FromNode<LobbyState>(NetProtocol.ToNode(lobby))!;
            t.Equal(six, seen.Skirmish!.EpochSix, "the players see the sixth stage option");
            t.Equal(not, seen.Skirmish.RemoveTitans, "the players see the no titans option");
            t.Equal(null, LobbyStart.Problem(seen), "the lobby can start");
            var start = NetProtocol.FromNode<GameStart>(NetProtocol.ToNode(LobbyStart.Make(seen, null)))!;
            t.Equal(six, start.Skirmish!.EpochSix, "GameStart carries the sixth stage option");
            var sk = SkirmishSession.Create(start.Skirmish.Clone(), null, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, flags: SimFlags.Game);
            t.True(sk.World.Players.Count >= 2, "players");
            foreach (var p in sk.World.Players) Check(t, sk.World, p, six, not, $"skirmish epochSix={six}");
        }
    }

    // ======================================================================================== campaign
    static MissionSession Mission(TestContext t, GameStart start)
    {
        var c = Cfg(t);
        if (MissionSession.MissionFolderOf(start, c.Config) == null) t.Skip($"mission {start.Mission} not in mirage");
        return MissionSession.Create(start, c.Config, new SessionOptions { Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = true });
    }

    /// <summary>a campaign mission (the campaign menu / army screen: GameStart.Gameplay): every player; null = MIRAGE's
    /// defaults; the start survives a save (JSON)</summary>
    public static void CampaignMission(TestContext t)
    {
        foreach (var gp in new[] { new GameplayOptions { EpochSix = false, RemoveTitans = true }, null })
        {
            var start = new GameStart { Configuration = "mirage", Mode = GameMode.Campaign, Mission = 3, Difficulty = 1, Seed = 5, Gameplay = gp };
            start = JsonSerializer.Deserialize<GameStart>(JsonSerializer.Serialize(start))!;       // (as a save keeps it)
            t.Equal(gp?.EpochSix, start.Gameplay?.EpochSix, "the save keeps the option");
            var s = Mission(t, start);
            bool six = gp?.EpochSix ?? true, not = gp?.RemoveTitans ?? false;
            var human = s.Campaign.Human!;
            human.UnlockAll = true;
            Check(t, s.World, human, six, not, $"campaign {(gp == null ? "defaults" : "options off")}");
            foreach (var p in s.World.Players.Where(p => p != human)) Check(t, s.World, p, six, not, "campaign", offer: false);
        }
    }

    // ======================================================================================== co-op
    /// <summary>the co-op lobby (offline and online: LobbyState.Campaign.Gameplay, the host's) -> LobbyStart.Make ->
    /// the mission: seat 1, a new seat's player, an AI commander, the co-op enemies; Shared mode keeps it too</summary>
    public static void CoopLobby(TestContext t)
    {
        var c = Cfg(t);
        var def = CoopDefinition.Load("mirage", 3);
        if (def == null) t.Skip("no co-op definition of mission 3");
        var probe = new GameStart { Configuration = "mirage", Mission = 3 };
        var folder = MissionSession.MissionFolderOf(probe, c.Config);
        if (folder == null) t.Skip("mission 3 not in mirage");
        var gp = new GameplayOptions { EpochSix = false, RemoveTitans = true };
        var lobby = new LobbyState
        {
            Mode = "coop", CoopMode = "campaign", Configuration = "mirage",
            Campaign = new GameStart { Configuration = "mirage", Mode = GameMode.Campaign, Mission = 3, MissionFolder = Path.GetFileName(folder!), Tribe = "SEAS", Difficulty = 1, Seed = 9, Gameplay = gp },
            Coop = new CoopLobbyState { Definition = def!.Raw, MissionPlayers = CoopPlan.MissionSlots(folder!) },
        };
        lobby.Coop.Seats[0].Tribe = "SEAS"; lobby.Coop.Seats[1].Kind = "ai"; lobby.Coop.Seats[1].Tribe = "Ninigi";
        lobby.Peers.Add(new LobbyPeer { Id = 0, Name = "Host", Host = true, Ready = true, Seat = 1 });
        var seen = NetProtocol.FromNode<LobbyState>(NetProtocol.ToNode(lobby))!;
        t.Equal(false, seen.Campaign!.Gameplay!.EpochSix, "the players see the host's sixth stage option");
        t.Equal(null, LobbyStart.Problem(seen), "the co-op lobby can start");
        var start = NetProtocol.FromNode<GameStart>(NetProtocol.ToNode(LobbyStart.Make(seen, null)))!;
        t.True(start.Coop != null && start.Gameplay is { EpochSix: false, RemoveTitans: true }, "GameStart carries the options (co-op campaign)");
        var s = Mission(t, start);
        var co = s.Campaign.Coop!;
        t.True(co.Seats.Count == 2 && co.Seats[1].New, "seat 2: a new player");
        Check(t, s.World, co.Seats[0].Player, false, true, "co-op seat 1");
        Check(t, s.World, co.Seats[1].Player, false, true, "co-op seat 2 (AI commander)");
        foreach (var p in s.World.Players) Check(t, s.World, p, false, true, "co-op", offer: false);
        // Shared mode
        var shared = seen.Clone(); shared.CoopMode = "shared"; shared.CoopPlayers = new() { new CoopChoice { Player = 0, Main = true } };
        foreach (var p in shared.Peers) p.Player = 0;
        var ss = LobbyStart.Make(shared, null);
        t.True(ss.Coop == null && ss.Gameplay is { EpochSix: false, RemoveTitans: true }, "GameStart carries the options (Shared)");
    }

    /// <summary>a seat that takes over an ally of another tribe: CoopRuntime makes its tree again in the seat's tribe -
    /// with the game's options</summary>
    public static void CoopAllyTribeSwap(TestContext t)
    {
        var start = CoopTests.CoopStartWith(t, "mirage", 2, CoopTests.AllyDef02, new[] { ("human", "Hu"), ("ai", "SEAS") });
        start.Gameplay = new GameplayOptions { EpochSix = false };
        var s = Mission(t, start);
        var co = s.Campaign.Coop!;
        var ally = co.Seats[1].Player;
        t.True(!co.Seats[1].New, "seat 2 took the mission's ally over");
        t.Equal("SEAS", ally.Tribe, "in the seat's tribe");
        Check(t, s.World, ally, false, false, "co-op ally swapped to SEAS");
        // and with the defaults
        var s2 = Mission(t, CoopTests.CoopStartWith(t, "mirage", 2, CoopTests.AllyDef02, new[] { ("human", "Hu"), ("ai", "SEAS") }));
        Check(t, s2.World, s2.Campaign.Coop!.Seats[1].Player, true, false, "co-op ally swapped to SEAS, defaults");
    }
}
