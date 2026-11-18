"""Tests of tools/prepare/setting_textures.py with small PNG files written here (test data only: the game's setting
textures come from the installation), and - when the staged copies of the installation are there - real DDS files."""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import setting_textures as ST  # noqa: E402

try:
    from PIL import Image
except ImportError:                                  # pragma: no cover
    Image = None

STAGED = os.environ.get('PW_INSTALL_DATA', '/mnt/user-data/uploads/Paraworld/Data')


def png(path, size, colour, alpha=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    im = Image.new('RGBA' if alpha is not None else 'RGB', (size, size), colour + ((alpha,) if alpha is not None else ()))
    im.save(path)


@unittest.skipIf(Image is None, 'needs Pillow')
class SettingTexturesTest(unittest.TestCase):
    def tree(self, d):
        b = os.path.join(d, 'Data', 'Base', 'Texture')
        m = os.path.join(d, 'Data', 'MIRAGE', 'Texture')
        png(os.path.join(b, 'Buildings', 'Hu_Lvl_1_a_(0512).png'), 8, (1, 1, 1))                    # the plain one: not a variant
        png(os.path.join(b, 'Buildings', 'settings', 'icewaste', 'Hu_Lvl_1_a_(0256).png'), 4, (10, 0, 0))
        png(os.path.join(b, 'Buildings', 'settings', 'icewaste', 'Hu_Lvl_1_a_(0512).png'), 8, (20, 0, 0), alpha=0)
        png(os.path.join(b, 'Buildings', 'settings', 'savanna', 'Aje_Lvl_1_a_(0512).png'), 8, (30, 0, 0))
        png(os.path.join(b, 'Buildings', 'settings', 'Jungle', 'Hu_Lvl_1_a_(0128).png'), 2, (40, 0, 0))
        # MIRAGE replaces Base's (even with a smaller size), adds cave3 and a texture outside Buildings
        png(os.path.join(m, 'Buildings', 'settings', 'savanna', 'Aje_Lvl_1_a_(0256).png'), 4, (50, 0, 0))
        png(os.path.join(m, 'Buildings', 'settings', 'cave3', 'Seas_Building_a_(1024).png'), 16, (60, 0, 0))
        png(os.path.join(m, 'settings', 'icewaste', 'boosterpack_01_a_(1024).png'), 16, (70, 0, 0))
        return os.path.join(d, 'Data')

    def test_find_and_override(self):
        with tempfile.TemporaryDirectory() as d:
            data = self.tree(d)
            base = ST.find_variants(data, ['Base', 'BoosterPack1'])
            self.assertEqual(sorted(base), ['buildings/aje_lvl_1_a', 'buildings/hu_lvl_1_a'])
            self.assertEqual(sorted(base['buildings/hu_lvl_1_a']), ['icewaste', 'jungle'])
            self.assertEqual(base['buildings/hu_lvl_1_a']['icewaste']['size'], 512)               # the largest
            self.assertEqual(base['buildings/aje_lvl_1_a']['savanna']['source'], 'Base/Texture/Buildings/settings/savanna/Aje_Lvl_1_a_(0512).png')
            mir = ST.find_variants(data, ['Base', 'BoosterPack3', 'MIRAGE'])
            self.assertEqual(mir['buildings/aje_lvl_1_a']['savanna']['source'], 'MIRAGE/Texture/Buildings/settings/savanna/Aje_Lvl_1_a_(0256).png')
            self.assertIn('cave3', mir['buildings/seas_building_a'])
            self.assertIn('icewaste', mir['boosterpack_01_a'])
            self.assertEqual(mir['buildings/hu_lvl_1_a']['icewaste']['size'], 512)                # Base's where MIRAGE has none

    def test_convert_index_incremental(self):
        with tempfile.TemporaryDirectory() as d:
            data = self.tree(d)
            ov = os.path.join(d, 'overrides')
            logs = []
            doc = ST.convert(data, ['Base', 'BoosterPack3', 'MIRAGE'], ov, log=logs.append)
            self.assertEqual(doc['format'], 'pw-setting-textures')
            self.assertEqual(doc['settings'], ['cave3', 'icewaste', 'jungle', 'savanna'])
            e = doc['textures']['buildings/hu_lvl_1_a']['icewaste']
            self.assertEqual(e['file'], 'settings/icewaste/hu_lvl_1_a.png')
            out = os.path.join(ov, 'assets', 'tex', 'settings', 'icewaste', 'hu_lvl_1_a.png')
            im = Image.open(out)
            self.assertEqual((im.size, im.mode), ((8, 8), 'RGBA'))                                 # alpha kept
            self.assertEqual(Image.open(os.path.join(ov, 'assets', 'tex', 'settings', 'savanna', 'aje_lvl_1_a.png')).getpixel((0, 0))[:3], (50, 0, 0))
            self.assertEqual(Image.open(os.path.join(ov, 'assets', 'tex', 'settings', 'jungle', 'hu_lvl_1_a.png')).mode, 'RGB')
            self.assertEqual(doc['textures']['boosterpack_01_a']['icewaste']['file'], 'settings/icewaste/boosterpack_01_a.png')
            with open(os.path.join(ov, 'assets', 'tex', 'settings', 'index.json'), encoding='utf-8') as f:
                self.assertEqual(json.load(f)['textures'], doc['textures'])
            self.assertIn('5 variants of 4 textures', logs[-1])
            # again: nothing converted; a changed source: that one only
            t0 = os.path.getmtime(out)
            ST.convert(data, ['Base', 'BoosterPack3', 'MIRAGE'], ov, log=logs.append)
            self.assertIn('0 converted, 5 unchanged', logs[-1])
            self.assertEqual(os.path.getmtime(out), t0)
            png(os.path.join(data, 'MIRAGE', 'Texture', 'settings', 'icewaste', 'boosterpack_01_a_(1024).png'), 4, (90, 0, 0))
            os.utime(os.path.join(data, 'MIRAGE', 'Texture', 'settings', 'icewaste', 'boosterpack_01_a_(1024).png'), (1, 1))
            ST.convert(data, ['Base', 'BoosterPack3', 'MIRAGE'], ov, log=logs.append)
            self.assertIn('1 converted, 4 unchanged', logs[-1])
            # the base configuration: no MIRAGE files; the MIRAGE-only outputs of an earlier run go
            doc = ST.convert(data, ['Base', 'BoosterPack1'], ov, log=logs.append)
            self.assertEqual(sorted(doc['textures']), ['buildings/aje_lvl_1_a', 'buildings/hu_lvl_1_a'])
            self.assertFalse(os.path.exists(os.path.join(ov, 'assets', 'tex', 'settings', 'cave3', 'seas_building_a.png')))

    def test_none(self):
        with tempfile.TemporaryDirectory() as d:
            os.makedirs(os.path.join(d, 'Data', 'Base', 'Texture'))
            doc = ST.convert(os.path.join(d, 'Data'), ['Base'], os.path.join(d, 'ov'), log=lambda *_: None)
            self.assertEqual((doc['textures'], doc['settings']), ({}, []))

    @unittest.skipUnless(os.path.isdir(os.path.join(STAGED, 'Base', 'Texture', 'Buildings', 'settings')), 'no staged installation')
    def test_real_dds(self):
        with tempfile.TemporaryDirectory() as d:
            doc = ST.convert(STAGED, ['Base', 'BoosterPack1'], d, log=lambda *_: None)
            e = doc['textures']['buildings/hu_lvl_1_a']['icewaste']
            self.assertGreaterEqual(e['size'], 512)
            im = Image.open(os.path.join(d, 'assets', 'tex', *e['file'].split('/')))
            self.assertEqual(im.mode, 'RGBA')                                                      # the atlas's cut-outs
            self.assertEqual(im.size, (e['size'], e['size']))


if __name__ == '__main__':
    unittest.main()
