@echo off
setlocal

rem Manually build the client-side deliverables:
rem   Client\DFLegacy.Ijl15\build-native.ps1        -> artifacts\native\ijl15\<config>\win-x86\ijl15.dll
rem   Client\DFLegacy.RandomNative\build-native.ps1 -> artifacts\native\random\<config>\DFLegacy.RandomNative.dll
rem   dotnet publish Client\DFLegacy.Launcher       -> artifacts\native\launcher\
rem   DFLegacy.RandomNative.dll is copied into artifacts\native\launcher as well.
rem CMake + Visual Studio C++ build the native modules; the .NET 10 SDK builds
rem the Launcher. The Launcher publish runs after the native builds, so ijl15.dll
rem is picked up automatically (optional content item).
rem Extra switches are forwarded to both build-native.ps1 scripts,
rem e.g.  patch.bat -Configuration Debug   or   patch.bat -RunTests

set "CONFIG=Release"
set "EXTRA="
:parse_args
if "%~1"=="" goto :run
if /I "%~1"=="-Configuration" (
    set "CONFIG=%~2"
    shift
    shift
    goto :parse_args
)
set "EXTRA=%EXTRA% %~1"
shift
goto :parse_args

:run
cd /d "%~dp0"

where cmake >nul 2>&1
if errorlevel 1 (
    echo [patch] CMake was not found on PATH ^(Visual Studio's bundled CMake is
    echo [patch] located by build-native.ps1 automatically when VS is installed^).
)

call :build_one "ijl15" ".\Client\DFLegacy.Ijl15\build-native.ps1" "artifacts\native\ijl15\%CONFIG%\win-x86\ijl15.dll"
if errorlevel 1 goto :fail

call :build_one "DFLegacy.RandomNative" ".\Client\DFLegacy.RandomNative\build-native.ps1" "artifacts\native\random\%CONFIG%\DFLegacy.RandomNative.dll"
if errorlevel 1 goto :fail

echo [patch] Publishing DFLegacy.Launcher (%CONFIG%) ...
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [patch] dotnet CLI was not found on PATH. The .NET 10 SDK is required.
    goto :fail
)
dotnet publish .\Client\DFLegacy.Launcher\DFLegacy.Launcher.csproj -c %CONFIG% -r win-x64 -o .\artifacts\native\launcher
if errorlevel 1 (
    echo [patch] DFLegacy.Launcher publish FAILED.
    goto :fail
)
echo [patch] DFLegacy.Launcher OK. Output: %CD%\artifacts\native\launcher

echo [patch] Publishing DFLegacy.RandomNative (%CONFIG%) ...
copy /Y "artifacts\native\random\%CONFIG%\DFLegacy.RandomNative.dll" "artifacts\native\launcher\" >nul
if errorlevel 1 (
    echo [patch] DFLegacy.RandomNative publish FAILED.
    goto :fail
)
echo [patch] DFLegacy.RandomNative OK. Output: %CD%\artifacts\native\launcher\DFLegacy.RandomNative.dll

echo.
echo [patch] Done.
if "%CI%"=="" pause
exit /b 0

:build_one
echo [patch] Building %~1 (%CONFIG%) ...
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~2" -Configuration "%CONFIG%"%EXTRA%
if errorlevel 1 (
    echo [patch] %~1 build FAILED.
    exit /b 1
)
echo [patch] %~1 OK. Output: %CD%\%~3
exit /b 0

:fail
echo.
echo [patch] FAILED. CMake, the Visual Studio C++ toolset and the .NET 10 SDK are required.
if "%CI%"=="" pause
exit /b 1
