// Shader snow on buildings: the fallback of the original's setting textures (Kacper 2026-11-11: "original game had
// setting material like snow on buildings in a snow biome"; docs/VIEW.md "Setting textures", docs/BIOMES.md).
//
// The original's snow on buildings is painted into its Icewaste setting textures (Texture/Buildings/settings/icewaste:
// the tribes' atlases with snow on the roofs and ledges; SettingMaterials draws them by the biome where a building
// stands). Some buildings have no such texture: the base game's SEAS buildings (only MIRAGE paints seas_building_a /
// seas_tent per setting), the sequel's own houses (hs_*), anything a mod adds, and every building of content prepared
// before the setting textures. In a snowy biome they would stand bare in a white village, so they get this snow: the
// upward faces of buildings, walls and built scenery get the Icewaste's snow ground texture where the map's biome has
// snow (Biome.SnowCover: the Icewaste and the remake's Frost) - following the biomes of a mixed map, none in the
// rainforest half of "Frost and Jungle". A model with an Icewaste setting texture gets none of it on any material
// (ModelLibrary: ToLambert's snow flag) - its own painted snow is the original's.
//
// How: the materials of such building models (fourcc Bldg, Wall, Deko - not the effects fx_*) are the Lambert shader of
// ModelMaterials with the snow code. It reads three global shader parameters, set per map by WorldView (SetMap):
//   pw_snow_map    a texture over the whole map, red = the snow cover there (GroundLayers.SnowCover, a texel per 8 m)
//   pw_snow_rect   (x0, z0, 1 / width, 1 / depth): world x, z -> the map texture's uv
//   pw_snow        the option times "the map has snow" (0: the shader skips it all)
//   pw_snow_tex    the snow texture (the Icewaste's scape_0.jpg: open snow), planar over x, z, a tile per SNOW_TILE m
// A face's cover: the snow cover there x how much it faces up (smoothstep of the world normal's y, its edge broken by the
// snow texture's grain), the texture lifted a little (SNOW_LIFT) so a roof reads white in the bluish light.
// Option "Setting textures on buildings" (Options, Graphics; AppSettings.SettingTextures, default on, applied at once:
// SettingMaterials.SetEnabled switches this too).
using System;
using Godot;

namespace PwRemake.View.Materials;

public static class SnowCover
{
    /// <summary>metres per tile of the snow texture on a building</summary>
    public const float SNOW_TILE = 6f;
    /// <summary>the snow texture's brightness on buildings</summary>
    public const float SNOW_LIFT = 1.12f;

    /// <summary>the option (AppSettings.SettingTextures, through SettingMaterials.SetEnabled)</summary>
    public static bool Enabled { get; private set; } = true;
    static bool s_mapHasSnow;

    static bool s_globals;
    static ImageTexture? s_none;

    /// <summary>the global shader parameters the snow code reads (added once, before any such shader compiles)</summary>
    public static void EnsureGlobals()
    {
        if (s_globals) return;
        s_globals = true;
        var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 1));
        s_none = ImageTexture.CreateFromImage(img);
        // (once per process: the parameters stay for every later game)
        static void Add(string name, RenderingServer.GlobalShaderParameterType type, Variant v) => RenderingServer.GlobalShaderParameterAdd(name, type, v);
        Add("pw_snow", RenderingServer.GlobalShaderParameterType.Float, 0f);
        Add("pw_snow_rect", RenderingServer.GlobalShaderParameterType.Vec4, new Vector4(0, 0, 1, 1));
        Add("pw_snow_map", RenderingServer.GlobalShaderParameterType.Sampler2D, s_none);
        Add("pw_snow_tex", RenderingServer.GlobalShaderParameterType.Sampler2D, s_none);
    }

    public static void SetEnabled(bool on) { Enabled = on; Apply(); }

    static void Apply()
    {
        EnsureGlobals();
        RenderingServer.GlobalShaderParameterSet("pw_snow", Enabled && s_mapHasSnow ? 1f : 0f);
    }

    /// <summary>
    /// A map's snow: cover(x, z) 0..1 in world coordinates over the square world of `size` m centred on (0, 0), sampled
    /// every `cell` m; tex the snow texture. No snow anywhere (cover null): off.
    /// </summary>
    public static void SetMap(double size, double cell, Func<double, double, double>? cover, Image? tex)
    {
        EnsureGlobals();
        s_mapHasSnow = false;
        if (cover != null)
        {
            int n = Math.Clamp((int)Math.Ceiling(size / cell), 1, 1024);
            var img = Image.CreateEmpty(n, n, false, Image.Format.R8);
            double half = size / 2, step = size / n;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    double v = Math.Clamp(cover(-half + (i + 0.5) * step, -half + (j + 0.5) * step), 0, 1);
                    if (v > 0) s_mapHasSnow = true;
                    img.SetPixel(i, j, new Color((float)v, 0, 0));
                }
            RenderingServer.GlobalShaderParameterSet("pw_snow_map", ImageTexture.CreateFromImage(img));
            RenderingServer.GlobalShaderParameterSet("pw_snow_rect", new Vector4((float)-half, (float)-half, (float)(1 / size), (float)(1 / size)));
            if (tex != null)
            {
                var t = (Image)tex.Duplicate();
                if (t.IsCompressed()) t.Decompress();
                if (!t.HasMipmaps()) t.GenerateMipmaps();
                RenderingServer.GlobalShaderParameterSet("pw_snow_tex", ImageTexture.CreateFromImage(t));
            }
        }
        Apply();
    }

    /// <summary>whether a model's materials carry the snow code: buildings, walls and built scenery, not effects</summary>
    public static bool Applies(string fourcc, string? model) =>
        fourcc is "Bldg" or "Wall" or "Deko" && !(model ?? "").StartsWith("fx_", StringComparison.OrdinalIgnoreCase);

    /// <summary>the shader code: the global parameters (before the functions)</summary>
    public const string Uniforms = @"
global uniform float pw_snow;
global uniform vec4 pw_snow_rect;
global uniform sampler2D pw_snow_map;
global uniform sampler2D pw_snow_tex;
";

    /// <summary>the shader code: a.rgb (the surface's albedo) under the snow; NORMAL and VERTEX are the fragment's (view space)</summary>
    public static readonly string Fragment = @"
	if (pw_snow > 0.0) {
		vec3 snow_wp = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		vec2 snow_uv = clamp((snow_wp.xz - pw_snow_rect.xy) * pw_snow_rect.zw, vec2(0.0), vec2(1.0));
		float snow_k = pw_snow * texture(pw_snow_map, snow_uv).r;
		if (snow_k > 0.004) {
			vec3 snow_wn = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
			vec3 snow_c = texture(pw_snow_tex, snow_wp.xz * " + (1 / SNOW_TILE).ToString("0.0####", System.Globalization.CultureInfo.InvariantCulture) + @").rgb;
			float snow_g = dot(snow_c, vec3(0.333));
#if CURRENT_RENDERER != RENDERER_COMPATIBILITY
			snow_c = pow(max(snow_c, vec3(0.0)), vec3(2.2));     // (a global sampler has no source_color: sRGB -> linear as the albedo)
#endif
			// (upward faces; the grain of the snow breaks the edge)
			float snow_up = smoothstep(0.38, 0.72, snow_wn.y + (snow_g - 0.6) * 0.5);
			a.rgb = mix(a.rgb, snow_c * " + SNOW_LIFT.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture) + @", clamp(snow_up * snow_k * 1.15, 0.0, 1.0));
		}
	}
";
}
