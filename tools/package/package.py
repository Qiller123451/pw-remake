"""Build a playable package of the remake and zip it (docs/CONTENT.md "Sending the game to a tester").

    python tools/package/package.py [--configs base,mirage] [--no-data] [--out dist] [--folder] [--no-zip]
                                    [--godot <Godot .exe>] [--name ParaWorldRemake]

What goes in (the layout the exported game expects - AppPaths.RootDir finds the root from the executable upwards):

    ParaWorldRemake/
      game/      the exported executable (ParaWorld Remake.exe, .console.exe, .pck, data_ParaWorldRemake_windows_x86_64/),
                 project.godot (the marker the executable finds its root by), lua/, maps/, campaigns/, coop/, missions/
      content/   configs/<id>/ (what Prepare made: config.json, maps/, missions/, overrides/), hd/
      data/<id>/ the converted game data of each configuration (the toolkit's export, ~2 GB each; --no-data leaves it out)
      Set ParaWorld folder.bat   writes the tester's ParaWorld folder into every config.json (set_paraworld_folder.ps1)
      README.txt

config.json in the package: "data" points at ../../../data/<id> (relative paths: GameConfiguration.Resolve), "install"
is empty until the tester runs "Set ParaWorld folder.bat" - the game reads sounds, music, texts and voices from the
tester's own ParaWorld installation (MIRAGE: with its MIRAGE and BoosterPack3 folders).

Left out: sources, the Godot project's cache (.godot), content/cache (Prepare's working files), tools, tests, docs.

The executable is the one exported from Godot (Project > Export > Windows Desktop, into game/). --godot <exe> exports
it first (headless, the "Windows Desktop" preset). The script warns when the export is older than the sources.

The zip is written straight from the files (no copy of the 2 GB folders); --folder also writes the package as a
folder next to it. Pictures, films and sounds are stored, the rest deflated.
"""
import argparse
import datetime
import json
import os
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GAME = ROOT / "game"
CONTENT = ROOT / "content"
EXE = "ParaWorld Remake.exe"
EXE_CONSOLE = "ParaWorld Remake.console.exe"
PCK = "ParaWorld Remake.pck"
DOTNET_DIR = "data_ParaWorldRemake_windows_x86_64"
GAME_DIRS = ["lua", "maps", "campaigns", "coop", "missions"]
CONTENT_DIRS = ["hd"]
# already compressed: stored, not deflated again (deflating 2 GB of them only costs time)
STORED = {".png", ".jpg", ".jpeg", ".ogg", ".ogv", ".mp3", ".zip", ".pck", ".dds", ".webp", ".glb", ".ktx2"}
SKIP_NAMES = {".git", ".godot", "__pycache__", "Thumbs.db", "desktop.ini"}

SET_FOLDER_BAT = r"""@echo off
rem Writes your ParaWorld folder (the one that contains "Data") into the game's configurations.
powershell -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0set_paraworld_folder.ps1" %*
pause
"""

SET_FOLDER_PS1 = r"""param([string]$Folder)
# Writes the ParaWorld folder (the one that contains Data) into content\configs\*\config.json ("install").
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Folder) {
    Add-Type -AssemblyName System.Windows.Forms
    $d = New-Object System.Windows.Forms.FolderBrowserDialog
    $d.Description = 'Choose your ParaWorld folder (the one that contains Data)'
    if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $Folder = $d.SelectedPath }
}
if (-not $Folder) { $Folder = Read-Host 'ParaWorld folder (the one that contains Data)' }
if (-not $Folder) { Write-Host 'Nothing chosen.'; exit 1 }
$Folder = $Folder.Trim('"')
if (-not (Test-Path -LiteralPath (Join-Path $Folder 'Data\Base'))) {
    Write-Host "$Folder has no Data\Base folder - that is not the ParaWorld folder."; exit 1
}
$p = $Folder.Replace('\', '/')
Get-ChildItem -LiteralPath (Join-Path $root 'content\configs') -Filter config.json -Recurse | ForEach-Object {
    $t = [IO.File]::ReadAllText($_.FullName)
    $t = [regex]::Replace($t, '("install"\s*:\s*)"[^"]*"', { param($m) $m.Groups[1].Value + '"' + $p + '"' })
    [IO.File]::WriteAllText($_.FullName, $t)
    Write-Host ('set ' + $_.Directory.Name + ': ' + $p)
}
if (Test-Path -LiteralPath (Join-Path $Folder 'Data\MIRAGE')) { Write-Host 'MIRAGE found.' }
else { Write-Host 'No MIRAGE in this installation: play the ParaWorld configuration.' }
Write-Host 'Done. Start game\ParaWorld Remake.exe'
"""

README = """ParaWorld Remake - test build {date}

You need your own ParaWorld installation: the game reads its sounds, music, texts and voices from it.
{mirage_note}
1. Unpack this folder anywhere (a path without special characters is safest).
2. Run "Set ParaWorld folder.bat" once and choose your ParaWorld folder (the one that contains "Data").
3. Start game\\ParaWorld Remake.exe  (game\\ParaWorld Remake.console.exe shows a log window as well).

Configurations in this build: {configs}
Settings, saves and logs go to %APPDATA%\\Godot\\app_userdata\\ParaWorld Remake\\ - please send the newest file from
its logs\\ folder with any bug report.
If the title screen's "Game data" lists a problem, it names the folder it cannot find.
"""


def die(msg):
    print("ERROR: " + msg)
    sys.exit(1)


def newest(paths, exts):
    t = 0.0
    for base in paths:
        if not base.exists():
            continue
        for dp, dns, fns in os.walk(base):
            dns[:] = [d for d in dns if d not in SKIP_NAMES and d not in ("bin", "obj")]
            for f in fns:
                if os.path.splitext(f)[1].lower() in exts:
                    t = max(t, os.path.getmtime(os.path.join(dp, f)))
    return t


def walk(src: Path, arc: str, out: list):
    """every file below src as (path, name in the package)"""
    if src.is_file():
        out.append((src, arc))
        return
    for dp, dns, fns in os.walk(src):
        dns[:] = [d for d in dns if d not in SKIP_NAMES]
        rel = Path(dp).relative_to(src)
        for f in fns:
            if f in SKIP_NAMES:
                continue
            out.append((Path(dp) / f, (Path(arc) / rel / f).as_posix()))


def export(godot: str):
    print(f"exporting with {godot} ...")
    r = subprocess.run([godot, "--headless", "--path", str(GAME), "--export-release", "Windows Desktop", EXE])
    if r.returncode != 0:
        die("the Godot export failed (is the Windows export template installed? Editor > Manage Export Templates)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--configs", help="comma separated configuration ids (default: all in content/configs)")
    ap.add_argument("--no-data", action="store_true", help="leave the converted game data out (the tester has it)")
    ap.add_argument("--out", default=str(ROOT / "dist"), help="where the zip goes (default: dist/)")
    ap.add_argument("--name", default="ParaWorldRemake", help="the package's folder name")
    ap.add_argument("--folder", action="store_true", help="also write the package as a folder")
    ap.add_argument("--no-zip", action="store_true", help="no zip (with --folder: only the folder)")
    ap.add_argument("--godot", help="Godot editor executable: export the game first")
    a = ap.parse_args()
    if a.no_zip and not a.folder:
        die("--no-zip without --folder makes nothing")

    if a.godot:
        export(a.godot)
    for f in (EXE, PCK):
        if not (GAME / f).exists():
            die(f"game/{f} is missing: export the game first (Godot: Project > Export > Windows Desktop, path game/), or pass --godot <Godot exe>")
    if not (GAME / DOTNET_DIR).is_dir():
        die(f"game/{DOTNET_DIR}/ is missing: the export is incomplete (export again)")
    t_exe = min((GAME / EXE).stat().st_mtime, (GAME / PCK).stat().st_mtime)
    t_src = newest([GAME / "src", GAME / "scenes", ROOT / "src"], {".cs", ".tscn", ".gdshader", ".tres"})
    if t_src > t_exe + 1:
        when = datetime.datetime.fromtimestamp(t_src).strftime("%Y-%m-%d %H:%M")
        print(f"WARNING: sources changed after the export (newest {when}) - the package would carry the old game. Export again (or --godot).")

    # the configurations
    cfg_root = CONTENT / "configs"
    ids = [c.strip() for c in a.configs.split(",")] if a.configs else sorted(d.name for d in cfg_root.iterdir() if (d / "config.json").exists())
    if not ids:
        die("no configurations in content/configs: run Prepare.bat first")
    cfgs = {}
    for cid in ids:
        f = cfg_root / cid / "config.json"
        if not f.exists():
            die(f"no configuration {cid} (content/configs/{cid}/config.json)")
        c = json.loads(f.read_text(encoding="utf-8-sig"))
        data = Path(c.get("data", ""))
        if not data.is_absolute():
            data = (cfg_root / cid / data).resolve()
        if not a.no_data and not (data / "assets" / "manifest.json").exists():
            die(f"{cid}: its converted data {data} is not there (assets/manifest.json missing)")
        if not (cfg_root / cid / "missions").is_dir():
            die(f"{cid}: no missions folder - run Prepare.bat first")
        cfgs[cid] = (c, data)

    top = a.name
    files: list = []
    for f in (EXE, EXE_CONSOLE, PCK, "project.godot"):
        if (GAME / f).exists():
            files.append((GAME / f, f"{top}/game/{f}"))
    walk(GAME / DOTNET_DIR, f"{top}/game/{DOTNET_DIR}", files)
    for d in GAME_DIRS:
        if (GAME / d).exists():
            walk(GAME / d, f"{top}/game/{d}", files)
    for d in CONTENT_DIRS:
        if (CONTENT / d).exists():
            walk(CONTENT / d, f"{top}/content/{d}", files)
    if (CONTENT / "README.md").exists():
        files.append((CONTENT / "README.md", f"{top}/content/README.md"))
    generated: dict = {}
    for cid, (c, data) in cfgs.items():
        cdir = cfg_root / cid
        part: list = []
        walk(cdir, f"{top}/content/configs/{cid}", part)
        files += [x for x in part if x[0].name != "config.json" or x[0].parent != cdir]
        pc = dict(c)
        pc["data"] = f"../../../data/{cid}" if not a.no_data else ""
        pc["install"] = ""
        generated[f"{top}/content/configs/{cid}/config.json"] = json.dumps(pc, indent=2, ensure_ascii=False)
        if not a.no_data:
            walk(data, f"{top}/data/{cid}", files)
    date = datetime.date.today().isoformat()
    mirage = any(c.get("mod") for c, _ in cfgs.values())
    generated[f"{top}/README.txt"] = README.format(
        date=date, configs=", ".join(f"{c.get('title', cid)} ({cid})" for cid, (c, _) in cfgs.items()),
        mirage_note="For the MIRAGE configuration it must have MIRAGE installed (its MIRAGE and BoosterPack3 folders).\n" if mirage else "")
    generated[f"{top}/Set ParaWorld folder.bat"] = SET_FOLDER_BAT
    generated[f"{top}/set_paraworld_folder.ps1"] = SET_FOLDER_PS1
    if a.no_data:
        generated[f"{top}/README.txt"] += "\nThis build has no converted game data: put the toolkit's export in data\\<configuration>\\ (or set \"data\" in content\\configs\\<configuration>\\config.json).\n"

    total = sum(p.stat().st_size for p, _ in files)
    print(f"{len(files)} files, {total / 2**30:.2f} GB; configurations: {', '.join(cfgs)}{' (no data)' if a.no_data else ''}")

    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    if a.folder:
        dst = out / top
        if dst.exists():
            shutil.rmtree(dst)
        for p, arc in files:
            t = out / arc
            t.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(p, t)
        for arc, text in generated.items():
            t = out / arc
            t.parent.mkdir(parents=True, exist_ok=True)
            t.write_text(text, encoding="utf-8", newline="\r\n" if arc.endswith((".bat", ".txt", ".ps1")) else "\n")
        print(f"folder: {dst}")
    if not a.no_zip:
        zpath = out / f"{top}-{date}{'-nodata' if a.no_data else ''}.zip"
        t0, done, last = time.time(), 0, 0.0
        with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as z:
            for arc, text in generated.items():
                z.writestr(arc, text.replace("\n", "\r\n") if arc.endswith((".bat", ".txt", ".ps1")) else text)
            for p, arc in files:
                ext = p.suffix.lower()
                z.write(p, arc, compress_type=zipfile.ZIP_STORED if ext in STORED else zipfile.ZIP_DEFLATED)
                done += p.stat().st_size
                if time.time() - last > 2:
                    last = time.time()
                    print(f"  {done / max(1, total) * 100:5.1f}%  {done / 2**30:.2f} / {total / 2**30:.2f} GB", end="\r", flush=True)
        print(f"\nzip: {zpath} ({zpath.stat().st_size / 2**30:.2f} GB, {time.time() - t0:.0f} s)")


if __name__ == "__main__":
    main()
