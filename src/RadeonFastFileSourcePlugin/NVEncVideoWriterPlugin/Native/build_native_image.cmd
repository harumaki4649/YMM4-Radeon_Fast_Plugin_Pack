@echo off
setlocal
chcp 65001 >nul

call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1

set "VIPS_DIR=%~dp0..\..\vendor\vips-dev-8.18"
set "OUT_DIR=%~dp0build\Release"
set "SRC=%~dp0RadeonFastNativeImage.c"

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

cl /nologo /O2 /LD /utf-8 /D_CRT_SECURE_NO_WARNINGS ^
  /I"%VIPS_DIR%\include" ^
  /I"%VIPS_DIR%\include\glib-2.0" ^
  /I"%VIPS_DIR%\lib\glib-2.0\include" ^
  "%SRC%" ^
  /Fe"%OUT_DIR%\RadeonFastNativeImage.dll" ^
  /Fo"%OUT_DIR%\RadeonFastNativeImage.obj" ^
  /link /LIBPATH:"%VIPS_DIR%\lib" libvips.lib libglib-2.0.lib libgobject-2.0.lib
