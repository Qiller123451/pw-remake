"""Tests of tools/prepare/films.py with a synthetic film (skipped without an ffmpeg that has libtheora)."""
import os
import shutil
import subprocess
import sys
import tempfile
import types
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import films  # noqa: E402

FFMPEG = shutil.which('ffmpeg')


@unittest.skipUnless(FFMPEG and films.has_theora(FFMPEG), 'no ffmpeg with libtheora')
class FilmsTest(unittest.TestCase):
    def test_convert_and_keep(self):
        with tempfile.TemporaryDirectory() as root:
            src = os.path.join(root, 'hs_0001.bik')            # any format ffmpeg reads; a film's name
            subprocess.run([FFMPEG, '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc=size=160x90:rate=10:duration=1',
                            '-f', 'lavfi', '-i', 'sine=duration=1', '-c:v', 'mpeg4', '-c:a', 'aac', '-f', 'matroska', src],
                           check=True)
            video = types.SimpleNamespace(films=lambda inst: ['hs_0001.bik', 'hs_0002.bik'],
                                          find=lambda inst, n, lang: src if n == 'hs_0001.bik' else None,
                                          ffmpeg=lambda log=print: FFMPEG)
            ov = os.path.join(root, 'overrides')
            ready, total = films.convert_films(None, video, os.path.join(root, 'cache'), ov, 'uk', log=lambda *a: None)
            self.assertEqual((ready, total), (1, 2))                 # hs_0002 is only a placeholder
            out = os.path.join(ov, 'assets', 'video', 'hs_0001.ogv')
            with open(out, 'rb') as f:
                self.assertEqual(f.read(4), b'OggS')
            t = os.path.getmtime(out)
            films.convert_films(None, video, os.path.join(root, 'cache'), ov, 'uk', log=lambda *a: None)
            self.assertEqual(os.path.getmtime(out), t)               # kept

    def test_start_films(self):
        with tempfile.TemporaryDirectory() as root:
            vids = os.path.join(root, 'Data', 'Base', 'Video')
            mods = os.path.join(root, 'Data', 'MIRAGE', 'Video')
            os.makedirs(vids); os.makedirs(mods)

            def film(path, size):
                subprocess.run([FFMPEG, '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc=size=%s:rate=10:duration=2' % size,
                                '-f', 'lavfi', '-i', 'sine=duration=2', '-c:v', 'mpeg4', '-q:v', '2', '-c:a', 'aac', '-f', 'matroska', path],
                               check=True)
            film(os.path.join(vids, 'sek_logo.bik'), '640x480')
            film(os.path.join(mods, 'SEK_Logo.bik'), '320x240')        # MIRAGE's own replaces Base's (any case)
            film(os.path.join(vids, 'intro.bik'), '160x80')             # an installation's placeholder
            with open(os.path.join(vids, 'credits.bik'), 'wb') as f:      # a stub
                f.write(b'BIKi' + bytes(756))
            video = types.SimpleNamespace(_video_dirs=lambda inst, lang: [mods, vids], ffmpeg=lambda log=print: FFMPEG)
            ov = os.path.join(root, 'overrides')
            ready, total = films.convert_start_films(None, video, os.path.join(root, 'cache'), ov, 'uk', log=lambda *a: None)
            self.assertEqual((ready, total), (1, 4))
            out = os.path.join(ov, 'assets', 'video', 'sek_logo.ogv')
            self.assertEqual(films.film_size(FFMPEG, out), (320, 240))
            self.assertFalse(os.path.exists(os.path.join(ov, 'assets', 'video', 'intro.ogv')))
            self.assertTrue(films.is_placeholder(FFMPEG, os.path.join(vids, 'credits.bik')))


if __name__ == '__main__':
    unittest.main()
