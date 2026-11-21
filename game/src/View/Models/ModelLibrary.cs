// Loads converted ParaWorld models on demand and hands out instances.
// Materials are swapped to cheap Lambert materials, and skinned pieces that share a skeleton+material are merged;
// static models get their rigid parts merged per material and part state (one draw per material instead of one per
// GSF chunk).
//
// The models are glTF files of the converted data (assets/models/<name>.glb + assets/tex/*), found through the
// configuration's AssetSource and loaded AT RUNTIME with Godot's GLTFDocument - they are never imported into res://.
//
// Ported from remake/src/engine/assets.js (loadModel, cloneModel, mergeSkinned, mergeStatic, tagWallArms, SMALL_PART)
// (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.View.Anim;
using PwRemake.View.Materials;
using PwRemake.View.Parts;

namespace PwRemake.View.Models;

public sealed class ModelLibrary
{
    static readonly PwRemake.Sim.Core.Logger L = Log.For("View.Models");
    public AssetSource Source { get; }
    public ModelManifest Manifest { get; }
    /// <summary>
    /// The parts other models hang on a model's links (the composites table: turrets, build-ups, cranes - by model name:
    /// (link, part model)). A model that carries its own copy of such a part (seas_carrier: the turret it gets at "we"
    /// is also modelled into the hull) is loaded without that copy - else it stood still next to the turning one
    /// (DropBakedParts). Set by the game before the first model loads; null = nothing dropped.
    /// </summary>
    public Func<string, IEnumerable<(string Link, string Gfx)>>? PartsOnLinks;
    public TextureCache Textures { get; }
    /// <summary>Called for every new instance of a model with particle emitters (assets.js Assets.onClone: the game's
    /// Particles.Track). null: nobody sprays them (the model viewer without particles).</summary>
    public Action<ModelInstance>? OnInstance;
    /// <summary>Called for an emitter of another model hung on a node of an instance (the infected look's flies:
    /// ModelInstance.SetInfected - the game's Particles.TrackNode). null: not sprayed.</summary>
    public Action<Node3D, PartRef, ModelInstance>? OnEmitter;
    readonly Dictionary<string, ModelTemplate?> _templates = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>m: the radius below which a rigid part casts no shadow</summary>
    const float SMALL_PART = 0.8f;

    public ModelLibrary(AssetSource src, ModelManifest? manifest = null)
    {
        Source = src;
        Manifest = manifest ?? ModelManifest.Load(src);
        Textures = new TextureCache(src);
        Settings = new SettingSource(SettingTextures.Load(src), Textures);
    }

    /// <summary>the original's setting textures of the configuration (empty for content prepared without them:
    /// SettingMaterials, docs/VIEW.md "Setting textures")</summary>
    public SettingSource Settings { get; }

    public ModelInfo? Info(string? name) => Manifest.Info(name);
    public bool Has(string? name) => Manifest.Info(name) != null;
    public IEnumerable<ModelTemplate> Loaded => _templates.Values.Where(t => t != null)!;

    /// <summary>The template of a model, loaded on first use (null if the model does not exist or fails to load;
    /// quiet: no warning for a missing model - world.template(name, soft)).</summary>
    public ModelTemplate? Template(string? name, ModelLoadOptions? opts = null, bool quiet = false)
    {
        if (string.IsNullOrEmpty(name)) return null;
        opts ??= ModelLoadOptions.Default;
        var key = name.ToLowerInvariant() + opts.Key;
        if (_templates.TryGetValue(key, out var t)) return t;
        if (Manifest.Info(name) == null) { if (!quiet) L.WarnOnce("unknown:" + name, $"unknown model {name}"); _templates[key] = null; return null; }
        if (!_loading.Add(key)) { L.Warn($"model {name} borrows its own animations"); return null; }
        try { t = Load(name.ToLowerInvariant(), opts); }
        // a file missing from the converted data is a data problem (Warn: the toolkit did not convert it), a file
        // that is there and does not load is a bug (Error with the exception)
        catch (System.IO.FileNotFoundException ex) { L.Warn($"model {name}: {ex.Message} - re-run the toolkit's \"Prepare the game data\""); t = null; }
        catch (Exception ex) { L.Error($"model {name} failed", ex); t = null; }
        finally { _loading.Remove(key); }
        _templates[key] = t;
        return t;
    }

    /// <summary>
    /// A new instance of a model (cloneModel): party = the owner's colour (null = untinted); party-colour materials
    /// are swapped for a tinted copy shared by every model of that player.
    /// </summary>
    public ModelInstance? Instantiate(string? name, Color? party = null, ModelLoadOptions? opts = null)
    {
        var t = Template(name, opts);
        return t == null ? null : Instantiate(t, party);
    }

    public ModelInstance Instantiate(ModelTemplate tpl, Color? party = null)
    {
        var inst = new ModelInstance(this, tpl);
        if (party != null) inst.SetPartyColor(party);
        if (tpl.Emitters.Count > 0) OnInstance?.Invoke(inst);
        return inst;
    }

    // ------------------------------------------------------------------------------------------------ loading

    sealed class Ctx
    {
        public string Fourcc = "";
        public readonly Dictionary<Node, PartRef> Tags = new();
        public Node3D Root = null!, Model = null!;
        public PartRef Tag(Node n) { if (!Tags.TryGetValue(n, out var p)) Tags[n] = p = new PartRef(); return p; }
        public PartRef? Get(Node n) => Tags.TryGetValue(n, out var p) ? p : null;
    }

    static readonly Regex FoliageName = new("_foliage");
    /// <summary>--perf-mem: the static memory after each loading step in LoadSteps</summary>
    public static bool MemSteps;

    ModelTemplate Load(string name, ModelLoadOptions opts)
    {
        var sw = Stopwatch.StartNew();
        var info = Manifest.Info(name)!;
        var path = Source.Require($"assets/models/{name}.glb");
        var json = GlbJson.Read(path);
        var doc = new GltfDocument();
        // the textures come from the shared cache (TextureCache): Godot does not load the images of the file
        var st = new GltfState { HandleBinaryImage = 0 };      // HANDLE_BINARY_DISCARD_TEXTURES
        var marks = new List<(string, double)>();
        ulong mem0 = MemSteps ? OS.GetStaticMemoryUsage() : 0;
        void Mark(string what) => marks.Add((MemSteps ? $"{what} [{(OS.GetStaticMemoryUsage() - (double)mem0) / 1048576:0.0} MB]" : what, sw.Elapsed.TotalMilliseconds));
        Mark("read");
        var err = doc.AppendFromBuffer(GlbJson.WithoutImages(json.Bytes, json.Root), System.IO.Path.GetDirectoryName(path) ?? "", st);
        if (err != Error.Ok) throw new InvalidOperationException($"{path}: glTF error {err}");
        Mark("parse");
        var gen = doc.GenerateScene(st, 30, false, false) as Node3D ?? throw new InvalidOperationException($"{path}: no scene");
        Mark("generate");
        var rootEx = json.RootExtras;
        var c = new Ctx { Fourcc = rootEx["fourcc"].Str(), Root = gen };
        string fourcc = c.Fourcc;
        gen.Name = "ModelRoot";

        // the glTF root (turns and mirrors the GSF frame) and the clips
        AnimationPlayer? ap = null;
        Node3D? model = null;
        foreach (var ch in gen.GetChildren())
        {
            if (ch is AnimationPlayer a) ap = a;
            else if (model == null && ch is Node3D n) model = n;
        }
        if (model == null) throw new InvalidOperationException($"{path}: empty model");
        string origName = model.Name;
        model.Name = "Model";
        c.Model = model;
        var clips = new List<(string, Animation)>();
        if (ap != null)
        {
            foreach (var lname in ap.GetAnimationLibraryList())
            {
                var lib = ap.GetAnimationLibrary(lname);
                foreach (var an in lib.GetAnimationList()) clips.Add((an, RenamePaths(lib.GetAnimation(an), origName)));
            }
            gen.RemoveChild(ap);
            ap.Free();
        }

        // materials: the remake's Lambert-like ones, textures from the shared cache
        var gm = st.GetMaterials();
        var jm = json.Materials;
        var swap = new Dictionary<Material, Material>();
        // a model with an Icewaste setting texture (the original's snowy atlas) gets no shader snow (SnowCover) on any
        // of its materials: the painted snow of the atlas is the original's
        bool icewaste = false;
        if (Settings.Index.Any)
            for (int i = 0; i < jm.Count; i++)
                if (jm[i] is JsonObject mj && Settings.Index.Has(Settings.Index.KeyOf(mj["extras"]?["gsf_texture"].Str(),
                        json.TextureUri(mj["pbrMetallicRoughness"]?["baseColorTexture"])), "icewaste")) { icewaste = true; break; }
        for (int i = 0; i < gm.Count; i++)
        {
            if (gm[i] is StandardMaterial3D sm)
                swap[sm] = ModelMaterials.ToLambert(sm, i < jm.Count ? jm[i] as JsonObject : null, fourcc, r => Textures.Get(json.TextureUri(r)), name,
                    Settings, !icewaste);
        }
        var meshes = new List<MeshInstance3D>();
        Walk(gen, n => { if (n is MeshInstance3D mi) meshes.Add(mi); });
        foreach (var mi in meshes)
        {
            var tag = c.Tag(mi);
            // (SEAS paint: a part that keeps its colours - kept apart from the meshes merged with it)
            tag.SeasKeep = SeasKeepParts.Keeps(name, mi.Name);
            if (mi.Mesh is ArrayMesh am)
                for (int s = 0; s < am.GetSurfaceCount(); s++)
                {
                    var m = am.SurfaceGetMaterial(s);
                    if (m != null && swap.TryGetValue(m, out var r)) am.SurfaceSetMaterial(s, r);
                }
            // (opaque vegetation surfaces on the bark band the cut-out would erase: ModelMaterials.BarkSurface)
            if (mi.Mesh is ArrayMesh bm)
                for (int s = 0; s < bm.GetSurfaceCount(); s++)
                    if (ModelMaterials.Tag(bm.SurfaceGetMaterial(s)) is { Bark: Material bark } bt && ModelMaterials.BarkSurface(bm, s, bt.Map))
                        bm.SurfaceSetMaterial(s, bark);
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
            if (SurfaceMats(mi).Any(m => ModelMaterials.Tag(m)?.Glow == true)) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            tag.Skinned = mi.Skin != null;
            // attribute of every mesh (glTF node extras; Godot keeps a multi-primitive node as one mesh of surfaces)
            for (Node? n = mi; n != null && n != gen; n = n.GetParent())
                if (Extra(n, "attr") is Variant v) { tag.HasAttr = true; tag.Attr = AttrOf(v); break; }
        }
        // particle emitters (engine/particles.js draws them; their part states are kept here)
        List<(string?, uint)>? emitMats = null;
        Walk(model, n =>
        {
            if (n is MeshInstance3D || Extra(n, "kind") is not Variant k || k.AsString() != "particles") return;
            var t = c.Tag(n);
            t.Kind = PartKind.Particles;
            if (Extra(n, "attr") is Variant v) { t.HasAttr = true; t.Attr = AttrOf(v); }
            if (Extra(n, "emitter") is Variant ev && ev.VariantType == Variant.Type.Dictionary)
                try { t.Emitter = PwRemake.View.Fx.EmitterDef.FromJson(System.Text.Json.Nodes.JsonNode.Parse(Godot.Json.Stringify(ev)) as JsonObject); }
                catch (Exception ex) { L.WarnOnce("emitter:" + name, $"model {name}: emitter {n.Name} unreadable ({ex.Message})"); }
            // the original's blending by the sheet's GSF material (alpha bits 0x3, not the converter's 0x4: the SEAS
            // buildings' steam is a mixed sprite - added, its grey sheet showed as glowing squares; docs/EFFECTS.md)
            if (t.Emitter != null && t.Emitter.ResolveBlend(emitMats ??= PwRemake.View.Fx.EmitterDef.MaterialsOf(json.Root))
                    == PwRemake.View.Fx.EmitterDef.Blend.Ambiguous)
                L.WarnOnce("emitblend:" + name + n.Name, $"model {name}: emitter {n.Name}: materials of {t.Emitter.Tex} disagree on the blending");
        });
        // day and night parts (buildings' and ships' lamps: PartBits.TodOf), the particle emitters too (fireflies)
        foreach (var (n, t) in c.Tags) if (t.HasAttr) t.Tod = PartBits.TodOf(t.Attr, fourcc);

        // parts never drawn: foliage quads (drawn as sprites), pick volumes, shadow models, lower LODs
        var fol = new List<Node3D>();
        var foliageMesh = new HashSet<Node>();
        Aabb? pick = null;
        // a gate model by its parts (a wall archive's model with skinned leaves of render flag 16) although its class is
        // no CGate: the holy city's main gates hcl13_gate (mission 13, a CBuilding) and hc_gate (mission 5's CHC_Wall
        // and its CGate hc_gate_real) - their leaves modelled standing open showed with the moving ones
        bool gate = opts.Gate || (fourcc == "Wall" && meshes.Any(m => c.Tags[m] is { HasAttr: true, Skinned: true } t && ((t.Attr >> 16) & 1) != 0));
        foreach (var mi in meshes)
        {
            var tag = c.Tags[mi];
            if (FoliageName.IsMatch(mi.Name) || FoliageName.IsMatch(mi.GetParent()?.Name ?? "")) { foliageMesh.Add(mi); fol.Add(mi); continue; }
            // a gate's leaves modelled standing open (render flag 14, without 16): only the level editor shows them; the
            // game hides flag 14 and shows 16, the leaves its open / close clips move (Building.usl:2390-2391)
            if (gate && tag.HasAttr && ((tag.Attr >> 14) & 1) != 0 && ((tag.Attr >> 16) & 1) == 0) { fol.Add(mi); continue; }
            if (tag.HasAttr)
            {
                var h = PartBits.HelperPart(tag.Attr, fourcc, SurfaceMats(mi).Any(m => ModelMaterials.Tag(m)?.Glow == true));
                // a building's leftover chunk: no construction state at all (PartBits.Unstaged)
                if (h == null && PartBits.Unstaged(tag.Attr, fourcc)) h = "unstaged";
                // flag-less low-poly hulls (whole-object pick/collision volumes)
                if (h == null && (tag.Attr >> 5) == 0 && !tag.Skinned && VertexCount(mi) <= 24)
                {
                    var bb = BoxIn(mi, gen);
                    var d = bb.Size;
                    // (a volume: a flat one is a surface - the water of the water sanctuary's pools is a quad of a few
                    // vertices with only the LOD bits; dropped as a "hull", its basins stood dry under the lily pads)
                    if (Mathf.Max(d.X, Mathf.Max(d.Y, d.Z)) > 8 && Mathf.Min(d.X, Mathf.Min(d.Y, d.Z)) > 0.5f) h = "pick";
                }
                if (h == "pick") { var bb = BoxIn(mi, gen); pick = pick == null ? bb : pick.Value.Merge(bb); }
                if (h != null) fol.Add(mi);
                continue;
            }
            // older exports without attributes: pick volumes / parked parts by shape
            if (!tag.Skinned && mi.Mesh != null)
            {
                var bb = mi.Mesh.GetAabb();
                float big = Mathf.Max(bb.Size.X, Mathf.Max(bb.Size.Y, bb.Size.Z));
                if (mi.Name.ToString().EndsWith("_f0") && VertexCount(mi) <= 24 && big > 8) fol.Add(mi);
                else if (bb.Position.Z > 40 || bb.Position.Y > 40) fol.Add(mi);
            }
        }
        // billboards of buildings / ships / machines (lanterns, banners): camera-facing sprite meshes in their place.
        // Trees and other vegetation keep theirs in the world-wide foliage field (props.js FoliageField).
        bool vegetation = fourcc == "Vgtn" || fourcc == "Ress" || fourcc == "Deko";
        var foliage = rootEx["foliage"] as JsonArray;
        if (!vegetation && foliage != null)
        {
            var byName = new Dictionary<string, JsonObject>();
            foreach (var e in foliage) if (e is JsonObject f) byName[f["mesh"].Str()] = f;
            foreach (var o in fol)
            {
                if (!foliageMesh.Contains(o) || o is not MeshInstance3D mi) continue;
                var tag = c.Tags[mi];
                string nodeName = mi.Name;
                JsonObject? f = byName.GetValueOrDefault(nodeName) ?? byName.Values.FirstOrDefault(x => nodeName.StartsWith(x["mesh"].Str()));
                if (f == null || f["pts"] is not JsonArray pts || pts.Count == 0 || Regex.IsMatch(f["tex"].Str(), "particle", RegexOptions.IgnoreCase) || tag.Skinned) continue;
                uint attr = tag.HasAttr ? tag.Attr : 0;
                if (PartBits.HelperPart(attr, fourcc) != null) continue;
                var mt = ModelMaterials.Tag(SurfaceMats(mi).FirstOrDefault());
                if (mt?.Map == null) continue;
                var sp = ModelSprites.Create(f, mt.Map, mt.Glow);
                // (marked for the map's decorations: FoliageField leaves a glow to these additive sprites - drawn as
                // cut-out leaves, a lantern's halo was a black square)
                if (mt.Glow) f["glow"] = true;
                var spt = c.Tag(sp);
                spt.Kind = PartKind.Sprite; spt.HasAttr = true; spt.Attr = attr; spt.Tod = PartBits.TodOf(attr, fourcc);
                sp.Transform = mi.Transform;
                (mi.GetParent() ?? model).AddChild(sp);
            }
        }
        foreach (var o in fol) { c.Tags.Remove(o); o.GetParent()?.RemoveChild(o); o.Free(); }
        // the model's own copies of the parts hung on its links (before merging: they are still single meshes)
        if (PartsOnLinks?.Invoke(name) is { } onLinks) DropBakedParts(c, name, onLinks);
        Mark("materials+parts");
        MergeSkinned(c);
        if (opts.WallArms) TagWallArms(c);
        bool isStatic = opts.Static ?? !PartBits.IsDynamicKind(fourcc);
        if (isStatic)
        {
            var animated = new HashSet<string>();
            foreach (var (_, a) in clips) for (int t = 0; t < a.GetTrackCount(); t++) animated.Add(ClipTools.TrackTarget(a.TrackGetPath(t)));
            MergeStatic(c, animated, opts.Gate ? PartBits.GATE_ENDS : 0, BuildUp.Keep(fourcc));    // (a gate's end pieces stay apart)
        }
        // building state, epoch and damage stage of the static parts and of the particle emitters (fires of the epoch
        // variants, smoke of the damage stages)
        if (!PartBits.IsDynamicKind(fourcc))
            foreach (var (_, t) in c.Tags)
                if (t.Sig < 0 && t.HasAttr) { int sg = PartBits.StaticSig(t.Attr, opts.Gate ? PartBits.GATE_ENDS : 0); if (sg != PartBits.SigAll) t.Sig = sg; }
        // the parts that rise out of the ground or sink into it while the building is built: the top of each in the GSF
        // frame (the engine's chunk box, PWClient 0x7aa060)
        bool buildUp = false;
        if (BuildUp.Of(fourcc) is { } bum)
            foreach (var (n, t) in c.Tags)
            {
                if (!t.HasAttr || (t.Attr & (bum.Appear | bum.Disappear)) == 0 || t.Kind == PartKind.Particles) continue;
                buildUp = true;
                if (n is MeshInstance3D bmi && bmi.Mesh != null) t.Top = BoxIn(bmi, c.Model).End.Z;
            }
        Mark("merge");
        // small rigid parts (a weapon, a cup, a lantern) cast no shadow: their shadows are a few pixels, but each is one
        // more draw in the shadow pass for every unit and building carrying it
        foreach (var (n, t) in c.Tags)
        {
            if (n is not MeshInstance3D mi || t.Skinned || mi.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off) continue;
            var xf = ToRoot(mi, null);
            if (SphereRadius(mi) * Mathf.Pow(Mathf.Abs(xf.Basis.Determinant()), 1f / 3) < SMALL_PART) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        // the model viewer's tree foliage (the game: a world-wide field)
        if (opts.TreeSprites && vegetation && foliage != null)
        {
            var rnd = new Random(name.GetHashCode());
            float top = ModelSprites.FoliageTop(foliage);
            foreach (var e in foliage)
            {
                if (e is not JsonObject f) continue;
                var tex = Textures.Get(f["tex"].Str());
                if (tex == null) continue;
                var sp = ModelSprites.CreateTree(f, tex, rnd, top);
                if (sp != null) model.AddChild(sp);
            }
        }

        // clips made of a start, a loop and an end part (the loop marks of the GSF animation chunks, manifest
        // models[m].loops = {clip: [t0, t1]}): the parts as extra clips "<name>#s", "<name>#l", "<name>#e" -
        // AnimController plays the start once, repeats the loop and plays the end when the action stops
        var loops = info.Loops;
        Mark("shadows+sprites");
        if (loops != null) clips = ClipTools.SplitLoops(clips, loops);
        var sounds = info.Sounds;
        if (info.Anims != null)
        {
            var src = Template(info.Anims);
            if (MemSteps) Mark("anim source");
            if (src != null && src.Clips != null)
            {
                var list = src.Clips.GetAnimationList().Select(n => ((string)n, src.Clips.GetAnimation(n))).ToList();
                clips = ClipTools.Retarget(list, src.Scene, gen);
                if (MemSteps) Mark($"retarget {clips.Count(c => !ReferenceEquals(c.Item2, src.Clips.GetAnimation(c.Item1)))} of {clips.Count}");
                loops = src.Loops;
                sounds ??= src.Sounds;
            }
            else { clips = new(); loops = null; }
        }

        var tpl = new ModelTemplate
        {
            Name = name, Info = info, Options = opts, Fourcc = fourcc, Extras = rootEx, Scene = gen, Loops = loops, Sounds = sounds,
            Pick = pick, HasSkeleton = gen.FindChild("Skeleton3D", true, false) != null, HasBuildUp = buildUp,
        };
        if (clips.Count > 0)
        {
            tpl.Clips = new AnimationLibrary();
            foreach (var (n, a) in clips) if (!tpl.Clips.HasAnimation(n)) { int dead = DropDeadTracks(a, gen); if (dead > 0) L.Debug($"{name}: clip {n}: {dead} of {dead + a.GetTrackCount()} tracks of dropped parts removed"); tpl.Clips.AddAnimation(n, a); tpl.ClipNames[n.ToLowerInvariant()] = n; }
        }
        if (loops != null) foreach (var (k, v) in loops) tpl.LoopsLower[k.ToLowerInvariant()] = v;
        // the switchable parts and the links, as paths from ModelRoot
        Walk(gen, n =>
        {
            if (n is Node3D n3 && n3.Name.ToString().StartsWith("link_")) tpl.Links[n3.Name.ToString().Substring(5)] = gen.GetPathTo(n3);
        });
        foreach (var (n, t) in c.Tags)
        {
            t.Path = gen.GetPathTo(n);
            if (n is MeshInstance3D mi)
            {
                var mats = SurfaceMats(mi).ToArray();
                t.Surfaces = mats;
                var ps = new List<(int, Material)>();
                for (int s = 0; s < mats.Length; s++) if (ModelMaterials.Tag(mats[s])?.Party == true) ps.Add((s, mats[s]));
                if (ps.Count > 0) t.PartySurfaces = ps.ToArray();
                tpl.MeshCount++; tpl.SurfaceCount += mats.Length;
            }
            tpl.Parts.Add(t);
            if (t.Kind == PartKind.Particles && t.Emitter != null) tpl.Emitters.Add(t);
        }
        // the box of the model (bind pose, hidden parts included)
        Aabb? box = null;
        foreach (var (n, t) in c.Tags) if (n is MeshInstance3D mi && mi.Mesh != null) { var b = BoxIn(mi, gen); box = box == null ? b : box.Value.Merge(b); }
        tpl.Bounds = box ?? new Aabb();
        // default look: finished, intact, first epoch / wild animal
        var resolved = c.Tags.Select(kv => ((Node3D)kv.Key, kv.Value)).ToArray();
        if (fourcc == "Anim" || fourcc == "Vehi") PartStates.ApplyMask(resolved, PartBits.AnimalMask(new AnimalLook { Owned = fourcc == "Vehi" }));
        else if (fourcc == "Char") PartStates.ApplyMask(resolved, 1u << PartBits.CHTR_ACTIVATED);   // VIS_FLAG_CHTR_ACTIVATED parts (healer kit) start hidden
        else PartStates.ApplyState(resolved, new BuildingState());
        SetOwner(gen, gen);
        Mark("clips+state");
        tpl.Packed = new PackedScene();
        var perr = tpl.Packed.Pack(gen);
        if (perr != Error.Ok) throw new InvalidOperationException($"{name}: cannot pack ({perr})");
        Mark("pack");
        tpl.LoadMs = sw.Elapsed.TotalMilliseconds;
        double prev = 0;
        tpl.LoadSteps = string.Join(" ", marks.Select(m => { var r = $"{m.Item1} {m.Item2 - prev:0}"; prev = m.Item2; return r; }));
        L.Debug(() => $"model {name} ({fourcc}): {tpl.MeshCount} meshes, {tpl.SurfaceCount} surfaces, {clips.Count} clips, {tpl.LoadMs:0} ms");
        // the importer's state (the file's buffers, its parsed meshes and clips) at once, not when the garbage collector
        // gets to its wrappers - the managed heap grows little and collects rarely, the native memory piled up
        st.Dispose(); doc.Dispose();
        if (MemSteps) { Mark("dispose"); tpl.LoadSteps += $" dispose [{(OS.GetStaticMemoryUsage() - (double)mem0) / 1048576:0.0} MB]"; }
        return tpl;
    }

    // ------------------------------------------------------------------------------------------------ helpers

    /// <summary>removes the tracks of parts the template dropped (lower LODs, pick volumes, foliage quads): the mixer
    /// warned "couldn't resolve track" for each (the question mark's 'Model/questionmark_red_mesh3_lod1f_f0') and looked
    /// them up again and again</summary>
    static int DropDeadTracks(Animation a, Node root)
    {
        int n = 0;
        for (int i = a.GetTrackCount() - 1; i >= 0; i--)
        {
            var path = a.TrackGetPath(i);
            if (root.GetNodeOrNull(new NodePath(path.GetConcatenatedNames())) == null) { a.RemoveTrack(i); n++; }
        }
        return n;
    }

    static Animation RenamePaths(Animation a, string origName)
    {
        for (int t = 0; t < a.GetTrackCount(); t++)
        {
            string p = a.TrackGetPath(t);
            if (p == origName || p.StartsWith(origName + "/") || p.StartsWith(origName + ":")) a.TrackSetPath(t, "Model" + p.Substring(origName.Length));
        }
        return a;
    }

    internal static void Walk(Node n, Action<Node> f)
    {
        f(n);
        foreach (var ch in n.GetChildren()) Walk(ch, f);
    }

    static void SetOwner(Node n, Node owner)
    {
        foreach (var ch in n.GetChildren()) { ch.Owner = owner; SetOwner(ch, owner); }
    }

    static Variant? Extra(Node n, string key)
    {
        if (!n.HasMeta("extras")) return null;
        var m = n.GetMeta("extras");
        if (m.VariantType != Variant.Type.Dictionary) return null;
        var d = m.AsGodotDictionary();
        return d.TryGetValue(key, out var v) ? v : (Variant?)null;
    }

    static uint AttrOf(Variant v) => v.VariantType switch
    {
        Variant.Type.Float => (uint)(long)v.AsDouble(),
        Variant.Type.Int => (uint)v.AsInt64(),
        Variant.Type.String => GlbJson.ParseAttr(v.AsString()),
        _ => 0,
    };

    internal static IEnumerable<Material> SurfaceMats(MeshInstance3D mi)
    {
        if (mi.Mesh == null) yield break;
        for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
        {
            var m = mi.GetSurfaceOverrideMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
            if (m != null) yield return m;
        }
    }

    static int VertexCount(MeshInstance3D mi)
    {
        if (mi.Mesh is not ArrayMesh am) return int.MaxValue;
        int n = 0;
        for (int s = 0; s < am.GetSurfaceCount(); s++) n += am.SurfaceGetArrayLen(s);
        return n;
    }

    /// <summary>The transform from a node to an ancestor (exclusive; null = the top of its tree).</summary>
    internal static Transform3D ToRoot(Node3D n, Node? stop)
    {
        var xf = Transform3D.Identity;
        for (Node? p = n; p != null && p != stop; p = p.GetParent())
            if (p is Node3D p3) xf = p3.Transform * xf;
        return xf;
    }

    static Aabb BoxIn(MeshInstance3D mi, Node stop)
    {
        var xf = ToRoot(mi, stop);
        return xf * (mi.Mesh?.GetAabb() ?? new Aabb());
    }

    // three.js computeBoundingSphere: the box centre and the farthest vertex
    static float SphereRadius(MeshInstance3D mi)
    {
        if (mi.Mesh is not ArrayMesh am) return 0;
        var c = am.GetAabb().GetCenter();
        float r2 = 0;
        for (int s = 0; s < am.GetSurfaceCount(); s++)
        {
            var arr = am.SurfaceGetArrays(s);
            foreach (var v in arr[(int)Mesh.ArrayType.Vertex].AsVector3Array()) r2 = Mathf.Max(r2, v.DistanceSquaredTo(c));
        }
        return Mathf.Sqrt(r2);
    }

    static ulong FormatKey(ArrayMesh am, int s) => (ulong)am.SurfaceGetFormat(s) & 0x7FFFF;   // the array bits and flags that make surfaces compatible

    // ---- parts hung on links that the model also carries itself

    /// <summary>
    /// A model can carry its own copy of a part the game hangs on one of its links: seas_carrier's hull has the turret
    /// seas_carrier_turret modelled in (meshes 57 / 58, attribute 0x1c00001f: intact and damaged, no construction
    /// level) on the spot of its link "we", where CSeasCarrier.SetReady puts the real turret. Both were drawn: the hull's
    /// standing still, the linked one turning and shooting. A mesh of the model whose box, seen from the link, is the
    /// box of one of the part's meshes (same size and place within a few cm) is that copy and is left out. Checked for
    /// every part of the composites table (base game and MIRAGE): only the carrier has such copies.
    /// </summary>
    void DropBakedParts(Ctx c, string name, IEnumerable<(string Link, string Gfx)> onLinks)
    {
        var drop = new List<MeshInstance3D>();
        foreach (var (link, gfx) in onLinks)
        {
            if (string.IsNullOrEmpty(link) || string.IsNullOrEmpty(gfx) || gfx.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Root.FindChild("link_" + link, true, false) is not Node3D ln) continue;
            var part = Template(gfx.ToLowerInvariant(), null, true);
            if (part == null) continue;
            // the part's meshes in the link's frame (an attached part keeps only its root's offset: ModelInstance.ToLinkFrame)
            var pm = part.Scene.GetNodeOrNull<Node3D>("Model");
            if (pm == null) continue;
            var boxes = new List<Aabb>();
            foreach (var t in part.Parts)
                if (t.Kind == PartKind.Mesh && part.Scene.GetNodeOrNull(t.Path) is MeshInstance3D mi && mi.Mesh != null)
                    boxes.Add(new Transform3D(Basis.Identity, pm.Position) * RestToRoot(mi, pm) * mi.Mesh.GetAabb());
            if (boxes.Count == 0) continue;
            var toLink = RestToRoot(ln, c.Model).AffineInverse();
            foreach (var (n, t) in c.Tags)
            {
                if (n is not MeshInstance3D mi || t.Kind != PartKind.Mesh || mi.Mesh == null || drop.Contains(mi)) continue;
                var b = toLink * RestToRoot(mi, c.Model) * mi.Mesh.GetAabb();
                if (boxes.Any(p => SameBox(b, p))) drop.Add(mi);
            }
            if (drop.Count > 0) L.Debug(() => $"model {name}: its own copy of {gfx} at \"{link}\" left out ({drop.Count} meshes)");
        }
        foreach (var mi in drop) { c.Tags.Remove(mi); mi.GetParent()?.RemoveChild(mi); mi.Free(); }
    }

    /// <summary>two boxes of the same size at the same place (within 5 % of the size + 15 cm)</summary>
    internal static bool SameBox(Aabb a, Aabb b)
    {
        var tol = b.Size * 0.05f + Vector3.One * 0.15f;
        var dc = (a.GetCenter() - b.GetCenter()).Abs();
        var ds = (a.Size - b.Size).Abs();
        return dc.X <= tol.X && dc.Y <= tol.Y && dc.Z <= tol.Z && ds.X <= tol.X && ds.Y <= tol.Y && ds.Z <= tol.Z;
    }

    /// <summary>ToRoot in the rest pose: a node on a bone (BoneAttachment3D) sits where the bone rests</summary>
    internal static Transform3D RestToRoot(Node3D n, Node? stop)
    {
        var xf = Transform3D.Identity;
        Node? p = n;
        while (p != null && p != stop)
        {
            if (p is BoneAttachment3D ba && ba.GetParent() is Skeleton3D sk && ba.BoneIdx >= 0 && ba.BoneIdx < sk.GetBoneCount())
            {
                xf = sk.GetBoneGlobalRest(ba.BoneIdx) * xf;
                p = sk;
                continue;
            }
            if (p is Node3D p3) xf = p3.Transform * xf;
            p = p.GetParent();
        }
        return xf;
    }

    // ---- merging (fewer draws: a merged part is one MeshInstance3D with a surface per material)

    /// <summary>Skinned meshes of the same skeleton, material, vertex format and attribute: one mesh.</summary>
    static void MergeSkinned(Ctx c)
    {
        var groups = new Dictionary<string, List<MeshInstance3D>>();
        foreach (var (n, t) in c.Tags)
        {
            if (n is not MeshInstance3D mi || mi.Skin == null || mi.Mesh is not ArrayMesh am || am.GetSurfaceCount() != 1) continue;
            var key = mi.GetParent()?.GetInstanceId() + "|" + mi.Skin.GetInstanceId() + "|" + am.SurfaceGetMaterial(0)?.GetInstanceId() + "|" + FormatKey(am, 0) + "|" + t.Attr + "|" + mi.Transform.ToString() + "|" + mi.CastShadow + "|" + t.SeasKeep;
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new();
            l.Add(mi);
        }
        foreach (var list in groups.Values)
        {
            if (list.Count < 2) continue;
            var first = (ArrayMesh)list[0].Mesh;
            var stool = new SurfaceTool();
            foreach (var mi in list) stool.AppendFrom(mi.Mesh, 0, Transform3D.Identity);
            stool.SetMaterial(first.SurfaceGetMaterial(0));
            var am = stool.Commit(null, (ulong)first.SurfaceGetFormat(0) & (ulong)Mesh.ArrayFormat.FlagUse8BoneWeights);
            list[0].Mesh = am;
            for (int i = 1; i < list.Count; i++) { c.Tags.Remove(list[i]); list[i].GetParent().RemoveChild(list[i]); list[i].Free(); }
        }
    }

    /// <summary>
    /// Static (non skinned, non animated) meshes: merged per part signature into the model root's frame, one surface
    /// per material. keep: attribute bits that keep parts apart (a gate's end pieces).
    /// </summary>
    static void MergeStatic(Ctx c, HashSet<string> animatedNodes, uint keep, uint buildKeep = 0)
    {
        var root = c.Model;
        bool Moving(Node n) { for (Node? p = n; p != null && p != root; p = p.GetParent()) if (animatedNodes.Contains(p.Name)) return true; return false; }
        var groups = new Dictionary<string, (PartRef tag, bool shadow, List<(MeshInstance3D mi, int s, Material m)> surfs)>();
        var merged = new List<MeshInstance3D>();
        foreach (var (n, t) in c.Tags.ToList())
        {
            if (n is not MeshInstance3D mi || t.Skinned || t.Kind != PartKind.Mesh || mi.Mesh is not ArrayMesh am) continue;
            if (animatedNodes.Count > 0 && Moving(mi))
            {
                if (t.HasAttr) { int sg0 = PartBits.StaticSig(t.Attr, keep); if (sg0 != PartBits.SigAll) t.Sig = sg0; }
                continue;
            }
            int sig = t.HasAttr ? PartBits.StaticSig(t.Attr, keep) : PartBits.SigAll;     // (a gate's end flags are no epoch bits)
            bool shadow = mi.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off;
            // (the build-up flags keep apart what rises, sinks or stays while the building is built, and the groups that
            // rise together: BuildUp)
            var k = sig + "|" + (t.Arm?.ToString() ?? "") + "|" + (t.VCount > 1 ? t.Variant + "/" + t.VCount : "") + "|" + (int)t.Tod + "|" + (t.Comp ?? "") + "|" + (keep != 0 && t.HasAttr ? (t.Attr & keep).ToString() : "") + "|" + shadow + "|" + t.SeasKeep
                + "|" + (buildKeep != 0 && t.HasAttr ? (t.Attr & buildKeep).ToString() : "");
            if (!groups.TryGetValue(k, out var g)) groups[k] = g = (new PartRef { HasAttr = t.HasAttr, Attr = keep != 0 && t.HasAttr ? t.Attr & (keep | buildKeep) : t.Attr, Sig = sig != PartBits.SigAll ? sig : -1, Tod = t.Tod, Arm = t.Arm, Variant = t.Variant, VCount = t.VCount, Comp = t.Comp, SeasKeep = t.SeasKeep }, shadow, new());
            for (int s = 0; s < am.GetSurfaceCount(); s++) g.surfs.Add((mi, s, am.SurfaceGetMaterial(s)));
            merged.Add(mi);
        }
        int idx = 0;
        foreach (var (tag, shadow, surfs) in groups.Values)
        {
            var am = new ArrayMesh();
            // one surface per material and vertex format
            foreach (var byMat in surfs.GroupBy(x => (x.m, FormatKey((ArrayMesh)x.mi.Mesh, x.s))))
            {
                var stool = new SurfaceTool();
                foreach (var (mi, s, _) in byMat) stool.AppendFrom(mi.Mesh, s, ToRoot(mi, root));
                stool.SetMaterial(byMat.Key.m);
                stool.Commit(am);
            }
            var outMi = new MeshInstance3D { Mesh = am, Name = $"merged_{idx++}", CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off };
            root.AddChild(outMi);
            c.Tags[outMi] = tag;
        }
        foreach (var mi in merged)
        {
            c.Tags.Remove(mi);
            var p = mi.GetParent();
            // (a mesh node with children - links - keeps an empty node in its place)
            if (mi.GetChildCount() > 0) { var keepNode = new Node3D { Name = mi.Name, Transform = mi.Transform }; p.AddChild(keepNode); foreach (var ch in mi.GetChildren()) { mi.RemoveChild(ch); keepNode.AddChild(ch); } }
            p.RemoveChild(mi); mi.Free();
        }
    }

    /// <summary>
    /// Wall pieces are hubs with arms in 8 directions (docs/spec/walls.md §2): the engine shows the arms towards
    /// connected neighbours. Every mesh gets an arm = direction 0..7 (0 = +x, 1 = +x -z, 2 = -z ... the world directions
    /// of wallmap.js DIRS) or -1 for the hub; the building shows the arms of its mask (PartStates.ApplyState).
    /// </summary>
    static void TagWallArms(Ctx c)
    {
        var root = c.Model;
        // the exporter names the parts (pwexport/gsf.py WALL_PARTS: node extras "part" = "3bD"): the engine shows parts by
        // name (BuildName), so a mesh only needs its component ("3b", "x1"); its variants A..P are building / damage states
        bool named = false;
        foreach (var (n, t) in c.Tags)
        {
            if (n is not MeshInstance3D) continue;
            for (Node? p = n; p != null && p != c.Root; p = p.GetParent())
                if (Extra(p, "part") is Variant v) { var s = v.AsString(); t.Comp = s.Length > 0 ? s.Substring(0, s.Length - 1) : s; named = true; break; }
        }
        // the emitters (chunk type 2: the smoke "G"/"H" and fire "I"/"J" of the damage stages) belong to their component
        // too (the engine's part mask holds them like the meshes). Untagged, every piece sprayed the emitters of all the
        // archive's components - 98 smoke and 92 fire emitters on a damaged MIRAGE SEAS wall piece (hs_re_enforced_wall),
        // 81 / 75 on a palisade: a ball of smoke far larger than the wall (Kacper 2026-11-12, mission 13)
        if (named)
            foreach (var (n, t) in c.Tags)
                if (t.Kind == PartKind.Particles)
                    // (an emitter of no component is never in the part mask the engine builds - hu_palisade's
                    // "x35H", a name the engine never asks for, with no state flags: it smoked on every palisade piece)
                    t.Comp = (Extra(n, "part") is Variant pv ? PartBits.WallComp(pv.AsString()) : null) ?? PartBits.WallComp(n.Name) ?? "-";
        if (named) return;
        // (older exports without part names: arms by angle)
        // Each part of a wall model belongs to the hub (centre post, arm -1) or to one of 8 arms (45 degree bins, GSF
        // model space). The models carry several geometry variants of every piece (palisade arms of different heights,
        // 11 hub posts) with identical flags; the engine shows one of them per tile, never all of them stacked (that
        // made the wall top jagged). Variants = parts of the same arm, state flags and footprint.
        var entries = new List<(MeshInstance3D mi, PartRef t, Aabb box, int arm)>();
        foreach (var (n, t) in c.Tags)
        {
            if (n is not MeshInstance3D mi || mi.Mesh == null) continue;
            var box = ToRoot(mi, root) * mi.Mesh.GetAabb();
            var ce = box.GetCenter();
            // the arm's direction in the world at rotation 0 (wallmap.js DIRS: 0 = +x, 2 = -z): the root mirrors GSF x
            // (GSF (x, y) -> world (x, z) = (-x, -y)), so the angle from +x towards -z is atan2(y, -x)
            int arm = Mathf.Sqrt(ce.X * ce.X + ce.Y * ce.Y) < 1.2f ? -1 : (((int)Math.Round(Math.Atan2(ce.Y, -ce.X) / (Math.PI / 4)) % 8) + 8) % 8;
            entries.Add((mi, t, box, arm));
        }
        static float R(float v) => Mathf.Round(v * 2) / 2;
        foreach (var g in entries.GroupBy(e => e.arm + "|" + (e.t.Attr >> 5) + "|" + R(e.box.Position.X) + "," + R(e.box.Position.Y) + "," + R(e.box.End.X) + "," + R(e.box.End.Y)))
        {
            // variants sorted by height: the k-th variant of opposite arms then has the same height, so a segment whose
            // two halves use the same pick (same edge) has an even top
            var list = g.OrderBy(e => e.box.End.Z).ThenBy(e => e.box.Position.Z).ToList();
            for (int k = 0; k < list.Count; k++) { list[k].t.Arm = list[k].arm; list[k].t.Variant = k; list[k].t.VCount = list.Count; }
        }
    }
}
