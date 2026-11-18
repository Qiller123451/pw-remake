"""The mission texts with the add-ons' own string tables.

pwexport.campaign.TextTable reads the base language folder (Data/locale/<lang>/Texts) and then the mods' master
files (Data/<mod>/Texts/*.lmf). The BoosterPack 1 missions (B41-B43, the SEAS missions 20-23) and MIRAGE's own ones
keep their strings in the add-ons' language folders (Data/BoosterPack1/locale/uk/Texts/boosterpack1.ltf,
Data/MIRAGE/Locale/uk/Texts/cep.ltf ...), which TextTable never opens; their master files hold only the placeholder
"!boosterpack1.ltf load failed!" (what the game shows when the table is missing), so those missions got that text as
title, description and every quest line.

PackTextTable reads the language folders as the game does (Install.locale_dirs: base, the official add-ons, the
loaded mod; a later folder's file replaces the same file of an earlier one), then the master files without their
placeholders. install(C) puts it in place of pwexport.campaign.TextTable for this run (the toolkit is not changed).
"""
import os
import re

VERSION = 'packtexts-1'            # part of the prepare tool's export stamp: a change re-exports the missions
_PLACEHOLDER = re.compile(r'^!\S+\.ltf load failed!$')


def make(C):
    """PackTextTable as a subclass of the toolkit's TextTable (module pwexport.campaign as C)"""

    class PackTextTable(C.TextTable):
        def __init__(self, install, lang='uk'):
            self.lang, self.t, self.seml = lang, {}, {}
            self.files = 0
            if install is None:
                return
            for lg in ([lang, 'uk'] if lang != 'uk' else ['uk']):
                dirs = install.locale_dirs(lg) if hasattr(install, 'locale_dirs') else [install.locale_dir(lg)]
                dirs = [d for d in dirs if d]
                # the game's file replacement: per file name, the last folder that has it
                files = {}
                for d in dirs:
                    for f in sorted(os.listdir(d)):
                        if f.lower().endswith('.ltf') and not f.startswith('._'):
                            files[f.lower()] = os.path.join(d, f)
                for _, p in sorted(files.items()):
                    self._ltf(p)
                quests = {}
                for d in dirs:
                    q = C._ci_join(d, 'Quests')
                    if q:
                        for f in sorted(os.listdir(q)):
                            if f.lower().endswith('.seml') and not f.startswith('._'):
                                quests[f.lower()] = os.path.join(q, f)
                for _, p in sorted(quests.items()):
                    self._seml(p)
            # keys only the master files have (setdefault: the language files above win)
            for mod in install.mods:
                d = C._ci_join(os.path.join(install.data, mod), 'Texts')
                if not d:
                    continue
                for f in sorted(os.listdir(d)):
                    if f.lower().endswith('.lmf') and not f.startswith('._'):
                        self._ltf(os.path.join(d, f), master=True)
            for k in [k for k, v in self.t.items() if _PLACEHOLDER.match(v.strip())]:
                del self.t[k]

    return PackTextTable


def install(C):
    """use PackTextTable for pwexport.campaign's exports in this process"""
    if getattr(C.TextTable, '_pack_texts', False):
        return
    T = make(C)
    T._pack_texts = True
    C.TextTable = T
