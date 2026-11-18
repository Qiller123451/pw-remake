"""The day-time light table of each landscape setting for the game: Texture/Scape/<Setting>/skygradient_day.tga (else
skygradient.tga) -> the configuration's overrides/assets/terrain/<Setting>/sky.json.

What the client does with it (PWClient.exe, read for the remake 2026-11-02, docs/GAME_SESSION.md "Cutscenes"):
  * on loading a level the sky manager loads "scape/<setting>/skygradient.tga" below /Data/Base/Texture/ (0x44ef17 ..
    0x44ef81, CSkyMgr.LoadSkyColors 0x48e560) - trying "<name>_day.tga" first (0x728745, the string at 0x88d804);
  * the picture is a table: one row per colour, the columns the day from midnight to midnight; a colour is read at
    x = (time of day / 86400) * (width - 1), linearly between two columns (0x821c60);
  * row 0 is the sky's colour (its alpha the sky's haze, used for the fog: docs/original/RENDERING.md "Fog"), row 1
    the ambient light and row 2 the sun's light (the light renderer's directional light: 0x72daab-0x72dad6 writes
    them over its defaults 0xff303030 ambient / 0xffbebebe diffuse, 0x7245f7); rows 3.. the sky dome, water, clouds,
    billboards (not used by the remake). A table with fewer rows gives row 0 for the missing ones.
The table is written as it is (rows top-down as the picture shows them, colours "rrggbbaa"); the game samples it
(src/Sim/Content/SkyGradient.cs). Nothing is made up: without the file the game keeps its fixed daylight.
"""
import json
import os
import struct

NAMES = ('skygradient_day.tga', 'skygradient.tga')     # the client's order (0x728745: "_day" first)
SETTINGS = ('Northland', 'Savanna', 'Jungle', 'Icewaste', 'Ashvalley', 'Cave1', 'Cave2', 'Cave3', 'TestSet')


def read_tga(path):
    """(width, height, rows of (r, g, b, a) top-down) of an uncompressed or RLE true-colour / grey TGA"""
    with open(path, 'rb') as f:
        d = f.read()
    idlen, cmtype, itype = d[0], d[1], d[2]
    w, h, bpp, desc = struct.unpack_from('<HHBB', d, 12)
    if itype not in (2, 3, 10, 11) or cmtype != 0 or bpp not in (8, 16, 24, 32):
        raise ValueError('%s: TGA type %d / %d bits not supported' % (path, itype, bpp))
    cmlen = struct.unpack_from('<H', d, 5)[0] * ((d[7] + 7) // 8) if cmtype else 0
    pos = 18 + idlen + cmlen
    n = w * h
    px = bpp // 8
    out = bytearray()
    if itype in (2, 3):
        out = d[pos:pos + n * px]
    else:                                            # run-length encoded
        while len(out) < n * px:
            c = d[pos]
            pos += 1
            k = (c & 0x7f) + 1
            if c & 0x80:
                out += d[pos:pos + px] * k
                pos += px
            else:
                out += d[pos:pos + k * px]
                pos += k * px

    def colour(i):
        o = i * px
        if px == 1:
            v = out[o]
            return (v, v, v, 255)
        if px == 2:
            v = out[o] | (out[o + 1] << 8)
            return (((v >> 10) & 31) * 255 // 31, ((v >> 5) & 31) * 255 // 31, (v & 31) * 255 // 31, 255)
        b, g, r = out[o], out[o + 1], out[o + 2]
        return (r, g, b, out[o + 3] if px == 4 else 255)
    rows = [[colour(y * w + x) for x in range(w)] for y in range(h)]
    if not desc & 0x20:                              # stored bottom-up (the TGA default): top row first
        rows.reverse()
    return w, h, rows


def find(inst_data, folders, setting):
    """the gradient the client loads for a setting: of the configuration's Data folders the last that has the file
    (a mod replaces Base's: MIRAGE.info tryfilereplace), "_day" before the plain name"""
    for name in NAMES:
        for folder in reversed(folders):
            p = _ci(inst_data, '%s/Texture/Scape/%s/%s' % (folder, setting, name))
            if p:
                return p
    return None


def _ci(base, rel):
    """case-insensitive path below base, or None"""
    p = base
    for part in rel.split('/'):
        q = os.path.join(p, part)
        if os.path.exists(q):
            p = q
            continue
        try:
            hit = next((e for e in os.listdir(p) if e.lower() == part.lower()), None)
        except OSError:
            return None
        if hit is None:
            return None
        p = os.path.join(p, hit)
    return p


def convert(inst_data, folders, overrides_dir, log=print):
    """every setting's table -> overrides/assets/terrain/<Setting>/sky.json; returns the settings written"""
    done = []
    for s in SETTINGS:
        src = find(inst_data, folders, s)
        if not src:
            continue
        try:
            w, h, rows = read_tga(src)
        except (OSError, ValueError, IndexError, struct.error) as e:
            log('  sky %s: %s' % (s, e))
            continue
        out = os.path.join(overrides_dir, 'assets', 'terrain', s, 'sky.json')
        os.makedirs(os.path.dirname(out), exist_ok=True)
        doc = {'source': os.path.relpath(src, inst_data).replace('\\', '/'), 'width': w, 'height': h,
               'rows': [['%02x%02x%02x%02x' % c for c in row] for row in rows]}
        with open(out, 'w', encoding='utf-8') as f:
            json.dump(doc, f, separators=(',', ':'))
        varies = any(len(set(row)) > 1 for row in rows[:3])
        log('  sky %s: %s, %d x %d, light %s over the day' % (s, os.path.basename(src), w, h, 'changes' if varies else 'the same'))
        done.append(s)
    return done
