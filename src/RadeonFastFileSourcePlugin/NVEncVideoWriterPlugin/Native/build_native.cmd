@echo off
setlocal
chcp 65001 >nul

cd /d "%~dp0"

set "CMAKE=C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
if not exist "%CMAKE%" (
  echo CMake not found: "%CMAKE%"
  exit /b 1
)

call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1

if exist "build\" (
  rmdir /s /q "build"
  if errorlevel 1 exit /b 1
) else if exist "build" (
  del /f /q "build"
  if errorlevel 1 exit /b 1
)

"%CMAKE%" -S . -B build -G "Visual Studio 17 2022" -A x64
if errorlevel 1 exit /b %errorlevel%

"%CMAKE%" --build build --config Release
exit /b %errorlevel%
