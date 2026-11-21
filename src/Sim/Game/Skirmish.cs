// The skirmish game's setup (the original's pre-game window in skirmish mode: PlayerListWindow / PlayerInfoSlot /
// LobbySettingsPage, ServerApp.usl game start; docs/spec/ui_groups7_9.md §10). The setup menu (game/src/UI/SkirmishMenu)
// edits it, GameSession starts the game from it (SkirmishSession). It is a plain JSON object - the shape of the JS
// setup - so that a lobby can fill it later and send it to the other machines of a lockstep game (docs/SKIRMISH.md).
//
//   setup = { map, mapName, seed, mode: 'deathmatch' | 'domination' | 'defender', defenderMin, points (-1 predefined,
//     1000, 3000, 10000), warpgate, fog, lockDiplomacy, handicap, chooseHQ, teams, debug,
//     slots: [{ type: 'human' | 'ai' | 'closed', tribe | 'Random', personality: 'Random' | 'Defensive' | 'Balanced' |
//              'Offensive' | 'Passive', level 0..9, team 1..16, color (Colors id or "#rrggbb"), hp 50..150, hq 0..15,
//              army / armyTribe / armyPoints / preset (the human's army: game/pointbuy.js) }] }
//   Slot 0 is the human player. A new AI slot: level 5, team (slot % teams) + 1 (PIS:755-777).
//
// Ported from remake/src/game/skirmish.js (pw-exporter 98c7703); resolveSetup is SkirmishSession.Resolve.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.Game;

/// <summary>a slot of the skirmish setup (skirmish.js defaultSetup)</summary>
public sealed class SkirmishSlot
{
    /// <summary>"human" | "ai" | "closed"; a network lobby (docs/MULTIPLAYER.md) also "open": waiting for a human to
    /// join (every human slot of a lobby has its Owner)</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "ai";
    /// <summary>a network game: the name of the human who plays this slot (null in a single player game)</summary>
    [JsonPropertyName("owner")] public string? Owner { get; set; }
    /// <summary>Hu, Aje, Ninigi, SEAS or "Random" (one of the start position's tribes)</summary>
    [JsonPropertyName("tribe")] public string Tribe { get; set; } = "Random";
    /// <summary>the "Army" column of a computer player: Random | Defensive | Balanced | Offensive | Passive</summary>
    [JsonPropertyName("personality")] public string Personality { get; set; } = "Random";
    /// <summary>the computer player's level 0-9 (easy 1, normal 4, hard 8)</summary>
    [JsonPropertyName("level")] public double Level { get; set; } = 5;
    [JsonPropertyName("team")] public int Team { get; set; } = 1;
    /// <summary>the party colour: a palette id (Colors.PLAYER_COLORS: the original's yellow red cyan blue green purple pink
    /// orange, the remake's white grey lime teal navy maroon brown olive), a colour of the whole spectrum "#rrggbb", or
    /// "none" (untinted)</summary>
    [JsonPropertyName("color")] public string? Color { get; set; }
    /// <summary>handicap: hit points in % (50..150), with setup.handicap</summary>
    [JsonPropertyName("hp")] public int Hp { get; set; } = 100;
    /// <summary>the headquarters position (start slot of the map), with setup.chooseHQ; else a free one at random</summary>
    [JsonPropertyName("hq")] public int Hq { get; set; }
    /// <summary>the human's army: the default preset kind (standard | boomer | rusher) or "custom" (Army)</summary>
    [JsonPropertyName("preset")] public string? Preset { get; set; }
    /// <summary>the army bought on the "My Army" page, for this tribe and budget</summary>
    [JsonPropertyName("army")] public Army? Army { get; set; }
    [JsonPropertyName("armyTribe")] public string? ArmyTribe { get; set; }
    [JsonPropertyName("armyPoints")] public double? ArmyPoints { get; set; }
    /// <summary>the remake's: a computer player's behaviour directly (Dodo | Giraffe | Schnecke | Turtle | Mikrobe), over
    /// the personality (tests); resolved: the behaviour played</summary>
    [JsonPropertyName("behaviour")] public string? Behaviour { get; set; }

    public SkirmishSlot Clone()
    {
        var c = (SkirmishSlot)MemberwiseClone();
        c.Army = Army?.Clone();
        return c;
    }
    /// <summary>the army of the "My Army" page belongs to this tribe and budget</summary>
    public bool OwnArmy(double points) => Army != null && ArmyTribe == Tribe && ArmyPoints == points;
}

/// <summary>the skirmish setup (skirmish.js)</summary>
public sealed class SkirmishSetup
{
    /// <summary>the map: a path below the installation's Data folder ("Base/Maps/Multiplayer/multi_2_jun_001.ula"), an
    /// absolute path (a map file the player picked), or "" = a random jungle (game/mapgen.js)</summary>
    [JsonPropertyName("map")] public string Map { get; set; } = "";
    [JsonPropertyName("mapName")] public string? MapName { get; set; }
    /// <summary>multiplayer: the content hash of the host's map (MapFiles.ContentHash of every file of it): a player whose
    /// copy differs - or who has none - is told so in the lobby (LobbyMap.Problem); null: not checked (a random map, a
    /// single player game, a dedicated server's setup without it)</summary>
    [JsonPropertyName("mapHash")] public string? MapHash { get; set; }
    [JsonPropertyName("seed")] public ulong Seed { get; set; } = 1234;
    /// <summary>deathmatch | domination | defender</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "deathmatch";
    /// <summary>Defender: minutes the defender has to hold out</summary>
    [JsonPropertyName("defenderMin")] public double DefenderMin { get; set; } = 45;
    /// <summary>the army budget: -1 predefined (the tribe's start), 1000, 3000, 10000</summary>
    [JsonPropertyName("points")] public double Points { get; set; } = 1000;
    [JsonPropertyName("warpgate")] public bool Warpgate { get; set; } = true;
    /// <summary>the remake's: minutes a finished warp gate must stand (the original: 10)</summary>
    [JsonPropertyName("warpgateMinutes")] public double WarpgateMinutes { get; set; } = 10;
    /// <summary>fog of war on (the setup's "Disable fog of war" unticked; FOWEnabled)</summary>
    [JsonPropertyName("fog")] public bool Fog { get; set; } = true;
    [JsonPropertyName("lockDiplomacy")] public bool LockDiplomacy { get; set; }
    [JsonPropertyName("handicap")] public bool Handicap { get; set; }
    [JsonPropertyName("chooseHQ")] public bool ChooseHQ { get; set; }
    [JsonPropertyName("teams")] public int Teams { get; set; } = 2;
    /// <summary>MIRAGE's "MaxEpoch 6": the sixth stage (final_stage) can be researched (GameplayOptions; MIRAGE's default on)</summary>
    [JsonPropertyName("epochSix")] public bool EpochSix { get; set; } = true;
    /// <summary>MIRAGE's "RemoveTitans": no titans (the xenage/titans_disabled filters)</summary>
    [JsonPropertyName("removeTitans")] public bool RemoveTitans { get; set; }
    /// <summary>MIRAGE's "FlyingEnabled": flying units (off: the xenage/flying_disabled filters; MIRAGE's default off)</summary>
    [JsonPropertyName("flyingEnabled")] public bool FlyingEnabled { get; set; }
    /// <summary>MIRAGE's "Portals": the portal termini (only without the warp gate victory; MIRAGE's default off)</summary>
    [JsonPropertyName("portals")] public bool Portals { get; set; }
    /// <summary>MIRAGE's "BldgMaxCapacity": building limits (max_quantity; World.BuildingLimits; MIRAGE's default off)</summary>
    [JsonPropertyName("buildingLimit")] public bool BuildingLimit { get; set; }
    /// <summary>the debug options chosen before the start (cheats.js; null: none)</summary>
    [JsonPropertyName("debug")] public DebugOptions? Debug { get; set; }
    [JsonPropertyName("slots")] public List<SkirmishSlot> Slots { get; set; } = new();

    public SkirmishSetup Clone()
    {
        var c = (SkirmishSetup)MemberwiseClone();
        c.Slots = Slots.Select(s => s.Clone()).ToList();
        c.Debug = Debug?.Normalized();
        return c;
    }
    public string ToJson() => JsonSerializer.Serialize(this, s_json);
    public static SkirmishSetup? FromJson(string json) { try { return JsonSerializer.Deserialize<SkirmishSetup>(json, s_json); } catch (JsonException) { return null; } }
    static readonly JsonSerializerOptions s_json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

public static class Skirmish
{
    /// <summary>the slots of a setup: the original's eight, the remake's sixteen (docs/SKIRMISH.md)</summary>
    public const int MAX_SLOTS = 16;
    public static readonly (string Id, string Name)[] MODES = { ("deathmatch", "Deathmatch"), ("domination", "Domination"), ("defender", "Defender") };
    public static string ModeName(string mode) => MODES.FirstOrDefault(m => m.Id == mode).Name ?? mode;
    /// <summary>the map's game type strings (LobbySettingsPage.usl:269-283: "KingOfTheHill" is the old name of Domination)</summary>
    public static string? ModeOfType(string type) => type switch { "Deathmatch" => "deathmatch", "Domination" => "domination", "KingOfTheHill" => "domination", "Defender" => "defender", _ => null };
    /// <summary>the "Army" column of an AI slot = its personality (PIS:601-642; ServerApp: ai_Random picks Dodo, Giraffe or
    /// Schnecke)</summary>
    public static readonly (string Name, string? Behaviour)[] PERSONALITIES = { ("Random", null), ("Defensive", "Schnecke"), ("Balanced", "Giraffe"), ("Offensive", "Dodo"), ("Passive", "Mikrobe") };
    public static string? BehaviourOf(string personality) => PERSONALITIES.FirstOrDefault(p => p.Name == personality).Behaviour;
    public static readonly string[] TRIBES = { "Hu", "Aje", "Ninigi", "SEAS" };
    public static readonly double[] POINTS = { -1, 1000, 3000, 10000 };

    /// <summary>skirmish.js defaultSetup: you (Hu, blue), one computer player (Aje, red), the other slots closed</summary>
    public static SkirmishSetup DefaultSetup(string me = "Hu", string ai = "Aje")
    {
        var s = new SkirmishSetup();
        for (int i = 0; i < MAX_SLOTS; i++)
            s.Slots.Add(new SkirmishSlot
            {
                Type = i == 0 ? "human" : i == 1 ? "ai" : "closed", Tribe = i == 0 ? me : i == 1 ? ai : "Random", Personality = "Random", Level = 5,
                Team = (i % 2) + 1, Color = i == 0 ? "blue" : i == 1 ? "red" : null, Hp = 100, Hq = i,
            });
        // every slot a colour of its own, the closed ones too (ready when they are opened)
        foreach (var sl in s.Slots)
            if (sl.Color == null) sl.Color = (Colors.PLAYER_COLORS.FirstOrDefault(c => !s.Slots.Any(o => o.Color == c.Id)) ?? Colors.PLAYER_COLORS[0]).Id;
        return s;
    }

    /// <summary>the first party colour (palette order) no other open slot has or comes near (Colors.NEAR_DISTANCE)</summary>
    public static string FreeColor(IList<SkirmishSlot> slots, SkirmishSlot? except = null)
    {
        var used = slots.Where(s => s != except && s.Type != "closed").Select(s => Colors.ColorById(s.Color)).Where(c => c != null).Select(c => Colors.Hex(c!.Light)).ToList();
        return (Colors.PLAYER_COLORS.FirstOrDefault(x => !used.Any(u => Colors.Distance(u, Colors.Hex(x.Light)) < Colors.NEAR_DISTANCE)) ?? Colors.PLAYER_COLORS[0]).Id;
    }

    /// <summary>two slots (indices) of the players whose colours are the same or too alike to tell apart (null: none)</summary>
    public static (int A, int B)? ColorClash(SkirmishSetup setup)
    {
        var act = ActiveSlots(setup).Select(x => (x.Index, C: Colors.ColorById(x.Slot.Color))).Where(x => x.C != null).ToList();
        for (int i = 0; i < act.Count; i++)
            for (int j = i + 1; j < act.Count; j++)
                if (act[i].C!.Id == act[j].C!.Id || Colors.Distance(Colors.Hex(act[i].C!.Light), Colors.Hex(act[j].C!.Light)) < Colors.NEAR_DISTANCE) return (act[i].Index, act[j].Index);
        return null;
    }

    /// <summary>a slot switched to "AI" (PIS:755-777): level 5, team (slot % teams) + 1, a colour of its own</summary>
    public static void OpenSlot(SkirmishSetup setup, int i)
    {
        var sl = setup.Slots[i];
        sl.Type = "ai"; sl.Level = 5; sl.Personality = "Random"; sl.Team = (i % Math.Max(1, setup.Teams)) + 1; sl.Hp = 100;
        var mine = Colors.ColorById(sl.Color);
        if (sl.Color != "none" && (mine == null || setup.Slots.Where((o, k) => k != i && o.Type != "closed").Select(o => Colors.ColorById(o.Color)).Any(c => c != null && Colors.Distance(Colors.Hex(c.Light), Colors.Hex(mine.Light)) < Colors.NEAR_DISTANCE)))
            sl.Color = FreeColor(setup.Slots, sl);
    }

    /// <summary>the rules a game type forces (LobbySettingsPage.usl:311-352): Domination and Defender lock diplomacy and
    /// switch the warp gate off by default, Defender lets the players choose head quarters (the one on the first defends)</summary>
    public static void ApplyMode(SkirmishSetup setup, string mode)
    {
        setup.Mode = mode;
        setup.Warpgate = mode == "deathmatch";
        if (mode != "deathmatch") setup.LockDiplomacy = true;
        if (mode == "defender") setup.ChooseHQ = true;
    }

    /// <summary>the setup stored in the settings (older ones: filled up to the current form); null: the default</summary>
    public static SkirmishSetup LoadSetup(SkirmishSetup? stored)
    {
        var d = DefaultSetup();
        if (stored == null || stored.Slots == null) return d;
        var s = stored.Clone();
        while (s.Slots.Count < MAX_SLOTS) s.Slots.Add(d.Slots[s.Slots.Count]);
        if (s.Slots.Count > MAX_SLOTS) s.Slots.RemoveRange(MAX_SLOTS, s.Slots.Count - MAX_SLOTS);
        s.Slots[0].Type = "human";
        foreach (var sl in s.Slots.Skip(1)) if (sl.Type == "human") sl.Type = "ai";
        return s;
    }

    /// <summary>the slots that play (with their index in the setup)</summary>
    public static List<(SkirmishSlot Slot, int Index)> ActiveSlots(SkirmishSetup setup) =>
        setup.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type != "closed").ToList();

    /// <summary>what the start screen refuses (PlayerListWindow.usl:334-456) -> a message or null</summary>
    public static string? Validate(SkirmishSetup setup, int maxPlayers)
    {
        var act = ActiveSlots(setup).Select(x => x.Slot).ToList();
        if (act.Count > maxPlayers) return $"This map has room for {maxPlayers} players.";
        if (ColorClash(setup) is { } cc)
            return setup.Slots[cc.A].Color == setup.Slots[cc.B].Color ? "Every player needs a colour of their own."
                : $"The colours of players {cc.A + 1} and {cc.B + 1} look alike: every player needs a colour of their own.";
        if (setup.ChooseHQ)
        {
            var hq = act.Select(s => s.Hq).ToList();
            if (hq.Distinct().Count() != hq.Count) return "Every player needs a headquarters position of their own.";
            if (hq.Any(h => h >= maxPlayers)) return $"This map has {maxPlayers} headquarters positions.";
        }
        if (setup.Mode == "defender" && setup.ChooseHQ && !act.Any(s => s.Hq == 0)) return "Somebody has to defend: headquarters position 1.";
        if (setup.LockDiplomacy && setup.Mode != "defender" && act.Select(s => s.Team).Distinct().Count() < 2) return "With locked diplomacy there must be at least two teams.";
        return null;
    }
}
