@echo off
setlocal

rem Publish the DAF-MCP module (docs/design/09-mcp-packet-tap.md) into
rem dist\DFLegacy.Mcp, separate from publish.bat.

cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [publish-mcp] dotnet CLI was not found on PATH.
    goto :fail
)

echo [publish-mcp] Cleaning dist\DFLegacy.Mcp ...
if exist "dist\DFLegacy.Mcp" rmdir /s /q "dist\DFLegacy.Mcp"
if exist "dist\DFLegacy.Mcp" (
    echo [publish-mcp] Could not remove dist\DFLegacy.Mcp. Stop any process using it and retry.
    goto :fail
)

echo [publish-mcp] Publishing DAF-MCP module to dist\DFLegacy.Mcp ...
dotnet publish .\Server\DFLegacy.Mcp\DFLegacy.Mcp.csproj -c Release -r win-x64 -o .\dist\DFLegacy.Mcp
if errorlevel 1 goto :fail

for %%F in ("DFLegacy.Mcp.dll" "ModelContextProtocol.Core.dll" "ModelContextProtocol.dll" "ModelContextProtocol.AspNetCore.dll" "Microsoft.Extensions.AI.Abstractions.dll") do (
    if not exist ".\dist\DFLegacy.Mcp\%%~F" (
        echo [publish-mcp] Missing module DLL: %%~F
        goto :fail
    )
)

echo.
echo [publish-mcp] Done. Output directory: %CD%\dist\DFLegacy.Mcp
if "%CI%"=="" pause
exit /b 0

:fail
echo.
echo [publish-mcp] FAILED. See the log above for details.
if "%CI%"=="" pause
exit /b 1
