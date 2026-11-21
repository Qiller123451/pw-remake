// The map viewer's biome brush (docs/BIOMES.md "Authoring"): paint a map's biome weights and save them as the map's
// extension file.
//   B          brush on / off (the info line shows the brush; F1)
//   [ / ]      the brush's biome: the previous / next of game/maps/biomes.json
//   - / =      the brush's radius (20 .. 400 m)
//   left mouse paint where the cursor points (Alt + left drag still turns the camera)
//   Ctrl+S     save: --biomes-out (a biome layer file, docs/BIOMES.md), else into the map itself (its map.lua and
//              terrain/biomes/*.png - docs/MAP_FORMAT.md; a .pwmap zip is written again; a .ula opened directly: its
//              <map>.biomes.json beside it, which Prepare takes into the converted map)
// A biome the map's layer does not have yet is added to it, and the map is drawn again with its ground textures (a
// second or two); painting then repaints only the ground and the grass under the brush. A map without a layer gets one
// on the first stroke (its setting's biome everywhere).
using System;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.Sim.Maps;
using PwRemake.View.Models;

namespace PwRemake.View.World;

public partial class MapViewer
{
    BiomeAmbience _biomes = null!;
    WorldViewOptions _opts = null!;
    ModelLibrary _lib = null!;
    string _mapPath = "";
    bool _brush;
    int _brushBiome;
    double _brushRadius = 80, _brushT;
    bool _dirty;

    void BrushInit()
    {
        var all = BiomeSet.Default.All;
        _brushBiome = Math.Max(0, all.FindIndex(b => b.Id.Equals(_view.Source.Setting, StringComparison.OrdinalIgnoreCase)));
        if (_args.TryGetValue("brush", out var id))
        {
            _brush = true;
            int k = all.FindIndex(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) _brushBiome = k;
        }
    }

    string BrushInfo()
    {
        var S = _view.Source;
        string mix = S.BiomeLayer == null ? "" : "\nbiomes here: " + string.Join(", ", _view.Layers.Biomes.Select((b, i) => $"{b.Id} {100 * (i < _biomes.Mix.Length ? _biomes.Mix[i] : 0):0} %"));
        if (!_brush) return mix;
        return mix + $"\nBRUSH {BiomeSet.Default.All[_brushBiome].Id}, radius {_brushRadius:0} m  [ [ ] biome, - = radius, left mouse paint, Ctrl+S save{(_dirty ? " - not saved" : "")}]";
    }

    bool BrushKey(InputEventKey k)
    {
        var all = BiomeSet.Default.All;
        switch (k.Keycode)
        {
            case Key.B: _brush = !_brush; return true;
            case Key.Bracketleft when _brush: _brushBiome = (_brushBiome + all.Count - 1) % all.Count; return true;
            case Key.Bracketright when _brush: _brushBiome = (_brushBiome + 1) % all.Count; return true;
            case Key.Minus when _brush: _brushRadius = Math.Max(20, _brushRadius / 1.25); return true;
            case Key.Equal when _brush: _brushRadius = Math.Min(400, _brushRadius * 1.25); return true;
            case Key.S when k.CtrlPressed: SaveBiomes(); return true;
        }
        return false;
    }

    /// <summary>the ground point under the mouse (null: the sky)</summary>
    Vector3? GroundUnderMouse()
    {
        var c = _r.Camera;
        var mp = GetViewport().GetMousePosition();
        Vector3 o = c.ProjectRayOrigin(mp), d = c.ProjectRayNormal(mp);
        float prev = 0;
        for (float t = 2; t < 2000; t += 2)
        {
            var p = o + d * t;
            if (p.Y <= _view.Ground.At(p.X, p.Z))
            {
                float a = prev, b = t;
                for (int i = 0; i < 12; i++) { float m = (a + b) / 2; var q = o + d * m; if (q.Y <= _view.Ground.At(q.X, q.Z)) b = m; else a = m; }
                return o + d * b;
            }
            prev = t;
        }
        return null;
    }

    void BrushStep(double dt)
    {
        if (!_brush || _shot != null || !Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsKeyPressed(Key.Alt)) return;
        if ((_brushT -= dt) > 0) return;
        _brushT = 0.06;
        if (GroundUnderMouse() is not { } p) return;
        var S = _view.Source;
        var id = BiomeSet.Default.All[_brushBiome].Id;
        var l = S.BiomeLayer;
        bool rebuild = false;
        if (l == null)
        {
            if (S.Md == null) return;
            l = BiomeLayer.For(S.Md, new[] { S.Setting });
            rebuild = true;
        }
        int b = l.Biomes.FindIndex(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (b < 0) { b = l.AddBiome(id); rebuild = true; }
        var (mx, my) = S.ToMap(p.X, p.Z);
        var r = BiomeAuthoring.Paint(l, b, mx, my, _brushRadius, 0.3);
        _dirty = true;
        if (rebuild) { S.UseBiomes(l); Redraw(); return; }
        if (r is { } rect)
        {
            var (x0, z0) = S.ToGame(rect.X0, rect.Y0);
            var (x1, z1) = S.ToGame(rect.X1, rect.Y1);
            _view.RefreshBiomes(x0, z0, x1, z1);
        }
    }

    /// <summary>the map drawn again (a biome was added: the ground's texture array changes)</summary>
    void Redraw()
    {
        var S = _view.Source;
        RemoveChild(_view);
        _view.QueueFree();
        _view = WorldView.Load(S, _lib, _opts);
        AddChild(_view);
        _view.Fog.RevealAll = true;
        _biomes = new BiomeAmbience(_view, _r);
        L.Info($"biomes of the map now: {string.Join(", ", S.BiomeLayer?.Biomes ?? new())}");
    }

    void SaveBiomes()
    {
        var l = _view.Source.BiomeLayer;
        if (l == null) { L.Warn("no biome layer to save"); return; }
        string path = _args.TryGetValue("biomes-out", out var o) ? o
            : PwMapReader.IsMap(_mapPath) ? _mapPath
            : BiomeLayer.SidecarOf(_mapPath);
        try
        {
            if (PwMapReader.IsMap(path))
            {
                // the map document with the new layer, written again (the writer is deterministic: only the biome
                // pictures and map.lua change)
                var doc = PwMapReader.Read(path);
                doc.Biomes = l;
                if (Directory.Exists(path)) PwMapWriter.WriteFolder(doc, path); else PwMapWriter.WriteZip(doc, path);
            }
            else l.Write(path);
            _dirty = false; L.Info($"biome layer saved: {path}");
        }
        catch (Exception e) { L.Warn($"biome layer not saved ({path}): {e.Message} - give --biomes-out=<file>"); }
    }
}
