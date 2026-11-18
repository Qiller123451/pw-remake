"""Tests of tools/prepare/effect_models.py with a stand-in converter (no game files needed)."""
import json
import os
import struct
import sys
import tempfile
import types
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import effect_models as E  # noqa: E402


def glb(j, rest=b''):
    js = json.dumps(j).encode()
    js += b' ' * ((4 - len(js) % 4) % 4)
    return struct.pack('<III', 0x46546C67, 2, 20 + len(js) + len(rest)) + struct.pack('<II', len(js), 0x4E4F534A) + js + rest


class FakeArchive:
    """pwexport.gsf.Archive: export writes a model with a texture in <out folder>/textures"""
    def __init__(self, path):
        self.path = path

    def index(self, name):
        return 0 if name in E.MODELS else None

    def export(self, name, out, data_dir=None, log=print):
        d = os.path.join(os.path.dirname(out), 'textures')
        os.makedirs(d, exist_ok=True)
        for t in ('twister.png', 'gore.png'):
            with open(os.path.join(d, t), 'wb') as f:
                f.write(b'png')
        j = {'images': [{'uri': 'textures/twister.png'}, {'uri': 'textures/gore.png'}],
             'materials': [{'name': 'material_65535', 'pbrMetallicRoughness': {}},
                           {'name': 'twister', 'pbrMetallicRoughness': {'baseColorTexture': {'index': 0}}}],
             'meshes': [{'primitives': [{'material': 0}]}, {'primitives': [{'material': 0}, {'material': 1}]}],
             'nodes': [{'name': name, 'extras': {'sounds': {'swirl': [[0.04, 'x.wav', 78, 66.0, '']]}}},
                       {'name': 'helper', 'mesh': 0}, {'name': 'body', 'mesh': 1},
                       {'name': 'particles_0', 'extras': {'kind': 'particles', 'emitter': {'tiles': [{'tex': 'textures/gore.png'}]}}}],
             'animations': [{'name': 'swirl'}]}
        with open(out, 'wb') as f:
            f.write(glb(j, struct.pack('<II', 4, 0x004E4942) + b'\0\0\0\0'))


class EffectModelsTest(unittest.TestCase):
    def test_convert(self):
        with tempfile.TemporaryDirectory() as root:
            data = os.path.join(root, 'data', 'assets')
            os.makedirs(os.path.join(data, 'tex'))
            with open(os.path.join(data, 'manifest.json'), 'w') as f:
                json.dump({'models': {'fx_seas_rpg': {'arch': 'all_effects'}}}, f)     # the export has this one
            with open(os.path.join(data, 'tex', 'gore.png'), 'wb') as f:
                f.write(b'old')
            gsf = types.SimpleNamespace(Archive=FakeArchive)
            archives = {a: os.path.join(root, a + '.gsf') for a in set(E.MODELS.values())}
            for p in archives.values():
                open(p, 'wb').close()
            ov = os.path.join(root, 'overrides')
            done = E.convert(gsf, archives, data, ov, log=lambda *a: None)
            self.assertNotIn('fx_seas_rpg', done)
            self.assertEqual(set(done), set(E.MODELS) - {'fx_seas_rpg'})
            man = json.load(open(os.path.join(ov, 'assets', 'manifest_fx.json')))
            self.assertEqual(man['models']['aje_tornado']['nanims'], 1)
            self.assertIn('sounds', man['models']['aje_tornado'])
            j, _ = E.load_glb(os.path.join(ov, 'assets', 'models', 'aje_tornado.glb'))
            self.assertEqual([im['uri'] for im in j['images']], ['../tex/twister.png', '../tex/gore.png'])
            self.assertEqual(j['nodes'][3]['extras']['emitter']['tiles'][0]['tex'], '../tex/gore.png')
            self.assertNotIn('mesh', j['nodes'][1])                                 # all of material 65535: gone
            self.assertEqual(j['meshes'][1]['primitives'], [{'material': 1}])
            self.assertTrue(os.path.isfile(os.path.join(ov, 'assets', 'tex', 'twister.png')))
            self.assertFalse(os.path.exists(os.path.join(ov, 'assets', 'tex', 'gore.png')))  # the data has it
            # a second run keeps what is there
            t = os.path.getmtime(os.path.join(ov, 'assets', 'models', 'aje_tornado.glb'))
            E.convert(gsf, archives, data, ov, log=lambda *a: None)
            self.assertEqual(t, os.path.getmtime(os.path.join(ov, 'assets', 'models', 'aje_tornado.glb')))


if __name__ == '__main__':
    unittest.main()
