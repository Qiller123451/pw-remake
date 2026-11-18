"""The campaign's story films for Godot: the original's Bink videos (hs_*.bik, ns_*.bik) -> Ogg Theora + Vorbis
(.ogv), the only video format Godot plays (VideoStreamTheora). The toolkit converts the films to WebM for the
browser; Godot cannot play those.

Which films and where they are, and which ffmpeg (PATH, else the pip package imageio-ffmpeg), come from the toolkit
(pwexport.video). A film is converted once into content/cache/films/<lang>/ and copied into each configuration's
overrides/assets/video/<name>.ogv, where the game looks for it first (AssetSource, MissionPresenter.FilmPath).
Existing files are kept; --force converts again. Without ffmpeg (or one without libtheora) the step is skipped
with a message: the missions then show a card with the film's lines instead (docs/MISSION_UI.md).

The start films (convert_start_films): the logos and the intro the client plays at start-up, in its order
(PWClient.exe 0x456abf..0x456b09: sf_logo, sek_logo, partner, intro; the game's IntroScreen). They are looked up like
the story films - the language's video folder first, then the configuration's Data folders from the last one (MIRAGE
replaces a film of Data/Base by its own of the same name: MIRAGE.info "tryfilereplace data/base data/MIRAGE") - but
without the story films' 1 MB minimum (a logo can be smaller); only stubs (credits.bik of many installations is 760
bytes) and the installations' 160 x 80 placeholders are skipped. credits.bik is not a start film: the main menu's
Credits button plays it (MainMenu.usl OnCredits); there is no credits text in the game's data to show instead.
"""
import os
import re
import shutil
import subprocess

# the client's start films in the order it plays them (sf_logo / sek_logo / partner when Global/PlayIntro >= 0,
# intro when it is >= 1)
START_FILMS = ('sf_logo.bik', 'sek_logo.bik', 'partner.bik', 'intro.bik')
STUB = 16 << 10                 # bytes: smaller .bik files are stubs (credits.bik: 760 bytes)
PLACEHOLDER_SIZE = (160, 80)    # the installations' dummy films (pwexport.video: 160 x 80, 4 seconds)

QUALITY = ['-c:v', 'libtheora', '-q:v', '7', '-c:a', 'libvorbis', '-q:a', '5']


def encode(exe, src, dst, log):
    """one film -> dst (.ogv). True when done."""
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    tmp = dst + '.tmp.ogv'
    cmd = [exe, '-hide_banner', '-loglevel', 'error', '-y', '-i', src] + QUALITY + [tmp]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode or not os.path.isfile(tmp) or os.path.getsize(tmp) == 0:
        log('    %s: %s' % (os.path.basename(src), (r.stderr or 'no output').strip()[-400:]))
        try:
            os.remove(tmp)
        except OSError:
            pass
        return False
    os.replace(tmp, dst)
    return True


def has_theora(exe):
    try:
        r = subprocess.run([exe, '-hide_banner', '-encoders'], capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.SubprocessError):
        return False
    return 'libtheora' in r.stdout and 'libvorbis' in r.stdout


def convert_films(inst, video, cache_dir, overrides_dir, lang, force=False, log=print):
    """every story film of the installation `inst` (pwexport Install / ModInstall; `video` = pwexport.video) ->
    <overrides_dir>/assets/video/<name>.ogv. Returns (films ready, films the installation has)."""
    names = video.films(inst)
    if not names:
        return 0, 0
    exe = video.ffmpeg(log)
    if not exe:
        log('  films: no ffmpeg found (put ffmpeg on the PATH, or: python -m pip install --user imageio-ffmpeg) - '
            'the films are skipped, missions show their lines instead')
        return 0, len(names)
    if not has_theora(exe):
        log('  films: %s has no Theora encoder (libtheora / libvorbis) - the films are skipped' % exe)
        return 0, len(names)
    out_dir = os.path.join(overrides_dir, 'assets', 'video')
    ready = 0
    for i, n in enumerate(names):
        stem = os.path.splitext(n)[0].lower()
        dst = os.path.join(out_dir, stem + '.ogv')
        if os.path.isfile(dst) and not force:
            ready += 1
            continue
        src = video.find(inst, n, lang)
        if not src:
            continue                                 # only the installation's placeholder (see pwexport.video)
        st = os.stat(src)
        cached = os.path.join(cache_dir, '%s_%d.ogv' % (stem, st.st_size))
        if force or not os.path.isfile(cached):
            log('  film %d/%d %s' % (i + 1, len(names), n))
            if not encode(exe, src, cached, log):
                continue
        os.makedirs(out_dir, exist_ok=True)
        shutil.copyfile(cached, dst)
        ready += 1
    return ready, len(names)


def film_size(exe, path):
    """(width, height) of a film's picture as ffmpeg reads it, or None"""
    try:
        r = subprocess.run([exe, '-hide_banner', '-i', path], capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.SubprocessError):
        return None
    m = re.search(r'Video:.*?\b(\d{2,5})x(\d{2,5})\b', r.stderr or '')
    return (int(m.group(1)), int(m.group(2))) if m else None


def is_placeholder(exe, path):
    """a stub or one of the installations' 160 x 80 dummy films"""
    try:
        if os.path.getsize(path) < STUB:
            return True
    except OSError:
        return True
    size = film_size(exe, path)
    return size is not None and size[0] <= PLACEHOLDER_SIZE[0] and size[1] <= PLACEHOLDER_SIZE[1]


def find_start(video, inst, name, lang, exe):
    """the Bink file of a start film: the first real one of the video folders, best first (pwexport.video's order:
    the language's folder, the mods' language folders and Video folders from the last mod, other languages)"""
    from_dirs = getattr(video, '_video_dirs', None)
    dirs = from_dirs(inst, lang) if from_dirs else []
    for d in dirs:
        p = os.path.join(d, name)
        if not os.path.isfile(p):
            hit = next((e for e in os.listdir(d) if e.lower() == name.lower()), None)
            p = os.path.join(d, hit) if hit else None
        if p and os.path.isfile(p) and not is_placeholder(exe, p):
            return p
    return None


def convert_start_films(inst, video, cache_dir, overrides_dir, lang, force=False, log=print, find=None):
    """the start films of the installation -> <overrides_dir>/assets/video/<name>.ogv (IntroScreen). Returns
    (films ready, films the client plays). find(name) -> path overrides the lookup (tests)."""
    exe = video.ffmpeg(log)
    if not exe or not has_theora(exe):
        log('  start films: no ffmpeg with Theora - skipped')
        return 0, len(START_FILMS)
    out_dir = os.path.join(overrides_dir, 'assets', 'video')
    ready = 0
    for n in START_FILMS:
        stem = os.path.splitext(n)[0]
        dst = os.path.join(out_dir, stem + '.ogv')
        src = find(n) if find else find_start(video, inst, n, lang, exe)
        if not src:
            if os.path.isfile(dst):
                ready += 1                            # converted before (the installation may have moved)
            else:
                log('  start film %s: not in the installation' % n)
            continue
        st = os.stat(src)
        cached = os.path.join(cache_dir, 'start_%s_%d.ogv' % (stem, st.st_size))
        if os.path.isfile(dst) and not force and os.path.isfile(cached) and os.path.getsize(cached) == os.path.getsize(dst):
            ready += 1
            continue
        if force or not os.path.isfile(cached):
            log('  start film %s' % n)
            if not encode(exe, src, cached, log):
                continue
        os.makedirs(out_dir, exist_ok=True)
        shutil.copyfile(cached, dst)
        ready += 1
    return ready, len(START_FILMS)
