// The blight on the ground round an infected building (docs/INFECTED_LOOK.md; Warcraft 3's undead blight): a dark
// green-black patch (an ellipse from the building's footprint: along a wall) with a torn edge, darker cracks and green glowing specks that twinkle. A disc mesh lying on the
// ground (InfectedLook.GroundHeight: the terrain's height under each vertex; flat without it), drawn alpha-blended over
// the terrain, no shadow. Not a Decal: the Compatibility renderer has none. Procedural - the original has no blight.
// Made and removed by ModelInstance.SetInfected.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.View.Materials;

namespace PwRemake.View.Fx;

public sealed partial class InfectionBlight : MeshInstance3D
{
    /// <summary>rings and segments of the disc</summary>
    const int RINGS = 14, SEGS = 40;
    /// <summary>m above the ground</summary>
    const float LIFT = 0.07f;
    static Shader? s_shader;

    /// <summary>the larger half width (m)</summary>
    public float Radius => Mathf.Max(RadiusX, RadiusZ);
    /// <summary>half widths along x and z (m): an ellipse - along a wall</summary>
    public float RadiusX { get; private set; }
    public float RadiusZ { get; private set; }

    public InfectionBlight(float radius, float strength) : this(radius, radius, strength) { }

    public InfectionBlight(float radiusX, float radiusZ, float strength)
    {
        Name = "InfectionBlight";
        RadiusX = radiusX; RadiusZ = radiusZ;
        CastShadow = ShadowCastingSetting.Off;
        s_shader ??= new Shader { Code = Code };
        var m = new ShaderMaterial { Shader = s_shader, RenderPriority = -1 };
        m.SetShaderParameter("radius", new Vector2(radiusX, radiusZ));
        m.SetShaderParameter("strength", Math.Clamp(strength, 0f, 1f));
        MaterialOverride = m;
        Mesh = Disc(radiusX, radiusZ, null);
    }

    public InfectionBlight() : this(10, 1) { }

    /// <summary>lays the disc on the ground under it (InfectedLook.GroundHeight) - once it is in the tree</summary>
    public void FitGround()
    {
        if (InfectedLook.GroundHeight is not { } h || !IsInsideTree()) return;
        var xf = GlobalTransform;
        Mesh = Disc(RadiusX, RadiusZ, p => { var w = xf * p; return h(w.X, w.Z) - xf.Origin.Y; });
    }

    public override void _Ready() => FitGround();

    /// <summary>the flat disc again (a warm-up copy deep under the ground: EntityViews.Infection.cs - laid on the ground
    /// it would reach up to the terrain over it)</summary>
    public void Unfit() => Mesh = Disc(RadiusX, RadiusZ, null);

    static ArrayMesh Disc(float rx, float rz, Func<Vector3, float>? ground)
    {
        var v = new List<Vector3>();
        var idx = new List<int>();
        Vector3 P(float x, float z) { var p = new Vector3(x, 0, z); p.Y = (ground?.Invoke(p) ?? 0) + LIFT; return p; }
        v.Add(P(0, 0));
        for (int i = 1; i <= RINGS; i++)
        {
            float k = (float)i / RINGS;
            for (int s = 0; s < SEGS; s++) { float a = s * Mathf.Tau / SEGS; v.Add(P(Mathf.Cos(a) * rx * k, Mathf.Sin(a) * rz * k)); }
        }
        int At(int ring, int s) => ring == 0 ? 0 : 1 + (ring - 1) * SEGS + ((s % SEGS) + SEGS) % SEGS;
        // (Godot's front faces wind clockwise seen from their side: from above here)
        for (int s = 0; s < SEGS; s++) { idx.Add(0); idx.Add(At(1, s)); idx.Add(At(1, s + 1)); }
        for (int i = 1; i < RINGS; i++)
            for (int s = 0; s < SEGS; s++)
            {
                int a = At(i, s), b = At(i, s + 1), c = At(i + 1, s), d = At(i + 1, s + 1);
                idx.Add(a); idx.Add(c); idx.Add(b);
                idx.Add(b); idx.Add(c); idx.Add(d);
            }
        var n = new Vector3[v.Count];
        Array.Fill(n, Vector3.Up);
        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arr[(int)Mesh.ArrayType.Normal] = n;
        arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var m = new ArrayMesh();
        m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        return m;
    }

    static readonly string Code = @"
shader_type spatial;
// InfectionBlight: the blight round an infected building (lit like the ground; green specks glow)
render_mode blend_mix, depth_draw_never, cull_back, diffuse_lambert, specular_disabled, shadows_disabled;
uniform vec2 radius = vec2(10.0); // half widths along x and z
uniform float strength = 1.0;
" + GammaBlend.Glsl + InfectedLook.NoiseGlsl + @"
varying vec3 wpos;
varying vec2 lp;
void vertex() {
	lp = VERTEX.xz / radius;
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
void fragment() {
	vec3 p = vec3(wpos.x, 0.0, wpos.z);
	float n = ifbm(p * 0.16);
	float r = length(lp);
	// a torn edge, fingers of blight reaching out
	float edge = 1.0 - smoothstep(0.5, 1.0, r + (n - 0.5) * 0.7 + (in3(p * 0.6) - 0.5) * 0.18);
	float tone = ifbm(p * 0.65 + 4.0);
	vec3 c = mix(pw_mix_out(vec3(0.035, 0.05, 0.02)), pw_mix_out(vec3(0.13, 0.19, 0.045)), smoothstep(0.35, 0.75, tone));
	// darker cracks
	float cv = in3(p * 0.9 + 2.0);
	float cw = max(fwidth(cv), 0.004);
	float cr = 1.0 - smoothstep(cw * 0.6, cw * 1.8, abs(cv - 0.5));
	c = mix(c, pw_mix_out(vec3(0.015, 0.02, 0.01)), cr * 0.8);
	// wet slime pools
	c = mix(c, pw_mix_out(vec3(0.25, 0.36, 0.06)), smoothstep(0.72, 0.85, ifbm(p * 0.35 + 9.0)) * 0.6);
	ALBEDO = c;
	ALPHA = clamp(edge * strength * 0.94, 0.0, 1.0);
	// green specks that twinkle, more in the middle
	float sp = smoothstep(0.93, 0.985, in3(vec3(wpos.x * 2.2, TIME * 0.35 + n * 3.0, wpos.z * 2.2)));
	EMISSION = pw_mix_out(vec3(0.42, 1.0, 0.16)) * sp * edge * (2.0 + 1.5 * (1.0 - r)) * strength;
	// glowing cracks near the centre
	EMISSION += pw_mix_out(vec3(0.42, 1.0, 0.16)) * cr * smoothstep(0.7, 0.15, r) * 0.3 * strength * (0.6 + 0.4 * sin(TIME * 1.7 + n * 8.0));
}
";
}
