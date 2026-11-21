"""The original's fog of war clouds for the game: the cloud atlas of each landscape setting and the fallback.

What the client does (PWClient.exe, read for the remake 2026-11-21, docs/TERRAIN.md "Fog of war: how the original
draws it"): the fog of war renderer (Pest::CFOWRenderer2) draws its cloud sprites with the setting's texture
"fog.tga" below Texture/Scape/<Setting>/ (the scape texture path "Scape/" + setting, 0x48cf67 -> 0x6fba70; the name
"fog.tga" and the texture slot "FogTxt" 0x6fc432) and, when a setting has none, with "Effects/fogofwar.tga"
(0x71ab49 .. 0x71ab89). On disk the textures carry their size: fog_(0256).dds (detailtable.txt: "fog dxt5 256",
"fogofwar dxt1a 64 128 256"). Each is a 4 x 4 atlas of cloud wisps (white, the wisp in the alpha).

  Texture/Scape/<Setting>/fog*      -> the configuration's overrides/assets/terrain/<Setting>/fog.png
  Texture/Effects/fogofwar*          -> the configuration's overrides/assets/terrain/fogofwar.png

Of the configuration's Data folders the last that has the file (a mod replaces Base's: MIRAGE's Cave3), of a folder's
sizes the largest. Incremental: converted again only when the source changed (its size and time, kept in
fog.json next to the textures) or with force. Nothing is made up: without the files nothing is written and the game
falls back (game/src/View/Terrain/FogClouds.cs).
"""
import json
import os
import re

SETTINGS = ('Northland', 'Savanna', 'Jungle', 'Icewaste', 'Ashvalley', 'Cave1', 'Cave2', 'Cave3', 'TestSet')
EXTS = ('.dds', '.tga', '.png', '.bmp', '.jpg')
SIZED = re.compile(r'^(.*)_\((\d+)\)$')
INDEX = 'fog.json'


def _ci_dir(base, rel):
    """case-insensitive directory below base, or None"""
    p = base
    for part in rel.split('/'):
        q = os.path.join(p, part)
        if os.path.isdir(q):
            p = q
            continue
        try:
            hit = next((e for e in os.listdir(p) if e.lower() == part.lower() and os.path.isdir(os.path.join(p, e))), None)
        except OSError:
            return None
        if hit is None:
            return None
        p = os.path.join(p, hit)
    return p


def find_texture(inst_data, folders, rel_dir, name):
    """the file the client loads for <rel_dir>/<name>: of the folders the last that has it, of its sizes the largest"""
    for folder in reversed(folders):
        d = _ci_dir(inst_data, '%s/Texture/%s' % (folder, rel_dir))
        if not d:
            continue
        best = None
        for e in os.listdir(d):
            stem, ext = os.path.splitext(e)
            if ext.lower() not in EXTS:
                continue
            m = SIZED.match(stem)
            base, size = (m.group(1), int(m.group(2))) if m else (stem, 0)
            if base.lower() != name.lower():
                continue
            if best is None or size > best[0]:
                best = (size, os.path.join(d, e))
        if best:
            return best[1]
    return None


def stamp(path):
    try:
        st = os.stat(path)
        return '%d:%d' % (st.st_size, int(st.st_mtime))
    except OSError:
        return ''


def to_png(src, dst):
    """a texture file -> RGBA PNG; returns (width, height)"""
    from PIL import Image
    im = Image.open(src)
    im.load()
    im = im.convert('RGBA')
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    tmp = dst + '.tmp'
    im.save(tmp, 'PNG')
    os.replace(tmp, dst)
    return im.size


def convert(inst_data, folders, overrides_dir, force=False, log=print):
    """every setting's fog.tga and the fallback fogofwar.tga -> overrides/assets/terrain/; returns the names written"""
    root = os.path.join(overrides_dir, 'assets', 'terrain')
    index_path = os.path.join(root, INDEX)
    try:
        with open(index_path, encoding='utf-8') as f:
            index = json.load(f)
    except (OSError, ValueError):
        index = {}
    jobs = [('%s/fog.png' % s, find_texture(inst_data, folders, 'Scape/' + s, 'fog')) for s in SETTINGS]
    jobs.append(('fogofwar.png', find_texture(inst_data, folders, 'Effects', 'fogofwar')))
    done = []
    for rel, src in jobs:
        if not src:
            continue
        dst = os.path.join(root, rel)
        st = stamp(src)
        if not force and index.get(rel, {}).get('stamp') == st and os.path.isfile(dst):
            done.append(rel)
            continue
        try:
            w, h = to_png(src, dst)
        except Exception as e:                     # (one texture must not stop the rest)
            log('  fog %s: %s' % (rel, e))
            continue
        index[rel] = {'source': os.path.relpath(src, inst_data).replace('\\', '/'), 'stamp': st, 'size': [w, h]}
        done.append(rel)
    if done:
        os.makedirs(root, exist_ok=True)
        with open(index_path, 'w', encoding='utf-8') as f:
            json.dump(index, f, indent=1, sort_keys=True)
    return done
