@echo off
rem Doubt Together (Co-op) bootstrap for Windows.
rem
rem Steam does not run a shell for launch options, it starts the first token directly, so the
rem line has to bring cmd.exe and do everything inside its /s payload (Steam > Properties >
rem Launch options):
rem   C:\Windows\System32\cmd.exe /d /s /c "..\..\workshop\content\986130\__ITEM_ID__\SoDCoop.cmd & %command%"
rem The /d skips AutoRun, the /s keeps the payload quotes intact, and %command%, the real
rem Steam game start, runs inside that payload right after this script returns.
rem
rem What it does, in order:
rem  - appends what it did to BepInEx\sodcoop_bootstrap.log in the game folder
rem    (or %TEMP%\sodcoop_bootstrap.log when the game folder is not writable);
rem  - if the in-game Mods menu switched this item off: remove the loader files it had
rem    installed, then return and let the game start plain;
rem  - otherwise copy the bundled BepInEx loader into the game folder (only where nothing
rem    is there yet, so an existing mod-manager setup is never overwritten) and always
rem    refresh SodCoop.dll.
rem
rem Coding rule for this file, learned the hard way: it deliberately uses NO multi-line
rem parenthesized if-blocks. cmd expands variables inside such blocks while it READS them,
rem before deciding which branch runs, so a path value containing parentheses, which
rem "C:\Program Files (x86)" always does, breaks the block parser and kills the whole
rem script before its first command. Keep every expansion quoted, keep the flow flat with
rem goto labels. A plain "cd /d" style launch line therefore works for every Windows user.
rem
rem Unofficial co-op mod. Source (GPL-3.0-or-later): https://github.com/0xsimsch/mp_sod

setlocal EnableExtensions
set "MOD=%~dp0"
rem Resolve this folder to an absolute path: Steam may invoke the script with a
rem relative one (..\..\workshop\...), and copy tools mishandle relative sources.
pushd "%MOD%." 2>nul
if not errorlevel 1 set "MOD=%CD%\"
popd 2>nul
set "GAME=%CD%"

rem Everything is logged so a remote bug report has something to attach: Steam shows
rem none of the output of this window. Prefer the game folder, fall back to TEMP.
set "LOGF=%TEMP%\sodcoop_bootstrap.log"
if not exist "%GAME%\Shadows of Doubt.exe" goto :log_ready
if not exist "%GAME%\BepInEx" mkdir "%GAME%\BepInEx" 2>nul
if exist "%GAME%\BepInEx" set "LOGF=%GAME%\BepInEx\sodcoop_bootstrap.log"
:log_ready
echo [%date% %time%] start, working folder is "%GAME%">>"%LOGF%"

rem Safety: only ever write into a real Shadows of Doubt folder.
if exist "%GAME%\Shadows of Doubt.exe" goto :folder_ok
echo [%date% %time%] NOT a Shadows of Doubt folder, doing nothing>>"%LOGF%"
echo [DoubtTogether] not in the game folder "%GAME%", doing nothing
exit /b 0
:folder_ok

rem The in-game Mods menu writes its Enabled checkbox back into this file.
findstr /r /c:"enabled.*false" "%MOD%ModSettings.txt" >nul 2>nul
if errorlevel 1 goto :install

rem Switched off: remove the mod itself. The bundled loader files and the dotnet folder go
rem too, but only when this item installed them, checked by a matching .doorstop_version
rem file. A loader left by another mod manager is always kept.
del /q "%GAME%\BepInEx\plugins\SodCoop.dll" 2>nul
fc "%GAME%\.doorstop_version" "%MOD%.doorstop_version" >nul 2>nul
if errorlevel 1 goto :off_keep_loader
attrib -r -s -h "%GAME%\dotnet\*" /s /d >nul 2>nul
del /q "%GAME%\winhttp.dll" "%GAME%\doorstop_config.ini" "%GAME%\.doorstop_version" 2>nul
rd /s /q "%GAME%\dotnet" 2>nul
echo [%date% %time%] switched off in the Mods menu, loader removed>>"%LOGF%"
exit /b 0
:off_keep_loader
echo [%date% %time%] switched off, mod dll removed, loader kept because it is not ours>>"%LOGF%"
exit /b 0

:install
if exist "%GAME%\BepInEx\plugins" goto :plugins_ready
mkdir "%GAME%\BepInEx\plugins" 2>nul
:plugins_ready

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
copy /y "%MOD%BepInEx\plugins\SodCoop.dll" "%GAME%\BepInEx\plugins\SodCoop.dll" >nul 2>>"%LOGF%"
if exist "%GAME%\BepInEx\plugins\SodCoop.dll" goto :dll_ready
echo [%date% %time%] FAILED to copy SodCoop.dll into BepInEx\plugins: game folder not writable?>>"%LOGF%"
:dll_ready

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
xcopy "%MOD%%~1" "%GAME%\%~1" /e /i /h /y /q >>"%LOGF%" 2>&1
if not errorlevel 4 goto :copydir_done
rem xcopy could not copy (reported problems with spaced paths on some systems):
rem recursive fallback built only from mkdir + copy, which handle spaces reliably.
call :copytree "%MOD%%~1" "%GAME%\%~1"
if not exist "%GAME%\%~1" echo [%date% %time%] FAILED to install %~1: xcopy and fallback copy both failed. The game folder is probably not writable.>>"%LOGF%"
:copydir_done
rem Read-only attributes would block the toggle-off removal later.
attrib -r -s -h "%GAME%\%~1\*" /s /d >nul 2>nul
exit /b 0

:copytree
if not exist "%~2" mkdir "%~2" 2>nul
copy /y "%~1\*" "%~2\" >nul 2>>"%LOGF%"
for /d %%D in ("%~1\*") do call :copytree "%%D" "%~2\%%~nxD"
exit /b 0
