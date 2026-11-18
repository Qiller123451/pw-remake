// Access to the original HUD texture atlas (UI/All_def.txt converted by the toolkit to assets/ui/atlas.json): name ->
// level -> [file, x, y, w, h]. The portraits of the dialogue box and the cutscene lines (the card sheets
// hud_cards_cards_*_lvl*.png), the news ticker's icons, the timers' pills.
// No Godot types: the tests link this file.
// Ported from remake/src/ui/atlas.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.UI.Mission.Logic;

public readonly record struct AtlasEntry(string File, int X, int Y, int W, int H);

public static class UiAtlas
{
    static readonly Dictionary<string, Dictionary<string, AtlasEntry>> s_atlas = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The card name the tech tree gives a class (atlas.js setCardNames), or null.</summary>
    public static Func<string, string?> CardName = _ => null;

    public static int Count => s_atlas.Count;

    /// <summary>Loads the atlas; further files (the sequel's cards, assets/ui/atlas_sequel.json written by the prepare
    /// tool when the data lacks them) are merged over it, entry by entry.</summary>
    public static void Load(string? path, params string?[] extra)
    {
        s_atlas.Clear();
        Merge(path);
        foreach (var x in extra) Merge(x);
    }

    static void Merge(string? path)
    {
        if (path == null || Json.TryParseFile(path) is not JsonObject root) return;
        foreach (var (name, levels) in root)
        {
            if (levels is not JsonObject lv) continue;
            var d = new Dictionary<string, AtlasEntry>(StringComparer.Ordinal);
            foreach (var (l, e) in lv)
                if (e is JsonArray a && a.Count >= 5) d[l] = new AtlasEntry(a[0].Str(), a[1].Int(), a[2].Int(), a[3].Int(), a[4].Int());
            s_atlas[name] = d;
        }
    }

    /// <summary>Best entry for a name: the requested level, else the largest available.</summary>
    public static AtlasEntry? Entry(string? name, int? level = null)
    {
        if (string.IsNullOrEmpty(name) || !s_atlas.TryGetValue(name, out var e)) return null;
        if (level != null && e.TryGetValue(level.Value.ToString(), out var x)) return x;
        AtlasEntry? best = null;
        foreach (var v in e.Values) if (best == null || v.W * v.H > best.Value.W * best.Value.H) best = v;
        return best;
    }

    /// <summary>Unit / building portrait icon name for an object (atlas keys follow the tech tree names).</summary>
    public static string? IconFor(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (Entry(name) != null) return name;
        var b = Regex.Replace(Regex.Replace(name, @"_s\d$", ""), @"_\d$", "");
        if (Entry(b) != null) return b;
        var card = CardName(name);
        return card != null && Entry(card) != null ? card : null;
    }
}
