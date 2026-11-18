#!/usr/bin/env python3
"""Converts the toolkit's campaign missions (pw-campaign/1 JSON, docs/CAMPAIGN_FORMAT.md of the ParaWorld Toolkit) into
the remake's mission folders (docs/LUA_MAPS.md):

    <missions_dir>/<NN>_<key>/
        mission.json      what the menu shows and the setup needs: id, key, folder, title, description, tribe,
                          point_buy, min_credits, map (the .ula below the installation's Data folder), the map block,
                          the players
        world.json        objects, groups, question marks, regions, quests, variables, dialogues, sequences, refs,
                          the trigger folder tree and the parameter defaults of the trigger node types
        triggers.lua      every compiled trigger, 1:1, in the original order, as `trigger "name" { ... }`
        texts/<lang>.json the mission's texts
    <missions_dir>/index.json   the missions in playing order (for the campaign menu)

Usage:
    python3 campaign_to_lua.py <json_dir> <missions_dir> [--force]     every mission of <json_dir>/index.json
    python3 campaign_to_lua.py <mission.json> <mission_folder>         one mission

<json_dir>/index.json is a list of {id, key, map, name, title, description, tribe, point_buy, min_credits, json?}; "json"
names the mission's file (default single_NN.json). A mission whose triggers.lua is newer than its JSON (and than this
script) is skipped unless --force.

The parameters of a node are written without the ones equal to the node type's default (the engine applies the
defaults again) and without editor-only ones (renderable*); GUIDs stay where the engine needs them and get a Lua
comment with the resolved name (doc.refs). Triggers that were not compiled (switched off in the editor) do not exist
for the game: they are listed in a comment block at the end. No dependencies beyond the Python standard library.
"""
import json
import os
import re
import sys

# The readable names of the node types. The same table as src/Sim/Scripting/TriggerTypes.cs (a test there checks
# that both agree): code -> name, for conditions and for actions.
CONDITION_NAMES = {
    'TIME': 'Time', 'RTME': 'RandomTime', 'TIMR': 'Timer', 'TRUE': 'True', 'REGN': 'Region', 'DEAD': 'Dead',
    'DYIN': 'Dying', 'OBJP': 'ObjAttrib', 'ITEM': 'Item', 'BLDG': 'BuildingCount', 'SGHT': 'Sight',
    'ISFG': 'IsAttacking', 'WAYR': 'WaypointReached', 'CKGR': 'GroupCount', 'UNTT': 'Tutorial', 'CVAR': 'Var',
    'QUES': 'QuestState', 'PLYR': 'PlayerAttrib', 'TECH': 'Tech', 'PLDE': 'PlayerDefeated', 'DIPL': 'Diplomacy',
    'SQEN': 'SequenceEnded', 'DSEN': 'DialogEnded', 'GREG': 'GroupInRegion',
}
ACTION_NAMES = {
    'TRIG': 'SetTrigger', 'DGSC': 'Dialog', 'FDBK': 'Feedback', 'SQNZ': 'Sequence', 'GAOV': 'GameOver',
    'ACND': 'SetFolder', 'QUIT': 'Win', 'BONI': 'Bonus', 'QUES': 'Quest', 'QMRK': 'QuestionMark', 'MPNG': 'MapPing',
    'TIMR': 'Timer', 'PSND': 'Sound', 'INBA': 'InfoBar', 'ACDO': 'ObjDo', 'SPGR': 'SpawnGroup', 'OBAP': 'ObjAppear',
    'WYPT': 'Waypoints', 'COBJ': 'CreateObj', 'DELO': 'DeleteObj', 'OCPY': 'Occupy', 'CPLX': 'ComplexCreate',
    'ADGR': 'AddToGroup', 'REPL': 'Replace', 'TRSP': 'Transport', 'UNIT': 'Unit', 'EFCT': 'Effect', 'VARS': 'SetVar',
    'RSRC': 'Resources', 'DIPL': 'Diplomacy', 'SNFA': 'NeutralToAnimals', 'TECH': 'TechFilters',
    'PLCP': 'PlayerCaps', 'BLSL': 'BlockSlots', 'POPL': 'Population', 'ARGN': 'ActivateRegion',
    'MRGN': 'MoveRegion', 'SFOW': 'FogReveal', 'AIFT': 'AiFight', 'AIBV': 'AiBehavior', 'AIAM': 'AiAggression',
    'AIDA': 'AiDefendArea', 'AILU': 'AiLockUnit', 'AIRG': 'AiRegion', 'AIST': 'AiBehaviorOld', 'AICM': 'AiCommand',
    'MUSI': 'Music', 'WTHR': 'Weather', 'RMGR': 'RemoveFromGroup',
}
EDITOR_ONLY = {'renderable', 'renderable_type'}
DIFFICULTY = {0: 'easy', 1: 'medium', 2: 'hard'}
LUA_KEYWORDS = {'and', 'break', 'do', 'else', 'elseif', 'end', 'false', 'for', 'function', 'goto', 'if', 'in',
                'local', 'nil', 'not', 'or', 'repeat', 'return', 'then', 'true', 'until', 'while'}
IDENT = re.compile(r'^[A-Za-z_][A-Za-z0-9_]*$')
CANON_INT = re.compile(r'^(0|-?[1-9][0-9]{0,14})$')
GUID = re.compile(r'^[a-p]{32}$')
WRAP = 150


# ------------------------------------------------------------------------------------------------ Lua text
def lua_str(s):
    """A Lua string literal (double quotes; control characters escaped, UTF-8 kept)."""
    out = ['"']
    for ch in s:
        o = ord(ch)
        if ch == '\\':
            out.append('\\\\')
        elif ch == '"':
            out.append('\\"')
        elif ch == '\n':
            out.append('\\n')
        elif ch == '\t':
            out.append('\\t')
        elif ch == '\r':
            out.append('\\r')
        elif o < 32 or o == 127:
            out.append('\\%03d' % o)
        else:
            out.append(ch)
    out.append('"')
    return ''.join(out)


def lua_value(v):
    """Strings that are canonical integers become numbers (the engine turns them back into the same string)."""
    if isinstance(v, bool):
        return 'true' if v else 'false'
    if isinstance(v, (int, float)):
        v = str(int(v)) if float(v).is_integer() else repr(v)
        return v if CANON_INT.match(v) else lua_str(v)
    s = str(v)
    return s if CANON_INT.match(s) else lua_str(s)


def lua_key(k):
    return k if IDENT.match(k) and k not in LUA_KEYWORDS else '[%s]' % lua_str(k)


def comment_text(s):
    return ' '.join(str(s).split())


# ------------------------------------------------------------------------------------------------ one mission
class Converter:
    def __init__(self, doc):
        self.doc = doc
        self.refs = doc.get('refs') or {}
        defs = doc.get('defaults') or {}
        self.cdef = defs.get('conditions') or {}
        self.adef = defs.get('actions') or {}
        self.stats = {'triggers': 0, 'conditions': 0, 'actions': 0, 'skipped': 0}

    def ref_note(self, v):
        """Names of the GUIDs a value contains (a GUID or a list of them)."""
        names = []
        for tok in re.split(r'[\s,;|]+', str(v)):
            r = self.refs.get(tok) if GUID.match(tok or '') else None
            if r:
                kind, name = r[0], r[1] if len(r) > 1 else '?'
                extra = ' (by name)' if len(r) > 2 and r[2] == 'by name' else ''
                names.append(('%s %s%s' % (kind, name, extra)) if kind not in ('trigger',) else '-> %s' % name)
        return names

    def params(self, node, defaults):
        d = defaults.get(node['type']) or {}
        out = []
        for k, v in (node.get('p') or {}).items():
            if k in EDITOR_ONLY:
                continue
            if k in d and str(d[k]) == str(v):
                continue
            out.append((k, v))
        return out

    def node(self, node, is_action, indent):
        code = node['type']
        names = ACTION_NAMES if is_action else CONDITION_NAMES
        ctor = names.get(code) or ('Node(%s)' % lua_str(code))
        parts, notes = [], []
        if is_action:
            diff = node.get('difficulty')
            if diff is not None and int(diff) != 1:
                parts.append('difficulty = %s' % lua_str(DIFFICULTY.get(int(diff), str(diff))))
        for k, v in self.params(node, self.adef if is_action else self.cdef):
            if is_action and k == 'difficulty' and str(v) == str(node.get('difficulty')):
                continue          # the base attribute `difficulty` = the action's difficulty, written above
            parts.append('%s = %s' % (lua_key(k), lua_value(v)))
            notes.extend(self.ref_note(v))
        lines = []
        label = ' - '.join(x for x in (comment_text(node.get('name') or ''), comment_text(node.get('note') or '')) if x)
        if label:
            lines.append('%s-- %s' % (indent, label))
        tail = ('  -- ' + '; '.join(dict.fromkeys(notes))) if notes else ''
        one = '%s%s{ %s },' % (indent, ctor, ', '.join(parts)) if parts else '%s%s{},' % (indent, ctor)
        if len(one) <= WRAP:
            lines.append(one + tail)
        else:
            lines.append('%s%s{%s' % (indent, ctor, tail))
            for p in parts:
                lines.append('%s  %s,' % (indent, p))
            lines.append('%s},' % indent)
        return lines

    def trigger(self, t):
        f = t.get('flags') or {}
        L = ['trigger %s {' % lua_str(t.get('name') or '')]
        for line in (t.get('description') or '').splitlines():
            if line.strip():
                L.append('  -- ' + comment_text(line))
        L.append('  guid = %s,' % lua_str(t.get('guid') or ''))
        L.append('  folder = %s,' % lua_str(t.get('folder') or 'Root'))
        flags = [k for k in ('enabled', 'once', 'random', 'by_difficulty', 'node_off') if f.get(k)]
        if flags:
            L.append('  ' + ', '.join('%s = true' % k for k in flags) + ',')
        if (t.get('expression') or '').strip():
            L.append('  expr = %s,' % lua_str(t['expression'].strip()))
        for key, nodes, is_action in (('conditions', t.get('conditions') or [], False), ('actions', t.get('actions') or [], True)):
            if not nodes:
                L.append('  %s = {},' % key)
                continue
            L.append('  %s = {' % key)
            for n in nodes:
                L.extend(self.node(n, is_action, '    '))
            L.append('  },')
            self.stats[key] += len(nodes)
        L.append('}')
        self.stats['triggers'] += 1
        return L

    def triggers_lua(self, title):
        doc = self.doc
        L = ['-- %s - the triggers of the original map, converted 1:1 by tools/prepare/campaign_to_lua.py.' % comment_text(title),
             '-- Syntax and every condition / action type: docs/LUA_MAPS.md. GUIDs are kept where the engine needs them;',
             '-- the comments name what they refer to. Own code belongs in custom.lua (this file is rewritten by the converter).',
             '']
        folder, skipped = None, []
        for t in doc.get('triggers') or []:
            f = t.get('flags') or {}
            if t.get('compiled') is False or f.get('disabled'):
                skipped.append(t)
                continue
            if t.get('folder') != folder:
                folder = t.get('folder')
                L.append('-- ' + '=' * 100)
                L.append('-- ' + comment_text(folder or 'Root'))
                L.append('')
            L.extend(self.trigger(t))
            L.append('')
        if skipped:
            self.stats['skipped'] = len(skipped)
            L.append('-- ' + '=' * 100)
            L.append('-- Not compiled (switched off in the editor): these triggers do not exist for the game (triggers.md §1.2).')
            for t in skipped:
                L.append('--   %s  [%s]  %d conditions, %d actions' % (comment_text(t.get('name')), comment_text(t.get('folder') or 'Root'),
                                                                  len(t.get('conditions') or []), len(t.get('actions') or [])))
        return '\n'.join(L) + '\n'


def data_map_path(index_map, doc_map):
    """"maps/Base/Cpn_single_001/single_11.ula" (the toolkit's index) -> "Base/Maps/Cpn_single_001/single_11.ula"
    (below the installation's Data folder)."""
    m = re.match(r'^maps/([^/]+)/(.+)$', index_map or '')
    if m:
        return '%s/Maps/%s' % (m.group(1), m.group(2))
    return index_map or (doc_map or {}).get('file', '')


def folder_name(entry):
    return '%02d_%s' % (int(entry['id']), re.sub(r'[^A-Za-z0-9_\-]+', '_', str(entry.get('key') or 'mission')))


def write_json(path, value):
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(value, f, ensure_ascii=False, indent=1)
        f.write('\n')
    os.replace(tmp, path)


def convert_mission(doc, out_dir, entry=None):
    """Writes one mission folder; returns the mission.json content."""
    entry = dict(entry or {})
    m = doc.get('map') or {}
    entry.setdefault('id', 0)
    entry.setdefault('key', os.path.splitext(m.get('file') or 'mission')[0])
    entry.setdefault('title', m.get('title') or m.get('name') or entry['key'])
    entry.setdefault('description', m.get('description') or '')
    human = next((p for p in doc.get('players') or [] if p.get('type') == 'human'), None)
    entry.setdefault('tribe', (human or {}).get('tribe', ''))
    entry.setdefault('point_buy', any((p.get('resources') or {}).get('food') == -1 for p in doc.get('players') or [] if p.get('type') == 'human'))
    entry.setdefault('min_credits', 0)
    os.makedirs(os.path.join(out_dir, 'texts'), exist_ok=True)
    conv = Converter(doc)
    lua = conv.triggers_lua(entry['title'])
    mission = {
        'schema': 'pw-mission/1',
        'id': int(entry['id']),
        'key': entry['key'],
        'folder': os.path.basename(os.path.normpath(out_dir)),
        'name': entry.get('name') or m.get('name') or '',
        'title': entry['title'],
        'description': entry['description'],
        'tribe': entry['tribe'],
        'point_buy': bool(entry['point_buy']),
        'min_credits': int(entry.get('min_credits') or 0),
        'map': data_map_path(entry.get('map'), m),
        'map_source': entry.get('map') or m.get('file', ''),
        'lang': doc.get('lang') or 'uk',
        'map_info': m,
        'players': doc.get('players') or [],
        'counts': dict(doc.get('counts') or {}, lua=conv.stats),
    }
    world = {
        'schema': 'pw-world/1',
        'objects': doc.get('objects') or [],
        'groups': doc.get('groups') or [],
        'question_marks': doc.get('question_marks') or [],
        'regions': doc.get('regions') or [],
        'region_tree': doc.get('region_tree'),
        'quests': doc.get('quests') or [],
        'variables': doc.get('variables') or {},
        'dialogs': doc.get('dialogs') or {},
        'sequences': doc.get('sequences') or {},
        'refs': doc.get('refs') or {},
        'trigger_tree': doc.get('trigger_tree'),
        'trigger_defaults': doc.get('defaults') or {},
        'warnings': doc.get('warnings') or [],
    }
    write_json(os.path.join(out_dir, 'mission.json'), mission)
    write_json(os.path.join(out_dir, 'world.json'), world)
    write_json(os.path.join(out_dir, 'texts', '%s.json' % mission['lang']), doc.get('texts') or {})
    tmp = os.path.join(out_dir, 'triggers.lua.tmp')
    with open(tmp, 'w', encoding='utf-8', newline='\n') as f:
        f.write(lua)
    os.replace(tmp, os.path.join(out_dir, 'triggers.lua'))
    return mission


INDEX_FIELDS = ('id', 'key', 'folder', 'name', 'title', 'description', 'tribe', 'point_buy', 'min_credits', 'map', 'map_source', 'lang')


def convert_folder(json_dir, missions_dir, force=False):
    with open(os.path.join(json_dir, 'index.json'), encoding='utf-8') as f:
        index = json.load(f)
    os.makedirs(missions_dir, exist_ok=True)
    me = os.path.getmtime(os.path.abspath(__file__))
    out, done, kept = [], 0, 0
    for entry in index:
        src = os.path.join(json_dir, entry.get('json') or 'single_%02d.json' % int(entry['id']))
        if not os.path.exists(src):
            print('  missing: %s' % src, file=sys.stderr)
            continue
        folder = folder_name(entry)
        dst = os.path.join(missions_dir, folder)
        lua, mj = os.path.join(dst, 'triggers.lua'), os.path.join(dst, 'mission.json')
        if not force and os.path.exists(lua) and os.path.exists(mj) and os.path.getmtime(lua) >= max(os.path.getmtime(src), me):
            with open(mj, encoding='utf-8') as f:
                mission = json.load(f)
            kept += 1
        else:
            with open(src, encoding='utf-8') as f:
                doc = json.load(f)
            mission = convert_mission(doc, dst, entry)
            done += 1
            c = mission['counts']['lua']
            print('  %-24s %4d triggers, %4d conditions, %4d actions (%d not compiled)' % (folder, c['triggers'], c['conditions'], c['actions'], c['skipped']))
        out.append({k: mission[k] for k in INDEX_FIELDS if k in mission})
    write_json(os.path.join(missions_dir, 'index.json'), out)
    print('%d missions converted, %d up to date -> %s' % (done, kept, missions_dir))
    return out


def main(argv):
    args = [a for a in argv if not a.startswith('--')]
    force = '--force' in argv
    if len(args) != 2:
        print(__doc__)
        return 2
    src, dst = args
    if os.path.isdir(src):
        convert_folder(src, dst, force)
    else:
        with open(src, encoding='utf-8') as f:
            doc = json.load(f)
        m = convert_mission(doc, dst)
        print('%s: %s' % (dst, m['counts']['lua']))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
