@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

rem Usage:
rem   build.cmd                 → Debug x64
rem   build.cmd Release         → Release x64
rem   build.cmd Release x86     → Release x86
rem   build.cmd x32 Debug       → Debug x86  (order-independent)
rem Arch aliases: x64 | x86 | x32 | Win32

set "CONFIG=Debug"
set "ARCH=x64"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="Release" set "CONFIG=Release" & shift & goto parse_args
if /I "%~1"=="Debug" set "CONFIG=Debug" & shift & goto parse_args
if /I "%~1"=="x64" set "ARCH=x64" & shift & goto parse_args
if /I "%~1"=="x86" set "ARCH=x86" & shift & goto parse_args
if /I "%~1"=="x32" set "ARCH=x86" & shift & goto parse_args
if /I "%~1"=="Win32" set "ARCH=x86" & shift & goto parse_args
echo ERROR: Unknown argument "%~1". Use Debug^|Release and/or x64^|x86^|x32.
exit /b 1
:args_done

rem Solution platform: x64 or x86 (TrackNative maps x86 → Win32).
set "SLN_PLATFORM=%ARCH%"
set "NATIVE_PLATFORM=x64"
if /I "%ARCH%"=="x86" set "NATIVE_PLATFORM=Win32"

set "OUT_DIR=%~dp0src\CPURacer.App\bin\%CONFIG%\net8.0-windows10.0.20348.0\%ARCH%"

set "MSBUILD="
if exist "D:\Development Programs\VS\Program\MSBuild\Current\Bin\amd64\MSBuild.exe" (
  set "MSBUILD=D:\Development Programs\VS\Program\MSBuild\Current\Bin\amd64\MSBuild.exe"
)
if not defined MSBUILD if exist "D:\Development Programs\VS\Program\MSBuild\Current\Bin\MSBuild.exe" (
  set "MSBUILD=D:\Development Programs\VS\Program\MSBuild\Current\Bin\MSBuild.exe"
)

rem VS 2022 installs to %ProgramFiles%\Microsoft Visual Studio\2022\<Edition>.
rem Probe each edition directly. %ProgramFiles(x86)% and vswhere contain
rem parens/spaces that break cmd's for /f parsing, so avoid them here.
if not defined MSBUILD for /d %%e in ("%ProgramFiles%\Microsoft Visual Studio\2022\*") do (
  if exist "%%e\MSBuild\Current\Bin\amd64\MSBuild.exe" set "MSBUILD=%%e\MSBuild\Current\Bin\amd64\MSBuild.exe"
)
if not defined MSBUILD for /d %%e in ("%ProgramFiles%\Microsoft Visual Studio\2022\*") do (
  if exist "%%e\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%%e\MSBuild\Current\Bin\MSBuild.exe"
)

rem GitHub Actions (setup-msbuild) and developer machines with MSBuild on PATH.
if not defined MSBUILD for /f "delims=" %%i in ('where msbuild 2^>nul') do (
  set "MSBUILD=%%i"
  goto :msbuild_ready
)
:msbuild_ready

if not defined MSBUILD (
  echo ERROR: MSBuild not found. Install VS 2022 ^(MSBuild + Desktop C++ + .NET^).
  exit /b 1
)

echo Using: %MSBUILD%
echo Building mixed C# + C++ ^(%CONFIG% / %ARCH%^)...
"%MSBUILD%" "%~dp0src\CPURacer.sln" /m /restore /p:Configuration=%CONFIG% /p:Platform=%SLN_PLATFORM% /p:CpuArch=%ARCH%
if errorlevel 1 exit /b 1

if not exist "%OUT_DIR%\CPURacer.TrackNative.dll" (
  echo TrackNative.dll missing in App output - building vcxproj explicitly ^(%NATIVE_PLATFORM%^)...
  "%MSBUILD%" "%~dp0src\CPURacer.TrackNative\CPURacer.TrackNative.vcxproj" /m /p:Configuration=%CONFIG% /p:Platform=%NATIVE_PLATFORM%
  if errorlevel 1 exit /b 1
)

if not exist "%OUT_DIR%\CPURacer.exe" (
  echo ERROR: CPURacer.exe missing in %OUT_DIR%
  exit /b 1
)
if not exist "%OUT_DIR%\CPURacer.TrackNative.dll" (
  echo ERROR: TrackNative.dll still missing in %OUT_DIR%
  exit /b 1
)

echo.
echo Build OK.
echo   %OUT_DIR%\CPURacer.exe
echo   %OUT_DIR%\CPURacer.TrackNative.dll
echo.
echo Run:
echo   "%OUT_DIR%\CPURacer.exe"
exit /b 0
