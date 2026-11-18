"""Tests of tools/prepare/campaign_to_lua.py (python3 -m unittest discover tools/prepare/tests). A small synthetic
pw-campaign/1 document; the round trip over the real missions (convert, load in the Lua sandbox, compare with the
JSON) is tests/PwRemake.Tests/TriggersRoundTripTests.cs."""
import json
import os
import sys
import tempfile
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import campaign_to_lua as C  # noqa: E402

G1, G2, Q1 = 'a' * 32, 'b' * 32, 'c' * 32


def doc():
    return {
        'schema': 'pw-campaign/1', 'lang': 'uk',
        'map': {'name': 'Single 99', 'file': 'single_99.ula', 'title': 'Mission 99: Test', 'description': 'Desc'},
        'players': [{'id': 0, 'present': True, 'type': 'human', 'tribe': 'Ninigi', 'resources': {'food': -1}}],
        'objects': [{'guid': G2, 'name': 'Cole_s0_1', 'class': 'Cole_s0', 'type': 'CHTR', 'owner': 0, 'x': 1, 'y': 2}],
        'groups': [], 'question_marks': [], 'regions': [], 'region_tree': None, 'quests': [{'guid': Q1, 'name': 'MQ1'}],
        'variables': {'n': {'type': 'int', 'value': '0'}}, 'trigger_tree': None,
        'defaults': {'conditions': {'TIME': {'duration': '0', 'reset': '1'}}, 'actions': {'TRIG': {'difficulty': '1', 'renderable': '0'}, 'VARS': {'difficulty': '1'}}},
        'texts': {'_x': 'X'}, 'dialogs': {}, 'sequences': {},
        'refs': {G1: ['trigger', 'second'], G2: ['CHTR', 'Cole_s0_1'], Q1: ['quest', 'MQ1']},
        'counts': {}, 'warnings': [],
        'triggers': [
            {'guid': 'g' * 32, 'name': 'first "one"', 'description': 'line 1\nline 2', 'folder': 'Root/A', 'compiled': True, 'expression': ' 1 && 2 ',
             'flags': {'once': True, 'enabled': True, 'by_difficulty': True},
             'conditions': [{'type': 'TIME', 'note': 'one sec', 'name': '', 'p': {'duration': '1', 'reset': '1'}},
                            {'type': 'DEAD', 'note': '', 'name': '', 'p': {'obj_name': 'Cole_s0_1', 'obj_guid': G2}}],
             'actions': [{'type': 'TRIG', 'note': '', 'name': '', 'difficulty': 0, 'p': {'guid': G1, 'state': '1', 'difficulty': '0', 'renderable': '1'}},
                         {'type': 'VARS', 'note': '', 'name': '', 'difficulty': 1, 'p': {'varname': 'n', 'value': '1.50', 'text': 'a\tb\n"c"\\'}},
                         {'type': 'ZZZZ', 'note': '', 'name': '', 'difficulty': 1, 'p': {'key with space': '007'}}]},
            {'guid': G1, 'name': 'second', 'description': '', 'folder': 'Root/A', 'compiled': True, 'expression': '',
             'flags': {}, 'conditions': [], 'actions': []},
            {'guid': 'h' * 32, 'name': 'switched off', 'description': '', 'folder': 'Root/B', 'compiled': False, 'expression': '',
             'flags': {'disabled': True}, 'conditions': [], 'actions': []},
        ],
    }


class LuaText(unittest.TestCase):
    def test_strings(self):
        self.assertEqual(C.lua_str('a"b\\c\n\t\x01é§'), '"a\\"b\\\\c\\n\\t\\001é§"')

    def test_values(self):
        self.assertEqual(C.lua_value('12'), '12')
        self.assertEqual(C.lua_value('-3'), '-3')
        for s in ('007', '1.50', '-0', '1e5', '', ' 1'):
            self.assertEqual(C.lua_value(s), C.lua_str(s), s)

    def test_keys(self):
        self.assertEqual(C.lua_key('obj_name'), 'obj_name')
        self.assertEqual(C.lua_key('end'), '["end"]')
        self.assertEqual(C.lua_key('a b'), '["a b"]')

    def test_map_path(self):
        self.assertEqual(C.data_map_path('maps/Base/Cpn_single_001/single_11.ula', {}), 'Base/Maps/Cpn_single_001/single_11.ula')
        self.assertEqual(C.folder_name({'id': 0, 'key': 'tutorial'}), '00_tutorial')


class Convert(unittest.TestCase):
    def test_mission(self):
        with tempfile.TemporaryDirectory() as d:
            out = os.path.join(d, '99_x')
            m = C.convert_mission(doc(), out, {'id': 99, 'key': 'x', 'map': 'maps/Base/C/single_99.ula'})
            lua = open(os.path.join(out, 'triggers.lua'), encoding='utf-8').read()
            self.assertEqual(m['counts']['lua'], {'triggers': 2, 'conditions': 2, 'actions': 3, 'skipped': 1})
            self.assertEqual((m['tribe'], m['point_buy'], m['map'], m['folder']), ('Ninigi', True, 'Base/Maps/C/single_99.ula', '99_x'))
            self.assertIn('trigger "first \\"one\\"" {', lua)
            self.assertIn('  -- line 1\n  -- line 2\n', lua)
            self.assertIn('enabled = true, once = true, by_difficulty = true,', lua)
            self.assertIn('expr = "1 && 2",', lua)
            self.assertIn('    -- one sec\n    Time{ duration = 1 },', lua)              # reset = 1 is the default: dropped
            self.assertIn('Dead{ obj_name = "Cole_s0_1", obj_guid = "%s" },  -- CHTR Cole_s0_1' % G2, lua)
            self.assertIn('SetTrigger{ difficulty = "easy", guid = "%s", state = 1 },  -- -> second' % G1, lua)
            self.assertNotIn('renderable', lua)
            self.assertIn('SetVar{ varname = "n", value = "1.50", text = "a\\tb\\n\\"c\\"\\\\" },', lua)
            self.assertIn('Node("ZZZZ"){ ["key with space"] = "007" },', lua)
            self.assertIn('  conditions = {},\n  actions = {},', lua)
            self.assertIn('--   switched off  [Root/B]  0 conditions, 0 actions', lua)
            self.assertNotIn('trigger "switched off"', lua)
            world = json.load(open(os.path.join(out, 'world.json'), encoding='utf-8'))
            self.assertEqual(world['trigger_defaults']['conditions']['TIME']['reset'], '1')
            self.assertEqual(world['quests'][0]['name'], 'MQ1')
            self.assertEqual(json.load(open(os.path.join(out, 'texts', 'uk.json'), encoding='utf-8')), {'_x': 'X'})

    def test_folder_and_skip(self):
        with tempfile.TemporaryDirectory() as d:
            src, dst = os.path.join(d, 'json'), os.path.join(d, 'missions')
            os.makedirs(src)
            json.dump([{'id': 99, 'key': 'single_99', 'map': 'maps/Base/C/single_99.ula', 'title': 'T', 'description': 'D', 'tribe': 'Hu',
                        'point_buy': False, 'min_credits': 5}], open(os.path.join(src, 'index.json'), 'w'))
            json.dump(doc(), open(os.path.join(src, 'single_99.json'), 'w'))
            idx = C.convert_folder(src, dst)
            self.assertEqual(idx[0]['folder'], '99_single_99')
            self.assertEqual((idx[0]['title'], idx[0]['min_credits'], idx[0]['tribe']), ('T', 5, 'Hu'))
            lua = os.path.join(dst, '99_single_99', 'triggers.lua')
            t0 = os.path.getmtime(lua)
            time.sleep(0.05)
            C.convert_folder(src, dst)
            self.assertEqual(os.path.getmtime(lua), t0, 'up to date: not written again')
            C.convert_folder(src, dst, force=True)
            self.assertNotEqual(os.path.getmtime(lua), t0, '--force converts again')
            self.assertEqual(json.load(open(os.path.join(dst, 'index.json')))[0]['key'], 'single_99')


if __name__ == '__main__':
    unittest.main()
