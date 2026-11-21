# HD heroes

New, detailed models of the heroes (Cole, Béla and Stina first) in a painterly, Arcane-like look, on the **original
skeletons**: every original animation, attachment point (`link_*`), walk set and animation sound keeps working, and
the simulation doesn't know the difference. The workflow for making one (references, prompts, image-to-3D) is in
`E:\Paraworld\HD_Heroes\README.md`. This page covers the tool and the game side.

## In the game

* Option **HD heroes** (Options → Graphics, `settings.json` `hdHeroes`, on by default). It takes effect from the
  next game (each game session has its own `ModelLibrary`).
* `GameApp.ApplyHd` sets `AssetSource.HdDir` to `<content>/hd` while the option is on and that folder exists.
  What is only drawn looks there first: `ModelLibrary` loads model files with `AssetSource.RequireView`, and
  `TextureCache` loads textures with `FindView`. That lookup isn't cached, so switching the option is seen by every
  model loaded afterwards. The simulation never looks there: it reads clips, links and sizes from
  `assets/modelinfo.json`. So HD models change nothing in a game's course, and saves, replays and online games stay
  the same with and without them.
* `content/hd/assets/models/<model>.glb` replaces the prepared model of that name, and its textures
  (`../tex/<model>_hd_<n>.png`) are found as `assets/tex/<file>` in the same folder. `content/hd` isn't in the
  repository (`.gitignore`): its glb files carry the original's animations.
* Materials with the glTF extra `pw_style = "painterly"` get `PainterlyLook` instead of `ModelMaterials.ToLambert`'s
  Lambert material. Nothing else in the game changes.

### PainterlyLook (`game/src/View/Materials/PainterlyLook.cs`)

One opaque `ShaderMaterial` pass with its own `light()`:

| Part | How | Uniforms |
|---|---|---|
| light bands | N·L (+ brush noise) through two smoothsteps: shadow → mid tone → full light | `band_shadow`, `band_light`, `band_soft`, `mid_level` |
| painted colours | shadows tinted cool, light warm (× the light's colour) | `shadow_tint`, `light_tint` |
| brush strokes | value noise in texture space (stretched strokes, two sizes; a sine-free hash, strokes finer than a pixel faded out - the sine hash drew moiré) shifts the band borders | `brush_strength`, `brush_scale` |
| rim light | warm, on the lit side only, from the view angle | `rim_color`, `rim_strength`, `rim_width` |
| highlight | small, hard, on the fully lit side | `highlight_strength`, `highlight_size` |
| contour | the surface turning away from the eye darkened (no outline pass) | `contour` |
| colour | a little more saturation than the texture | `saturation` |

* Shadow maps count as the shadow band (`ATTENUATION` of directional lights). Lamps (omni or spot) add the same
  bands, scaled by their falloff.
* The code starts with `shader_type spatial;` and has no `ALPHA =`, so `UnitsBehind` (silhouettes behind
  buildings) treats it as an opaque model material. It has no next pass for the same reason.
* No player colour: `PartyTinted` leaves materials it doesn't know as they are (see "Limits").
* `tools/hd_heroes/preview_look.py` renders a model in the original Lambert look and in this look side by side with
  Cycles nodes built from the same formulas. It's for tuning without the game; the game's shader is what counts.

## Looking at them in the toolkit

The ParaWorld Toolkit's model viewer (exporter) shows them too: a hero model with an HD version in `content/hd`
gets the row **Version → HD heroes (painted)**, as the Executioner's HD versions do, with animations, links and the
painterly look (the toolkit's `pwexport/web/materials.js` `PainterlyMaterial`, the same values as `PainterlyLook` -
keep them in step). The toolkit finds `content/hd` in `ParaWorldRemakeGodot` next to it, or where `PW_HD_HEROES`
points (toolkit `docs/HD_MODELS.md`).

## The tool: `tools/hd_heroes/refit.py`

```
blender -b --python tools/hd_heroes/refit.py -- --original <data>/assets/models/cole_s0.glb --new cole.glb
        --out-dir content/hd [--preview cole.png] [--tris 24000] [--facing auto] [--keep <mesh>] [--tex-size 2048]
```

(or `python refit.py ...` with the `bpy` module; needs numpy and scipy). `<data>` is the **MIRAGE** configuration's
prepared data (`content/configs/mirage/config.json` `"data"`): MIRAGE is the base of the HD heroes - its models
have a few more clips than the base game's (cole_s0: 254 / 249), its hero textures are 1024 px, and its Béla and
Stina wear the box art's outfits. `content/hd` serves both configurations. Run it for `<hero>_s0` … `_s4`: the level variants borrow `_s0`'s animations and
share its skeleton (`E:\Paraworld\HD_Heroes\run_refit.bat` does all of them).

1. **Original**: the skeleton, the body (skinned meshes of ≥ 60 faces; tiny skinned helpers such as the
   `…_lod1f_f2000` pick parts stay), their rest-pose vertices and weights.
2. **New mesh**: joined, welded, reduced to `--tris` (collapse decimation).
3. **Placement**: scaled to the original's height, on the same ground, centred on the upper body (the original's
   rest pose is mid-stride). The facing is tried in all four directions, and the best pose fit (4) wins.
4. **Pose fit** (leaf bones - the head and the jaw - are never turned: a turned jaw skewed the mouth in every
   clip) on 8000 surface samples of each model: a rigid fit (yaw ≤ 10° per round, scale, move), then an
   **articulated ICP**. Each long bone (subtree ≥ 2 % of the weight and ≥ 15 % of the height long: legs, arms,
   spine, neck; not hands, fingers or toes) is turned about its joint (Kabsch, damped, ≤ 20° per round, parents
   first), with a rigid round in between. Turning hands and fingers distorted the fingers in step 6.
5. **Skin weights**, robust transfer (after Abdrashitov et al., *Robust Skin Weights Transfer via Weight
   Inpainting*, 2023): a new vertex takes the barycentric weights of the nearest point of the posed original only
   if that point is within 4 % of the height, its face normal is within 35° of the vertex normal, and the bone
   carrying it is near (≤ 2 × the nearest bone's distance + 3 % of the height). Without the last check, a hand
   resting on the hip took the thigh's weights and its fingers stretched into claws when walking. The other
   vertices are inpainted (Laplace equation over the new mesh, the matched vertices fixed). Then 3 rounds of
   smoothing, 4 bones at most, normalised.
6. **Back to the rest pose**: inverse linear-blend skinning with the fitted pose. Rest pose, animations and links
   stay exactly as in the original.
7. **Splice** (`--out-dir`): the original glb file is kept and only the body is swapped. The map from Blender's rest
   space to the glb's mesh space is fitted on the original vertices (affine least squares; the importer keeps
   their order; the ParaWorld glbs come out as a mirror `diag(-1,1,1)`, error ~1e-7), and the run stops if it
   doesn't fit. New primitives (POSITION, NORMAL, TEXCOORD_0, JOINTS_0 by joint name, WEIGHTS_0, indices) go on
   the first body node with its skin; other body nodes lose their mesh. Faces are turned around when the map mirrors
   (the ParaWorld glbs keep counter-clockwise faces in mesh space). New materials carry the original's `gsf_*` extras
   plus `pw_style` - a material's own flags when it brought some (a mesh made from a game model: the hair's
   0x1 → alphaMode MASK, double-sided; PainterlyLook has a cut-out, double-sided variant). Images are written as `assets/tex/<model>_hd_<n>.png` (one per distinct image), because the
   remake loads textures by file name (`TextureCache`, embedded images are discarded).
   `--out` writes a plain Blender glTF export instead (for other tools).

Tested with stand-ins (the original Cole and Béla, posed differently, subdivided, unrigged, turned and rescaled):
the facing was found, the pose fit ended at ~1.7 % of the height (sampling floor ~1 %), the rest-pose mesh lay within
~1.1 % of the original (median), and walk / fight / idle clips play like the original's (Béla's four body meshes
became one).

## Limits

* **No player colour** on HD bodies. The original heroes have small party-colour parts (GSF flag 0x1000, e.g.
  `cole.001`). Later: a mask painted on the new texture and a `party` uniform in `PainterlyLook`.
* Level variants `_s1` … `_s4` get the same new body (anything they add over `_s0` is lost).
* A generated mesh with the arms fused to the body, or with floating bits, gets odd weights there. Clean it in
  Blender first (`E:\Paraworld\HD_Heroes\README.md`, step 2).
