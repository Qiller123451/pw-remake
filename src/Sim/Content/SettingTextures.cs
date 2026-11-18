// The original's setting textures ("setting material"): ParaWorld ships repainted copies of the buildings' texture
// atlases per landscape setting and swaps them by the map's setting - the Icewaste's with snow on the roofs and ledges,
// the Savanna's sandy and dusty, the Jungle's mossy, the Ashvalley's sooty, the Northland's a little weathered:
//
//   Data/Base/Texture/Buildings/settings/<ashvalley|icewaste|jungle|northland|savanna>/<Aje|Hu|Ninigi>_Lvl_<n>_a_(<size>).dds
//   Data/MIRAGE/Texture/Buildings/settings/<... cave3 ...>/(also Hu_Misc_a, Seas_Building_a, Seas_Tent)
//   Data/MIRAGE/Texture/settings/icewaste/boosterpack_01_a_(1024).dds        (of Texture/boosterpack_01_a)
//
// The rule: the texture <dir>/<name> of a material (the GSF's texture path below Texture/, e.g. "Buildings/hu_lvl_1_a")
// has the variant <dir>/settings/<setting>/<name>. The toolkit's conversion has only the plain textures; the prepare
// tool (tools/prepare/setting_textures.py) converts the variants of the configuration's Data folders (a later folder's
// replacing an earlier's, the largest size) to PNG into the configuration's overrides:
//
//   assets/tex/settings/<setting>/<name>.png
//   assets/tex/settings/index.json   { "format": "pw-setting-textures", "version": 1, "settings": [...],
//                                      "textures": { "buildings/hu_lvl_1_a": { "icewaste": { "file": "settings/icewaste/hu_lvl_1_a.png", ... } } } }
//
// This class is that index, without the engine (tests: SettingTexturesTests): which variant a material's texture has
// in a setting. Content prepared before the variants existed has no index: Empty, every texture plain.
// The view: View/Materials/SettingMaterials.cs; which setting a building takes: GroundLayers.SettingAt (its biome).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Content;

public sealed class SettingTextures
{
    static readonly Logger L = Log.For("Content.Settings");

    /// <summary>the index below the configuration's assets (overrides first: AssetSource)</summary>
    public const string INDEX = "assets/tex/settings/index.json";
    /// <summary>the folder of the variants' files below assets/</summary>
    public const string TEX_DIR = "assets/tex/";

    /// <summary>key ("buildings/hu_lvl_1_a") -> setting ("icewaste") -> file below assets/tex/</summary>
    readonly Dictionary<string, Dictionary<string, string>> _tex = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>the texture's name alone ("hu_lvl_1_a") -> key, for a material without its GSF texture path</summary>
    readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _settings = new();

    public static readonly SettingTextures Empty = new();

    /// <summary>whether there are any variants (false: content without them - every texture plain)</summary>
    public bool Any => _tex.Count > 0;
    public int Count => _tex.Count;
    /// <summary>the settings that have variants (lower case)</summary>
    public IReadOnlyList<string> Settings => _settings;
    public IEnumerable<string> Keys => _tex.Keys;

    /// <summary>the configuration's index; Empty when there is none (old content) or it cannot be read (one warning)</summary>
    public static SettingTextures Load(AssetSource src)
    {
        var p = src.Resolve(INDEX);
        if (p == null)
        {
            L.Info($"no setting textures ({INDEX}; run Prepare.bat): buildings keep their plain textures in every setting");
            return Empty;
        }
        try
        {
            var s = Parse(File.ReadAllText(p));
            L.Info($"setting textures: {s.Count} textures, settings {string.Join(", ", s.Settings)} ({p})");
            return s;
        }
        catch (Exception e)
        {
            L.Warn($"{p}: {e.Message} - buildings keep their plain textures");
            return Empty;
        }
    }

    public static SettingTextures Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("not an object");
        if (root["format"]?.GetValue<string>() is string f && f != "pw-setting-textures") throw new FormatException($"format {f}");
        var s = new SettingTextures();
        if (root["textures"] is JsonObject tex)
            foreach (var (key, v) in tex)
            {
                if (v is not JsonObject per) continue;
                foreach (var (setting, e) in per)
                {
                    // ({"file": ...} or the file name alone)
                    string? file = e is JsonObject eo ? eo["file"]?.GetValue<string>() : e?.GetValue<string>();
                    if (!string.IsNullOrEmpty(file)) s.Add(key, setting, file!);
                }
            }
        return s;
    }

    /// <summary>a variant: texture key (Key), setting, file below assets/tex/</summary>
    public void Add(string key, string setting, string file)
    {
        key = Key(key) ?? key;
        setting = setting.ToLowerInvariant();
        if (!_tex.TryGetValue(key, out var per)) _tex[key] = per = new(StringComparer.OrdinalIgnoreCase);
        per[setting] = PathUtil.Norm(file).TrimStart('/');
        _byName.TryAdd(key.Substring(key.LastIndexOf('/') + 1), key);
        if (!_settings.Contains(setting)) _settings.Add(setting);
    }

    static readonly Regex Sized = new(@"_\(\d+\)$");
    static readonly Regex Archive = new(@"__[^/]*$");

    /// <summary>
    /// A texture path as the index keys it: lower case, '/', no extension, no size suffix "_(0512)":
    /// "Buildings\Hu_Lvl_1_a.tga" -> "buildings/hu_lvl_1_a". null for nothing.
    /// </summary>
    public static string? Key(string? texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath)) return null;
        var k = texturePath!.Trim().Replace('\\', '/').ToLowerInvariant().TrimStart('/');
        if (k.StartsWith("texture/")) k = k.Substring(8);
        int dot = k.LastIndexOf('.');
        if (dot > k.LastIndexOf('/')) k = k.Substring(0, dot);
        return Sized.Replace(k, "");
    }

    /// <summary>
    /// The index key of a material's texture: by its GSF texture path (the material's extras "gsf_texture",
    /// "buildings/hu_lvl_1_a.tga"), else by the converted file's name ("../tex/hu_lvl_1_a.png"; the toolkit names a file
    /// that two archives have differently "x__archive.png"). null: the texture has no variants.
    /// </summary>
    public string? KeyOf(string? gsfTexture, string? file = null)
    {
        if (_tex.Count == 0) return null;
        var k = Key(gsfTexture);
        if (k != null && _tex.ContainsKey(k)) return k;
        if (k != null && _byName.TryGetValue(k.Substring(k.LastIndexOf('/') + 1), out var byPath)) return byPath;
        var n = Key(file);
        if (n == null) return null;
        n = Archive.Replace(n.Substring(n.LastIndexOf('/') + 1), "");
        return _byName.TryGetValue(n, out var kk) ? kk : null;
    }

    /// <summary>whether the texture (an index key) has a variant in the setting</summary>
    public bool Has(string? key, string? setting) =>
        key != null && setting != null && _tex.TryGetValue(key, out var per) && per.ContainsKey(setting);

    /// <summary>
    /// The variant of a texture (an index key) in a setting, as an asset path ("assets/tex/settings/icewaste/hu_lvl_1_a.png");
    /// null: the plain texture (no variant in that setting - the Dustriders in the Northland, any texture in a cave but
    /// MIRAGE's SEAS buildings in cave3 -, a texture without variants, no setting).
    /// </summary>
    public string? Variant(string? key, string? setting)
    {
        if (key == null || setting == null || !_tex.TryGetValue(key, out var per)) return null;
        return per.TryGetValue(setting, out var f) ? TEX_DIR + f : null;
    }

    /// <summary>the variants of a texture: setting -> asset path</summary>
    public IReadOnlyDictionary<string, string> VariantsOf(string? key) =>
        key != null && _tex.TryGetValue(key, out var per) ? per.ToDictionary(kv => kv.Key, kv => TEX_DIR + kv.Value) : new Dictionary<string, string>();
}
