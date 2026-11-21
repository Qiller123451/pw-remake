// The original mouse cursors (Data/Base/Cursors, converted by the toolkit to assets/ui/cur/<name>.png with their hot
// spots in hotspots.json): standard, walk, walk_aggro, attack, attack_walk, attack_harvest, harvest_food / wood /
// stone, build, load, trade, special, no, mapping, the screen-edge arrows sm_* ... (ui/input.js loadCursors and the
// body[data-cursor] rules). Without the files the system cursor stays.
// Ported from remake/src/ui/input.js (loadCursors, update's cursor choice) (pw-exporter 98c7703).
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.UI.PlayerInput;

public static class Cursors
{
    static readonly Dictionary<string, (Texture2D Tex, Vector2 Hot)> _cur = new();
    static string? _shown;
    static bool _loaded;

    public static void Load(AssetSource? assets)
    {
        if (_loaded || assets == null) return;
        _loaded = true;
        var hs = assets.Resolve("assets/ui/cur/hotspots.json");
        if (hs == null) { Log.For("UI.Input").Warn("assets/ui/cur/hotspots.json is not in the data: system cursors"); return; }
        if (Json.TryParseFile(hs) is not JsonObject o) return;
        foreach (var (k, v) in o)
        {
            var f = assets.Resolve($"assets/ui/cur/{k}.png");
            if (f == null || Image.LoadFromFile(f) is not Image img) continue;
            var hot = v is JsonArray a && a.Count >= 2 ? new Vector2((float)a[0].Num(), (float)a[1].Num()) : Vector2.Zero;
            _cur[k] = (ImageTexture.CreateFromImage(img), hot);
        }
    }

    /// <summary>shows the cursor of that name (null: the system's arrow)</summary>
    public static void Set(string? name)
    {
        if (name == _shown) return;
        _shown = name;
        // (a name the data lacks: the next shorter one - attack_area_weapon_walk -> attack_area_weapon -> ... -> attack)
        var nm = name;
        while (nm != null && !_cur.ContainsKey(nm) && nm.LastIndexOf('_') > 0) nm = nm[..nm.LastIndexOf('_')];
        if (nm != null && _cur.TryGetValue(nm, out var c)) Godot.Input.SetCustomMouseCursor(c.Tex, Godot.Input.CursorShape.Arrow, c.Hot);
        else if (name != null && name != "standard" && _cur.TryGetValue("standard", out var s)) Godot.Input.SetCustomMouseCursor(s.Tex, Godot.Input.CursorShape.Arrow, s.Hot);
        else if (name == "standard" && _cur.TryGetValue("standard", out var st)) Godot.Input.SetCustomMouseCursor(st.Tex, Godot.Input.CursorShape.Arrow, st.Hot);
        else Godot.Input.SetCustomMouseCursor(null);
    }
}
