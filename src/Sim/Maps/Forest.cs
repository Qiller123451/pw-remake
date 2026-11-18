// Forest blocks of original maps: the trees a map stores as 32 m squares of forest instead of placed objects
// (toolkit docs/MAP_FORMAT.md "Forest blocks"; the Python twin is pwexport/forest.py).
//
// The map's Frst chunk has one record per forest square: 31 item bytes (15 trees, 16 undergrowth plants; bits 5-6 =
// state, 2 = standing; bits 0-4 = amount). Where the items stand is not in the map: the engine picks one of 32
// built-in layouts per square. forest.json (pipeline step "forest") holds that table, copied from the player's own
// PWServer.exe / PWClient.exe, and the tree kinds of every setting (Forest_<Setting>.txt).
//
//   ForestData.Load(json)                  forest.json, or null (then maps have no forest trees)
//   data.ForestItems(md, heightAt) -> {Trees: [{X, Y, Rot, Kind, Amount}], Deco: [{X, Y, Kind}]}   map coordinates
//   data.ForestKinds(setting) -> {Trees: [{Standard, Stump, Timber} | null x 5], Deco: [model | null x 8]} | null
// (The JS keeps the table in a module variable set by setForestData; here it is an object the caller keeps.)
// Ported from remake/src/game/maps/forest.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

public sealed class ForestTreeKind
{
    public string Standard = "";
    public string? Stump, Timber;
}

/// <summary>the tree and undergrowth models of a setting's forest blocks (Forest_&lt;Setting&gt;.txt)</summary>
public sealed class ForestKindSet
{
    /// <summary>5 tree kinds (null = none)</summary>
    public List<ForestTreeKind?> Trees = new();
    /// <summary>8 undergrowth models (null = none)</summary>
    public List<string?> Deco = new();
}

public readonly record struct ForestTree(double X, double Y, double Rot, int Kind, int Amount);
public readonly record struct ForestDeco(double X, double Y, int Kind);

public sealed class ForestItemList
{
    public List<ForestTree> Trees = new();
    public List<ForestDeco> Deco = new();
}

public static class Forest
{
    public const int CELL = 32, TREES = 15, ITEMS = 31, PATTERNS = 32, PWORDS = 156;
    /// <summary>the engine shows nothing of a forest block at or below the water level</summary>
    public const double FOREST_MIN_HEIGHT = 16;

    /// <summary>Frst chunk -> {W, H, Blocks: [{X, Y, B (31 bytes)}]}</summary>
    public static ForestMap ParseForest(ByteView? bv)
    {
        var output = new ForestMap();
        if (bv is not ByteView b || b.Length < 12) return output;
        uint w = b.U32(0), h = b.U32(4);
        if (!(w > 0 && w <= 4096 && h > 0 && h <= 4096)) return output;
        output.W = (int)w; output.H = (int)h;
        for (int o = 8; o + 4 <= b.Length;)
        {
            uint i = b.U32(o); o += 4;
            if (i == 0xffffffff || o + 32 > b.Length) break;
            if (i < w * h) output.Blocks.Add(new ForestBlock { X = (int)(i % w), Y = (int)(i / w), B = b.Sub(o + 1, o + 32).ToArray() });
            o += 32;
        }
        return output;
    }
}

/// <summary>forest.json: the 32 built-in layouts of a forest square and the tree kinds of every setting.</summary>
public sealed class ForestData
{
    static readonly Logger L = Log.For("Maps.Forest");

    readonly uint[] _words;
    readonly float[] _floats;
    readonly Dictionary<string, ForestKindSet> _settings;

    ForestData(uint[] words, float[] floats, Dictionary<string, ForestKindSet> settings) { _words = words; _floats = floats; _settings = settings; }

    /// <summary>forest.json (its JSON), or null when it is missing or broken (then maps have no forest trees)</summary>
    public static ForestData? Load(JsonNode? json)
    {
        if (json is not JsonObject o || o["patterns"] is not JsonValue pv) return null;
        try
        {
            var bin = Convert.FromBase64String(pv.GetValue<string>());
            if (bin.Length != Forest.PATTERNS * Forest.PWORDS * 4) return null;
            var words = new uint[bin.Length / 4];
            var floats = new float[bin.Length / 4];
            Buffer.BlockCopy(bin, 0, words, 0, bin.Length);                 // (little endian, as the JS typed arrays on PCs)
            Buffer.BlockCopy(bin, 0, floats, 0, bin.Length);
            var settings = new Dictionary<string, ForestKindSet>();
            if (o["settings"] is JsonObject so)
                foreach (var (name, node) in so)
                {
                    var k = new ForestKindSet();
                    if (node?["trees"] is JsonArray ta)
                        foreach (var t in ta)
                            k.Trees.Add(t is JsonObject to ? new ForestTreeKind { Standard = to["standard"].Str(), Stump = to["stump"]?.Str(), Timber = to["timber"]?.Str() } : null);
                    if (node?["deco"] is JsonArray da) foreach (var d in da) k.Deco.Add(d == null ? null : d.Str());
                    settings[name] = k;
                }
            return new ForestData(words, floats, settings);
        }
        catch (Exception e) { L.Warn($"forest.json unreadable: {e.Message}"); return null; }
    }

    public static ForestData? LoadFile(string path) => Load(Json.TryParseFile(path));

    public ForestKindSet? ForestKinds(string setting) => _settings.TryGetValue(setting, out var k) ? k : null;

    // the layout the engine uses for square (cx, cy)
    // (squares of an extended map's new land can have negative indices - MapExtension: the index stays in the table)
    int PatternOf(int cx, int cy) { long k = (2317L * cy + 13L * cx) % _words.Length; if (k < 0) k += _words.Length; return (int)(_words[(int)k] & 31); }

    /// <summary>every standing tree and undergrowth plant of the map's forest blocks. heightAt(x, y) = terrain height in
    /// map coordinates: nothing stands at or below 16 m (under water).</summary>
    public ForestItemList ForestItems(MapData md, Func<double, double, double>? heightAt)
    {
        var output = new ForestItemList();
        if (md.Forest == null || md.Forest.Blocks.Count == 0) return output;
        foreach (var blk in md.Forest.Blocks)
        {
            int p = PatternOf(blk.X, blk.Y) * Forest.PWORDS;
            for (int i = 0; i < Forest.ITEMS; i++)
            {
                int v = blk.B[i], state = (v >> 5) & 3;
                if (state != 2 && state != 3) continue;
                double x = blk.X * Forest.CELL + (double)_floats[p + 1 + i], y = blk.Y * Forest.CELL + (double)_floats[p + 32 + i];
                if (heightAt != null && heightAt(x, y) <= Forest.FOREST_MIN_HEIGHT) continue;
                uint r = _words[p + 94 + i];
                if (i < Forest.TREES)
                {
                    if ((v & 31) == 0) continue;
                    output.Trees.Add(new ForestTree(x, y, -(double)((r >> 4) & 7) * Math.PI / 4, (int)(((r >> 1) & 0x3fffffff) % 5), v & 31));
                }
                else output.Deco.Add(new ForestDeco(x, y, (int)(r & 7)));
            }
        }
        return output;
    }
}
