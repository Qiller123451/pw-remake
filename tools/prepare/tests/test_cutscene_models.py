"""Tests of tools/prepare/cutscene_models.py with a stand-in converter (no game files needed)."""
import json
import os
import sys
import tempfile
import types
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import cutscene_models as M  # noqa: E402
from test_effect_models import glb  # noqa: E402

IN = {'all_characters': {'ns1050_stina_eusmilus'}, 'all_animals': {'eusmilus'}, 'hu_buildings': set()}


class FakeArchive:
    def __init__(self, path):
        self.arch = os.path.splitext(os.path.basename(path))[0]

    def index(self, name):
        return 0 if name in IN.get(self.arch, set()) else None

    def export(self, name, out, data_dir=None, log=print):
        with open(out, 'wb') as f:
            f.write(glb({'nodes': [{'name': name}], 'animations': [{'name': 'NS1050_anim'}]}))


def actor(cls, typ='DCCO'):
    return {'cls': cls, 'type': typ, 'bind': {'cls': cls, 'owner': 0}}


class CutsceneModelsTest(unittest.TestCase):
    def test_convert(self):
        with tempfile.TemporaryDirectory() as root:
            data = os.path.join(root, 'data')
            os.makedirs(os.path.join(data, 'assets'))
            with open(os.path.join(data, 'assets', 'manifest.json'), 'w') as f:
                json.dump({'models': {'stina_s0': {}, 'cole_s0': {}}}, f)
            # the official game's class files lack the class: the model is the class's name
            with open(os.path.join(data, 'gamedata.json'), 'w') as f:
                json.dump({'classgfx': {'stina_s0': 'stina_s0', 'golem': 'golem_gfx'}}, f)
            m = os.path.join(root, 'missions', '01_single_01')
            os.makedirs(m)
            with open(os.path.join(m, 'world.json'), 'w') as f:
                json.dump({'sequences': {'a/sc_1050.seq': {'id': 'sc_1050', 'play': {'actors': [
                    actor('NS1050_stina_eusmilus'), actor('Cole_s0', 'CHTR'), actor('Stina_s0', 'CHTR'),
                    actor('golem', 'CHTR'), actor('unknown', 0)]}}}}, f)
            need = M.wanted(os.path.join(root, 'missions'), json.load(open(os.path.join(data, 'gamedata.json'))), {'stina_s0', 'cole_s0'})
            self.assertEqual(set(need), {'ns1050_stina_eusmilus', 'golem_gfx'})
            self.assertEqual(need['ns1050_stina_eusmilus'], ['01_single_01:sc_1050'])
            archives = {a: os.path.join(root, a + '.gsf') for a in IN}
            for p in archives.values():
                open(p, 'wb').close()
            gsf = types.SimpleNamespace(Archive=FakeArchive)
            ov = os.path.join(root, 'overrides')
            logs = []
            done = M.convert(gsf, archives, data, os.path.join(root, 'missions'), ov, log=logs.append)
            self.assertEqual(done, ['ns1050_stina_eusmilus'])
            self.assertTrue(any('golem_gfx' in x and 'in no archive' in x for x in logs), logs)
            man = json.load(open(os.path.join(ov, 'assets', 'manifest_fx.json')))
            self.assertEqual(man['models']['ns1050_stina_eusmilus']['arch'], 'all_characters')
            self.assertEqual(man['models']['ns1050_stina_eusmilus']['nanims'], 1)
            self.assertTrue(os.path.isfile(os.path.join(ov, 'assets', 'models', 'ns1050_stina_eusmilus.glb')))
            # the effect models in the same manifest stay; a second run converts nothing again
            man['models']['aje_tornado'] = {'arch': 'aje_products'}
            with open(os.path.join(ov, 'assets', 'manifest_fx.json'), 'w') as f:
                json.dump(man, f)
            t = os.path.getmtime(os.path.join(ov, 'assets', 'models', 'ns1050_stina_eusmilus.glb'))
            self.assertEqual(M.convert(gsf, archives, data, os.path.join(root, 'missions'), ov, log=lambda *a: None), [])
            self.assertEqual(t, os.path.getmtime(os.path.join(ov, 'assets', 'models', 'ns1050_stina_eusmilus.glb')))
            self.assertIn('aje_tornado', json.load(open(os.path.join(ov, 'assets', 'manifest_fx.json')))['models'])


if __name__ == '__main__':
    unittest.main()
