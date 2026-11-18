// The game's map space against the world's. ParaWorld is a Direct3D game: its spaces are LEFT-handed - maps x east,
// y SOUTH, z up (docs/MAP_FORMAT.md), the models in the same handedness. The world (as three.js in the JS remake and
// Godot here) is right-handed (y up, -z north), so a map position becomes (x - ox, height, y - oy) (MapSource.ToGame,
// ox = map width / 2, oy = map height / 2), and the converter mirrors every model (pwexport/gsf.py GSF_MIRROR: GSF
// (x, y, z) -> glTF (-x, z, -y)). Without both the whole world came out mirrored.
// Ported from remake/src/game/maps/frame.js (pw-exporter 98c7703).
using System;

namespace PwRemake.Sim.Maps;

public static class Frame
{
    /// <summary>the heading (rotation about y, 0 = facing -z) of an object the map turns by `rot` (the angle about the
    /// map's z, from x towards y)</summary>
    public static double MapHeading(double rot) => Math.PI - (double.IsNaN(rot) ? 0 : rot);

    /// <summary>
    /// the orientation [x, y, z, w] of an object the map stores with quaternion q (x, y, z, w; Direct3D style: the
    /// engine applies its conjugate). Tilted landscape pieces (plateaus, cliffs) need all of it; for a turn about z it
    /// gives MapHeading. Derivation: world = S R(conj q) Rz(pi)^-1 S with S = swap(y, z): S R S = (qx, qz, qy, qw),
    /// times Ry(pi) = (0, 1, 0, 0).
    /// </summary>
    public static double[] MapQuat(double[] q)
    {
        double n = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        if (n == 0 || double.IsNaN(n)) n = 1;
        double ax = q[0] / n, ay = q[2] / n, az = q[1] / n, aw = q[3] / n;          // (qx, qz, qy, qw)
        // (ax, ay, az, aw) * (0, 1, 0, 0)
        return new[] { -az, aw, ax, -ay };
    }
}
