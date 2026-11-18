"""The cutscene actors' look at the start of a scene, which pwexport.campaign.seq_play leaves out.

The toolkit's seq_play writes each actor's attachments as [slot, model] (the actor state's models on link slots,
docs/SEQUENCE_FORMAT.md §8) and drops two things the remake needs to draw a scene as the original:

* the clip of an attachment (the state's `extra`): the riders sit with "ride_idle_0" on Ride (mammoths, rhinos,
  stegosaurs, mission 2's ankylosaurus - without it the rider stood upright on its back), the flamers' "weapon_off";
  written as a third element: [slot, model, clip];
* the actor block's embedded ItemLink (+0x318, pwexport.seq `links`): the links between actors at the scene's start,
  op 2 = attached. Mission 2's sc_1082 hangs the transport turtle's shell (aje_transport_turtle_shell_1_tmp) on the
  turtle (Macrolemys_Land_0) at "con" - without it the Dustriders' turtle came as a plain giant turtle and its shell
  stood alone by the shore; also riders and gunners (sc_0416, sc_4080, sc_7040), the Brachiosaurus' catapult
  (sb4103_aje), the carrier's turret (sb4201). Written as `links`: [[slot, guid of the other actor], ...].

install(C) wraps pwexport.campaign.seq_play for this run (the toolkit is not changed); VERSION is part of the
prepare tool's export stamp, so the missions are exported again once.
"""

VERSION = 'seqactors-1'


def actor_extras(a, ac):
    """add the attachments' clips and the start links of one decoded actor `a` (pwexport.seq) to its play entry `ac`"""
    st = a.get('state') or {}
    ac['attach'] = [[x['slot'], x['model']] + ([x['extra']] if x.get('extra') else [])
                    for x in st.get('attachments') or [] if x.get('model')]
    links = [[l.get('slot'), l.get('object')] for l in ((a.get('links') or {}).get('links') or [])
             if l.get('op') == 2 and l.get('object')]
    if links:
        ac['links'] = links
    return ac


def install(C):
    """use the extended seq_play for pwexport.campaign's exports in this process"""
    if getattr(C.seq_play, '_seq_actors', False):
        return
    orig = C.seq_play

    def seq_play(doc):
        out = orig(doc)
        for a, ac in zip(doc.get('actors') or [], out.get('actors') or []):
            actor_extras(a, ac)
        return out

    seq_play._seq_actors = True
    C.seq_play = seq_play
