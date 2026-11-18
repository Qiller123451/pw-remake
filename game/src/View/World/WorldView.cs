// The 3D scene of a map: the ground (terrain mesh with the setting's ground materials), the water, the forests and the
// map's plants, rocks and scenery (instanced props + foliage sprites), the ground grass around the camera, the
// weather, the fog of war - everything of the world that is not a unit or a building of the game. Built from a map
// source (Sim/Maps MapSource: an original .ula map or the random map) and the configuration's converted assets.
//
//   var view = WorldView.Load(source, library, options);    // a Node3D: Terrain, Water, Props, Foliage, Grass, ...
//   parent.AddChild(view);
//   view.Step(dt, camera, cameraTarget, weatherNow);         // every frame
//   view.SetFog(visible, explored, w, h);                    // the local player's fog of war (or view.Fog / FogGrid)
//   world.AdaptTerrain = view.AdaptTerrain;                   // a building put down levels and paints the ground
//   world.ClearVegetation = view.ClearVegetation;             // ... and takes away the plants around it
//   view.RemoveTree(index) / view.AddProp(...) / RemoveProp   // a tree felled, a stump, a fallen log
//
// The world's ground is `Ground` (Sim/Maps HeightField): pass the simulation's own height field to Load (the world and
// the mesh then read the same samples), or let the view make one and give it to the world.
//
// Ported from remake/src/main.js buildWorld (the drawing parts: terrain, skirt, water, props, foliage, the map's trees,
// decoration, stones, bushes, fish, W.adaptTerrain, W.clearVegetation, the grass layer) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.View.Fx;
using PwRemake.View.Materials;
using PwRemake.View.Models;
using PwRemake.View.Terrain;

namespace PwRemake.View.World;

public sealed class WorldViewOptions
{
    public RenderSettings Render = new();
    /// <summary>draw the map's players' objects, animals, wells, flags and items as static models (map viewer; the game
    /// session draws its own entities)</summary>
    public bool MapObjects;
    /// <summary>the model of a class (map objects: Rules.Stats(cls, level).Gfx); null = no map objects</summary>
    public Func<string, int, string?>? ClassModel;
    /// <summary>where no grass grows besides the ground's own rules (the game: blocked nav cells, inside buildings)</summary>
    public Func<double, double, bool>? GrassBlocked;
    /// <summary>the fog of war drawn (false: everything visible, no overlay)</summary>
    public bool Fog = true;
    /// <summary>the ground brushes the simulation levels with (BuiltWorld.Brushes: the original's and the made ones of
    /// BuildingGround); null = the configuration's objbrush.json (the map viewer)</summary>
    public GroundBrushes? Brushes;
}

/// <summary>a tree of the map in the view (props.js handles of main.js's tree resources)</summary>
public sealed class TreeView
{
    public MapTree Src = null!;
    public PropInstance? Prop;
    public List<FoliageHandle> Sprites = new();
    /// <summary>its additive glow sprites (WorldView.AddGlowSprites)</summary>
    public List<Node3D> Glows = new();
    public bool Gone;
}

/// <summary>a plant or decoration of the map, for ClearVegetation (main.js decorList: {x, z, prop, sprites})</summary>
public sealed class DecorView
{
    public double X, Z;
    public PropInstance? Prop;
    public List<FoliageHandle> Sprites = new();
    /// <summary>its additive glow sprites (WorldView.AddGlowSprites)</summary>
    public List<Node3D> Glows = new();
    public bool Gone;
}

public partial class WorldView : Node3D
{
    static readonly PwRemake.Sim.Core.Logger L = Log.For("View.World");

    public MapSource Source { get; private set; } = null!;
    public ModelLibrary Library { get; private set; } = null!;
    public AssetSource Assets => Library.Source;
    public HeightField Ground { get; private set; } = null!;
    public TerrainMesh Terrain { get; private set; } = null!;
    public WaterSurface? Water { get; private set; }
    public PropField Props { get; private set; } = null!;
    public FoliageField Foliage { get; private set; } = null!;
    /// <summary>the light of the unshaded sprites - tree foliage, grass (main.js: foliage.light 0.86, 0.9, 0.8)</summary>
    public static readonly Vector3 SpriteLight = new(0.86f, 0.9f, 0.8f);

    /// <summary>
    /// The foliage and grass in another light (a cutscene's time of day: DayLight): their fixed light times a factor
    /// per channel - the mean of the sun's and the sky light's factors, as they stand for both; null = as always.
    /// (The buildings' own sprites keep theirs: ModelSprites sets it when it makes them.)
    /// </summary>
    public void SetSpriteLight(Color? mul)
    {
        var m = mul ?? Colors.White;
        var l = new Vector3(SpriteLight.X * m.R, SpriteLight.Y * m.G, SpriteLight.Z * m.B);
        Foliage?.SetLight(l);
        foreach (var g in GrassLayers) g.SetLight(l);
    }

    public WeatherFx Weather { get; private set; } = null!;
    /// <summary>the particle emitters of the models (the map's waterfalls, every model instance's fires and smoke:
    /// Library.OnInstance); updated in Step. Level: the "Particles" setting.</summary>
    public Particles Particles { get; private set; } = null!;
    public FogOverlay Fog { get; private set; } = null!;
    public Node3D Objects { get; private set; } = null!;
    public WorldViewOptions Options { get; private set; } = new();
    public readonly List<TreeView> Trees = new();
    public readonly List<DecorView> Decor = new();
    public readonly List<PropInstance?> Stones = new(), Bushes = new(), Fish = new();
    public readonly List<ModelInstance> MapModels = new();
    int _settingEpoch = SettingMaterials.Epoch;
    /// <summary>how long the parts of Load took (ms)</summary>
    public readonly Dictionary<string, double> LoadMs = new();

    Func<double, double, double[]> _baseSplat = null!;
    readonly Dictionary<long, int> _painted = new();
    GroundBrushes? _brushes;
    Random _rnd = new(4242);
    /// <summary>Ground is the simulation's height field (Load's hf): AdaptTerrain only redraws what the simulation levelled</summary>
    bool _sharedGround;
    /// <summary>what AdaptTerrain did, in order (a saved game does it again: game/save.js)</summary>
    public readonly List<(string gfx, double x, double z, double rot)> GroundLog = new();
    public readonly List<(double x, double z, double r)> VegetationLog = new();

    // the terrain texture showing ground material k of the setting (scape_<k>.jpg), else the nearest kind of ground
    static readonly Dictionary<int, int[]> GROUND_NEAR = new()
    {
        [5] = new[] { 6, 3, 4, 1 }, [3] = new[] { 5, 6, 1 }, [2] = new[] { 0 }, [6] = new[] { 5, 3 },
        [4] = new[] { 5, 7 }, [0] = new[] { 2 }, [1] = new[] { 3, 5 }, [7] = new[] { 4, 5 },
    };

    /// <summary>
    /// Builds the view of a map source. lib: the configuration's models (its AssetSource finds the terrain textures);
    /// hf: the world's height field (null: made here from S.Height).
    /// </summary>
    public static WorldView Load(MapSource S, ModelLibrary lib, WorldViewOptions? opts = null, HeightField? hf = null)
    {
        var v = new WorldView { Name = "WorldView", Source = S, Library = lib, Options = opts ?? new WorldViewOptions(), _sharedGround = hf != null };
        v.Build(hf);
        return v;
    }

    void Mark(Stopwatch sw, string what) { LoadMs[what] = sw.Elapsed.TotalMilliseconds; sw.Restart(); }

    void Build(HeightField? hf)
    {
        var S = Source;
        var sw = Stopwatch.StartNew();
        Ground = hf ?? new HeightField(S.Size, S.MeshCell, S.Height);
        Mark(sw, "heightfield");
        // ground textures rebuilt from the original scape atlases (tools/build_terrain.py): the map's setting's, or every
        // biome's of its biome layer (GroundLayers, docs/BIOMES.md)
        Layers = new GroundLayers(S);
        IReadOnlyList<double[]>? scales = Layers.Scales;
        var tex = LoadTerrainImages(Layers.Textures);
        int perBiome = Layers.Single ? Layers.Count : 8;
        if (tex == null && !Layers.Single)
        {
            L.Warn("the biomes' ground textures are missing: the map's own setting only");
            Layers = new GroundLayers(S, null, ignoreBiomes: true);
            scales = Layers.Scales; perBiome = Layers.Count;
            tex = LoadTerrainImages(Layers.Textures);
        }
        if (tex == null)
        {
            tex = LoadTerrainImages(S.FallbackTextures ?? new List<string> { "grass_green.jpg", "grass_yellow.jpg", "dirt.jpg", "rock.jpg" });
            scales = null;
        }
        tex ??= new List<Image> { GrayImage() };
        RefreshSnow();
        Mark(sw, "textures");
        // the map's ground materials, and over them the 4 m cells the ground brush of a building put down painted
        // (AdaptTerrain): a painted cell counts as its material, blended with its neighbours like the map's own cells;
        // then into the biomes' layers (without biomes: the same weights)
        _baseSplat = S.Splat(Ground);
        Terrain = TerrainMesh.Build(Ground, LayerWeights, tex, scales, perBiome);
        AddChild(Terrain);
        Mark(sw, "terrain");
        if (S.Skirt)
        {
            // ground skirt beyond the map edge
            double e = S.Size / 2 - 2;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            st.SetNormal(Vector3.Up);
            float O = 3000, I = (float)e;
            // four quads around the hole (clockwise from above = Godot's front)
            void Quad(float x0, float z0, float x1, float z1)
            {
                st.AddVertex(new Vector3(x0, 0, z0)); st.AddVertex(new Vector3(x1, 0, z0)); st.AddVertex(new Vector3(x1, 0, z1));
                st.AddVertex(new Vector3(x0, 0, z0)); st.AddVertex(new Vector3(x1, 0, z1)); st.AddVertex(new Vector3(x0, 0, z1));
            }
            Quad(-O, -O, O, -I); Quad(-O, I, O, O); Quad(-O, -I, -I, I); Quad(I, -I, O, I);
            var skirt = new MeshInstance3D
            {
                Name = "Skirt", Mesh = st.Commit(), Position = new Vector3(0, (float)(Ground.At(e, 0) - 1), 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.Color8(0x2c, 0x3f, 0x1c), SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled, Roughness = 1 },
            };
            AddChild(skirt);
        }
        if (S.Water is double level)
        {
            var nm = LoadTerrainTexture("water_normal.png", srgb: false);
            Water = WaterSurface.Build(Ground, level, nm);
            AddChild(Water);
        }
        Mark(sw, "water");
        Fog = FogOverlay.Create(S.Size);
        Fog.SetSceneFog(WorldRenderer.Background, WorldRenderer.FogStart, WorldRenderer.FogEnd);
        Fog.On = Options.Fog;
        AddChild(Fog);
        Particles = new Particles(Library.Textures) { FogStart = (float)WorldRenderer.FogStart, FogEnd = (float)WorldRenderer.FogEnd, Level = Options.Render.Particles };
        AddChild(Particles);
        Library.OnInstance = Particles.Track;
        Props = new PropField { Particles = Particles };
        AddChild(Props);
        Foliage = new FoliageField(64);
        Foliage.SetLight(SpriteLight);
        ModelSprites.Light = new Color(0.86f, 0.9f, 0.8f);     // 2D parts of buildings (lanterns) lit like the foliage
        AddChild(Foliage);
        Weather = WeatherFx.Create();
        Weather.Enabled = Options.Render.Weather;
        AddChild(Weather);
        _brushes = Options.Brushes ?? (Assets.Find("assets/terrain/objbrush.json") is string bp ? GroundBrushes.LoadFile(bp) : null);
        BuildContent();
        Mark(sw, "props");
        Props.Build();
        Foliage.Build();
        Mark(sw, "instances");
        BuildGrass();
        Mark(sw, "grass");
        Objects = new Node3D { Name = "MapObjects" };
        AddChild(Objects);
        if (Options.MapObjects && Options.ClassModel != null) BuildMapObjects();
        Mark(sw, "objects");
        L.Info($"{S.Name}: {S.Size} m, {Trees.Count} trees, {Decor.Count} decoration, {Props.Kinds.Count} prop kinds, {Foliage.SpriteCount} sprites, {MapModels.Count} objects ({string.Join(", ", LoadMs.Keys)}: {string.Join(" / ", System.Linq.Enumerable.Select(LoadMs.Values, x => x.ToString("0")))} ms)");
    }

    // ---------------------------------------------------------------- textures

    /// <summary>the ground's layer images (assets/terrain/, flipped and mipmapped as LoadTerrainTexture); null when one is missing</summary>
    List<Image>? LoadTerrainImages(IReadOnlyList<string> names)
    {
        var res = new List<Image>();
        var seen = new Dictionary<string, Image>();
        foreach (var n in names)
        {
            if (seen.TryGetValue(n, out var dup)) { res.Add(dup); continue; }
            var img = LoadTerrainImage(n);
            if (img == null) { L.Warn($"terrain texture missing: assets/terrain/{n}"); return null; }
            res.Add(seen[n] = img);
        }
        return res;
    }

    Image? LoadTerrainImage(string rel)
    {
        var path = Assets.Find("assets/terrain/" + rel);
        if (path == null) return null;
        var img = Image.LoadFromFile(path);
        if (img == null || img.IsEmpty()) return null;
        img.FlipY();
        img.GenerateMipmaps();
        return img;
    }

    /// <summary>
    /// a texture of assets/terrain/ (three.js' TextureLoader flips images vertically - flipY true - and the JS's shaders
    /// are written for it, so the images are flipped here too)
    /// </summary>
    public Texture2D? LoadTerrainTexture(string rel, bool srgb = true)
    {
        var path = Assets.Find("assets/terrain/" + rel);
        if (path == null) return null;
        var img = Image.LoadFromFile(path);
        if (img == null || img.IsEmpty()) return null;
        img.FlipY();
        img.GenerateMipmaps();
        var t = ImageTexture.CreateFromImage(img);
        t.ResourceName = rel;
        return t;
    }

    static Image GrayImage()
    {
        var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgb8);
        img.Fill(new Color(0.4f, 0.42f, 0.3f));
        img.GenerateMipmaps();
        return img;
    }

    // ---------------------------------------------------------------- ground materials

    /// <summary>the ground's texture layers (the setting's materials, or every biome's: docs/BIOMES.md)</summary>
    public GroundLayers Layers { get; private set; } = null!;

    /// <summary>the texture layer weights at (x, z): Splat into the biomes' layers (without biomes: Splat itself)</summary>
    public double[] LayerWeights(double x, double z) => Layers.Of(x, z, Splat(x, z));

    /// <summary>the biome weights at (x, z), one per Layers.Biomes</summary>
    public double[] BiomeWeights(double x, double z) => Layers.BiomeWeights(x, z);

    /// <summary>the snow on the buildings (SnowCover): the biomes' snow cover over the map, the snowiest biome's texture</summary>
    public void RefreshSnow()
    {
        var layers = Layers;
        SnowCover.SetMap(Source.Size, 8, layers.AnySnow ? layers.SnowCover : null, layers.AnySnow ? LoadTerrainImage(layers.SnowTexture) : null);
    }

    /// <summary>the biome layer changed around world x0..x1, z0..z1 (the map viewer's brush): the ground and the grass there</summary>
    public void RefreshBiomes(double x0, double z0, double x1, double z1)
    {
        Terrain.RefreshSplat(x0 - 8, z0 - 8, x1 + 8, z1 + 8);
        RefreshSnow();
        foreach (var g in GrassLayers) g.Invalidate((x0 + x1) / 2, (z0 + z1) / 2, Math.Max(x1 - x0, z1 - z0) / 2 + 16);
    }

    /// <summary>the ground material weights at (x, z) with the painted cells of buildings (main.js splat)</summary>
    public double[] Splat(double x, double z)
    {
        var bs = _baseSplat(x, z);
        if (_painted.Count == 0) return bs;
        double half = Source.Size / 2;
        double fx = (x + half) / 4 - 0.5, fz = (z + half) / 4 - 0.5;
        int i = (int)Math.Floor(fx), j = (int)Math.Floor(fz);
        double u = fx - i, v = fz - j;
        double[]? w = null;
        double rest = 0;
        foreach (var (ci, cj, k) in new[] { (i, j, (1 - u) * (1 - v)), (i + 1, j, u * (1 - v)), (i, j + 1, (1 - u) * v), (i + 1, j + 1, u * v) })
        {
            if (!_painted.TryGetValue((long)ci * 65536 + cj, out int m)) { rest += k; continue; }
            w ??= new double[bs.Length];
            w[m] += k;
        }
        if (w == null) return bs;
        for (int k = 0; k < bs.Length; k++) w[k] += bs[k] * rest;
        return w;
    }

    /// <summary>the texture slot showing ground material k (scape_k.jpg), else the nearest kind of ground; null: none</summary>
    public int? SlotOf(int k)
    {
        var tx = Source.Textures;
        IEnumerable<int> cand = GROUND_NEAR.TryGetValue(k, out var near) ? Prepend(k, near) : new[] { k };
        foreach (var m in cand)
            for (int i = 0; i < tx.Count; i++)
                if (System.IO.Path.GetFileName(tx[i]) == $"scape_{m}.jpg") return i;
        return null;
    }
    static IEnumerable<int> Prepend(int k, int[] rest) { yield return k; foreach (var r in rest) yield return r; }

    /// <summary>
    /// AdaptTerrain (main.js W.adaptTerrain): under a building put down in the game the ground is levelled and takes the
    /// brush's materials (Sim/Maps Ground). gfx: the building's model; heading: its rotation. False: no brush for it.
    /// Set as World.AdaptTerrain.
    /// </summary>
    public bool AdaptTerrain(string gfx, double x, double z, double rot)
    {
        var b = _brushes?.Get(gfx);
        if (b == null) return false;
        // (the simulation's own height field: the simulation has levelled it already - levelling it again would pull
        // the ground a second time, and only where a view draws it)
        var r = _sharedGround ? Sim.Maps.Ground.BrushRect(Ground, b, _brushes!.Mpp, x, z) : Sim.Maps.Ground.LevelGround(Ground, b, _brushes!.Mpp, x, z, rot);
        if (r is SampleRect sr) { Terrain.RefreshHeights(sr); Water?.RefreshDepth(); }
        bool paintedAny = false;
        foreach (var c in Sim.Maps.Ground.BrushCells(b, _brushes.Mpp, x, z, rot, GroundBrushes.MaterialCellOf(Source.Size)))
        {
            var k = SlotOf(c.Material);
            if (k != null) { _painted[(long)c.I * 65536 + c.J] = k.Value; paintedAny = true; }
        }
        double R = Math.Sqrt(b.W * b.W + b.H * b.H) * _brushes.Mpp / 2 + 8;
        if (paintedAny) Terrain.RefreshSplat(x - R, z - R, x + R, z + R);
        GroundLog.Add((gfx, x, z, rot));
        InvalidateGrass(x, z, R);                       // the grass follows the new ground
        return true;
    }

    // ---------------------------------------------------------------- map content

    /// <param name="setting">the decoration's setting (SettingAt): a model with building atlases is a kind per setting
    /// in that setting's textures ("hu_ruin_arena@icewaste"; SettingMaterials)</param>
    PropKind? Kind(string name, bool castShadow = true, string? setting = null)
    {
        if (setting != null && Props.Kinds.TryGetValue(name + "@" + setting, out var ks)) return ks;
        var tpl = Library.Template(name, null, true);
        if (setting != null && tpl != null && PropField.UsesSettings(tpl)) return Props.AddKind(name + "@" + setting, tpl, castShadow, setting);
        if (Props.Kinds.TryGetValue(name, out var k)) return k;
        return Props.AddKind(name, tpl, castShadow);
    }

    /// <summary>the setting whose textures a building, wall or built object at world (x, z) takes: the dominant
    /// biome's (an original map: its setting; docs/BIOMES.md "Setting textures")</summary>
    public string SettingAt(double x, double z) => Layers.SettingAt(x, z);

    /// <summary>
    /// The glows of a map decoration (GSF material flag 0x4 billboards: halos, flames) as the model's own additive
    /// sprites (ModelSprites) at the prop's transform m - the foliage field draws cut-out leaves and showed them as black
    /// squares, turned at random. Only those of the template's default look: the night lights (bit 20) stay off.
    /// </summary>
    List<Node3D> AddGlowSprites(string model, Transform3D m)
    {
        var r = new List<Node3D>();
        var t = Library.Template(model, null, true);
        if (t?.Extras["foliage"] is not System.Text.Json.Nodes.JsonArray fol) return r;
        foreach (var e in fol)
        {
            if (e is not System.Text.Json.Nodes.JsonObject f || !f["glow"].Bool()) continue;
            if (t.Scene.FindChild(f["mesh"].Str() + "_sprites", true, false) is not MultiMeshInstance3D src) continue;
            // (its place in the model; hidden in the default look - a light of the night - it is left out)
            var rel = Transform3D.Identity;
            bool shown = true;
            for (Node? n = src; n != null && n != t.Scene; n = n.GetParent())
                if (n is Node3D n3) { rel = n3.Transform * rel; shown &= n3.Visible; }
            if (!shown) continue;
            var g = new MultiMeshInstance3D
            {
                Name = src.Name, Multimesh = src.Multimesh, MaterialOverride = src.MaterialOverride,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Transform = m * rel,
            };
            GlowSprites ??= new Node3D { Name = "DecorGlows" };
            if (GlowSprites.GetParent() == null) AddChild(GlowSprites);
            GlowSprites.AddChild(g);
            r.Add(g);
        }
        return r;
    }
    Node3D? GlowSprites;

    List<FoliageHandle> AddSprites(string model, double x, double y, double z, double rot, double scale, bool ground = false)
    {
        var handles = new List<FoliageHandle>();
        var t = Library.Template(model, null, true);
        if (t == null) return handles;
        foreach (var (uri, list) in FoliageField.TreeSprites(t.Extras, x, y, z, rot, scale, ground, _rnd))
            if (Foliage.Add(Library.Textures.Get(uri), list) is FoliageHandle h) handles.Add(h);
        return handles;
    }

    static readonly Vector4[] LEAF = { new(0.5f, 0.5f, 0.75f, 0.75f), new(0.75f, 0.5f, 1, 0.75f) }, FRUIT = { new(0.75f, 0.125f, 0.875f, 0.25f), new(0.875f, 0.125f, 1, 0.25f) };
    static readonly Vector4[] BUSH_CELLS = { new(0, 0.25f, 0.25f, 0.5f), new(0.25f, 0.25f, 0.5f, 0.5f), new(0.5f, 0.25f, 0.75f, 0.5f), new(0, 0.5f, 0.25f, 0.75f), new(0.25f, 0.5f, 0.5f, 0.75f), new(0, 0, 0.25f, 0.25f), new(0.25f, 0, 0.5f, 0.25f) };
    static readonly Vector4[] FLOW = { new(0.75f, 0.25f, 0.875f, 0.375f), new(0.875f, 0.25f, 1, 0.375f), new(0.75f, 0.375f, 0.875f, 0.5f), new(0.875f, 0.375f, 1, 0.5f), new(0.75f, 0, 0.875f, 0.125f), new(0.875f, 0, 1, 0.125f) };
    static readonly Regex BushRe = new("^jungle_(bush_|underwood_|mountain_bush)"), FlowerRe = new("^jungle_(flowers|reed|wildrice|beachgrass)");
    static readonly Regex TreeLike = new("tree|palm"), ShadowLike = new("bush|underwood|tree"), NotGround = new("tree|palm|bamboo|deco");

    void BuildContent()
    {
        var S = Source;
        double brA = 99;
        double Brnd() { brA = (brA * 16807) % 2147483647; return brA / 2147483647; }
        // fruit bush: the original sprite layers (leaves + fruit) drawn from jng_misc_a
        var miscTex = Library.Textures.Get("jng_misc_a.png");
        List<FoliageHandle> FruitBushSprites(double x, double y, double z, double rot)
        {
            var ex = Library.Template("jungle_fruit_bush", null, true)?.Extras;
            double s = Math.Sin(rot), c = Math.Cos(rot);
            var list = new List<FoliageSprite>();
            if (ex?["foliage"] is System.Text.Json.Nodes.JsonArray fol)
                foreach (var e in fol)
                {
                    if (e?["pts"] is not System.Text.Json.Nodes.JsonArray p) continue;
                    double size0 = e["size"].Num();
                    bool fruit = size0 <= 2.01;
                    for (int i = 0; i + 2 < p.Count; i += 3)
                    {
                        double lx = -p[i].Num(), ly = p[i + 2].Num(), lz = -p[i + 1].Num();       // GSF -> world: (-x, z, -y)
                        int k = (int)Math.Floor(Brnd() * 2);
                        list.Add(new FoliageSprite
                        {
                            X = (float)(x + lx * c + lz * s), Y = (float)(y + ly + (fruit ? 0.1 : 0)), Z = (float)(z - lx * s + lz * c),
                            Size = (float)(fruit ? 0.55 + Brnd() * 0.3 : size0 * 0.55), Uv = fruit ? FRUIT[k] : LEAF[k], Detail = !fruit && (i / 3) % 2 == 1,
                            Tint = (float)(fruit ? 0.9 : 0.35 + Math.Min(1, ly / 2) * 0.5 + Brnd() * 0.15), Aspect = 1,
                        });
                    }
                }
            // leaves first, then fruit (drawn on top within the chunk)
            list.Sort((a, b) => (a.Size < 1 ? 1 : 0) - (b.Size < 1 ? 1 : 0));
            var h = Foliage.Add(miscTex, list);
            return h != null ? new List<FoliageHandle> { h } : new List<FoliageHandle>();
        }

        foreach (var t in S.Trees)
        {
            var tv = new TreeView { Src = t };
            Trees.Add(tv);
            if (Library.Template(t.Model, null, true) == null) continue;
            double y = Ground.At(t.X, t.Z);
            Kind(t.Model);
            tv.Prop = Props.Add(t.Model, t.X, y, t.Z, t.Rot, t.Scale);
            tv.Sprites = AddSprites(t.Model, t.X, y, t.Z, t.Rot, t.Scale);
        }
        // undergrowth: camera-facing leaf sprites (same technique as the tree canopies)
        var bushTex = Library.Textures.Get("jng_small_a.png");
        foreach (var d in S.Decor)
        {
            double y = d.Y ?? Ground.At(d.X, d.Z);
            if (BushRe.IsMatch(d.Model))
            {
                int n = 6 + (int)Math.Floor(Brnd() * 6);
                double sc = d.Scale * 1.3;
                var list = new List<FoliageSprite>();
                int pal = (int)Math.Floor(Brnd() * 3);    // leaves / ferns / mixed
                for (int i = 0; i < n; i++)
                {
                    double a = Brnd() * 6.283, r = Brnd() * 1.5 * sc, hy = 0.35 + Brnd() * 1.1 * sc;
                    var cell = pal == 0 ? BUSH_CELLS[3 + (int)Math.Floor(Brnd() * 2)] : pal == 1 ? BUSH_CELLS[(int)Math.Floor(Brnd() * 3)] : BUSH_CELLS[(int)Math.Floor(Brnd() * BUSH_CELLS.Length)];
                    list.Add(new FoliageSprite
                    {
                        X = (float)(d.X + Math.Cos(a) * r), Y = (float)(y + hy), Z = (float)(d.Z + Math.Sin(a) * r), Size = (float)((1.3 + Brnd() * 1.1) * sc),
                        Uv = cell, Detail = i >= 5, Tint = (float)(Math.Min(1, hy / (1.5 * sc)) * 0.7 + Brnd() * 0.25), Aspect = 1,
                    });
                }
                var dv = new DecorView { X = d.X, Z = d.Z };
                if (Foliage.Add(bushTex, list) is FoliageHandle h) dv.Sprites.Add(h);
                Decor.Add(dv);
                continue;
            }
            if (FlowerRe.IsMatch(d.Model))
            {
                var list = new List<FoliageSprite>();
                int kindF = (int)Math.Floor(Brnd() * 3);
                for (int i = 0; i < 3; i++)
                {
                    double a = Brnd() * 6.283, r = Brnd() * 0.9;
                    list.Add(new FoliageSprite { X = (float)(d.X + Math.Cos(a) * r), Y = (float)(y + 0.45), Z = (float)(d.Z + Math.Sin(a) * r), Size = (float)(1.4 + Brnd() * 0.6), Uv = LEAF[(int)Math.Floor(Brnd() * 2)], Detail = true, Tint = (float)(0.3 + Brnd() * 0.2), Aspect = 1 });
                }
                for (int i = 0; i < 5; i++)
                {
                    double a = Brnd() * 6.283, r = Brnd() * 1.1;
                    list.Add(new FoliageSprite { X = (float)(d.X + Math.Cos(a) * r), Y = (float)(y + 0.6 + Brnd() * 0.4), Z = (float)(d.Z + Math.Sin(a) * r), Size = (float)(0.6 + Brnd() * 0.35), Uv = FLOW[kindF * 2 + (int)Math.Floor(Brnd() * 2)], Detail = true, Tint = 0.85f, Aspect = 1 });
                }
                var dv = new DecorView { X = d.X, Z = d.Z };
                if (Foliage.Add(miscTex, list) is FoliageHandle h) dv.Sprites.Add(h);
                Decor.Add(dv);
                continue;
            }
            if (Library.Template(d.Model, null, true) == null) continue;
            var dk = Kind(d.Model, d.Block || ShadowLike.IsMatch(d.Model), SettingAt(d.X, d.Z));
            var prop = Props.Add(dk?.Name ?? d.Model, d.X, y, d.Z, d.Rot, d.Scale, d.Q != null ? Frame.MapQuat(d.Q) : null);
            var sprites = AddSprites(d.Model, d.X, y, d.Z, d.Rot, d.Scale, !NotGround.IsMatch(d.Model));
            var glows = prop != null ? AddGlowSprites(d.Model, prop.M) : new List<Node3D>();
            // the sequel's houses, cars and camps stand where the story put them (not plants)
            if (!d.Model.StartsWith("sq_")) Decor.Add(new DecorView { X = d.X, Z = d.Z, Prop = prop, Sprites = sprites, Glows = glows });
        }
        foreach (var s in S.Stones)
        {
            if (s.Model == null || Library.Template(s.Model, null, true) == null) { Stones.Add(null); continue; }
            Kind(s.Model);
            Stones.Add(Props.Add(s.Model, s.X, Ground.At(s.X, s.Z), s.Z, s.Rot, 1));
        }
        foreach (var b in S.Bushes)
        {
            if (b.Model == null || Library.Template(b.Model, null, true) == null) { Bushes.Add(null); continue; }
            double y = Ground.At(b.X, b.Z);
            Kind(b.Model);
            Bushes.Add(Props.Add(b.Model, b.X, y, b.Z, b.Rot, 1));
            if (b.Model == "jungle_fruit_bush") FruitBushSprites(b.X, y, b.Z, b.Rot); else AddSprites(b.Model, b.X, y, b.Z, b.Rot, 1);
        }
        // fish shoals: food in the water (fishing boats); shown as ripples in the water
        foreach (var f in S.Fish)
        {
            if (f.Model == null || Library.Template(f.Model, null, true) == null) { Fish.Add(null); continue; }
            Kind(f.Model);
            Fish.Add(Props.Add(f.Model, f.X, S.Water is double w ? w - 0.3 : Ground.At(f.X, f.Z), f.Z, _rnd.NextDouble() * 6.28, 1));
        }
    }

    /// <summary>
    /// ClearVegetation (main.js W.clearVegetation, the drawing part): a building put down - the decoration, plants and
    /// trees within r go (ServerApp.usl PlaceObj, object types DECO, VGTN, WOOD; fruit bushes and stones stay). The
    /// world depletes its tree resources itself; their props go with RemoveTree.
    /// </summary>
    public void ClearVegetation(double x, double z, double r)
    {
        foreach (var e in Decor)
        {
            if (e.Gone || Math.Sqrt((e.X - x) * (e.X - x) + (e.Z - z) * (e.Z - z)) > r) continue;
            e.Gone = true;
            Props.Remove(e.Prop);
            foreach (var h in e.Sprites) Foliage.Hide(h);
            foreach (var g in e.Glows) g.QueueFree();
        }
        for (int i = 0; i < Trees.Count; i++)
        {
            var t = Trees[i];
            if (!t.Gone && Math.Sqrt((t.Src.X - x) * (t.Src.X - x) + (t.Src.Z - z) * (t.Src.Z - z)) <= r) RemoveTree(i);
        }
        VegetationLog.Add((x, z, r));
        InvalidateGrass(x, z, r);
    }

    /// <summary>a tree of the map felled or cleared (index into Source.Trees): its model and leaves go</summary>
    public void RemoveTree(int index)
    {
        if (index < 0 || index >= Trees.Count) return;
        var t = Trees[index];
        if (t.Gone) return;
        t.Gone = true;
        Props.Remove(t.Prop);
        foreach (var h in t.Sprites) Foliage.Hide(h);
    }

    /// <summary>a prop put down in the game (a stump, a fallen log, a depleted stone): its handle for RemoveProp</summary>
    public PropInstance? AddProp(string model, double x, double z, double rot, double scale = 1, double? y = null)
    {
        if (Kind(model) == null) return null;
        var h = Props.Add(model, x, y ?? Ground.At(x, z), z, rot, scale);
        Props.Build();
        return h;
    }
    public void RemoveProp(PropInstance? h) => Props.Remove(h);

    // ---------------------------------------------------------------- grass

    void BuildGrass()
    {
        var S = Source;
        // per biome of the map (an original map: its setting's): its atlas, density per ground material x the layer
        // weights of its materials; none on cliffs (and where GrassBlocked says). A biome's own clumps (seed): two
        // biomes' grass mixes in the transition.
        for (int b = 0; b < Layers.Biomes.Count; b++)
        {
            var bio = Layers.Biomes[b];
            var dens = S.Kind == "generated" ? new[] { 1.0, 0.75, 0.3, 0.15 } : bio.GrassDensity;
            if (bio.Grass == null || dens.All(d => d <= 0)) continue;
            var gtex = LoadTerrainTexture(bio.Grass);
            if (gtex == null) { L.Warn($"no grass: assets/terrain/{bio.Grass}"); continue; }
            int bi = b;
            var g = GrassLayer.Create(gtex, (x, z) => Ground.At(x, z), (x, z) =>
            {
                double d = Layers.GrassDensity(bi, LayerWeights(x, z), dens);
                if (d <= 0.01 || Ground.NormalY(x, z) < 0.8 || (Options.GrassBlocked?.Invoke(x, z) ?? false)) return 0;
                return d * (Options.Render.Grass == "dense" ? 1 : 0.55);
            }, S.Water, Foliage.Light, seed: b == 0 ? 0 : unchecked(b * 0x5bd1e995));
            g.Name = b == 0 ? "Grass" : "Grass_" + bio.Id;
            g.Visible = Options.Render.Grass != "off";
            AddChild(g);
            GrassLayers.Add(g);
        }
    }

    /// <summary>every grass layer (one per biome with grass); Grass is the first</summary>
    public readonly List<GrassLayer> GrassLayers = new();
    public GrassLayer? Grass => GrassLayers.Count > 0 ? GrassLayers[0] : null;
    /// <summary>the grass clumps drawn now</summary>
    public int GrassCount => GrassLayers.Sum(g => g.Count);
    /// <summary>forget the grass around (x, z) (a building, a felled tree) - or all of it</summary>
    public void InvalidateGrass(double? x = null, double? z = null, double r = 0) { foreach (var g in GrassLayers) g.Invalidate(x, z, r); }

    /// <summary>the grass setting changed (Off / On / Dense)</summary>
    public void SetGrass(string mode)
    {
        Options.Render.Grass = mode;
        foreach (var g in GrassLayers) { g.Visible = mode != "off"; g.Invalidate(); }
    }

    // ---------------------------------------------------------------- the map's objects as static models

    void BuildMapObjects()
    {
        var S = Source;
        var cm = Options.ClassModel!;
        void Put(string? model, double x, double z, double rot, Color? party, double? y = null, double[]? q = null)
        {
            if (model == null) return;
            var m = Library.Instantiate(model.ToLowerInvariant(), party);
            if (m == null) return;
            m.Position = new Vector3((float)x, (float)(y ?? Ground.At(x, z)), (float)z);
            if (q != null) { var mq = Frame.MapQuat(q); m.Quaternion = new Quaternion((float)mq[0], (float)mq[1], (float)mq[2], (float)mq[3]).Normalized(); }
            else m.Rotation = new Vector3(0, (float)rot, 0);
            // a standing clip, so characters and animals do not stand in their bind pose
            var clip = m.Anim?.Pick("standanim", "stand_anim", "idle", "standing", "stand");
            if (clip != null) m.Play(clip, true);
            if (!m.Template.IsDynamic) m.SetSetting(SettingAt(x, z));     // (buildings: their biome's setting textures)
            Objects.AddChild(m);
            MapModels.Add(m);
        }
        foreach (var o in S.Owned)
        {
            var pc = o.Owner is int ow && ow >= 0 ? Materials.PartyColors.All[ow % Materials.PartyColors.All.Length].Light : (Color?)null;
            Put(cm(o.Cls, Math.Max(1, o.Level)), o.X, o.Z, o.Rot, pc);
        }
        foreach (var a in S.Animals) Put(cm(a.Species, 1), a.X, a.Z, _rnd.NextDouble() * 6.28, null);
        foreach (var w in S.Wells) Put(w.Model, w.X, w.Z, w.Rot, null, w.Y, w.Q);
        foreach (var f in S.Flags) Put(f.Model, f.X, f.Z, f.Rot, null);
        foreach (var i in S.Items) Put("item_treasure_chest", i.X, i.Z, i.Rot, null);
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>
    /// every frame: the props in view, the foliage's detail layer, the grass around the camera target, the weather and
    /// the map objects' clips. weather: the world's weather now (null: none).
    /// </summary>
    public void Step(double dt, Camera3D camera, Vector3 target, WeatherState? weather = null)
    {
        var P = PerfProbe.P;
        P?.Begin();
        if (_settingEpoch != SettingMaterials.Epoch)
        {
            // the option "Setting textures on buildings" changed: the decoration and the map's objects again
            _settingEpoch = SettingMaterials.Epoch;
            Props.RefreshSettings();
            foreach (var m in MapModels) m.SetSetting(m.Setting);
        }
        Props.UpdateView(camera);
        P?.Mark("view:props");
        Foliage.UpdateView(camera.GlobalPosition);
        P?.Mark("view:foliage");
        foreach (var g in GrassLayers) g.UpdateView(target);
        P?.Mark("view:grass");
        Weather.Step(weather, target, dt);
        P?.Mark("view:weather");
        foreach (var m in MapModels) m.Advance(dt);
        P?.Mark("view:map models");
        // the emitters last: their nodes are where this frame draws them (particles.js after the render)
        Particles.Update(dt, camera, target);
        P?.Mark("view:particles");
    }

    /// <summary>the fog of war of the local player (FogOverlay.SetFog): per cell 1 = visible now / explored; null = all visible</summary>
    public void SetFog(byte[]? visible, byte[]? explored, int w, int h) => Fog.SetFog(visible, explored, w, h);

    /// <summary>the fog of war from a FogGrid (the JS FogOfWar)</summary>
    public void SetFog(PwRemake.Sim.World.FogGrid g) { Fog.RevealAll = g.RevealAll; Fog.SetFogValues(g.TextureBytes(), g.N, g.N); }
}
