"""Tests of tools/prepare/fog_textures.py with small textures written here (test data only: the game's come from the
installation)."""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import fog_textures  # noqa: E402


def png(path, size, colour):
    from PIL import Image
    os.makedirs(os.path.dirname(path), exist_ok=True)
    Image.new('RGBA', (size, size), colour).save(path, 'TGA' if path.lower().endswith('.tga') else 'PNG')


class FogTexturesTest(unittest.TestCase):
    def test_settings_fallback_mod_and_sizes(self):
        with tempfile.TemporaryDirectory() as t:
            data, out = os.path.join(t, 'Data'), os.path.join(t, 'overrides')
            png(os.path.join(data, 'Base/Texture/Scape/Jungle/fog_(0128).tga'), 8, (255, 255, 255, 10))
            png(os.path.join(data, 'Base/Texture/Scape/Jungle/fog_(0256).tga'), 16, (255, 255, 255, 20))
            png(os.path.join(data, 'Base/Texture/Scape/savanna/FOG_(0256).tga'), 16, (255, 255, 255, 30))
            png(os.path.join(data, 'MIRAGE/Texture/Scape/Cave3/fog_(0256).tga'), 16, (255, 255, 255, 40))
            png(os.path.join(data, 'Base/Texture/Effects/fogofwar.tga'), 16, (170, 170, 170, 50))
            png(os.path.join(data, 'Base/Texture/Scape/Jungle/skygradient.tga'), 4, (1, 2, 3, 4))
            done = fog_textures.convert(data, ['Base', 'MIRAGE'], out, log=lambda *_: None)
            self.assertEqual(sorted(done), ['Cave3/fog.png', 'Jungle/fog.png', 'Savanna/fog.png', 'fogofwar.png'])
            from PIL import Image
            im = Image.open(os.path.join(out, 'assets/terrain/Jungle/fog.png'))
            self.assertEqual(im.size, (16, 16))                      # the largest size
            self.assertEqual(im.getpixel((0, 0))[3], 20)
            with open(os.path.join(out, 'assets/terrain/fog.json')) as f:
                index = json.load(f)
            self.assertEqual(index['Cave3/fog.png']['source'], 'MIRAGE/Texture/Scape/Cave3/fog_(0256).tga')
            # unchanged sources: nothing converted again (the PNG left as it is)
            p = os.path.join(out, 'assets/terrain/Jungle/fog.png')
            os.utime(p, (1, 1))
            fog_textures.convert(data, ['Base', 'MIRAGE'], out, log=lambda *_: None)
            self.assertEqual(int(os.stat(p).st_mtime), 1)

    def test_nothing_without_files(self):
        with tempfile.TemporaryDirectory() as t:
            self.assertEqual(fog_textures.convert(os.path.join(t, 'Data'), ['Base'], os.path.join(t, 'o'), log=lambda *_: None), [])
            self.assertFalse(os.path.exists(os.path.join(t, 'o', 'assets', 'terrain', 'fog.json')))


if __name__ == '__main__':
    unittest.main()
