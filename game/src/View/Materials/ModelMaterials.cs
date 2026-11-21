// The materials of the converted models, as the browser remake draws them: the glTF's PBR materials are swapped for
// cheap Lambert-like ones (StandardMaterial3D with lambert diffuse, no specular), glows (GSF material flag 0x4) become
// additive unlit materials, the textures come from one shared cache (assets/tex, loaded once for all models, with
// mipmaps), and party-colour materials (GSF flag 0x1000) get a tinted copy per player colour. Double-sided materials
// are a small shader of the same look that lights the camera's side of a mirrored model (TwoSided). The SEAS models'
// red paint is recoloured to the owner's colour (SeasPainted), and the "Original colours" option turns all player
// colours off (OriginalColours).
//
// Ported from remake/src/engine/assets.js (gsfAdditive, toLambert, tintParty, loadTexture) and the tint of
// remake/src/game/compose.js updateMask (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.View.Materials;

/// <summary>What the remake needs to know about a converted material (three.js kept it in material.userData).</summary>
public sealed class MaterialTag
{
    public string GsfFlags = "";
    /// <summary>party colour material (GSF flag 0x1000): tinted with the owner's colour</summary>
    public bool Party;
    /// <summary>additive glow (GSF flag 0x4): not lit, no depth written, no shadow</summary>
    public bool Glow;
    public Texture2D? Map;
    /// <summary>a SEAS material (texture seas_*): its red paint takes the owner's colour (ModelMaterials.SeasPainted)</summary>
    public bool Paint;
    /// <summary>the material belongs to a building (SeasMarkings.IsBuilding: its markings of Scope.Buildings)</summary>
    public bool Building;
    /// <summary>the material carries the snow code (SnowCover.Applies: buildings, walls, built scenery whose textures
    /// have no Icewaste variant)</summary>
    public bool Snow;
    /// <summary>its texture's key in the setting textures' index (SettingTextures.KeyOf; null: no variants) and where
    /// the variants come from (SettingMaterials.Variant)</summary>
    public string? SettingKey;
    public SettingSource? Settings;
    /// <summary>the setting of this material's texture ("icewaste"; null: the plain texture)</summary>
    public string? Setting;
    /// <summary>a setting variant's plain texture (SettingMaterials.Variant; null: not a variant) - the SEAS paint keys
    /// its white parts by it, never by the variant's painted snow (SeasPainted)</summary>
    public Texture2D? Plain;
    /// <summary>the model's name (SeasMarkings boxes for one model)</summary>
    public string? Model;
    /// <summary>the Lambert material it was made from (single-sided; for the painted copies of SeasPainted)</summary>
    public StandardMaterial3D? Source;
    /// <summary>a vegetation material flagged opaque that ToLambert cut out by the texture's alpha: the opaque material
    /// for the surfaces the cut would erase (BarkSurface: a trunk on the bark of jng_large_*, alpha 0 there)</summary>
    public Material? Bark;
}

/// <summary>Textures of assets/tex, shared by every model (engine/assets.js loadTexture).</summary>
public sealed class TextureCache
{
    static readonly PwRemake.Sim.Core.Logger L = Log.For("View.Textures");
    readonly AssetSource _src;
    readonly Dictionary<string, Texture2D?> _cache = new(StringComparer.OrdinalIgnoreCase);
    public TextureCache(AssetSource src) { _src = src; }
    public int Count => _cache.Count;
    public long Bytes { get; private set; }

    /// <summary>A texture by an image URI of a model ("../tex/hu_lvl_1_a.png", "textures/x.png") or a bare file name.</summary>
    public Texture2D? Get(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var file = Path.GetFileName(uri.Replace('\\', '/'));
        return Load(file, "assets/tex/" + file, file);
    }

    /// <summary>A texture by its asset path ("assets/tex/settings/icewaste/hu_lvl_1_a.png": a setting texture), named
    /// as the plain one it replaces (the SEAS markings and paint find their boxes by the texture's name).</summary>
    public Texture2D? GetAsset(string rel, string? name = null) => Load(rel, rel, name ?? Path.GetFileName(rel));

    Texture2D? Load(string key, string rel, string name)
    {
        if (_cache.TryGetValue(key, out var t)) return t;
        t = null;
        var path = _src.Find(rel);
        if (path != null)
        {
            var img = Image.LoadFromFile(path);
            if (img == null || img.IsEmpty()) L.Warn($"cannot read texture {path}");
            else
            {
                img.GenerateMipmaps();
                Bytes += img.GetData().Length;
                t = ImageTexture.CreateFromImage(img);
                t.ResourceName = name;
                img.Dispose();                // (the picture is the renderer's now; not kept until a collection)
            }
        }
        _cache[key] = t;
        return t;
    }
}

public static class ModelMaterials
{
    static readonly Dictionary<Material, MaterialTag> s_tags = new();
    static readonly Dictionary<(Material, uint), Material> s_tinted = new();
    static Shader? s_glowShader;

    public static MaterialTag? Tag(Material? m) => m != null && s_tags.TryGetValue(m, out var t) ? t : null;

    // GSF material flag 0x4 (docs/GSF_FORMAT.md): the picture is ADDED to what is behind it - light cones, glows, the
    // rays of the healing aura. Bright shapes on black: not lit, no depth written, alpha only where 0x1/0x2 ask for it.
    public static uint GsfAdditive(string? flags)
    {
        uint a1 = PartyColors.GsfFlags1(flags);
        return (a1 & 0x4) != 0 ? a1 : 0;
    }

    static readonly Regex VegName = new("^jng_", RegexOptions.IgnoreCase);
    static readonly Regex VegSrc = new(@"^vegetation[\\/]|^land[\\/]jng_misc_", RegexOptions.IgnoreCase);

    /// <summary>
    /// The remake's material for a converted glTF material. imported: Godot's material from the glTF (texture
    /// slots discarded at load), json: the glTF material (extras, texture refs), uri: resolves a texture reference.
    /// </summary>
    /// <param name="settings">the configuration's setting textures (null: none); snow: the shader snow is allowed (the
    /// model has no Icewaste variant of its textures: SnowCover)</param>
    public static Material ToLambert(StandardMaterial3D imported, JsonObject? json, string fourcc, Func<JsonNode?, Texture2D?> tex, string? model = null,
        SettingSource? settings = null, bool snow = true)
    {
        var ex = json?["extras"] as JsonObject;
        string flags = ex?["gsf_flags"].Str() ?? "";
        var map = tex(json?["pbrMetallicRoughness"]?["baseColorTexture"]);
        uint a1 = GsfAdditive(flags);
        if (a1 != 0)
        {
            s_glowShader ??= new Shader { Code = GlowShader };
            var g = new ShaderMaterial { Shader = s_glowShader, ResourceName = imported.ResourceName, RenderPriority = 3 };
            g.SetShaderParameter("map", map != null ? map : default(Variant));
            g.SetShaderParameter("use_alpha", (a1 & 0x3) != 0);
            s_tags[g] = new MaterialTag { GsfFlags = flags, Glow = true, Map = map };
            return g;
        }
        var l = new StandardMaterial3D
        {
            ResourceName = imported.ResourceName,
            DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            Roughness = 1, Metallic = 0, MetallicSpecular = 0,
            AlbedoColor = imported.AlbedoColor,
            AlbedoTexture = map,
            Transparency = imported.Transparency,
            AlphaScissorThreshold = imported.AlphaScissorThreshold,
            CullMode = imported.CullMode,
            VertexColorUseAsAlbedo = imported.VertexColorUseAsAlbedo,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        var nmap = tex(json?["normalTexture"]);
        if (nmap != null) { l.NormalEnabled = true; l.NormalTexture = nmap; }
        // the sequel's glowing parts: the texture of the lit signs, lamps and coils (emissiveMap: art/sequel/kit.py)
        var em = imported.Emission;
        if (imported.EmissionEnabled && (em.R > 0 || em.G > 0 || em.B > 0))
        {
            l.EmissionEnabled = true; l.Emission = em; l.EmissionEnergyMultiplier = imported.EmissionEnergyMultiplier;
            l.EmissionTexture = tex(json?["emissiveTexture"]);
            // glTF: the emissive colour is factor x texture (Godot's default operator adds them: a white factor would light
            // the whole surface - the sequel's houses glowed white instead of their windows)
            l.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
        }
        // vegetation textures carry cut-out alpha even where the source material was flagged opaque
        // (only plants: animal skins like jng_deinonychus_a keep other data in alpha and must stay opaque, and so do the
        // carried wood logs product_wood_* - Misc models cut from the bark area of jng_large_b, alpha 0 there - and the
        // rock and stone textures of Texture/Land: their alpha is a gloss mask, MIRAGE's jng_hill_b made the stone
        // deposits vanish; of Land only the grass and flower sheet jng_misc_* is cut out)
        string src = ex?["gsf_texture"].Str() ?? "";
        StandardMaterial3D? bark = null;
        if (VegName.IsMatch(imported.ResourceName ?? "") && (fourcc == "Vgtn" || fourcc == "Deko" || fourcc == "Ress")
            && (VegSrc.IsMatch(src) || src == "") && l.Transparency == BaseMaterial3D.TransparencyEnum.Disabled)
        {
            bark = (StandardMaterial3D)l.Duplicate();
            l.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; l.AlphaScissorThreshold = 0.5f;
            l.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }
        // GSF material flags; bit 0x1000 marks party-colour materials (tinted with the owner's colour, see Tinted)
        var tag = new MaterialTag { GsfFlags = flags, Party = PartyColors.IsPartyFlags(flags), Map = map, Bark = bark, Source = l,
            // (another tribe's texture with SEAS markings - ash_specloc_d, ash_triceratops_a - only on SEAS models)
            Paint = map != null && ((map.ResourceName ?? "").StartsWith(SEAS_PAINT_TEXTURES, StringComparison.OrdinalIgnoreCase)
                || SeasMarkings.HasMarks(map.ResourceName, model)),
            Building = SeasMarkings.IsBuilding(fourcc), Model = model };
        if (bark != null) s_tags[bark] = new MaterialTag { GsfFlags = flags, Map = map };
        // the original's setting textures (SettingMaterials): the variants of this texture, by its GSF texture path
        if (bark == null && settings != null && settings.Index.KeyOf(src, map?.ResourceName) is string skey)
        {
            tag.SettingKey = skey;
            tag.Settings = settings;
        }
        // double-sided: the same material as a shader that knows the models are mirrored (see TwoSided); a building's
        // without an Icewaste texture of its own: the shader with the snow of a snowy biome (SnowCover)
        tag.Snow = bark == null && snow && SnowCover.Applies(fourcc, model);
        Material res = tag.Snow ? LambertShader(l, l.CullMode == BaseMaterial3D.CullModeEnum.Disabled, false, true)
            : l.CullMode == BaseMaterial3D.CullModeEnum.Disabled ? TwoSided(l) : l;
        s_tags[res] = tag;
        return res;
    }

    // ---- bark under the vegetation cut-out
    //
    // The cut-out of ToLambert erased opaque surfaces that sit on the bark band of the jungle sheets (jng_large_a / _b:
    // the lower quarter, alpha 0): mission 10's treehouse stood without its tree (dh_darwins_treehouse mesh37, the trunk
    // and branches), jungle_tree_01 and jungle_dead_tree_01 without their lower trunks (docs/VIEW.md). A surface
    // whose triangles' centres lie almost all (90 %) where the alpha is below the threshold was never meant to be
    // cut: it keeps the opaque material, as the original draws a material without the alpha flags.
    static readonly Dictionary<Texture2D, Image?> s_images = new();

    public static bool BarkSurface(ArrayMesh m, int surface, Texture2D? tex)
    {
        if (tex == null) return false;
        if (!s_images.TryGetValue(tex, out var img)) s_images[tex] = img = tex.GetImage();
        if (img == null || img.IsEmpty()) return false;
        var arr = m.SurfaceGetArrays(surface);
        if (arr[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil) return false;
        var uv = arr[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var ix = arr[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.Nil ? arr[(int)Mesh.ArrayType.Index].AsInt32Array() : null;
        int n = ix != null ? ix.Length / 3 : uv.Length / 3, clear = 0;
        if (n == 0) return false;
        int w = img.GetWidth(), h = img.GetHeight();
        for (int t = 0; t < n; t++)
        {
            int a = ix != null ? ix[t * 3] : t * 3, b = ix != null ? ix[t * 3 + 1] : t * 3 + 1, c = ix != null ? ix[t * 3 + 2] : t * 3 + 2;
            var p = (uv[a] + uv[b] + uv[c]) / 3;
            float u = p.X - Mathf.Floor(p.X), v = p.Y - Mathf.Floor(p.Y);
            if (img.GetPixel(Math.Min(w - 1, (int)(u * w)), Math.Min(h - 1, (int)(v * h))).A < 0.5f) clear++;
        }
        return clear >= 0.9 * n;
    }

    // ---- double-sided materials on mirrored models
    //
    // Every model's root mirrors (scale x -1: the GSF frame is left-handed, ModelTemplate). For a mirrored instance
    // Godot swaps the culled side, but not the face winding the fragment shader sees: on a mirrored mesh the side
    // facing the camera has FRONT_FACING false. A StandardMaterial3D with cull disabled then turns the normal of every
    // visible face around (its back-face check, measured on Godot 4.7: a mirrored double-sided quad facing the sun
    // gets only ambient light) - every cloth, tent, sail, leaf, banner, fur fringe and alpha-tested part of the
    // original models (glTF doubleSided: GSF flag 0x1) was lit from behind: dark and flat, while single-sided parts
    // and the terrain were right. three.js sets the face winding of a mirrored object (frontFaceCW), so the browser
    // remake draws them lit. This shader is the Lambert material of ToLambert, plus: where the model matrix mirrors,
    // the normal Godot turned is turned back (a mirrored instance in the world, a model hung on a link; baked props
    // with an unmirrored transform, PropField, keep Godot's normal).
    // (the same shader, single-sided or double-sided, also with the SEAS paint key: SeasPainted)
    static readonly Dictionary<(BaseMaterial3D.TransparencyEnum, bool twoSided, bool paint, bool snow), Shader> s_twoSided = new();

    static Material TwoSided(StandardMaterial3D l) => LambertShader(l, true, false, false);

    static ShaderMaterial LambertShader(StandardMaterial3D l, bool twoSided, bool paint, bool snow)
    {
        var mode = l.Transparency;
        if (snow) SnowCover.EnsureGlobals();
        if (!s_twoSided.TryGetValue((mode, twoSided, paint, snow), out var sh))
            s_twoSided[(mode, twoSided, paint, snow)] = sh = new Shader { Code = TwoSidedShader(mode, twoSided, paint, snow) };
        var m = new ShaderMaterial { Shader = sh, ResourceName = l.ResourceName, RenderPriority = l.RenderPriority };
        m.SetShaderParameter("albedo", l.AlbedoColor);
        m.SetShaderParameter("texture_albedo", l.AlbedoTexture != null ? l.AlbedoTexture : default(Variant));
        m.SetShaderParameter("alpha_scissor_threshold", l.AlphaScissorThreshold);
        m.SetShaderParameter("use_vertex_color", l.VertexColorUseAsAlbedo);
        m.SetShaderParameter("use_normal_map", l.NormalEnabled && l.NormalTexture != null);
        if (l.NormalTexture != null) m.SetShaderParameter("texture_normal", l.NormalTexture);
        if (l.EmissionEnabled)
        {
            m.SetShaderParameter("emission", l.Emission);
            m.SetShaderParameter("emission_energy", l.EmissionEnergyMultiplier);
            if (l.EmissionTexture != null) m.SetShaderParameter("texture_emission", l.EmissionTexture);
            m.SetShaderParameter("emission_multiply", l.EmissionOperator == BaseMaterial3D.EmissionOperatorEnum.Multiply && l.EmissionTexture != null);
        }
        return m;
    }

    /// <summary>The albedo colour of a model material (StandardMaterial3D or the double-sided shader).</summary>
    public static Color Albedo(Material m) => m switch
    {
        StandardMaterial3D sm => sm.AlbedoColor,
        ShaderMaterial sh when sh.GetShaderParameter("albedo").VariantType == Variant.Type.Color => sh.GetShaderParameter("albedo").AsColor(),
        _ => Colors.White,
    };

    /// <summary>a copy of a model material with another albedo texture (a setting texture: SettingMaterials); null for
    /// a material that is not ToLambert's</summary>
    public static Material? WithTexture(Material m, Texture2D tex)
    {
        switch (m)
        {
            case StandardMaterial3D sm: { var d = (StandardMaterial3D)sm.Duplicate(); d.AlbedoTexture = tex; return d; }
            case ShaderMaterial sh when sh.Shader != null && s_twoSided.ContainsValue(sh.Shader):
                { var d = (ShaderMaterial)sh.Duplicate(); d.SetShaderParameter("texture_albedo", tex); return d; }
            default: return null;
        }
    }

    /// <summary>the tag of a material made outside (SettingMaterials' variants)</summary>
    public static void SetTag(Material m, MaterialTag tag) => s_tags[m] = tag;

    static Material? WithAlbedo(Material m, Color c)
    {
        switch (m)
        {
            case StandardMaterial3D sm: { var d = (StandardMaterial3D)sm.Duplicate(); d.AlbedoColor = c; return d; }
            case ShaderMaterial sh when sh.Shader != null && s_twoSided.ContainsValue(sh.Shader):
                { var d = (ShaderMaterial)sh.Duplicate(); d.SetShaderParameter("albedo", c); return d; }
            default: return null;
        }
    }

    static string TwoSidedShader(BaseMaterial3D.TransparencyEnum mode, bool twoSided, bool paint, bool snow)
    {
        bool blend = mode is BaseMaterial3D.TransparencyEnum.Alpha or BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass or BaseMaterial3D.TransparencyEnum.AlphaHash;
        bool scissor = mode == BaseMaterial3D.TransparencyEnum.AlphaScissor;
        string rm = "blend_mix, " + (twoSided ? "cull_disabled" : "cull_back") + ", diffuse_lambert, specular_disabled, "
            + (mode == BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass ? "depth_prepass_alpha" : "depth_draw_opaque");
        return @"
shader_type spatial;
// ModelMaterials.TwoSided: ToLambert's material (StandardMaterial3D, lambert diffuse, no specular) for double-sided
// parts, lit on the side facing the camera also when the model matrix mirrors
render_mode " + rm + @";
uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D texture_albedo : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float alpha_scissor_threshold = 0.5;
uniform bool use_vertex_color = false;
uniform bool use_normal_map = false;
uniform sampler2D texture_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec3 emission : source_color = vec3(0.0);
uniform float emission_energy = 1.0;
uniform bool emission_multiply = false;
uniform sampler2D texture_emission : source_color, hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
" + (paint ? SeasPaintGlsl : "") + (snow ? SnowCover.Uniforms : "") + @"
varying flat float mirrored;
void vertex() {
	mirrored = determinant(mat3(MODEL_MATRIX)) < 0.0 ? 1.0 : 0.0;
	" + (paint ? "seas_lpos = VERTEX;" : "") + @"
}
void fragment() {
	// (Godot turned the normal of a face whose winding looks like a back face; on a mirrored model that is the front)
	" + (twoSided ? "if (mirrored > 0.5) NORMAL = -NORMAL;" : "") + @"
	vec4 a = albedo * texture(texture_albedo, UV);
	if (use_vertex_color) a *= COLOR;
	" + (paint ? "a.rgb = seas_paint(a.rgb, (albedo * (use_plain ? textureLod(paint_plain, UV, " + F(SEAS_PAINT_LOD) + ") : textureLod(texture_albedo, UV, " + F(SEAS_PAINT_LOD) + "))).rgb, (albedo * textureLod(texture_albedo, UV, " + F(SEAS_MARK_LOD) + ")).rgb, seas_unsnowed(seas_mask(UV), UV));" : "") + @"
" + (snow ? SnowCover.Fragment : "") + @"
	ALBEDO = a.rgb;
	METALLIC = 0.0;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	if (use_normal_map) NORMAL_MAP = texture(texture_normal, UV).rgb;
	vec3 et = texture(texture_emission, UV).rgb;
	EMISSION = (emission_multiply ? emission * et : emission + et) * emission_energy;
" + (blend || scissor ? "\tALPHA = a.a;\n" : "") + (scissor ? "\tALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;\n" : "") + "}\n";
    }

    /// <summary>
    /// Party colour. The original (PWClient material setup 0x6ac340) draws materials with GSF flag 0x1000 as
    /// texture × lighting × colour × 2 (D3D MODULATE2X with TFACTOR), and their texture areas are greyscale painted at
    /// about half brightness (hu_tech_a: 61-80 against 117 elsewhere) so the × 2 brings them up to full brightness.
    /// The original then skipped the stage for an object without an owner (dark, "black" parts); MIRAGE's
    /// mirage_party_colour_no_owner runs it with white, as here: an unowned object and the "Original colours" option
    /// pass Colors.White (PARTY_NEUTRAL). The × 2 is applied in gamma space as the original did (the albedo's linear
    /// value is the colour's times 2^2.2). One tinted copy per material and colour is shared by every model of that player.
    /// </summary>
    public static Material PartyTinted(Material baseMat, Color party)
    {
        var key = (baseMat, (uint)party.ToRgba32());
        if (s_tinted.TryGetValue(key, out var t)) return t;
        var c = WithAlbedo(baseMat, Modulate2X(party) with { A = Albedo(baseMat).A });
        if (c == null) return baseMat;
        s_tags[c] = s_tags.TryGetValue(baseMat, out var tag) ? tag : new MaterialTag();
        s_tinted[key] = c;
        return c;
    }

    /// <summary>The colour of an unowned object's party materials and of the "Original colours" option (MIRAGE: white × 2).</summary>
    public static readonly Color PARTY_NEUTRAL = Colors.White;
    /// <summary>MODULATE2X: the albedo (sRGB, may exceed 1) that draws texture × c × 2 in gamma space.</summary>
    public static Color Modulate2X(Color c)
    {
        var l = c.SrgbToLinear();
        float k = MathF.Pow(2f, 2.2f);
        return new Color(l.R * k, l.G * k, l.B * k, 1).LinearToSrgb() with { A = c.A };
    }

    /// <summary>
    /// The party-colour parts of an owned animal or vehicle (attr bit 5, compose.js updateMask): the material's colour
    /// times the player's colour times 1.6 (in linear space, as three.js multiplied its linear colours).
    /// </summary>
    public static Material AnimalTinted(Material baseMat, Color party)
    {
        var key = (baseMat, (uint)party.ToRgba32() ^ 0x9e3779b9u);
        if (s_tinted.TryGetValue(key, out var t)) return t;
        Color a0 = Albedo(baseMat), a = a0.SrgbToLinear(), p = party.SrgbToLinear();
        var c = WithAlbedo(baseMat, new Color(a.R * p.R * 1.6f, a.G * p.G * 1.6f, a.B * p.B * 1.6f, 1).LinearToSrgb() with { A = a0.A });
        if (c == null) return baseMat;
        s_tags[c] = s_tags.TryGetValue(baseMat, out var tag) ? tag : new MaterialTag();
        s_tinted[key] = c;
        return c;
    }

    // ---- player colours on SEAS models, the "Original colours" option
    //
    // The SEAS were never meant to carry player colours: their models have few party-colour materials (GSF flag 0x1000:
    // the soldiers' and riders' cloth, a few building parts; none on most vehicles, robots, weapons), so their sides
    // were hard to tell apart. SeasPainted gives the materials of the SEAS textures (seas_*) a copy per player colour whose white and
    // light grey parts (trims, panels, window frames, cloth: unsaturated and bright, read from a blurred copy so whole
    // parts change, not specks) take the owner's colour the way the other tribes' marked parts do (the texture times the
    // colour). Per texture at most SEAS_PAINT_SHARE of it, its brightest such parts (Kacper: "like 30% of the model",
    // "try to paint white elements"). Where a texture has little white, SeasMarkings adds marked parts (paint_mask:
    // stripes, bands, emblems, armour plates, cloth). Glows (fire, lights, rays: GSF flag 0x4) are other materials.

    /// <summary>The textures whose materials are painted (the SEAS': seas_building_a, seas_carrier, seas_worker ...).</summary>
    public const string SEAS_PAINT_TEXTURES = "seas_";
    /// <summary>Tuning: the most saturation (HSV) a texel may have to count as white / grey.</summary>
    public static readonly float SEAS_PAINT_MAX_SAT = Tune(0, 0.22f);
    /// <summary>Tuning: the least brightness of a white part (per texture raised so at most SEAS_PAINT_SHARE is painted).</summary>
    public static readonly float SEAS_PAINT_MIN_VAL = Tune(1, 0.5f);
    /// <summary>Tuning: how strongly the owner's colour is applied (1: the texture times the colour, as the other tribes).</summary>
    public static readonly float SEAS_PAINT_STRENGTH = Tune(2, 0.9f);
    /// <summary>Tuning: brightness of the painted parts (the colour multiplied in darkens them).</summary>
    public static readonly float SEAS_PAINT_LIFT = Tune(3, 1.15f);
    /// <summary>Tuning: the mip level the key is read from (higher: larger, smoother painted areas).</summary>
    static readonly float SEAS_PAINT_LOD = Tune(4, 2f);
    /// <summary>Tuning: the largest share of a texture painted.</summary>
    public static readonly float SEAS_PAINT_SHARE = Tune(5, 0.3f);
    /// <summary>Tuning: brightness of the marked parts (SeasMarkings; linear, times the colour, the shading kept).</summary>
    public static readonly float SEAS_MARK_LEVEL = Tune(6, 0.5f);
    /// <summary>Tuning: the mip level of the local average a marked texel's shading is measured against.</summary>
    static readonly float SEAS_MARK_LOD = Tune(7, 4f);
    /// <summary>the share of white a setting variant adds to a texel (its snow) from which the paint fades, and where
    /// it is gone (seas_snow)</summary>
    const float SEAS_SNOW_FROM = 0.06f, SEAS_SNOW_TO = 0.4f;
    // (PW_SEAS_PAINT="max sat,min val,strength,lift,lod,share,mark level,mark lod": the constants for a tuning run,
    // docs/VIEW.md)
    static float Tune(int i, float d)
    {
        var e = System.Environment.GetEnvironmentVariable("PW_SEAS_PAINT")?.Split(',');
        return e != null && e.Length > i && float.TryParse(e[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    }

    /// <summary>"Original colours (no player colours)" (Options, Graphics; AppSettings.OriginalColors): no model is tinted.</summary>
    public static bool OriginalColours { get; private set; }
    /// <summary>counts the changes of OriginalColours (models re-apply their tints when it changed)</summary>
    public static int TintEpoch { get; private set; }
    public static void SetOriginalColours(bool on) { if (on != OriginalColours) { OriginalColours = on; TintEpoch++; } }

    static readonly Dictionary<Texture2D, float> s_paintMin = new();

    /// <summary>
    /// The least brightness of the painted white for a texture: its unsaturated texels (a 64 px copy, as blurred as the
    /// shader's key) sorted by brightness, the line above which SEAS_PAINT_SHARE of the texture lies; never below
    /// SEAS_PAINT_MIN_VAL. Unreadable: SEAS_PAINT_MIN_VAL.
    /// </summary>
    static float PaintThreshold(Texture2D? tex)
    {
        if (tex == null) return SEAS_PAINT_MIN_VAL;
        if (s_paintMin.TryGetValue(tex, out var v)) return v;
        v = SEAS_PAINT_MIN_VAL;
        try
        {
            var img = tex.GetImage();
            if (img != null && !img.IsEmpty())
            {
                img = (Image)img.Duplicate();
                if (img.IsCompressed()) img.Decompress();
                img.Resize(64, 64, Image.Interpolation.Bilinear);
                var vals = new List<float>();
                int n = 0;
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        var c = img.GetPixel(x, y);
                        if (c.A < 0.5f) continue;
                        n++;
                        c.ToHsv(out _, out float ss, out float vv);
                        if (ss <= SEAS_PAINT_MAX_SAT && vv >= SEAS_PAINT_MIN_VAL) vals.Add(vv);
                    }
                int want = (int)(n * SEAS_PAINT_SHARE);
                if (vals.Count > want && want > 0) { vals.Sort(); v = Math.Max(SEAS_PAINT_MIN_VAL, vals[vals.Count - want]); }
            }
        }
        catch (Exception) { /* not readable: the default */ }
        s_paintMin[tex] = v;
        return v;
    }

    /// <summary>
    /// A SEAS material (MaterialTag.Paint) with its white parts in a player's colour; one copy per material and colour.
    /// The material itself for a material it cannot paint.
    /// </summary>
    public static Material SeasPainted(Material baseMat, Color party)
    {
        var tag = Tag(baseMat);
        if (tag?.Paint != true || tag.Source == null) return baseMat;
        var key = (baseMat, (uint)party.ToRgba32() ^ 0x5ea5u);
        if (s_tinted.TryGetValue(key, out var t)) return t;
        var m = LambertShader(tag.Source, tag.Source.CullMode == BaseMaterial3D.CullModeEnum.Disabled, true, tag.Snow);
        m.SetShaderParameter("albedo", Albedo(baseMat));
        m.SetShaderParameter("paint_col", party);
        // a setting variant (MIRAGE's Icewaste seas_building_a / seas_tent: snow painted on the roofs, ledges and cloth):
        // the white parts are those of the plain texture (its threshold, its key), and the snow the variant adds -
        // brighter than the plain texel - stays unpainted (paint_plain: seas_snow). Keyed on the variant itself, the
        // white snow was the "white part": yellow blotches on a yellow player's roofs, a yellow-green tent.
        var keyTex = tag.Plain ?? tag.Source.AlbedoTexture;
        if (tag.Plain != null) { m.SetShaderParameter("paint_plain", tag.Plain); m.SetShaderParameter("use_plain", true); }
        // (a texture of another tribe with SEAS markings - ash_specloc_d of the SEAS defences: its markings only, no
        // white key - a brightness no texel reaches)
        bool seasTex = (keyTex?.ResourceName ?? "").StartsWith(SEAS_PAINT_TEXTURES, StringComparison.OrdinalIgnoreCase);
        m.SetShaderParameter("paint_min_val", seasTex ? PaintThreshold(keyTex) : 2f);
        if (SeasMarkings.Mask(keyTex, tag.Building, tag.Model) is { } mask) m.SetShaderParameter("paint_mask", mask);
        if (SeasMarkings.OutlineFor(tag.Model, keyTex?.ResourceName) is { } o)
        {
            m.SetShaderParameter("outline_pts", o.Points);
            m.SetShaderParameter("outline_n", o.Points.Length);
            m.SetShaderParameter("outline_uv", new Vector4(o.U0, o.V0, o.U1, o.V1));
            m.SetShaderParameter("outline_w", o.Width);
        }
        s_tags[m] = tag;
        s_tinted[key] = m;
        return m;
    }

    // the key (a blurred texel: unsaturated and bright = a white / grey part) and the tint (the texel times the colour)
    static readonly string SeasPaintGlsl = $@"
uniform vec3 paint_col : source_color = vec3(1.0); // the owner's colour
uniform float paint_min_val = 0.5; // the least brightness of a painted white (per texture: ModelMaterials.PaintThreshold)
// the texture's markings (SeasMarkings.Mask: red 1 = painted, green 1 = kept unpainted; none: black)
uniform sampler2D paint_mask : hint_default_black, filter_linear, repeat_enable;
// a painted outline in the mesh frame (SeasMarkings.Outline: the small tent's net border; outline_w 0 = none)
uniform vec2 outline_pts[12];
uniform int outline_n = 0;
uniform vec4 outline_uv = vec4(0.0);
uniform float outline_w = 0.0;
// a setting variant's plain texture (ModelMaterials.SeasPainted: the white key's source; use_plain false: none)
uniform sampler2D paint_plain : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_plain = false;
varying vec3 seas_lpos;
vec2 seas_mask(vec2 uv) {{
	vec2 m = texture(paint_mask, uv).rg;
	if (outline_w > 0.0) {{
		vec2 u = fract(uv);
		float g = step(outline_uv.x, u.x) * step(u.x, outline_uv.z) * step(outline_uv.y, u.y) * step(u.y, outline_uv.w);
		float d = 1e6;
		for (int i = 0; i < outline_n; i++) {{
			vec2 a = outline_pts[i], b = outline_pts[(i + 1) % outline_n], p = seas_lpos.xy;
			float t = clamp(dot(p - a, b - a) / max(dot(b - a, b - a), 1e-6), 0.0, 1.0);
			d = min(d, length(p - a - t * (b - a)));
		}}
		m.r = max(m.r, g * (1.0 - smoothstep(outline_w * 0.7, outline_w, d)));
	}}
	return m;
}}
// The snow a setting variant paints over the plain texture (MIRAGE's Icewaste seas_building_a / seas_tent): the share of
// white the variant's texel adds to the plain one's, (variant - plain) / (1 - plain) in brightness (sRGB), 0..1 - the
// paint fades out there, the snow stays white. 0 without a plain texture (the plain material) and where a variant adds
// no white (the Jungle's moss, the Ashvalley's soot: the paint as on the plain texture).
float seas_snow(vec2 uv) {{
	if (!use_plain) return 0.0;
	vec3 v = texture(texture_albedo, uv).rgb, p = texture(paint_plain, uv).rgb;
#if CURRENT_RENDERER != RENDERER_COMPATIBILITY
	v = pow(max(v, vec3(0.0)), vec3(1.0 / 2.2)); p = pow(max(p, vec3(0.0)), vec3(1.0 / 2.2));
#endif
	const vec3 LUM = vec3(0.2126, 0.7152, 0.0722);
	float lp = dot(p, LUM);
	return smoothstep({F(SEAS_SNOW_FROM)}, {F(SEAS_SNOW_TO)}, clamp((dot(v, LUM) - lp) / max(1.0 - lp, 0.05), 0.0, 1.0));
}}
// the markings with the snow kept unpainted as a green mark is (mk.g: the white key and the markings fade out)
vec2 seas_unsnowed(vec2 mk, vec2 uv) {{ return vec2(mk.r, max(mk.g, seas_snow(uv))); }}
vec3 rgb2hsv(vec3 c) {{
	vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
	vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
	vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));
	float d = q.x - min(q.w, q.y);
	return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + 1e-6)), d / (q.x + 1e-6), q.x);
}}
// (the Compatibility renderer - OpenGL - hands the shader the texture's sRGB values; Forward+ / Mobile linear ones: the
// key is taken in sRGB either way)
// White key: the texel times the colour. Markings (mask): the colour at SEAS_MARK_LEVEL times the texel's brightness
// against its neighbourhood's (avglin, a high mip) - dark or red paint takes the colour as a coat of paint, its
// shading, dirt and scratches kept. Both: k = max(key, mask) x strength.
vec3 seas_paint(vec3 lin, vec3 keylin, vec3 avglin, vec2 mk) {{
	float mask = mk.r;
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
	vec3 kc = keylin;
	float level = {F(MathF.Pow(SEAS_MARK_LEVEL, 1 / 2.2f))};
#else
	vec3 kc = pow(max(keylin, vec3(0.0)), vec3(1.0 / 2.2));
	float level = {F(SEAS_MARK_LEVEL)};
#endif
	vec3 khsv = rgb2hsv(kc);
	float kk = (1.0 - smoothstep({F(SEAS_PAINT_MAX_SAT)}, {F(SEAS_PAINT_MAX_SAT + 0.08f)}, khsv.y))
		* smoothstep(paint_min_val - 0.05, paint_min_val + 0.03, khsv.z);
	const vec3 LUM = vec3(0.2126, 0.7152, 0.0722);
	vec3 coat = paint_col * level * clamp(dot(lin, LUM) / max(dot(avglin, LUM), 0.004), 0.0, 1.6);
	vec3 target = mix(lin * paint_col * {F(SEAS_PAINT_LIFT)}, coat, clamp((mask - kk) * 4.0 + 0.5, 0.0, 1.0));
	return mix(lin, target, max(kk, mask) * (1.0 - mk.g) * {F(SEAS_PAINT_STRENGTH)});
}}
";
    static string F(float v) => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    static readonly string GlowShader = @"
shader_type spatial;
// GSF material flag 0x4: added to what is behind it, not lit (engine/assets.js toLambert: MeshBasicMaterial, custom
// blending SrcAlpha or One / One). Added as the original adds, to the screen's sRGB value (Fx/GammaBlend; the texture
// sampled as its sRGB values - no source_color); until 2026-11-18 added in linear light in Forward+, fainter.
render_mode blend_add, unshaded, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;
uniform sampler2D map : filter_linear_mipmap;
uniform bool use_alpha = false;
" + PwRemake.View.Fx.GammaBlend.Glsl + @"
void fragment() {
    vec4 t = texture(map, UV);
    ALBEDO = pw_gamma_add(t.rgb * (use_alpha ? t.a : 1.0));
    ALPHA = 1.0;
}
";
}
