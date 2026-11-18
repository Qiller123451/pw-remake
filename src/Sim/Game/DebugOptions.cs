// Debug options of a game, chosen before it starts (the menu's "Debug options" window in the campaign menu; tests:
// GameStart.Debug). They are part of the game's start settings, so a saved game and "Restart" keep them.
// docs/DEBUG.md. Ported from remake/src/game/cheats.js (pw-exporter 98c7703): defaultCheats, normCheats, cheatSummary.
//
// "You" is the human player. "Enemies" are the players hostile to you when the game starts (for damage: whoever is
// hostile at the moment of the hit) and everything without an owner (wild animals, neutral buildings). Allies and
// neutral players play by the normal rules.
//
//   On          the options apply at all (off: the game is played as normal, the choices are kept)
//   Free        everything is free and builds, trains and researches at once (the older "debug mode")
//   Reveal      the whole map visible: no fog of war, every place explored
//   Unlock      a mission's bans are ignored: every unit, building and upgrade of your tribe
//   Res         your start resources food, wood, stone, skulls (null = the game's)
//   Storage     your storage limit per resource (null = the buildings')
//   Pyramid     your army pyramid's places per level [l1..l5] (null = the game's / the mission's: 25 15 8 3 1)
//   PopMax      your unit limit (null = the map's / the mission's, 52 in skirmish)
//   NoHousing   your unit limit needs no houses
//   Gather      your gathering × (each load is worth that much more)
//   Speed       your construction, training and research speed ×
//   Me / Foe    StartEpoch 0 | 2..5, MaxEpoch 0 | 1..5, Dmg: damage dealt ×, Def: defence (damage taken ÷)
//   Ai          "normal" | "passive": the computer players' own armies do not attack (scripted attacks still come)
//   FirstShot   the original's first-shot delay (a ranged character's first shot after raising its weapon: 0.4 s, Bela
//               0.6 s; World.FirstShot) - on by default; FirstShotDelay: one time in seconds for every unit (null = the
//               original's). For every player, not only you.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace PwRemake.Sim.Game;

public sealed class DebugSide
{
    [JsonPropertyName("startEpoch")] public int StartEpoch { get; set; }
    [JsonPropertyName("maxEpoch")] public int MaxEpoch { get; set; }
    [JsonPropertyName("dmg")] public double Dmg { get; set; } = 1;
    [JsonPropertyName("def")] public double Def { get; set; } = 1;

    public DebugSide Normalized() => new()
    {
        StartEpoch = Math.Clamp(StartEpoch, 0, 5), MaxEpoch = Math.Clamp(MaxEpoch, 0, 5),
        Dmg = DebugOptions.Fin(Dmg, 0, 100, 1), Def = DebugOptions.Fin(Def, 0.05, 100, 1),
    };
}

public sealed class DebugResources
{
    [JsonPropertyName("food")] public int? Food { get; set; }
    [JsonPropertyName("wood")] public int? Wood { get; set; }
    [JsonPropertyName("stone")] public int? Stone { get; set; }
    [JsonPropertyName("skulls")] public int? Skulls { get; set; }

    public IEnumerable<(string name, int? value)> All()
    {
        yield return ("food", Food); yield return ("wood", Wood); yield return ("stone", Stone); yield return ("skulls", Skulls);
    }
}

public sealed class DebugOptions
{
    [JsonPropertyName("on")] public bool On { get; set; }
    [JsonPropertyName("free")] public bool Free { get; set; }
    [JsonPropertyName("reveal")] public bool Reveal { get; set; }
    [JsonPropertyName("unlock")] public bool Unlock { get; set; }
    [JsonPropertyName("noHousing")] public bool NoHousing { get; set; }
    [JsonPropertyName("res")] public DebugResources Res { get; set; } = new();
    [JsonPropertyName("storage")] public int? Storage { get; set; }
    [JsonPropertyName("pyramid")] public int?[] Pyramid { get; set; } = new int?[5];
    [JsonPropertyName("popMax")] public int? PopMax { get; set; }
    [JsonPropertyName("gather")] public double Gather { get; set; } = 1;
    [JsonPropertyName("speed")] public double Speed { get; set; } = 1;
    [JsonPropertyName("ai")] public string Ai { get; set; } = "normal";
    [JsonPropertyName("me")] public DebugSide Me { get; set; } = new();
    [JsonPropertyName("foe")] public DebugSide Foe { get; set; } = new();
    /// <summary>the original's first-shot delay (World.FirstShot) on</summary>
    [JsonPropertyName("firstShot")] public bool FirstShot { get; set; } = true;
    /// <summary>the first-shot delay of every unit in seconds (null: the original's 0.4 s, Bela 0.6 s)</summary>
    [JsonPropertyName("firstShotDelay")] public double? FirstShotDelay { get; set; }

    internal static double Fin(double v, double lo, double hi, double dflt) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : dflt;
    static int? Clip(int? v, int lo, int hi) => v == null ? null : Math.Clamp(v.Value, lo, hi);

    /// <summary>Every field present and in range (normCheats).</summary>
    public DebugOptions Normalized()
    {
        var p = new int?[5];
        for (int i = 0; i < 5; i++) p[i] = Clip(Pyramid != null && i < Pyramid.Length ? Pyramid[i] : null, 0, 200);
        return new DebugOptions
        {
            On = On, Free = Free, Reveal = Reveal, Unlock = Unlock, NoHousing = NoHousing,
            Res = new DebugResources
            {
                Food = Clip(Res?.Food, 0, 10_000_000), Wood = Clip(Res?.Wood, 0, 10_000_000),
                Stone = Clip(Res?.Stone, 0, 10_000_000), Skulls = Clip(Res?.Skulls, 0, 10_000_000),
            },
            Storage = Clip(Storage, 0, 10_000_000), Pyramid = p, PopMax = Clip(PopMax, 1, 2000),
            Gather = Fin(Gather, 0, 100, 1), Speed = Fin(Speed, 0.1, 100, 1),
            Ai = Ai == "passive" ? "passive" : "normal",
            Me = (Me ?? new()).Normalized(), Foe = (Foe ?? new()).Normalized(),
            FirstShot = FirstShot, FirstShotDelay = FirstShotDelay is double fs && double.IsFinite(fs) ? Math.Round(Math.Clamp(fs, 0, 5), 2) : null,
        };
    }

    /// <summary>The options a game is started with: normalised, or null for a normal game (configCheats).</summary>
    public DebugOptions? ForGame() { var c = Normalized(); return c.On ? c : null; }

    /// <summary>What differs from a normal game, as short phrases (the menus' summary line, the HUD) - empty when nothing does.</summary>
    public List<string> Summary()
    {
        var o = new List<string>();
        if (!On) return o;
        string X(double v) => "×" + Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
        if (Free) o.Add("free and instant");
        if (Reveal) o.Add("map revealed");
        if (Unlock) o.Add("no mission bans");
        var res = Res.All().Where(r => r.value != null).ToList();
        if (res.Count > 0) o.Add("start " + string.Join(", ", res.Select(r => $"{r.value} {r.name}")));
        if (Storage != null) o.Add($"storage {Storage}");
        if (Pyramid.Any(v => v != null)) o.Add("pyramid " + string.Join("/", Pyramid.Select(v => v == null ? "·" : v.ToString())));
        if (PopMax != null) o.Add($"unit limit {PopMax}");
        if (NoHousing) o.Add("no housing");
        if (Gather != 1) o.Add($"gathering {X(Gather)}");
        if (Speed != 1) o.Add($"build speed {X(Speed)}");
        if (Ai == "passive") o.Add("passive computer");
        if (!FirstShot) o.Add("no first-shot delay");
        else if (FirstShotDelay != null) o.Add($"first-shot delay {FirstShotDelay.Value.ToString(CultureInfo.InvariantCulture)} s");
        foreach (var (s, who) in new[] { (Me, "you"), (Foe, "enemies") })
        {
            if (s.StartEpoch > 1) o.Add($"{who} start in epoch {s.StartEpoch}");
            if (s.MaxEpoch > 0) o.Add($"{who} up to epoch {s.MaxEpoch}");
            if (s.Dmg != 1) o.Add($"{who} damage {X(s.Dmg)}");
            if (s.Def != 1) o.Add($"{who} defence {X(s.Def)}");
        }
        return o;
    }

    public DebugOptions Clone() => System.Text.Json.JsonSerializer.Deserialize<DebugOptions>(
        System.Text.Json.JsonSerializer.Serialize(this))!;
}
