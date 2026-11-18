"""Tests of tools/prepare/seq_actors.py: the attachments' clips and the start links between actors are added to the
toolkit's seq_play output (mission 2's sc_1082: the turtle's shell at "con", the ankylosaurus' rider with
"ride_idle_0"). Uses a stand-in for pwexport.campaign (the toolkit is not needed)."""
import os
import sys
import types
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import seq_actors  # noqa: E402

SHELL = 'dklgdgkhkidkknbbolckaabbjaoiboeo'


def decoded():
    """two actors of sc_1082 as pwexport.seq decodes them (the fields seq_actors reads)"""
    turtle = {'name': 'Macrolemys_Land_0', 'state': {'attachments': []},
              'links': {'links': [{'op': 2, 'slot': 'con', 'object': SHELL}, {'op': 1, 'slot': 'HndR', 'object': ''}]}}
    anky = {'name': 'aje_ankylosaurus_11', 'links': {'links': []},
            'state': {'attachments': [{'op': 1, 'slot': 'Ride', 'model': 'aje_rider_a', 'extra': 'ride_idle_0'},
                                      {'op': 1, 'slot': 0, 'model': 'dummy', 'extra': ''},
                                      {'op': 1, 'slot': 'HndL', 'model': '', 'extra': ''}]}}
    return {'actors': [turtle, anky]}


def fake_campaign():
    C = types.ModuleType('fake_campaign')

    def seq_play(doc):        # the toolkit's: [slot, model] only
        return {'actors': [{'name': a['name'], 'attach': [[x['slot'], x['model']] for x in a['state']['attachments'] if x.get('model')]}
                           for a in doc['actors']]}
    C.seq_play = seq_play
    return C


class SeqActorsTest(unittest.TestCase):
    def test_clips_and_links(self):
        C = fake_campaign()
        seq_actors.install(C)
        seq_actors.install(C)                     # (once)
        out = C.seq_play(decoded())
        turtle, anky = out['actors']
        self.assertEqual(turtle['links'], [['con', SHELL]])           # op 2 only
        self.assertEqual(turtle['attach'], [])
        self.assertEqual(anky['attach'], [['Ride', 'aje_rider_a', 'ride_idle_0'], [0, 'dummy']])
        self.assertNotIn('links', anky)


if __name__ == '__main__':
    unittest.main()
