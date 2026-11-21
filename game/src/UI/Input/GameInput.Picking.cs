// Picking (ui/input.js): the ground under the mouse (a march along the mouse ray over the height field), the entity
// under the mouse with the original's rules - units first by their pick volume along the ray (the one in front in a
// clump), else the nearest small figure around the cursor (far zoom); buildings by their visible surfaces (the hulls
// and pick volumes of many models reach far beyond the building); resources by a standing cylinder of their kind's
// height; wells and items like buildings - and the screen rectangle of an entity's pick volume (box selection).
// Ported from remake/src/ui/input.js (ray, groundPoint, pickRect, unitRayHit, boxHit, pickHit, resHit, entityAt)
// (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.World;
using PwRemake.View.Models;

namespace PwRemake.UI.PlayerInput;

public partial class GameInput
{
    // how tall the resources stand for picking (ResNode.type), metres; and how wide (their radius keeps the workers
    // apart and can be much wider than the plant)
    static readonly Dictionary<string, double> RES_HEIGHT = new() { ["tree"] = 10, ["timber"] = 1.5, ["stone"] = 3, ["bush"] = 2, ["corpse"] = 1.5, ["farm"] = 0.6 };
    static readonly Dictionary<string, (double, double)> RES_RADIUS = new() { ["tree"] = (1.2, 1.6), ["bush"] = (0.8, 2.2), ["stone"] = (1, 4) };
    static double ResRadius(ResNode n) => RES_RADIUS.TryGetValue(n.Type, out var c) ? Math.Min(c.Item2, Math.Max(c.Item1, n.Radius)) : Math.Max(0.8, n.Radius);

    public readonly record struct ScreenRect(float X0, float Y0, float X1, float Y1, float Z)
    {
        public float Area => (X1 - X0) * (Y1 - Y0);
    }

    (Vector3 O, Vector3 D) Ray(Vector2 m)
    {
        var c = G.Renderer.Camera;
        return (c.ProjectRayOrigin(m), c.ProjectRayNormal(m));
    }

    /// <summary>the ground under a screen point (null: the ray leaves the map)</summary>
    public Vector3? GroundPoint(Vector2 m)
    {
        var (o, d) = Ray(m);
        double t = 0, prev = 0;
        for (int i = 0; i < 400 && t < 1600; i++)
        {
            double x = o.X + d.X * t, y = o.Y + d.Y * t, z = o.Z + d.Z * t;
            double h = W.Height(x, z);
            if (y <= h)
            {
                double a = prev, b = t;
                for (int k = 0; k < 8; k++) { double mid = (a + b) / 2; double yy = o.Y + d.Y * mid; if (yy <= W.Height(o.X + d.X * mid, o.Z + d.Z * mid)) b = mid; else a = mid; }
                return new Vector3((float)(o.X + d.X * b), (float)(o.Y + d.Y * b), (float)(o.Z + d.Z * b));
            }
            prev = t;
            t += Math.Max(0.5, (y - h) * 0.6);
        }
        return null;
    }

    /// <summary>screen rectangle of an entity's pick volume (the GSF selection hull) or its model's bounds</summary>
    public ScreenRect? PickRect(Entity e)
    {
        var m = G.Entities.ModelOf(e);
        if (m == null || !m.Visible || m.Template == null) return null;
        var bb = m.Template.Pick ?? m.Template.Bounds;
        var cam = G.Renderer.Camera;
        var xf = m.GlobalTransform;
        float x0 = 1e9f, y0 = 1e9f, x1 = -1e9f, y1 = -1e9f, zmin = 1e9f;
        for (int i = 0; i < 8; i++)
        {
            var v = bb.Position + new Vector3((i & 1) != 0 ? bb.Size.X : 0, (i & 2) != 0 ? bb.Size.Y : 0, (i & 4) != 0 ? bb.Size.Z : 0);
            var w = xf * v;
            if (cam.IsPositionBehind(w)) return null;
            var s = cam.UnprojectPosition(w);
            x0 = Math.Min(x0, s.X); x1 = Math.Max(x1, s.X); y0 = Math.Min(y0, s.Y); y1 = Math.Max(y1, s.Y);
            zmin = Math.Min(zmin, cam.GlobalPosition.DistanceTo(w));
        }
        return new ScreenRect(x0, y0, x1, y1, zmin);
    }

    static float? RayBox(Vector3 o, Vector3 d, Aabb b)
    {
        float tmin = 0, tmax = 1e9f;
        for (int a = 0; a < 3; a++)
        {
            float oa = o[a], da = d[a], lo = b.Position[a], hi = b.End[a];
            if (Math.Abs(da) < 1e-9f) { if (oa < lo || oa > hi) return null; continue; }
            float t1 = (lo - oa) / da, t2 = (hi - oa) / da;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1); tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return null;
        }
        return tmin;
    }

    // distance along the mouse ray to a unit's pick volume (a little narrower than the box so that the bodies of a clump
    // do not overlap as much), or null
    float? UnitRayHit(Entity u, Vector3 o, Vector3 d)
    {
        var m = G.Entities.ModelOf(u);
        if (m == null || m.Template == null) return null;
        var bb = m.Template.Pick ?? m.Template.Bounds;
        var c = bb.GetCenter(); var h = bb.Size * 0.5f; h.X *= 0.8f; h.Z *= 0.8f;
        var box = new Aabb(c - h, h * 2);
        var inv = m.GlobalTransform.AffineInverse();
        var lo = inv * o; var ld = (inv.Basis * d);
        var t = RayBox(lo, ld, box);
        if (t == null) return null;
        return (m.GlobalTransform * (lo + ld * t.Value)).DistanceTo(o);
    }

    // distance along the mouse ray to the box around a building's model (cheap; the order pickHit tries them in)
    static float? BoxHit(ModelInstance m, Vector3 o, Vector3 d)
    {
        if (m.Template == null) return null;
        var bb = m.Template.Bounds;
        if (m.Template.Pick is Aabb p) bb = bb.Merge(p);
        var inv = m.GlobalTransform.AffineInverse();
        var lo = inv * o; var ld = inv.Basis * d;
        var t = RayBox(lo, ld, bb);
        if (t == null) return null;
        return (m.GlobalTransform * (lo + ld * t.Value)).DistanceTo(o);
    }

    static readonly Dictionary<Mesh, Vector3[]> s_tris = new();
    // distance along the mouse ray to a building's visible surfaces, as the player sees them; both sides of every
    // triangle count. null = missed.
    static float? PickHit(Node3D root, Vector3 o, Vector3 d)
    {
        float? best = null;
        void Visit(Node n)
        {
            if (n is Node3D n3 && !n3.Visible) return;
            // a skinned part (an animated building's hull: the carrier, floating harbours) has no fixed triangles here:
            // its bounding box counts (Kacper: the carrier could only be clicked on its few unskinned parts)
            if (n is MeshInstance3D sk && sk.Mesh != null && sk.Skin != null)
            {
                var inv = sk.GlobalTransform.AffineInverse();
                var lo = inv * o; var ld = inv.Basis * d;
                if (RayBox(lo, ld, sk.Mesh.GetAabb()) is float tb)
                {
                    float dd = (sk.GlobalTransform * (lo + ld * tb)).DistanceTo(o);
                    if (best == null || dd < best) best = dd;
                }
            }
            if (n is MeshInstance3D mi && mi.Mesh != null && mi.Skin == null)
            {
                var inv = mi.GlobalTransform.AffineInverse();
                var lo = inv * o; var ld = inv.Basis * d;
                if (RayBox(lo, ld, mi.Mesh.GetAabb()) != null)
                {
                    if (!s_tris.TryGetValue(mi.Mesh, out var T)) s_tris[mi.Mesh] = T = mi.Mesh.GetFaces();
                    for (int i = 0; i + 2 < T.Length; i += 3)
                    {
                        if (Geometry3D.RayIntersectsTriangle(lo, ld, T[i], T[i + 1], T[i + 2]) is { VariantType: Variant.Type.Vector3 } hv)
                        {
                            float dd = (mi.GlobalTransform * hv.AsVector3()).DistanceTo(o);
                            if (best == null || dd < best) best = dd;
                        }
                    }
                }
            }
            foreach (var ch in n.GetChildren()) Visit(ch);
        }
        Visit(root);
        return best;
    }

    // distance along the ray to a resource: a standing cylinder of its radius and the height of its kind
    // (a carcass: round its body as it lies, EntityViews.BodyOf - the death clip throws it off the place it died at)
    static double? ResHit(ResNode n, Vector3 o, Vector3 d, (double X, double Z, double Reach)? body = null)
    {
        double h = RES_HEIGHT.GetValueOrDefault(n.Type, 2), r = body is { } bd ? Math.Max(ResRadius(n), Math.Min(8, bd.Reach)) : ResRadius(n);
        double nx = body?.X ?? n.Pos.X, nz = body?.Z ?? n.Pos.Z;
        double ox = o.X - nx, oz = o.Z - nz;
        double A = d.X * d.X + d.Z * d.Z, B = 2 * (ox * d.X + oz * d.Z), C = ox * ox + oz * oz - r * r;
        double D = B * B - 4 * A * C;
        if (A >= 1e-9 && D >= 0)
            foreach (var t in new[] { (-B - Math.Sqrt(D)) / (2 * A), (-B + Math.Sqrt(D)) / (2 * A) })
            {
                double y = o.Y + d.Y * t;
                if (t > 0 && y >= n.Pos.Y - 0.5 && y <= n.Pos.Y + h) return t;
            }
        // looking straight down onto the top
        if (Math.Abs(d.Y) > 1e-6)
        {
            double t = (n.Pos.Y + h - o.Y) / d.Y;
            double px = o.X + d.X * t - nx, pz = o.Z + d.Z * t - nz;
            if (t > 0 && px * px + pz * pz <= r * r) return t;
        }
        return null;
    }

    bool ShownUnit(Unit u) => u.Alive && !u.Inside && !u.Unselectable && G.Entities.Shown(u) && !(u.Owner != G.Me && (!G.Fog.Visible(u.Pos.X, u.Pos.Z) || W.HiddenFrom(u, G.Me)));

    /// <summary>
    /// The entity under the cursor: units first (the one whose pick volume the ray meets first), then the nearest small
    /// figure around the cursor (far zoom) unless a building's surface is under it, then the nearest along the ray of
    /// the buildings, resources, wells and items; a gate's wing selects the gate.
    /// </summary>
    public Entity? EntityAt(Vector2 m)
    {
        if (G.Sim == null) return null;           // (any game: a campaign mission, a skirmish, the sequel's missions)
        var (o, d) = Ray(m);
        Entity? best = null; float ud = float.MaxValue;
        foreach (var u in W.Units)
        {
            if (!ShownUnit(u)) continue;
            var t = UnitRayHit(u, o, d);
            if (t != null && t < ud) { ud = t.Value; best = u; }
        }
        if (best != null) return best;
        // nothing under the cursor itself: the nearest small figure around it (far zoom - a unit a few pixels tall).
        // Only a fallback: a building whose surface is under the cursor wins over figures caught by the widened box.
        const float pad = 4;
        double ba = 1e12; bool inRect = false;
        foreach (var u in W.Units)
        {
            if (!ShownUnit(u)) continue;
            if (PickRect(u) is not { } r) continue;
            float cx = (r.X0 + r.X1) / 2, cy = (r.Y0 + r.Y1) / 2;
            float hw = Math.Max(10, (r.X1 - r.X0) / 2) + pad, hh = Math.Max(12, (r.Y1 - r.Y0) / 2) + pad;
            if (Math.Abs(m.X - cx) > hw || Math.Abs(m.Y - cy) > hh) continue;
            double score = r.Area + Math.Sqrt((m.X - cx) * (m.X - cx) + (m.Y - cy) * (m.Y - cy)) * 4;
            if (score < ba) { ba = score; best = u; inRect = r.Y1 - r.Y0 < 28 && m.X >= r.X0 && m.X <= r.X1 && m.Y >= r.Y0 && m.Y <= r.Y1; }
        }
        var fig = best;
        var g = GroundPoint(m);
        float far = g is Vector3 gp ? gp.DistanceTo(o) + 1 : 1600;
        Entity? hit = null; float hd = far;
        var cands = new List<(float D, Building B)>();
        foreach (var b in W.Buildings)
        {
            if (!b.Alive || b.Parked || b.Unselectable || (b.Owner != G.Me && (!G.Fog.Explored(b.Pos.X, b.Pos.Z) || W.HiddenFrom(b, G.Me)))) continue;
            var bm = G.Entities.ModelOf(b);
            if (bm == null || !bm.Visible) continue;
            var dd = BoxHit(bm, o, d);
            if (dd != null && dd < hd) cands.Add((dd.Value, b));
        }
        foreach (var (d0, b) in cands.OrderBy(c => c.D))       // nearest box first; a hit nearer than the next box ends it
        {
            if (d0 >= hd) break;
            var dd = PickHit(G.Entities.ModelOf(b)!, o, d);
            if (dd != null && dd < hd) { hd = dd.Value; hit = b; }
        }
        // resources near the ray between the eye and the ground (tall trees in front of a building count)
        var seen = new HashSet<ResNode>();
        for (float t = Math.Max(0, far - 160); t <= far; t += 6)
        {
            double x = o.X + d.X * t, y = o.Y + d.Y * t, z = o.Z + d.Z * t;
            if (y - W.Height(x, z) > 14) continue;
            W.RHash.Query(x, z, 8, n =>
            {
                if (!seen.Add(n)) return;
                if (!n.Alive || n.Amount <= 0 || !G.Fog.Explored(n.Pos.X, n.Pos.Z)) return;
                var dd = ResHit(n, o, d, G.Entities.BodyOf(n));
                if (dd != null && dd < hd) { hd = (float)dd.Value; hit = n; }
            });
        }
        // healing wells and items (their visible surfaces, like buildings)
        foreach (var w in W.Wells.Cast<Entity>().Concat(W.Items))
        {
            if (!w.Alive || !G.Fog.Explored(w.Pos.X, w.Pos.Z) || G.Entities.PropModel(w) is not { } pm || !pm.Visible) continue;
            var d0 = BoxHit(pm, o, d);
            if (d0 == null || d0 >= hd) continue;
            var dd = PickHit(pm, o, d);
            if (dd != null && dd < hd) { hd = dd.Value; hit = w; }
        }
        if (hit != null && fig != null)
        {
            // a small figure drawn under the cursor in front of the surface hit
            var fp = G.Entities.PosOf(fig);
            fp.Y += Math.Min(2, (float)(((fig as Unit)?.Height ?? 2) * 0.5));
            if (inRect && fp.DistanceTo(o) < hd + fig.Radius) return fig;
        }
        else if (fig != null) return fig;
        if (hit != null) return hit is Building hb && hb.ParentGate is { Alive: true } pg ? pg : hit;
        if (g is Vector3 gg)
        {
            // the ground right next to a small resource (bushes, stones in the grass)
            ResNode? rb = null; double rd = 1;
            W.RHash.Query(gg.X, gg.Z, 6, n =>
            {
                if (!n.Alive || n.Amount <= 0 || !G.Fog.Explored(n.Pos.X, n.Pos.Z)) return;
                var bd = G.Entities.BodyOf(n);
                double nx = bd?.X ?? n.Pos.X, nz = bd?.Z ?? n.Pos.Z, nr = bd is { } b2 ? Math.Max(ResRadius(n), Math.Min(8, b2.Reach)) : ResRadius(n);
                double dist = Math.Sqrt((nx - gg.X) * (nx - gg.X) + (nz - gg.Z) * (nz - gg.Z)) - nr;
                if (dist < rd) { rd = dist; rb = n; }
            });
            if (rb != null) return rb;
        }
        return null;
    }
}
