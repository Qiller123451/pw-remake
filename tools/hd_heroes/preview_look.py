"""
preview_look.py - a picture of a model in the remake's two looks: the original's Lambert shading and the HD heroes'
painterly shading (game/src/View/Materials/PainterlyLook.cs), side by side, from the game's camera angle.

The painterly shading is rebuilt with Cycles shader nodes from the same formulas and uniform values (one sun, the
game's ambient as a constant), so it shows the look without starting the game - the game's own shader decides in
the end. Run:  blender -b --python preview_look.py -- model.glb out.png [action] [frame]
"""
import sys, math
import bpy, mathutils

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else sys.argv[1:]
src, out = argv[0], argv[1]
action = argv[2] if len(argv) > 2 else 'standanim'
frame = int(argv[3]) if len(argv) > 3 else 0

# the uniforms of PainterlyLook.cs
U = dict(band_shadow=0.12, band_light=0.50, band_soft=0.06, mid_level=0.58, shadow_tint=(0.26, 0.24, 0.44),
         light_tint=(1.04, 1.00, 0.93), brush_strength=0.20, brush_scale=10.0, rim_color=(1.0, 0.85, 0.65),
         rim_strength=0.45, rim_width=0.30, highlight_strength=0.12, highlight_size=0.06, contour=0.38, saturation=1.15)
SUN = mathutils.Vector((0.75, 0.35, 0.55)).normalized()     # towards the light (game-like: high, from the front left)
SUN_COLOR = (1.0, 0.97, 0.92)
AMBIENT = (0.36, 0.38, 0.42)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
for o in list(bpy.data.objects):
    if o.name.startswith('Icosphere') or (o.type == 'MESH' and len(o.data.polygons) < 60): bpy.data.objects.remove(o)
arm = next((o for o in bpy.data.objects if o.type == 'ARMATURE'), None)
if arm and action in bpy.data.actions:
    arm.animation_data_create(); act = bpy.data.actions[action]; arm.animation_data.action = act
    try: arm.animation_data.action_slot = act.slots[0]
    except Exception: pass
scene = bpy.context.scene
scene.frame_set(frame)


def image_of(m):
    if not m or not m.use_nodes: return None
    return next((n.image for n in m.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image), None)


def build(m, painterly):
    img = image_of(m)
    nt = bpy.data.node_trees.new('x', 'ShaderNodeTree') if False else None
    mat = bpy.data.materials.new(m.name + ('_paint' if painterly else '_lambert'))
    mat.use_nodes = True
    N = mat.node_tree.nodes; L = mat.node_tree.links
    for n in list(N): N.remove(n)
    outn = N.new('ShaderNodeOutputMaterial'); em = N.new('ShaderNodeEmission'); L.new(em.outputs[0], outn.inputs[0])
    tc = N.new('ShaderNodeTexCoord'); geo = N.new('ShaderNodeNewGeometry')

    def val(v):
        n = N.new('ShaderNodeValue'); n.outputs[0].default_value = v; return n.outputs[0]

    def col(c):
        n = N.new('ShaderNodeRGB'); n.outputs[0].default_value = (*c, 1); return n.outputs[0]

    def math_(op, a, b=None, clamp=False):
        n = N.new('ShaderNodeMath'); n.operation = op; n.use_clamp = clamp
        for i, x in enumerate((a, b)):
            if x is None: continue
            if isinstance(x, (int, float)): n.inputs[i].default_value = x
            else: L.new(x, n.inputs[i])
        return n.outputs[0]

    def vmath(op, a, b=None):
        n = N.new('ShaderNodeVectorMath'); n.operation = op
        for i, x in enumerate((a, b)):
            if x is None: continue
            if isinstance(x, (tuple, list)): n.inputs[i].default_value = x
            else: L.new(x, n.inputs[i])
        return n.outputs['Value'] if op in ('DOT_PRODUCT', 'LENGTH') else n.outputs[0]

    def mix(a, b, f):
        n = N.new('ShaderNodeMix'); n.data_type = 'RGBA'
        for sock, x in ((n.inputs[0], f), (n.inputs[6], a), (n.inputs[7], b)):
            if isinstance(x, (int, float)): sock.default_value = x
            elif isinstance(x, tuple): sock.default_value = (*x, 1)
            else: L.new(x, sock)
        return n.outputs[2]

    def cmul(a, b):
        n = N.new('ShaderNodeMix'); n.data_type = 'RGBA'; n.blend_type = 'MULTIPLY'; n.inputs[0].default_value = 1
        for sock, x in ((n.inputs[6], a), (n.inputs[7], b)):
            if isinstance(x, tuple): sock.default_value = (*x, 1)
            else: L.new(x, sock)
        return n.outputs[2]

    def cadd(a, b):
        n = N.new('ShaderNodeMix'); n.data_type = 'RGBA'; n.blend_type = 'ADD'; n.inputs[0].default_value = 1
        for sock, x in ((n.inputs[6], a), (n.inputs[7], b)):
            if isinstance(x, tuple): sock.default_value = (*x, 1)
            else: L.new(x, sock)
        return n.outputs[2]

    def smooth(x, lo, hi):
        n = N.new('ShaderNodeMapRange'); n.interpolation_type = 'SMOOTHSTEP'
        n.inputs['From Min'].default_value = lo; n.inputs['From Max'].default_value = hi
        L.new(x, n.inputs['Value']); return n.outputs[0]

    if img:
        t = N.new('ShaderNodeTexImage'); t.image = img; L.new(tc.outputs['UV'], t.inputs[0]); albedo = t.outputs[0]
    else:
        albedo = col((0.7, 0.7, 0.7))
    ndl = vmath('DOT_PRODUCT', geo.outputs['Normal'], tuple(SUN))
    sun = col(SUN_COLOR)
    if not painterly:
        lam = math_('MAXIMUM', ndl, 0.0)
        light = cadd(col(AMBIENT), cmul(sun, mix((0, 0, 0), (1, 1, 1), lam)))
        L.new(cmul(albedo, light), em.inputs[0]); return mat
    # saturation and contour
    hsv = N.new('ShaderNodeHueSaturation'); hsv.inputs['Saturation'].default_value = U['saturation']; L.new(albedo, hsv.inputs['Color'])
    facing = vmath('DOT_PRODUCT', geo.outputs['Normal'], geo.outputs['Incoming'])
    edge = math_('SUBTRACT', 1.0, math_('MAXIMUM', facing, 0.0))
    cont = math_('SUBTRACT', 1.0, math_('MULTIPLY', smooth(edge, 0.55, 0.95), U['contour']))
    c = cmul(hsv.outputs[0], mix((0, 0, 0), (1, 1, 1), cont))
    # brush noise in UV space (stretched strokes)
    uvs = vmath('MULTIPLY', tc.outputs['UV'], (U['brush_scale'], U['brush_scale'] * 3, 1))
    nz = N.new('ShaderNodeTexNoise'); nz.noise_dimensions = '2D'; nz.inputs['Scale'].default_value = 1.0
    nz.inputs['Detail'].default_value = 1.0; L.new(uvs, nz.inputs['Vector'])
    brush = math_('SUBTRACT', nz.outputs['Fac'], 0.5)
    s = math_('ADD', ndl, math_('MULTIPLY', brush, U['brush_strength'] * 1.6))
    lit = smooth(s, U['band_shadow'] - U['band_soft'], U['band_shadow'] + U['band_soft'])
    full = math_('MULTIPLY', smooth(s, U['band_light'] - U['band_soft'], U['band_light'] + U['band_soft']), lit)
    tone = mix(U['shadow_tint'], mix((U['mid_level'],) * 3, U['light_tint'], full), lit)
    diffuse = cadd(col(AMBIENT), cmul(sun, tone))
    rim = math_('MULTIPLY', smooth(edge, 1 - U['rim_width'], 1.0), lit)
    h = vmath('NORMALIZE', vmath('ADD', tuple(SUN), geo.outputs['Incoming']))
    hl = math_('MULTIPLY', smooth(vmath('DOT_PRODUCT', geo.outputs['Normal'], h), 1 - U['highlight_size'], 1 - U['highlight_size'] * 0.6), full)
    spec = cadd(mix((0, 0, 0), U['rim_color'], math_('MULTIPLY', rim, U['rim_strength'])),
                mix((0, 0, 0), (1, 1, 1), math_('MULTIPLY', hl, U['highlight_strength'])))
    L.new(cadd(cmul(c, diffuse), cmul(sun, spec)), em.inputs[0])
    return mat


meshes = [o for o in bpy.data.objects if o.type == 'MESH']
orig = {o.name: [s.material for s in o.material_slots] for o in meshes}
world = bpy.data.worlds.new('w'); scene.world = world; world.use_nodes = True
world.node_tree.nodes['Background'].inputs[0].default_value = (0.42, 0.45, 0.36, 1)
scene.render.engine = 'CYCLES'; scene.cycles.samples = 8; scene.cycles.max_bounces = 0
scene.view_settings.view_transform = 'Standard'
scene.render.resolution_x, scene.render.resolution_y = 520, 640
dg = bpy.context.evaluated_depsgraph_get()
pts = []
for o in meshes:
    e = o.evaluated_get(dg); me = e.to_mesh(); pts += [e.matrix_world @ v.co for v in me.vertices]; e.to_mesh_clear()
lo = mathutils.Vector([min(p[i] for p in pts) for i in range(3)]); hi = mathutils.Vector([max(p[i] for p in pts) for i in range(3)])
c = (lo + hi) / 2; size = max(hi - lo)
cam_d = bpy.data.cameras.new('cam'); cam_d.type = 'ORTHO'; cam_d.ortho_scale = size * 1.15
cam = bpy.data.objects.new('cam', cam_d); scene.collection.objects.link(cam); scene.camera = cam
d = mathutils.Vector((0.25, 0.8, 0.55)).normalized()       # the game's camera looks down at about 35 deg
cam.location = c + d * size * 3; cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
from PIL import Image, ImageDraw
shots = []
for painterly in (False, True):
    for o in meshes:
        for i, slot in enumerate(o.material_slots):
            if orig[o.name][i] is not None: slot.material = build(orig[o.name][i], painterly)
    p = f'/tmp/_look_{int(painterly)}.png'; scene.render.filepath = p
    bpy.ops.render.render(write_still=True); shots.append(Image.open(p).convert('RGB'))
sheet = Image.new('RGB', (shots[0].width * 2, shots[0].height + 28), (30, 30, 30))
dr = ImageDraw.Draw(sheet)
for i, (s, label) in enumerate(zip(shots, ('original look (Lambert)', 'painterly look (HD heroes)'))):
    sheet.paste(s, (i * s.width, 28)); dr.text((i * s.width + 10, 8), label, fill=(230, 230, 230))
sheet.save(out); print('saved', out)
