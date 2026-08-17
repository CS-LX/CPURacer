@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

rem Usage:
rem   pack-release.cmd                 → version from default, arch x64
rem   pack-release.cmd 1.2.0.0         → version + x64
rem   pack-release.cmd 1.2.0.0 x86     → version + x86
rem   pack-release.cmd x86             → default version + x86
rem Env: PACK_NAME overrides zip stem; PACK_ARCH overrides arch.

set "CONFIG=Release"
set "VER=1.2.0.0"
set "ARCH=x64"
if defined PACK_ARCH set "ARCH=%PACK_ARCH%"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="x64" set "ARCH=x64" & shift & goto parse_args
if /I "%~1"=="x86" set "ARCH=x86" & shift & goto parse_args
if /I "%~1"=="x32" set "ARCH=x86" & shift & goto parse_args
if /I "%~1"=="Win32" set "ARCH=x86" & shift & goto parse_args
rem Otherwise treat as version string.
set "VER=%~1"
shift
goto parse_args
:args_done

if /I "%ARCH%"=="x32" set "ARCH=x86"
if /I "%ARCH%"=="Win32" set "ARCH=x86"

set "OUT=src\CPURacer.App\bin\%CONFIG%\net8.0-windows10.0.20348.0\%ARCH%"

if not exist "%OUT%\CPURacer.exe" (
  echo Building Release %ARCH% first...
  call "%~dp0build.cmd" Release %ARCH%
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

rem PACK_NAME overrides the zip folder/file stem (CI: CPURacer-ci-<sha>-win-x64).
if defined PACK_NAME (
  set "NAME=%PACK_NAME%"
) else (
  set "NAME=CPURacer-%VER%-win-%ARCH%"
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
