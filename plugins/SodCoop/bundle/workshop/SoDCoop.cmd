@echo off
rem Doubt Together (Co-op) bootstrap for Windows.
rem
rem Steam runs this from the game folder, with its real launch command chained after it
rem (Steam > Properties > Launch options). The path is deliberately NOT quoted: it has no
rem spaces, and a leading quote trips cmd's quote handling and breaks the whole line
rem (Steam runs launch options as: cmd /c "<the line>"):
rem   ..\..\workshop\content\986130\__ITEM_ID__\SoDCoop.cmd & %command%
rem
rem What it does, in order:
rem  - appends what it did to BepInEx\sodcoop_bootstrap.log in the game folder
rem    (or %TEMP%\sodcoop_bootstrap.log when the game folder is not writable);
rem  - if the in-game Mods menu switched this item off: remove the loader files it had
rem    installed, then return and let Steam start the plain game;
rem  - otherwise copy the bundled BepInEx loader into the game folder (only where nothing
rem    is there yet, so an existing mod-manager setup is never overwritten) and always
rem    refresh SodCoop.dll.
rem Steam starts the game itself right after this script returns.
rem
rem Unofficial co-op mod. Source (GPL-3.0-or-later): https://github.com/0xsimsch/mp_sod

setlocal EnableExtensions
set "MOD=%~dp0"
set "GAME=%CD%"

rem Everything is logged so a remote bug report has something to attach: Steam shows
rem none of this window's output. Prefer the game folder, fall back to TEMP.
set "LOGF=%TEMP%\sodcoop_bootstrap.log"
if exist "%GAME%\Shadows of Doubt.exe" (
    if not exist "%GAME%\BepInEx" mkdir "%GAME%\BepInEx" 2>nul
    if exist "%GAME%\BepInEx" set "LOGF=%GAME%\BepInEx\sodcoop_bootstrap.log"
)
echo [%date% %time%] start, working folder is "%GAME%">>"%LOGF%"

rem Safety: only ever write into a real Shadows of Doubt folder.
if not exist "%GAME%\Shadows of Doubt.exe" (
    echo [%date% %time%] NOT a Shadows of Doubt folder, doing nothing>>"%LOGF%"
    echo [DoubtTogether] not in the game folder %GAME%, doing nothing
    exit /b 0
)

rem The in-game Mods menu writes its Enabled checkbox back into this file.
findstr /r /c:"enabled.*false" "%MOD%ModSettings.txt" >nul 2>nul
if not errorlevel 1 (
    rem Remove the mod itself. The loader (winhttp.dll, doorstop_config.ini, dotnet\) only
    rem goes too if it is the copy this item installed (same .doorstop_version), never some
    rem other manager's loader.
    del /q "%GAME%\BepInEx\plugins\SodCoop.dll" 2>nul
    fc "%GAME%\.doorstop_version" "%MOD%.doorstop_version" >nul 2>nul
    if not errorlevel 1 (
        attrib -r -s -h "%GAME%\dotnet\*" /s /d >nul 2>nul
        del /q "%GAME%\winhttp.dll" "%GAME%\doorstop_config.ini" "%GAME%\.doorstop_version" 2>nul
        rd /s /q "%GAME%\dotnet" 2>nul
        echo [%date% %time%] switched off in the Mods menu, loader removed>>"%LOGF%"
    ) else (
        echo [%date% %time%] switched off, mod dll removed, loader kept because it is not ours>>"%LOGF%"
    )
    exit /b 0
)

if not exist "%GAME%\BepInEx\plugins" mkdir "%GAME%\BepInEx\plugins" 2>nul

rem Loader: install where nothing is there yet. A failing copy usually means the game
rem folder is not writable, e.g. a Steam library inside Program Files.
call :copyfile winhttp.dll
call :copyfile doorstop_config.ini
call :copyfile .doorstop_version
call :copydir dotnet
call :copydir BepInEx\core
call :copydir BepInEx\config
call :copydir BepInEx\patchers
call :copydir BepInEx\unity-libs

rem The mod itself: always the version that was subscribed.
copy /y "%MOD%BepInEx\plugins\SodCoop.dll" "%GAME%\BepInEx\plugins\SodCoop.dll" >nul 2>>"%LOGF%" || echo [%date% %time%] FAILED to copy SodCoop.dll into BepInEx\plugins: game folder not writable?>>"%LOGF%"

echo [%date% %time%] done, Steam starts the game now>>"%LOGF%"
exit /b 0

:copyfile
if exist "%GAME%\%~1" exit /b 0
copy /y "%MOD%%~1" "%GAME%\" >nul 2>>"%LOGF%"
if not exist "%GAME%\%~1" echo [%date% %time%] FAILED to install %~1, the game folder is not writable. A Steam library inside Program Files is not; move it out and try again.>>"%LOGF%"
rem Workshop files can arrive read-only; our copies must stay removable by the toggle-off.
attrib -r "%GAME%\%~1" >nul 2>nul
exit /b 0

:copydir
if exist "%GAME%\%~1" exit /b 0
if not exist "%MOD%%~1" exit /b 0
xcopy "%MOD%%~1" "%GAME%\%~1\" /e /i /h /y /q >>"%LOGF%" 2>&1
if errorlevel 4 echo [%date% %time%] FAILED to install %~1, xcopy error above. The game folder is probably not writable.>>"%LOGF%"
rem Read-only attributes would block the toggle-off removal later.
attrib -r -s -h "%GAME%\%~1\*" /s /d >nul 2>nul
exit /b 0
