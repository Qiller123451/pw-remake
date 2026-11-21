// The map's preview picture made from its terrain (docs/MAP_EDITOR.md "Map settings"): the 200 x 200 RGBA picture the
// map lists show (MapData.Preview, as the original's LInf preview), drawn from above - the ground shaded by its slopes
// (light from the north-west) in the colour of its setting and of each 4 m ground material, the water blue (darker
// where deeper), the start locations as gold dots. The same shading as the editor's minimap. No Godot types.
using System;
using System.Linq;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

public static class MapPreviewMaker
{
    /// <summary>the setting's ground colour (r, g, b 0..1)</summary>
    public static (double R, double G, double B) BaseColor(string setting) => setting switch
    {
        "Savanna" => (0.62, 0.55, 0.32), "Icewaste" => (0.82, 0.85, 0.9), "Northland" => (0.42, 0.5, 0.3),
        "Ashvalley" => (0.4, 0.36, 0.33), "Cave1" or "Cave2" or "Cave3" => (0.38, 0.33, 0.28), _ => (0.3, 0.48, 0.22),
    };

    // the eight ground materials a little apart (0 the setting's base ... 7 rock-like)
    static readonly double[] MAT_SHADE = { 1.0, 0.92, 1.08, 0.85, 1.12, 0.8, 0.95, 0.75 };

    /// <summary>the preview of the document (n x n pixels, rows from the top = north)</summary>
    public static MapPreview Make(IEditorMapDoc d, int n = 200)
    {
        var img = new byte[n * n * 4];
        double w = d.HeightsW * d.Cell, h = d.HeightsH * d.Cell, s = Math.Max(w, h) / n;
        var (br, bg, bb) = BaseColor(d.Setting);
        double? water = d.Water;
        for (int py = 0; py < n; py++)
            for (int px = 0; px < n; px++)
            {
                int o = (py * n + px) * 4;
                double mx = d.OriginX + (px + 0.5) * s, my = d.OriginY + (py + 0.5) * s;
                img[o + 3] = 255;
                if (mx > d.OriginX + w || my > d.OriginY + h) continue;
                double z = d.HeightAt(mx, my);
                double sl = (d.HeightAt(mx - s, my - s) - d.HeightAt(mx + s, my + s)) / (2 * s);
                int mat = d.MaterialAt(mx, my);
                double k = Math.Clamp(0.85 + sl * 1.6 + (z - 20) * 0.004, 0.35, 1.35) * (mat >= 0 && mat < 8 ? MAT_SHADE[mat] : 1);
                double r = br * k, g = bg * k, b = bb * k;
                if (water is double wl && z < wl)
                {
                    double dep = Math.Clamp((wl - z) / 8, 0, 1);
                    double wr = 0.16 + (0.06 - 0.16) * dep, wg = 0.32 + (0.14 - 0.32) * dep, wb = 0.5 + (0.3 - 0.5) * dep;
                    r = wr + (r - wr) * 0.15; g = wg + (g - wg) * 0.15; b = wb + (b - wb) * 0.15;
                }
                img[o] = (byte)Math.Clamp(r * 255, 0, 255); img[o + 1] = (byte)Math.Clamp(g * 255, 0, 255); img[o + 2] = (byte)Math.Clamp(b * 255, 0, 255);
            }
        // the start locations
        foreach (var st in d.Objects.Where(o => o.Type == "SLOC"))
        {
            int cx = (int)((st.X - d.OriginX) / s), cy = (int)((st.Y - d.OriginY) / s);
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= n || y >= n || dx * dx + dy * dy > 10) continue;
                    int o = (y * n + x) * 4;
                    bool rim = dx * dx + dy * dy > 5;
                    img[o] = (byte)(rim ? 0 : 255); img[o + 1] = (byte)(rim ? 0 : 216); img[o + 2] = (byte)(rim ? 0 : 50);
                }
        }
        return new MapPreview { W = n, H = n, Rgba = img };
    }
}
