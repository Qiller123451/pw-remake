// assets/manifest.json of the converted data (toolkit: remake/pipeline/build_assets.py): per model its archive, the
// model whose animations it borrows ("anims"), walk speeds, walk set, footprint, sound events and the loop marks of
// its clips. Ported from the manifest part of remake/src/engine/assets.js (initAssets, modelInfo)
// (pw-exporter 98c7703).
//
// (The simulation reads the same file for what it needs - walk speeds, footprints; this is the view's copy of the
// fields that drawing uses.)
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.View.Models;

public sealed class ModelInfo
{
    public string Name = "";
    /// <summary>The archive the model came from (hu_buildings, all_characters ...).</summary>
    public string Arch = "";
    /// <summary>The model whose clips this one plays (level 2-5 characters, seas_warrior ...), or null.</summary>
    public string? Anims;
    public bool Foliage;
    public int NAnims;
    /// <summary>A map object (landscape), loaded only when a map uses it.</summary>
    public bool Map;
    /// <summary>Clips made of start + loop + end: clip -> [t0, t1] of the loop part.</summary>
    public Dictionary<string, (double t0, double t1)>? Loops;
    /// <summary>Walk set: slot "1".."3" -> clip.</summary>
    public Dictionary<string, string> Walk = new();
    /// <summary>Ground speed of the walk clips (m/s).</summary>
    public Dictionary<string, double> Speeds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Animation sound events: clip -> [[time, wav, volume, maxHear, group], ...] (raw).</summary>
    public JsonObject? Sounds;
    public JsonObject Raw = new();
}

public sealed class ModelManifest
{
    static readonly PwRemake.Sim.Core.Logger L = Log.For("View.Models");
    readonly Dictionary<string, ModelInfo> _models = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ModelInfo> Models => _models;

    public static ModelManifest Load(AssetSource src)
    {
        var m = new ModelManifest();
        var path = src.Find("assets/manifest.json");
        if (path == null) return m;
        m.Add(path, false);
        // the original effect models the toolkit's export leaves out, converted by tools/prepare/effect_models.py
        // (docs/EFFECTS.md): the same format, in the configuration's overrides
        if (src.Find("assets/manifest_fx.json") is { } fx) m.Add(fx, true);
        // the sequel's own art the converted data lacked, put into the overrides by tools/prepare/sequel_art.py (docs/SEQUEL.md)
        if (src.Resolve("assets/manifest_sequel.json") is { } sq) m.Add(sq, true);
        L.Info($"manifest: {m._models.Count} models");
        return m;
    }

    void Add(string path, bool keep)
    {
        var root = Json.ParseFile(path);
        if (root?["models"] is not JsonObject models) { L.Warn($"{path}: no models"); return; }
        foreach (var (name, v) in models)
        {
            if (keep && _models.ContainsKey(name)) continue;      // (the main manifest's entry wins)
            if (v is not JsonObject o) continue;
            var info = new ModelInfo
            {
                Name = name.ToLowerInvariant(), Arch = o["arch"].Str(), Foliage = o["foliage"].Bool(), NAnims = o["nanims"].Int(),
                Map = o["map"].Bool(), Raw = o, Sounds = o["sounds"] as JsonObject,
            };
            var a = o["anims"].Str();
            info.Anims = string.IsNullOrEmpty(a) ? null : a;
            if (o["loops"] is JsonObject lp)
            {
                info.Loops = new Dictionary<string, (double, double)>();
                foreach (var (clip, r) in lp) if (r is JsonArray ra && ra.Count >= 2) info.Loops[clip] = (ra[0].Num(), ra[1].Num());
            }
            if (o["walk"] is JsonObject w) foreach (var (k, c) in w) info.Walk[k] = c.Str();
            if (o["speeds"] is JsonObject sp) foreach (var (k, s) in sp) info.Speeds[k] = s.Num();
            _models[info.Name] = info;
        }
    }

    public ModelInfo? Info(string? name) => name != null && _models.TryGetValue(name, out var i) ? i : null;
}
