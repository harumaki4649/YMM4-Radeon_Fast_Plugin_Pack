@echo off
setlocal
chcp 65001 >nul

cd /d "%~dp0"

set "BUILD_CONFIG=Release"
if not "%~1"=="" set "BUILD_CONFIG=%~1"

set "CMAKE="
set "VCVARS="
for %%E in (Community Professional Enterprise BuildTools) do (
  if not defined CMAKE if exist "C:\Program Files\Microsoft Visual Studio\2022\%%E\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" (
    set "CMAKE=C:\Program Files\Microsoft Visual Studio\2022\%%E\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    set "VCVARS=C:\Program Files\Microsoft Visual Studio\2022\%%E\VC\Auxiliary\Build\vcvars64.bat"
  )
)

if not defined CMAKE (
  echo CMake not found in Visual Studio 2022 installation.
  exit /b 1
)

call "%VCVARS%" >nul
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

"%CMAKE%" --build build --config %BUILD_CONFIG%
exit /b %errorlevel%
