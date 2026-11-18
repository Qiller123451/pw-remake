"""Export every campaign mission of one configuration (as `python -m pwexport.campaign --all` does, but with a mod
chain from pwexport.mods) and write index.json in the format of toolkit/remake.py campaign_index.

    python3 campaign_export.py <install root> <outdir> [--mod MIRAGE] [--lang uk] [--index-only]
Run with the pw-exporter repo on PYTHONPATH (or from its folder).
"""
import argparse
import json
import os
import sys

from pwexport import campaign as C, ula
from pwexport.install import Install
from pwexport.mods import ModInstall
from pwexport.campaign import text_dump

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pack_texts  # noqa: E402  (the add-ons' string tables)
pack_texts.install(C)
import seq_actors  # noqa: E402  (the cutscene actors' clips and start links)
seq_actors.install(C)


def index(inst, lang):
    """toolkit/remake.py RemakeServer.campaign_index, without the server"""
    tx = C.TextTable(inst, lang)
    res = []
    for e in C.campaign_maps(inst):
        r = {'id': e['id'], 'key': e['key'], 'map': 'maps/%s/%s' % (e['pack'], e['rel']),
             'data': 'campaign/%s/%s.json' % (e['pack'], e['rel']), 'name': os.path.splitext(e['file'])[0],
             'title': '', 'description': '', 'tribe': '', 'point_buy': e['point_buy'], 'min_credits': e['min_credits']}
        try:
            with open(e['path'], 'rb') as fh:
                surf = ula.unpack(fh.read())
            li = None

            def find(c):
                nonlocal li
                for ch in c.children:
                    if ch.tag == 'LInf' and ch.data is not None and li is None:
                        li = ula.level_info(ch.data)
                    find(ch)
            find(ula.read_tree(surf, 0))
            d = (li or {}).get('description') or {}
            r['name'] = d.get('Root/Base/LevelName') or d.get('LevelName') or r['name']
            r['title'] = tx.get(r['name'], r['name'])
            key = d.get('Root/Base/Description') or ''
            r['description'] = tx.get(key, '') if key else ''
            r['tribe'] = (d.get('Root/PlayerSettings/Player_0/Restrictions/Base/Tribes') or '').split(':')[0]
        except (OSError, ValueError, KeyError, IndexError) as ex:
            r['error'] = str(ex)
        res.append(r)
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('install')
    ap.add_argument('outdir')
    ap.add_argument('--mod', default='')
    ap.add_argument('--lang', default='uk')
    ap.add_argument('--index-only', action='store_true')
    a = ap.parse_args()
    inst = ModInstall(a.install, mod=a.mod) if a.mod else Install(a.install)
    print('configuration:', inst.mods, file=sys.stderr)
    os.makedirs(a.outdir, exist_ok=True)
    if not a.index_only:
        for e in C.campaign_maps(inst):
            doc = C.campaign(e['path'], inst, a.lang)
            stem = os.path.join(a.outdir, 'single_%02d' % e['id'])
            with open(stem + '.json', 'w', encoding='utf-8') as f:
                json.dump(doc, f, ensure_ascii=False, indent=1)
            with open(stem + '.txt', 'w', encoding='utf-8') as f:
                f.write(text_dump(doc))
            c = doc['counts']
            print('%-20s %-8s %-30s %5d objects %4d triggers %3d quests %4d warnings' % (
                e['file'], e['pack'], doc['map']['title'][:30], c['objects'], c['triggers'], c['quests'], len(doc['warnings'])))
    with open(os.path.join(a.outdir, 'index.json'), 'w', encoding='utf-8') as f:
        json.dump(index(inst, a.lang), f, ensure_ascii=False, indent=1)


if __name__ == '__main__':
    main()
