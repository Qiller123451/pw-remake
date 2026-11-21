"""The selection circle's box of every model: the GSF model info's box (corner and size in the GSF frame: x, y on the
ground, z up) - what the original sizes and places a selected object's circle by (PWClient.exe: model info + 0x34,
0x46b840 / 0x46b900 / 0x6bc100; docs/VIEW.md "Selection circles"). The converted .glb files keep only what is drawn
(no pick volumes, shadow models, low LODs), so their box is not the original's.

Writes <overrides>/assets/selboxes.json:  {"stamp": ..., "models": {"<model, lower case>": [cx, cy, cz, sx, sy, sz]}}
for every model of the installation's archives (a mod's archive of the same name replaces Base's: Install.archives).
The simulation reads it (ModelManifest.AddSelectionBoxes, SelectionRing.Original). Skipped when the archives did not
change since the last run (the stamp: their names, sizes and times), unless forced.

    python tools/prepare/selection_boxes.py --toolkit <ParaWorldToolkit> --paraworld E:\\Paraworld --overrides <folder>
"""
import argparse
import hashlib
import json
import os
import sys


def stamp(archives):
    rows = []
    for name, path in sorted(archives.items()):
        try:
            rows.append([name, os.path.getsize(path), int(os.path.getmtime(path))])
        except OSError:
            rows.append([name, 0, 0])
    return hashlib.sha1(json.dumps(rows).encode('utf-8')).hexdigest()


def convert(gsf, archives, overrides, force=False, log=print):
    """writes selboxes.json; returns the number of models in it"""
    out = os.path.join(overrides, 'assets', 'selboxes.json')
    st = stamp(archives)
    if not force and os.path.isfile(out):
        try:
            with open(out, encoding='utf-8') as f:
                old = json.load(f)
            if old.get('stamp') == st:
                return len(old.get('models') or {})
        except (OSError, ValueError):
            pass
    models = {}
    for name in sorted(archives):
        try:
            a = gsf.Archive(archives[name])
        except Exception as e:                      # a broken archive must not stop the others
            log('  selection boxes: %s unreadable (%s)' % (name, e))
            continue
        for n in a.names():
            box = (a.info(n) or {}).get('bbox')
            if box and len(box) == 6:
                # (the toolkit calls the second triple "max"; in the model info it is the box's size - corner + size)
                models.setdefault(n.lower(), [round(float(v), 4) for v in box])
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, 'w', encoding='utf-8') as f:
        json.dump({'stamp': st, 'models': models}, f, separators=(',', ':'))
    log('  selection boxes: %d models -> %s' % (len(models), out))
    return len(models)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('--toolkit', required=True)
    ap.add_argument('--paraworld', required=True)
    ap.add_argument('--mod', default='')
    ap.add_argument('--overrides', required=True)
    ap.add_argument('--force', action='store_true')
    a = ap.parse_args(argv)
    sys.path.insert(0, a.toolkit)
    from pwexport import gsf                       # noqa: E402  (the toolkit's GSF reader)
    from pwexport.install import Install           # noqa: E402
    from pwexport.mods import ModInstall           # noqa: E402
    inst = ModInstall(a.paraworld, mod=a.mod) if a.mod else Install(a.paraworld)
    convert(gsf, inst.archives(), a.overrides, a.force)


if __name__ == '__main__':
    main()
