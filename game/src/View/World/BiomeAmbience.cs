// The biomes around the camera (docs/BIOMES.md): the light, fog and sky blend by the biome mix at the camera target
// (WorldRenderer.SetBiomeLook; the fog of war pass and the particles follow the fog), the weather's snow and rain are
// scaled by the biomes' factors, and the ambience is the template of the biome that dominates there (with a margin, so
// it does not flip at the border; Atmo cross-fades). A map without a biome layer: nothing happens here at all.
using System;
using System.Linq;
using Godot;
using PwRemake.Sim.Maps;
using PwRemake.View.Fx;

namespace PwRemake.View.World;

public sealed class BiomeAmbience
{
    readonly WorldView _v;
    readonly WorldRenderer _r;
    readonly BiomeLook?[] _looks;
    double[] _mix = Array.Empty<double>();
    /// <summary>the biome whose ambience plays (index into the view's Layers.Biomes); -1 before the first update</summary>
    public int Dominant { get; private set; } = -1;
    /// <summary>the weather's factors at the camera</summary>
    public double Snow { get; private set; } = 1;
    public double Rain { get; private set; } = 1;
    public bool Active => !_v.Layers.Single;
    /// <summary>the mix at the camera (tests, the map viewer's info line)</summary>
    public double[] Mix => _mix;

    public BiomeAmbience(WorldView v, WorldRenderer r)
    {
        _v = v; _r = r;
        _looks = v.Layers.Biomes.Select(b => b.Look).ToArray();
    }

    /// <summary>every frame: the camera target</summary>
    public void Update(Vector3 target)
    {
        if (!Active) return;
        // the point and four around it at 24 m: a soft mix even where the painted border is sharp
        var bs = _v.Layers.Biomes;
        var m = new double[bs.Count];
        foreach (var (dx, dz) in new[] { (0.0, 0.0), (24.0, 0.0), (-24.0, 0.0), (0.0, 24.0), (0.0, -24.0) })
        {
            var w = _v.BiomeWeights(target.X + dx, target.Z + dz);
            for (int b = 0; b < m.Length && b < w.Length; b++) m[b] += w[b] / 5;
        }
        _mix = m;
        if (_r.SetBiomeLook(BiomeLook.Mix(_looks, m)))
        {
            _v.Fog.SetSceneFog(_r.FogColor, _r.FogBegin, _r.FogFinish);
            _v.Particles.FogStart = (float)_r.FogBegin; _v.Particles.FogEnd = (float)_r.FogFinish;
        }
        double snow = 0, rain = 0;
        for (int b = 0; b < m.Length; b++) { snow += m[b] * bs[b].Snow; rain += m[b] * bs[b].Rain; }
        Snow = snow; Rain = rain;
        int best = 0;
        for (int b = 1; b < m.Length; b++) if (m[b] > m[best]) best = b;
        if (Dominant < 0 || Dominant >= m.Length || m[best] > m[Dominant] + 0.2) Dominant = best;
    }

    /// <summary>the map's weather as it falls here (the biomes' snow and rain factors); null: none</summary>
    public WeatherState? Weather(WeatherState? w)
    {
        if (w is not { } s || !Active) return w;
        double k = s.Type == 1 ? Snow : s.Type == 2 ? Rain : 1;
        return s.Intensity * k < 0.05 ? null : s with { Intensity = s.Intensity * k };
    }

    /// <summary>the ambience template now: the dominant biome's (the map's own material_atmos for the biome of the map's
    /// setting); mapAtmos without biomes</summary>
    public string? Ambience(string? mapAtmos)
    {
        if (!Active || Dominant < 0) return mapAtmos;
        var b = _v.Layers.Biomes[Dominant];
        if (b.Original && string.Equals(b.Id, _v.Source.Setting, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(mapAtmos)) return mapAtmos;
        return b.Ambience == null ? "No Atmos" : b.Ambience + ".txt";
    }

    /// <summary>the ambience's ground material (the map's 0..7, 8 forest) as the dominant biome's material</summary>
    public int SoundMat(int mat) => !Active || Dominant < 0 || mat < 0 || mat > 7 ? mat : _v.Layers.Remap[Dominant][mat];
}
