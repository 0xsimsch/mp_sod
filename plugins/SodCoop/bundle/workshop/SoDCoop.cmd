@echo off
rem Doubt Together (Co-op) bootstrap for Windows.
rem
rem Steam runs this from the game folder before the game would start (Steam > Properties > Launch options):
rem   "..\..\workshop\content\986130\__ITEM_ID__\SoDCoop.cmd" & %command%
rem
rem What it does, in order:
rem  - if the in-game Mods menu switched this item off: remove the loader files it had installed,
rem    then let Steam start the plain game;
rem  - otherwise copy the bundled BepInEx loader into the game folder (only where nothing is there yet,
rem    so an existing mod-manager setup is never overwritten) and always refresh SodCoop.dll.
rem Steam starts the game itself right after this script returns.
rem
rem Unofficial co-op mod. Source (GPL-3.0-or-later): https://github.com/0xsimsch/mp_sod

setlocal
set "MOD=%~dp0"
set "GAME=%CD%"

rem Safety: only ever write into a real Shadows of Doubt folder.
if not exist "%GAME%\Shadows of Doubt.exe" (
    echo [DoubtTogether] not in the game folder %GAME%, doing nothing
    exit /b 0
)

rem The in-game Mods menu writes its Enabled checkbox back into this file.
findstr /r /c:"enabled.*false" "%MOD%ModSettings.txt" >nul 2>nul
if not errorlevel 1 (
    rem Remove the mod itself. The loader (winhttp.dll, doorstop_config.ini, dotnet\) only goes too if it
    rem is the copy this item installed (same .doorstop_version), never some other manager's loader.
    del /q "%GAME%\BepInEx\plugins\SodCoop.dll" 2>nul
    fc /b "%GAME%\.doorstop_version" "%MOD%.doorstop_version" >nul 2>nul
    if not errorlevel 1 (
        del /q "%GAME%\winhttp.dll" "%GAME%\doorstop_config.ini" "%GAME%\.doorstop_version" 2>nul
        rd /s /q "%GAME%\dotnet" 2>nul
    )
    echo [DoubtTogether] switched off in the Mods menu, loader removed
    exit /b 0
)

if not exist "%GAME%\BepInEx\plugins" mkdir "%GAME%\BepInEx\plugins"

rem Loader files: install where nothing is there yet.
if not exist "%GAME%\winhttp.dll" copy /y "%MOD%winhttp.dll" "%GAME%\" >nul
if not exist "%GAME%\doorstop_config.ini" copy /y "%MOD%doorstop_config.ini" "%GAME%\" >nul
if not exist "%GAME%\.doorstop_version" copy /y "%MOD%.doorstop_version" "%GAME%\" >nul
if not exist "%GAME%\dotnet" robocopy "%MOD%dotnet" "%GAME%\dotnet" /e /r:1 /w:1 /njh /njs /ndl /nfl /np >nul
if not exist "%GAME%\BepInEx\core" robocopy "%MOD%BepInEx\core" "%GAME%\BepInEx\core" /e /r:1 /w:1 /njh /njs /ndl /nfl /np >nul
if not exist "%GAME%\BepInEx\config" robocopy "%MOD%BepInEx\config" "%GAME%\BepInEx\config" /e /r:1 /w:1 /njh /njs /ndl /nfl /np >nul
if exist "%MOD%BepInEx\patchers" if not exist "%GAME%\BepInEx\patchers" robocopy "%MOD%BepInEx\patchers" "%GAME%\BepInEx\patchers" /e /r:1 /w:1 /njh /njs /ndl /nfl /np >nul
if exist "%MOD%BepInEx\unity-libs" if not exist "%GAME%\BepInEx\unity-libs" robocopy "%MOD%BepInEx\unity-libs" "%GAME%\BepInEx\unity-libs" /e /r:1 /w:1 /njh /njs /ndl /nfl /np >nul

rem The mod itself: always the version that was subscribed.
copy /y "%MOD%BepInEx\plugins\SodCoop.dll" "%GAME%\BepInEx\plugins\SodCoop.dll" >nul
exit /b 0
