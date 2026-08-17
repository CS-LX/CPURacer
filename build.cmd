@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "CONFIG=Debug"
if /I "%~1"=="Release" set "CONFIG=Release"

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
echo Building mixed C# + C++ solution ^(one step^)...
"%MSBUILD%" "%~dp0src\CPURacer.sln" /m /restore /p:Configuration=%CONFIG% /p:Platform="Any CPU"
if errorlevel 1 exit /b 1

if not exist "%~dp0src\CPURacer.App\bin\%CONFIG%\net8.0-windows10.0.20348.0\CPURacer.TrackNative.dll" (
  echo TrackNative.dll missing in App output - building vcxproj explicitly...
  "%MSBUILD%" "%~dp0src\CPURacer.TrackNative\CPURacer.TrackNative.vcxproj" /m /p:Configuration=%CONFIG% /p:Platform=x64
  if errorlevel 1 exit /b 1
)

echo.
echo Build OK.
echo   src\CPURacer.App\bin\%CONFIG%\net8.0-windows10.0.20348.0\CPURacer.exe
echo   src\CPURacer.App\bin\%CONFIG%\net8.0-windows10.0.20348.0\CPURacer.TrackNative.dll
echo.
echo Run:
echo   dotnet run --project src\CPURacer.App --no-build
exit /b 0