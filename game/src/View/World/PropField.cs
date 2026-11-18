// Static props (trees, rocks, bushes, stone deposits, the map's decoration) drawn instanced: one
// MultiMeshInstance3D per model part. Each frame the camera moved, the instances in the view are packed to the front
// of the instance buffers (one draw call per model part, only visible instances are processed).
//
// A part's own transform in the model (the converter's GSF frame: turned and mirrored - x -1) is baked into a copy of
// its mesh, triangles reversed where it mirrors (assets.js mirroredGeometry), so an instance's transform is only the
// placement on the map. A model's parts are its visible meshes in the template's default look (finished, intact,
// first epoch; helpers, lower LODs and billboards dropped by ModelLibrary); billboards and foliage come from the
// foliage field (FoliageField, treeSprites).
//
// Ported from remake/src/engine/props.js PropField (pw-exporter 98c7703). The see-through holes of tree canopies
// (applyHoles) are done for the foliage sprites (FoliageField.SetHoles); the trunks' materials are the models' own.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.View.Materials;
using PwRemake.View.Models;

namespace PwRemake.View.World;

public sealed class PropKind
{
    public string Name = "";
    public readonly List<(ArrayMesh mesh, bool shadow)> Parts = new();
    /// <summary>the parts' surfaces' own materials (the setting textures are made from them: ApplySetting)</summary>
    public readonly List<Material?[]> BaseMats = new();
    /// <summary>the setting whose textures the kind's building atlases take (SettingMaterials; null: plain) - a
    /// decoration of a model with setting textures is one kind per setting ("hu_ruin_arena@icewaste")</summary>
    public string? Setting;
    public readonly List<PropInstance> Inst = new();
    public readonly List<MultiMeshInstance3D> Meshes = new();
    /// <summary>the model's bounding sphere (centre in the model's frame, radius)</summary>
    public Vector3 SphereCenter;
    public float SphereRadius;
    public bool CastShadow = true;
    public ModelTemplate? Template;
    /// <summary>the instance buffer (12 floats per instance, the multimeshes' capacity)</summary>
    public float[] Buf = Array.Empty<float>();
}

public sealed class PropInstance
{
    public Transform3D M;
    public Vector3 Center;
    public float Radius;
    public bool Alive = true;
    public PropKind Kind = null!;
    /// <summary>its model's particle emitters (the waterfalls' spray: Particles.AddStatic)</summary>
    public List<PwRemake.View.Fx.Particles.Emitter>? Emit;
}

public partial class PropField : Node3D
{
    readonly Dictionary<string, PropKind> _kinds = new(StringComparer.OrdinalIgnoreCase);
    bool _dirty = true;
    Transform3D _lastCam;
    Projection _lastProj;
    public IReadOnlyDictionary<string, PropKind> Kinds => _kinds;
    /// <summary>instances drawn after the last update, draw calls (parts with instances)</summary>
    public int Drawn { get; private set; }
    public int Calls { get; private set; }

    public PropField() { Name = "Props"; }
    /// <summary>the emitters of the props' models (null: none sprayed)</summary>
    public PwRemake.View.Fx.Particles? Particles;

    /// <summary>a model as a prop kind (addKind). castShadow: its parts cast shadows (the JS opts.castShadow); setting:
    /// its building atlases in that setting's textures (SettingMaterials).</summary>
    public PropKind? AddKind(string name, ModelTemplate? tpl, bool castShadow = true, string? setting = null)
    {
        if (_kinds.TryGetValue(name, out var k)) return k;
        if (tpl == null) return null;
        k = new PropKind { Name = name, CastShadow = castShadow, Template = tpl, Setting = setting };
        var root = tpl.Scene;
        Aabb? box = null;
        Walk(root, Transform3D.Identity, true, (mi, xf) =>
        {
            var mats = new List<Material?>();
            var baked = Bake(mi, xf, mats);
            if (baked == null) return;
            k.Parts.Add((baked, mi.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off));
            k.BaseMats.Add(mats.ToArray());
            var b = xf * mi.Mesh.GetAabb();
            box = box == null ? b : box.Value.Merge(b);
        });
        var bb = box ?? new Aabb(Vector3.Zero, Vector3.One);
        k.SphereCenter = bb.GetCenter();
        k.SphereRadius = bb.Size.Length() / 2;
        if (setting != null) ApplySetting(k);
        _kinds[name] = k;
        return k;
    }

    /// <summary>the kind's surfaces in its setting's textures, or plain with the option off (SettingMaterials)</summary>
    static void ApplySetting(PropKind k)
    {
        for (int p = 0; p < k.Parts.Count; p++)
        {
            var mesh = k.Parts[p].mesh;
            var mats = k.BaseMats[p];
            for (int s = 0; s < mats.Length && s < mesh.GetSurfaceCount(); s++)
                if (mats[s] is Material m) mesh.SurfaceSetMaterial(s, SettingMaterials.Variant(m, k.Setting));
        }
    }

    int _settingEpoch = SettingMaterials.Epoch;

    /// <summary>the option "Setting textures on buildings" changed: the kinds with a setting again</summary>
    public void RefreshSettings()
    {
        if (_settingEpoch == SettingMaterials.Epoch) return;
        _settingEpoch = SettingMaterials.Epoch;
        foreach (var k in _kinds.Values) if (k.Setting != null) ApplySetting(k);
    }

    /// <summary>whether a model has building atlases with setting textures (its kind is made per setting)</summary>
    public static bool UsesSettings(ModelTemplate tpl)
    {
        foreach (var p in tpl.Parts)
            if (p.Surfaces != null) foreach (var m in p.Surfaces) if (SettingMaterials.HasVariants(m)) return true;
        return false;
    }

    static void Walk(Node n, Transform3D parent, bool visible, Action<MeshInstance3D, Transform3D> f)
    {
        foreach (var ch in n.GetChildren())
        {
            if (ch is not Node3D c) continue;
            var xf = parent * c.Transform;
            bool vis = visible && c.Visible;
            if (!vis) continue;
            if (c is MeshInstance3D mi && mi.Mesh != null) f(mi, xf);
            Walk(c, xf, vis, f);
        }
    }

    /// <summary>a copy of the part's mesh with its transform in the model baked in (triangles reversed when it mirrors)</summary>
    static ArrayMesh? Bake(MeshInstance3D mi, Transform3D xf, List<Material?> mats)
    {
        var src = mi.Mesh;
        bool flip = xf.Basis.Determinant() < 0;
        var nb = xf.Basis.Inverse().Transposed();
        var outMesh = new ArrayMesh();
        for (int s = 0; s < src.GetSurfaceCount(); s++)
        {
            var arr = src.SurfaceGetArrays(s);
            if (arr.Count == 0) continue;
            var v = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            for (int i = 0; i < v.Length; i++) v[i] = xf * v[i];
            arr[(int)Mesh.ArrayType.Vertex] = v;
            if (arr[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil)
            {
                var nr = arr[(int)Mesh.ArrayType.Normal].AsVector3Array();
                for (int i = 0; i < nr.Length; i++) nr[i] = (nb * nr[i]).Normalized();
                arr[(int)Mesh.ArrayType.Normal] = nr;
            }
            if (arr[(int)Mesh.ArrayType.Tangent].VariantType != Variant.Type.Nil)
            {
                var tg = arr[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
                for (int i = 0; i + 3 < tg.Length; i += 4)
                {
                    var t = (xf.Basis * new Vector3(tg[i], tg[i + 1], tg[i + 2])).Normalized();
                    tg[i] = t.X; tg[i + 1] = t.Y; tg[i + 2] = t.Z; if (flip) tg[i + 3] = -tg[i + 3];
                }
                arr[(int)Mesh.ArrayType.Tangent] = tg;
            }
            // skinned parts are drawn in their bind pose (static scenery)
            arr[(int)Mesh.ArrayType.Bones] = default;
            arr[(int)Mesh.ArrayType.Weights] = default;
            if (flip)
            {
                if (arr[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.Nil)
                {
                    var ix = arr[(int)Mesh.ArrayType.Index].AsInt32Array();
                    for (int i = 0; i + 2 < ix.Length; i += 3) (ix[i + 1], ix[i + 2]) = (ix[i + 2], ix[i + 1]);
                    arr[(int)Mesh.ArrayType.Index] = ix;
                }
                else
                {
                    // (no index: make one)
                    var ix = new int[v.Length];
                    for (int i = 0; i + 2 < ix.Length; i += 3) { ix[i] = i; ix[i + 1] = i + 2; ix[i + 2] = i + 1; }
                    arr[(int)Mesh.ArrayType.Index] = ix;
                }
            }
            var prim = src is ArrayMesh am ? am.SurfaceGetPrimitiveType(s) : Mesh.PrimitiveType.Triangles;
            outMesh.AddSurfaceFromArrays(prim, arr);
            var mat = mi.GetActiveMaterial(s);
            if (mat != null) outMesh.SurfaceSetMaterial(outMesh.GetSurfaceCount() - 1, mat);
            mats.Add(mat);
        }
        return outMesh.GetSurfaceCount() > 0 ? outMesh : null;
    }

    /// <summary>
    /// an instance (returns its handle). q (optional): the map orientation [x, y, z, w] already in the world frame
    /// (Frame.MapQuat of the stored quaternion: tilted plateaus and cliffs); else it turns by rotY.
    /// </summary>
    public PropInstance? Add(string name, double x, double y, double z, double rotY, double scale, double[]? q = null)
    {
        if (!_kinds.TryGetValue(name, out var k)) return null;
        var quat = q != null ? new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]).Normalized() : new Quaternion(Vector3.Up, (float)rotY);
        var basis = new Basis(quat).Scaled(new Vector3((float)scale, (float)scale, (float)scale));
        var m = new Transform3D(basis, new Vector3((float)x, (float)y, (float)z));
        // (the model's bounding sphere moved with the instance: a model whose parts lie far from its origin - the water
        // sanctuary's ring pieces, 30-60 m off - was culled while its parts were on the screen)
        var inst = new PropInstance { M = m, Center = m * k.SphereCenter, Radius = k.SphereRadius * (float)scale + 4, Kind = k };
        k.Inst.Add(inst);
        // particle emitters (waterfall spray ...): sprayed for every instance (props.js add -> particles.addStatic)
        if (Particles != null && k.Template != null && k.Template.Emitters.Count > 0) inst.Emit = Particles.AddStatic(k.Template, m);
        _dirty = true;
        return inst;
    }

    public void Remove(PropInstance? h)
    {
        if (h == null || !h.Alive) return;
        h.Alive = false;
        Particles?.RemoveStatic(h.Emit);
        _dirty = true;
    }

    /// <summary>makes the multimeshes (props.js build): one per part of every kind, sized for its instances</summary>
    public void Build()
    {
        foreach (var k in _kinds.Values)
        {
            if (k.Meshes.Count > 0 && k.Meshes[0].Multimesh.InstanceCount >= k.Inst.Count) continue;
            foreach (var m in k.Meshes) { RemoveChild(m); m.QueueFree(); }
            k.Meshes.Clear();
            int cap = Math.Max(16, k.Inst.Count);
            k.Buf = new float[cap * 12];
            foreach (var (mesh, shadow) in k.Parts)
            {
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = cap, VisibleInstanceCount = 0 };
                var mmi = new MultiMeshInstance3D
                {
                    Multimesh = mm, Name = k.Name,
                    CastShadow = k.CastShadow && shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                    Visible = false,
                };
                AddChild(mmi);
                k.Meshes.Add(mmi);
            }
        }
        _dirty = true;
    }

    /// <summary>every frame: packs the instances in the camera's view (only when the camera moved or props changed)</summary>
    public void UpdateView(Camera3D camera, bool force = false)
    {
        var cam = camera.GlobalTransform;
        var proj = camera.GetCameraProjection();
        if (!force && !_dirty && cam.IsEqualApprox(_lastCam) && proj == _lastProj) return;
        _dirtyWhy = _dirty;
        _lastCam = cam; _lastProj = proj;
        PerfProbe.P?.Add(_dirtyWhy ? "props:rebuild (changed)" : "props:rebuild (camera)", 0);
        _dirtyWhy = false;
        Build();
        // (after Build, which marks the field changed: with _dirty cleared before it, every frame packed and sent all the
        // instance buffers again, the camera still or not)
        _dirty = false;
        var planes = Frustum.Of(camera, ref _planes)!;
        int drawn = 0, calls = 0;
        var vis = _vis;
        foreach (var k in _kinds.Values)
        {
            vis.Clear();
            foreach (var it in k.Inst) if (it.Alive && InFrustum(planes, it.Center, it.Radius)) vis.Add(it);
            int n = vis.Count;
            var _buf = k.Buf;
            Aabb box = default;
            for (int i = 0; i < n; i++)
            {
                var m = vis[i].M;
                int o = i * 12;
                _buf[o] = m.Basis.X.X; _buf[o + 1] = m.Basis.Y.X; _buf[o + 2] = m.Basis.Z.X; _buf[o + 3] = m.Origin.X;
                _buf[o + 4] = m.Basis.X.Y; _buf[o + 5] = m.Basis.Y.Y; _buf[o + 6] = m.Basis.Z.Y; _buf[o + 7] = m.Origin.Y;
                _buf[o + 8] = m.Basis.X.Z; _buf[o + 9] = m.Basis.Y.Z; _buf[o + 10] = m.Basis.Z.Z; _buf[o + 11] = m.Origin.Z;
                var b = new Aabb(vis[i].Center - Vector3.One * vis[i].Radius, Vector3.One * vis[i].Radius * 2);
                box = i == 0 ? b : box.Merge(b);
            }
            foreach (var mmi in k.Meshes)
            {
                var mm = mmi.Multimesh;
                if (n > 0)
                {
                    // (the whole buffer: the part after n keeps old data that is not drawn)
                    mm.Buffer = _buf;
                    mm.CustomAabb = box;
                    calls++;
                }
                mm.VisibleInstanceCount = n;
                mmi.Visible = n > 0;            // (no draw call, nothing in the shadow pass)
            }
            drawn += n;
        }
        Drawn = drawn; Calls = calls;
    }

    static bool InFrustum(Plane[] planes, Vector3 c, float r) => Frustum.Has(planes, c, r);
    Plane[] _planes = System.Array.Empty<Plane>();
    bool _dirtyWhy;
    readonly List<PropInstance> _vis = new();
}
