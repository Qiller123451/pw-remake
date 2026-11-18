// Clip fixes applied at load time: start / loop / end clips cut into their parts, borrowed animations retargeted.
// Ported from remake/src/engine/assets.js (retarget, clipPart, splitLoops) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using Godot;

namespace PwRemake.View.Anim;

public static class ClipTools
{
    /// <summary>Rest positions by node / bone name below a model's ModelRoot (three.js: every Object3D's position).</summary>
    public static Dictionary<string, Vector3> RestPositions(Node root)
    {
        var m = new Dictionary<string, Vector3>();
        void Walk(Node n)
        {
            if (n is Skeleton3D sk)
                for (int i = 0; i < sk.GetBoneCount(); i++) m[sk.GetBoneName(i)] = sk.GetBoneRest(i).Origin;
            foreach (var c in n.GetChildren())
            {
                if (c is Node3D c3 && c3 is not BoneAttachment3D && c3 is not Skeleton3D && !m.ContainsKey(c3.Name)) m[c3.Name] = c3.Position;
                Walk(c);
            }
        }
        Walk(root);
        return m;
    }

    /// <summary>The bone or node a track drives: the subname ("Model/Skeleton3D:bone" -> bone), else the node's name.</summary>
    public static string TrackTarget(NodePath p)
    {
        if (p.GetSubNameCount() > 0) return p.GetSubName(0);
        int n = p.GetNameCount();
        return n > 0 ? p.GetName(n - 1) : "";
    }

    /// <summary>
    /// Borrowed animations (a model that animates with another model's clips, e.g. level 4/5 characters with the level 1
    /// clips, seas_warrior with livingstone's): the bones have the same names but other rest offsets (a bulkier body).
    /// Position tracks are moved by (own rest offset - source rest offset), so every bone keeps its own length and only
    /// the motion is taken over - the engine's shared animation sets work that way. Without it the mesh is stretched
    /// onto the source skeleton. A rig that is the source's scaled as a whole gets its motion scaled instead.
    /// Returns the clips for dst (unchanged clips are shared with the source).
    /// </summary>
    public static List<(string name, Animation anim)> Retarget(List<(string name, Animation anim)> clips, Node srcScene, Node dstScene)
    {
        var a = RestPositions(srcScene);
        var b = RestPositions(dstScene);
        var delta = new Dictionary<string, Vector3>();
        foreach (var (n, pb) in b) if (a.TryGetValue(n, out var pa) && pa.DistanceSquaredTo(pb) > 1e-8f) delta[n] = pb - pa;
        if (delta.Count == 0) return clips;
        // the same rig scaled as a whole (seas_endboss_gate = seas_gate x 1.25): the motion scales with it
        double? s = null;
        foreach (var (n, pb) in b)
        {
            if (!a.TryGetValue(n, out var pa) || pa.LengthSquared() < 1e-6f) continue;
            double f = pb.Length() / pa.Length();
            s ??= f;
            if (Math.Abs(f - s.Value) > 0.01 * s.Value || (pa * (float)f).DistanceTo(pb) > 0.01 * pb.Length() + 1e-4) { s = null; break; }
        }
        var outList = new List<(string, Animation)>(clips.Count);
        if (s != null && Math.Abs(s.Value - 1) > 0.02)
        {
            foreach (var (name, c) in clips)
            {
                var d = (Animation)c.Duplicate();
                for (int t = 0; t < d.GetTrackCount(); t++)
                {
                    if (d.TrackGetType(t) != Animation.TrackType.Position3D) continue;
                    for (int k = 0; k < d.TrackGetKeyCount(t); k++) d.TrackSetKeyValue(t, k, (Vector3)d.TrackGetKeyValue(t, k) * (float)s.Value);
                }
                outList.Add((name, d));
            }
            return outList;
        }
        foreach (var (name, c) in clips)
        {
            Animation? d = null;
            for (int t = 0; t < c.GetTrackCount(); t++)
            {
                if (c.TrackGetType(t) != Animation.TrackType.Position3D) continue;
                if (!delta.TryGetValue(TrackTarget(c.TrackGetPath(t)), out var dv)) continue;
                d ??= (Animation)c.Duplicate();
                for (int k = 0; k < d.TrackGetKeyCount(t); k++) d.TrackSetKeyValue(t, k, (Vector3)d.TrackGetKeyValue(t, k) + dv);
            }
            outList.Add((name, d ?? c));
        }
        return outList;
    }

    /// <summary>Clips made as start + loop + end: one part of the clip as its own clip (see SplitLoops).</summary>
    public static Animation ClipPart(Animation clip, double t0, double t1)
    {
        var na = new Animation { Length = (float)(t1 - t0), LoopMode = Animation.LoopModeEnum.None };
        for (int t = 0; t < clip.GetTrackCount(); t++)
        {
            var type = clip.TrackGetType(t);
            if (type != Animation.TrackType.Position3D && type != Animation.TrackType.Rotation3D && type != Animation.TrackType.Scale3D && type != Animation.TrackType.Value) continue;
            int nt = na.AddTrack(type);
            na.TrackSetPath(nt, clip.TrackGetPath(t));
            na.TrackSetInterpolationType(nt, clip.TrackGetInterpolationType(t));
            Variant At(double time) => type switch
            {
                Animation.TrackType.Position3D => clip.PositionTrackInterpolate(t, time),
                Animation.TrackType.Rotation3D => clip.RotationTrackInterpolate(t, time),
                Animation.TrackType.Scale3D => clip.ScaleTrackInterpolate(t, time),
                _ => clip.ValueTrackInterpolate(t, time),
            };
            void Put(double time, Variant v)
            {
                switch (type)
                {
                    case Animation.TrackType.Position3D: na.PositionTrackInsertKey(nt, time, (Vector3)v); break;
                    case Animation.TrackType.Rotation3D: na.RotationTrackInsertKey(nt, time, (Quaternion)v); break;
                    case Animation.TrackType.Scale3D: na.ScaleTrackInsertKey(nt, time, (Vector3)v); break;
                    default: na.TrackInsertKey(nt, time, v); break;
                }
            }
            Put(0, At(t0));
            int n = clip.TrackGetKeyCount(t);
            for (int k = 0; k < n; k++)
            {
                double kt = clip.TrackGetKeyTime(t, k);
                if (kt > t0 + 1e-4 && kt < t1 - 1e-4) Put(kt - t0, clip.TrackGetKeyValue(t, k));
            }
            Put(t1 - t0, At(t1));
        }
        return na;
    }

    /// <summary>
    /// Clips made of a start, a loop and an end part (the loop marks of the GSF animation chunks, manifest
    /// models[m].loops = {clip: [t0, t1]}): the parts as extra clips "&lt;name&gt;#s", "#l", "#e" - AnimController
    /// plays the start once, repeats the loop and plays the end when the action stops, like the original engine.
    /// Parts shorter than 0.05 s are left out. The loop parts loop by themselves.
    /// </summary>
    public static List<(string name, Animation anim)> SplitLoops(List<(string name, Animation anim)> clips, IReadOnlyDictionary<string, (double t0, double t1)> loops)
    {
        var outList = new List<(string, Animation)>(clips);
        foreach (var (name, c) in clips)
        {
            if (!loops.TryGetValue(name, out var lp) || lp.t1 - lp.t0 < PwRemake.Sim.Content.ModelManifest.MinLoopPart(name) || lp.t1 > c.Length + 0.02) continue;
            double t1 = Math.Min(lp.t1, c.Length);
            var l = ClipPart(c, lp.t0, t1);
            l.LoopMode = Animation.LoopModeEnum.Linear;
            outList.Add((name + "#l", l));
            if (lp.t0 > 0.05) outList.Add((name + "#s", ClipPart(c, 0, lp.t0)));
            if (c.Length - t1 > 0.05) outList.Add((name + "#e", ClipPart(c, t1, c.Length)));
        }
        return outList;
    }
}
