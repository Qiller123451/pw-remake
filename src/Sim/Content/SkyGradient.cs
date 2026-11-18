// The day-time light of the original (PWClient.exe CSkyMgr): a landscape setting's colour table
// Texture/Scape/<Setting>/skygradient_day.tga (else skygradient.tga) - converted by the prepare tool
// (tools/prepare/sky.py) into assets/terrain/<Setting>/sky.json - and the sun's way across the sky.
//
//   * the table: one row per colour, the columns the day from midnight to midnight; a colour is read at
//     x = (time of day / 86400) (width - 1), linearly between two columns (0x821c60), t clamped to 0..1; row 0 is the
//     sky's colour (its alpha the haze), row 1 the ambient light, row 2 the sun's light (0x727c90 / 0x727cc0 -> the
//     light renderer's ambient / diffuse colour, 0x72dab9-0x72dad6; a table with fewer rows gives row 0);
//   * the sun (CSkyMgr.SetTime 0x727710): f = the day's fraction (time of day / 86400, 0 = midnight); azimuth
//     2 pi f + 3 pi / 2, elevation 0.4 - 0.2 cos(2 pi f) radians (0.2 at midnight, 0.6 at noon: the light never goes
//     below the horizon); the light shines from (cos el cos az, cos el sin az, sin el) of the map's frame (x, y, z up)
//     towards the ground (0x727540; the light renderer negates it). The sun and moon sprites of the sky dome have their
//     own elevations (+-0.6 cos(2 pi f)), not used here.
// No Godot here: the session turns it into the renderer's light (GameSession: a cutscene's day time item).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Content;

public sealed class SkyGradient
{
    public const int RowSky = 0, RowAmbient = 1, RowSun = 2;
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>the file the client would load (sky.json "source")</summary>
    public string Source { get; private set; } = "";
    /// <summary>rows top-down, colours (r, g, b, a) 0..1</summary>
    readonly List<(double R, double G, double B, double A)[]> _rows = new();

    public static SkyGradient? Load(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try { return FromJson(Json.ParseFile(path)); }
        catch (Exception e) { Log.For("Content.Sky").Warn($"{path}: {e.Message}"); return null; }
    }

    public static SkyGradient? FromJson(JsonNode? n)
    {
        if (n?["rows"] is not JsonArray rows || rows.Count == 0) return null;
        var g = new SkyGradient { Source = n["source"].Str() };
        foreach (var r in rows)
        {
            if (r is not JsonArray cols || cols.Count == 0) return null;
            var row = new (double, double, double, double)[cols.Count];
            for (int i = 0; i < cols.Count; i++)
            {
                var h = cols[i].Str();
                if (h.Length != 8 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
                row[i] = ((v >> 24) / 255.0, ((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
            }
            if (g._rows.Count > 0 && row.Length != g._rows[0].Length) return null;
            g._rows.Add(row);
        }
        g.Width = g._rows[0].Length;
        g.Height = g._rows.Count;
        return g;
    }

    /// <summary>the day's fraction of a time of day in seconds (0 = midnight, 0.5 = noon)</summary>
    public static double DayFraction(double tod) { double f = tod / 86400; return f - Math.Floor(f); }

    /// <summary>a row's colour at a time of day (0x821c60; a missing row: row 0, as the client's getters)</summary>
    public (double R, double G, double B, double A) At(int row, double tod)
    {
        var r = _rows[row < Height ? row : 0];
        double x = Math.Clamp(DayFraction(tod), 0, 1) * (Width - 1);
        int i = (int)Math.Floor(x);
        if (i >= Width - 1) return r[Width - 1];
        double t = x - i;
        var a = r[i]; var b = r[i + 1];
        return (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
    }

    /// <summary>does a row's colour change over the day (a "_day" table may hold the day's colours only)</summary>
    public bool Varies(int row)
    {
        var r = _rows[row < Height ? row : 0];
        foreach (var c in r) if (c != r[0]) return true;
        return false;
    }

    /// <summary>the sun's azimuth and elevation (radians) at a time of day (CSkyMgr.SetTime)</summary>
    public static (double Azimuth, double Elevation) SunAngles(double tod)
    {
        double f = DayFraction(tod);
        return (f * 2 * Math.PI + 3 * Math.PI / 2, 0.4 - 0.2 * Math.Cos(f * 2 * Math.PI));
    }

    /// <summary>the direction towards the sun in the game's frame (x = map x, y up, z = map y) at a time of day</summary>
    public static V3 SunDirection(double tod)
    {
        var (az, el) = SunAngles(tod);
        return new V3(Math.Cos(el) * Math.Cos(az), Math.Sin(el), Math.Cos(el) * Math.Sin(az));
    }
}
