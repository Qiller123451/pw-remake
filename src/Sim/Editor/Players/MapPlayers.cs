// The map editor's players (docs/MAP_EDITOR.md "Players and map settings", plan phase 4): the map's eight player slots
// as the original's level editor keeps them in the level's description tree (Root/PlayerSettings/Player_<n>: PlayerName,
// Restrictions/Base/Tribes "Hu:Aje:Ninigi:SEAS:", Tribes/Default, DefTeam, DefPlayer human | computer, DefColor,
// IncludeBuildings, Restrictions/Resources/food | wood | stone | iron, Restrictions/Chars/Population/Max, Diplomacy
// "10001111" - one digit per player: this slot's relation towards it, 0 hostile, 1 neutral, 2 friendly; ServerApp.usl
// 539-551 reads it for the campaign) and the game's defaults the remake adds (Root/Remake/...: the army budget, the
// head quarters choice, MIRAGE's GameplayOptions). A slot plays when the map has a start location (an SLOC object) of
// its index: the start locations are objects of the map (the object tools place and move them), the slots their
// settings. No Godot types.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

/// <summary>one of the map's player slots (a start location's player)</summary>
public sealed class MapPlayerSlot
{
    /// <summary>the slot's name (Root/PlayerSettings/Player_n/PlayerName: "Player_1" ...)</summary>
    public string Name = "";
    /// <summary>the tribes the slot may play (Restrictions/Base/Tribes); empty: every playable tribe</summary>
    public List<string> Tribes = new();
    /// <summary>the tribe it starts as (Tribes/Default; "Random": one of Tribes)</summary>
    public string Tribe = "Random";
    /// <summary>its team (DefTeam, 1..)</summary>
    public int Team = 1;
    /// <summary>its colour: a palette id of Colors.PLAYER_COLORS (DefColor holds the original's index 0..7)</summary>
    public string Color = "";
    /// <summary>who plays it: "human", "computer" or "either" (DefPlayer; the original's maps say human)</summary>
    public string Control = "either";
    /// <summary>start resources (Restrictions/Resources; all 0: the tribe's own start, as on every original map)</summary>
    public double Food, Wood, Stone, Skulls;
    /// <summary>the population limit (Restrictions/Chars/Population/Max; 0: the game's)</summary>
    public double PopMax;
    /// <summary>the main building at the start (IncludeBuildings)</summary>
    public bool IncludeBuildings = true;
    /// <summary>this slot's relation towards each slot: 0 hostile, 1 neutral, 2 friendly (Diplomacy)</summary>
    public int[] Diplomacy = new int[MapPlayerSettings.SLOTS];

    public MapPlayerSlot Clone()
    {
        var c = (MapPlayerSlot)MemberwiseClone();
        c.Tribes = new List<string>(Tribes);
        c.Diplomacy = (int[])Diplomacy.Clone();
        return c;
    }
    /// <summary>any start resources set (else the tribe's own start applies)</summary>
    public bool HasResources => Food > 0 || Wood > 0 || Stone > 0 || Skulls > 0;
}

/// <summary>the map's players: the eight slots and the game's defaults for them</summary>
public sealed class MapPlayerSettings
{
    /// <summary>the original's eight player slots (a map's start locations)</summary>
    public const int SLOTS = 8;
    public static readonly string[] CONTROLS = { "human", "computer", "either" };
    public static readonly string[] TRIBES = { "Hu", "Aje", "Ninigi", "SEAS" };
    public List<MapPlayerSlot> Slots = new();
    /// <summary>the army budget the skirmish starts with (-1 the tribe's predefined start, 1000, 3000, 10000)</summary>
    public double Points = -1;
    /// <summary>the players choose their head quarters (a slot's start location) instead of a random one</summary>
    public bool ChooseHQ = true;
    /// <summary>MIRAGE's game options (GameplayOptions) as the map's defaults</summary>
    public bool EpochSix = true, RemoveTitans, FlyingEnabled, Portals, BuildingLimit;

    public MapPlayerSettings Clone()
    {
        var c = (MapPlayerSettings)MemberwiseClone();
        c.Slots = Slots.Select(s => s.Clone()).ToList();
        return c;
    }

    /// <summary>eight slots with the original's defaults (teams 1 and 2 alternating, the palette's first colours, every
    /// tribe, hostile to the others - "1" towards itself, as the original's maps)</summary>
    public static MapPlayerSettings Default()
    {
        var p = new MapPlayerSettings();
        for (int i = 0; i < SLOTS; i++) p.Slots.Add(DefaultSlot(i));
        return p;
    }

    static MapPlayerSlot DefaultSlot(int i)
    {
        var s = new MapPlayerSlot
        {
            Name = $"Player_{i + 1}", Team = i % 2 + 1, Color = PwRemake.Sim.Rules.Colors.PLAYER_COLORS[i % PwRemake.Sim.Rules.Colors.PLAYER_COLORS.Count].Id,
        };
        for (int j = 0; j < SLOTS; j++) s.Diplomacy[j] = j == i ? 1 : 0;        // (the original's maps: "1" towards itself)
        return s;
    }

    // ---------------------------------------------------------------- the level's description tree

    const string P = "Root/PlayerSettings/Player_";
    const string R = "Root/Remake/";
    static readonly string[] RES = { "food", "wood", "stone", "iron" };
    static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
    static double Get(Dictionary<string, string> d, string k, double def) =>
        d.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : def;
    static bool Flag(Dictionary<string, string> d, string k, bool def) => d.TryGetValue(k, out var v) ? v == "1" || v == "true" : def;

    /// <summary>the players as the level's description says (missing keys: the defaults)</summary>
    public static MapPlayerSettings Read(Dictionary<string, string> d)
    {
        var p = Default();
        for (int i = 0; i < SLOTS; i++)
        {
            var s = p.Slots[i];
            string k = P + i + "/";
            if (d.TryGetValue(k + "PlayerName", out var nm) && nm.Length > 0) s.Name = nm;
            if (d.TryGetValue(k + "Restrictions/Base/Tribes", out var tr)) s.Tribes = tr.Split(':').Where(t => t.Length > 0).ToList();
            if (d.TryGetValue(k + "Restrictions/Base/Tribes/Default", out var dt) && dt.Length > 0) s.Tribe = dt;
            s.Team = (int)Get(d, k + "Restrictions/Base/DefTeam", s.Team);
            if (d.TryGetValue(k + "Restrictions/Base/DefPlayer", out var dp)) s.Control = dp is "human" or "computer" or "either" ? dp : dp == "ai" ? "computer" : "either";
            if (d.TryGetValue(R + "Players/" + i + "/Color", out var col) && col.Length > 0) s.Color = col;
            else if (d.TryGetValue(k + "Restrictions/Base/DefColor", out var dc) && int.TryParse(dc, out var ci) && ci >= 0 && ci < PwRemake.Sim.Rules.Colors.PLAYER_COLORS.Count)
                s.Color = PwRemake.Sim.Rules.Colors.PLAYER_COLORS[ci].Id;
            s.Food = Get(d, k + "Restrictions/Resources/food", 0); s.Wood = Get(d, k + "Restrictions/Resources/wood", 0);
            s.Stone = Get(d, k + "Restrictions/Resources/stone", 0); s.Skulls = Get(d, k + "Restrictions/Resources/iron", 0);
            s.PopMax = Get(d, k + "Restrictions/Chars/Population/Max", 0);
            s.IncludeBuildings = Flag(d, k + "Restrictions/Base/IncludeBuildings", Flag(d, k + "IncludeBuildings", true));
            if (d.TryGetValue(k + "Diplomacy", out var dip))
                for (int j = 0; j < SLOTS && j < dip.Length; j++) s.Diplomacy[j] = dip[j] is >= '0' and <= '2' ? dip[j] - '0' : 1;
        }
        p.Points = Get(d, R + "Points", -1);
        p.ChooseHQ = Flag(d, R + "ChooseHQ", true);
        p.EpochSix = Flag(d, R + "Options/EpochSix", true);
        p.RemoveTitans = Flag(d, R + "Options/RemoveTitans", false);
        p.FlyingEnabled = Flag(d, R + "Options/FlyingEnabled", false);
        p.Portals = Flag(d, R + "Options/Portals", false);
        p.BuildingLimit = Flag(d, R + "Options/BuildingLimit", false);
        return p;
    }

    /// <summary>writes the players into the level settings (the tree map.lua saves and the key/values MapSource.MpSettingsOf
    /// and the campaign read)</summary>
    public void Write(MapData md)
    {
        LevelSettings.Detach(md);
        void Set(string k, string? v) => LevelSettings.Set(md, k, v);
        for (int i = 0; i < SLOTS && i < Slots.Count; i++)
        {
            var s = Slots[i];
            string k = P + i + "/";
            Set(k + "PlayerName", s.Name);
            Set(k + "Restrictions/Base/Tribes", s.Tribes.Count > 0 ? string.Join("", s.Tribes.Select(t => t + ":")) : string.Join("", TRIBES.Select(t => t + ":")));
            Set(k + "Restrictions/Base/Tribes/Default", s.Tribe);
            Set(k + "Restrictions/Base/DefTeam", s.Team.ToString(CultureInfo.InvariantCulture));
            Set(k + "Restrictions/Base/DefPlayer", s.Control);
            int ci = PwRemake.Sim.Rules.Colors.IndexOf(s.Color);
            Set(k + "Restrictions/Base/DefColor", Math.Max(0, Math.Min(7, ci < 0 ? i : ci)).ToString(CultureInfo.InvariantCulture));
            Set(R + "Players/" + i + "/Color", s.Color);
            Set(k + "Restrictions/Base/IncludeBuildings", s.IncludeBuildings ? "1" : "0");
            Set(k + "IncludeBuildings", s.IncludeBuildings ? "1" : "0");
            double[] res = { s.Food, s.Wood, s.Stone, s.Skulls };
            for (int r = 0; r < 4; r++) Set(k + "Restrictions/Resources/" + RES[r], Num(res[r]));
            Set(k + "Restrictions/Chars/Population/Max", s.PopMax > 0 ? Num(s.PopMax) : null);
            Set(k + "Diplomacy", string.Concat(s.Diplomacy.Select(x => (char)('0' + Math.Clamp(x, 0, 2)))));
        }
        Set(R + "Points", Num(Points));
        Set(R + "ChooseHQ", ChooseHQ ? "1" : "0");
        Set(R + "Options/EpochSix", EpochSix ? "1" : "0");
        Set(R + "Options/RemoveTitans", RemoveTitans ? "1" : "0");
        Set(R + "Options/FlyingEnabled", FlyingEnabled ? "1" : "0");
        Set(R + "Options/Portals", Portals ? "1" : "0");
        Set(R + "Options/BuildingLimit", BuildingLimit ? "1" : "0");
    }

    /// <summary>sets the relation of slots a and b both ways (the diplomacy table of a mission)</summary>
    public void SetRelation(int a, int b, int rel)
    {
        if (a == b || a < 0 || b < 0 || a >= Slots.Count || b >= Slots.Count) return;
        Slots[a].Diplomacy[b] = Slots[b].Diplomacy[a] = Math.Clamp(rel, 0, 2);
    }

    /// <summary>the relations made from the teams: one team friendly, the others hostile</summary>
    public void DiplomacyFromTeams()
    {
        for (int a = 0; a < Slots.Count; a++)
            for (int b = 0; b < Slots.Count; b++)
                Slots[a].Diplomacy[b] = a == b ? 1 : Slots[a].Team == Slots[b].Team ? 2 : 0;
    }
}
