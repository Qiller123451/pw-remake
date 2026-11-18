// Typed access to the model manifest (assets/manifest.json, the toolkit's pipeline/build_assets.py) - what the JS
// remake read through engine/assets.js (Assets.manifest.models[name], the template's `info`) - plus the values the JS
// simulation took from the loaded three.js template (ModelFileInfo: animation lengths, link positions, bounding box,
// the object kind). The simulation never loads a model; the view (game/src/View) does, from the same names.
//
// manifest.json: { models: { <name lower case>: {
//     arch      the archive the model came from (all_characters, hu_buildings ...)
//     anims     the model whose animations it uses (null: its own) - engine/assets.js retargets them
//     foliage   has billboard foliage            nanims  number of clips
//     map       a map object (loaded only when a map uses it)
//     pf        GSF pathfinder table rows [type, x, y, z, sx, sy, sz, attr] (type 1 = box; Building footprints)
//     speeds    clip -> metres per second of its walk (walk_1 .. walk_4, walk sets)
//     walk      walk set slots "1".."3" -> clip ("defn" sets: walk_<n>_new)
//     sounds    clip -> [[time, wav, volume, maxHear, group], ...] (GSF sound table; one time = alternatives)
//     loops     clip -> [t0, t1]: the loop part of a start / loop / end clip
//     sequel, size, foot   the sequel's own models: kind, size [x, y, z], footprint [cx, cz, hx, hz]
// } } }
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Content;

/// <summary>An animation sound event: [time, wav, volume, maxHear, group(, minFade)] of the GSF sound table (the min fade
/// distance only when the toolkit exported it; 0: the view's default).</summary>
public readonly record struct AnimSoundEvent(double T, string Wav, double Volume, double MaxHear, string Group, double MinFade = 0);

/// <summary>One model of the manifest.</summary>
public sealed class ModelInfo
{
    public string Name = "";
    public string Arch = "";
    /// <summary>the model whose animations this one uses (null: its own)</summary>
    public string? Anims;
    public bool Foliage, Map;
    public int NAnims;
    /// <summary>GSF pathfinder rows [type, x, y, z, sx, sy, sz, attr] (null: none)</summary>
    public List<double[]>? Pf;
    /// <summary>the sequel's footprint [cx, cz, hx, hz] / size [x, y, z]</summary>
    public double[]? Foot, Size;
    public string? Sequel;
    public readonly Dictionary<string, double> Speeds = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> Walk = new(StringComparer.Ordinal);
    public readonly Dictionary<string, List<AnimSoundEvent>> Sounds = new(StringComparer.Ordinal);
    public readonly Dictionary<string, (double T0, double T1)> Loops = new(StringComparer.Ordinal);
    public bool HasSounds => Sounds.Count > 0;

    public static ModelInfo From(string name, JsonNode n)
    {
        var m = new ModelInfo
        {
            Name = name, Arch = n["arch"].Str(), Anims = n["anims"] is JsonValue a && a.Str() is { Length: > 0 } s ? s.ToLowerInvariant() : null,
            Foliage = n["foliage"].Bool(), Map = n["map"].Bool(), NAnims = n["nanims"].Int(), Sequel = n["sequel"] is JsonValue q ? q.Str() : null,
        };
        if (n["pf"] is JsonArray pf)
        {
            m.Pf = new List<double[]>();
            foreach (var r in pf) if (r is JsonArray ra) { var row = new double[ra.Count]; for (int i = 0; i < ra.Count; i++) row[i] = ra[i].Num(); m.Pf.Add(row); }
        }
        m.Foot = Arr(n["foot"]); m.Size = Arr(n["size"]);
        if (n["speeds"] is JsonObject sp) foreach (var (k, v) in sp) m.Speeds[k] = v.Num();
        if (n["walk"] is JsonObject w) foreach (var (k, v) in w) m.Walk[k] = v.Str();
        if (n["sounds"] is JsonObject so)
            foreach (var (k, v) in so)
            {
                var list = new List<AnimSoundEvent>();
                if (v is JsonArray ea) foreach (var e in ea) list.Add(new AnimSoundEvent(e?[0].Num() ?? 0, e?[1].Str() ?? "", e?[2].Num() ?? 0, e?[3].Num() ?? 0, e?[4].Str() ?? "",
                    e is JsonArray ja && ja.Count > 5 ? ja[5].Num() : 0));
                m.Sounds[k] = list;
            }
        if (n["loops"] is JsonObject lo) foreach (var (k, v) in lo) m.Loops[k] = (v?[0].Num() ?? 0, v?[1].Num() ?? 0);
        return m;
        static double[]? Arr(JsonNode? x) { if (x is not JsonArray a) return null; var r = new double[a.Count]; for (int i = 0; i < a.Count; i++) r[i] = a[i].Num(); return r; }
    }
}

/// <summary>
/// The clips an instance of a model plays (AnimCtl): lower-case name -> duration, with the parts of start / loop /
/// end clips as "&lt;name&gt;#s", "#l", "#e" (engine/assets.js splitLoops), and the loop marks (lower-case name -> [t0, t1]).
/// </summary>
public sealed class ClipSet
{
    public static readonly ClipSet Empty = new();
    public readonly Dictionary<string, double> Duration = new(StringComparer.Ordinal);
    public readonly Dictionary<string, (double T0, double T1)> Loops = new(StringComparer.Ordinal);
    /// <summary>the clip names in file order (lower case; parts after their clips)</summary>
    public readonly List<string> Names = new();
    public int Count => Duration.Count;
    public bool Has(string name) => Duration.ContainsKey(name);

    public void Add(string name, double dur)
    {
        var n = name.ToLowerInvariant();
        if (!Duration.ContainsKey(n)) Names.Add(n);
        Duration[n] = dur;
    }
}

public sealed class ModelManifest
{
    static readonly Logger L = Log.For("Content.Models");

    readonly Dictionary<string, ModelInfo> _models = new(StringComparer.Ordinal);
    readonly Dictionary<string, ModelFileInfo?> _files = new(StringComparer.Ordinal);
    readonly Dictionary<string, ClipSet> _clips = new(StringComparer.Ordinal);
    readonly Func<string, string?>? _glbPath;

    public static readonly ModelManifest Empty = new(new JsonObject(), null, null);

    /// <param name="manifest">manifest.json</param>
    /// <param name="modelInfo">modelinfo.json (ModelFileInfo.BuildAll) or null</param>
    /// <param name="glbPath">where a model's .glb is (name -> path or null), read when modelinfo.json lacks it</param>
    public ModelManifest(JsonNode manifest, JsonNode? modelInfo, Func<string, string?>? glbPath)
    {
        if (manifest["models"] is JsonObject ms) foreach (var (k, v) in ms) if (v != null) _models[k.ToLowerInvariant()] = ModelInfo.From(k.ToLowerInvariant(), v);
        if (modelInfo?["models"] is JsonObject mi) foreach (var (k, v) in mi) _files[k.ToLowerInvariant()] = ModelFileInfo.FromJson(v);
        _glbPath = glbPath;
    }

    /// <summary>A data folder's assets/manifest.json (+ assets/modelinfo.json, + the .glb files in assets/models).</summary>
    public static ModelManifest Load(string dataDir)
    {
        string a = Path.Combine(dataDir, "assets");
        var man = Json.ParseFile(Path.Combine(a, "manifest.json"))!;
        var mi = Json.TryParseFile(Path.Combine(a, "modelinfo.json"));
        string models = Path.Combine(a, "models");
        var m = new ModelManifest(man, mi, n => { var p = Path.Combine(models, n + ".glb"); return System.IO.File.Exists(p) ? p : null; });
        L.Info($"manifest {dataDir}: {m._models.Count} models, model info for {m._files.Count}");
        return m;
    }

    readonly Dictionary<string, string> _extraGlb = new(StringComparer.Ordinal);
    /// <summary>adds the models of an extra manifest that this one lacks (the sequel's art the prepare tool put into a
    /// configuration's overrides: overrides/assets/manifest_sequel.json, the .glb files beside it in models/); returns how
    /// many</summary>
    public int AddExtra(string manifestPath, string modelsDir)
    {
        if (Json.TryParseFile(manifestPath)?["models"] is not JsonObject ms) return 0;
        int n = 0;
        foreach (var (k, v) in ms)
        {
            var name = k.ToLowerInvariant();
            if (v == null) continue;
            var p = Path.Combine(modelsDir, name + ".glb");
            // (a model the data lists without its file: the override's file)
            if (System.IO.File.Exists(p) && (!_models.ContainsKey(name) || _glbPath?.Invoke(name) == null)) _extraGlb[name] = p;
            if (_models.ContainsKey(name)) continue;
            _models[name] = ModelInfo.From(name, v);
            n++;
        }
        if (n > 0) L.Info($"{manifestPath}: {n} more models");
        return n;
    }
    /// <summary>a configuration's extra manifests (overrides/assets/manifest_sequel.json) into this one</summary>
    public ModelManifest WithOverrides(GameConfiguration? cfg)
    {
        if (cfg != null) AddExtra(Path.Combine(cfg.OverridesDir, "assets", "manifest_sequel.json"), Path.Combine(cfg.OverridesDir, "assets", "models"));
        return this;
    }

    /// <summary>Through a configuration's asset source (overrides first).</summary>
    public static ModelManifest Load(AssetSource src)
    {
        var man = Json.ParseFile(src.Require("assets/manifest.json"))!;
        var mip = src.Resolve("assets/modelinfo.json");
        var mi = mip != null ? Json.TryParseFile(mip) : null;
        return new ModelManifest(man, mi, n => src.Resolve("assets/models/" + n + ".glb"));
    }

    public int Count => _models.Count;
    public IEnumerable<string> Names => _models.Keys;
    /// <summary>does the model exist (World.template(name, true) of the JS)</summary>
    public bool Has(string? name) => name != null && _models.ContainsKey(name.ToLowerInvariant());
    public ModelInfo? Get(string? name) => name != null && _models.TryGetValue(name.ToLowerInvariant(), out var m) ? m : null;

    /// <summary>the values of the model file (null: neither modelinfo.json nor the .glb have it)</summary>
    public ModelFileInfo? File(string? name)
    {
        if (name == null) return null;
        var n = name.ToLowerInvariant();
        lock (_files)
        {
            if (_files.TryGetValue(n, out var f)) return f;
            string? p = _extraGlb.TryGetValue(n, out var xp) ? xp : _glbPath?.Invoke(n);
            if (p != null)
            {
                try { f = ModelFileInfo.ReadGlb(p); }
                catch (Exception e) { L.Warn($"cannot read {p}: {e.Message}"); }
            }
            if (f == null && Has(n)) { if (Log.FirstTime("Content.Models|nodata")) L.Warn($"no model data (assets/modelinfo.json or models/<name>.glb) for some models, first {n}: their clips, links and size are unknown"); L.Debug($"no model data for {n}"); }
            _files[n] = f;
            return f;
        }
    }

    /// <summary>the GSF object kind ("Char", "Anim", "Vehi", "Bldg", "Ship" ...), "" if unknown</summary>
    public string Fourcc(string? name) => File(name)?.Fourcc ?? "";

    /// <summary>
    /// The clips an instance of the model plays: its own (split by its loop marks), or those of its animation source
    /// (info.anims: retargeted, with the source's loop marks) - engine/assets.js loadModel.
    /// </summary>
    public ClipSet Clips(string? name)
    {
        if (name == null) return ClipSet.Empty;
        var n = name.ToLowerInvariant();
        lock (_clips)
        {
            if (_clips.TryGetValue(n, out var c)) return c;
            var info = Get(n);
            if (info?.Anims != null && info.Anims != n) c = Clips(info.Anims);
            else
            {
                c = new ClipSet();
                var f = File(n);
                if (f != null)
                {
                    foreach (var (clip, dur) in f.Clips) c.Add(clip, dur);
                    if (info != null) SplitLoops(c, f.Clips, info.Loops);
                }
            }
            _clips[n] = c;
            return c;
        }
    }

    // clips made of a start, a loop and an end part (the loop marks of the GSF animation chunks): the parts as extra
    // clips "<name>#s", "<name>#l", "<name>#e" - AnimCtl plays the start once, repeats the loop and plays the end when the
    // action stops, like the original engine (engine/assets.js splitLoops)
    /// <summary>
    /// The shortest loop part a clip is split for (s): 0.05, but one frame (0.04 s) for an attack clip - the Black Widow's
    /// attack_front loops frames 30-31 (start: it curls up, loop: it holds while it shoots, end: it uncurls; Vehicle.usl
    /// CWehrspinne). Other one-frame loops (characters' idle_1 / sleep, buildings' spawn / deliver / work_finished) stay
    /// whole clips, as before.
    /// </summary>
    public static double MinLoopPart(string clip) => clip.StartsWith("attack", StringComparison.OrdinalIgnoreCase) ? 0.035 : 0.05;

    static void SplitLoops(ClipSet c, Dictionary<string, double> clips, Dictionary<string, (double T0, double T1)> loops)
    {
        foreach (var (name, dur) in clips)
        {
            if (!loops.TryGetValue(name, out var lp) || lp.T1 - lp.T0 < MinLoopPart(name) || lp.T1 > dur + 0.02) continue;
            double t1 = Math.Min(lp.T1, dur);
            c.Add(name + "#l", t1 - lp.T0);
            if (lp.T0 > 0.05) c.Add(name + "#s", lp.T0);
            if (dur - t1 > 0.05) c.Add(name + "#e", dur - t1);
            c.Loops[name.ToLowerInvariant()] = (lp.T0, lp.T1);
        }
    }

    /// <summary>a link's position in the model frame (null: the model has no such link or is unknown)</summary>
    public V3? Link(string? model, string link) => File(model) is { } f && f.Links.TryGetValue(link, out var p) ? p : null;
    public bool HasLink(string? model, string link) => File(model)?.Links.ContainsKey(link) ?? false;

    /// <summary>bounding box size (x, y, z) of the drawn model; (-inf ...) when unknown, like an empty three.js Box3</summary>
    public (V3 Min, V3 Max)? Box(string? name)
    {
        var f = File(name);
        if (f?.BoxMin is V3 a && f.BoxMax is V3 b) return (a, b);
        var sz = Get(name)?.Size;
        if (sz != null && sz.Length >= 3) return (new V3(-sz[0] / 2, 0, -sz[2] / 2), new V3(sz[0] / 2, sz[1], sz[2] / 2));
        return null;
    }

    /// <summary>
    /// collision radius and height of a unit model (entities.js buildModel: radius = clamp(min(sx, sz) * 0.42, 0.45, 6),
    /// height &gt;= 1.5; an unknown model gives what an empty box gives: 0.45 and 1.5)
    /// </summary>
    public (double Radius, double Height) UnitSize(string? name)
    {
        var b = Box(name);
        if (b is not var (mn, mx)) return (0.45, 1.5);
        double sx = mx.X - mn.X, sz = mx.Z - mn.Z;
        return (Math.Max(0.45, Math.Min(Math.Min(sx, sz) * 0.42, 6)), Math.Max(1.5, mx.Y - mn.Y));
    }

    /// <summary>
    /// how far the model reaches out from its origin on the ground plane: the largest distance of its drawn box's sides
    /// from the origin - what the selection circle has to go round (SelectionRing). A skinned model's box (characters,
    /// animals, walkers) stands in the GSF frame, z pointing down from the ground (max z ~ 0, min z = -height): its ground
    /// plane is x / y; every other box is y up (x / z). Without a drawn box the pick volumes (attribute bit 18, y up),
    /// then the manifest's size. Null: no model data.
    /// </summary>
    public double? SelReach(string? name)
    {
        var f = File(name);
        if (f?.BoxMin is V3 a && f.BoxMax is V3 b)
            return Math.Abs(b.Z) < 0.25 && -a.Z > 0.5 && b.Y - a.Y > 0.1 ? Reach(a.X, b.X, a.Y, b.Y) : Reach(a.X, b.X, a.Z, b.Z);
        if (f?.PickMin is V3 pa && f.PickMax is V3 pb) return Reach(pa.X, pb.X, pa.Z, pb.Z);
        return Box(name) is var (mn, mx) ? Reach(mn.X, mx.X, mn.Z, mx.Z) : null;
        static double Reach(double x0, double x1, double z0, double z1) => Math.Max(Math.Max(Math.Abs(x0), Math.Abs(x1)), Math.Max(Math.Abs(z0), Math.Abs(z1)));
    }

    /// <summary>the model's object kind is a character / animal / vehicle ("dynamic" models: parts.js isDynamicKind)</summary>
    public static bool IsDynamicKind(string fourcc) => fourcc == "Anim" || fourcc == "Vehi" || fourcc == "Char";
}
