"""tools/prepare/sequel_art.py: the sequel's built art copied into the overrides where the data lacks it."""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import sequel_art  # noqa: E402


def write(path, data=b'x'):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(data)


class SequelArtTest(unittest.TestCase):
    def test_copies_what_the_data_lacks(self):
        with tempfile.TemporaryDirectory() as d:
            built = os.path.join(d, 'tk', 'remake', 'art', 'sequel', 'built')
            write(os.path.join(built, 'manifest.json'), json.dumps({'models': {'sq_a': {'arch': 'sequel', 'sha': '1'}, 'sq_b': {'arch': 'sequel'}}}).encode())
            write(os.path.join(built, 'models', 'sq_a.glb'))
            write(os.path.join(built, 'models', 'sq_b.glb'))
            write(os.path.join(built, 'tex', 'sq_a.jpg'))
            write(os.path.join(built, 'tex', 'sq_road_city.png'))
            data = os.path.join(d, 'data')
            write(os.path.join(data, 'assets', 'manifest.json'), json.dumps({'models': {'sq_a': {}}}).encode())
            write(os.path.join(data, 'assets', 'models', 'sq_a.glb'))
            write(os.path.join(data, 'assets', 'tex', 'sq_a.jpg'))
            ov = os.path.join(d, 'ov')
            self.assertEqual(sequel_art.built_dir(os.path.join(d, 'tk')), built)
            m, t = sequel_art.install(built, data, ov, log=lambda *a: None)
            self.assertEqual((m, t), (1, 1))
            self.assertTrue(os.path.isfile(os.path.join(ov, 'assets', 'models', 'sq_b.glb')))
            self.assertFalse(os.path.exists(os.path.join(ov, 'assets', 'models', 'sq_a.glb')))
            self.assertTrue(os.path.isfile(os.path.join(ov, 'assets', 'tex', 'sq_road_city.png')))
            man = json.load(open(os.path.join(ov, 'assets', 'manifest_sequel.json')))
            self.assertEqual(list(man['models']), ['sq_b'])
            # again: nothing new
            self.assertEqual(sequel_art.install(built, data, ov, log=lambda *a: None), (0, 0))


if __name__ == '__main__':
    unittest.main()
