@echo off
REM ===========================================================================
REM  RefractorForge - BUILD the editor (framework-dependent)
REM  The .NET runtime is NOT bundled - the .exe uses the .NET 8 Runtime you have
REM  installed (see README: "Install .NET 8"). Output is small.
REM ===========================================================================
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================================
echo  RefractorForge - building the native GPU editor (.exe)
echo ============================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [X] The .NET SDK was not found. Install the .NET 8 SDK ^(x64^):
  echo       winget install Microsoft.DotNet.SDK.8
  echo     or download from https://dotnet.microsoft.com/download/dotnet/8.0
  echo.
  pause & exit /b 1
)

echo Using .NET SDK:
dotnet --version
echo.
echo Publishing (framework-dependent; first run restores NuGet packages - needs internet)...
echo.

dotnet publish src\RefractorForge.Viewer -c Release
if errorlevel 1 (
  echo.
  echo ============================================================
  echo  BUILD FAILED.
  echo  Copy ALL the error text above and send it back - I'll fix it.
  echo ============================================================
  pause & exit /b 1
)

set "OUT=%~dp0src\RefractorForge.Viewer\bin\Release\net8.0-windows\publish"
echo.
echo ============================================================
echo  BUILD OK.
echo  Editor folder:      %OUT%
echo  Editor executable:  %OUT%\RefractorForge.Viewer.exe
echo.
echo  This needs the .NET 8 Runtime installed to run (you have it).
echo  Next: edit the 3 paths in run.bat, then double-click it.
echo ============================================================
pause
