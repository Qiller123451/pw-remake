// The finer parts of the frame for --perf (GameSession.Perf.cs, docs/DEBUG.md "Performance"): null unless a
// measurement runs, so the probes cost one null check otherwise.
//   var P = PerfProbe.P; P?.Sub(); ...; P?.SubMark("entities:anim");
// And the camera's view as planes in a plain array (Camera3D.GetFrustum gives a Godot array: every element read
// from it crosses into the engine - ~1000 entities x 6 planes x 2 tests a frame).
using Godot;
using PwRemake.Sim.World;

namespace PwRemake.View;

public static class PerfProbe
{
    public static StepProfile? P;
}

public static class Frustum
{
    /// <summary>the camera's planes into buf (resized when needed); null without a camera</summary>
    public static Plane[]? Of(Camera3D? cam, ref Plane[] buf)
    {
        if (cam == null) return null;
        var gp = cam.GetFrustum();
        int n = gp.Count;
        if (buf.Length != n) buf = new Plane[n];
        for (int k = 0; k < n; k++) buf[k] = gp[k];
        return buf;
    }

    /// <summary>a sphere at least partly inside (no planes: everything is)</summary>
    public static bool Has(Plane[]? f, Vector3 c, float r)
    {
        if (f == null) return true;
        for (int i = 0; i < f.Length; i++) if (f[i].DistanceTo(c) > r) return false;
        return true;
    }
}
