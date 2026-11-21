"""
refit.py - put a new (generated or hand-made) character mesh on an original ParaWorld skeleton.

The new mesh keeps every original animation: it is fitted to the original model, gets skin weights from it and is
bound to the original skeleton, so the game plays it with the original clips and attachment points (link_*).

Run with Blender (4.2 or newer; tested with the bpy 5.3 module):
    blender -b --python refit.py -- --original <prepared data>/assets/models/cole_s0.glb --new cole.glb
            --out-dir <remake>/content/hd --preview cole.png
or with the bpy module:  python refit.py --original ... --new ... --out-dir ...

Steps
  1. Load the original glb (skeleton, skinned meshes, link_* empties, all animations). The body = its skinned meshes
     of 60 faces or more (tiny skinned helpers - pick volumes, shadow parts - stay).
  2. Load the new mesh (glb / gltf / obj / fbx / ply), join it into one object, weld it, reduce it to --tris.
  3. Place it: scaled to the original's height, standing on the same ground, centred on the upper body (the
     original's rest pose is mid-stride). The facing (--facing auto) is tried in all four directions; the one whose
     pose fit (4) ends closest wins.
  4. Pose fit on dense samples of both surfaces: a rigid fit (turn about the vertical, scale, move), then an
     articulated ICP: each long bone (legs, arms, spine, neck - not hands, fingers, toes) turned about its joint
     so that its part of the original covers the new mesh, parents first, damped, several rounds.
  5. Skin weights (robust transfer, after Abdrashitov et al. 2023): a new vertex takes the weights of the nearest
     point of the posed original only when it is close, faces the same way and its bone is near; the rest are
     inpainted over the new mesh (Laplace equation). Then smoothed a little, 4 bones at most, normalised.
  6. The new mesh is moved from the fitted pose back into the skeleton's rest pose (inverse skinning), so that
     the rest pose, every animation and every attachment point stay exactly as in the original.
  7. Written out (below); materials get the original's gsf_* extras plus pw_style = "painterly" (the remake's
     PainterlyLook shader).

Output: --out-dir <content/hd>  writes assets/models/<model>.glb + assets/tex/<model>_hd_<n>.png for the remake: the
         original glb file with only the body mesh swapped (sounds, walk sets, links, skin and animations stay as
         they are), textures as files next to the converted ones (the remake loads textures by file name)
        --out <file.glb>      a plain glb exported by Blender (for other tools)
Options: --tris 24000  --facing auto|0|90|180|270  --keep <mesh name part>[,...]  --tex-size 2048
         --preview <png>  (the new model at rest and in a few animation frames)
Needs numpy and scipy (Blender has numpy; scipy: "<blender>/<version>/python/bin/python -m pip install scipy").
"""
import sys, os, math, argparse, time
import numpy as np
import bpy, bmesh, mathutils
from mathutils.bvhtree import BVHTree

try:
    from scipy.spatial import cKDTree
except ImportError:  # Blender's bundled Python has no scipy: a small grid fallback
    cKDTree = None


# --------------------------------------------------------------------------------------------- helpers

def log(*a):
    print('[refit]', *a, flush=True)


class GridNN:
    """Nearest neighbour without scipy: uniform grid, search the 27 neighbouring cells (good enough here)."""
    def __init__(self, pts, cell):
        self.p = pts; self.c = cell
        keys = np.floor(pts / cell).astype(np.int64)
        self.cells = {}
        for i, k in enumerate(map(tuple, keys)): self.cells.setdefault(k, []).append(i)
        self.cells = {k: np.array(v) for k, v in self.cells.items()}

    def query(self, q):
        out = np.full(len(q), 1e9); keys = np.floor(q / self.c).astype(np.int64)
        offs = [(a, b, c) for a in (-1, 0, 1) for b in (-1, 0, 1) for c in (-1, 0, 1)]
        for i, k in enumerate(keys):
            best = 1e9
            for o in offs:
                idx = self.cells.get((k[0]+o[0], k[1]+o[1], k[2]+o[2]))
                if idx is None: continue
                d = np.min(np.sum((self.p[idx]-q[i])**2, 1))
                if d < best: best = d
            out[i] = math.sqrt(best) if best < 1e9 else self.c * 2
        return out, None


def nn(pts, cell):
    if cKDTree is not None:
        t = cKDTree(pts)
        return lambda q: t.query(q)[0]
    g = GridNN(pts, cell)
    return lambda q: g.query(q)[0]


def rotvec_to_mat(r):
    a = float(np.linalg.norm(r))
    if a < 1e-9: return np.eye(4)
    m = mathutils.Matrix.Rotation(a, 4, mathutils.Vector(r / a))
    return np.array(m)


def mesh_arrays(obj, depsgraph=None, world=True):
    """Vertices (world), triangles, the loop UVs per triangle corner are not needed here."""
    o = obj.evaluated_get(depsgraph) if depsgraph else obj
    me = o.to_mesh()
    me.calc_loop_triangles()
    v = np.empty(len(me.vertices) * 3); me.vertices.foreach_get('co', v); v = v.reshape(-1, 3)
    t = np.empty(len(me.loop_triangles) * 3, dtype=np.int64); me.loop_triangles.foreach_get('vertices', t)
    if world:
        M = np.array(obj.matrix_world); v = v @ M[:3, :3].T + M[:3, 3]
    o.to_mesh_clear()
    return v, t.reshape(-1, 3)


def outward(nrm, pts):
    """Normals turned to point away from the middle (the winding of a mirrored import can be either way)."""
    c = pts.mean(0)
    return nrm if np.mean(np.sum(nrm * (pts - c), 1)) >= 0 else -nrm


def sample_surface(v, t, n, rng, attrs=None, normals=False):
    """n random points on the triangles (area weighted). With attrs (per vertex rows) also their interpolation, with
    normals also the (outward) face normal of each point."""
    a, b, c = v[t[:, 0]], v[t[:, 1]], v[t[:, 2]]
    area = 0.5 * np.linalg.norm(np.cross(b - a, c - a), axis=1)
    idx = rng.choice(len(t), n, p=area / area.sum())
    r1, r2 = rng.random(n), rng.random(n)
    s = np.sqrt(r1)
    l0, l1, l2 = 1 - s, s * (1 - r2), s * r2
    pts = l0[:, None] * a[idx] + l1[:, None] * b[idx] + l2[:, None] * c[idx]
    out = [pts]
    if attrs is not None:
        out.append(l0[:, None] * attrs[t[idx, 0]] + l1[:, None] * attrs[t[idx, 1]] + l2[:, None] * attrs[t[idx, 2]])
    if normals:
        fn = np.cross(b - a, c - a)[idx]; fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-12)
        out.append(outward(fn, pts))
    return out[0] if len(out) == 1 else tuple(out)


# --------------------------------------------------------------------------------------------- skeleton

class Skeleton:
    """Forward kinematics of a Blender armature in numpy (rotations only, Blender's pose convention:
    pose[i] = pose[parent] @ (rest[parent]^-1 @ rest[i]) @ basis[i])."""
    def __init__(self, arm):
        self.arm = arm
        self.names = [b.name for b in arm.data.bones]
        self.index = {n: i for i, n in enumerate(self.names)}
        self.parent = [self.index[b.parent.name] if b.parent else -1 for b in arm.data.bones]
        self.rest = np.array([np.array(b.matrix_local) for b in arm.data.bones])      # armature space
        self.rest_inv = np.linalg.inv(self.rest)
        self.local = np.array([self.rest_inv[p] @ self.rest[i] if p >= 0 else self.rest[i]
                               for i, p in enumerate(self.parent)])
        self.W = np.array(arm.matrix_world); self.W_inv = np.linalg.inv(self.W)
        self.length = np.array([b.length for b in arm.data.bones])
        order, seen = [], set()
        def visit(i):
            if i in seen: return
            if self.parent[i] >= 0: visit(self.parent[i])
            seen.add(i); order.append(i)
        for i in range(len(self.names)): visit(i)
        self.order = order
        self.depth = [0] * len(self.names)
        for i in order: self.depth[i] = self.depth[self.parent[i]] + 1 if self.parent[i] >= 0 else 0

    def identity(self):
        return np.tile(np.eye(3), (len(self.names), 1, 1))

    def pose(self, basis):
        """basis: (B,3,3) rotations. Returns (B,4,4) pose matrices in armature space."""
        P = np.empty_like(self.rest)
        for i in self.order:
            B = np.eye(4); B[:3, :3] = basis[i]
            p = self.parent[i]
            P[i] = (P[p] @ self.local[i] @ B) if p >= 0 else (self.local[i] @ B)
        return P

    def skin_mats(self, basis, G=None):
        """World-space skinning matrices; G is an optional extra world transform (global placement)."""
        S = self.W @ self.pose(basis) @ self.rest_inv @ self.W_inv
        return S if G is None else G @ S

    def segments(self, basis):
        """World-space head and tail of every bone in a pose."""
        P = self.W @ self.pose(basis)
        head = P[:, :3, 3]
        tail = head + P[:, :3, 1] / np.maximum(np.linalg.norm(P[:, :3, 1], axis=1, keepdims=True), 1e-12) * \
            (self.length * np.cbrt(abs(np.linalg.det(self.W[:3, :3]))))[:, None]
        return head, tail

    def subtree(self, i):
        out, stack = [], [i]
        while stack:
            j = stack.pop(); out.append(j)
            stack += [k for k, p in enumerate(self.parent) if p == j]
        return out


def skin_dir(n, Wt, S):
    """Directions (normals) through the skinning matrices' linear parts, normalised."""
    out = np.zeros_like(n)
    for b in np.nonzero(Wt.any(0))[0]:
        w = Wt[:, b]; m = w > 0
        out[m] += w[m, None] * (n[m] @ S[b][:3, :3].T)
    return out / np.maximum(np.linalg.norm(out, axis=1, keepdims=True), 1e-12)


def skin(v, Wt, S):
    """Linear blend skinning: v (V,3), Wt (V,B) weights, S (B,4,4)."""
    vh = np.c_[v, np.ones(len(v))]
    out = np.zeros_like(v)
    for b in np.nonzero(Wt.any(0))[0]:
        w = Wt[:, b]; m = w > 0
        out[m] += w[m, None] * (vh[m] @ S[b].T)[:, :3]
    return out


# --------------------------------------------------------------------------------------------- loading

def load_original(path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=path)
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    skinned = [o for o in bpy.data.objects if o.type == 'MESH' and any(m.type == 'ARMATURE' for m in o.modifiers)]
    return arm, skinned


def rest_data(arm, meshes, skel):
    """Rest-pose vertices, triangles and weight matrix of all given meshes together (world space)."""
    arm.data.pose_position = 'REST'
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    V, T, W = [], [], []
    off = 0
    rest_data.per_object = {}
    for o in meshes:
        v, t = mesh_arrays(o, dg)
        rest_data.per_object[o.name] = v
        wt = np.zeros((len(o.data.vertices), len(skel.names)))
        gidx = {g.index: skel.index.get(g.name, -1) for g in o.vertex_groups}
        for vi, vx in enumerate(o.data.vertices):
            for g in vx.groups:
                b = gidx.get(g.group, -1)
                if b >= 0: wt[vi, b] += g.weight
        s = wt.sum(1); s[s == 0] = 1; wt /= s[:, None]
        V.append(v); T.append(t + off); W.append(wt); off += len(v)
    arm.data.pose_position = 'POSE'
    return np.concatenate(V), np.concatenate(T), np.concatenate(W)


def load_new(path):
    before = set(bpy.data.objects)
    ext = os.path.splitext(path)[1].lower()
    if ext in ('.glb', '.gltf'): bpy.ops.import_scene.gltf(filepath=path)
    elif ext == '.obj': bpy.ops.wm.obj_import(filepath=path)
    elif ext == '.fbx': bpy.ops.import_scene.fbx(filepath=path)
    elif ext == '.ply': bpy.ops.wm.ply_import(filepath=path)
    else: raise SystemExit(f'unknown mesh format {ext}')
    new = [o for o in bpy.data.objects if o not in before]
    meshes = [o for o in new if o.type == 'MESH']
    if not meshes: raise SystemExit('no mesh in ' + path)
    # bake world transforms, drop any armature / empties that came with it
    for o in meshes:
        mw = o.matrix_world.copy(); o.parent = None; o.matrix_world = mw
        for m in list(o.modifiers): o.modifiers.remove(m)
    for o in new:
        if o.type != 'MESH': bpy.data.objects.remove(o, do_unlink=True)
    bpy.ops.object.select_all(action='DESELECT')
    for o in meshes: o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1: bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    for g in list(obj.vertex_groups): obj.vertex_groups.remove(g)
    obj.name = 'HD_body'
    return obj


def decimate(obj, tris):
    me = obj.data
    n = sum(len(p.vertices) - 2 for p in me.polygons)
    # weld duplicated vertices first (generators often split every UV seam)
    bm = bmesh.new(); bm.from_mesh(me)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6 * max(obj.dimensions))
    bm.to_mesh(me); bm.free()
    if n > tris:
        m = obj.modifiers.new('dec', 'DECIMATE'); m.ratio = tris / n; m.use_collapse_triangulate = True
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.modifier_apply(modifier=m.name)
    log(f'new mesh: {n} -> {sum(len(p.vertices) - 2 for p in obj.data.polygons)} triangles')
    bm = bmesh.new(); bm.from_mesh(obj.data)
    open_edges = sum(1 for e in bm.edges if e.is_boundary); bm.free()
    if open_edges:
        log(f'note: the new mesh has {open_edges} open edges (holes or separate shells) - they stay as they are')


# --------------------------------------------------------------------------------------------- fitting

def place(newv, origv, angle):
    """Turn (around z), scale and move new vertices onto the original's footprint."""
    a = math.radians(angle); c, s = math.cos(a), math.sin(a)
    R = np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])
    v = newv @ R.T
    h0 = origv[:, 2].max() - origv[:, 2].min(); h1 = v[:, 2].max() - v[:, 2].min()
    v = v * (h0 / h1)
    # horizontal centre from the upper body (the original's legs are mid-stride)
    def centre(p):
        z0, z1 = p[:, 2].min(), p[:, 2].max(); up = p[p[:, 2] > z0 + 0.55 * (z1 - z0)]
        return (up[:, :2].min(0) + up[:, :2].max(0)) / 2
    v[:, :2] += centre(origv) - centre(v)
    v[:, 2] += origv[:, 2].min() - v[:, 2].min()
    return v, R, h0 / h1


def kabsch(P, Q, w=None):
    """Rotation R minimising sum w |R P - Q|^2 (P, Q centred already)."""
    H = (P * (w[:, None] if w is not None else 1)).T @ Q
    U, _, Vt = np.linalg.svd(H)
    D = np.eye(3); D[2, 2] = np.sign(np.linalg.det(Vt.T @ U.T))
    return Vt.T @ D @ U.T


def limit_rotation(R, max_angle, damp):
    """Scale the rotation angle by damp and clamp it."""
    rv = np.array(mathutils.Matrix(R.tolist()).to_quaternion().to_axis_angle()[0]) * \
        mathutils.Matrix(R.tolist()).to_quaternion().angle
    a = np.linalg.norm(rv)
    if a < 1e-9: return np.eye(3)
    a2 = min(a * damp, max_angle)
    return np.array(mathutils.Matrix.Rotation(a2, 3, mathutils.Vector(rv / a)))


class Fitter:
    """Fits the original model onto the target points: rigid placement (yaw, scale, move) then articulated ICP
    (each bone's subtree turned about its joint, parents first)."""
    def __init__(self, skel, ov, ot, ow, target, height, target_n=None):
        self.skel, self.h = skel, height
        # the original is low-poly: work on dense samples of its surface (weights interpolated, outward normals)
        self.ov, self.ow, self.on = sample_surface(ov, ot, 8000, np.random.default_rng(3), ow, normals=True)
        self.tq, self.tn = target, target_n
        self.tree_t = cKDTree(target) if cKDTree else None
        self.dom = self.ow.argmax(1)
        self.basis = skel.identity()
        self.G = np.eye(4)

    def posed(self, with_normals=False):
        S = self.skel.skin_mats(self.basis, self.G)
        P = skin(self.ov, self.ow, S)
        return (P, skin_dir(self.on, self.ow, S)) if with_normals else P

    def pairs(self, P, cap, Pn=None, min_dot=0.3):
        """Correspondences both ways: (index into P, target point). With normals only pairs whose surfaces face the
        same way (an arm hanging next to the body is not pulled onto the body's side)."""
        d, j = self.tree_t.query(P)
        m = d < cap
        if Pn is not None and self.tn is not None: m &= np.sum(Pn * self.tn[j], 1) > min_dot
        idx = [np.nonzero(m)[0]]; tgt = [self.tq[j[m]]]
        tp = cKDTree(P); d2, k = tp.query(self.tq)
        m2 = d2 < cap
        if Pn is not None and self.tn is not None: m2 &= np.sum(Pn[k] * self.tn, 1) > min_dot
        idx.append(k[m2]); tgt.append(self.tq[m2])
        return np.concatenate(idx), np.concatenate(tgt)

    def error(self):
        P = self.posed()
        cap = 0.08 * self.h
        return (np.minimum(self.tree_t.query(P)[0], cap).mean() + np.minimum(cKDTree(P).query(self.tq)[0], cap).mean())

    def rigid(self, iters=25):
        for it in range(iters):
            P, Pn = self.posed(True)
            cap = self.h * (0.3 if it < 5 else 0.12 if it < 12 else 0.06)
            idx, Q = self.pairs(P, cap, Pn)
            A = P[idx]
            ca, cq = A.mean(0), Q.mean(0)
            # yaw only (both models stand upright), plus uniform scale and move
            Hm = (A - ca)[:, :2].T @ (Q - cq)[:, :2]
            ang = math.atan2(Hm[0, 1] - Hm[1, 0], Hm[0, 0] + Hm[1, 1])
            ang = max(-math.radians(10), min(math.radians(10), ang))   # small steps: keep the start facing
            c, s_ = math.cos(ang), math.sin(ang)
            R = np.array([[c, -s_, 0], [s_, c, 0], [0, 0, 1]])
            Ar = (A - ca) @ R.T
            sc = np.sqrt(((Q - cq) ** 2).sum() / max(((Ar) ** 2).sum(), 1e-12))
            sc = float(np.clip(sc, 0.9, 1.1))
            T = np.eye(4); T[:3, :3] = R * sc; T[:3, 3] = cq - (R * sc) @ ca
            self.G = T @ self.G

    def articulated(self, bones, iters=30):
        sk = self.skel
        sub = {b: np.isin(self.dom, sk.subtree(b)) for b in bones}
        for it in range(iters):
            cap = self.h * (0.15 if it < 8 else 0.08 if it < 18 else 0.04 if it < 26 else 0.025)
            damp = 0.6 if it < 20 else 0.4
            for b in bones:
                P, Pn = self.posed(True)
                idx, Q = self.pairs(P, cap, Pn)
                m = sub[b][idx]
                if m.sum() < 30: continue
                A, B = P[idx[m]], Q[m]
                S = sk.skin_mats(self.basis, self.G)
                Pose = sk.pose(self.basis)
                head = (self.G @ sk.W @ Pose[b])[:3, 3]           # joint position (world)
                Rw = kabsch(A - head, B - head)
                Rw = limit_rotation(Rw, math.radians(20), damp)
                # world rotation -> new basis of bone b
                GW = (self.G @ sk.W)[:3, :3]; GWn = GW / np.cbrt(np.linalg.det(GW))
                Ra = GWn.T @ Rw @ GWn                              # rotation in armature space
                parent_frame = (Pose[sk.parent[b]] @ sk.local[b])[:3, :3] if sk.parent[b] >= 0 else sk.local[b][:3, :3]
                newpose = Ra @ Pose[b][:3, :3]
                self.basis[b] = np.linalg.solve(parent_frame, newpose)
                # keep it a pure rotation
                U, _, Vt = np.linalg.svd(self.basis[b]); self.basis[b] = U @ Vt


def fit_bones(skel, ow, ov=None, height=1.0):
    """Bones worth turning, parents first: those that move a fair share of the body and a long part of it (legs,
    arms, spine, neck). Leaf bones (head, jaw), hands, fingers and toes stay: turning them to fit a different model distorts
    the fingers when the mesh is put back into the rest pose (blended weights of differently turned bones)."""
    mass = ow.sum(0) / ow.sum()
    sub = mass.copy()
    for i in reversed(skel.order):
        p = skel.parent[i]
        if p >= 0: sub[p] += sub[i]
    dom = ow.argmax(1)
    picked = []
    has_child = {p for p in skel.parent if p >= 0}
    for i in skel.order:
        if skel.parent[i] < 0 or sub[i] <= 0.02: continue
        # leaf bones (the head and the jaw end the neck chain) never: a turned jaw skewed the mouth in every clip
        if i not in has_child: continue
        if ov is not None:
            m = np.isin(dom, skel.subtree(i))
            if m.sum() < 10: continue
            ext = np.linalg.norm(ov[m].max(0) - ov[m].min(0))
            if ext < 0.15 * height: continue
        picked.append(i)
    return sorted(picked, key=lambda i: skel.depth[i])


# --------------------------------------------------------------------------------------------- weights

def vertex_normals(v, t):
    fn = np.cross(v[t[:, 1]] - v[t[:, 0]], v[t[:, 2]] - v[t[:, 0]])
    vn = np.zeros_like(v)
    for k in range(3): np.add.at(vn, t[:, k], fn)
    return vn / np.maximum(np.linalg.norm(vn, axis=1, keepdims=True), 1e-12)


def seg_dist(x, a, b):
    ab = b - a; t = np.clip(((x[:, None, :] - a[None]) * ab[None]).sum(2) / np.maximum((ab * ab).sum(1), 1e-12)[None], 0, 1)
    return np.linalg.norm(x[:, None, :] - (a[None] + t[..., None] * ab[None]), axis=2)


def transfer_weights(nv, ntris, pv, pt, ow, edges, height, segs=None, max_dist=0.04, max_angle=35, smooth_iter=3):
    """Robust skin weight transfer (after Abdrashitov et al. 2023): a new vertex takes the weights of the nearest
    point of the posed original only when that point is close AND faces the same way; the others (a hand resting on
    the hip would else take the thigh's weights and stretch when walking) get weights by harmonic inpainting over the
    new mesh (Laplace equation with the matched vertices fixed)."""
    import scipy.sparse as sp
    from scipy.sparse.linalg import spsolve
    bvh = BVHTree.FromPolygons([tuple(x) for x in pv], [tuple(x) for x in pt])
    nn_ = vertex_normals(nv, ntris)
    fno = np.cross(pv[pt[:, 1]] - pv[pt[:, 0]], pv[pt[:, 2]] - pv[pt[:, 0]])
    fno /= np.maximum(np.linalg.norm(fno, axis=1, keepdims=True), 1e-12)
    # both meshes' normals turned outwards (the winding of a mirrored import can be either way)
    nn_ = outward(nn_, nv); fno = outward(fno, pv[pt].mean(1))
    n = len(nv); B = ow.shape[1]
    W = np.zeros((n, B)); ok = np.zeros(n, bool)
    cos_max = math.cos(math.radians(max_angle)); dmax = max_dist * height
    for i, x in enumerate(nv):
        loc, nrm, fi, d = bvh.find_nearest(mathutils.Vector(x))
        if fi is None: continue
        a, b, c = pv[pt[fi]]
        v0, v1, v2 = b - a, c - a, np.array(loc) - a
        d00, d01, d11 = v0 @ v0, v0 @ v1, v1 @ v1; d20, d21 = v2 @ v0, v2 @ v1
        den = d00 * d11 - d01 * d01
        if abs(den) < 1e-20: l = (1.0, 0.0, 0.0)
        else:
            y = (d11 * d20 - d01 * d21) / den; z = (d00 * d21 - d01 * d20) / den; l = (1 - y - z, y, z)
        W[i] = l[0] * ow[pt[fi][0]] + l[1] * ow[pt[fi][1]] + l[2] * ow[pt[fi][2]]
        ok[i] = d < dmax and float(fno[fi] @ nn_[i]) > cos_max
    # semantic check: the bone that would carry the vertex must be near it (relative to the nearest bone)
    if segs is not None:
        used = np.nonzero(ow.any(0))[0]
        D = seg_dist(nv, segs[0][used], segs[1][used])
        dom = used[np.argmax(W[:, used], axis=1)]
        dd = D[np.arange(n), np.searchsorted(used, dom)]
        far = dd > 2.0 * D.min(1) + 0.03 * height
        log(f'weights: {(ok & far).sum()} matches dropped (their bone is far away)')
        ok &= ~far
    log(f'weights: {ok.mean()*100:.0f} % of the vertices matched, the rest inpainted')
    # graph Laplacian of the new mesh
    A = sp.coo_matrix((np.ones(len(edges) * 2), (np.r_[edges[:, 0], edges[:, 1]], np.r_[edges[:, 1], edges[:, 0]])), shape=(n, n)).tocsr()
    A.data[:] = 1.0
    L = sp.diags(np.asarray(A.sum(1)).ravel()) - A
    # islands without any matched vertex keep the nearest-point weights
    ncomp, lab = sp.csgraph.connected_components(A, directed=False)
    has = np.bincount(lab[ok], minlength=ncomp) > 0
    fixed = ok | ~has[lab]
    free = np.nonzero(~fixed)[0]; fx = np.nonzero(fixed)[0]
    if len(free):
        Lff = L[free][:, free].tocsc(); Lfx = L[free][:, fx]
        rhs = -(Lfx @ W[fx])
        W[free] = np.column_stack([spsolve(Lff, rhs[:, k]) if np.any(rhs[:, k]) else np.zeros(len(free)) for k in range(B)])
    # a little smoothing over the edges (removes the seams of nearest-point lookups)
    deg = np.asarray(A.sum(1)).ravel(); deg[deg == 0] = 1
    for _ in range(smooth_iter):
        W = 0.5 * W + 0.5 * (A @ W) / deg[:, None]
    # at most 4 bones per vertex, normalised
    W = np.clip(W, 0, None)
    if W.shape[1] > 4:
        thr = -np.sort(-W, axis=1)[:, 3:4]
        W[W < thr] = 0
    W[W < 0.01] = 0
    s_ = W.sum(1); s_[s_ == 0] = 1
    return W / s_[:, None]


def inverse_skin(v, W, S):
    M = np.einsum('vb,bij->vij', W, S)
    vh = np.c_[v, np.ones(len(v))]
    return np.einsum('vij,vj->vi', np.linalg.inv(M), vh)[:, :3]


# --------------------------------------------------------------------------------------------- textures / materials

def fix_materials(obj, ref_mat, tex_size):
    for m in obj.data.materials:
        if m is None: continue
        if ref_mat is not None and 'gsf_flags' not in m:
            for k in ('gsf_texture', 'gsf_flags'):
                if k in ref_mat: m[k] = ref_mat[k]
        m['pw_style'] = 'painterly'
        m.use_backface_culling = False
        if m.use_nodes:
            for n in m.node_tree.nodes:
                if n.type == 'TEX_IMAGE' and n.image and max(n.image.size) > tex_size:
                    f = tex_size / max(n.image.size)
                    n.image.scale(int(n.image.size[0] * f), int(n.image.size[1] * f))
                    log('texture', n.image.name, 'scaled to', tuple(n.image.size))
            # generated meshes are often shiny PBR: the game wants matte
            for n in m.node_tree.nodes:
                if n.type == 'BSDF_PRINCIPLED':
                    n.inputs['Metallic'].default_value = 0.0
                    for l in list(n.inputs['Metallic'].links) + list(n.inputs['Roughness'].links):
                        m.node_tree.links.remove(l)
                    n.inputs['Roughness'].default_value = 0.9


# --------------------------------------------------------------------------------------------- preview

def preview(arm, out, action_names):
    scene = bpy.context.scene
    hidden = [o for o in bpy.data.objects if o.type == 'MESH' and o.name not in ('HD_body',)]
    for o in hidden: o.hide_render = True
    world = bpy.data.worlds.new('w'); scene.world = world; world.use_nodes = True
    world.node_tree.nodes['Background'].inputs[0].default_value = (0.55, 0.55, 0.58, 1)
    l = bpy.data.lights.new('sun', 'SUN'); l.energy = 3
    lo = bpy.data.objects.new('sun', l); lo.rotation_euler = (math.radians(50), 0, math.radians(150)); scene.collection.objects.link(lo)
    cam_d = bpy.data.cameras.new('cam'); cam_d.type = 'ORTHO'
    cam = bpy.data.objects.new('cam', cam_d); scene.collection.objects.link(cam); scene.camera = cam
    scene.render.engine = 'CYCLES'; scene.cycles.samples = 16
    scene.render.resolution_x, scene.render.resolution_y = 360, 600
    scene.view_settings.view_transform = 'Standard'
    try: from PIL import Image
    except ImportError: Image = None          # (Blender's Python without Pillow: one picture per pose)
    shots = []
    arm.animation_data_create()
    for name, frame in [(None, 0)] + [(a, f) for a in action_names for f in (5,)]:
        if name and name not in bpy.data.actions: continue
        act = bpy.data.actions[name] if name else None
        arm.animation_data.action = act
        if act is not None:
            try: arm.animation_data.action_slot = act.slots[0]
            except Exception: pass
        else:
            for pb in arm.pose.bones: pb.matrix_basis = mathutils.Matrix()
        scene.frame_set(frame); bpy.context.view_layer.update()
        dg = bpy.context.evaluated_depsgraph_get()
        v, _ = mesh_arrays(bpy.data.objects['HD_body'], dg)
        c = (v.min(0) + v.max(0)) / 2; size = (v.max(0) - v.min(0)).max()
        cam_d.ortho_scale = size * 1.1
        a = math.radians(30); d = mathutils.Vector((math.sin(a), math.cos(a), 0.15)).normalized()
        cam.location = mathutils.Vector(c) + d * size * 3
        cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
        p = (f'/tmp/_pv_{len(shots)}.png' if Image else out.replace('.png', f'_{len(shots)}.png'))
        scene.render.filepath = p
        bpy.ops.render.render(write_still=True)
        shots.append(Image.open(p).convert('RGB') if Image else p)
    if Image is None: log('preview', ', '.join(shots)); return
    sheet = Image.new('RGB', (sum(s.width for s in shots), shots[0].height))
    x = 0
    for s in shots: sheet.paste(s, (x, 0)); x += s.width
    sheet.save(out); log('preview', out)



# --------------------------------------------------------------------------------------------- glb splice

class Glb:
    """A glb file as JSON + binary chunk, with helpers to read accessors and append new data."""
    COMP = {5126: np.float32, 5125: np.uint32, 5123: np.uint16, 5121: np.uint8, 5122: np.int16, 5120: np.int8}
    NCOMP = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}

    def __init__(self, path):
        import json, struct
        b = open(path, 'rb').read()
        n = struct.unpack('<I', b[12:16])[0]
        self.j = json.loads(b[20:20 + n])
        rest = b[20 + n:]
        self.bin = bytearray(rest[8:8 + struct.unpack('<I', rest[:4])[0]]) if len(rest) >= 8 else bytearray()

    def read(self, i):
        a = self.j['accessors'][i]; bv = self.j['bufferViews'][a['bufferView']]
        dt = self.COMP[a['componentType']]; nc = self.NCOMP[a['type']]
        off = bv.get('byteOffset', 0) + a.get('byteOffset', 0); st = bv.get('byteStride', 0)
        isz = np.dtype(dt).itemsize * nc
        if st and st != isz:
            raw = np.frombuffer(bytes(self.bin), np.uint8, st * (a['count'] - 1) + isz, off)
            rows = np.lib.stride_tricks.as_strided(raw, (a['count'], isz), (st, 1))
            return np.frombuffer(rows.copy().tobytes(), dt).reshape(a['count'], nc)
        return np.frombuffer(bytes(self.bin), dt, a['count'] * nc, off).reshape(a['count'], nc)

    def add(self, arr, target=None, minmax=False):
        arr = np.ascontiguousarray(arr)
        while len(self.bin) % 4: self.bin.append(0)
        off = len(self.bin); self.bin += arr.tobytes()
        bv = {'buffer': 0, 'byteOffset': off, 'byteLength': arr.nbytes}
        if target: bv['target'] = target
        self.j['bufferViews'].append(bv)
        comp = {np.dtype(np.float32): 5126, np.dtype(np.uint32): 5125, np.dtype(np.uint16): 5123, np.dtype(np.uint8): 5121}[arr.dtype]
        nc = 1 if arr.ndim == 1 else arr.shape[1]
        acc = {'bufferView': len(self.j['bufferViews']) - 1, 'componentType': comp, 'count': len(arr),
               'type': {1: 'SCALAR', 2: 'VEC2', 3: 'VEC3', 4: 'VEC4'}[nc]}
        if minmax: acc['min'] = arr.min(0).tolist(); acc['max'] = arr.max(0).tolist()
        self.j['accessors'].append(acc)
        return len(self.j['accessors']) - 1

    def save(self, path):
        import json, struct
        js = json.dumps(self.j, separators=(',', ':')).encode()
        js += b' ' * ((4 - len(js) % 4) % 4)
        while len(self.bin) % 4: self.bin.append(0)
        self.j.setdefault('buffers', [{}])[0]['byteLength'] = len(self.bin)
        js = json.dumps(self.j, separators=(',', ':')).encode(); js += b' ' * ((4 - len(js) % 4) % 4)
        total = 12 + 8 + len(js) + 8 + len(self.bin)
        with open(path, 'wb') as f:
            f.write(struct.pack('<4sII', b'glTF', 2, total))
            f.write(struct.pack('<I4s', len(js), b'JSON')); f.write(js)
            f.write(struct.pack('<I4s', len(self.bin), b'BIN\0')); f.write(bytes(self.bin))


def bind_space(glb, replace_names, height):
    """The affine map from Blender's rest-pose world space to the glb's mesh (bind) space, fitted on the original
    vertices (the importer keeps their order). None when it does not fit (then the Blender export is used)."""
    j = glb.j; X, Y = [], []
    for name in replace_names:
        ni = next((i for i, n in enumerate(j['nodes']) if n.get('name') == name and 'mesh' in n), None)
        if ni is None: return None
        P = np.concatenate([glb.read(pr['attributes']['POSITION']) for pr in j['meshes'][j['nodes'][ni]['mesh']]['primitives']])
        v = rest_data.per_object[name]
        if len(P) != len(v): return None
        X.append(v); Y.append(P)
    X = np.concatenate(X); Y = np.concatenate(Y).astype(np.float64)
    Xh = np.c_[X, np.ones(len(X))]
    A, *_ = np.linalg.lstsq(Xh, Y, rcond=None)
    err = np.abs(Xh @ A - Y).max()
    log(f'bind space fit: max error {err/height:.2e} x height')
    return A.T if err < 1e-4 * height else None          # (3,4): glb = A @ [x,1]


def body_primitives(body, rest_world):
    """Per material: vertices split by UV and normal (glTF needs one per corner combination), triangles."""
    me = body.data
    me.calc_loop_triangles()
    uv = me.uv_layers.active.data if me.uv_layers.active else None
    try: cn = me.corner_normals                     # Blender 4.1+
    except AttributeError: me.calc_normals_split(); cn = None
    prims = {}
    for lt in me.loop_triangles:
        P = prims.setdefault(lt.material_index, {'key': {}, 'v': [], 'uv': [], 'n': [], 'tri': []})
        tri = []
        for li, vi in zip(lt.loops, lt.vertices):
            u = tuple(uv[li].uv) if uv else (0.0, 0.0)
            n = tuple(cn[li].vector) if cn is not None else tuple(me.loops[li].normal)
            k = (vi, round(u[0], 5), round(u[1], 5), round(n[0], 3), round(n[1], 3), round(n[2], 3))
            idx = P['key'].get(k)
            if idx is None:
                idx = P['key'][k] = len(P['v']); P['v'].append(vi); P['uv'].append(u); P['n'].append(n)
            tri.append(idx)
        P['tri'].append(tri)
    return prims


def material_image(m):
    """The image on a material's base colour (or its first image)."""
    if not (m and m.use_nodes): return None
    for n in m.node_tree.nodes:
        if n.type == 'BSDF_PRINCIPLED':
            for l in n.inputs['Base Color'].links:
                f = l.from_node
                if f.type == 'TEX_IMAGE' and f.image: return f.image
    return next((n.image for n in m.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image), None)


def write_spliced(src_glb, out_dir, model, body, rest_world, W, skel, A, replace_names, ref_extras, tex_size):
    glb = Glb(src_glb); j = glb.j
    nodes = j['nodes']
    first = next(i for i, n in enumerate(nodes) if n.get('name') == replace_names[0] and 'mesh' in n)
    skin = j['skins'][nodes[first]['skin']]
    joint_of = {nodes[ni]['name']: k for k, ni in enumerate(skin['joints'])}
    bone_joint = np.array([joint_of.get(n, -1) for n in skel.names])
    if (bone_joint[np.nonzero(W.any(0))[0]] < 0).any(): raise SystemExit('a weighted bone is not a joint of the skin')
    A3, At = A[:, :3], A[:, 3]
    Nm = np.linalg.inv(A3).T
    # top 4 bones per vertex
    order = np.argsort(-W, axis=1)[:, :4]
    wts = np.take_along_axis(W, order, 1); wts /= np.maximum(wts.sum(1, keepdims=True), 1e-9)
    jts = np.where(wts > 0, bone_joint[order], 0)
    jdt = np.uint8 if len(skin['joints']) < 256 else np.uint16
    os.makedirs(os.path.join(out_dir, 'assets', 'models'), exist_ok=True)
    os.makedirs(os.path.join(out_dir, 'assets', 'tex'), exist_ok=True)
    j.setdefault('images', []); j.setdefault('textures', []); j.setdefault('materials', [])
    sampler = 0 if j.get('samplers') else None
    prims = body_primitives(body, rest_world)
    out_prims = []
    tex_of = {}                       # one texture per image, shared by the materials using it
    for mi, P in sorted(prims.items()):
        m = body.data.materials[mi] if mi < len(body.data.materials) else None
        # the material's own GSF flags when it brought some (a mesh made from a game model: hair cut out, party
        # colour), else the body's; flag 0x1 = cut out (alpha test, both sides), 0x2 = blended, as the converter does
        ex = dict(ref_extras)
        if m is not None and 'gsf_flags' in m:
            ex.update({k: m[k] for k in ('gsf_texture', 'gsf_flags') if k in m})
        mat = {'name': f'{model}_hd_{mi}', 'pbrMetallicRoughness': {'metallicFactor': 0.0, 'roughnessFactor': 1.0},
               'extras': dict(ex, pw_style='painterly')}
        try: a1 = int(str(ex.get('gsf_flags', '0')).split('/')[0], 16)
        except ValueError: a1 = 0
        if a1 & 0x1: mat.update(alphaMode='MASK', alphaCutoff=0.5, doubleSided=True)
        elif a1 & 0x2: mat.update(alphaMode='BLEND', doubleSided=True)
        img = material_image(m)
        if img is not None and img.name in tex_of:
            mat['pbrMetallicRoughness']['baseColorTexture'] = {'index': tex_of[img.name]}
        elif img is not None:
            fn = f'{model}_hd_{len(tex_of)}.png'
            im = img.copy()
            if max(im.size) > tex_size:
                f = tex_size / max(im.size); im.scale(int(im.size[0] * f), int(im.size[1] * f))
            im.filepath_raw = os.path.join(out_dir, 'assets', 'tex', fn); im.file_format = 'PNG'; im.save()
            j['images'].append({'uri': '../tex/' + fn, 'name': fn})
            t = {'source': len(j['images']) - 1}
            if sampler is not None: t['sampler'] = sampler
            j['textures'].append(t)
            mat['pbrMetallicRoughness']['baseColorTexture'] = {'index': len(j['textures']) - 1}
            tex_of[img.name] = len(j['textures']) - 1
            log('texture', fn, tuple(im.size))
        j['materials'].append(mat)
        vi = np.array(P['v'])
        pos = (rest_world[vi] @ A3.T + At).astype(np.float32)
        nrm = np.array(P['n']) @ Nm.T; nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
        uvs = np.array(P['uv'], dtype=np.float32); uvs[:, 1] = 1.0 - uvs[:, 1]        # Blender -> glTF UV
        attrs = {'POSITION': glb.add(pos, 34962, True), 'NORMAL': glb.add(nrm.astype(np.float32), 34962),
                 'TEXCOORD_0': glb.add(uvs, 34962), 'JOINTS_0': glb.add(jts[vi].astype(jdt), 34962),
                 'WEIGHTS_0': glb.add(wts[vi].astype(np.float32), 34962)}
        tri = np.array(P['tri'], dtype=np.uint32)
        # the ParaWorld glbs keep counter-clockwise faces in mesh space (their mirror is in the root node): a map
        # that mirrors (det < 0) turns our faces around, so they are turned back
        if np.linalg.det(A3) < 0: tri = tri[:, [0, 2, 1]]
        ind = glb.add(np.ascontiguousarray(tri).ravel(), 34963)
        out_prims.append({'attributes': attrs, 'indices': ind, 'material': len(j['materials']) - 1})
    j['meshes'].append({'name': f'{model}_hd', 'primitives': out_prims})
    nodes[first]['mesh'] = len(j['meshes']) - 1
    for name in replace_names[1:]:
        for n in nodes:
            if n.get('name') == name and 'mesh' in n: n.pop('mesh'); n.pop('skin', None)
    out = os.path.join(out_dir, 'assets', 'models', model + '.glb')
    glb.save(out)
    log(f'wrote {out} ({os.path.getsize(out)/1e6:.1f} MB), {sum(len(p["tri"]) for p in prims.values())} triangles')
    return out


# --------------------------------------------------------------------------------------------- main

def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else sys.argv[1:]
    ap = argparse.ArgumentParser()
    ap.add_argument('--original', required=True); ap.add_argument('--new', required=True)
    ap.add_argument('--out', default='', help='a plain glb (Blender export)')
    ap.add_argument('--out-dir', default='', help='the remake\'s HD folder (content/hd): assets/models/<model>.glb + assets/tex')
    ap.add_argument('--tris', type=int, default=24000)
    ap.add_argument('--facing', default='auto')
    ap.add_argument('--keep', default='')
    ap.add_argument('--tex-size', type=int, default=2048)
    ap.add_argument('--preview', default='')
    ap.add_argument('--preview-actions', default='walk_1,all_walk_2_loop,attack_0')
    a = ap.parse_args(argv)
    t0 = time.time()

    arm, skinned = load_original(a.original)
    keep = [k for k in a.keep.split(',') if k]
    # the body: skinned meshes of some size (tiny skinned helpers - pick volumes, shadow parts - stay)
    replace = [o for o in skinned if not any(k in o.name for k in keep) and len(o.data.polygons) >= 60]
    ref_mat = next((m for o in replace for m in o.data.materials if m and 'gsf_flags' in m and m['gsf_flags'].startswith('00000000')), None)
    skel = Skeleton(arm)
    ov, ot, ow = rest_data(arm, replace, skel)
    height = ov[:, 2].max() - ov[:, 2].min()
    log(f'original: {len(skel.names)} bones, {len(ov)} vertices to replace in {len(replace)} meshes, height {height:.2f}')

    body = load_new(a.new)
    decimate(body, a.tris)
    nv0, nt = mesh_arrays(body)

    # 3 + 4: facing, placement and pose fit
    if cKDTree is None: raise SystemExit('refit.py needs scipy (pip install scipy into Blender\'s Python)')
    bones = fit_bones(skel, ow, ov, height)
    log('bones fitted:', len(bones))
    angles = [0, 90, 180, 270] if a.facing == 'auto' else [float(a.facing)]
    tries = []
    for ang in angles:
        pv, R, sc = place(nv0, ov, ang)
        samp, samp_n = sample_surface(pv, nt, 8000, np.random.default_rng(2), normals=True)
        F = Fitter(skel, ov, ot, ow, samp, height, samp_n)
        F.rigid()
        tries.append((F.error(), ang, pv, F))
        log(f'facing {ang:5.0f}: rigid fit error {tries[-1][0]/height:.4f} x height')
    tries.sort(key=lambda t: t[0])
    best = None
    for e0, ang, pv, F in tries:
        F.articulated(bones)
        F.rigid(10)
        F.articulated(bones, 15)
        e = F.error()
        log(f'facing {ang:5.0f}: pose fit error {e/height:.4f} x height')
        if best is None or e < best[0]: best = (e, ang, pv, F)
    _, ang, pv, F = best
    log('facing chosen:', ang)
    # move the new mesh into the original's frame (undo the global placement), keep the fitted pose
    Gi = np.linalg.inv(F.G)
    pv = pv @ Gi[:3, :3].T + Gi[:3, 3]
    basis = F.basis
    turned = []
    for b in bones:
        ang_b = math.degrees(math.acos(np.clip((np.trace(basis[b]) - 1) / 2, -1, 1)))
        if ang_b > 3: turned.append(f'{skel.names[b]}={ang_b:.0f}')
    log('bones turned (deg):', ', '.join(turned))

    # 5: weights from the posed original
    S = skel.skin_mats(basis)
    posed_orig = skin(ov, ow, S)
    me = body.data
    edges = np.empty(len(me.edges) * 2, dtype=np.int64); me.edges.foreach_get('vertices', edges); edges = edges.reshape(-1, 2)
    W = transfer_weights(pv, nt, posed_orig, ot, ow, edges, height, skel.segments(basis))

    # 6: back into the rest pose
    rest_v = inverse_skin(pv, W, S)
    d = cKDTree(ov).query(rest_v)[0]
    log(f'new mesh in the rest pose: median distance to the original {np.median(d)/height:.4f} x height, 95% {np.percentile(d,95)/height:.4f}')
    Mi = np.array(body.matrix_world.inverted())
    me.vertices.foreach_set('co', (rest_v @ Mi[:3, :3].T + Mi[:3, 3]).ravel())
    me.update()

    # 7: bind, replace, export
    for b, name in enumerate(skel.names):
        idx = np.nonzero(W[:, b])[0]
        if len(idx) == 0: continue
        g = body.vertex_groups.new(name=name)
        for i in idx: g.add([int(i)], float(W[i, b]), 'REPLACE')
    body.parent = arm; body.matrix_parent_inverse = arm.matrix_world.inverted()
    mod = body.modifiers.new('Armature', 'ARMATURE'); mod.object = arm
    fix_materials(body, ref_mat, a.tex_size)
    replace_names = [o.name for o in replace]
    model = os.path.splitext(os.path.basename(a.original))[0]
    if a.out_dir:
        A = bind_space(Glb(a.original), replace_names, height)
        if A is None: raise SystemExit('the original glb\'s mesh space could not be matched: use --out for a Blender export')
        ref_extras = {k: ref_mat[k] for k in ('gsf_texture', 'gsf_flags') if ref_mat is not None and k in ref_mat}
        write_spliced(a.original, a.out_dir, model, body, rest_v, W, skel, A, replace_names, ref_extras, a.tex_size)
    if a.out:
        for o in replace: bpy.data.objects.remove(o, do_unlink=True)
        body.name = model + '_hd_body'
        for m in body.data.materials:          # drop the importer's .001 suffixes where the name is free now
            if m and '.' in m.name and m.name.rsplit('.', 1)[0] not in bpy.data.materials: m.name = m.name.rsplit('.', 1)[0]
        bpy.ops.object.select_all(action='SELECT')
        bpy.ops.export_scene.gltf(filepath=a.out, export_format='GLB', use_selection=False, export_extras=True,
                                  export_skins=True, export_animations=True, export_animation_mode='ACTIONS',
                                  export_force_sampling=True, export_def_bones=False, export_yup=True,
                                  export_image_format='AUTO', export_apply=False)
        log(f'wrote {a.out} ({os.path.getsize(a.out)/1e6:.1f} MB)')
    log(f'done in {time.time()-t0:.0f} s')
    if a.preview:
        for o in replace:
            if o.name in bpy.data.objects: o.hide_render = True
        body.name = 'HD_body'
        preview(arm, a.preview, [x for x in a.preview_actions.split(',') if x])


if __name__ == '__main__':
    main()
