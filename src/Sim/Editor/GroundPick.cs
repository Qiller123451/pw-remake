// The ground point under the mouse: a ray (the camera's, through the cursor) marched over a height function in steps,
// then narrowed by halving to the crossing - the map viewer's GroundUnderMouse as plain maths (the editor's cursor and
// brushes; tests). Game coordinates: y up.
using System;

namespace PwRemake.Sim.Editor;

public static class GroundPick
{
    /// <summary>the first point of the ray o + t d (d need not be normalised; t in metres along d / |d|) at or below the
    /// ground, within maxDist; null: it does not meet the ground (the sky)</summary>
    public static (double X, double Y, double Z)? Ray(double ox, double oy, double oz, double dx, double dy, double dz,
        Func<double, double, double> heightAt, double maxDist = 3000, double step = 2)
    {
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len <= 0) return null;
        dx /= len; dy /= len; dz /= len;
        bool Below(double t) => oy + dy * t <= heightAt(ox + dx * t, oz + dz * t);
        if (Below(0)) return (ox, heightAt(ox, oz), oz);
        double prev = 0;
        for (double t = step; t <= maxDist; t += step)
        {
            if (Below(t))
            {
                double a = prev, b = t;
                for (int i = 0; i < 16; i++) { double m = (a + b) / 2; if (Below(m)) b = m; else a = m; }
                return (ox + dx * b, oy + dy * b, oz + dz * b);
            }
            prev = t;
        }
        return null;
    }
}
