// The original's setting textures on buildings ("setting material", docs/VIEW.md "Setting textures"): the buildings'
// texture atlases repainted per landscape setting (Texture/Buildings/settings/<setting>: snow on the Icewaste's roofs,
// sand on the Savanna's, moss in the Jungle, soot in the Ashvalley; the index: PwRemake.Sim.Content.SettingTextures).
//
// The original gives a whole map one setting. The remake follows a mixed map's biomes: a building, wall or built object
// takes the setting of the biome that dominates where it stands (GroundLayers.SettingAt - each biome has a setting:
// the original settings their own, Frost the Icewaste's, Rainforest the Jungle's); a map of one setting draws as the
// original. Units are not affected (the original has no unit variants; a SEAS vehicle sharing seas_building_a keeps it).
//
// How: a model's materials whose texture has variants carry the key (MaterialTag.SettingKey, ToLambert). An instance
// told its setting (ModelInstance.SetSetting: buildings, their attached parts, the map's decoration and objects) draws
// those surfaces with Variant(material, setting) - one copy per (material, setting), shared by every model of that
// kind there: a village of 40 Norse buildings in the Icewaste adds one material per atlas material, not per building.
// The party colour, SEAS paint and "units behind" copies are made from the variant as from the plain material.
// No variant in that setting (the Dustriders in the Northland, caves, content prepared before the variants): the plain
// material. Option "Setting textures on buildings" (Options, Graphics; AppSettings.SettingTextures, default on,
// applied at once - Epoch): off, every building plain and no shader snow (SnowCover).
using System.Collections.Generic;
using Godot;
using PwRemake.Sim.Content;

namespace PwRemake.View.Materials;

/// <summary>Where a library's setting textures come from: the configuration's index and its texture cache.</summary>
public sealed class SettingSource
{
    public readonly SettingTextures Index;
    public readonly TextureCache Cache;
    public SettingSource(SettingTextures index, TextureCache cache) { Index = index; Cache = cache; }

    /// <summary>the variant texture of an index key in a setting (null: none or unreadable - the plain one is drawn)</summary>
    public Texture2D? Texture(string key, string setting, string? plainName)
    {
        var rel = Index.Variant(key, setting);
        return rel == null ? null : Cache.GetAsset(rel, plainName);
    }
}

public static class SettingMaterials
{
    /// <summary>the option (AppSettings.SettingTextures)</summary>
    public static bool Enabled { get; private set; } = true;
    /// <summary>counts the option's changes (models re-apply their materials when it changed)</summary>
    public static int Epoch { get; private set; }

    static readonly Dictionary<(Material, string), Material> s_variants = new();

    /// <summary>the option: setting textures on, and the shader snow with them (SnowCover)</summary>
    public static void SetEnabled(bool on)
    {
        if (on != Enabled) { Enabled = on; Epoch++; }
        SnowCover.SetEnabled(on);
    }

    /// <summary>whether a material's texture has variants (a building's atlas)</summary>
    public static bool HasVariants(Material? m) => ModelMaterials.Tag(m)?.SettingKey != null;

    /// <summary>
    /// The material with its texture in a setting ("icewaste"): one copy per material and setting, shared. The material
    /// itself when the option is off, there is no setting, its texture has no variant there or the variant cannot be read.
    /// </summary>
    public static Material Variant(Material m, string? setting)
    {
        if (setting == null || !Enabled) return m;
        var tag = ModelMaterials.Tag(m);
        if (tag?.SettingKey == null || tag.Settings == null || tag.Setting != null) return m;
        var key = (m, setting);
        if (s_variants.TryGetValue(key, out var v)) return v;
        v = m;
        var tex = tag.Settings.Texture(tag.SettingKey, setting, tag.Map?.ResourceName);
        if (tex != null && ModelMaterials.WithTexture(m, tex) is Material c)
        {
            var src = tag.Source != null ? (StandardMaterial3D)tag.Source.Duplicate() : null;
            if (src != null) src.AlbedoTexture = tex;
            ModelMaterials.SetTag(c, new MaterialTag
            {
                GsfFlags = tag.GsfFlags, Party = tag.Party, Glow = tag.Glow, Map = tex, Paint = tag.Paint, Building = tag.Building,
                Snow = tag.Snow, Model = tag.Model, Source = src, SettingKey = tag.SettingKey, Settings = tag.Settings, Setting = setting,
            });
            v = c;
        }
        s_variants[key] = v;
        return v;
    }

    /// <summary>the variant materials made so far (the performance line, tests)</summary>
    public static int Count => s_variants.Count;
}
