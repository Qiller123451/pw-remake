"""The models of cutscene actors that only the cutscenes use, which the toolkit's remake export leaves out (its
pipeline takes the tech tree's, the maps' and some archives' models - not the objects a sequence brings itself).

Mission 1's sc_1050 ("Now that's my weak spot. He's staying with me.") shows Stina with her sabre-tooth as one
animated object of its own, class NS1050_stina_eusmilus (a CDecoColObj; its model ns1050_stina_eusmilus with the clip
NS1050_anim is in all_characters.gsf). The scene hides the units of its region - the real Stina too - so without that
model nobody stood where Stina talks. The official game's class files do not even name the class (MIRAGE's do): the
model is the class's own name then, as the cutscene player looks it up (SessionSequenceStage: the class's model, else
the model of the class's name).

Which models: every actor of every sequence of the configuration's missions (world.json sequences[*].play.actors)
whose model - the class file's gfx (gamedata classgfx), else the class name - is neither in the converted data
(assets/manifest.json) nor in the overrides already, and is in one of the installation's archives. They are
converted like the effect models (effect_models.convert: overrides/assets/models, the textures the data lacks,
manifest_fx.json, which the view merges into the manifest).

    python tools/prepare/cutscene_models.py <ParaWorld folder> <converted data folder> <missions folder> <overrides folder>
                                            [--toolkit <folder>] [--mod MIRAGE] [--force]

prepare.py runs it for each configuration after the missions are converted.
"""
import argparse
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import effect_models  # noqa: E402

# archives looked at first (the cutscene objects are characters, animals or misc objects)
FIRST = ('all_characters', 'all_animals', 'all_misc')


def _load(path, default):
    try:
        with open(path, encoding='utf-8') as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def wanted(missions_dir, gamedata, have):
    """the models the missions' cutscene actors need that `have` (lower-case model names) lacks: {model: [mission:
    sequence, ...]}"""
    cg = (gamedata or {}).get('classgfx') or {}
    out = {}
    for wj in sorted(glob.glob(os.path.join(missions_dir, '*', 'world.json'))):
        w = _load(wj, {})
        for path, s in (w.get('sequences') or {}).items():
            for a in ((s or {}).get('play') or {}).get('actors') or []:
                cls = str(a.get('cls') or '').lower()
                if not cls or cls == 'unknown':
                    continue
                model = str(cg.get(cls) or cls).lower()
                if model in have or cls in have:
                    continue
                out.setdefault(model, []).append(os.path.basename(os.path.dirname(wj)) + ':' + str(s.get('id') or path))
    return out


def locate(gsf, archives, names, log=print):
    """{model: archive name} of the models found in the installation's archives ({archive name: .gsf path})"""
    order = sorted(archives, key=lambda a: (a.lower() not in FIRST, FIRST.index(a.lower()) if a.lower() in FIRST else 0, a.lower()))
    left, found = set(names), {}
    for arch in order:
        if not left:
            break
        path = archives[arch]
        if not path or not os.path.isfile(path):
            continue
        try:
            a = gsf.Archive(path)
        except Exception as e:                     # noqa: BLE001 (a broken archive only loses its models)
            log('  cutscene models: archive %s: %s' % (arch, e))
            continue
        for n in sorted(left):
            if a.index(n) is not None:
                found[n] = arch
        left -= set(found)
    return found


def convert(gsf, archives, data_dir, missions_dir, overrides, force=False, log=print):
    """the missing cutscene models of a configuration into its overrides; returns the names converted or kept"""
    assets = os.path.join(data_dir, 'assets')
    have = set(_load(os.path.join(assets, 'manifest.json'), {}).get('models', {}))
    fx = _load(os.path.join(overrides, 'assets', 'manifest_fx.json'), {})
    if fx.get('version') == effect_models.VERSION:
        have |= {m for m in fx.get('models', {}) if os.path.isfile(os.path.join(overrides, 'assets', 'models', m + '.glb'))} if not force else set()
    need = wanted(missions_dir, _load(os.path.join(data_dir, 'gamedata.json'), {}), have)
    if not need:
        return []
    found = locate(gsf, archives, need, log)
    for n in sorted(set(need) - set(found)):
        log('  cutscene model %s (%s): in no archive' % (n, ', '.join(need[n][:3])))
    if not found:
        return []
    return effect_models.convert(gsf, archives, assets, overrides, force=force, log=log, models=found)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('paraworld', help='the ParaWorld folder (the one with Data in it)')
    ap.add_argument('data', help='the converted game data (the folder with gamedata.json and assets/manifest.json)')
    ap.add_argument('missions', help="the configuration's missions folder (the Lua mission folders with world.json)")
    ap.add_argument('overrides', help="the configuration's overrides folder")
    ap.add_argument('--toolkit', help='the ParaWorld Toolkit folder (with pwexport in it)')
    ap.add_argument('--mod', default='', help="the configuration's mod (MIRAGE ...; empty: the official game)")
    ap.add_argument('--force', action='store_true')
    a = ap.parse_args(argv)
    if a.toolkit:
        sys.path.insert(0, a.toolkit)
    from pwexport import gsf                       # noqa: E402
    from pwexport.install import Install           # noqa: E402
    from pwexport.mods import ModInstall           # noqa: E402
    inst = ModInstall(a.paraworld, mod=a.mod) if a.mod else Install(a.paraworld)
    done = convert(gsf, inst.archives(), a.data, a.missions, a.overrides, a.force)
    print('%d cutscene models' % len(done))


if __name__ == '__main__':
    main()
