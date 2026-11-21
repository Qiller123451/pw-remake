// A day-night cycle any map can switch on (map.lua `day_night = { ... }`, docs/MAP_FORMAT.md "day_night"; the map
// editor's map settings): a real cycle of day, dusk, night and dawn instead of the original's slow virtual clock
// (CTimeMgr: a day of 10 minutes that only switches the buildings' lights). The remake's own (Kacper 2026-11-19, the
// "Outbreak" mission: StarCraft II's day and night waves).
//
// The clock is a pure function of the world's time (World.Time) and the spec - nothing of it has to be saved: a saved
// game (a replay, docs/SAVES.md) and every machine of a lockstep game compute the same phase at the same step. The
// world steps it only to notice the phase changes: the event "daynight" (phase, night number) for the view and the
// sound, the units' sight at night (FogOfWar.SightScale), the buildings' lights (IsNight). The trigger engine has the
// condition DayPhase and Lua `api.daynight` (docs/LUA_MAPS.md).
//
// The time of day the rest of the game reads (World.DayTime: the nests' hotspot table, the cutscenes' clock, the HUD)
// follows the cycle: the day runs from 7:00 to 18:00, dusk to 20:00, the night to 5:00, dawn to 7:00.
using System;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public enum DayPhase { Day, Dusk, Night, Dawn }

/// <summary>map.lua's `day_night`: the length of each phase (game seconds), where the cycle starts, the sight at night.</summary>
public sealed class DayNightSpec
{
    /// <summary>seconds of each phase (game time); the defaults are the Outbreak mission's</summary>
    public double Day = 300, Dusk = 30, Night = 180, Dawn = 30;
    /// <summary>where the cycle stands at the start: seconds after the beginning of the first day (0: the day begins)</summary>
    public double Start;
    /// <summary>the units' and buildings' sight at night, a share of their range (1: no change); dusk and dawn go
    /// smoothly between</summary>
    public double NightSight = 0.7;
    /// <summary>the first day's length (s; 0: as every day) - a longer first day to build up before the first night</summary>
    public double FirstDay;

    public double Cycle => Day + Dusk + Night + Dawn;

    /// <summary>map.lua's table (null: no cycle - the map keeps the original's clock). Lengths below 1 s become 1 s
    /// (dusk and dawn may be 0: a sudden change).</summary>
    public static DayNightSpec? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        if (o["enabled"] is JsonValue ev && !ev.Bool(true)) return null;
        var s = new DayNightSpec
        {
            Day = Math.Max(1, o["day"].Num(300)), Dusk = Math.Max(0, o["dusk"].Num(30)), Night = Math.Max(1, o["night"].Num(180)),
            Dawn = Math.Max(0, o["dawn"].Num(30)), Start = Math.Max(0, o["start"].Num(0)), NightSight = Math.Clamp(o["night_sight"].Num(0.7), 0.1, 1),
            FirstDay = Math.Max(0, o["first_day"].Num(0)),
        };
        return s;
    }

    /// <summary>map.lua's table (the writer, the editor)</summary>
    public JsonObject ToJson() => new()
    {
        ["day"] = Day, ["dusk"] = Dusk, ["night"] = Night, ["dawn"] = Dawn, ["start"] = Start, ["night_sight"] = NightSight, ["first_day"] = FirstDay,
    };

    public DayNightSpec Clone() => (DayNightSpec)MemberwiseClone();
    public bool SameAs(DayNightSpec? o) => o != null && o.Day == Day && o.Dusk == Dusk && o.Night == Night && o.Dawn == Dawn && o.Start == Start && o.NightSight == NightSight && o.FirstDay == FirstDay;
    public override string ToString() => $"day {Day} s, dusk {Dusk} s, night {Night} s, dawn {Dawn} s, start {Start} s, night sight {NightSight}{(FirstDay > 0 ? $", first day {FirstDay} s" : "")}";

    // ------------------------------------------------------------------ the clock (pure functions of the time)
    /// <summary>the state at game time t: the phase, the seconds into it, the seconds left of it, the night number (how
    /// many dusks have begun: 0 on the first day, 1 from the first dusk to the next one ...)</summary>
    public DayNightState At(double t)
    {
        double C = Cycle;
        double local = Math.Max(0, t) + Start;
        // a longer first day: its extra seconds come before the cycle
        double extra = FirstDay > Day ? FirstDay - Day : 0;
        if (local < extra) return new DayNightState(DayPhase.Day, local, FirstDay - local, 0, FirstDay - local, 0, 0, Clock(DayPhase.Day, local, FirstDay));
        local -= extra;
        double k = Math.Floor(local / C);
        double w = local - k * C;
        int cycles = (int)k;
        DayPhase ph; double into, len;
        if (w < Day) { ph = DayPhase.Day; into = w; len = Day; }
        else if (w < Day + Dusk) { ph = DayPhase.Dusk; into = w - Day; len = Dusk; }
        else if (w < Day + Dusk + Night) { ph = DayPhase.Night; into = w - Day - Dusk; len = Night; }
        else { ph = DayPhase.Dawn; into = w - Day - Dusk - Night; len = Dawn; }
        int night = cycles + (ph == DayPhase.Day ? 0 : 1);
        // seconds until the next dusk begins (0 while it is not day: the night is here)
        double toNight = ph == DayPhase.Day ? Day - w : ph == DayPhase.Dawn ? (len - into) + Day : 0;
        double toDay = ph == DayPhase.Day ? 0 : ph == DayPhase.Dawn ? len - into : (Day + Dusk + Night + Dawn) - w;
        return new DayNightState(ph, into, len - into, night, toNight, toDay, Darkness(ph, into, len), Clock(ph, into, len));
    }

    /// <summary>0 by day, 1 at night, smooth (smoothstep) through dusk and dawn</summary>
    static double Darkness(DayPhase ph, double into, double len)
    {
        double f = len <= 0 ? 1 : Math.Clamp(into / len, 0, 1);
        double s = f * f * (3 - 2 * f);
        return ph switch { DayPhase.Day => 0, DayPhase.Night => 1, DayPhase.Dusk => s, _ => 1 - s };
    }

    /// <summary>the time of day (seconds of 24 h) the phase stands for: day 7:00-18:00, dusk 18:00-20:00, night 20:00-5:00,
    /// dawn 5:00-7:00</summary>
    static double Clock(DayPhase ph, double into, double len)
    {
        double f = len <= 0 ? 1 : Math.Clamp(into / len, 0, 1);
        double h = ph switch { DayPhase.Day => 7 + 11 * f, DayPhase.Dusk => 18 + 2 * f, DayPhase.Night => 20 + 9 * f, _ => 5 + 2 * f };
        return (h % 24) * 3600;
    }
}

/// <summary>Where the cycle stands (DayNightSpec.At): the phase, seconds into it and left of it, the night number, the
/// seconds to the next dusk / to the next day, the darkness 0..1 and the time of day.</summary>
public readonly record struct DayNightState(DayPhase Phase, double Into, double Left, int NightNo, double ToNight, double ToDay, double Darkness, double Clock)
{
    public bool IsNight => Darkness >= 0.5;
    public string PhaseName => Phase switch { DayPhase.Day => "day", DayPhase.Dusk => "dusk", DayPhase.Night => "night", _ => "dawn" };
    public static DayPhase? Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "day" => DayPhase.Day, "dusk" => DayPhase.Dusk, "night" => DayPhase.Night, "dawn" => DayPhase.Dawn, _ => null,
    };
}

public partial class World
{
    /// <summary>the map's day-night cycle (map.lua day_night), null: the original's clock (docs/MAP_FORMAT.md)</summary>
    public DayNightSpec? DayNight;
    DayPhase _dnPhase;
    int _dnNight = -1;

    /// <summary>where the cycle stands now (null: the map has none)</summary>
    public DayNightState? DayNightNow => DayNight?.At(Time);

    /// <summary>switches the cycle on (or off: null) - the world's constructor from the map; a test</summary>
    public void SetDayNight(DayNightSpec? spec)
    {
        DayNight = spec;
        _dnNight = -1;
        if (spec == null) return;
        var s = spec.At(Time);
        _dnPhase = s.Phase; _dnNight = s.NightNo;
        SightScale = 1 - (1 - spec.NightSight) * s.Darkness;
    }

    /// <summary>every step (World.Update): the phase changes as events, the sight at night</summary>
    void DayNightUpdate()
    {
        var spec = DayNight;
        if (spec == null) return;
        var s = spec.At(Time);
        if (NightFn == null) SightScale = 1 - (1 - spec.NightSight) * s.Darkness;
        if (s.Phase == _dnPhase && s.NightNo == _dnNight) return;
        _dnPhase = s.Phase; _dnNight = s.NightNo;
        Emit("daynight", new Args { ["phase"] = s.PhaseName, ["night"] = s.NightNo });
    }
}
