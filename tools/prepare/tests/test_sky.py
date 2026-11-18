"""Tests of tools/prepare/sky.py with small TGA files written here (test data only: the game's tables come from the
installation)."""
import json
import os
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import sky  # noqa: E402


def tga(path, rows, rle=False, top_down=False, bpp=32):
    """rows top-down of (r, g, b, a)"""
    h, w = len(rows), len(rows[0])
    stored = rows if top_down else list(reversed(rows))
    px = bpp // 8
    data = bytearray()
    for row in stored:
        for r, g, b, a in row:
            data += bytes([b, g, r] + ([a] if px == 4 else []))
    if rle:                                          # one raw packet per pixel (valid RLE)
        out = bytearray()
        for i in range(0, len(data), px):
            out += bytes([0]) + data[i:i + px]
        data = out
    head = struct.pack('<BBBHHBHHHHBB', 0, 0, 10 if rle else 2, 0, 0, 0, 0, 0, w, h, bpp, 0x20 if top_down else 0)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(head + data)


ROWS = [[(10, 20, 30, 40), (50, 60, 70, 80)], [(1, 2, 3, 255), (4, 5, 6, 255)], [(200, 100, 0, 255), (255, 255, 255, 255)]]


class SkyTest(unittest.TestCase):
    def test_read_variants(self):
        with tempfile.TemporaryDirectory() as d:
            for i, (rle, td, bpp) in enumerate([(False, False, 32), (True, False, 32), (False, True, 32), (True, True, 24)]):
                p = os.path.join(d, '%d.tga' % i)
                tga(p, ROWS, rle, td, bpp)
                w, h, rows = sky.read_tga(p)
                self.assertEqual((w, h), (2, 3))
                want = ROWS if bpp == 32 else [[c[:3] + (255,) for c in r] for r in ROWS]
                self.assertEqual(rows, want, (rle, td, bpp))

    def test_convert_day_first_and_mod_order(self):
        with tempfile.TemporaryDirectory() as d:
            tga(os.path.join(d, 'Base', 'Texture', 'Scape', 'Northland', 'skygradient.tga'), ROWS)
            tga(os.path.join(d, 'Base', 'Texture', 'Scape', 'Northland', 'skygradient_day.tga'), [[(1, 1, 1, 1)] * 2])
            tga(os.path.join(d, 'MIRAGE', 'texture', 'scape', 'jungle', 'SkyGradient.tga'), ROWS)
            ov = os.path.join(d, 'overrides')
            done = sky.convert(d, ['Base', 'BoosterPack3', 'MIRAGE'], ov, log=lambda *a: None)
            self.assertEqual(sorted(done), ['Jungle', 'Northland'])
            with open(os.path.join(ov, 'assets', 'terrain', 'Northland', 'sky.json')) as f:
                n = json.load(f)
            self.assertTrue(n['source'].endswith('skygradient_day.tga'))          # the client tries "_day" first
            with open(os.path.join(ov, 'assets', 'terrain', 'Jungle', 'sky.json')) as f:
                j = json.load(f)
            self.assertEqual((j['width'], j['height']), (2, 3))
            self.assertEqual(j['rows'][2][0], 'c86400ff')


if __name__ == '__main__':
    unittest.main()
