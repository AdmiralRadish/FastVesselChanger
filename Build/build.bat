@echo off
REM Build script for FastVesselChanger using .NET CLI (VS 2026 compatible)
REM Usage: build.bat [Configuration] [Platform]
REM Example: build.bat Release x64

setlocal enabledelayedexpansion

set CONFIG=%1
set PLATFORM=%2

if "%CONFIG%"=="" (
  set CONFIG=Release
)

if "%PLATFORM%"=="" (
  set PLATFORM=x64
)

echo Building FastVesselChanger with %CONFIG% configuration and %PLATFORM% platform...

REM Build using dotnet CLI
dotnet build ".\FastVesselChanger.sln" -c %CONFIG% -f net472 || (
  echo Build failed!
  pause
  exit /b 1
)

echo.
echo Build complete. KSPBuildTools will automatically handle GameData packaging.
echo The output DLL will be placed in: GameData\FastVesselChanger\Plugins\
echo.
pause

