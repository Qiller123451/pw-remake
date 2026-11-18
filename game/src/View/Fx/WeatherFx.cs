// The weather of a level (the client's FXWeather, fed by CWeatherMgr: the world's weather program, MapSource.Weather /
// landmarks.js weatherNow): snow (type 1) or rain (type 2), with an intensity (how many flakes or drops: "Very Light
// Snow" is 2) and a wind (direction in degrees counter-clockwise from north - WeatherMgr: east 270, west 90 -
// strength 0 .. 120).
//
// One cloud in a box around the point the camera looks at; a flake that leaves the box comes in again on the other
// side, so the box follows the camera without the weather jumping. Snow: soft round flakes that drift; rain: streaks
// along the fall. The options' "Weather" switches it off (CClientWrap.SetGfxComponentLOD 7).
//
// Godot: snow = a MultiMesh of camera-facing quads with a round soft texture, rain = a MultiMesh of line segments
// (each instance's y axis is the streak); both refilled on the CPU every frame like the JS's position buffers.
// Ported from remake/src/engine/weather.js (pw-exporter 98c7703).
using System;
using Godot;

namespace PwRemake.View.Fx;

/// <summary>the weather now (World.weatherNow): type 1 snow / 2 rain, intensity, wind direction (deg) and strength</summary>
public readonly record struct WeatherState(int Type, double Intensity, double Wind, double Strength);

public partial class WeatherFx : Node3D
{
    const float BOX = 110, HEIGHT = 70;
    const int MAX = 9000, PER_INTENSITY = 280;

    public bool Enabled = true;
    readonly float[] _pos = new float[MAX * 3];
    readonly float[] _seed = new float[MAX];
    MultiMeshInstance3D _snow = null!, _rain = null!;
    ShaderMaterial _snowMat = null!, _rainMat = null!;
    float[] _snowBuf = Array.Empty<float>(), _rainBuf = Array.Empty<float>();
    double _t, _amount;      // amount: fades in and out (0 .. 1)
    int _kind;
    public int Drawn { get; private set; }

    public static WeatherFx Create()
    {
        var w = new WeatherFx { Name = "Weather" };
        var rnd = new Random(7);
        for (int i = 0; i < MAX; i++)
        {
            w._pos[i * 3] = (float)((rnd.NextDouble() - 0.5) * BOX); w._pos[i * 3 + 1] = (float)(rnd.NextDouble() * HEIGHT);
            w._pos[i * 3 + 2] = (float)((rnd.NextDouble() - 0.5) * BOX); w._seed[i] = (float)rnd.NextDouble();
        }
        // snow: soft round flakes (PointsMaterial size 0.85, sizeAttenuation)
        w._snowMat = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = @"shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, skip_vertex_transform;
uniform sampler2D map : source_color, filter_linear;
uniform float opacity = 0.9;
uniform float size = 0.31;     // three.js PointsMaterial size 0.85 with size attenuation = 0.85 x tan(fov / 2) m
void vertex() {
    vec4 mv = VIEW_MATRIX * vec4((MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz, 1.0);
    mv.xy += VERTEX.xy * size;
    VERTEX = mv.xyz;
}
void fragment() { vec4 t = texture(map, UV); ALBEDO = t.rgb; ALPHA = t.a * opacity; }
",
            },
            RenderPriority = 12,
        };
        w._snowMat.SetShaderParameter("map", FlakeTexture());
        // rain: line segments (top and bottom of each drop)
        w._rainMat = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = @"shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform vec3 color : source_color;
uniform float opacity = 0.45;
void fragment() { ALBEDO = color; ALPHA = opacity; }
",
            },
            RenderPriority = 12,
        };
        w._rainMat.SetShaderParameter("color", Color.Color8(0xbf, 0xd0, 0xe0));
        var line = new ArrayMesh();
        var la = new Godot.Collections.Array();
        la.Resize((int)Mesh.ArrayType.Max);
        la[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Up };
        line.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, la);
        w._snow = new MultiMeshInstance3D { Name = "Snow", Visible = false, MaterialOverride = w._snowMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        w._snow.Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new QuadMesh { Size = Vector2.One }, InstanceCount = MAX, VisibleInstanceCount = 0 };
        w._rain = new MultiMeshInstance3D { Name = "Rain", Visible = false, MaterialOverride = w._rainMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        w._rain.Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = line, InstanceCount = MAX, VisibleInstanceCount = 0 };
        w._snowBuf = new float[MAX * 12];
        w._rainBuf = new float[MAX * 12];
        w.AddChild(w._snow);
        w.AddChild(w._rain);
        return w;
    }

    static ImageTexture FlakeTexture()
    {
        // a radial gradient: white, alpha 1 at the centre, 0.75 at 45 %, 0 at the edge
        var img = Image.CreateEmpty(32, 32, false, Image.Format.Rgba8);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                double r = Math.Sqrt((x + 0.5 - 16) * (x + 0.5 - 16) + (y + 0.5 - 16) * (y + 0.5 - 16)) / 16;
                double a = r < 0.45 ? 1 - 0.25 * r / 0.45 : r < 1 ? 0.75 * (1 - (r - 0.45) / 0.55) : 0;
                img.SetPixel(x, y, new Color(1, 1, 1, (float)a));
            }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>w: the weather now (null: none); at: the point the camera looks at</summary>
    public void Step(WeatherState? w, Vector3 at, double dt)
    {
        double want = Enabled && w is WeatherState ws && ws.Intensity > 0 ? 1 : 0;
        if (w is WeatherState wt && wt.Type != 0) _kind = wt.Type;
        _amount += (want - _amount) * Math.Min(1, dt);
        bool show = _amount > 0.01;
        _snow.Visible = show && _kind == 1;
        _rain.Visible = show && _kind == 2;
        if (!show) { Drawn = 0; return; }
        _t += dt;
        int n = Math.Min(MAX, (int)Math.Round((w?.Intensity ?? 4) * PER_INTENSITY * _amount));
        double rad = (w?.Wind ?? 0) * Math.PI / 180, str = (w?.Strength ?? 0) / 10;
        double wx = -Math.Sin(rad) * str, wz = -Math.Cos(rad) * str;        // north is -z
        bool snow = _kind == 1;
        double fall = snow ? 2.2 : 26;
        var P = _pos;
        float half = BOX / 2, cx = at.X, cy = at.Y - 6, cz = at.Z;
        for (int i = 0; i < n; i++)
        {
            int k = i * 3;
            double s = _seed[i];
            double x = P[k], y = P[k + 1], z = P[k + 2];
            if (snow) { x += (wx + Math.Sin(_t * 0.8 + s * 40) * 0.6) * dt; z += (wz + Math.Cos(_t * 0.6 + s * 30) * 0.6) * dt; }
            else { x += wx * dt; z += wz * dt; }
            y -= fall * (0.8 + 0.4 * s) * dt;
            if (x - cx > half) x -= BOX; else if (x - cx < -half) x += BOX;
            if (z - cz > half) z -= BOX; else if (z - cz < -half) z += BOX;
            if (y < cy) y += HEIGHT; else if (y - cy > HEIGHT) y -= HEIGHT;
            P[k] = (float)x; P[k + 1] = (float)y; P[k + 2] = (float)z;
        }
        var aabb = new Aabb(new Vector3(cx - half - 2, cy - 2, cz - half - 2), new Vector3(BOX + 4, HEIGHT + 6, BOX + 4));
        if (snow)
        {
            var B = _snowBuf;
            for (int i = 0; i < n; i++)
            {
                int o = i * 12, k = i * 3;
                B[o] = 1; B[o + 3] = P[k]; B[o + 5] = 1; B[o + 7] = P[k + 1]; B[o + 10] = 1; B[o + 11] = P[k + 2];
            }
            _snow.Multimesh.Buffer = B;
            _snow.Multimesh.VisibleInstanceCount = n;
            _snow.Multimesh.CustomAabb = aabb;
            _snowMat.SetShaderParameter("opacity", (float)(0.9 * _amount));
        }
        else
        {
            const double len = 1.4;
            float sx = (float)(wx / fall * len), sz = (float)(wz / fall * len);
            var B = _rainBuf;
            for (int i = 0; i < n; i++)
            {
                int o = i * 12, k = i * 3;
                // the instance's y axis is the streak: from the drop up and against the wind
                B[o] = 1; B[o + 1] = -sx; B[o + 2] = 0; B[o + 3] = P[k];
                B[o + 4] = 0; B[o + 5] = (float)len; B[o + 6] = 0; B[o + 7] = P[k + 1];
                B[o + 8] = 0; B[o + 9] = -sz; B[o + 10] = 1; B[o + 11] = P[k + 2];
            }
            _rain.Multimesh.Buffer = B;
            _rain.Multimesh.VisibleInstanceCount = n;
            _rain.Multimesh.CustomAabb = aabb;
            _rainMat.SetShaderParameter("opacity", (float)(0.45 * _amount));
        }
        Drawn = n;
    }
}
