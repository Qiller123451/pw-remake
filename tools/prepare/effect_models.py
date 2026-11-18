"""The original effect models the toolkit's remake export leaves out (its pipeline exports only the effects the
browser remake uses), converted from the installation's GSF archives with the toolkit's converter (pwexport.gsf)
into a configuration's overrides, where the game looks first (AssetSource):

    overrides/assets/models/<name>.glb      the model (its emitters in the node extras, as the export has them)
    overrides/assets/tex/<texture>.png      its textures (only those the converted data does not have yet)
    overrides/assets/manifest_fx.json       {"models": {<name>: {arch, anims, foliage, nanims[, sounds]}}} - the view
                                            merges it into assets/manifest.json (View/Models/ModelManifest.cs)

Which models and why (docs/EFFECTS.md): the ruins' dust (Hu_Temple_Dem_Fx, Hu_Wall_Dem_Fx), the rocket launcher's
smoke (fx_seas_rpg), the spirits (fx_ghost_big), the Ninigi fireworks, the Aje tornado and quicksand trap. A model
the converted data already has is skipped; one that is there from an earlier run is kept unless forced.

    python tools/prepare/effect_models.py <ParaWorld folder> <converted data folder> <overrides folder>
                                          [--toolkit <folder>] [--mod MIRAGE] [--force]

prepare.py runs it for each configuration.
"""
import argparse
import json
import os
import shutil
import struct
import sys
import tempfile

# model -> the archive it is in (Data/<mod>/GSF/<archive>.gsf; a mod's archive of that name replaces Base's)
MODELS = {
    'hu_temple_dem_fx': 'all_effects',
    'hu_wall_dem_fx': 'all_effects',
    'fx_seas_rpg': 'all_effects',
    'fx_ghost_big': 'all_effects',
    'ninigi_fireworks': 'ninigi_products',
    'aje_tornado': 'aje_products',
    'aje_quicksand_trap': 'aje_buildings',
}

VERSION = 2   # bump when the conversion changes: every model is converted again


def load_glb(path):
    with open(path, 'rb') as f:
        b = f.read()
    jl = struct.unpack_from('<I', b, 12)[0]
    return json.loads(b[20:20 + jl]), b[20 + jl:]


def write_glb(path, j, rest):
    js = json.dumps(j, separators=(',', ':')).encode()
    js += b' ' * ((4 - len(js) % 4) % 4)
    out = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + len(rest)) + struct.pack('<II', len(js), 0x4E4F534A) + js + rest
    tmp = path + '.tmp'
    with open(tmp, 'wb') as f:
        f.write(out)
    os.replace(tmp, path)


def relink(j, src_dir, tex_out, have_tex):
    """the model's images and its emitters' sprite textures -> ../tex/<name>.png; copies the textures the data lacks.
    Returns the textures copied."""
    copied = []
    renamed = {}
    for im in j.get('images', []):
        uri = im.get('uri')
        if not uri:
            continue
        name = os.path.basename(uri)
        if name not in have_tex:
            src = os.path.join(src_dir, uri)
            if os.path.isfile(src):
                os.makedirs(tex_out, exist_ok=True)
                shutil.copyfile(src, os.path.join(tex_out, name))
                have_tex.add(name)
                copied.append(name)
        im['uri'] = '../tex/' + name
        renamed[uri] = im['uri']
    for nd in j.get('nodes', []):
        for t in ((nd.get('extras') or {}).get('emitter') or {}).get('tiles', []):
            if t.get('tex') in renamed:
                t['tex'] = renamed[t['tex']]
    return copied


def drop_unmaterialed(j):
    """the primitives whose GSF material index is 65535 ("none": the converter names it material_65535 and gives it
    no texture - ninigi_fireworks has a whole mesh of them, which would show as a white figure); the game draws no
    such surface (an assumption: docs/EFFECTS.md). Returns how many were dropped."""
    none = {i for i, m in enumerate(j.get('materials', [])) if m.get('name') == 'material_65535'
            and 'baseColorTexture' not in (m.get('pbrMetallicRoughness') or {})}
    if not none:
        return 0
    n = 0
    for nd in j.get('nodes', []):
        if 'mesh' not in nd:
            continue
        mesh = j['meshes'][nd['mesh']]
        keep = [p for p in mesh['primitives'] if p.get('material') not in none]
        n += len(mesh['primitives']) - len(keep)
        if keep:
            mesh['primitives'] = keep
        else:
            del nd['mesh']
    return n


def convert(gsf, archives, data_assets, overrides, data_dir=None, force=False, log=print, models=None):
    """gsf: the toolkit's pwexport.gsf module; archives: {archive name: .gsf path} (pwexport Install.archives());
    data_assets: the converted data's assets folder (models it already has are skipped, textures it has are not
    copied); overrides: the configuration's overrides folder; data_dir: the installation's Data folder for the
    textures (default: the archive's own). Returns the names converted or kept."""
    models = models or MODELS
    out_assets = os.path.join(overrides, 'assets')
    out_models = os.path.join(out_assets, 'models')
    man_path = os.path.join(out_assets, 'manifest_fx.json')
    try:
        with open(os.path.join(data_assets, 'manifest.json'), encoding='utf-8') as f:
            main = json.load(f).get('models', {})
    except (OSError, ValueError):
        main = {}
    try:
        with open(man_path, encoding='utf-8') as f:
            man = json.load(f)
    except (OSError, ValueError):
        man = {}
    if man.get('version') != VERSION:
        man = {}
    man = {'version': VERSION, 'models': man.get('models', {})}
    have_tex = set()
    for d in (os.path.join(data_assets, 'tex'), os.path.join(out_assets, 'tex')):
        if os.path.isdir(d):
            have_tex.update(os.listdir(d))
    done = []
    opened = {}
    for name, arch in sorted(models.items()):
        if name in main:
            man['models'].pop(name, None)             # the converted data has it now
            continue
        dst = os.path.join(out_models, name + '.glb')
        if not force and name in man['models'] and os.path.isfile(dst):
            done.append(name)
            continue
        path = archives.get(arch)
        if not path or not os.path.isfile(path):
            log('  effect model %s: no archive %s.gsf' % (name, arch))
            continue
        if path not in opened:
            opened[path] = gsf.Archive(path)
        a = opened[path]
        if a.index(name) is None:
            log('  effect model %s: not in %s' % (name, path))
            continue
        tmp = tempfile.mkdtemp(prefix='pwfx_')
        try:
            out = os.path.join(tmp, name + '.glb')
            a.export(name, out, data_dir=data_dir, log=lambda *x: None)
            j, rest = load_glb(out)
            copied = relink(j, tmp, os.path.join(out_assets, 'tex'), have_tex)
            drop_unmaterialed(j)
            os.makedirs(out_models, exist_ok=True)
            write_glb(dst, j, rest)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        ex = (j.get('nodes') or [{}])[0].get('extras') or {}
        info = {'arch': arch, 'anims': None, 'foliage': 'foliage' in ex, 'nanims': len(j.get('animations', []))}
        if ex.get('sounds'):
            info['sounds'] = ex['sounds']
        man['models'][name] = info
        log('  effect model %s (%s)%s' % (name, arch, ', textures ' + ', '.join(copied) if copied else ''))
        done.append(name)
    os.makedirs(out_assets, exist_ok=True)
    with open(man_path, 'w', encoding='utf-8') as f:
        json.dump(man, f, indent=1)
    return done


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('paraworld', help='the ParaWorld folder (the one with Data in it)')
    ap.add_argument('data', help="the converted game data (the folder with assets/manifest.json)")
    ap.add_argument('overrides', help="the configuration's overrides folder")
    ap.add_argument('--toolkit', help='the ParaWorld Toolkit folder (with pwexport in it)')
    ap.add_argument('--mod', default='', help='the configuration\'s mod (MIRAGE ...; empty: the official game)')
    ap.add_argument('--textures', help='a Data folder to take the textures from (default: the installation\'s)')
    ap.add_argument('--force', action='store_true')
    a = ap.parse_args(argv)
    if a.toolkit:
        sys.path.insert(0, a.toolkit)
    from pwexport import gsf                       # noqa: E402
    from pwexport.install import Install           # noqa: E402
    from pwexport.mods import ModInstall           # noqa: E402
    inst = ModInstall(a.paraworld, mod=a.mod) if a.mod else Install(a.paraworld)
    done = convert(gsf, inst.archives(), os.path.join(a.data, 'assets'), a.overrides, a.textures, a.force)
    print('%d effect models' % len(done))


if __name__ == '__main__':
    main()
