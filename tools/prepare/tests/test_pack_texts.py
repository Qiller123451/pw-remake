"""Tests of tools/prepare/pack_texts.py: the add-ons' language folders are read, a later folder's file replaces the
same file of an earlier one, and the master files' "!x.ltf load failed!" placeholders never become texts. Uses a
stand-in for pwexport.campaign (the toolkit is not needed)."""
import os
import re
import sys
import tempfile
import types
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import pack_texts  # noqa: E402


def fake_campaign():
    """the parts of pwexport.campaign that PackTextTable uses (TextTable._ltf / _seml as in the toolkit)"""
    C = types.ModuleType('fake_campaign')

    def _ci_join(base, rel):
        p = os.path.join(base, rel)
        return p if os.path.exists(p) else None

    class TextTable:
        def __init__(self, install, lang='uk'):
            self.t, self.seml = {}, {}

        def _ltf(self, path, master=False):
            with open(path, encoding='utf-8') as fh:
                for line in fh.read().splitlines():
                    m = re.match(r'\s*"([^"]*)";"([^"]*)"', line)
                    if m and not (master and m.group(2) in ('!DUMMY!', '')):
                        self.t.setdefault(m.group(1), m.group(2))

        def _seml(self, path):
            self.seml[os.path.basename(path)] = path

        def get(self, k, d=None):
            return self.t.get(k, d)

    C._ci_join, C.TextTable = _ci_join, TextTable
    return C


def write(path, lines):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))


class PackTextsTest(unittest.TestCase):
    def test_addon_tables_and_placeholders(self):
        with tempfile.TemporaryDirectory() as root:
            data = os.path.join(root, 'Data')
            base = os.path.join(data, 'locale', 'uk', 'Texts')
            bp1 = os.path.join(data, 'BoosterPack1', 'locale', 'uk', 'Texts')
            write(os.path.join(base, 'main_uk.ltf'), ['"_a";"Base A"', '"_b";"Base B"'])
            write(os.path.join(base, 'shared.ltf'), ['"_s";"old"'])
            write(os.path.join(bp1, 'shared.ltf'), ['"_s";"new"'])               # replaces the base file
            write(os.path.join(bp1, 'boosterpack1.ltf'), ['"Single B41";"Mission 4-1"'])
            write(os.path.join(bp1, 'Quests', 'single_b41.seml'), ['x'])
            write(os.path.join(data, 'BoosterPack1', 'Texts', 'boosterpack1.lmf'),
                  ['"Single B41";"!boosterpack1.ltf load failed!"', '"_LB41X";"!boosterpack1.ltf load failed!"',
                   '"_only_master";"From master"'])
            inst = types.SimpleNamespace(data=data, mods=['BoosterPack1'], locale_dirs=lambda lg: [base, bp1])
            C = fake_campaign()
            pack_texts.install(C)
            t = C.TextTable(inst, 'uk')
            self.assertEqual(t.get('Single B41'), 'Mission 4-1')
            self.assertEqual(t.get('_a'), 'Base A')
            self.assertEqual(t.get('_s'), 'new')
            self.assertEqual(t.get('_only_master'), 'From master')
            self.assertIsNone(t.get('_LB41X'))                                    # a placeholder is no text
            self.assertIn('single_b41.seml', t.seml)


if __name__ == '__main__':
    unittest.main()
