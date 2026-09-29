@echo off
setlocal

rem Build and publish the portable game server into dist\DFLegacy.Server.
rem Requires only the .NET 10 SDK (the solution no longer contains native
rem projects). The client plugin (ijl15.dll) is built separately with patch.bat.
rem The DAF-MCP module is published separately with publish-mcp.bat.

cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [publish] dotnet CLI was not found on PATH.
    goto :fail
)

echo [publish] Cleaning dist\DFLegacy.Server ...
if exist "dist\DFLegacy.Server" rmdir /s /q "dist\DFLegacy.Server"
if exist "dist\DFLegacy.Server" (
    echo [publish] Could not remove dist\DFLegacy.Server. Stop any process using it and retry.
    goto :fail
)

echo [publish] Publishing server to dist\DFLegacy.Server ...
dotnet publish .\Server\DFLegacy.Server\DFLegacy.Server.csproj -c Release -r win-x64 -o .\dist\DFLegacy.Server
if errorlevel 1 goto :fail

echo.
echo [publish] Done. Output directory: %CD%\dist\DFLegacy.Server
if "%CI%"=="" pause
exit /b 0

:fail
echo.
echo [publish] FAILED. See the log above for details.
if "%CI%"=="" pause
exit /b 1
