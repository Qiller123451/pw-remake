// A map's day-night cycle in the game (map.lua day_night: World/DayNight.cs, docs/MAP_FORMAT.md; the Outbreak mission):
// the light follows the world's clock smoothly - the sun sinks and turns orange at dusk, the night is dark blue (the
// sequel's night light: SequelFx), dawn the other way round; the buildings' torches and lamps are lit by the simulation
// (Building.Night through World.IsNight); the indicator under the top bar shows the phase and the time to the next one.
// A cutscene's own time of day (its day time items) takes over while it plays (UpdateDayLight).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.World;

namespace PwRemake.App;

public partial class GameSession
{
    PwRemake.UI.Hud.DayNightIndicator? _dayNightUi;
    CanvasLayer? _dayNightLayer;
    double _cycDark = -1, _cycWarm = -1, _cycClock = -1;

    // the colours: the remake's daylight, dusk's, the night's (SequelFx's night: sun 0.22 of the day's, 0x8aa8ff, the sky
    // light 0.36, the sky 0x101828)
    static readonly Color DaySun = Color.Color8(0xff, 0xf2, 0xd8), DuskSun = Color.Color8(0xff, 0x96, 0x58), NightSun = Color.Color8(0x8a, 0xa8, 0xff);
    static readonly Color DaySky = Color.Color8(0x9f, 0xb8, 0xc8), DuskSky = Color.Color8(0x6e, 0x5c, 0x78), NightSky = Color.Color8(0x10, 0x18, 0x28);

    /// <summary>_Ready's part: the indicator, when the map has a cycle</summary>
    void ReadyDayNight()
    {
        if (World.DayNight == null) return;
        _dayNightLayer = new CanvasLayer { Layer = 30, Name = "DayNight" };
        _dayNightUi = new PwRemake.UI.Hud.DayNightIndicator(() => World.DayNight?.At(CycleViewTime ?? World.Time), () => World.DayNight) { Name = "DayNightIndicator" };
        _dayNightLayer.AddChild(_dayNightUi);
        AddChild(_dayNightLayer);
        L.Info($"day-night cycle: {World.DayNight}");
        if (_args.TryGetValue("outbreak-shots", out var dir) && dir.Length > 0) AddChild(new OutbreakShots(this, dir) { Name = "OutbreakShots", Only = _args.GetValueOrDefault("outbreak-shot"), Every = PwRemake.Sim.Core.Json.ParseNum(_args.GetValueOrDefault("outbreak-shot-every", "5.5"), 5.5) });
    }

    /// <summary>the light shows this time of the cycle instead of the world's (the screenshots of a night; null: the world's)</summary>
    public double? CycleViewTime;

    /// <summary>the light of the cycle now (UpdateDayLight, every frame without a cutscene's time of day)</summary>
    void CycleLight()
    {
        if (World.DayNight is not { } spec) return;
        var s = spec.At(CycleViewTime ?? World.Time);
        if (_dayNightUi != null) _dayNightUi.Visible = Hud?.Visible != false;
        double d = s.Darkness;
        double f = s.Into + s.Left > 0 ? s.Into / (s.Into + s.Left) : 0;
        double warm = s.Phase is DayPhase.Dusk or DayPhase.Dawn ? Math.Sin(Math.PI * f) : 0;
        if (Math.Abs(d - _cycDark) < 0.003 && Math.Abs(warm - _cycWarm) < 0.003 && Math.Abs(s.Clock - _cycClock) < 120) return;
        _cycDark = d; _cycWarm = warm; _cycClock = s.Clock;
        float k = (float)d, w = (float)warm;
        var sunCol = DaySun.Lerp(DuskSun, w).Lerp(NightSun, k);
        var sky = DaySky.Lerp(DuskSky, w).Lerp(NightSky, k);
        float sunE = Mathf.Lerp(1f, 0.22f, k) * Mathf.Lerp(1f, 0.8f, w), hemi = Mathf.Lerp(1f, 0.36f, k) * Mathf.Lerp(1f, 0.85f, w);
        Renderer.SetNight(k < 0.002f && w < 0.002f ? null : (sunE, sunCol, hemi, sky));
        // the sun's way through the day (its direction: the original's sun path, DayLight; the colours are the cycle's)
        Renderer.SetDayLight(PwRemake.View.World.DayLight.At(s.Clock, null));
        var lit = Colors.White.Lerp(new Color(0.42f, 0.48f, 0.66f), k).Lerp(new Color(1f, 0.8f, 0.65f), w * (1 - k));
        View.SetSpriteLight(lit);
    }
}

/// <summary>--outbreak-shots &lt;dir&gt; (docs/OUTBREAK.md, docs/DEBUG.md): the mission's screenshots - the plateau's base
/// from above by day, the valley in the south-west with the rockfall on its ramp, a wave of the dead coming up ramp A
/// into camp A at night (put down for the picture: a test run, not a game to save), a plague lord out of its town at
/// night - then the game quits.</summary>
public partial class OutbreakShots : Node
{
    readonly GameSession G;
    readonly string _dir;
    double _t;
    int _step;
    public OutbreakShots(GameSession g, string dir) { G = g; _dir = dir; System.IO.Directory.CreateDirectory(dir); }

    (double X, double Z) At(double mx, double my) => G.Mission!.Campaign.ToGame(mx, my);
    void Look(double mx, double my, double dist, double yaw)
    {
        var (x, z) = At(mx, my);
        G.Camera.X = x; G.Camera.Z = z; G.Camera.Dist = G.Camera.TDist = dist; G.Camera.Yaw = yaw;
    }
    void Shot(string name)
    {
        var f = System.IO.Path.Combine(_dir, name + ".png");
        GetViewport().GetTexture().GetImage().SavePng(f);
        PwRemake.Sim.Core.Log.For("Outbreak").Info("SHOT " + f);
    }

    /// <summary>--outbreak-shot day|valley|night|boss: one picture per run (a test machine's memory)</summary>
    public string? Only;

    public override void _Process(double delta)
    {
        if (G.Mission == null) return;
        _t += delta;
        var W = G.World;
        if (_step == 0)
        {
            G.CycleViewTime = 60;
            // (only the places of the pictures revealed: the whole map visible needs too much memory on a test machine)
            foreach (var (mx, my, r) in Only switch
            {
                "valley" => new[] { (600.0, 620.0, 140.0) },
                "day" => new[] { (384.0, 384.0, 170.0) },
                "boss" => new[] { (611.0, 632.0, 75.0) },
                "night" => new[] { (384.0, 302.0, 80.0) },
                _ => new[] { (384.0, 384.0, 170.0), (611.0, 632.0, 75.0), (600.0, 620.0, 120.0) },
            })
            {
                var (x, z) = At(mx, my);
                W.RevealArea(G.Me, x, z, r);
            }
            _step = 1;
        }
        var shots = Only != null ? new[] { Only } : new[] { "day", "valley", "night", "boss" };
        if (_step - 1 >= shots.Length) return;
        var name = shots[_step - 1];
        if (_set != _step)
        {
            _set = _step;
            var spec = W.DayNight!;
            switch (name)
            {
                case "day": Look(377, 362, 290, 0.785); break;                            // the plateau from above: the base
                case "valley": Look(600, 620, 175, 2.6); break;                           // the valley: a town of the infected in the south-east, its woods
                case "night":
                {
                    // a wave at the base: the light of the middle of a night, the dead up ramp A into camp A
                    G.CycleViewTime = spec.FirstDay + spec.Dusk + spec.Night * 0.5;
                    var inf = W.Players.FirstOrDefault(p => p.Id == 1);
                    var (cx, cz) = At(384, 324);
                    int n = 0;
                    foreach (var cls in new[] { "hu_undead_warrior", "hu_warrior", "hu_spearman", "hu_undead_warrior", "Smilodon", "Wild_Boar", "hu_warrior", "hu_archer", "hu_undead_killer", "hu_undead_warrior", "hu_warrior", "hu_undead_warrior" })
                    {
                        var (x, z) = At(376 + (n % 4) * 5, 282 + (n / 4) * 6);
                        if (W.Spawn(cls, inf, x, z) is Unit u) { W.SetInfection(u, 3); W.Order(u, new Order("attackmove") { X = cx, Z = cz }); }
                        n++;
                    }
                    // (the camp's soldiers go out to meet them in front of the headquarters)
                    var (fx, fz) = At(384, 300);
                    foreach (var u in W.Units.Where(u => u.Alive && u.Owner == G.Me && !u.Name.EndsWith("_worker")).ToList()) W.Order(u, new Order("attackmove") { X = fx, Z = fz });
                    Look(384, 290, 58, 0.3);
                    break;
                }
                case "boss":
                {
                    // a plague lord out of its town at night - the beast, the infected titan (by day it is hidden: custom.lua)
                    G.CycleViewTime = spec.FirstDay + spec.Dusk + spec.Night * 0.5;
                    var lord = W.Units.Where(u => u.Alive && u.Owner?.Id == 1 && u.DrawScale > 1.05).OrderBy(u => u.Name == "hu_triceratops" ? 0 : 1).FirstOrDefault();
                    if (lord != null)
                    {
                        W.SetParked(lord, false); lord.Untargetable = false;
                        var (mx, my) = G.Mission!.Campaign.ToMap(lord.Pos.X, lord.Pos.Z);
                        Look(mx, my, 46, 2.6);
                    }
                    break;
                }
            }
        }
        HideDialogs(GetTree().Root);              // (the start's dialogue box would cover the picture)
        if (_t >= _step * Every - 0.2)            // (a picture every few seconds of real time, taken at its end)
        {
            Shot("outbreak_" + name);
            _step++;
            if (_step - 1 >= shots.Length) GetTree().Quit();
        }
    }
    int _set;
    static void HideDialogs(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            if (c is PwRemake.UI.Mission.DialogBoxView d) d.Visible = false;
            HideDialogs(c);
        }
    }
    /// <summary>seconds of real time per picture (--outbreak-shot-every; short: a test machine's memory fills as the game streams in)</summary>
    public double Every = 5.5;
}
