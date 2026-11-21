// The look of the world around the map: background and scene fog, the sky light (a hemisphere light: sky colour from
// above, ground colour from below), the sun with shadows fitted to the ground the camera sees, the camera's lens and
// the performance settings (shadow quality, resolution scale, frame cap, antialiasing).
//
// three.js -> Godot:
//   * HemisphereLight(0xe6eef2, 0x5a5236, 1.25): the environment's ambient light from a sky that is the sky colour
//     above the horizon and the ground colour below - Godot's cosine-weighted ambient of such a sky is exactly the
//     hemisphere light's mix(ground, sky, 0.5 + 0.5 n.y); the sky's colours x 1.25 / pi (three.js r155+ lights:
//     diffuse = albedo / pi x irradiance; Godot: albedo x ambient; Godot ignores ambient_light_energy for a sky's light,
//     so the factor is in the sky shader - measured: a white sky of 1.0 lights an albedo a to exactly a).
//   * DirectionalLight(0xfff2d8, 2.3) from (-90, 150, 60) above the camera target: a DirectionalLight3D in that
//     direction, energy 2.3 / pi (Godot scales a directional light's energy by pi - measured: energy e at normal incidence lights
//     an albedo a to e x a).
//   * Fog(0x9fb8c8, 260, 620): the environment's depth fog, linear from 260 to 620 m.
//   * the shadow box (fitShadow): the ground the camera sees plus 14 m, in steps of 8 m, at most 105 m (130 m on
//     "high") -> the directional shadow's max distance (Godot fits its splits to the view itself).
//   * shadows every 2nd frame on medium: Godot draws them every frame (no partial update of the shadow atlas).
//
// Ported from remake/src/engine/renderer.js Renderer (pw-exporter 98c7703).
using System;
using Godot;

namespace PwRemake.View.World;

public sealed class RenderSettings
{
    /// <summary>"off" | "medium" | "high"</summary>
    public string Shadows = "medium";
    /// <summary>render resolution x (the JS: pixel ratio x resScale)</summary>
    public double ResScale = 1;
    /// <summary>frames per second at most (0 = no cap)</summary>
    public int FpsCap = 0;
    public bool Antialias = true;
    /// <summary>"off" | "on" | "dense"</summary>
    public string Grass = "on";
    public bool Weather = true;
    /// <summary>the models' particle effects: "off" | "low" | "on" | "high" (View/Fx/Particles)</summary>
    public string Particles = "on";
}

public partial class WorldRenderer : Node3D
{
    public static readonly Color Background = Hex(0x9fb8c8);
    public const double FogStart = 260, FogEnd = 620;
    /// <summary>the sun's offset from the camera target (renderer.js sunOffset)</summary>
    public static readonly Vector3 SunOffset = new(-90, 150, 60);

    public Camera3D Camera { get; private set; } = null!;
    public DirectionalLight3D Sun { get; private set; } = null!;
    public WorldEnvironment Env { get; private set; } = null!;
    public RenderSettings Settings { get; private set; } = new();
    ShaderMaterial _sky = null!;
    double _shadowR;

    static Color Hex(int rgb) => Color.Color8((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static WorldRenderer Create(RenderSettings? settings = null)
    {
        var r = new WorldRenderer { Name = "Renderer", Settings = settings ?? new RenderSettings() };
        r._sky = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = @"shader_type sky;
uniform vec3 sky_color : source_color;
uniform vec3 ground_color : source_color;
uniform float energy = 1.0;
// the hemisphere light's two colours: above the horizon the sky, below it the ground (x the light's intensity: the
// environment's ambient energy does not scale a sky's light)
void sky() { COLOR = (EYEDIR.y >= 0.0 ? sky_color : ground_color) * energy; }
",
            },
        };
        r._sky.SetShaderParameter("sky_color", Hex(0xe6eef2));
        r._sky.SetShaderParameter("ground_color", Hex(0x5a5236));
        r._sky.SetShaderParameter("energy", (float)(1.25 / Math.PI));
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Background,
            Sky = new Sky { SkyMaterial = r._sky, RadianceSize = Sky.RadianceSizeEnum.Size32 },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 1,
            AmbientLightSkyContribution = 1,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Depth,
            FogLightColor = Background,
            FogLightEnergy = 1,
            FogDensity = 1,
            FogDepthBegin = (float)FogStart,
            FogDepthEnd = (float)FogEnd,
            FogDepthCurve = 1,
            FogSkyAffect = 0,
            FogSunScatter = 0,
            FogAerialPerspective = 0,
        };
        r.Env = new WorldEnvironment { Environment = env, Name = "Environment" };
        r.AddChild(r.Env);
        r.Sun = new DirectionalLight3D
        {
            Name = "Sun", LightColor = Hex(0xfff2d8), LightEnergy = (float)(2.3 / Math.PI),
            ShadowEnabled = true, ShadowBias = 0.05f, ShadowNormalBias = 1.0f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 105, LightAngularDistance = 0,
        };
        r.AddChild(r.Sun);
        // (a directional light shines along its -z: from the sun's position towards the target)
        r.Sun.Basis = Basis.LookingAt(-SunOffset.Normalized(), Vector3.Up);
        r.Camera = new Camera3D { Name = "Camera", Fov = 40, Near = 2, Far = 1400, Current = true };
        r.AddChild(r.Camera);
        r.ApplySettings();
        return r;
    }

    /// <summary>shadows, resolution scale, frame cap and antialiasing from Settings (setShadows, resize, applyShadowQuality)</summary>
    public void ApplySettings()
    {
        var s = Settings;
        bool on = s.Shadows != "off";
        Sun.ShadowEnabled = on;
        RenderingServer.DirectionalShadowAtlasSetSize(s.Shadows == "high" ? 4096 : 2048, true);
        _shadowR = 0;
        var vp = GetViewportOrNull();
        if (vp != null)
        {
            vp.Scaling3DScale = (float)Math.Clamp(s.ResScale, 0.25, 2);
            // (FXAA, not MSAA: with MSAA the depth texture the fog of war pass reads stays empty - Godot 4.7, seen on
            // Vulkan / llvmpipe)
            vp.Msaa3D = Viewport.Msaa.Disabled;
            vp.ScreenSpaceAA = s.Antialias ? Viewport.ScreenSpaceAAEnum.Fxaa : Viewport.ScreenSpaceAAEnum.Disabled;
        }
        Engine.MaxFps = Math.Max(0, s.FpsCap);
    }

    Viewport? GetViewportOrNull() => IsInsideTree() ? GetViewport() : null;

    (float Sun, Color SunColor, float Sky, Color Bg, Color SkyColor, Color GroundColor)? _base;
    (float Sun, Color SunColor, float Hemi, Color Sky)? _night;
    DayLight? _dayLight;

    /// <summary>a mode's own night (the sequel's: SequelFx): the sun's and the sky light's share of the day's, the sun's
    /// colour, the sky's (background and fog) colour; null = the day again</summary>
    public void SetNight((float Sun, Color SunColor, float Hemi, Color Sky)? n) { _night = n; ApplyLight(); }

    /// <summary>
    /// A time of day other than the game's (a cutscene's day time item, GameSession): the sun's direction and the
    /// colours of the sun, the sky light and the sky as factors of the game's own (DayLight: the original's sky table
    /// and sun path relative to noon); null = the game's light again.
    /// </summary>
    public void SetDayLight(DayLight? d) { _dayLight = d; ApplyLight(); }
    public DayLight? DayLightNow => _dayLight;

    /// <summary>the darkest light of the mixed particles: the light renderer's 0xff0f0f0f (PWClient 0x7245c1, its
    /// constructor's colour at +0x34, which 0x723520 takes channel by channel with the sun's)</summary>
    public const float PARTICLE_LIGHT_MIN = 15 / 255f;
    Color _sunTint = Colors.White;
    /// <summary>
    /// The light of the mixed (alpha-blended) particles - smoke, steam, dust (Particles.MixLight; the additive ones are
    /// not lit). The original (PWClient 0x704f31 - 0x7052d7, the particle renderer's lit path for the "_pm" sheets)
    /// multiplies a mixed sprite's colour per channel by (L + 1) / 2 x light / 255, L the model's light value (1.0
    /// from every caller of the model drawing 0x6eb4c0: the factor 1) and light the light renderer's colour of
    /// 0x723520: per channel the larger of the sun's colour (the sky table's row 2 at the time of day: 0x727cc0 ->
    /// +0x55dc, 0x72dacf) and 0x0f0f0f (+0x34) - clamped to 255. Here: the setting's sun colour at noon (SetSunTint)
    /// times the scene's sun now against the remake's daylight (energy x colour, per channel: a cutscene's time of day
    /// gives the table's row 2 of that time, the cycle's night and dusk and the biomes darken or tint it), at least
    /// PARTICLE_LIGHT_MIN, at most 1.
    /// </summary>
    public Color ParticleLight { get; private set; } = Colors.White;
    /// <summary>the sun colour of the map's setting at noon (the sky table's row 2; white without a table)</summary>
    public void SetSunTint(Color c) { _sunTint = c; ApplyLight(); }

    PwRemake.Sim.Maps.BiomeLook? _biome;
    /// <summary>
    /// The biomes around the camera (docs/BIOMES.md): their light, fog and sky as factors of the colours above (the
    /// sun's, the sky light's, the sky's and fog's colour) and of the fog's distances; null or neutral = none (a map
    /// without biomes never sets it). Returns whether anything changed.
    /// </summary>
    public bool SetBiomeLook(PwRemake.Sim.Maps.BiomeLook? look)
    {
        if (look != null && look.IsNeutral) look = null;
        if (look == null && _biome == null) return false;
        if (look != null && _biome != null && Same(look, _biome)) return false;
        _biome = look;
        ApplyLight();
        return true;
    }
    static bool Same(PwRemake.Sim.Maps.BiomeLook a, PwRemake.Sim.Maps.BiomeLook b)
    {
        static bool N(double x, double y) => Math.Abs(x - y) < 0.002;
        for (int c = 0; c < 3; c++) if (!N(a.Sun[c], b.Sun[c]) || !N(a.Ambient[c], b.Ambient[c]) || !N(a.Sky[c], b.Sky[c])) return false;
        return N(a.Fog, b.Fog);
    }
    /// <summary>the sky's / fog's colour and the fog's distances now (the fog of war pass and the particles undo / follow them)</summary>
    public Color FogColor => Env.Environment.FogLightColor;
    public double FogBegin => Env.Environment.FogDepthBegin;
    public double FogFinish => Env.Environment.FogDepthEnd;

    void ApplyLight()
    {
        var env = Env.Environment;
        _base ??= (Sun.LightEnergy, Sun.LightColor, (float)_sky.GetShaderParameter("energy"), env.BackgroundColor,
                   (Color)_sky.GetShaderParameter("sky_color"), (Color)_sky.GetShaderParameter("ground_color"));
        var b = _base.Value;
        var n = _night;
        var d = _dayLight;
        Color sunCol = n == null ? b.SunColor : n.Value.SunColor, bg = n == null ? b.Bg : n.Value.Sky;
        Sun.LightEnergy = n == null ? b.Sun : b.Sun * n.Value.Sun;
        _sky.SetShaderParameter("energy", n == null ? b.Sky : b.Sky * n.Value.Hemi);
        Color skyC = b.SkyColor, groundC = b.GroundColor;
        Vector3 sunFrom = SunOffset;
        if (d != null)
        {
            sunCol *= d.SunMul;
            // the sky (background and fog): the original's own fog colour of that time when there is a table (its row
            // 0, DayLight.Sky); a factor on the remake's blue-grey would push a channel up to 3x (a dusk row turned the
            // whole sky flat bright yellow in mission 1's sc_1050 at 19:00); the sequel's night keeps its own sky
            if (d.Sky is { } sc && n == null) bg = sc; else bg *= d.SkyMul;
            skyC *= d.AmbientMul; groundC *= d.AmbientMul;
            sunFrom = d.SunOffset;
        }
        if (_biome is { } bl)
        {
            static Color M(double[] f) => new((float)f[0], (float)f[1], (float)f[2]);
            sunCol *= M(bl.Sun); skyC *= M(bl.Ambient); groundC *= M(bl.Ambient); bg *= M(bl.Sky);
        }
        double fogK = _biome?.Fog ?? 1;
        env.FogDepthBegin = (float)(FogStart * fogK); env.FogDepthEnd = (float)(FogEnd * fogK);
        Sun.LightColor = new Color(sunCol.R, sunCol.G, sunCol.B);
        _sky.SetShaderParameter("sky_color", new Color(skyC.R, skyC.G, skyC.B));
        _sky.SetShaderParameter("ground_color", new Color(groundC.R, groundC.G, groundC.B));
        env.BackgroundColor = env.FogLightColor = new Color(bg.R, bg.G, bg.B);
        Sun.Basis = Basis.LookingAt(-sunFrom.Normalized(), Vector3.Up);
        // the light now against the day's (sun + sky light, by brightness): the flat silhouettes of the units behind
        // buildings follow it (UnitsBehind.SetSceneLight) - at night they glowed in full colour over the dark buildings
        static float Lum(Color c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
        float skyNow = n == null ? b.Sky : b.Sky * n.Value.Hemi;
        float day = b.Sun * Lum(b.SunColor) + b.Sky * Lum(b.SkyColor), now = Sun.LightEnergy * Lum(sunCol) + skyNow * Lum(skyC);
        PwRemake.View.Materials.UnitsBehind.SetSceneLight(day > 1e-4f ? now / day : 1);
        // the mixed particles' light (ParticleLight): the setting's sun colour times the sun now against the day's
        static float Ch(float tint, float now, float day) =>
            Math.Clamp(tint * (day > 1e-4f ? now / day : 1), PARTICLE_LIGHT_MIN, 1f);
        ParticleLight = new Color(Ch(_sunTint.R, Sun.LightEnergy * sunCol.R, b.Sun * b.SunColor.R),
            Ch(_sunTint.G, Sun.LightEnergy * sunCol.G, b.Sun * b.SunColor.G), Ch(_sunTint.B, Sun.LightEnergy * sunCol.B, b.Sun * b.SunColor.B));
    }

    public override void _Ready() => ApplySettings();

    /// <summary>
    /// followSun + fitShadow: the shadow box only as big as the ground the camera sees, plus the reach of the shadows
    /// falling into it (the shadow pass then draws only what can matter, and the shadows are sharper close up). In
    /// steps of 8 m: a box that changes size makes the shadows crawl, so it changes only when the zoom does.
    /// </summary>
    public void FollowSun(Vector3 target)
    {
        if (!Sun.ShadowEnabled) return;
        double rMax = Settings.Shadows == "high" ? 130 : 105;
        var cam = Camera;
        double ext = 0;
        var camPos = cam.GlobalPosition;
        foreach (var (x, y) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) })
        {
            var size = cam.GetViewport().GetVisibleRect().Size;
            var dir = cam.ProjectRayNormal(new Vector2(x * size.X, y * size.Y));    // the ray through a corner of the view
            // (onto ground 30 m below the target: looking down a slope, the far edge of the view lies lower and further out)
            double t = dir.Y < -1e-3 ? (target.Y - 30 - camPos.Y) / dir.Y : double.PositiveInfinity;
            if (!(t > 0 && t < 2000)) { ext = rMax; break; }
            var p = camPos + dir * (float)t;
            // the distance from the camera along the ground (Godot measures the shadow range from the camera)
            ext = Math.Max(ext, (p - camPos).Length() - (target - camPos).Length());
        }
        double dist = (target - camPos).Length();
        double r = Math.Min(rMax, Math.Max(40, Math.Ceiling((ext + 14) / 8) * 8));
        if (r != _shadowR)
        {
            _shadowR = r;
            Sun.DirectionalShadowMaxDistance = (float)(dist + r);
        }
    }
}

/// <summary>
/// The light of a time of day as factors of the game's own (WorldRenderer.SetDayLight): the original's sky table of
/// the map's setting (SkyGradient: the sun's, the ambient light's and the sky's colour) at that time divided by its
/// colour at noon - the game's light is the remake's daylight, so noon stays as it is -, and the sun's direction: the
/// remake's sun turned about the vertical by the original sun's turn since noon and lowered as the original's
/// elevation drops (0.6 rad at noon, 0.2 at midnight).
/// </summary>
public sealed class DayLight
{
    public double Tod;
    public Color SunMul = Colors.White, AmbientMul = Colors.White, SkyMul = Colors.White;
    /// <summary>the table's sky colour at that time (row 0, opaque): the original's fog colour - the sky renderer sets it
    /// as the fog's colour with the alpha forced to 255 (PWClient 0x7308f3-0x730a0c: CSkyMgr 0x727d80, or edi,0xff000000,
    /// stored next to the fog's start and end), so what the original shows towards the horizon; null without a table</summary>
    public Color? Sky;
    public Vector3 SunOffset = WorldRenderer.SunOffset;

    public const double Noon = 43200;

    public static DayLight At(double tod, PwRemake.Sim.Content.SkyGradient? sky)
    {
        var d = new DayLight { Tod = tod };
        var (az, el) = PwRemake.Sim.Content.SkyGradient.SunAngles(tod);
        var (az0, el0) = PwRemake.Sim.Content.SkyGradient.SunAngles(Noon);
        // (the map's frame turns from x towards y; the game's x is the map's x, its z the map's y)
        double turn = az - az0;
        var o = WorldRenderer.SunOffset;
        double h = Math.Sqrt(o.X * o.X + o.Z * o.Z), r = o.Length();
        double elR = Math.Atan2(o.Y, h) * el / el0;
        double hx = o.X / h, hz = o.Z / h;
        double rx = hx * Math.Cos(turn) - hz * Math.Sin(turn), rz = hx * Math.Sin(turn) + hz * Math.Cos(turn);
        d.SunOffset = new Vector3((float)(rx * Math.Cos(elR) * r), (float)(Math.Sin(elR) * r), (float)(rz * Math.Cos(elR) * r));
        if (sky != null)
        {
            d.SunMul = Mul(sky, PwRemake.Sim.Content.SkyGradient.RowSun, tod);
            d.AmbientMul = Mul(sky, PwRemake.Sim.Content.SkyGradient.RowAmbient, tod);
            d.SkyMul = Mul(sky, PwRemake.Sim.Content.SkyGradient.RowSky, tod);
            var c = sky.At(PwRemake.Sim.Content.SkyGradient.RowSky, tod);
            d.Sky = new Color((float)c.R, (float)c.G, (float)c.B);
        }
        return d;
    }

    /// <summary>a row's colour at tod / at noon, per channel (a channel black at noon: its value itself), at most 3</summary>
    static Color Mul(PwRemake.Sim.Content.SkyGradient g, int row, double tod)
    {
        var c = g.At(row, tod);
        var n = g.At(row, Noon);
        static float F(double v, double at) => (float)Math.Clamp(at < 0.02 ? v : v / at, 0, 3);
        return new Color(F(c.R, n.R), F(c.G, n.G), F(c.B, n.B));
    }
}

