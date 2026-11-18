"""The sequel's own art (docs/SEQUEL.md "Art"): the models, textures and road surfaces of the remake's own campaigns,
made in Blender by the ParaWorld Toolkit (remake/art/sequel, kept built in its repository: built/models/*.glb,
built/tex/*, built/manifest.json). The toolkit's pipeline step "sequel" normally copies them into the converted game
data already; this step fills in whatever a configuration's data lacks (an export made before the step existed, a
model added since) - into the configuration's overrides, never into the toolkit's data:

    overrides/assets/models/<name>.glb      the models the data lacks (or whose built file is newer)
    overrides/assets/tex/<file>             their textures and the road surfaces sq_road_*.png
    overrides/assets/manifest_sequel.json   {"models": {<name>: entry}} of the models copied (the game merges it)
    overrides/assets/ui/sq_cards.jpg        the sequel's unit / building cards, when the data's card atlas lacks them
    overrides/assets/ui/sq_cards_x.jpg      the cards of the classes that keep an original's look (its card + a mark)
    overrides/assets/ui/atlas_sequel.json   their atlas entries (the game merges them over assets/ui/atlas.json)

    python sequel_art.py <toolkit folder> <data folder> <overrides folder> [--force]

Incremental: a file is copied when the data has none, or when the built one is newer than the copy (--force: all)."""
import json
import os
import shutil
import sys


def built_dir(toolkit):
    """the toolkit's built sequel art, or None"""
    for rel in (('remake', 'art', 'sequel', 'built'), ('art', 'sequel', 'built')):
        d = os.path.join(toolkit, *rel)
        if os.path.isfile(os.path.join(d, 'manifest.json')):
            return d
    return None


def _newer(src, dst):
    return not os.path.exists(dst) or os.path.getmtime(src) > os.path.getmtime(dst) + 1


def install(built, data, overrides, force=False, log=print):
    """copies what the data lacks; returns (models copied, textures copied)"""
    entries = json.load(open(os.path.join(built, 'manifest.json'), encoding='utf-8'))['models']
    dman = {}
    try:
        dman = json.load(open(os.path.join(data, 'assets', 'manifest.json'), encoding='utf-8')).get('models', {})
    except (OSError, ValueError):
        pass
    om = os.path.join(overrides, 'assets', 'models')
    ot = os.path.join(overrides, 'assets', 'tex')
    man_path = os.path.join(overrides, 'assets', 'manifest_sequel.json')
    try:
        man = json.load(open(man_path, encoding='utf-8'))
    except (OSError, ValueError):
        man = {'models': {}}
    models = 0
    for name, e in entries.items():
        src = os.path.join(built, 'models', name + '.glb')
        if not os.path.isfile(src):
            continue
        in_data = name in dman and os.path.isfile(os.path.join(data, 'assets', 'models', name + '.glb'))
        dst = os.path.join(om, name + '.glb')
        if in_data and not force and not os.path.exists(dst):
            continue
        if force or _newer(src, dst):
            os.makedirs(om, exist_ok=True)
            shutil.copyfile(src, dst)
            models += 1
        man['models'][name] = {k: v for k, v in e.items() if k != 'sha'}
    tex = 0
    tdir = os.path.join(built, 'tex')
    for f in sorted(os.listdir(tdir)) if os.path.isdir(tdir) else []:
        src = os.path.join(tdir, f)
        dst = os.path.join(ot, f)
        if os.path.isfile(os.path.join(data, 'assets', 'tex', f)) and not force and not os.path.exists(dst):
            continue
        if force or _newer(src, dst):
            os.makedirs(ot, exist_ok=True)
            shutil.copyfile(src, dst)
            tex += 1
    if man['models']:
        os.makedirs(os.path.dirname(man_path), exist_ok=True)
        json.dump(man, open(man_path, 'w', encoding='utf-8'), separators=(',', ':'))
    ncards = cards(built, data, overrides, force)
    log('  sequel art: %d models, %d textures copied (%d models listed in manifest_sequel.json), %d cards'
        % (models, tex, len(man['models']), ncards))
    return models, tex


# as the toolkit's pipeline (remake/pipeline/sequel_assets.py): class -> (the original whose card it takes, its mark)
BADGES = {'sq_shock_trooper': ('seas_gunner', 'bolt'), 'sq_tranq': ('seas_marksman', 'dart')}
CARD = (128, 122)
LEVELS = ('0', '1', '2', '3', '4')


def _badge(im, kind):
    """a round dark plate in the lower right corner with the mark: a lightning bolt, a tranquilizer dart"""
    from PIL import ImageDraw
    d = ImageDraw.Draw(im)
    w, h = im.size
    cx, cy, r = w - 24, h - 24, 19
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(30, 14, 8), outline=(214, 170, 90), width=3)
    if kind == 'bolt':
        pts = [(4, -15), (-9, 2), (-1, 2), (-5, 15), (9, -3), (1, -3), (6, -15)]
        d.polygon([(cx + x, cy + y) for x, y in pts], fill=(255, 226, 80), outline=(140, 90, 10))
    else:
        d.line((cx - 11, cy + 11, cx + 8, cy - 8), fill=(220, 220, 225), width=3)
        d.line((cx - 4, cy + 4, cx + 6, cy - 6), fill=(70, 190, 200), width=7)
        d.polygon([(cx + 6, cy - 6), (cx + 15, cy - 9), (cx + 9, cy - 15)], fill=(230, 60, 40))


def cards(built, data, overrides, force=False):
    """the sequel's cards for the HUD (portraits of its classes) when the data's atlas lacks them (an export whose
    toolkit step "sequel" did not run): atlas_sequel.json beside the copied sheets; returns the number of entries.
    Without Pillow the marked classes simply show their original's card."""
    try:
        c = json.load(open(os.path.join(built, 'manifest.json'), encoding='utf-8')).get('cards')
    except (OSError, ValueError):
        return 0
    if not c or not os.path.isfile(os.path.join(built, 'ui', c['file'])):
        return 0
    dui = os.path.join(data, 'assets', 'ui')
    try:
        atlas = json.load(open(os.path.join(dui, 'atlas.json'), encoding='utf-8'))
    except (OSError, ValueError):
        atlas = {}
    oui = os.path.join(overrides, 'assets', 'ui')
    out_path = os.path.join(oui, 'atlas_sequel.json')
    names = list(c['cards']) + list(BADGES)
    def in_data(n):                                # listed, and its sheet is there (an atlas can name a missing file)
        e = atlas.get(n)
        return bool(e) and all(os.path.isfile(os.path.join(dui, v[0])) for v in e.values())
    if not force and all(in_data(n) for n in names) and not os.path.exists(out_path):
        return 0                                   # the toolkit put them into the data already
    os.makedirs(oui, exist_ok=True)
    src = os.path.join(built, 'ui', c['file'])
    if force or _newer(src, os.path.join(oui, c['file'])):
        shutil.copyfile(src, os.path.join(oui, c['file']))
    out = {name: {lv: [c['file'], *box] for lv in LEVELS} for name, box in c['cards'].items()}
    try:
        from PIL import Image
    except ImportError:
        Image = None
    sheet = Image.new('RGB', (CARD[0] * len(BADGES), CARD[1])) if Image else None
    x = 0
    for name, (orig, kind) in BADGES.items():
        e = atlas.get(orig)
        if not e:
            continue
        f, sx, sy, sw, sh = max(e.values(), key=lambda v: v[3] * v[4])
        if sheet is None or not os.path.isfile(os.path.join(dui, f)):
            out[name] = e                          # the original's card, unmarked
            continue
        im = Image.open(os.path.join(dui, f)).convert('RGB').crop((sx, sy, sx + sw, sy + sh)).resize(CARD, Image.LANCZOS)
        _badge(im, kind)
        sheet.paste(im, (x, 0))
        out[name] = {lv: ['sq_cards_x.jpg', x, 0, *CARD] for lv in LEVELS}
        x += CARD[0]
    if sheet is not None and x:
        sheet.save(os.path.join(oui, 'sq_cards_x.jpg'), quality=92)
    json.dump(out, open(out_path, 'w', encoding='utf-8'), separators=(',', ':'))
    return len(out)


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    built = built_dir(argv[0])
    if not built:
        print('no built sequel art in %s (remake/art/sequel/built)' % argv[0])
        return 1
    install(built, argv[1], argv[2], force='--force' in argv)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
