"""The original's setting textures for the game: the buildings' texture atlases repainted per landscape setting ->
the configuration's overrides/assets/tex/settings/<setting>/<name>.png + overrides/assets/tex/settings/index.json.

What the original has (docs/VIEW.md "Setting textures"): beside a texture <dir>/<name> below Data/<folder>/Texture/ a
copy per setting in <dir>/settings/<setting>/<name>_(<size>).dds, swapped in by the map's setting -
  Base:   Texture/Buildings/settings/<ashvalley|icewaste|jungle|northland|savanna>/<Aje|Hu|Ninigi>_Lvl_<n>_a, Hu_Misc_a
          (the Icewaste's with snow on the roofs and ledges, the Savanna's sand and dust, the Jungle's moss, the
          Ashvalley's soot, the Northland's a little weathering; the Northland only Hu_Lvl_1..3)
  MIRAGE: the same and cave3, Seas_Building_a, Seas_Tent; Texture/settings/icewaste/boosterpack_01_a (of
          Texture/boosterpack_01_a).
The toolkit's conversion has only the plain textures. Here: every such variant of the configuration's Data folders, a
later folder's replacing an earlier's (MIRAGE over Base, as the game's file lookup of a mod), of a folder's sizes the
largest, as PNG (with its alpha: the atlases' cut-outs). The index the game reads (src/Sim/Content/SettingTextures.cs):

  { "format": "pw-setting-textures", "version": 1, "settings": ["ashvalley", ...],
    "textures": { "buildings/hu_lvl_1_a": { "icewaste": { "file": "settings/icewaste/hu_lvl_1_a.png",
                                                          "source": "Base/Texture/Buildings/settings/icewaste/Hu_Lvl_1_a_(0512).dds",
                                                          "size": 512, "stamp": "<bytes>:<mtime>" } } } }

keys: the texture's path below Texture/ as the models name it (lower case, no extension: the GSF material's texture,
the glTF material's extras "gsf_texture"); "file" below assets/tex/. Incremental: a variant is converted again only
when its source changed (the stamp) or with force. Nothing is made up: without the files the index is empty.
"""
import json
import os
import re

INDEX = 'index.json'
EXTS = ('.dds', '.tga', '.png', '.bmp', '.jpg')
SIZED = re.compile(r'^(.*)_\((\d+)\)$')
VERSION = 1


def _ci_dir(base, name):
    """the entry of base named name ignoring case, or None"""
    p = os.path.join(base, name)
    if os.path.isdir(p):
        return p
    try:
        hit = next((e for e in os.listdir(base) if e.lower() == name.lower()), None)
    except OSError:
        return None
    return os.path.join(base, hit) if hit and os.path.isdir(os.path.join(base, hit)) else None


def find_variants(inst_data, folders):
    """{key: {setting: {'path', 'source', 'size'}}} of the configuration's Data folders (later folders replace earlier
    ones per key and setting; a folder's largest size). inst_data: the installation's Data folder."""
    out = {}
    for folder in folders:
        fdir = _ci_dir(inst_data, folder)
        tdir = _ci_dir(fdir, 'Texture') if fdir else None
        if not tdir:
            continue
        mine = {}
        for dp, dirs, files in os.walk(tdir):
            dirs.sort()
            rel = os.path.relpath(dp, tdir).replace('\\', '/')
            parts = [] if rel == '.' else rel.split('/')
            # <dir...>/settings/<setting>/<file>
            if len(parts) < 2 or parts[-2].lower() != 'settings':
                continue
            setting = parts[-1].lower()
            prefix = '/'.join(p.lower() for p in parts[:-2])
            for f in sorted(files):
                stem, ext = os.path.splitext(f)
                if ext.lower() not in EXTS:
                    continue
                m = SIZED.match(stem)
                size = int(m.group(2)) if m else (1 << 20)        # a plain file beats the sized copies (pwexport.gsf)
                name = (m.group(1) if m else stem).lower()
                key = (prefix + '/' + name) if prefix else name
                prev = mine.get((key, setting))
                if prev is None or size > prev['size']:
                    p = os.path.join(dp, f)
                    mine[(key, setting)] = {'path': p, 'size': size,
                                            'source': os.path.relpath(p, inst_data).replace('\\', '/')}
        for (key, setting), v in mine.items():
            out.setdefault(key, {})[setting] = v
    return out


def stamp(path):
    try:
        st = os.stat(path)
        return '%d:%d' % (st.st_size, int(st.st_mtime))
    except OSError:
        return ''


def to_png(src, dst):
    """a texture file -> PNG (RGBA when it has alpha below 250, else RGB); returns (width, height)"""
    from PIL import Image
    im = Image.open(src)
    im.load()
    if im.mode not in ('RGB', 'RGBA'):
        im = im.convert('RGBA')
    if im.mode == 'RGBA':
        lo, _ = im.getchannel('A').getextrema()
        if lo >= 250:
            im = im.convert('RGB')
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    tmp = dst + '.tmp'
    im.save(tmp, 'PNG')
    os.replace(tmp, dst)
    return im.size


def convert(inst_data, folders, overrides_dir, force=False, log=print):
    """every variant -> overrides/assets/tex/settings/<setting>/<name>.png and the index; returns the index document"""
    out_dir = os.path.join(overrides_dir, 'assets', 'tex', 'settings')
    index_path = os.path.join(out_dir, INDEX)
    try:
        with open(index_path, encoding='utf-8') as f:
            old = json.load(f).get('textures', {})
    except (OSError, ValueError):
        old = {}
    found = find_variants(inst_data, folders)
    textures, made, kept, failed = {}, 0, 0, 0
    used = {}                                             # file -> key (two keys with the same name in a setting)
    for key in sorted(found):
        for setting in sorted(found[key]):
            v = found[key][setting]
            base = key.rsplit('/', 1)[-1]
            file = 'settings/%s/%s.png' % (setting, base)
            if used.get(file, key) != key:
                file = 'settings/%s/%s.png' % (setting, key.replace('/', '_'))
            used[file] = key
            dst = os.path.join(overrides_dir, 'assets', 'tex', *file.split('/'))
            st = stamp(v['path'])
            prev = old.get(key, {}).get(setting)
            entry = {'file': file, 'source': v['source'], 'size': v['size'] if v['size'] < (1 << 20) else None, 'stamp': st}
            if not force and isinstance(prev, dict) and prev.get('stamp') == st and prev.get('source') == v['source'] \
                    and prev.get('file') == file and os.path.isfile(dst):
                textures.setdefault(key, {})[setting] = prev
                kept += 1
                continue
            try:
                w, h = to_png(v['path'], dst)
            except Exception as e:                        # an unreadable file: that variant stays plain
                log('  setting texture %s: %s' % (v['source'], e))
                failed += 1
                continue
            entry['size'] = max(w, h)
            textures.setdefault(key, {})[setting] = entry
            made += 1
    # files of an earlier run that are no longer variants
    for key, per in old.items():
        for setting, e in (per or {}).items():
            f = e.get('file') if isinstance(e, dict) else None
            if f and not any(x.get('file') == f for p in textures.values() for x in p.values()):
                try:
                    os.remove(os.path.join(overrides_dir, 'assets', 'tex', *f.split('/')))
                except OSError:
                    pass
    doc = {'format': 'pw-setting-textures', 'version': VERSION,
           'settings': sorted({s for p in textures.values() for s in p}),
           'folders': list(folders), 'textures': textures}
    os.makedirs(out_dir, exist_ok=True)
    with open(index_path, 'w', encoding='utf-8') as f:
        json.dump(doc, f, indent=1, sort_keys=True)
    n = sum(len(p) for p in textures.values())
    log('  setting textures: %d variants of %d textures (%s), %d converted, %d unchanged%s'
        % (n, len(textures), ', '.join(doc['settings']) or 'none in the installation', made, kept,
           ', %d unreadable' % failed if failed else ''))
    return doc
