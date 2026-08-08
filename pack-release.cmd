@echo off
setlocal EnableExtensions
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

robocopy "%OUT%" "%STAGE%\%NAME%" /E /NFL /NDL /NJH /NJS /nc /ns /np >nul
del /q "%STAGE%\%NAME%\*.pdb" 2>nul
del /q "%STAGE%\%NAME%\*.xml" 2>nul

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
exit /b 0
