@echo off
REM Double-click launcher for the PSC log parser.
REM Opens a file-selection dialog (defaulting to the RimWorld Player.log folder),
REM pulls out the [PSC] lines, and shows the clean trace plus a tally.

setlocal
REM Run from this .bat's own folder so the script path resolves wherever the repo lives.
cd /d "%~dp0"

REM Prefer the Python launcher (py); fall back to python on PATH.
set "PYEXE=py"
where py >nul 2>nul || set "PYEXE=python"

"%PYEXE%" "%~dp0parse_psc_log.py" --pick

echo.
pause
endlocal
