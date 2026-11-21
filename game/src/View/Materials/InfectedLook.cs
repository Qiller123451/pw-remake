// The "infected" look of buildings and animals (docs/INFECTED_LOOK.md): a material overlay a model switches on with
// ModelInstance.SetInfected(stage) - view only, nothing in the simulation. Kacper 2026-11-19 (a mission in the spirit of
// StarCraft 2's "Outbreak"): "give a new texture/effects for infected buildings/units ... some green/decay effect like
// in Warcraft 3".
//
// What the game has is used as it is (the user's rule: the original's assets wherever they exist): the poison clouds
// fx_poison / fx_poison_big, the meat flies of the Norse lumberjack cottage (an emitter, Particles.TrackNode), the
// game's own fires and lights recoloured, the undead's palette (InfectedLookData). What the game lacks is procedural,
// in the model's shader (this file):
//
//   Buildings (any model that is not an animal): the surfaces rot - desaturated and darkened towards the undead's
//   grey-olive, rotten patches; a sickly green-black slime creeps up from the ground (height above the model's origin,
//   its edge broken by noise), moss on the upward faces; glowing green veins / cracks (emissive, pulsing softly).
//   Party colour parts (GSF 0x1000) rot half as much, so the owner stays readable.
//   Animals (fourcc Anim): the skin turns pale grey-green with dark bruises; torn wounds - ellipsoids of the wound table
//   in the bind pose (the vertex's bind position in CUSTOM0, so they move with the animation): inside them the surface
//   is cut away (discard - also in the shadow pass) except rib / spine / bone bands in bone colour and a torn edge of
//   raw flesh; the cut's far side (back faces, the material is double-sided) is dark red flesh; a wet dark-red rim and
//   blood running down below each wound; green glowing eyes.
//   Glows (GSF 0x4: fire, light cones) and the model's particles: green (pw_green; Particles reads
//   ModelInstance.GreenFire).
//
// The infected materials are copies of the model's materials (ToLambert's StandardMaterial3D or Lambert shaders, the
// party tinted and setting variants), one per (material, stage[, animal model]), shared by every infected model. They
// are the Lambert shader of ModelMaterials with the infection code; an infected copy has no SEAS paint and no shader
// snow (the blight covers them). Colours: the albedo's space (linear in Forward+ / Mobile, sRGB values in
// Compatibility - GammaBlend.pw_mix_out turns the sRGB constants into it).
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.View.Fx;
using PwRemake.View.Models;

namespace PwRemake.View.Materials;

public static class InfectedLook
{
    /// <summary>how the ground under a building is found for its blight (world x, z -> height); null: flat at the
    /// building's foot. The game's world view sets the terrain's.</summary>
    public static Func<float, float, float>? GroundHeight;

    /// <summary>a model drawn with the animal look (skin, wounds, eyes) rather than the building look</summary>
    public static bool IsAnimal(ModelTemplate t) => t.Fourcc == "Anim";

    /// <summary>the look a model wears by itself (InfectedLookData.KindOf; an attached part: ChildKind)</summary>
    public static LookKind KindOf(ModelTemplate t) => InfectedLookData.KindOf(t.Name, t.Fourcc, t.HasSkeleton || t.HasClips);

    /// <summary>the kinds drawn from their bind positions (CUSTOM0: InfectedLook.RestMesh) - they move</summary>
    public static bool UsesRest(LookKind k) => k is LookKind.Animal or LookKind.Character or LookKind.Undead or LookKind.Vehicle;

    // ------------------------------------------------------------------------------------------- materials

    static readonly Dictionary<(Material, int, LookKind, string), Material> s_cache = new();
    static readonly Dictionary<(LookKind kind, bool twoSided, BaseMaterial3D.TransparencyEnum mode), Shader> s_shaders = new();
    static Shader? s_glow;

    /// <summary>
    /// The infected copy of a model material at a stage (stage 0: m itself). kind: the look (KindOf / ChildKind);
    /// model, bounds: the model's name and its box (ModelTemplate.Bounds, ModelRoot space) - a building's slime height
    /// and tune (InfectedLookData.TuneFor); wounds: an animal's or character's wound table and eyes (WoundsOf).
    /// </summary>
    public static Material Material(Material m, int stage, LookKind kind, string model, Aabb bounds, AnimalWounds? wounds = null)
    {
        if (stage <= 0) return m;
        var key = (m, stage, kind, model);
        if (s_cache.TryGetValue(key, out var c)) return c;
        long t0 = PwRemake.View.PerfProbe.P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var st = InfectedLookData.Stage(stage);
        var tag = ModelMaterials.Tag(m);
        bool body = kind is LookKind.Animal or LookKind.Character or LookKind.Undead;
        if (tag?.Glow == true)
        {
            // fire, light cones, halos: green from the stage that turns the fires (the others keep their light)
            c = st.GreenFire ? GreenGlow(m) : m;
        }
        else if (Read(m) is { } src)
        {
            var sh = ShaderFor(kind, kind == LookKind.Animal || src.TwoSided, src.Mode);
            // (an animal's surfaces draw both sides - the inside of the body seen through a wound; a material that was
            // double-sided already is thin - a frill, a wing, an ear: its back is no inside)
            var sm = new ShaderMaterial { Shader = sh, ResourceName = m.ResourceName + "#infected" + stage, RenderPriority = m.RenderPriority };
            sm.SetShaderParameter("albedo", src.Albedo);
            sm.SetShaderParameter("texture_albedo", src.Tex != null ? src.Tex : default(Variant));
            sm.SetShaderParameter("alpha_scissor_threshold", src.Scissor);
            sm.SetShaderParameter("use_vertex_color", src.VertexColor);
            sm.SetShaderParameter("use_normal_map", src.Normal != null);
            if (src.Normal != null) sm.SetShaderParameter("texture_normal", src.Normal);
            bool undead = kind == LookKind.Undead;
            sm.SetShaderParameter("infect_rot", undead ? 0f : (float)(kind == LookKind.Item ? st.Rot * 0.7 : st.Rot));
            sm.SetShaderParameter("infect_party", tag?.Party == true ? 1f : 0f);
            if (body)
            {
                var a = wounds ?? new AnimalWounds(model, Array.Empty<Wound>(), null);
                // (the undead: no wounds - they are rotten already - only the eyes)
                var w = undead ? Array.Empty<Wound>() : InfectedLookData.WoundsAt(a, stage);
                var wc = new Vector4[InfectedLookData.MAX_WOUNDS];
                var wr = new Vector4[InfectedLookData.MAX_WOUNDS];
                for (int i = 0; i < w.Length; i++)
                {
                    wc[i] = new Vector4((float)w[i].X, (float)w[i].Y, (float)w[i].Z, (int)w[i].Kind);
                    wr[i] = new Vector4((float)w[i].Rx, (float)w[i].Ry, (float)w[i].Rz, i);
                }
                sm.SetShaderParameter("wound_c", wc);
                sm.SetShaderParameter("wound_r", wr);
                sm.SetShaderParameter("wound_n", w.Length);
                var e = a.Eyes;
                // (the undead's eyes glow from the first stage: the only sign they carry)
                bool eyes = (st.Eyes || undead) && e != null;
                sm.SetShaderParameter("eyes", eyes ? new Vector4((float)(e!.Value.Cx + e.Value.Dx), (float)e.Value.Y, (float)e.Value.Z, (float)e.Value.R) : Vector4.Zero);
                sm.SetShaderParameter("eyes2", eyes ? new Vector3((float)(e!.Value.Cx - e.Value.Dx), (float)e.Value.Y, (float)e.Value.Z) : Vector3.Zero);
                sm.SetShaderParameter("infect_skin", undead ? 0f : (float)st.Skin);
                sm.SetShaderParameter("infect_bruise", undead ? 0f : (float)st.Bruises);
                sm.SetShaderParameter("infect_veins", undead ? 0f : (float)st.Veins);
                if (kind == LookKind.Animal) sm.SetShaderParameter("thin", src.TwoSided);
            }
            else
            {
                var tune = kind == LookKind.Building ? InfectedLookData.TuneFor(model) : InfectedLookData.BuildingTune.Default;
                // (an item - a weapon, a tool - has no ground to grow from: rot and veins only; a vehicle's slime climbs
                // from its keel / wheels as on a low building)
                double h = kind == LookKind.Item ? 0 : bounds.Size.Y;
                sm.SetShaderParameter("infect_slime", (float)InfectedLookData.SlimeOn(h, stage, tune));
                sm.SetShaderParameter("infect_moss", kind == LookKind.Item ? 0f : (float)st.Moss);
                sm.SetShaderParameter("infect_veins", (float)(st.Veins * tune.Veins));
                sm.SetShaderParameter("vein_scale", (float)tune.VeinScale);
            }
            if (tag != null) ModelMaterials.SetTag(sm, tag);
            c = sm;
        }
        else c = m;
        s_cache[key] = c;
        PwRemake.View.PerfProbe.P?.Add("inf:new material", System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        return c;
    }

    /// <summary>(compatibility) the look of an animal (animal = true) or a building</summary>
    public static Material Material(Material m, int stage, bool animal, string model, Aabb bounds) =>
        Material(m, stage, animal ? LookKind.Animal : LookKind.Building, model, bounds, animal ? InfectedLookData.Animal(model) ?? GenericFor(model, bounds) : null);

    static readonly Dictionary<ModelTemplate, AnimalWounds?> s_wounds = new();

    /// <summary>
    /// The wound table and eyes of an animal or character template: an animal's from the table
    /// (InfectedLookData.Animal), else from its skin (FromSkin: the bind-pose vertices of its body meshes); a
    /// character's from its skeleton (CharacterWounds: the bones' bind positions), else from its box. Once per template.
    /// </summary>
    public static AnimalWounds? WoundsOf(ModelTemplate t, LookKind kind)
    {
        if (kind is not (LookKind.Animal or LookKind.Character or LookKind.Undead)) return null;
        if (s_wounds.TryGetValue(t, out var w)) return w;
        var model = t.Scene.GetNodeOrNull<Node3D>("Model");
        if (kind == LookKind.Animal)
            w = InfectedLookData.Animal(t.Name) ?? (model != null ? InfectedLookData.FromSkin(t.Name, SkinPoints(t, model)) : null) ?? GenericFor(t.Name, t.Bounds);
        else
        {
            var joints = model != null ? Joints(model) : new Dictionary<string, (double, double, double)>();
            w = InfectedLookData.CharacterWounds(t.Name, b => joints.TryGetValue(b, out var p) ? p : null);
            // (on this model's body: its armour, cloak or bare neck)
            if (w != null && model != null) w = InfectedLookData.OntoSkin(w, SkinPoints(t, model));
            if (w == null)
            {
                var b = t.Bounds;
                w = InfectedLookData.GenericCharacter(t.Name, -b.End.X, -b.Position.X, -b.End.Z, -b.Position.Z, b.Position.Y, b.End.Y);
            }
        }
        s_wounds[t] = w;
        return w;
    }

    // the bind-pose vertices of a template's body meshes (attribute 0x1f bits only: no gear, no lower detail), GSF frame
    static List<(double X, double Y, double Z)> SkinPoints(ModelTemplate t, Node3D model)
    {
        var l = new List<(double, double, double)>();
        foreach (var p in t.Parts)
        {
            if (p.HasAttr && (p.Attr & 0xffffffe0u) != 0) continue;
            if (t.Scene.GetNodeOrNull<MeshInstance3D>(p.Path) is not { Mesh: ArrayMesh am } mi) continue;
            var xf = mi.Skin != null ? ModelLibrary.ToRoot(mi, model) : ModelLibrary.RestToRoot(mi, model);
            for (int s = 0; s < am.GetSurfaceCount(); s++)
                foreach (var v in am.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                {
                    var q = xf * v;
                    l.Add((q.X, q.Y, q.Z));
                }
        }
        return l;
    }

    // the bind positions of a template's bones (its skins' bind poses: the joint's origin), GSF frame
    static Dictionary<string, (double X, double Y, double Z)> Joints(Node3D model)
    {
        var d = new Dictionary<string, (double, double, double)>();
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Skin: { } skin } mi)
            {
                var xf = ModelLibrary.ToRoot(mi, model);
                var sk = mi.GetNodeOrNull<Skeleton3D>(mi.Skeleton);
                for (int i = 0; i < skin.GetBindCount(); i++)
                {
                    // (the glTF import names the binds by their bone index: the skeleton's bone of that index)
                    string name = skin.GetBindName(i);
                    int bone = skin.GetBindBone(i);
                    if (string.IsNullOrEmpty(name) && sk != null && bone >= 0 && bone < sk.GetBoneCount()) name = sk.GetBoneName(bone);
                    if (string.IsNullOrEmpty(name) || d.ContainsKey(name)) continue;
                    var o = xf * skin.GetBindPose(i).AffineInverse().Origin;
                    d[name] = (o.X, o.Y, o.Z);
                }
            }
            foreach (var c in n.GetChildren()) Walk(c);
        }
        Walk(model);
        return d;
    }

    // wounds from the model's box: ModelRoot space (Godot, y up) -> the GSF frame (ModelTemplate: x mirrored, GSF y = -z,
    // GSF z = y)
    static AnimalWounds GenericFor(string model, Aabb b) =>
        InfectedLookData.Generic(model, -b.End.X, -b.Position.X, -b.End.Z, -b.Position.Z, b.Position.Y, b.End.Y);

    // ------------------------------------------------------------------------------------------- green emitters

    static readonly Dictionary<string, bool> s_greenEmitter = new();

    /// <summary>
    /// Whether a model's emitter turns green when the model's fires do (Particles: ModelInstance.GreenFire): its
    /// name "template/node" - fires, smoke and lights do, insects, dust, stones and the building tune's Keep (the
    /// harbour's water) not (InfectedLookData.EmitterTurnsGreen). Cached per name.
    /// </summary>
    public static bool TurnsGreen(string emitterName)
    {
        if (s_greenEmitter.TryGetValue(emitterName, out var g)) return g;
        int k = emitterName.IndexOf('/');
        string model = k > 0 ? emitterName.Substring(0, k) : "", node = k >= 0 ? emitterName.Substring(k + 1) : emitterName;
        g = InfectedLookData.EmitterTurnsGreen(node, InfectedLookData.TuneFor(model));
        s_greenEmitter[emitterName] = g;
        return g;
    }

    sealed record Src(Color Albedo, Texture2D? Tex, float Scissor, bool VertexColor, Texture2D? Normal, BaseMaterial3D.TransparencyEnum Mode, bool TwoSided);

    // the look of a ToLambert material (a StandardMaterial3D or one of ModelMaterials' Lambert shaders)
    static Src? Read(Material m)
    {
        switch (m)
        {
            case StandardMaterial3D sm:
                return new Src(sm.AlbedoColor, sm.AlbedoTexture, sm.AlphaScissorThreshold, sm.VertexColorUseAsAlbedo,
                    sm.NormalEnabled ? sm.NormalTexture : null, sm.Transparency, sm.CullMode == BaseMaterial3D.CullModeEnum.Disabled);
            case ShaderMaterial sh when sh.Shader != null && sh.Shader.Code.Contains("texture_albedo"):
            {
                var code = sh.Shader.Code;
                var mode = code.Contains("ALPHA_SCISSOR_THRESHOLD") ? BaseMaterial3D.TransparencyEnum.AlphaScissor
                    : code.Contains("ALPHA =") ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled;
                Texture2D? T(string n) => sh.GetShaderParameter(n).VariantType == Variant.Type.Object ? sh.GetShaderParameter(n).As<Texture2D>() : null;
                var alb = sh.GetShaderParameter("albedo");
                var sc = sh.GetShaderParameter("alpha_scissor_threshold");
                return new Src(alb.VariantType == Variant.Type.Color ? alb.AsColor() : Colors.White, T("texture_albedo"),
                    sc.VariantType == Variant.Type.Float ? sc.AsSingle() : 0.5f, sh.GetShaderParameter("use_vertex_color").AsBool(),
                    sh.GetShaderParameter("use_normal_map").AsBool() ? T("texture_normal") : null, mode, code.Contains("cull_disabled"));
            }
            default: return null;
        }
    }

    static Material GreenGlow(Material m)
    {
        if (m is not ShaderMaterial g) return m;
        s_glow ??= new Shader { Code = GlowCode };
        var c = new ShaderMaterial { Shader = s_glow, ResourceName = g.ResourceName + "#green", RenderPriority = g.RenderPriority };
        c.SetShaderParameter("map", g.GetShaderParameter("map"));
        c.SetShaderParameter("use_alpha", g.GetShaderParameter("use_alpha"));
        if (ModelMaterials.Tag(m) is { } tag) ModelMaterials.SetTag(c, tag);
        return c;
    }

    static Shader ShaderFor(LookKind kind, bool twoSided, BaseMaterial3D.TransparencyEnum mode)
    {
        if (mode is BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass or BaseMaterial3D.TransparencyEnum.AlphaHash) mode = BaseMaterial3D.TransparencyEnum.Alpha;
        // (the undead draw with the character shader: their uniforms switch everything but the eyes off)
        if (kind == LookKind.Undead) kind = LookKind.Character;
        var key = (kind, twoSided, mode);
        if (!s_shaders.TryGetValue(key, out var s))
        {
            long t0 = PwRemake.View.PerfProbe.P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            s_shaders[key] = s = new Shader { Code = Code(kind, twoSided, mode) };
            PwRemake.View.PerfProbe.P?.Add("inf:new shader", System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }
        return s;
    }

    // ------------------------------------------------------------------------------------------- shader code

    static string F(double v) => v.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);
    static string V(double r, double g, double b) => $"vec3({F(r)}, {F(g)}, {F(b)})";

    /// <summary>the green of the infection's light (sRGB): veins, eyes, specks, the blight's glow</summary>
    public static readonly Color GLOW = new(0.42f, 1.0f, 0.16f);

    /// <summary>GLSL: value noise (in3), three octaves (ifbm) - hashed from the position, no texture</summary>
    public const string NoiseGlsl = @"
float ih(vec3 p) {
	p = fract(p * 0.3183099 + vec3(0.1, 0.2, 0.3));
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float in3(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(ih(i), ih(i + vec3(1.0, 0.0, 0.0)), f.x), mix(ih(i + vec3(0.0, 1.0, 0.0)), ih(i + vec3(1.0, 1.0, 0.0)), f.x), f.y),
		mix(mix(ih(i + vec3(0.0, 0.0, 1.0)), ih(i + vec3(1.0, 0.0, 1.0)), f.x), mix(ih(i + vec3(0.0, 1.0, 1.0)), ih(i + vec3(1.0, 1.0, 1.0)), f.x), f.y), f.z);
}
float ifbm(vec3 p) { return 0.5 * in3(p) + 0.3 * in3(p * 2.03 + 7.1) + 0.2 * in3(p * 4.01 + 3.7); }
";

    /// <summary>GLSL: pw_green(c) - a fire's or light's colour turned to the infection's green, its brightness and its
    /// white core kept (a green already: unchanged)</summary>
    public const string GreenGlsl = @"
vec3 pw_green(vec3 c) {
	float m = max(c.r, max(c.g, c.b));
	float w = min(c.r, min(c.g, c.b)) / max(m, 0.0001);
	return m * mix(vec3(0.3, 1.0, 0.14), vec3(0.62, 1.0, 0.48), w * w * 0.8);
}
";

    static readonly string GlowCode = @"
shader_type spatial;
// InfectedLook: a glow (GSF material flag 0x4: ModelMaterials' GlowShader) of an infected model, turned green
render_mode blend_add, unshaded, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;
uniform sampler2D map : filter_linear_mipmap;
uniform bool use_alpha = false;
" + GammaBlend.Glsl + GreenGlsl + @"
void fragment() {
	vec4 t = texture(map, UV);
	ALBEDO = pw_gamma_add(pw_green(t.rgb * (use_alpha ? t.a : 1.0)));
	ALPHA = 1.0;
}
";

    static string Code(LookKind kind, bool twoSided, BaseMaterial3D.TransparencyEnum mode)
    {
        bool blend = mode == BaseMaterial3D.TransparencyEnum.Alpha, scissor = mode == BaseMaterial3D.TransparencyEnum.AlphaScissor;
        bool body = kind is LookKind.Animal or LookKind.Character;
        string rm = "blend_mix, " + (twoSided ? "cull_disabled" : "cull_back") + ", diffuse_lambert, specular_disabled, depth_draw_opaque";
        string ipos = kind switch
        {
            // the bind position in the model's frame (InfectedLook.RestMesh: CUSTOM0, GSF: z up): the wounds move with the bones
            LookKind.Animal or LookKind.Character => "\tipos = CUSTOM0.xyz;\n",
            // a vehicle: its bind position too, turned to y up (the building look climbs along y) - it moves and turns
            LookKind.Vehicle => "\tipos = vec3(CUSTOM0.x, CUSTOM0.z, -CUSTOM0.y);\n",
            // an item (a weapon in a hand): its own vertices - the rot stays on it as it swings
            LookKind.Item => "\tipos = VERTEX;\n",
            // world position relative to the mesh's origin - the model's origin for the merged static meshes (ModelLibrary
            // MergeStatic: in the model's frame); y up, the ground at 0
            _ => "\tipos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz - MODEL_MATRIX[3].xyz;\n",
        };
        return @"
shader_type spatial;
// InfectedLook: ToLambert's material (ModelMaterials' Lambert shader) of an infected " + kind.ToString().ToLowerInvariant() + @"
render_mode " + rm + @";
uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D texture_albedo : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float alpha_scissor_threshold = 0.5;
uniform bool use_vertex_color = false;
uniform bool use_normal_map = false;
uniform sampler2D texture_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float infect_rot = 0.0;    // 0..1 rotten (InfectionStage.Rot)
uniform float infect_party = 0.0;  // 1: a party colour material (GSF 0x1000) - rots half as much
uniform float infect_veins = 0.0;  // 0..1 glowing veins
" + (body ? @"
uniform float infect_skin = 0.0;   // 0..1 pale grey-green skin
uniform float infect_bruise = 0.0; // 0..1 dark bruises
uniform vec4 wound_c[" + InfectedLookData.MAX_WOUNDS + @"]; // centre (bind pose, GSF frame), w: WoundKind
uniform vec4 wound_r[" + InfectedLookData.MAX_WOUNDS + @"]; // radii, w: the wound's index (its noise)
uniform int wound_n = 0;
uniform vec4 eyes = vec4(0.0);     // one eye (x, y, z, radius); radius 0: none
uniform vec3 eyes2 = vec3(0.0);    // the other eye
uniform bool thin = false;         // a double-sided material (a frill, a wing): its back faces are no inside of the body
" : @"
uniform float infect_slime = 0.0;  // m: how high the slime climbs from the model's origin
uniform float infect_moss = 0.0;   // 0..1 moss on upward faces
uniform float vein_scale = 1.0;    // x the veins' density (BuildingTune.VeinScale)
") + GammaBlend.Glsl + NoiseGlsl + @"
varying vec3 ipos;
varying flat float mirrored;
void vertex() {
	mirrored = determinant(mat3(MODEL_MATRIX)) < 0.0 ? 1.0 : 0.0;
" + ipos + @"
}
void fragment() {
	" + (twoSided ? "if (mirrored > 0.5) NORMAL = -NORMAL;" : "") + @"
	vec4 a = albedo * texture(texture_albedo, UV);
	if (use_vertex_color) a *= COLOR;
	const vec3 LUM = vec3(0.2126, 0.7152, 0.0722);
	vec3 glow = pw_mix_out(" + V(GLOW.R, GLOW.G, GLOW.B) + @");
	vec3 em = vec3(0.0);
	float lum = dot(a.rgb, LUM);
" + (kind == LookKind.Animal ? AnimalFragment(twoSided) : kind == LookKind.Character ? CharacterFragment(twoSided) : BuildingFragment) + @"
	ALBEDO = a.rgb;
	METALLIC = 0.0;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	if (use_normal_map) NORMAL_MAP = texture(texture_normal, UV).rgb;
	EMISSION = em;
" + (blend || scissor ? "\tALPHA = a.a;\n" : "") + (scissor ? "\tALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;\n" : "") + "}\n";
    }

    // the building look: rot, slime from the ground, moss on the upward faces, glowing veins
    static readonly string BuildingFragment = @"
	vec3 wp = ipos;
	vec3 wn = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	float n1 = ifbm(wp * 0.33);
	float n2 = ifbm(wp * 1.25 + 11.0);
	// rot: towards the undead's grey-olive (spec_hu_undead_killer.png), darker rotten patches; party parts half
	float rk = infect_rot * (1.0 - 0.5 * infect_party);
	vec3 rotc = lum * " + V(0.95, 1.0, 0.72) + @" * mix(0.82, 0.42, smoothstep(0.5, 0.78, n1));
	a.rgb = mix(a.rgb, rotc, rk);
	// slime from the ground: its edge broken by noise, wet highlights
	float edge = infect_slime * (0.65 + 0.7 * n1);
	float s = infect_slime > 0.0 ? 1.0 - smoothstep(edge - 0.35 - 0.12 * infect_slime, edge, wp.y + (n2 - 0.5) * 0.9) : 0.0;
	vec3 slime = mix(pw_mix_out(" + V(0.07, 0.10, 0.035) + @"), pw_mix_out(" + V(0.20, 0.30, 0.06) + @"), smoothstep(0.32, 0.72, n2));
	slime = mix(slime, pw_mix_out(" + V(0.42, 0.58, 0.10) + @"), smoothstep(0.74, 0.9, ifbm(wp * 2.7)) * 0.75);
	a.rgb = mix(a.rgb, slime, s * 0.96);
	// moss on roofs and ledges above it
	float up = smoothstep(0.42, 0.8, wn.y);
	float moss = up * infect_moss * smoothstep(0.42, 0.62, n2 + (n1 - 0.5) * 0.5);
	a.rgb = mix(a.rgb, pw_mix_out(" + V(0.17, 0.23, 0.07) + @") * (0.7 + 0.6 * n2), moss * (1.0 - s));
	// glowing veins / cracks (the line where a noise crosses its middle), thicker and denser low on the walls
	// (the line's width: a few pixels at any zoom - fwidth - and at least a few centimetres close up)
	float vn = in3(wp * 0.62 * vein_scale + 3.3) * 0.62 + in3(wp * 1.7 * vein_scale) * 0.38;
	float low = 1.0 - smoothstep(edge, edge + 5.0 + 4.0 * infect_veins, wp.y);
	float vw = max(fwidth(vn) * (0.8 + 0.5 * low), 0.004);
	float vline = 1.0 - smoothstep(vw * 0.35, vw * 1.1, abs(vn - 0.5));
	float vmask = clamp(infect_veins * (0.1 + 0.9 * low) * smoothstep(0.5, 0.66, ifbm(wp * 0.19 + 5.0) + infect_veins * 0.12), 0.0, 1.0);
	float veins = vline * vmask;
	float pulse = 0.62 + 0.38 * sin(TIME * 2.1 + n1 * 9.0);
	a.rgb = mix(a.rgb, pw_mix_out(" + V(0.16, 0.34, 0.05) + @"), veins);
	em += glow * veins * pulse * 1.0;
	// glowing specks in the slime
	float sp = smoothstep(0.92, 0.975, in3(wp * 5.5)) * s;
	em += glow * sp * (0.55 + 0.45 * sin(TIME * 3.0 + wp.x * 5.0 + wp.z * 3.0)) * 1.6 * infect_veins;
";

    // the animal look: pale skin, bruises, torn wounds with bones, blood, green eyes
    static string AnimalFragment(bool twoSided) => @"
	vec3 rp = ipos;
	// the side of the surface seen (a mirrored model: Godot's FRONT_FACING is the other way round)
	bool front = FRONT_FACING != (mirrored > 0.5);
	// pale grey-green skin (the undead's / the zombie's), dark bruises
	float sk = infect_skin * (1.0 - 0.5 * infect_party);
	vec3 pale = min(0.08 + lum * 1.6, 0.5) * " + V(0.80, 0.92, 0.62) + @";
	a.rgb = mix(a.rgb, pale, sk);
	float bn = ifbm(rp * 1.15 + 2.0);
	float br = smoothstep(0.56, 0.7, bn) * infect_bruise;
	a.rgb = mix(a.rgb, pw_mix_out(" + V(0.22, 0.16, 0.20) + @") * (0.6 + lum), br * 0.85);
	// faint green veins on the skin
	float vn = in3(rp * 2.2 + 3.3);
	float vf = max(fwidth(vn), 0.004);
	float vl = (1.0 - smoothstep(vf * 0.5, vf * 1.5, abs(vn - 0.5))) * infect_veins;
	// the wounds: the nearest ellipsoid (its edge torn by noise)
	float wd = 9.0;
	vec3 wq = vec3(0.0);
	float kind = 0.0;
	for (int i = 0; i < wound_n; i++) {
		vec3 q = (rp - wound_c[i].xyz) / wound_r[i].xyz;
		float d = length(q) + (in3(rp / wound_r[i].xyz * 1.6 + wound_r[i].w * 13.0) - 0.5) * 0.4;
		if (d < wd) { wd = d; wq = q; kind = wound_c[i].w; }
		// blood running down from it: streaks across its width, of different lengths
		float st = in3(vec3(q.y * 4.5 + wound_r[i].w * 9.0, q.x * 1.5, 0.5));
		float len = 0.9 + 1.9 * in3(vec3(q.y * 2.2, wound_r[i].w * 5.0, 1.5));
		float drip = step(0.55, st) * step(-len - 0.6, q.z) * step(q.z, -0.6) * step(length(q.xy), 1.05);
		a.rgb = mix(a.rgb, pw_mix_out(" + V(0.30, 0.02, 0.02) + @"), drip * (0.65 + 0.35 * smoothstep(-len - 0.6, -0.6, q.z)));
	}
	// green veins spreading from the wounds
	vl *= (1.0 - smoothstep(1.3, 2.4, wd)) * smoothstep(0.5, 0.65, in3(rp * 0.9 + 5.0));
	a.rgb = mix(a.rgb, pw_mix_out(" + V(0.22, 0.38, 0.10) + @"), vl * 0.85);
	em += glow * vl * 0.4;
	if (wd < 1.0) {
		// inside: bones (by the wound's kind) and a torn edge of raw flesh; the rest is cut away
		bool bone = false;
		if (kind < 0.5) bone = abs(fract(wq.y * 2.4) - 0.5) > 0.29 && wd < 0.92;                               // ribs
		else if (kind < 1.5) bone = abs(wq.x) < 0.2 || (abs(wq.x) < 0.55 && abs(fract(wq.y * 3.0) - 0.5) > 0.34); // spine
		else if (kind < 2.5) bone = length(wq.xy) < 0.3;                                                       // a leg bone
		if (bone) {
			float sh = 0.75 + 0.25 * in3(rp * 9.0);
			a.rgb = pw_mix_out(" + V(0.86, 0.80, 0.64) + @") * sh;
		} else if (wd > 0.8) {
			a.rgb = pw_mix_out(" + V(0.58, 0.07, 0.05) + @") * (0.8 + 0.4 * in3(rp * 11.0));
		} else discard;
	} else if (wd < 1.5) {
		// a wet dark-red rim
		float k = 1.0 - smoothstep(1.05, 1.5, wd);
		a.rgb = mix(a.rgb, pw_mix_out(" + V(0.26, 0.015, 0.015) + @"), k * 0.92);
	}
	// the inside of the body seen through a hole: dark red flesh (not the back of a thin part)
	if (!front && !thin) {
		a.rgb = pw_mix_out(" + V(0.30, 0.03, 0.03) + @") * (0.7 + 0.5 * in3(rp * 6.0));
		em += pw_mix_out(" + V(0.08, 0.0, 0.0) + @");
	}
	// green glowing eyes
	if (eyes.w > 0.0) {
		float e = min(length(rp - eyes.xyz), length(rp - eyes2)) / eyes.w;
		if (e < 1.0) { a.rgb = glow; em += glow * (2.5 * (1.0 - e * e) + 0.6); }
	}
";

    // the character look: pale grey-green skin, rotting clothes, bruises, torn cloth over painted wounds (a body 2 m
    // tall is too thin to cut through: the wound is drawn on the surface - raw flesh, ribs, a bone - with a ragged dark
    // edge of torn cloth and blood running down), green glowing eyes. The undead: the uniforms leave only the eyes.
    static string CharacterFragment(bool twoSided) => @"
	vec3 rp = ipos;
	// skin: the textures' skin tones (red above green above blue, moderately saturated, not dark) - the rest is cloth,
	// leather, metal
	float mx = max(a.r, max(a.g, a.b)), mn = min(a.r, min(a.g, a.b));
	float sat = (mx - mn) / max(mx, 0.001);
	float skinm = smoothstep(0.015, 0.07, a.r - a.g) * smoothstep(-0.01, 0.05, a.g - a.b) * smoothstep(0.1, 0.22, sat)
		* (1.0 - smoothstep(0.65, 0.85, sat)) * smoothstep(0.16, 0.32, mx);
	// clothes rot: grey-olive, mottled stains (party colours half)
	float n1 = ifbm(rp * 2.4 + 1.0);
	float rk = infect_rot * (1.0 - 0.5 * infect_party) * (1.0 - skinm);
	vec3 rotc = lum * " + V(0.92, 1.0, 0.70) + @" * mix(0.95, 0.45, smoothstep(0.45, 0.78, n1));
	a.rgb = mix(a.rgb, rotc, rk * 0.85);
	// pale grey-green skin (the undead's, Schliemann's zombie's)
	vec3 pale = min(0.12 + lum * 1.45, 0.56) * " + V(0.78, 0.92, 0.60) + @";
	a.rgb = mix(a.rgb, pale, skinm * infect_skin);
	// dark bruises
	float br = smoothstep(0.58, 0.72, ifbm(rp * 3.2 + 2.0)) * infect_bruise;
	a.rgb = mix(a.rgb, pw_mix_out(" + V(0.22, 0.15, 0.20) + @") * (0.6 + lum), br * (0.45 + 0.4 * skinm));
	// faint green veins on the skin
	float vn = in3(rp * 4.5 + 3.3);
	float vf = max(fwidth(vn), 0.004);
	float vl = (1.0 - smoothstep(vf * 0.5, vf * 1.5, abs(vn - 0.5))) * infect_veins * (0.3 + 0.7 * skinm);
	// the wounds: the nearest ellipsoid, its edge torn by noise
	float wd = 9.0;
	vec3 wq = vec3(0.0);
	float kind = 0.0;
	for (int i = 0; i < wound_n; i++) {
		vec3 q = (rp - wound_c[i].xyz) / wound_r[i].xyz;
		float d = length(q) + (in3(rp / wound_r[i].xyz * 2.6 + wound_r[i].w * 13.0) - 0.5) * 0.55 + (in3(rp / wound_r[i].xyz * 7.0 + wound_r[i].w * 5.0) - 0.5) * 0.22;
		if (d < wd) { wd = d; wq = q; kind = wound_c[i].w; }
		// blood running down the clothes from it
		float st = in3(vec3(q.x * 4.0 + q.y * 3.0 + wound_r[i].w * 9.0, 0.5, 0.5));
		float len = 0.8 + 1.6 * in3(vec3(q.x * 2.0 + q.y * 1.5, wound_r[i].w * 5.0, 1.5));
		float drip = step(0.55, st) * step(-len - 0.7, q.z) * step(q.z, -0.7) * step(length(q.xy), 1.0);
		a.rgb = mix(a.rgb, pw_mix_out(" + V(0.30, 0.02, 0.02) + @"), drip * 0.8);
	}
	// (the veins on the skin and round the wounds - not over the clothes)
	vl *= max(skinm, 1.0 - smoothstep(1.0, 1.9, wd));
	a.rgb = mix(a.rgb, pw_mix_out(" + V(0.22, 0.38, 0.10) + @"), vl * 0.75);
	em += glow * vl * 0.25;
	if (wd < 1.0) {
		// the wound: dark raw flesh, wet, blackened deep inside; a few ribs / the spine / a bone showing
		float f = in3(rp * 16.0 + 4.0);
		a.rgb = mix(pw_mix_out(" + V(0.46, 0.05, 0.035) + @"), pw_mix_out(" + V(0.13, 0.012, 0.01) + @"), smoothstep(0.35, 0.8, f) * 0.8 + smoothstep(0.8, 0.25, wd) * 0.3);
		a.rgb += pw_mix_out(" + V(0.25, 0.06, 0.04) + @") * smoothstep(0.86, 0.96, in3(rp * 30.0)) * 0.6;
		bool bone = false;
		if (kind < 0.5) bone = abs(fract(wq.z * 1.7 + 0.25 * wq.x * wq.x + 0.3) - 0.5) > 0.37 && wd < 0.78 && abs(wq.x) < 0.8;   // ribs (curved, across the height)
		else if (kind < 1.5) bone = abs(wq.x) < 0.14 && abs(fract(wq.z * 3.0) - 0.5) < 0.38 && wd < 0.85;                    // vertebrae
		else if (kind < 2.5) bone = length(wq.xy) < 0.26 && wd < 0.9;                                                            // a bone along the limb
		if (bone) a.rgb = pw_mix_out(" + V(0.78, 0.71, 0.56) + @") * (0.7 + 0.3 * in3(rp * 22.0));
	} else if (wd < 1.32) {
		// torn cloth: a ragged dark edge with frayed threads, wet with blood next to the wound
		float fray = in3(rp * 45.0);
		float k = 1.0 - smoothstep(1.0, 1.32, wd + (fray - 0.5) * 0.2);
		a.rgb = mix(a.rgb, mix(pw_mix_out(" + V(0.24, 0.025, 0.02) + @"), pw_mix_out(" + V(0.06, 0.045, 0.03) + @"), smoothstep(1.0, 1.14, wd)), k * 0.95);
	}
	" + (twoSided ? @"// the inside of a sleeve or cloak seen through: dark
	if (!(FRONT_FACING != (mirrored > 0.5))) a.rgb *= 0.55;
	" : "") + @"// green glowing eyes
	if (eyes.w > 0.0) {
		float e = min(length(rp - eyes.xyz), length(rp - eyes2)) / eyes.w;
		if (e < 1.0) { a.rgb = glow; em += glow * (2.2 * (1.0 - e * e) + 0.6); }
	}
";

    // ------------------------------------------------------------------------------------------- the bind pose

    static readonly Dictionary<(Mesh, Transform3D), ArrayMesh?> s_rest = new();

    /// <summary>
    /// A copy of a mesh whose vertices carry their bind position in the model's frame in CUSTOM0 (the animal shader's
    /// wounds are placed there): toModel takes the mesh's vertices to the model's (GSF) frame. One copy per mesh, shared
    /// by every instance. null: a mesh it cannot copy (blend shapes) - drawn without wounds.
    /// </summary>
    public static ArrayMesh? RestMesh(Mesh mesh, Transform3D toModel)
    {
        var key = (mesh, toModel);
        if (s_rest.TryGetValue(key, out var r)) return r;
        r = null;
        if (mesh is ArrayMesh src && src.GetBlendShapeCount() == 0)
        {
            r = new ArrayMesh { ResourceName = src.ResourceName + "#rest" };
            for (int s = 0; s < src.GetSurfaceCount(); s++)
            {
                var arr = src.SurfaceGetArrays(s);
                var v = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var c = new float[v.Length * 4];
                for (int i = 0; i < v.Length; i++) { var p = toModel * v[i]; c[i * 4] = p.X; c[i * 4 + 1] = p.Y; c[i * 4 + 2] = p.Z; c[i * 4 + 3] = 1; }
                arr[(int)Mesh.ArrayType.Custom0] = c;
                ulong fmt = (ulong)src.SurfaceGetFormat(s);
                var flags = (Mesh.ArrayFormat)((fmt & (ulong)Mesh.ArrayFormat.FlagUse8BoneWeights)
                    | ((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
                r.AddSurfaceFromArrays(src.SurfaceGetPrimitiveType(s), arr, null, null, flags);
                r.SurfaceSetMaterial(s, src.SurfaceGetMaterial(s));
            }
        }
        s_rest[key] = r;
        return r;
    }

    // ------------------------------------------------------------------------------------------- the original's effects

    static readonly Dictionary<ModelLibrary, (Node3D? node, PartRef? part)> s_flies = new();

    /// <summary>
    /// The original's flies: the "meat flies" emitter of the Norse lumberjack cottage (hu_lumberjack_cottage node
    /// particles_aje_Schlachthaus_lecker_Fleischfliegen_01: six dark flies of particle_01.png buzzing on a sphere of
    /// 0.7 m). null when the data lacks the model.
    /// </summary>
    public static PartRef? FliesEmitter(ModelLibrary lib)
    {
        if (s_flies.TryGetValue(lib, out var f)) return f.part;
        PartRef? found = null;
        var t = lib.Template("hu_lumberjack_cottage", null, true);
        if (t != null)
            foreach (var p in t.Emitters)
                if (p.Emitter != null && p.Path.ToString().Contains("Fleischfliegen", StringComparison.OrdinalIgnoreCase)) { found = p; break; }
        s_flies[lib] = (null, found);
        return found;
    }
}
