@echo off
rem Build MultiDesk with the in-box .NET Framework 4.8 MSBuild.
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo.
  echo BUILD FAILED, exit code %EXITCODE%
)
exit /b %EXITCODE%
