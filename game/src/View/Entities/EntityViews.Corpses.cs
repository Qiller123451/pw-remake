// Where a carcass lies. A dead animal's death clip throws its body off the place it died at (the body lies a few metres
// beside it), while the carcass (ResNode "corpse", World.Kill: its "corpseOf" is the dead unit, whose view stays) keeps
// that place. The selection ring, the food bar and the pick volume go round the body as drawn: the middle and the
// reach of the bones its meshes are skinned to, measured from the dead unit's model. Only skinned bones count: a GSF
// skeleton also has helper bones (root, link and IK dummies, a biped's footsteps) that stay on the place it died at,
// and averaging those in pulled the middle back there (Kacper 2026-11-20: "still off-centre").
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.Sim.World;
using PwRemake.View.Models;

namespace PwRemake.View.Entities;

public partial class EntityViews
{
    /// <summary>per model instance: its skeleton and the indices of the bones its meshes are skinned to</summary>
    readonly Dictionary<ModelInstance, (Skeleton3D Sk, int[] Bones)?> _body = new();
    readonly HashSet<string> _bodyWarned = new();

    /// <summary>the dead unit a carcass is the body of (the carcass itself, or the dead unit)</summary>
    static Unit? DeadOf(Entity e) => e is ResNode { Type: "corpse" } n ? n.Opts.U("corpseOf") : e is Unit { Alive: false } u ? u : null;

    /// <summary>a carcass's body as it lies: its middle on the ground (world x, z) and the bones' reach round it (m);
    /// null when it is no carcass or its body is not drawn (a model without a skeleton: the carcass's own place)</summary>
    public (double X, double Z, double Reach)? BodyOf(Entity e)
    {
        if (DeadOf(e) is not { } dead) return null;
        if (!_views.TryGetValue(dead, out var v) || v.M is not { } m || !GodotObject.IsInstanceValid(m)) return null;
        if (!_body.TryGetValue(m, out var sb) || sb is { } s0 && !GodotObject.IsInstanceValid(s0.Sk))
        {
            // (forget the models of bodies that are gone)
            if (_body.Count > 64) foreach (var k in new List<ModelInstance>(_body.Keys)) if (!GodotObject.IsInstanceValid(k)) _body.Remove(k);
            _body[m] = sb = SkinnedBones(m.Model);
            if (sb == null && _bodyWarned.Add(dead.Gfx ?? "?")) GD.Print($"[carcass] {dead.Gfx}: no skinned skeleton - its ring stays where it died");
        }
        if (sb is not { } b) return null;
        var gt = b.Sk.GlobalTransform;
        double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
        foreach (int i in b.Bones)
        {
            var p = gt * b.Sk.GetBoneGlobalPose(i).Origin;
            x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); z0 = Math.Min(z0, p.Z); z1 = Math.Max(z1, p.Z);
        }
        if (x0 > x1) return null;
        double cx = (x0 + x1) / 2, cz = (z0 + z1) / 2;
        return (cx, cz, Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / 2);
    }

    /// <summary>the skeleton and the bones the model's meshes are skinned to (every bone when no mesh says)</summary>
    static (Skeleton3D Sk, int[] Bones)? SkinnedBones(Node root)
    {
        if (FindSkeleton(root) is not { } sk || sk.GetBoneCount() == 0) return null;
        var used = new SortedSet<int>();
        foreach (var mi in Meshes(root))
        {
            if (mi.Skin is not { } skin) continue;
            for (int i = 0; i < skin.GetBindCount(); i++)
            {
                int bi = skin.GetBindBone(i);
                if (bi < 0) bi = sk.FindBone(skin.GetBindName(i));
                if (bi >= 0 && bi < sk.GetBoneCount()) used.Add(bi);
            }
        }
        if (used.Count == 0)
            for (int i = 0; i < sk.GetBoneCount(); i++)
                if (!sk.GetBoneName(i).Contains("footstep", StringComparison.OrdinalIgnoreCase)) used.Add(i);
        return used.Count > 0 ? (sk, new List<int>(used).ToArray()) : null;
    }

    static Skeleton3D? FindSkeleton(Node n)
    {
        if (n is Skeleton3D s) return s;
        foreach (var ch in n.GetChildren()) if (FindSkeleton(ch) is { } f) return f;
        return null;
    }

    static IEnumerable<MeshInstance3D> Meshes(Node n)
    {
        if (n is MeshInstance3D mi) yield return mi;
        foreach (var ch in n.GetChildren()) foreach (var m in Meshes(ch)) yield return m;
    }
}
