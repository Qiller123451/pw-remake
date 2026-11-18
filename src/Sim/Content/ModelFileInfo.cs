// What the simulation needs of a model file (assets/models/<name>.glb, converted by the toolkit) - the values the JS
// remake read from the loaded three.js template (engine/assets.js loadModel, game/entities.js):
//   fourcc     the GSF object kind of the root node's extras ("Char", "Anim", "Vehi", "Bldg", "Ship" ...)
//   clips      animation name -> duration in seconds (the length of the clip as three.js makes it: the largest time of
//              its samplers' inputs - glTF requires min/max on them, so only the JSON chunk is read)
//   links      link name (node "link_<name>") -> position in the model's frame (three.js, Y up, at rest), the frame in
//              which Building.linkWorld / Composite place things
//   box        the bounding box of the drawn meshes (three.js Box3.setFromObject of the template after loadModel
//              dropped the helper parts: pick volumes, shadow models, low LODs, foliage meshes, flag-less hulls)
// The prepare tool writes these for every model into assets/modelinfo.json (ModelManifest reads it); without that file
// ModelManifest reads the .glb files it finds on first use.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Content;

public sealed class ModelFileInfo
{
    public string Fourcc = "";
    /// <summary>clip name (as in the file) -> duration</summary>
    public Dictionary<string, double> Clips = new(StringComparer.Ordinal);
    /// <summary>link name (without "link_") -> position in the model frame</summary>
    public Dictionary<string, V3> Links = new(StringComparer.Ordinal);
    /// <summary>bounding box of the drawn parts in the model frame; null if the model has no meshes</summary>
    public V3? BoxMin, BoxMax;
    /// <summary>bounding box of the pick volumes (GSF attr bit 18: the object's selection volume) in the model frame;
    /// null if it has none. A field's (hu_corn_field, ninigi_paddy) covers its whole plot, which has no pathfinder box.</summary>
    public V3? PickMin, PickMax;

    public JsonObject ToJson()
    {
        var clips = new JsonObject();
        foreach (var (k, v) in Clips) clips[k] = Math.Round(v, 5);
        var links = new JsonObject();
        foreach (var (k, v) in Links) links[k] = new JsonArray(R(v.X), R(v.Y), R(v.Z));
        var o = new JsonObject { ["fourcc"] = Fourcc, ["clips"] = clips, ["links"] = links };
        if (BoxMin is V3 a && BoxMax is V3 b) o["box"] = new JsonArray(R(a.X), R(a.Y), R(a.Z), R(b.X), R(b.Y), R(b.Z));
        if (PickMin is V3 pa && PickMax is V3 pb) o["pick"] = new JsonArray(R(pa.X), R(pa.Y), R(pa.Z), R(pb.X), R(pb.Y), R(pb.Z));
        return o;
        static double R(double x) => Math.Round(x, 4);
    }

    public static ModelFileInfo FromJson(JsonNode? n)
    {
        var m = new ModelFileInfo { Fourcc = n?["fourcc"].Str() ?? "" };
        if (n?["clips"] is JsonObject c) foreach (var (k, v) in c) m.Clips[k] = v.Num();
        if (n?["links"] is JsonObject l) foreach (var (k, v) in l) m.Links[k] = new V3(v?[0].Num() ?? 0, v?[1].Num() ?? 0, v?[2].Num() ?? 0);
        if (n?["box"] is JsonArray b && b.Count == 6) { m.BoxMin = new V3(b[0].Num(), b[1].Num(), b[2].Num()); m.BoxMax = new V3(b[3].Num(), b[4].Num(), b[5].Num()); }
        if (n?["pick"] is JsonArray p && p.Count == 6) { m.PickMin = new V3(p[0].Num(), p[1].Num(), p[2].Num()); m.PickMax = new V3(p[3].Num(), p[4].Num(), p[5].Num()); }
        return m;
    }

    // ------------------------------------------------------------------ reading a .glb
    /// <summary>Reads the JSON chunk of a binary glTF and works out the values above.</summary>
    public static ModelFileInfo ReadGlb(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (br.ReadUInt32() != 0x46546C67) throw new InvalidDataException($"{path}: not a binary glTF");
        br.ReadUInt32(); br.ReadUInt32();
        int len = (int)br.ReadUInt32();
        if (br.ReadUInt32() != 0x4E4F534A) throw new InvalidDataException($"{path}: first chunk is not JSON");
        var json = JsonNode.Parse(Encoding.UTF8.GetString(br.ReadBytes(len)))!;
        byte[]? bin = null;
        if (fs.Position + 8 <= fs.Length)
        {
            int blen = (int)br.ReadUInt32();
            if (br.ReadUInt32() == 0x004E4942) bin = br.ReadBytes(blen);
        }
        return FromGltf(json, bin);
    }

    /// <param name="bin">the binary chunk (for the skins' inverse bind matrices), null: skinned meshes measured unposed</param>
    public static ModelFileInfo FromGltf(JsonNode j, byte[]? bin = null)
    {
        var info = new ModelFileInfo();
        var nodes = j["nodes"] as JsonArray ?? new JsonArray();
        var acc = j["accessors"] as JsonArray ?? new JsonArray();
        var meshes = j["meshes"] as JsonArray ?? new JsonArray();
        int sceneIx = j["scene"].Int(0);
        var roots = j["scenes"]?[sceneIx]?["nodes"] as JsonArray ?? new JsonArray();
        if (roots.Count > 0) info.Fourcc = nodes[roots[0].Int()]?["extras"]?["fourcc"].Str() ?? "";
        string fourcc = info.Fourcc;

        // animations: duration = the largest input time
        if (j["animations"] is JsonArray anims)
            foreach (var a in anims)
            {
                double dur = 0;
                if (a?["samplers"] is JsonArray ss)
                    foreach (var s in ss)
                    {
                        var ac = acc[s!["input"].Int()];
                        if (ac?["max"] is JsonArray mx && mx.Count > 0) dur = Math.Max(dur, mx[0].Num());
                    }
                string name = a?["name"].Str() ?? "";
                info.Clips[name] = dur;
            }

        // world matrices of all nodes (rest pose)
        var world = new M4?[nodes.Count];
        void WalkWorld(int ix, M4 parent)
        {
            var n = nodes[ix];
            if (n == null) return;
            var m = parent * M4.Of(n);
            world[ix] = m;
            if (n["children"] is JsonArray ch) foreach (var c in ch) WalkWorld(c.Int(), m);
        }
        foreach (var r in roots) WalkWorld(r.Int(), M4.Identity);
        // a skinned mesh at rest: vertex in the world = joint world * inverse bind * mesh world * v (three.js SkinnedMesh
        // with the bind matrix of its node; at rest every joint gives the same matrix - the first one is taken)
        M4? SkinMatrix(JsonNode n, M4 meshWorld)
        {
            var skin = j["skins"]?[n["skin"]!.Int()];
            var joints = skin?["joints"] as JsonArray;
            if (bin == null || joints == null || joints.Count == 0 || skin!["inverseBindMatrices"] is not JsonNode ibmN) return null;
            var ac = acc[ibmN.Int()];
            var bv = j["bufferViews"]?[ac?["bufferView"].Int() ?? 0];
            if (ac == null || bv == null) return null;
            int off = bv["byteOffset"].Int() + ac["byteOffset"].Int();
            if (off + 64 > bin.Length) return null;
            var mm = new double[16];
            for (int k = 0; k < 16; k++) mm[k] = BitConverter.ToSingle(bin, off + k * 4);
            var jw = world[joints[0].Int()];
            if (jw == null) return null;
            return jw.Value * M4.FromColumnMajor(mm) * meshWorld;
        }
        // walk the scene: links, drawn meshes
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        bool any = false;
        double pX0 = double.MaxValue, pY0 = double.MaxValue, pZ0 = double.MaxValue, pX1 = double.MinValue, pY1 = double.MinValue, pZ1 = double.MinValue;
        void Walk(int ix, M4 parent, uint? attrAbove, bool foliageParent)
        {
            var n = nodes[ix];
            if (n == null) return;
            var m = parent * M4.Of(n);
            string name = n["name"].Str();
            if (name.StartsWith("link_", StringComparison.Ordinal) && !info.Links.ContainsKey(name.Substring(5))) info.Links[name.Substring(5)] = m.Origin;
            uint? attr = n["extras"]?["attr"] is JsonNode an ? (uint)(long)an.Num() : attrAbove;
            bool foliage = name.Contains("_foliage");
            if (n["mesh"] is JsonNode mi && !foliage && !foliageParent)
            {
                bool skinned = n["skin"] != null;
                var mesh = meshes[mi.Int()];
                // the mesh's box in the model frame, unless it is a part loadModel drops
                var bmin = new V3(double.MaxValue, double.MaxValue, double.MaxValue); var bmax = new V3(double.MinValue, double.MinValue, double.MinValue);
                int verts = 0; bool has = false;
                if (mesh?["primitives"] is JsonArray prims)
                    foreach (var p in prims)
                    {
                        var pa = p?["attributes"]?["POSITION"];
                        if (pa == null) continue;
                        var ac = acc[pa.Int()];
                        verts += ac?["count"].Int() ?? 0;
                        if (ac?["min"] is not JsonArray mn || ac["max"] is not JsonArray mx) continue;
                        has = true;
                        bmin = new V3(Math.Min(bmin.X, mn[0].Num()), Math.Min(bmin.Y, mn[1].Num()), Math.Min(bmin.Z, mn[2].Num()));
                        bmax = new V3(Math.Max(bmax.X, mx[0].Num()), Math.Max(bmax.Y, mx[1].Num()), Math.Max(bmax.Z, mx[2].Num()));
                    }
                if (has && attr is uint pk && ((pk >> 18) & 1) != 0 && (pk & 1) != 0 && fourcc != "Wall")
                    foreach (var c in Corners(bmin, bmax))
                    {
                        var w = m.Apply(c);
                        pX0 = Math.Min(pX0, w.X); pY0 = Math.Min(pY0, w.Y); pZ0 = Math.Min(pZ0, w.Z);
                        pX1 = Math.Max(pX1, w.X); pY1 = Math.Max(pY1, w.Y); pZ1 = Math.Max(pZ1, w.Z);
                    }
                if (has && !Dropped(attr, fourcc, skinned, verts, m, bmin, bmax))
                {
                    var sm = skinned ? SkinMatrix(n, m) ?? m : m;
                    foreach (var c in Corners(bmin, bmax))
                    {
                        var w = sm.Apply(c);
                        minX = Math.Min(minX, w.X); minY = Math.Min(minY, w.Y); minZ = Math.Min(minZ, w.Z);
                        maxX = Math.Max(maxX, w.X); maxY = Math.Max(maxY, w.Y); maxZ = Math.Max(maxZ, w.Z);
                    }
                    any = true;
                }
            }
            if (n["children"] is JsonArray ch) foreach (var c in ch) Walk(c.Int(), m, attr, foliage || foliageParent);
        }
        foreach (var r in roots) Walk(r.Int(), M4.Identity, null, false);
        if (any) { info.BoxMin = new V3(minX, minY, minZ); info.BoxMax = new V3(maxX, maxY, maxZ); }
        if (pX1 >= pX0) { info.PickMin = new V3(pX0, pY0, pZ0); info.PickMax = new V3(pX1, pY1, pZ1); }
        return info;
    }

    // engine/parts.js helperPart + the hull rule of engine/assets.js loadModel
    static bool Dropped(uint? attr, string fourcc, bool skinned, int verts, M4 m, V3 bmin, V3 bmax)
    {
        if (attr is not uint a) return false;
        const int PICK = 18, SHADOW = 19;
        if (((a >> PICK) & 1) != 0 && fourcc != "Wall") return true;                       // pick volume (additive Deko parts aside)
        if (fourcc != "Anim" && fourcc != "Char" && fourcc != "Wall" && ((a >> SHADOW) & 1) != 0) return true;   // shadow model
        if ((a & 1) == 0) return true;                                                     // a lower LOD
        if ((a >> 5) == 0 && !skinned && verts <= 24)
        {
            // flag-less low-poly hulls (whole-object pick/collision volumes): a volume, not a flat surface
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (var c in Corners(bmin, bmax)) { var w = m.Apply(c); x0 = Math.Min(x0, w.X); y0 = Math.Min(y0, w.Y); z0 = Math.Min(z0, w.Z); x1 = Math.Max(x1, w.X); y1 = Math.Max(y1, w.Y); z1 = Math.Max(z1, w.Z); }
            double dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
            if (Math.Max(dx, Math.Max(dy, dz)) > 8 && Math.Min(dx, Math.Min(dy, dz)) > 0.5) return true;
        }
        return false;
    }

    static IEnumerable<V3> Corners(V3 a, V3 b)
    {
        for (int i = 0; i < 8; i++) yield return new V3((i & 1) != 0 ? b.X : a.X, (i & 2) != 0 ? b.Y : a.Y, (i & 4) != 0 ? b.Z : a.Z);
    }

    /// <summary>A 4x4 affine matrix (column vectors, as glTF).</summary>
    readonly struct M4
    {
        // rows of the 3x4 part
        readonly double a, b, c, tx, d, e, f, ty, g, h, i, tz;
        M4(double a, double b, double c, double tx, double d, double e, double f, double ty, double g, double h, double i, double tz)
        { this.a = a; this.b = b; this.c = c; this.tx = tx; this.d = d; this.e = e; this.f = f; this.ty = ty; this.g = g; this.h = h; this.i = i; this.tz = tz; }
        public static readonly M4 Identity = new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);
        public V3 Origin => new(tx, ty, tz);
        public V3 Apply(V3 p) => new(a * p.X + b * p.Y + c * p.Z + tx, d * p.X + e * p.Y + f * p.Z + ty, g * p.X + h * p.Y + i * p.Z + tz);
        public static M4 operator *(M4 l, M4 r) => new(
            l.a * r.a + l.b * r.d + l.c * r.g, l.a * r.b + l.b * r.e + l.c * r.h, l.a * r.c + l.b * r.f + l.c * r.i, l.a * r.tx + l.b * r.ty + l.c * r.tz + l.tx,
            l.d * r.a + l.e * r.d + l.f * r.g, l.d * r.b + l.e * r.e + l.f * r.h, l.d * r.c + l.e * r.f + l.f * r.i, l.d * r.tx + l.e * r.ty + l.f * r.tz + l.ty,
            l.g * r.a + l.h * r.d + l.i * r.g, l.g * r.b + l.h * r.e + l.i * r.h, l.g * r.c + l.h * r.f + l.i * r.i, l.g * r.tx + l.h * r.ty + l.i * r.tz + l.tz);
        public static M4 FromColumnMajor(double[] mm) => new(mm[0], mm[4], mm[8], mm[12], mm[1], mm[5], mm[9], mm[13], mm[2], mm[6], mm[10], mm[14]);
        /// <summary>the local matrix of a glTF node (matrix, or T * R * S)</summary>
        public static M4 Of(JsonNode n)
        {
            if (n["matrix"] is JsonArray mm && mm.Count == 16)
            {
                double M(int k) => mm[k].Num();       // column-major
                return new(M(0), M(4), M(8), M(12), M(1), M(5), M(9), M(13), M(2), M(6), M(10), M(14));
            }
            double tx = 0, ty = 0, tz = 0, qx = 0, qy = 0, qz = 0, qw = 1, sx = 1, sy = 1, sz = 1;
            if (n["translation"] is JsonArray t) { tx = t[0].Num(); ty = t[1].Num(); tz = t[2].Num(); }
            if (n["rotation"] is JsonArray q) { qx = q[0].Num(); qy = q[1].Num(); qz = q[2].Num(); qw = q[3].Num(1); }
            if (n["scale"] is JsonArray s) { sx = s[0].Num(1); sy = s[1].Num(1); sz = s[2].Num(1); }
            double xx = qx * qx, yy = qy * qy, zz = qz * qz, xy = qx * qy, xz = qx * qz, yz = qy * qz, wx = qw * qx, wy = qw * qy, wz = qw * qz;
            return new(
                (1 - 2 * (yy + zz)) * sx, 2 * (xy - wz) * sy, 2 * (xz + wy) * sz, tx,
                2 * (xy + wz) * sx, (1 - 2 * (xx + zz)) * sy, 2 * (yz - wx) * sz, ty,
                2 * (xz - wy) * sx, 2 * (yz + wx) * sy, (1 - 2 * (xx + yy)) * sz, tz);
        }
    }

    /// <summary>modelinfo.json for every .glb of a models folder (what the prepare tool writes into assets/).</summary>
    public static JsonObject BuildAll(string modelsDir)
    {
        var models = new JsonObject();
        var files = Directory.GetFiles(modelsDir, "*.glb");
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var f in files)
        {
            try { models[Path.GetFileNameWithoutExtension(f).ToLowerInvariant()] = ReadGlb(f).ToJson(); }
            catch (Exception e) { Log.For("Content.Models").Warn($"cannot read {f}: {e.Message}"); }
        }
        return new JsonObject { ["version"] = 1, ["models"] = models };
    }
}
