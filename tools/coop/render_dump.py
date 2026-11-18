#!/usr/bin/env python3
"""Draws a map dumped by the CoopMapsDump test (PW_COOP_DUMP=<dir>): a top-down picture of the map as the game builds
it, for writing co-op definitions (docs/COOP.md "Maps").

    python3 tools/coop/render_dump.py <dir>/<cfg>_<NN>_s<seats>.json [out.png] [--scale 2] [--crop x0,z0,x1,z1]
            [--triggers <mission folder>/triggers.lua]

Water blue by depth, land shaded by height and ground material, blocked nav cells dark, the ground seat 1 reaches
(enemy gates closed) tinted light; buildings coloured by their owner's relation to the human player (green the
player, cyan co-op seats, red enemies, blue allies, grey neutral; walls / gates outlined), units as dots, start
locations as crosses, regions as yellow outlines (orange: referenced by the triggers); a 100 m grid in the original
map's coordinates with labels every 200 m.
"""
import json, sys, re
import numpy as np
from PIL import Image, ImageDraw, ImageFont

def main():
    args = sys.argv[1:]
    src = args[0]
    out = args[1] if len(args) > 1 and not args[1].startswith('--') else src.replace('.json', '.png')
    scale = 2.0
    crop = None
    trig = None
    comp = '--comp' in args
    for i, a in enumerate(args):
        if a == '--scale': scale = float(args[i + 1])
        if a == '--crop': crop = [float(v) for v in args[i + 1].split(',')]
        if a == '--triggers': trig = open(args[i + 1], encoding='utf-8', errors='replace').read()
    d = json.load(open(src))
    hx, hy, ox, oy, water = d['hx'], d['hy'], d['offx'], d['offy'], d['water']
    H = np.fromfile(src.replace('.json', '.h'), dtype=np.float32).reshape(hy, hx)
    F = np.fromfile(src.replace('.json', '.f'), dtype=np.uint8).reshape(hy, hx)
    # (2 m per sample; `scale` m per pixel)
    step = max(1, int(round(scale / 2)))
    H2 = H[::step, ::step]; F2 = F[::step, ::step]
    mpp = 2.0 * step
    gy, gx = np.gradient(H2.astype(np.float64), mpp)
    shade = np.clip(0.75 + (-gx * 0.6 - gy * 0.6) * 0.9, 0.35, 1.25)
    mats = (F2 >> 4) & 7
    pal = np.array([[118, 140, 70], [150, 130, 90], [95, 120, 60], [170, 160, 120], [120, 110, 100], [140, 150, 80], [100, 100, 90], [160, 140, 70]], dtype=np.float64)
    rgb = pal[mats]
    hn = np.clip((H2 - water) / 60.0, 0, 1)[..., None]
    rgb = rgb * (0.85 + 0.3 * hn) * shade[..., None]
    wet = H2 < water
    depth = np.clip((water - H2) / 12.0, 0, 1)
    wc = np.stack([40 + 60 * (1 - depth), 90 + 80 * (1 - depth), 150 + 60 * (1 - depth)], -1)
    rgb[wet] = wc[wet]
    blocked = (F2 & 1) != 0
    rgb[blocked & ~wet] *= 0.4
    reach = (F2 & 2) != 0
    rgb[reach] = rgb[reach] * 0.7 + np.array([255, 255, 200]) * 0.3
    # (walkable land seat 1 does not reach: orange)
    lone = ~blocked & ~reach & ~wet
    rgb[lone] = rgb[lone] * 0.6 + np.array([255, 140, 0]) * 0.4
    if comp:
        # the walkable ground's parts with every gate closed (scipy): each big one in a colour of its own, outlined
        from scipy import ndimage
        free = ~blocked & ((F2 & 4) == 0)
        lab, nlab = ndimage.label(free, structure=np.ones((3, 3)))
        sizes = ndimage.sum(free, lab, range(1, nlab + 1))
        big = [i + 1 for i in np.argsort(-sizes)[:12] if sizes[i] > 400]
        cols = [(255, 80, 80), (80, 200, 255), (255, 220, 60), (180, 90, 255), (60, 255, 140), (255, 140, 220), (140, 255, 255), (255, 170, 90), (170, 170, 255), (200, 255, 120), (255, 255, 255), (120, 160, 120)]
        for n, li in enumerate(big):
            m = lab == li
            edge = m & ~ndimage.binary_erosion(m, iterations=2)
            rgb[edge] = cols[n]
    # (reachable only through a gate that is not the player's: hatched)
    viaGate = reach & ((F2 & 8) == 0)
    hatch = ((np.indices(viaGate.shape).sum(0) // 2) % 3) == 0
    rgb[viaGate & hatch] = rgb[viaGate & hatch] * 0.5 + np.array([120, 60, 200]) * 0.5
    gate = (F2 & 4) != 0
    rgb[gate] = [255, 0, 255]
    img = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8))
    dr = ImageDraw.Draw(img)
    def P(x, z): return ((x - ox) / mpp, (z - oy) / mpp)
    # grid
    W_, H_ = img.size
    x0m, y0m = ox, oy
    x1m, y1m = ox + W_ * mpp, oy + H_ * mpp
    font = ImageFont.load_default()
    for gxm in range(int(np.floor(x0m / 100)) * 100, int(x1m) + 1, 100):
        px, _ = P(gxm, 0)
        dr.line([(px, 0), (px, H_)], fill=(255, 255, 255) if gxm % 500 == 0 else (200, 200, 200), width=1)
        if gxm % 200 == 0: dr.text((px + 2, 2), str(gxm), fill=(255, 255, 255), font=font)
    for gym in range(int(np.floor(y0m / 100)) * 100, int(y1m) + 1, 100):
        _, py = P(0, gym)
        dr.line([(0, py), (W_, py)], fill=(255, 255, 255) if gym % 500 == 0 else (200, 200, 200), width=1)
        if gym % 200 == 0: dr.text((2, py + 2), str(gym), fill=(255, 255, 255), font=font)
    if 'old' in d:
        a = d['old']; p0 = P(a[0], a[1]); p1 = P(a[2], a[3])
        dr.rectangle([p0, p1], outline=(255, 255, 0))
    # regions
    for r in d['regions']:
        used = trig is not None and (r['guid'] in trig or ('"' + r['name'] + '"') in trig)
        col = (255, 150, 0) if used else (230, 230, 80)
        if trig is not None and not used: continue
        for s in r['shapes']:
            oval, en, x, y, w, h = s
            if w > 3000 or h > 3000: continue
            p0 = P(x, y); p1 = P(x + w, y + h)
            if oval: dr.ellipse([p0, p1], outline=col)
            else: dr.rectangle([p0, p1], outline=col)
        if r['shapes'] and r['shapes'][0][4] < 3000:
            s = r['shapes'][0]; dr.text(P(s[2] + 2, s[3] + 2), r['name'][:22], fill=col, font=font)
    rel = {p['id']: p for p in d['players']}
    def col_of(o):
        p = rel.get(o)
        if p is None: return (200, 200, 200)
        if p['seat'] >= 2: return (0, 230, 230)
        return {'me': (60, 255, 60), 'enemy': (255, 40, 40), 'ally': (60, 120, 255), 'neutral': (170, 170, 170)}[p['rel']]
    for typ, x, z in d['resources']:
        px, py = P(x, z)
        c = {'tree': (20, 70, 20), 'stone': (230, 230, 230), 'bush': (255, 120, 200), 'fish': (0, 255, 255)}.get(typ)
        if c is None: continue
        if typ == 'tree': img.putpixel((int(px), int(py)), c) if 0 <= px < W_ and 0 <= py < H_ else None
        else: dr.rectangle([px - 1, py - 1, px + 1, py + 1], fill=c)
    for b in d['buildings']:
        px, py = P(*b['p']); r = max(1.5, b['r'] / mpp)
        c = col_of(b['o'])
        if b.get('wall'): dr.rectangle([px - r, py - r, px + r, py + r], outline=c)
        else: dr.rectangle([px - r, py - r, px + r, py + r], fill=c, outline=(0, 0, 0))
    for u in d['units']:
        px, py = P(*u['p'])
        dr.point((px, py), fill=col_of(u['o']))
    for s in d['starts']:
        px, py = P(*s['p']); c = col_of(s['o'])
        dr.line([(px - 6, py - 6), (px + 6, py + 6)], fill=c, width=2); dr.line([(px - 6, py + 6), (px + 6, py - 6)], fill=c, width=2)
        dr.text((px + 7, py - 7), str(s['o']), fill=c, font=font)
    if crop:
        a = P(crop[0], crop[1]); b = P(crop[2], crop[3])
        img = img.crop((int(a[0]), int(a[1]), int(b[0]), int(b[1])))
    img.save(out)
    print(out, img.size, 'players:', ', '.join(f"{p['id']}:{p['tribe']}:{p['rel']}{'(seat %d)' % p['seat'] if p['seat'] else ''}" for p in d['players']))

if __name__ == '__main__':
    main()
