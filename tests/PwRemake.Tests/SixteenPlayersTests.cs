// The remake's games of up to 16 players (docs/SKIRMISH.md "16 players"): the colour palette and colours of the whole
// spectrum, the random jungle's sizes, a 16-player skirmish that runs the same twice and through a save, an old
// (8-slot) setup still loading. The network side: MultiplayerTests.SixteenPlayers.
using System;
using System.IO;
using System.Linq;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.Sim.World;
using C = PwRemake.Sim.Rules.Colors;

namespace PwRemake.Tests;

[TestClass]
public static class SixteenPlayersTests
{
    public static void PaletteAndCustomColours(TestContext t)
    {
        t.Equal(16, C.PLAYER_COLORS.Count, "sixteen palette colours");
        t.Equal("orange", C.PLAYER_COLORS[7].Id, "the original's eight first, in ACColors order");
        for (int i = 0; i < 16; i++)
            for (int j = i + 1; j < 16; j++)
                t.True(C.Distance(C.Hex(C.PLAYER_COLORS[i].Light), C.Hex(C.PLAYER_COLORS[j].Light)) >= C.NEAR_DISTANCE * 1.5, $"{C.PLAYER_COLORS[i].Id} / {C.PLAYER_COLORS[j].Id} far apart");
        var cu = C.ColorById("#12ab34")!;
        t.True(cu.Light.SequenceEqual(new[] { 0x12, 0xab, 0x34 }) && cu.Id == "#12ab34", "a custom colour: the light one as picked");
        t.True(cu.Dark[1] < cu.Light[1] && cu.Dark[0] <= cu.Light[0], "its dark one darker");
        t.Equal(-1, C.IndexOf("#12ab34"), "not in the palette");
        t.Equal(null, C.ColorById("#12ab3"), "a bad id");
        t.Equal("#00ff00", C.CustomId(0x00ff00), "id of an RGB value");
        // near-duplicates are refused, a free colour avoids them
        var s = Skirmish.DefaultSetup();
        t.Equal(16, s.Slots.Count, "16 slots");
        t.Equal(16, s.Slots.Select(x => x.Color).Distinct().Count(), "every slot its own colour");
        t.Equal(null, Skirmish.Validate(s, 2), "the default is valid");
        s.Slots[1].Color = "#7aa0f0";                                   // ~ blue (slot 0)
        t.True(Skirmish.Validate(s, 2)?.Contains("look alike") == true, "a colour close to another refused");
        s.Slots[1].Color = "blue";
        t.Equal("Every player needs a colour of their own.", Skirmish.Validate(s, 2), "the same colour refused");
        s.Slots[1].Color = "#ff00ff";
        t.Equal(null, Skirmish.Validate(s, 2), "a custom colour of its own");
        Skirmish.OpenSlot(s, 2); s.Slots[2].Color = "#fe02fd";
        Skirmish.OpenSlot(s, 2);
        t.True(s.Slots[2].Color != "#fe02fd" && Skirmish.ColorClash(s) == null, "opening a slot moves it off a near colour");
    }

    public static void RandomJungleSizes(TestContext t)
    {
        foreach (var (n, size) in new[] { (4, 480.0), (5, 800.0), (8, 800.0), (9, 1120.0), (16, 1120.0) })
        {
            var S = MapSource.GeneratedSource(new MapSourceConfig { Players = n, Seed = 5 });
            t.Equal(n, S.Starts.Count, $"{n} bases");
            t.Equal(size + 160, S.Size, $"{n} players: the size");
            t.Equal(16.0, S.Mp.MaxPlayers, "up to 16 players");
            double min = double.MaxValue;
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) min = Math.Min(min, Math.Sqrt(Math.Pow(S.Starts[i][0] - S.Starts[j][0], 2) + Math.Pow(S.Starts[i][1] - S.Starts[j][1], 2)));
            t.True(min >= 255, $"{n} players: room between the bases ({min:0} m)");
            t.True(S.Starts.All(b => S.Play.Inside(b[0], b[1], 80)), "every base well inside the map");
            if (n == 16) t.Log($"huge jungle: {S.Trees.Count} trees, {S.Stones.Count} stones, {S.Animals.Count} animals");
        }
    }

    static SkirmishSetup Sixteen(ulong seed)
    {
        var s = Skirmish.DefaultSetup("Hu", "Aje");
        s.Map = ""; s.Seed = seed; s.Teams = 16;
        string[] tr = { "Hu", "Aje", "Ninigi" };
        for (int i = 0; i < 16; i++)
        {
            if (i > 0) Skirmish.OpenSlot(s, i);
            s.Slots[i].Tribe = tr[i % 3]; s.Slots[i].Team = i + 1; s.Slots[i].Level = 8;
        }
        s.Slots[3].Color = "#ff00ff";
        return s;
    }

    /// <summary>you and 15 computer players on the huge jungle: every player placed with its colour and team, the same
    /// world twice, a save replayed to the same hash</summary>
    public static void SixteenPlayerSkirmish(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no configuration base");
        var setup = Sixteen(31);
        t.Equal(null, Skirmish.Validate(setup, 16), "a valid setup");
        var start = new GameStart { Configuration = "base", Mode = GameMode.Skirmish, Skirmish = setup, Seed = setup.Seed };
        SkirmishSession Make() => SkirmishSession.Create(start.Skirmish!.Clone(), null, c!.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, null, SimFlags.Game);
        var a = Make();
        var W = a.World;
        t.Equal(16, W.Players.Count, "16 players");
        t.Equal(15, a.Ai.Brains.Count, "15 computer players");
        t.Equal(16, a.Homes.Count, "each with its home");
        t.Equal(16, W.Players.Select(p => p.Team).Distinct().Count(), "16 teams");
        t.True(W.Players[0].IsEnemy(W.Players[15]) && W.Players[15].IsEnemy(W.Players[9]), "every one against every one");
        t.True(W.Players[3].PartyColor == 0xff00ff && W.Players[3].ColorSlot == -1, "the custom colour");
        t.True(W.Players[12].ColorSlot == 12 && W.Players[12].PartyColor == C.Hex(C.PLAYER_COLORS[12].Light), "a remake palette colour");
        var b0 = W.Players.Select(p => W.Buildings.Count(x => x.Alive && x.Owner == p)).ToArray();
        var rec = new GameRecorder(W, a.StateHash, null);
        int n = (int)(120 / World.STEP);
        for (int i = 0; i < n; i++) a.Step();
        int bld = W.Players.Count(p => W.Buildings.Count(x => x.Alive && x.Owner == p) > b0[p.Id]);
        t.Log($"2 min: {W.Units.Count(u => u.Alive)} units, {W.Buildings.Count(b => b.Alive)} buildings, {bld} players building, hash {a.StateHash():x}");
        t.True(bld >= 12, $"the computer players build ({bld})");
        var b = Make();
        for (int i = 0; i < n; i++) b.Step();
        t.Equal(a.StateHash(), b.StateHash(), "the same world twice");
        var save = rec.Capture(start, SimFlags.Game, System.Text.Json.JsonSerializer.SerializeToNode(start.Skirmish));
        var file = Path.Combine(Path.GetTempPath(), $"pw16_{Guid.NewGuid():N}.pwsave");
        save.Write(file);
        var loaded = SaveGame.Read(file);
        File.Delete(file);
        t.Equal("#ff00ff", loaded.Start.Skirmish!.Slots[3].Color, "the colour in the save");
        var r = SkirmishSession.Create(loaded.Start.Skirmish!.Clone(), null, c!.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, null, loaded.Flags);
        var rep = new SaveReplay(loaded, r.World, r.StateHash, null);
        rep.Run(); rep.End();
        t.True(!rep.Diverged, $"the save replays ({rep.Problem})");
        t.Equal(a.StateHash(), r.StateHash(), "the loaded game is the saved one");
    }

    /// <summary>a setup of before (eight slots, as old saves and settings keep it) still loads; the settings' one is
    /// filled up to 16 slots</summary>
    public static void OldEightSlotSetupLoads(TestContext t)
    {
        var old = Skirmish.DefaultSetup();
        old.Slots.RemoveRange(8, 8);
        var json = old.ToJson();
        var back = SkirmishSetup.FromJson(json)!;
        t.Equal(8, back.Slots.Count, "a save's setup keeps its eight slots");
        t.Equal(2, Skirmish.ActiveSlots(back).Count, "and its players");
        var filled = Skirmish.LoadSetup(back);
        t.Equal(16, filled.Slots.Count, "the stored setup filled up to 16");
        t.True(filled.Slots.Skip(8).All(s => s.Type == "closed") && Skirmish.ColorClash(filled) == null, "the new slots closed, colours of their own");
    }
}
