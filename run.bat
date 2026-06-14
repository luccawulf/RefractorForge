@echo off
REM ===========================================================================
REM  RefractorForge - RUN the standalone editor built with build.bat
REM  First launch ASKS for the level folder + the two .rfa files (dialogs) and
REM  remembers them - no need to edit this file.
REM  To choose a different level later:   run.bat --pick
REM ===========================================================================
setlocal EnableExtensions
cd /d "%~dp0"
set "EXE=%~dp0src\RefractorForge.Viewer\bin\Release\net8.0-windows\publish\RefractorForge.Viewer.exe"
if not exist "%EXE%" (
  echo Editor not built yet. Run build.bat first.
  echo Looked for: "%EXE%"
  echo.
  pause & exit /b 1
)
"%EXE%" %*
