// The map editor's map settings (docs/MAP_EDITOR.md "Players and map settings", plan phase 4): the level's name,
// author, description (per language), game types, time of day, weather, atmosphere ("sky": the ambience and music of
// a setting - Root/Base/material_atmos), default camera and the preview picture. The original keeps them in the level
// info (LInf: LevelName, the description tree Root/Base/..., Root/WeatherData, the 200 x 200 preview); a phase 1
// document (MapDataDoc) reads and writes them there, so the game (MapSource: StartTime, DefaultCamera, the weather
// program, the music's atmosphere, the game types) plays them. The texts per language are the remake's
// (Root/Remake/Texts/<lang>/...: the new format's texts/<lang>.lua). No Godot types.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

public sealed class MapSettings
{
    /// <summary>the game types the original's maps offer (Root/Base/GameType "Deathmatch|Domination|")</summary>
    public static readonly string[] GAME_TYPES = { "Deathmatch", "Domination", "Defender" };
    /// <summary>the atmospheres of the original's levels (ambience and music: material_atmos)</summary>
    public static readonly string[] ATMOS = { "jungle.txt", "savanna.txt", "northland.txt", "icewaste.txt", "ashvalley.txt", "arena.txt", "No Atmos" };
    /// <summary>the weather kinds (WeatherData track Type: 0 none, 1 snow, 2 rain - WeatherFx)</summary>
    public static readonly string[] WEATHER = { "None", "Snow", "Rain" };

    public string Name = "", Author = "";
    /// <summary>the description per language ("uk", "de" ...); the original's maps give a text key (_MN_MP_..._DESC)</summary>
    public Dictionary<string, string> Descriptions = new(StringComparer.Ordinal);
    public List<string> GameTypes = new() { "Deathmatch" };
    public bool LastManStanding;
    /// <summary>Defender: minutes (Root/Base/DefenderTimer)</summary>
    public double DefenderTimer = 10;
    /// <summary>the time of day the level starts at (seconds of the day)</summary>
    public double StartTime = 12 * 3600;
    /// <summary>the weather: kind (0 none, 1 snow, 2 rain), intensity (the original's levels: 2 - 16), wind direction
    /// (0 - 7: north, north-west ... as WindDir) and strength</summary>
    public int Weather;
    public double WeatherIntensity = 6, WindStrength;
    public int WindDir;
    /// <summary>the ambience and music (material_atmos)</summary>
    public string Atmos = "";
    /// <summary>the camera's angle at the start (radians; the original's 0.785)</summary>
    public double DefaultCamera = 0.785;
    /// <summary>the preview picture (200 x 200; null: none yet)</summary>
    public MapPreview? Preview;
    /// <summary>the remake's day-night cycle (map.lua day_night: World/DayNight.cs); null: the original's clock</summary>
    public World.DayNightSpec? DayNight;

    public MapSettings Clone()
    {
        var c = (MapSettings)MemberwiseClone();
        c.Descriptions = new Dictionary<string, string>(Descriptions, StringComparer.Ordinal);
        c.GameTypes = new List<string>(GameTypes);
        c.DayNight = DayNight?.Clone();
        return c;
    }

    /// <summary>"12:00"</summary>
    public static string TimeText(double s)
    {
        int m = (int)Math.Round(s / 60) % (24 * 60);
        return $"{m / 60}:{m % 60:00}";
    }

    // ---------------------------------------------------------------- the level info

    static string D(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";
    static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>the settings of a map read by Ula (or made by MapDataDoc.CreateFlat)</summary>
    public static MapSettings Read(MapData md, string language = "uk")
    {
        var d = md.Description;
        var s = new MapSettings
        {
            Name = md.Name, Author = D(d, "Root/Base/Author"), StartTime = md.StartTime > 0 ? md.StartTime : 12 * 3600, DefaultCamera = md.DefaultCamera,
            Atmos = D(d, "Root/Base/material_atmos"), Preview = md.Preview,
            LastManStanding = D(d, "Root/Base/LastManStanding") == "1", DayNight = md.DayNight?.Clone(),
        };
        if (s.Author.Length == 0 && md.Info.TryGetValue("Author", out var au)) s.Author = au;
        if (double.TryParse(D(d, "Root/Base/DefenderTimer"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dt)) s.DefenderTimer = dt;
        var gt = D(d, "Root/Base/GameType");
        if (gt.Length > 0) s.GameTypes = gt.Split('|').Where(x => x.Length > 0).Select(x => x == "KingOfTheHill" ? "Domination" : x).Distinct().ToList();
        // the descriptions: the remake's per language, else the level's own (a text key or a text)
        foreach (var (k, v) in d)
            if (k.StartsWith("Root/Remake/Texts/", StringComparison.Ordinal) && k.EndsWith("/Description", StringComparison.Ordinal))
                s.Descriptions[k["Root/Remake/Texts/".Length..^"/Description".Length]] = v;
        if (s.Descriptions.Count == 0 && D(d, "Root/Base/Description") is { Length: > 0 } desc) s.Descriptions[language] = desc;
        var w = MapSource.WeatherProgramOf(md.InfoTree);
        if (w != null && w.Tracks.FirstOrDefault(t => t.Type != 0) is { } tr)
        {
            s.Weather = (int)tr.Type; s.WeatherIntensity = tr.Intensity; s.WindStrength = tr.Strength;
            double[] DIRS = { 0, 315, 270, 225, 180, 135, 90, 45 };
            s.WindDir = Math.Max(0, Array.IndexOf(DIRS, tr.Wind));
        }
        return s;
    }

    /// <summary>writes the settings into the map: its name, the description tree (and its short keys the map source
    /// reads), the start time, the camera, the weather program (Root/WeatherData), the preview</summary>
    public void Write(MapData md)
    {
        var d = md.Description;
        var root = LevelSettings.Detach(md);
        md.Name = Name; md.Info["LevelName"] = Name;
        void Set(string key, string v) { LevelSettings.Set(md, "Root/Base/" + key, v); d[key] = v; }
        Set("LevelName", Name);
        Set("Author", Author);
        md.Info["Author"] = Author;
        Set("GameType", string.Join("", GameTypes.Select(g => g + "|")));
        Set("LastManStanding", LastManStanding ? "1" : "0");
        Set("DefenderTimer", Num(DefenderTimer));
        md.StartTime = StartTime;
        md.DayNight = DayNight?.Clone();
        int m = (int)Math.Round(StartTime / 60);
        Set("StartTime", $"{m / 60}:{m % 60}");
        md.DefaultCamera = DefaultCamera;
        Set("DefaultCamera", DefaultCamera.ToString("0.000000", CultureInfo.InvariantCulture));
        if (Atmos.Length > 0) Set("material_atmos", Atmos);
        LevelSettings.RemoveBelow(md, "Root/Remake/Texts");
        foreach (var (lang, text) in Descriptions) LevelSettings.Set(md, $"Root/Remake/Texts/{lang}/Description", text);
        if (Descriptions.Count > 0) Set("Description", Descriptions.TryGetValue("uk", out var uk) ? uk : Descriptions.Values.First());
        md.Preview = Preview;
        // the weather program: one track of the chosen weather, looping (Root/WeatherData, read by MapSource.WeatherProgramOf)
        root.Subs.RemoveAll(n => n.Name == "WeatherData");
        var wd = new InfoNode { Name = "WeatherData" };
        InfoNode V(string n, string v) => new() { Name = n, Value = v };
        wd.Subs.Add(V("Name", Weather == 0 ? "None" : WEATHER[Math.Clamp(Weather, 0, 2)]));
        wd.Subs.Add(V("Loop", "1"));
        var entry = new InfoNode { Name = "Entry" };
        entry.Subs.Add(V("Name", WEATHER[Math.Clamp(Weather, 0, 2)]));
        entry.Subs.Add(V("Duration", "60"));
        entry.Subs.Add(V("WindDir", WindDir.ToString(CultureInfo.InvariantCulture)));
        entry.Subs.Add(V("WindStrength", Num(WindStrength)));
        entry.Subs.Add(V("Type", Weather.ToString(CultureInfo.InvariantCulture)));
        entry.Subs.Add(V("Intensity", Num(Weather == 0 ? 0 : WeatherIntensity)));
        var tracks = new InfoNode { Name = "Tracks" };
        tracks.Subs.Add(entry);
        wd.Subs.Add(tracks);
        root.Subs.Add(wd);
    }
}
