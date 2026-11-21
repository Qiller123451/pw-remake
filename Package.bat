@echo off
rem Builds a playable package of the game and zips it into dist\ (docs/CONTENT.md "Sending the game to a tester").
rem Export the game from Godot first (Project > Export > Windows Desktop, into game\).
rem Options: --configs base,mirage  --no-data  --folder  --no-zip  --out <folder>  --godot <Godot exe>
cd /d "%~dp0"
python tools\package\package.py %*
if errorlevel 1 (echo. & echo Packaging failed - see the messages above. & pause & exit /b 1)
echo.
echo Done: the zip is in dist\
pause
