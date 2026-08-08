@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "CONFIG=Release"
set "OUT=src\CPURacer.App\bin\%CONFIG%\net8.0-windows"
set "VER=0.4.0"
if not "%~1"=="" set "VER=%~1"

if not exist "%OUT%\CPURacer.exe" (
  echo Building Release first...
  call "%~dp0build.cmd" Release
  if errorlevel 1 exit /b 1
)

if not exist "%OUT%\CPURacer.TrackNative.dll" (
  echo ERROR: TrackNative.dll missing in %OUT%
  exit /b 1
)

set "STAGE=%TEMP%\CPURacer-pack-%RANDOM%"
set "NAME=CPURacer-%VER%-win-x64"
set "DIST=%~dp0dist"
mkdir "%DIST%" 2>nul
mkdir "%STAGE%\%NAME%" 2>nul

rem Whitelist runtime files only — no pdb / lib / exp / xml / obj junk.
robocopy "%OUT%" "%STAGE%\%NAME%" *.exe *.dll *.json /NFL /NDL /NJH /NJS /nc /ns /np /XF *.pdb *.xml *.lib *.exp >nul
if errorlevel 8 (
  echo ERROR: robocopy failed
  rd /s /q "%STAGE%" 2>nul
  exit /b 1
)

rem Belt-and-suspenders: purge any debug leftovers if patterns slip through.
del /s /q "%STAGE%\%NAME%\*.pdb" 2>nul
del /s /q "%STAGE%\%NAME%\*.xml" 2>nul
del /s /q "%STAGE%\%NAME%\*.lib" 2>nul
del /s /q "%STAGE%\%NAME%\*.exp" 2>nul
del /s /q "%STAGE%\%NAME%\*.iobj" 2>nul
del /s /q "%STAGE%\%NAME%\*.ipdb" 2>nul

if not exist "%STAGE%\%NAME%\CPURacer.exe" (
  echo ERROR: staged package missing CPURacer.exe
  rd /s /q "%STAGE%" 2>nul
  exit /b 1
)
if not exist "%STAGE%\%NAME%\CPURacer.TrackNative.dll" (
  echo ERROR: staged package missing TrackNative.dll
  rd /s /q "%STAGE%" 2>nul
  exit /b 1
)

echo Staged files:
dir /b "%STAGE%\%NAME%"

set "ZIP=%DIST%\%NAME%.zip"
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path (Join-Path '%STAGE%' '%NAME%') -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  rd /s /q "%STAGE%" 2>nul
  exit /b 1
)

rd /s /q "%STAGE%" 2>nul
echo.
echo Packed: %ZIP%
for %%A in ("%ZIP%") do echo Size: %%~zA bytes
exit /b 0
