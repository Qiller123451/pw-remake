// "Show units behind buildings" (Options, Graphics): a unit hidden behind a building is drawn over it as a flat
// silhouette in its owner's colour (the colour of its ring and minimap dot, GroundRings.RingColor). The original's
// "Show Flatshades" option (OptionsMenu.usl: Game/GraphicOptions/ShowFlatshades -> CClientWrap.EnableFlatshades;
// tooltip "units can be seen through objects or walls in the line of sight"; drawn by the engine's OcclusionRenderer,
// PWClient.exe). The scripts set no owner filter: every unit shown to the player gets one, in its owner's colour.
// (The original's default is off - Game.usl writes "false" on the first start; the remake's is on, Kacper's choice.)
//
// How (the stencil buffer, Godot 4.5+; Forward+, Mobile and Compatibility):
//  - a building's materials write STENCIL_BUILDING where they pass the depth test (they are the nearest surface
//    there, or something nearer is drawn later) - also its alpha-blended ones (the warehouse's cloth roofs, no
//    opaque part under them);
//  - a unit's opaque materials write 0 where they pass it (a unit in front of a building clears the mark, whichever
//    of the two is drawn first; another unit in front clears it too);
//  - a unit's material has a next pass, drawn after every other surface (transparent, render priority 127): unlit, the
//    owner's colour, only where the stencil is STENCIL_BUILDING - the unit's projection where a building is nearer. No
//    depth test: where the unit itself is visible the stencil is 0 (and an inverted depth test would miss the units
//    behind a blended roof, which writes no depth).
// So a unit shows through a building, not through the terrain, trees, props or other units in front of it. (A tree
// or prop drawn after a building, in front of it, keeps the building's mark: a unit behind both shows through the tree
// there - rare, and the original's occlusion pass is no stricter.) Ships are not shown (a harbour would always be
// covered in them). Glows are left as they are, and a unit's alpha-blended materials.
// Costs: no extra draw for buildings or most units. The next pass (one more draw per surface) only on the units that
// may be behind a building on screen (EntityViews.MayBeCovered: the line from the camera to the unit goes through a
// drawn building's cylinder before it), its fragments nearly all rejected by the stencil test.
using System;
using System.Collections.Generic;
using Godot;

namespace PwRemake.View.Materials;

/// <summary>What a model is for UnitsBehind: nothing, a building (covers), a unit (clears the mark), a unit that may
/// stand behind a building on screen (clears the mark and shows through: EntityViews.MayBeCovered).</summary>
public enum BehindRole : byte { None, Building, Unit, UnitCovered }

public static class UnitsBehind
{
    /// <summary>the stencil value of the pixels where a building is the nearest surface</summary>
    public const int STENCIL_BUILDING = 1;

    /// <summary>The option (AppSettings.ShowUnitsBehind); applied by the models when Epoch changed.</summary>
    public static bool Enabled => s_option && !s_suspended;
    static bool s_option = true, s_suspended;
    /// <summary>counts the changes of Enabled (models re-apply their materials when it changed)</summary>
    public static int Epoch { get; private set; }
    public static void SetEnabled(bool on) { bool was = Enabled; s_option = on; if (Enabled != was) Epoch++; }
    /// <summary>off for a while whatever the option says: during cutscenes (Kacper 2026-11-12: "even if the units see-through
    /// setting is enabled it shouldn't work during cutscenes"; GameSession sets it each frame from Cinematic)</summary>
    public static void SetSuspended(bool on) { bool was = Enabled; s_suspended = on; if (Enabled != was) Epoch++; }

    static readonly Dictionary<Material, Material> s_building = new(), s_unit0 = new();
    static readonly Dictionary<(Material, uint), Material> s_unit = new();
    static readonly Dictionary<(Shader, int), Shader> s_shaders = new();
    static readonly Dictionary<(uint, bool), ShaderMaterial> s_flat = new();
    static Shader? s_silhouette, s_silhouetteTwo, s_silhouetteCut;

    /// <summary>
    /// The material a surface of a model draws with: m itself (no role, option off, a glow or blended material), or a
    /// copy that writes the stencil (building) / writes it and draws the silhouette in col (unit). Cached.
    /// </summary>
    public static Material Apply(Material m, BehindRole role, Color col)
    {
        if (role == BehindRole.None || !Enabled) return m;
        if (!Solid(m, role == BehindRole.Building, out bool cut, out bool twoSided)) return m;
        if (role != BehindRole.UnitCovered)
        {
            var cache = role == BehindRole.Building ? s_building : s_unit0;
            if (!cache.TryGetValue(m, out var b)) cache[m] = b = Writing(m, role == BehindRole.Building ? STENCIL_BUILDING : 0);
            return b;
        }
        var key = (m, (uint)col.ToRgba32());
        if (s_unit.TryGetValue(key, out var u)) return u;
        u = Writing(m, 0);
        u.NextPass = Silhouette(m, col, cut, twoSided);
        s_unit[key] = u;
        return u;
    }

    /// <summary>opaque or cut out (alpha scissor), or alpha-blended when blended is true; not a glow (additive)</summary>
    static bool Solid(Material m, bool blended, out bool cut, out bool twoSided)
    {
        cut = twoSided = false;
        switch (m)
        {
            case StandardMaterial3D sm:
                cut = sm.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor;
                twoSided = sm.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
                return sm.NextPass == null && (cut || blended || sm.Transparency == BaseMaterial3D.TransparencyEnum.Disabled)
                    && sm.BlendMode == BaseMaterial3D.BlendModeEnum.Mix;
            case ShaderMaterial sh when sh.Shader != null && sh.NextPass == null:
            {
                // (the Lambert shaders of ModelMaterials: "ALPHA = " only when blended or cut out)
                var code = sh.Shader.Code;
                if (code.Contains("blend_add") || code.Contains("stencil_mode")) return false;
                cut = code.Contains("ALPHA_SCISSOR_THRESHOLD");
                twoSided = code.Contains("cull_disabled");
                return cut || blended || !code.Contains("ALPHA =");
            }
            default: return false;
        }
    }

    /// <summary>a copy of m that writes the stencil value v where it passes the depth test</summary>
    static Material Writing(Material m, int v)
    {
        if (m is StandardMaterial3D sm)
        {
            var d = (StandardMaterial3D)sm.Duplicate();
            d.StencilMode = BaseMaterial3D.StencilModeEnum.Custom;
            d.StencilFlags = (int)BaseMaterial3D.StencilFlagsEnum.Write;
            d.StencilCompare = BaseMaterial3D.StencilCompareEnum.Always;
            d.StencilReference = v;
            return d;
        }
        var sh = (ShaderMaterial)m;
        if (!s_shaders.TryGetValue((sh.Shader, v), out var s))
        {
            const string HEAD = "shader_type spatial;";
            var code = sh.Shader.Code;
            int at = code.IndexOf(HEAD, StringComparison.Ordinal);
            s_shaders[(sh.Shader, v)] = s = new Shader { Code = at < 0 ? code
                : code.Insert(at + HEAD.Length, $"\nstencil_mode write, compare_always, {v}; // UnitsBehind\n") };
        }
        var c = (ShaderMaterial)sh.Duplicate();
        c.Shader = s;
        return c;
    }

    // the silhouettes are drawn after every other transparent surface: over a building's blended parts (the cloth roofs
    // of the warehouse), which mark the stencil in the transparent pass
    const int LAST = (int)Material.RenderPriorityMax;

    /// <summary>the next pass: the owner's colour, unlit, where the surface is hidden behind a building</summary>
    static ShaderMaterial Silhouette(Material m, Color col, bool cut, bool twoSided)
    {
        var c = new Color(col.R, col.G, col.B, 1);
        if (!cut)
        {
            var key = ((uint)c.ToRgba32(), twoSided);
            if (s_flat.TryGetValue(key, out var f)) return f;
            f = new ShaderMaterial { Shader = twoSided ? s_silhouetteTwo ??= Make(false, true) : s_silhouette ??= Make(false, false), RenderPriority = LAST };
            f.SetShaderParameter("col", c);
            s_flat[key] = f;
            return f;
        }
        // cut-out parts (fur, hair, cloth fringes): their texture's outline
        var t = new ShaderMaterial { Shader = s_silhouetteCut ??= Make(true, true), RenderPriority = LAST };
        t.SetShaderParameter("col", c);
        Texture2D? tex = m switch
        {
            StandardMaterial3D sm => sm.AlbedoTexture,
            ShaderMaterial sh => sh.GetShaderParameter("texture_albedo").AsGodotObject() as Texture2D,
            _ => null,
        };
        if (tex != null) t.SetShaderParameter("tex", tex);
        return t;
    }

    static Shader Make(bool cut, bool twoSided) => new()
    {
        Code = @"
shader_type spatial;
// UnitsBehind: a unit's surfaces hidden behind a building, flat in the owner's colour
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, " + (twoSided ? "cull_disabled" : "cull_back") + @", fog_disabled, shadows_disabled;
stencil_mode read, compare_equal, " + STENCIL_BUILDING + @";
uniform vec3 col : source_color = vec3(1.0);
" + (cut ? "uniform sampler2D tex : source_color, hint_default_white, filter_linear_mipmap, repeat_enable;\n" : "") + @"
void fragment() {
" + (cut ? "\tif (texture(tex, UV).a < 0.5) discard;\n" : "") + @"	ALBEDO = col;
	ALPHA = 1.0;
}
",
    };
}
