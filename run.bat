@echo off
rem Launch MultiDesk. 优先运行构建脚本生成的运行副本（src\bin\Run），
rem 避免直接占用编译输出文件。
setlocal
set EXE=%~dp0src\bin\Run\MultiDesk.exe
if not exist "%EXE%" (
  set EXE=%~dp0src\bin\Debug\MultiDesk.exe
)
if not exist "%EXE%" (
  echo MultiDesk.exe not found. Run build.bat first.
  pause
  exit /b 1
)
start "" "%EXE%"
