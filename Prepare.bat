@echo off
rem Makes the game configurations from your ParaWorld installation (docs/CONTENT.md).
rem Needs Python 3 and the ParaWorld Toolkit. Options: --paraworld <folder> --toolkit <folder> --force
cd /d "%~dp0"
python tools\prepare\prepare.py %*
if errorlevel 1 (echo. & echo The prepare tool failed - see the messages above. & pause & exit /b 1)
echo.
echo Done. Start the game from Godot (game\project.godot).
pause
