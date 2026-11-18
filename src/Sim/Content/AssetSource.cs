// Where the files of a configuration come from. Everything that is drawn or played is looked up here by a relative
// path, never by an absolute one, so the source can change without touching the game code:
//
//   1. <configuration>/overrides/<path>      own or improved assets (a re-made model, an HD texture, new UI art)
//   2. <data>/<path>                         converted from the original game by the toolkit (models, textures, UI)
//   3. the installation, for sound and music: "snd/<p>" -> Data/<folder>/Audio/Sound/<p>, "music/<f>" -> Audio/Music
//
// When the project has its own assets one day, they go into (1) - or a configuration whose "data" points at a folder
// with the same layout (docs/CONTENT.md) - and nothing else changes.
using System;
using System.Collections.Concurrent;
using System.IO;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Content;

public sealed class AssetSource
{
    static readonly Logger L = Log.For("Content.Assets");
    readonly GameConfiguration _cfg;
    readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AssetSource(GameConfiguration cfg) { _cfg = cfg; }

    public GameConfiguration Config => _cfg;

    /// <summary>Absolute path of a data file (gamedata.json, assets/manifest.json, assets/models/x.glb ...), or null.</summary>
    public string? Resolve(string rel)
    {
        rel = PathUtil.Norm(rel).TrimStart('/');
        return _cache.GetOrAdd(rel, r =>
        {
            var o = Path.Combine(_cfg.OverridesDir, r);
            if (File.Exists(o)) return o;
            if (r.StartsWith("assets/snd/", StringComparison.OrdinalIgnoreCase))
                return _cfg.GameFile("Audio/Sound/" + r.Substring("assets/snd/".Length));
            if (r.StartsWith("assets/music/", StringComparison.OrdinalIgnoreCase))
                return _cfg.GameFile("Audio/Music/" + r.Substring("assets/music/".Length));
            var d = Path.Combine(_cfg.Data, r);
            if (File.Exists(d)) return d;
            return PathUtil.FindCaseInsensitive(_cfg.Data, r);
        });
    }

    public string Require(string rel) =>
        Resolve(rel) ?? throw new FileNotFoundException($"asset not found in configuration '{_cfg.Id}': {rel}");

    /// <summary>Like Resolve, but logs a missing file once.</summary>
    public string? Find(string rel)
    {
        var p = Resolve(rel);
        if (p == null) L.WarnOnce(rel, $"missing asset {rel} (configuration {_cfg.Id})");
        return p;
    }
}
