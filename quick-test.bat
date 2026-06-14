@echo off
REM ===========================================================================
REM  RefractorForge - QUICK TEST (build + run in one step)
REM  On first launch it ASKS you to pick the level folder + standardMesh.rfa +
REM  objects.rfa via dialogs, then remembers them. No path editing needed.
REM  To choose a different level later:   quick-test.bat --pick
REM ===========================================================================
setlocal EnableExtensions
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo [X] .NET SDK not found. Install the .NET 8 SDK ^(x64^):
  echo       winget install Microsoft.DotNet.SDK.8
  echo.
  pause & exit /b 1
)
echo Building and launching (first run restores NuGet packages - needs internet)...
dotnet run --project src\RefractorForge.Viewer -c Release -- %*
echo.
echo (editor window closed)
pause
