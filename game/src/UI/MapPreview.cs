// The skirmish menu's map picture: the preview the map stores (LInf; drawn by the engine's minimap renderer when the
// map was saved: a diamond in the setting's minimap colours with the start locations), else one drawn the same way
// from the map's heights and the setting's minimap colours (the HUD's MinimapModel.ReliefCells / PaintCells), inside
// the original's frame of the multiplayer map list (menue__decoration__levelimage_background_mp.png). The random
// jungle: the frame and "random".
//
// Ported from remake/src/ui/mappreview.js drawPreview (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.UI.Hud.Logic;

namespace PwRemake.UI;

public partial class MapPreview : Control
{
    static readonly Logger L = Log.For("UI.Skirmish");
    static readonly Dictionary<string, Texture2D?> s_cache = new();
    Texture2D? _pic;
    string _label = "";
    readonly Texture2D? _frame = PwTheme.Tex("decoration__levelimage_background_mp");

    public MapPreview(float size = 225)
    {
        CustomMinimumSize = new Vector2(size, size);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>the picture of a map file (null: the random jungle)</summary>
    public void Show(string? path)
    {
        _label = path == null ? "random" : "";
        _pic = path == null ? null : PictureOf(path);
        if (path != null && _pic == null) _label = "?";
        QueueRedraw();
    }

    /// <summary>the map's stored preview, else drawn from its heights (cached per file)</summary>
    public static Texture2D? PictureOf(string path)
    {
        if (s_cache.TryGetValue(path, out var t)) return t;
        t = null;
        try
        {
            var h = Sim.Game.SkirmishMaps.Header(path);
            if (h.Preview is { } p && p.W > 0 && p.H > 0 && p.Rgba.Length >= p.W * p.H * 4)
                t = ImageTexture.CreateFromImage(Image.CreateFromData(p.W, p.H, false, Image.Format.Rgba8, p.Rgba));
            else t = Drawn(MapFiles.Read(path));
        }
        catch (Exception ex) { L.Warn($"map preview {path}: {ex.Message}"); }
        s_cache[path] = t;
        return t;
    }

    /// <summary>the picture of a map in memory, drawn from its heights (a co-op game's extended map: MapExtension.Apply
    /// gives it without a stored preview - docs/COOP.md "Maps")</summary>
    public static Texture2D? PictureOf(MapData md) => md.Preview is { } p && p.W > 0 && p.H > 0 && p.Rgba.Length >= p.W * p.H * 4
        ? ImageTexture.CreateFromImage(Image.CreateFromData(p.W, p.H, false, Image.Format.Rgba8, p.Rgba)) : Drawn(md);
    /// <summary>shows a map in memory (PictureOf(MapData)): the co-op lobby can show the extended map</summary>
    public void ShowMap(MapData md) { _label = ""; _pic = PictureOf(md); if (_pic == null) _label = "?"; QueueRedraw(); }

    /// <summary>mappreview.js without a stored preview: the playable area (the map minus 128 m on every side) in 4 m
    /// cells in the setting's minimap colours, turned to the level's camera as a diamond</summary>
    static Texture2D? Drawn(MapData md)
    {
        var table = MinimapModel.LoadTable(GameApp.Assets?.Resolve($"assets/terrain/{md.Setting}/setting.json"));
        if (table == null || md.Hx == 0) return null;
        int W2 = md.Hx * 2, H2 = md.Hy * 2;
        int gw = Math.Max(1, (int)Math.Round((W2 - 256) / 4.0)), gh = Math.Max(1, (int)Math.Round((H2 - 256) / 4.0));
        double HAt(int i, int j) => md.Heights[Math.Min(md.Hy - 1, (128 + j * 4 + 2) / 2) * md.Hx + Math.Min(md.Hx - 1, (128 + i * 4 + 2) / 2)];
        var col = MinimapModel.ReliefCells(gw, gh, HAt, md.Water);
        var data = new byte[gw * gh * 4];
        MinimapModel.PaintCells(data, col, null, table, _ => 2);
        var cells = Image.CreateFromData(gw, gh, false, Image.Format.Rgba8, data);
        // turned: into a square picture the size of the diagonal
        int S = 200;
        var outp = Image.CreateEmpty(S, S, false, Image.Format.Rgba8);
        double k = S / Math.Sqrt((double)gw * gw + (double)gh * gh), a = MinimapModel.SnapDiamond(Math.PI / 2 - md.DefaultCamera);
        double ca = Math.Cos(-a), sa = Math.Sin(-a);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                double dx = (x + 0.5 - S / 2.0) / k, dy = (y + 0.5 - S / 2.0) / k;
                double u = dx * ca - dy * sa + gw / 2.0, v = dx * sa + dy * ca + gh / 2.0;
                if (u < 0 || v < 0 || u >= gw || v >= gh) continue;
                outp.SetPixel(x, y, cells.GetPixel((int)u, (int)v));
            }
        return ImageTexture.CreateFromImage(outp);
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        if (_frame != null) DrawTextureRect(_frame, r, false);
        else DrawRect(r, new Color(0.16f, 0.25f, 0.11f));
        // the picture inside the frame's diamond (200 of 225 px, centred)
        float s = Math.Min(Size.X, Size.Y) * 200f / 225f;
        var inner = new Rect2((Size.X - s) / 2, (Size.Y - s) / 2, s, s);
        if (_pic != null) DrawTextureRect(_pic, inner, false);
        if (_label != "")
        {
            var f = PwTheme.ButtonFont;
            var sz = f.GetStringSize(_label, HorizontalAlignment.Left, -1, 15);
            DrawString(f, new Vector2((Size.X - sz.X) / 2, Size.Y / 2 + 5), _label, HorizontalAlignment.Left, -1, 15, PwTheme.Title);
        }
    }
}
