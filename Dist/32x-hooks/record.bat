@echo off
rem Starts EmuHawk with PicoDrive's call recorder on. See docs/32x-hooks.md, "Call recorder".
rem
rem Put this file next to EmuHawk.exe in your BizHawk-2.11.1-32x-hooks folder (the copy with this branch's
rem picodrive.wbx.zst and BizHawk.Emulation.Cores.dll in dll\), put your config file there too (start from
rem recorder-example.txt), and set the paths below. The variable is set for this EmuHawk only.
setlocal

rem the config file
set "CONFIG=%~dp0recorder.txt"
rem the game: a full path
set "ROM=C:\path\to\your\game.32x"
rem a movie to play from power-on, or nothing. Movies need the 32X BIOS files in EmuHawk's firmware
rem (Config > Firmware); without them the core refuses deterministic mode, which movies use.
set "MOVIE="

set "PICODRIVE_CALL_RECORDER=%CONFIG%"
if not exist "%CONFIG%" (
	echo No config file at %CONFIG%
	pause
	exit /b 1
)
if defined MOVIE (
	start "" /D "%~dp0" "%~dp0EmuHawk.exe" "--movie=%MOVIE%" "%ROM%"
) else (
	start "" /D "%~dp0" "%~dp0EmuHawk.exe" "%ROM%"
)
