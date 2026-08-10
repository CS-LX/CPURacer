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
if not defined MSBUILD (
  set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
  if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe"`) do set "MSBUILD=%%i"
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
