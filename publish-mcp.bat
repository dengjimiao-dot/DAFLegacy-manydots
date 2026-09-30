@echo off
setlocal

rem Publish the DAF-MCP module (docs/design/09-mcp-packet-tap.md) into
rem dist\DAFLegacy.Mcp, separate from publish.bat.

cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [publish-mcp] dotnet CLI was not found on PATH.
    goto :fail
)

echo [publish-mcp] Cleaning dist\DAFLegacy.Mcp ...
if exist "dist\DAFLegacy.Mcp" rmdir /s /q "dist\DAFLegacy.Mcp"
if exist "dist\DAFLegacy.Mcp" (
    echo [publish-mcp] Could not remove dist\DAFLegacy.Mcp. Stop any process using it and retry.
    goto :fail
)

echo [publish-mcp] Publishing DAF-MCP module to dist\DAFLegacy.Mcp ...
dotnet publish .\Server\DAFLegacy.Mcp\DAFLegacy.Mcp.csproj -c Release -r win-x64 -o .\dist\DAFLegacy.Mcp
if errorlevel 1 goto :fail

for %%F in ("DAFLegacy.Mcp.dll" "ModelContextProtocol.Core.dll" "ModelContextProtocol.dll" "ModelContextProtocol.AspNetCore.dll" "Microsoft.Extensions.AI.Abstractions.dll") do (
    if not exist ".\dist\DAFLegacy.Mcp\%%~F" (
        echo [publish-mcp] Missing module DLL: %%~F
        goto :fail
    )
)

echo.
echo [publish-mcp] Done. Output directory: %CD%\dist\DAFLegacy.Mcp
if "%CI%"=="" pause
exit /b 0

:fail
echo.
echo [publish-mcp] FAILED. See the log above for details.
if "%CI%"=="" pause
exit /b 1
