"""Make the game configurations of the remake from the player's own ParaWorld installation.

    python tools/prepare/prepare.py [--paraworld E:\\Paraworld] [--toolkit <ParaWorldToolkit folder>]
                                    [--data <toolkit data folder>] [--content <content folder>] [--force]

What it does (docs/CONTENT.md):
  1. finds the toolkit's converted game data: <data>/remake/<installation id> (official game) and
     <installation id>@<mod> (MIRAGE ...) - made by the ParaWorld Toolkit's launcher ("Prepare the game data"), in
     <ParaWorld>/export/remake or %APPDATA%/ParaWorldToolkit/remake;
  2. exports every campaign mission of each configuration with the toolkit's exporter (pwexport.campaign) into
     content/cache/campaign/<config>/ - only missions whose map, or the exporter, changed since the last run;
  3. converts the missions to Lua mission folders of the intermediate layout (campaign_to_lua.py: mission.json,
     world.json, triggers.lua, texts/*.json) in content/cache/missions/<config>/<lang>/;
  3a. converts the maps to the remake's map format (tools/mapconvert, C#: docs/MAP_FORMAT.md) - every original map
     of the configuration's Data folders (Data/<folder>/Maps/**/*.ula) into content/configs/<config>/maps/, the
     remake's own maps (game/maps/*.pwmap.json) into maps/Remake/, and every mission (its map + the folder of step 3)
     into content/configs/<config>/missions/<NN>_<key>/ (map.lua, terrain/*.png, world.lua, triggers.lua, texts/*.lua;
     custom.lua there is kept). Incremental: a map is converted again only when its source files or the converter
     changed (content/cache/maps/<config>.json), --force converts all. Needs the .NET SDK (dotnet), as the game;
  4. converts the story films and the start films (logos, intro) to Ogg Theora (films.py; needs ffmpeg, else
     skipped) into content/configs/<config>/overrides/assets/video/;
  5. converts the original effect models the toolkit's export leaves out (effect_models.py: the ruins' dust, the
     rocket smoke, the spirits, fireworks, tornado, quicksand) into content/configs/<config>/overrides/assets/;
  5a. converts the models only the cutscenes' own actors use that the export leaves out (cutscene_models.py: mission
     1's Stina with her sabre-tooth in sc_1050) into the same overrides;
  5a'. writes every model's GSF box (selection_boxes.py: the box the original sizes the selection circles by) into
     content/configs/<config>/overrides/assets/selboxes.json;
  5b. converts the landscape settings' day-time light tables (sky.py: Texture/Scape/<Setting>/skygradient*.tga) into
     content/configs/<config>/overrides/assets/terrain/<Setting>/sky.json;
  5b2. converts the fog of war's cloud atlases (fog_textures.py: Texture/Scape/<Setting>/fog_(0256).dds and the
     fallback Texture/Effects/fogofwar.tga) into overrides/assets/terrain/<Setting>/fog.png and fogofwar.png;
  5c. converts the buildings' setting textures (setting_textures.py: the atlases repainted per landscape setting,
     Texture/Buildings/settings/<setting>/*.dds - snow in the Icewaste, sand in the Savanna ...) into
     content/configs/<config>/overrides/assets/tex/settings/<setting>/<name>.png + index.json;
  6. fills in the sequel's own art the converted data lacks (sequel_art.py: the toolkit's remake/art/sequel/built ->
     overrides/assets, manifest_sequel.json; docs/SEQUEL.md),
  7. writes content/configs/<config>/config.json.

Needs Python 3.8+ and the ParaWorld Toolkit (its pwexport package). Nothing is uploaded anywhere; nothing in the
ParaWorld folder is changed.
"""
import argparse
import datetime
import hashlib
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))

# what each configuration is: the toolkit's mod name -> our id, title, Data folders, playable tribes
CONFIGS = {
    '': {'id': 'base', 'title': 'ParaWorld', 'folders': ['Base', 'BoosterPack1'],
         'tribes': ['Hu', 'Aje', 'Ninigi'],
         'description': 'The official game (with BoosterPack 1): Norsemen, Dustriders, Dragon Clan.'},
    'MIRAGE': {'id': 'mirage', 'title': 'MIRAGE', 'folders': ['Base', 'BoosterPack3', 'MIRAGE'],
               'tribes': ['Hu', 'Aje', 'Ninigi', 'SEAS'],
               'description': 'The MIRAGE mod: its rules, units and heroes, and the SEAS as a playable tribe.'},
}

CONVERTER_VERSION = 1   # bump when the Lua conversion changes: every mission is converted again


def log(*a):
    print(*a, flush=True)


def guess_paraworld():
    for p in [os.path.abspath(os.path.join(ROOT, '..')), 'E:/Paraworld', 'C:/Program Files (x86)/Sunflowers/ParaWorld',
              'C:/Games/ParaWorld']:
        if os.path.isdir(os.path.join(p, 'Data', 'Base')):
            return p
    return None


def guess_toolkit(pw):
    for p in [os.path.join(pw or '', 'ParaWorldToolkit'), os.path.abspath(os.path.join(ROOT, '..', 'ParaWorldToolkit')),
              os.path.abspath(os.path.join(ROOT, '..', 'pw-exporter'))]:
        if os.path.isfile(os.path.join(p, 'pwexport', 'campaign.py')):
            return p
    return None


def data_roots(pw, given):
    """folders that may hold <installation id>[@mod] game data folders of the toolkit"""
    roots = []
    if given:
        roots += [given, os.path.join(given, 'remake')]
    if pw:
        roots.append(os.path.join(pw, 'export', 'remake'))
    appdata = os.environ.get('APPDATA') or os.path.expanduser('~/.config')
    for base in [os.path.join(appdata, 'ParaWorldToolkit'), os.path.join(appdata, 'paraworld-toolkit')]:
        roots.append(os.path.join(base, 'remake'))
    if os.environ.get('PWTOOLKIT_DATA'):
        roots.append(os.path.join(os.environ['PWTOOLKIT_DATA'], 'remake'))
    return [r for r in roots if os.path.isdir(r)]


def find_data(pw, given):
    """{mod: folder} of the newest built data per configuration"""
    found = {}
    for root in data_roots(pw, given):
        for name in os.listdir(root):
            d = os.path.join(root, name)
            if not os.path.isfile(os.path.join(d, 'gamedata.json')) or not os.path.isfile(os.path.join(d, 'assets', 'manifest.json')):
                continue
            mod = name.split('@', 1)[1] if '@' in name else ''
            try:
                with open(os.path.join(d, 'build.json'), encoding='utf-8') as f:
                    mod = json.load(f).get('mod', mod) or mod
            except (OSError, ValueError):
                pass
            t = os.path.getmtime(os.path.join(d, 'gamedata.json'))
            if mod not in found or t > found[mod][1]:
                found[mod] = (d, t)
    return {k: v[0] for k, v in found.items()}


def file_stamp(path):
    try:
        st = os.stat(path)
        return '%d:%d' % (st.st_size, int(st.st_mtime))
    except OSError:
        return ''


def export_missions(toolkit, pw, mod, cache_dir, lang, force):
    """pwexport.campaign for every mission of the configuration, incremental; returns the index list"""
    sys.path.insert(0, toolkit)
    from pwexport import campaign as C            # noqa: E402  (the toolkit's exporter)
    from pwexport.install import Install           # noqa: E402
    from pwexport.mods import ModInstall           # noqa: E402
    sys.path.insert(0, HERE)
    import campaign_export as CE                   # noqa: E402  (index like the toolkit's server)
    import pack_texts                              # noqa: E402  (the add-ons' string tables, pack_texts.py)
    pack_texts.install(C)
    import seq_actors                              # noqa: E402  (the cutscene actors' clips and links, seq_actors.py)
    seq_actors.install(C)

    inst = ModInstall(pw, mod=mod) if mod else Install(pw)
    os.makedirs(cache_dir, exist_ok=True)
    stamps_path = os.path.join(cache_dir, 'stamps.json')
    try:
        with open(stamps_path, encoding='utf-8') as f:
            stamps = json.load(f)
    except (OSError, ValueError):
        stamps = {}
    exporter = file_stamp(C.__file__) + '|' + pack_texts.VERSION + '|' + seq_actors.VERSION
    folders = [m.lower() for m in CONFIGS[mod]['folders']]
    index = []
    for e, entry in zip(C.campaign_maps(inst), CE.index(inst, lang)):
        if e['pack'].lower() not in folders:      # maps of mods outside the configuration (the exporter fills gaps)
            continue
        out = os.path.join(cache_dir, 'single_%02d.json' % e['id'])
        key = '%s|%s|%s' % (file_stamp(e['path']), exporter, lang)
        if force or stamps.get(out) != key or not os.path.isfile(out):
            log('  export mission %d (%s)' % (e['id'], e['file']))
            doc = C.campaign(e['path'], inst, lang)
            with open(out, 'w', encoding='utf-8') as f:
                json.dump(doc, f, ensure_ascii=False)
            stamps[out] = key
        entry['json'] = os.path.basename(out)
        index.append(entry)
    with open(stamps_path, 'w', encoding='utf-8') as f:
        json.dump(stamps, f, indent=1)
    with open(os.path.join(cache_dir, 'index.json'), 'w', encoding='utf-8') as f:
        json.dump(index, f, ensure_ascii=False, indent=1)
    return index


def prepare_films(toolkit, pw, mod, cache_dir, overrides_dir, lang, force):
    """films.py: the story films as Ogg Theora for Godot"""
    sys.path.insert(0, toolkit)
    from pwexport import video                     # noqa: E402  (the toolkit's film finder and ffmpeg)
    from pwexport.install import Install           # noqa: E402
    from pwexport.mods import ModInstall           # noqa: E402
    sys.path.insert(0, HERE)
    import films                                   # noqa: E402
    inst = ModInstall(pw, mod=mod) if mod else Install(pw)
    try:
        story = films.convert_films(inst, video, cache_dir, overrides_dir, lang, force, log)
    except Exception as e:                         # a film problem must not stop the preparation
        log('  films failed: %s' % e)
        story = (0, 0)
    try:                                           # the logos and the intro at start-up (the game's IntroScreen)
        start = films.convert_start_films(inst, video, cache_dir, overrides_dir, lang, force, log)
        log('  start films: %d of %d ready' % start)
    except Exception as e:
        log('  start films failed: %s' % e)
    return story


def prepare_effects(toolkit, pw, mod, data, overrides_dir, force):
    """effect_models.py: the original effect models the converted data lacks"""
    sys.path.insert(0, toolkit)
    from pwexport import gsf                       # noqa: E402  (the toolkit's model converter)
    from pwexport.install import Install           # noqa: E402
    from pwexport.mods import ModInstall           # noqa: E402
    sys.path.insert(0, HERE)
    import effect_models                           # noqa: E402
    inst = ModInstall(pw, mod=mod) if mod else Install(pw)
    try:
        return len(effect_models.convert(gsf, inst.archives(), os.path.join(data, 'assets'), overrides_dir, force=force, log=log))
    except Exception as e:                         # an effect problem must not stop the preparation
        log('  effect models failed: %s' % e)
        return 0


def prepare_cutscene_models(toolkit, pw, mod, data, missions_dir, overrides_dir, force):
    """cutscene_models.py: the models only the cutscenes' own actors use (mission 1's Stina with her sabre-tooth in
    sc_1050) that the converted data lacks"""
    sys.path.insert(0, toolkit)
    from pwexport import gsf                       # noqa: E402  (the toolkit's model converter)
    from pwexport.install import Install           # noqa: E402
    from pwexport.mods import ModInstall           # noqa: E402
    sys.path.insert(0, HERE)
    import cutscene_models                         # noqa: E402
    inst = ModInstall(pw, mod=mod) if mod else Install(pw)
    try:
        return len(cutscene_models.convert(gsf, inst.archives(), data, missions_dir, overrides_dir, force=force, log=log))
    except Exception as e:                         # a model problem must not stop the preparation
        log('  cutscene models failed: %s' % e)
        return 0


def prepare_selection_boxes(toolkit, pw, mod, overrides_dir, force):
    """selection_boxes.py: every model's GSF box, which the original sizes its selection circles by"""
    sys.path.insert(0, toolkit)
    from pwexport import gsf                       # noqa: E402  (the toolkit's GSF reader)
    from pwexport.install import Install           # noqa: E402
    from pwexport.mods import ModInstall           # noqa: E402
    sys.path.insert(0, HERE)
    import selection_boxes                         # noqa: E402
    inst = ModInstall(pw, mod=mod) if mod else Install(pw)
    try:
        return selection_boxes.convert(gsf, inst.archives(), overrides_dir, force=force, log=log)
    except Exception as e:                         # a box problem must not stop the preparation
        log('  selection boxes failed: %s' % e)
        return 0


def convert_missions(cache_dir, missions_dir, force):
    """campaign_to_lua.py: the exported missions -> Lua mission folders of the intermediate layout (+ index.json);
    convert_maps makes the game's mission folders from them"""
    conv = os.path.join(HERE, 'campaign_to_lua.py')
    args = [sys.executable, conv, cache_dir, missions_dir]
    if force:
        args.append('--force')
    log('  ' + ' '.join(args))
    r = subprocess.run(args)
    if r.returncode != 0:
        raise SystemExit('the Lua conversion failed (exit code %d)' % r.returncode)


MAPCONVERT = os.path.join(ROOT, 'tools', 'mapconvert')


def convert_maps(cdir, pw, folders, missions_from, force):
    """tools/mapconvert (C#): the configuration's original maps and its missions -> the remake's map format
    (docs/MAP_FORMAT.md). False when it could not run (no dotnet) or a map failed - the game then lacks those maps."""
    args = ['dotnet', 'run', '--project', MAPCONVERT, '-c', 'Release', '--', 'prepare', '--out', cdir,
            '--install', pw, '--folders', ','.join(folders), '--missions-from', missions_from]
    if force:
        args.append('--force')
    log('  ' + ' '.join(args))
    try:
        r = subprocess.run(args)
    except FileNotFoundError:
        log('  the maps were NOT converted: the .NET SDK (dotnet) was not found - install it (the game needs it too) '
            'and run Prepare again')
        return False
    if r.returncode != 0:
        log('  some maps were not converted (exit code %d, see above) - the game lists only the converted ones' % r.returncode)
        return False
    return True


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--paraworld', help='the ParaWorld folder (the one with Data in it)')
    ap.add_argument('--toolkit', help='the ParaWorld Toolkit folder (with pwexport in it)')
    ap.add_argument('--data', help="the toolkit's data folder (default: <ParaWorld>/export or %%APPDATA%%/ParaWorldToolkit)")
    ap.add_argument('--content', default=os.path.join(ROOT, 'content'), help='where to write the configurations')
    ap.add_argument('--lang', default='uk', help='text language of the missions (uk = English, de, fr, pl, ru ...)')
    ap.add_argument('--force', action='store_true', help='export and convert everything again')
    ap.add_argument('--no-films', action='store_true', help='do not convert the story films (they take a while)')
    a = ap.parse_args(argv)

    pw = a.paraworld or guess_paraworld()
    if not pw or not os.path.isdir(os.path.join(pw, 'Data')):
        raise SystemExit('ParaWorld not found - give its folder with --paraworld')
    toolkit = a.toolkit or guess_toolkit(pw)
    if not toolkit:
        raise SystemExit('the ParaWorld Toolkit (pwexport) was not found - give its folder with --toolkit')
    log('ParaWorld:', pw)
    log('toolkit:  ', toolkit)
    data = find_data(pw, a.data)
    if not data:
        raise SystemExit("no converted game data found. Start the ParaWorld Toolkit's launcher and run "
                         '"Prepare the game data" for the remake (once for the official game, once with MIRAGE).')
    made = []
    for mod, folder in sorted(data.items()):
        if mod not in CONFIGS:
            log('skipping data of an unknown configuration %r: %s' % (mod, folder))
            continue
        c = CONFIGS[mod]
        log('configuration %s: data %s' % (c['id'], folder))
        cdir = os.path.join(a.content, 'configs', c['id'])
        cache = os.path.join(a.content, 'cache', 'campaign', c['id'], a.lang)
        index = export_missions(toolkit, pw, mod, cache, a.lang, a.force)
        log('  %d missions' % len(index))
        # the missions' Lua folders of the intermediate layout, then the map converter makes the game's map folders
        # (maps and missions in the map format, docs/MAP_FORMAT.md)
        lua_cache = os.path.join(a.content, 'cache', 'missions', c['id'], a.lang)
        convert_missions(cache, lua_cache, a.force)
        convert_maps(cdir, pw, c['folders'], lua_cache, a.force)
        if not a.no_films:
            ready, total = prepare_films(toolkit, pw, mod, os.path.join(a.content, 'cache', 'films', a.lang),
                                         os.path.join(cdir, 'overrides'), a.lang, a.force)
            log('  films: %d of %d ready' % (ready, total))
        log('  effect models: %d' % prepare_effects(toolkit, pw, mod, folder, os.path.join(cdir, 'overrides'), a.force))
        log('  cutscene models: %d' % prepare_cutscene_models(toolkit, pw, mod, folder, lua_cache,
                                                             os.path.join(cdir, 'overrides'), a.force))
        log('  selection boxes: %d' % prepare_selection_boxes(toolkit, pw, mod, os.path.join(cdir, 'overrides'), a.force))
        # the day-time light tables of the landscape settings (sky.py: Texture/Scape/<Setting>/skygradient*.tga)
        sys.path.insert(0, HERE)
        import sky                                 # noqa: E402
        try:
            sky_data = os.path.join(pw, 'Data') if os.path.isdir(os.path.join(pw, 'Data')) else pw
            done = sky.convert(sky_data, c['folders'], os.path.join(cdir, 'overrides'), log)
            log('  sky tables: %s' % (', '.join(done) if done else 'none in the installation'))
        except Exception as e:                     # the light must not stop the preparation
            log('  sky tables failed: %s' % e)
        # the fog of war's cloud atlases (fog_textures.py: Texture/Scape/<Setting>/fog*, Texture/Effects/fogofwar* ->
        # overrides/assets/terrain/<Setting>/fog.png, fogofwar.png; docs/TERRAIN.md "Fog of war")
        import fog_textures                        # noqa: E402
        try:
            fog_data = os.path.join(pw, 'Data') if os.path.isdir(os.path.join(pw, 'Data')) else pw
            done = fog_textures.convert(fog_data, c['folders'], os.path.join(cdir, 'overrides'), a.force, log)
            log('  fog of war clouds: %s' % (', '.join(done) if done else 'none in the installation (the game falls back)'))
        except Exception as e:                     # the clouds must not stop the preparation
            log('  fog of war clouds failed: %s' % e)
        # the buildings' setting textures (setting_textures.py: Texture/Buildings/settings/<setting>/* ->
        # overrides/assets/tex/settings/, the snowy Icewaste atlases ...)
        import setting_textures                    # noqa: E402
        try:
            tex_data = os.path.join(pw, 'Data') if os.path.isdir(os.path.join(pw, 'Data')) else pw
            setting_textures.convert(tex_data, c['folders'], os.path.join(cdir, 'overrides'), a.force, log)
        except Exception as e:                     # the textures must not stop the preparation
            log('  setting textures failed: %s' % e)
        # the sequel's own art (Containment, docs/SEQUEL.md): what the converted data lacks, into the overrides
        sys.path.insert(0, HERE)
        import sequel_art                          # noqa: E402
        built = sequel_art.built_dir(toolkit)
        if built:
            try:
                sequel_art.install(built, folder, os.path.join(cdir, 'overrides'), a.force, log)
            except Exception as e:                 # the art must not stop the preparation
                log('  sequel art failed: %s' % e)
        else:
            log('  sequel art: not in the toolkit (remake/art/sequel/built) - the data must have it already')
        cfg = {'id': c['id'], 'title': c['title'], 'description': c['description'], 'mod': mod,
               'folders': c['folders'], 'playableTribes': c['tribes'], 'data': folder.replace('\\', '/'),
               'install': pw.replace('\\', '/'), 'language': a.lang,
               'prepared': '%s with converter %d' % (datetime.datetime.now().strftime('%Y-%m-%d %H:%M'), CONVERTER_VERSION)}
        os.makedirs(cdir, exist_ok=True)
        with open(os.path.join(cdir, 'config.json'), 'w', encoding='utf-8') as f:
            json.dump(cfg, f, indent=2)
        made.append(c['id'])
    log('done: ' + ', '.join(made))


if __name__ == '__main__':
    main()
