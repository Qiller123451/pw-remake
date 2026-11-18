// Biomes: a remake upgrade over the original (docs/BIOMES.md). The original gives a whole map one landscape setting
// (Jungle, Northland ...: its eight ground textures, grass, ambience); the remake lets a map mix any number of
// **biomes** with a soft, noisy transition - a frost map whose east half turns into jungle.
//
//   Biome       a named bundle: its 8 ground materials (assets/terrain/<Ground>/scape_k.jpg) with a role each (land,
//               rock, shore ...), its grass atlas and grass density per material, its ambience template, and optional
//               light / fog / sky factors and weather factors. The original settings are the first biomes, one per
//               setting, with exactly the look the remake gave them before (BiomeSet.Original).
//   BiomeSet    every biome there is: game/maps/biomes.json (made from BiomeSet.Original + new ones), Default
//   BiomeLayer  a map's biome weights: one byte per biome per 8 m cell (map coordinates), the map's extension file
//               <map>.biomes.json next to the .ula or inside a remake map (MapFiles: .pwmap.json)
//   GroundLayers  the ground's texture layers of a map: every biome's 8 materials; the map's own 4 m material cells
//               (indices into its setting's 8) turned into each biome's material of the same role, times the biome
//               weight - the terrain mesh keeps the strongest 8 per vertex (LayerPick)
//
// Biomes are only drawn and heard: the simulation does not read them (the world keeps the map's setting for its wood
// logs and resource models), so a map with a biome layer plays exactly as the same map without it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

/// <summary>light, fog and sky of a biome as factors of the renderer's own (1 = unchanged; WorldRenderer)</summary>
public sealed class BiomeLook
{
    /// <summary>the sun's colour, the sky light's (hemisphere: sky and ground colour), the sky's / fog's colour (RGB factors)</summary>
    public double[] Sun = { 1, 1, 1 }, Ambient = { 1, 1, 1 }, Sky = { 1, 1, 1 };
    /// <summary>the depth fog's distances (start and end) times this</summary>
    public double Fog = 1;

    public static readonly BiomeLook Neutral = new();

    /// <summary>the weighted mix of several looks (weights summing to 1; null = neutral)</summary>
    public static BiomeLook Mix(IReadOnlyList<BiomeLook?> looks, IReadOnlyList<double> w)
    {
        var r = new BiomeLook { Sun = new double[3], Ambient = new double[3], Sky = new double[3], Fog = 0 };
        double t = 0;
        for (int b = 0; b < looks.Count && b < w.Count; b++)
        {
            if (w[b] <= 0) continue;
            var l = looks[b] ?? Neutral;
            for (int c = 0; c < 3; c++) { r.Sun[c] += l.Sun[c] * w[b]; r.Ambient[c] += l.Ambient[c] * w[b]; r.Sky[c] += l.Sky[c] * w[b]; }
            r.Fog += l.Fog * w[b];
            t += w[b];
        }
        if (t <= 0) return Neutral;
        for (int c = 0; c < 3; c++) { r.Sun[c] /= t; r.Ambient[c] /= t; r.Sky[c] /= t; }
        r.Fog /= t;
        return r;
    }

    public bool IsNeutral => Fog == 1 && Sun.All(v => v == 1) && Ambient.All(v => v == 1) && Sky.All(v => v == 1);
}

public sealed class Biome
{
    public string Id = "", Name = "";
    /// <summary>the texture folder of the ground materials: assets/terrain/&lt;Ground&gt;/scape_0..7.jpg</summary>
    public string Ground = "Jungle";
    /// <summary>what each of the 8 materials is (land: the main open ground, land2, dry, dirt, earth, seabed: under the
    /// sea, stones, rock: the steep, path, shore, snow, ash, lava, tiles): another biome's material k becomes this biome's
    /// material of the same role (BiomeSet.Remap)</summary>
    public string[] Roles = new string[8];
    /// <summary>the grass atlas (below assets/terrain/; null: no grass) and how much grass grows on each material (0..1)</summary>
    public string? Grass;
    public double[] GrassDensity = new double[8];
    /// <summary>the ambience template (Audio/AtmoTemplates: "jungle"; null: silence)</summary>
    public string? Ambience;
    /// <summary>light, fog and sky (null: the renderer's own)</summary>
    public BiomeLook? Look;
    /// <summary>the map's weather where this biome is: snow and rain intensity times these</summary>
    public double Snow = 1, Rain = 1;
    /// <summary>snow lying on the buildings and the built scenery where this biome is (0..1: how much of their upward
    /// faces; the remake's: docs/BIOMES.md "Snow on buildings") and the ground texture it is drawn with (below
    /// assets/terrain/; null: the Icewaste's snow, Icewaste/scape_0.jpg)</summary>
    public double SnowCover;
    public string? SnowTexture;
    /// <summary>the snow texture when a biome names none: the Icewaste's open snow</summary>
    public const string SNOW_TEXTURE = "Icewaste/scape_0.jpg";
    /// <summary>one of the original settings (its look must stay the remake's)</summary>
    public bool Original;
    /// <summary>the original setting whose building textures the buildings, walls and built scenery take where this
    /// biome dominates (Texture/Buildings/settings/&lt;setting&gt;: SettingTextures, docs/BIOMES.md "Setting textures");
    /// null: the setting of its ground textures (Frost: icewaste, Rainforest: jungle)</summary>
    public string? Setting;
    /// <summary>the setting of the building textures, lower case: Setting, else the ground's folder</summary>
    public string TextureSetting => (Setting ?? Ground).ToLowerInvariant();

    public string Texture(int k) => $"{Ground}/scape_{k}.jpg";
}

public sealed class BiomeSet
{
    static readonly Logger L = Log.For("Maps.Biomes");
    readonly Dictionary<string, Biome> _byId = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<Biome> All = new();
    public string Source = "";

    public Biome? Get(string? id) => id != null && _byId.TryGetValue(id, out var b) ? b : null;
    public void Add(Biome b) { if (_byId.TryGetValue(b.Id, out var old)) All.Remove(old); _byId[b.Id] = b; All.Add(b); }

    // ------------------------------------------------------------------ the original settings
    /// <summary>
    /// The original settings as biomes, with what the remake used for them before biomes (the grass densities were
    /// GrassLayer.GRASS_DENSITY; the ambience of an original map is still the map's own material_atmos). Roles: from
    /// the textures and from what the multiplayer maps use them for (tools/biomes "materials": the most used flat ground
    /// is "land", what lies under the sea "seabed", the steep "rock").
    /// </summary>
    public static BiomeSet Original()
    {
        var s = new BiomeSet { Source = "built-in" };
        void Add(string id, string[] roles, double[]? grass, string? atmo) => s.Add(new Biome
        {
            Id = id, Name = id, Ground = id, Roles = roles, Grass = grass != null ? $"{id}/grass.png" : null,
            GrassDensity = grass ?? new double[8], Ambience = atmo, Original = true,
        });
        Add("Jungle", new[] { "land2", "seabed", "land", "dirt", "rock", "path", "shore", "dry" }, new[] { 0.75, 0.04, 1.0, 0.3, 0.15, 0.0, 0.04, 0.04 }, "jungle");
        Add("Northland", new[] { "land2", "seabed", "land", "dirt", "rock", "path", "earth", "snow" }, new[] { 1.0, 0.05, 0.85, 0.3, 0.04, 0.1, 0.1, 0.0 }, "northland");
        Add("Savanna", new[] { "earth", "seabed", "land", "land2", "rock", "path", "shore", "dirt" }, new[] { 0.12, 0.04, 1.0, 0.5, 0.0, 0.04, 0.04, 0.12 }, "savanna");
        Add("Icewaste", new[] { "land2", "seabed", "dry", "earth", "rock", "path", "land", "snow" }, new[] { 0.0, 0.0, 0.8, 0.08, 0.0, 0.3, 0.0, 0.0 }, "icewaste");
        Add("Ashvalley", new[] { "ash", "earth", "land2", "dirt", "rock", "path", "land", "lava" }, new[] { 0.0, 0.1, 0.9, 0.2, 0.0, 0.05, 0.0, 0.0 }, "ashvalley");
        Add("Cave1", new[] { "earth", "seabed", "land", "path", "rock", "tiles", "shore", "dirt" }, null, null);
        Add("Cave2", new[] { "ash", "tiles", "land2", "dirt", "rock", "earth", "land", "lava" }, null, null);
        Add("Cave3", Enumerable.Repeat("land", 8).ToArray(), null, null);
        s.Get("Icewaste")!.SnowCover = 1;          // (snow on the buildings: the remake's)
        return s;
    }

    // ------------------------------------------------------------------ material roles
    /// <summary>a role missing in a biome: the nearest ones, in order</summary>
    public static readonly Dictionary<string, string[]> RoleFallback = new()
    {
        ["land"] = new[] { "land2", "earth", "dirt" }, ["land2"] = new[] { "land", "dry", "dirt" }, ["dry"] = new[] { "land2", "dirt", "land" },
        ["dirt"] = new[] { "earth", "dry", "path", "land2" }, ["earth"] = new[] { "dirt", "path", "land" }, ["stones"] = new[] { "rock", "path", "earth" },
        ["rock"] = new[] { "stones", "snow", "earth" }, ["path"] = new[] { "earth", "dirt", "stones" }, ["shore"] = new[] { "path", "earth", "land" },
        ["snow"] = new[] { "rock", "stones", "shore" }, ["lava"] = new[] { "rock", "earth" }, ["tiles"] = new[] { "path", "stones" },
        ["seabed"] = new[] { "stones", "shore", "path" }, ["ash"] = new[] { "stones", "rock", "earth" },
    };

    /// <summary>material k of biome a -> the material of biome b with the same role (else the nearest role, else land, else k)</summary>
    public static int[] Remap(Biome a, Biome b)
    {
        var r = new int[8];
        for (int k = 0; k < 8; k++)
        {
            if (ReferenceEquals(a, b) || a.Id == b.Id) { r[k] = k; continue; }
            string role = a.Roles.ElementAtOrDefault(k) ?? "land";
            int m = Array.IndexOf(b.Roles, role);
            if (m < 0 && RoleFallback.TryGetValue(role, out var fb)) foreach (var f in fb) { m = Array.IndexOf(b.Roles, f); if (m >= 0) break; }
            if (m < 0) m = Array.IndexOf(b.Roles, "land");
            r[k] = m < 0 ? k : m;
        }
        return r;
    }

    // ------------------------------------------------------------------ the file
    /// <summary>game/maps next to the running game (or PW_MAPS): biomes.json and the remake's own maps</summary>
    public static string Root() => MapFiles.Root();

    static BiomeSet? s_default;
    /// <summary>game/maps/biomes.json, else the original settings only (read once)</summary>
    public static BiomeSet Default
    {
        get
        {
            if (s_default != null) return s_default;
            var f = Path.Combine(Root(), "biomes.json");
            BiomeSet? s = null;
            try { if (File.Exists(f)) s = Parse(File.ReadAllText(f)); if (s != null) s.Source = f; }
            catch (Exception e) { L.Warn($"{f}: {e.Message} - the original settings only"); }
            s ??= Original();
            // (an original setting the file lacks: built in)
            foreach (var o in Original().All) if (s.Get(o.Id) == null) s.Add(o);
            return s_default = s;
        }
        set => s_default = value;
    }

    static double[] Arr(JsonNode? n, double[] def) => n is JsonArray a ? a.Select(x => x?.GetValue<double>() ?? 0).ToArray() : def;

    public static BiomeSet Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("empty");
        var s = new BiomeSet();
        foreach (var n in root["biomes"]?.AsArray() ?? new JsonArray())
        {
            if (n == null) continue;
            var b = new Biome
            {
                Id = n["id"]?.GetValue<string>() ?? throw new FormatException("a biome without an id"),
                Ground = n["ground"]?.GetValue<string>() ?? "Jungle",
                Roles = (n["roles"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "land").ToArray() ?? Enumerable.Repeat("land", 8).ToArray(),
                Grass = n["grass"]?.GetValue<string>(),
                GrassDensity = Arr(n["grassDensity"], new double[8]),
                Ambience = n["ambience"]?.GetValue<string>(),
                Original = n["original"]?.GetValue<bool>() ?? false,
            };
            b.Name = n["name"]?.GetValue<string>() ?? b.Id;
            if (b.Roles.Length < 8) b.Roles = b.Roles.Concat(Enumerable.Repeat("land", 8 - b.Roles.Length)).ToArray();
            if (b.GrassDensity.Length < 8) b.GrassDensity = b.GrassDensity.Concat(new double[8 - b.GrassDensity.Length]).ToArray();
            if (n["look"] is JsonObject lk)
                b.Look = new BiomeLook { Sun = Arr(lk["sun"], new double[] { 1, 1, 1 }), Ambient = Arr(lk["ambient"], new double[] { 1, 1, 1 }), Sky = Arr(lk["sky"], new double[] { 1, 1, 1 }), Fog = lk["fog"]?.GetValue<double>() ?? 1 };
            if (n["weather"] is JsonObject w) { b.Snow = w["snow"]?.GetValue<double>() ?? 1; b.Rain = w["rain"]?.GetValue<double>() ?? 1; }
            b.SnowCover = Math.Clamp(n["snowCover"]?.GetValue<double>() ?? 0, 0, 1);
            b.SnowTexture = n["snowTexture"]?.GetValue<string>();
            b.Setting = n["setting"]?.GetValue<string>();
            s.Add(b);
        }
        return s;
    }

    /// <summary>indented JSON with the arrays of numbers and strings on one line</summary>
    public static string Compact(string json) => System.Text.RegularExpressions.Regex.Replace(json, @"\[\s*([^\[\]{}]*?)\s*\]",
        m => "[" + System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @",\s+", ", ") + "]");

    public string ToJson()
    {
        static JsonArray A(IEnumerable<double> v) => new(v.Select(x => (JsonNode)JsonValue.Create(Math.Round(x, 4))).ToArray());
        var arr = new JsonArray();
        foreach (var b in All)
        {
            var o = new JsonObject
            {
                ["id"] = b.Id, ["name"] = b.Name, ["ground"] = b.Ground,
                ["roles"] = new JsonArray(b.Roles.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["grass"] = b.Grass, ["grassDensity"] = A(b.GrassDensity), ["ambience"] = b.Ambience,
            };
            if (b.Look != null) o["look"] = new JsonObject { ["sun"] = A(b.Look.Sun), ["ambient"] = A(b.Look.Ambient), ["sky"] = A(b.Look.Sky), ["fog"] = b.Look.Fog };
            if (b.Snow != 1 || b.Rain != 1) o["weather"] = new JsonObject { ["snow"] = b.Snow, ["rain"] = b.Rain };
            if (b.SnowCover != 0) o["snowCover"] = b.SnowCover;
            if (b.SnowTexture != null) o["snowTexture"] = b.SnowTexture;
            if (b.Setting != null) o["setting"] = b.Setting;
            if (b.Original) o["original"] = true;
            arr.Add(o);
        }
        var root = new JsonObject { ["format"] = "pw-biomes", ["version"] = 1, ["biomes"] = arr };
        return Compact(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// A map's biome weights: a grid of Cell-metre cells (8 m) in map coordinates from (X0, Y0), one byte (0..255) per
/// biome per cell; read bilinearly between the cell centres and normalised. The file (&lt;map&gt;.biomes.json, or the
/// "biomes" of a remake map):
/// { "format": "pw-biome-layer", "version": 1, "biomes": ["Frost", "Rainforest"], "cell": 8, "x0": 0, "y0": 0,
///   "w": 128, "h": 128, "weights": "&lt;base64 of w x h x biomes bytes, row by row, the biomes of a cell together&gt;",
///   "remap": { "Rainforest": [2, 1, 0, 3, 4, 5, 6, 4] } }   (optional: the map's material k -> that biome's material)
/// </summary>
public sealed class BiomeLayer
{
    public List<string> Biomes = new();
    public double Cell = 8, X0, Y0;
    public int W, H;
    public byte[] Weights = Array.Empty<byte>();
    public Dictionary<string, int[]> Remap = new(StringComparer.OrdinalIgnoreCase);
    public int N => Biomes.Count;

    /// <summary>a layer over map rectangle (x0, y0) .. (x0 + w, y0 + h) metres, every cell 100 % biome 0</summary>
    public static BiomeLayer Create(IEnumerable<string> biomes, double x0, double y0, double w, double h, double cell = 8)
    {
        var l = new BiomeLayer { Biomes = biomes.ToList(), Cell = cell, X0 = x0, Y0 = y0, W = Math.Max(1, (int)Math.Ceiling(w / cell)), H = Math.Max(1, (int)Math.Ceiling(h / cell)) };
        l.Weights = new byte[l.W * l.H * l.N];
        for (int c = 0; c < l.W * l.H; c++) l.Weights[c * l.N] = 255;
        return l;
    }

    /// <summary>a layer covering a map's terrain</summary>
    public static BiomeLayer For(MapData md, IEnumerable<string> biomes, double cell = 8) => Create(biomes, md.OffX, md.OffY, md.Hx * 2, md.Hy * 2, cell);

    /// <summary>the biome weights at map (mx, my): bilinear between cell centres, clamped at the edges, sum 1</summary>
    public void At(double mx, double my, double[] w)
    {
        int n = N;
        Array.Clear(w, 0, Math.Min(w.Length, n));
        double fx = Math.Clamp((mx - X0) / Cell - 0.5, 0, W - 1), fy = Math.Clamp((my - Y0) / Cell - 0.5, 0, H - 1);
        int i = Math.Min(W - 2, (int)Math.Floor(fx)), j = Math.Min(H - 2, (int)Math.Floor(fy));
        if (i < 0) i = 0; if (j < 0) j = 0;
        double u = W > 1 ? fx - i : 0, v = H > 1 ? fy - j : 0;
        int i1 = Math.Min(W - 1, i + 1), j1 = Math.Min(H - 1, j + 1);
        double t = 0;
        void Tap(int ci, int cj, double k)
        {
            if (k <= 0) return;
            int o = (cj * W + ci) * n;
            for (int b = 0; b < n; b++) { double v2 = Weights[o + b] * k; w[b] += v2; t += v2; }
        }
        Tap(i, j, (1 - u) * (1 - v)); Tap(i1, j, u * (1 - v)); Tap(i, j1, (1 - u) * v); Tap(i1, j1, u * v);
        if (t <= 0) { w[0] = 1; return; }
        for (int b = 0; b < n; b++) w[b] /= t;
    }

    public double[] At(double mx, double my) { var w = new double[N]; At(mx, my, w); return w; }

    /// <summary>the cell's weights as 0..1 (sum 1)</summary>
    public double[] CellWeights(int i, int j)
    {
        var r = new double[N]; double t = 0; int o = (j * W + i) * N;
        for (int b = 0; b < N; b++) { r[b] = Weights[o + b]; t += r[b]; }
        if (t <= 0) { r[0] = 1; return r; }
        for (int b = 0; b < N; b++) r[b] /= t;
        return r;
    }

    /// <summary>sets a cell's weights (any scale: normalised to bytes summing to ~255)</summary>
    public void SetCell(int i, int j, double[] w)
    {
        double t = 0; for (int b = 0; b < N; b++) t += Math.Max(0, w[b]);
        int o = (j * W + i) * N;
        if (t <= 0) { for (int b = 0; b < N; b++) Weights[o + b] = (byte)(b == 0 ? 255 : 0); return; }
        for (int b = 0; b < N; b++) Weights[o + b] = (byte)Math.Round(Math.Max(0, w[b]) / t * 255);
    }

    /// <summary>adds a biome (weight 0 everywhere); its index</summary>
    public int AddBiome(string id)
    {
        int k = Biomes.FindIndex(b => string.Equals(b, id, StringComparison.OrdinalIgnoreCase));
        if (k >= 0) return k;
        int n = N, n2 = n + 1;
        var nw = new byte[W * H * n2];
        for (int c = 0; c < W * H; c++) Array.Copy(Weights, c * n, nw, c * n2, n);
        Biomes.Add(id); Weights = nw;
        return n;
    }

    /// <summary>is every cell 100 % of one biome (the same one)</summary>
    public int? Uniform()
    {
        int? only = null;
        for (int c = 0; c < W * H; c++)
            for (int b = 0; b < N; b++)
                if (Weights[c * N + b] > 0) { if (only == null) only = b; else if (only != b) return null; }
        return only;
    }

    // ------------------------------------------------------------------ the file
    public static string SidecarOf(string mapPath) => Path.Combine(Path.GetDirectoryName(mapPath) ?? "", Path.GetFileNameWithoutExtension(mapPath) + ".biomes.json");

    public static BiomeLayer Parse(JsonNode n)
    {
        var l = new BiomeLayer
        {
            Biomes = (n["biomes"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? new List<string>(),
            Cell = n["cell"]?.GetValue<double>() ?? 8, X0 = n["x0"]?.GetValue<double>() ?? 0, Y0 = n["y0"]?.GetValue<double>() ?? 0,
            W = n["w"]?.GetValue<int>() ?? 0, H = n["h"]?.GetValue<int>() ?? 0,
        };
        if (l.N == 0 || l.W <= 0 || l.H <= 0 || l.Cell <= 0) throw new FormatException("a biome layer needs biomes, w, h and cell");
        l.Weights = Convert.FromBase64String(n["weights"]?.GetValue<string>() ?? "");
        if (l.Weights.Length != l.W * l.H * l.N) throw new FormatException($"weights: {l.Weights.Length} bytes, expected {l.W * l.H * l.N}");
        if (n["remap"] is JsonObject rm)
            foreach (var (k, v) in rm)
                if (v is JsonArray a && a.Count == 8) l.Remap[k] = a.Select(x => Math.Clamp(x?.GetValue<int>() ?? 0, 0, 7)).ToArray();
        return l;
    }

    public static BiomeLayer Parse(string json) => Parse(JsonNode.Parse(json) ?? throw new FormatException("empty"));
    public static BiomeLayer Read(string path) => Parse(File.ReadAllText(path));

    public JsonObject ToJsonNode()
    {
        var o = new JsonObject
        {
            ["format"] = "pw-biome-layer", ["version"] = 1,
            ["biomes"] = new JsonArray(Biomes.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()),
            ["cell"] = Cell, ["x0"] = X0, ["y0"] = Y0, ["w"] = W, ["h"] = H,
        };
        if (Remap.Count > 0) { var rm = new JsonObject(); foreach (var (k, v) in Remap) rm[k] = new JsonArray(v.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()); o["remap"] = rm; }
        o["weights"] = Convert.ToBase64String(Weights);
        return o;
    }

    public string ToJson() => ToJsonNode().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    public void Write(string path) => File.WriteAllText(path, ToJson());
}

/// <summary>
/// The ground's texture layers of a map source. Without a biome layer: the source's own textures, and the weights are
/// the source's material weights unchanged (an original map draws as before). With one: biome b's material m is layer
/// 8 b + m; the map's material k at a point counts for every biome b with the biome's weight there, as b's material
/// Remap[b][k].
/// </summary>
public sealed class GroundLayers
{
    public readonly List<Biome> Biomes = new();
    public readonly List<int[]> Remap = new();
    /// <summary>the texture of every layer (below assets/terrain/) and its scale</summary>
    public readonly List<string> Textures = new();
    public readonly List<double[]> Scales = new();
    readonly MapSource _s;
    readonly bool _single;
    /// <summary>no biome layer: the source's own textures and weights</summary>
    public bool Single => _single;
    public int Count => Textures.Count;
    /// <summary>layer -> its biome (index into Biomes)</summary>
    public int BiomeOf(int layer) => Single ? 0 : layer / 8;

    /// <param name="ignoreBiomes">the map's setting alone even when it has a biome layer (a biome's textures are missing)</param>
    public GroundLayers(MapSource S, BiomeSet? set = null, bool ignoreBiomes = false)
    {
        _s = S;
        set ??= BiomeSet.Default;
        var baseBiome = set.Get(S.Setting) ?? BiomeSet.Original().Get(S.Setting) ?? BiomeSet.Original().Get("Jungle")!;
        _single = S.BiomeLayer == null || ignoreBiomes;
        if (_single)
        {
            Biomes.Add(baseBiome);
            Remap.Add(Enumerable.Range(0, 8).ToArray());
            Textures.AddRange(S.Textures);
            Scales.AddRange(S.Scales);
            return;
        }
        foreach (var id in S.BiomeLayer.Biomes)
        {
            var b = set.Get(id) ?? baseBiome;
            Biomes.Add(b);
            Remap.Add(S.BiomeLayer.Remap.TryGetValue(id, out var r) ? r : BiomeSet.Remap(baseBiome, b));
            for (int k = 0; k < 8; k++) { Textures.Add(b.Texture(k)); Scales.Add(S.Scales.Count > 0 ? S.Scales[0] : new[] { 1 / 24.0, 1 / 24.0 }); }
        }
    }

    /// <summary>the layer weights at game (x, z) for the map's material weights mats there (MapSource.Splat with the
    /// painted cells): mats itself without a biome layer</summary>
    public double[] Of(double x, double z, double[] mats)
    {
        if (Single) return mats;
        var bw = _s.BiomeAt!(x, z);
        var L = new double[Count];
        for (int b = 0; b < Biomes.Count; b++)
        {
            double wb = bw[b];
            if (wb <= 0) continue;
            var rm = Remap[b];
            for (int k = 0; k < mats.Length && k < 8; k++) if (mats[k] > 0) L[b * 8 + rm[k]] += mats[k] * wb;
        }
        return L;
    }

    /// <summary>the biome weights at game (x, z) (one biome: [1])</summary>
    public double[] BiomeWeights(double x, double z) => Single ? new[] { 1.0 } : _s.BiomeAt!(x, z);

    /// <summary>how much snow lies on the buildings at game (x, z): the biomes' SnowCover by their weights there</summary>
    public double SnowCover(double x, double z)
    {
        if (Single) return Biomes[0].SnowCover;
        var bw = _s.BiomeAt!(x, z);
        double s = 0, t = 0;
        for (int b = 0; b < Biomes.Count && b < bw.Length; b++) { s += bw[b] * Biomes[b].SnowCover; t += bw[b]; }
        return t > 0 ? s / t : 0;
    }

    /// <summary>the biome with the largest weight at game (x, z) (the first of equal ones): the one whose setting textures
    /// a building there takes (docs/BIOMES.md "Setting textures")</summary>
    public Biome DominantBiome(double x, double z)
    {
        if (Single) return Biomes[0];
        var bw = _s.BiomeAt!(x, z);
        int best = 0;
        for (int b = 1; b < Biomes.Count && b < bw.Length; b++) if (bw[b] > bw[best]) best = b;
        return Biomes[best];
    }

    /// <summary>the setting of the building textures at game (x, z): the dominant biome's (lower case: "icewaste")</summary>
    public string SettingAt(double x, double z) => DominantBiome(x, z).TextureSetting;

    /// <summary>the settings of the map's biomes (lower case, each once)</summary>
    public IReadOnlyList<string> TextureSettings => Biomes.Select(b => b.TextureSetting).Distinct().ToList();

    /// <summary>whether any biome of the map has snow on its buildings</summary>
    public bool AnySnow => Biomes.Exists(b => b.SnowCover > 0);

    /// <summary>the snow texture (below assets/terrain/): the snowiest biome's</summary>
    public string SnowTexture
    {
        get
        {
            Biome? best = null;
            foreach (var b in Biomes) if (b.SnowCover > 0 && (best == null || b.SnowCover > best.SnowCover)) best = b;
            return best?.SnowTexture ?? Biome.SNOW_TEXTURE;
        }
    }

    /// <summary>the grass density of biome b at a point (its materials' densities x the layer weights, / all weights)</summary>
    public double GrassDensity(int b, double[] layers, double[]? dens = null)
    {
        dens ??= Biomes[b].GrassDensity;
        double d = 0, t = 0;
        for (int l = 0; l < layers.Length; l++)
        {
            t += layers[l];
            if (BiomeOf(l) == b) { int m = Single ? l : l - 8 * b; d += layers[l] * (m < dens.Length ? dens[m] : 0); }
        }
        return t > 0 ? d / t : 0;
    }
}

/// <summary>
/// Which texture layers the ground shader blends (TerrainMesh / TerrainShader): a vertex keeps its strongest SLOTS
/// layers; every tile of 8 x 8 cells gets one palette of SLOTS layers that all its vertices refer to (the shader's
/// cost does not grow with the number of biomes: SLOTS samples at most). A tile inside one biome gets that biome's 8
/// materials in order, so neighbouring tiles share it and an original map has one palette everywhere (layers 0..7).
/// </summary>
public static class LayerPick
{
    public const int SLOTS = 8;

    /// <summary>the strongest n layers of w (weight > 0) by weight, strongest first; returns how many</summary>
    public static int Top(IReadOnlyList<double> w, int n, Span<int> idx, Span<float> ws)
    {
        int c = 0;
        for (int l = 0; l < w.Count; l++)
        {
            double v = w[l];
            if (v <= 0) continue;
            if (c == n && v <= ws[c - 1]) continue;
            int p = c < n ? c++ : c - 1;
            while (p > 0 && ws[p - 1] < v) { ws[p] = ws[p - 1]; idx[p] = idx[p - 1]; p--; }
            ws[p] = (float)v; idx[p] = l;
        }
        return c;
    }

    /// <summary>
    /// a tile's palette from the sums of its vertices' layer weights: the strongest SLOTS layers with a weight, padded
    /// with the other layers of the biomes they belong to (then layer 0 ..), sorted ascending. perBiome: the layers of a
    /// biome (8; the random map's single set: its count).
    /// </summary>
    public static int[] Palette(IReadOnlyList<double> sums, int perBiome = 8)
    {
        Span<int> idx = stackalloc int[SLOTS]; Span<float> ws = stackalloc float[SLOTS];
        int c = Top(sums, SLOTS, idx, ws);
        var set = new SortedSet<int>();
        for (int k = 0; k < c; k++) set.Add(idx[k]);
        if (set.Count < SLOTS)
            foreach (var b in set.Select(l => l / perBiome).Distinct().OrderBy(b => b).ToList())
                for (int m = 0; m < perBiome && set.Count < SLOTS; m++) if (b * perBiome + m < sums.Count) set.Add(b * perBiome + m);
        for (int l = 0; set.Count < SLOTS && l < sums.Count; l++) set.Add(l);
        var r = set.ToArray();
        if (r.Length < SLOTS) r = r.Concat(Enumerable.Repeat(r.Length > 0 ? r[0] : 0, SLOTS - r.Length)).ToArray();
        return r;
    }

    /// <summary>packs a palette two layers per float (a + 256 b) for the vertex data (TerrainShader v_pal)</summary>
    public static float[] Pack(int[] pal) => new float[] { pal[0] + 256f * pal[1], pal[2] + 256f * pal[3], pal[4] + 256f * pal[5], pal[6] + 256f * pal[7] };
}
