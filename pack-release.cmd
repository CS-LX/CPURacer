@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "CONFIG=Release"
set "OUT=src\CPURacer.App\bin\%CONFIG%\net8.0-windows10.0.20348.0"
set "VER=1.1.0.0"
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

rem Sanity: Release must not leave PDBs in App output (see src/Directory.Build.props + TrackNative).
dir /b "%OUT%\*.pdb" 2>nul | findstr /r "." >nul
if not errorlevel 1 (
  echo ERROR: PDB files found under %OUT% - fix Release symbol settings, do not strip at pack time.
  dir /b "%OUT%\*.pdb"
  exit /b 1
)

rem PACK_NAME overrides the zip folder/file stem (CI: CPURacer-ci-<sha>).
if defined PACK_NAME (
  set "NAME=%PACK_NAME%"
) else (
  set "NAME=CPURacer-%VER%-win-x64"
)

set "STAGE=%TEMP%\CPURacer-pack-%RANDOM%"
set "DIST=%~dp0dist"
mkdir "%DIST%" 2>nul
mkdir "%STAGE%\%NAME%" 2>nul

rem Output dir is already pack-ready; copy as-is.
robocopy "%OUT%" "%STAGE%\%NAME%" /E /NFL /NDL /NJH /NJS /nc /ns /np >nul
if errorlevel 8 (
  echo ERROR: robocopy failed
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
