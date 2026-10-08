@echo off
setlocal

rem Stop DAFLegacy.Server.exe and verify its ports are released.
rem Template ports from server.json: admin 8081 (TCP), entrance 7001 (TCP),
rem channels Channel.PortBase+number 10001+ (TCP), character datagram 7001 (UDP),
rem gameplay datagram 12311/12312 (UDP).

set "SERVER_EXE=DAFLegacy.Server.exe"
set "PORT_PATTERNS=:8081[^0-9].*LISTENING :7001[^0-9].*LISTENING :100[0-9][0-9][^0-9].*LISTENING :7001[^0-9].*\*:\* :1231[12][^0-9].*\*:\*"

tasklist /FI "IMAGENAME eq %SERVER_EXE%" 2>nul | find /I "%SERVER_EXE%" >nul
if errorlevel 1 (
    echo [stop] %SERVER_EXE% is not running.
) else (
    echo [stop] Stopping %SERVER_EXE% ...
    taskkill /F /T /IM "%SERVER_EXE%" >nul 2>&1
    if errorlevel 1 (
        echo [stop] Failed to stop %SERVER_EXE%. Close it manually and retry.
        exit /b 1
    )
    echo [stop] Stopped.
)

ping -n 2 127.0.0.1 >nul

netstat -ano | findstr /R "%PORT_PATTERNS%" >nul 2>&1
if errorlevel 1 (
    echo [stop] All DAFLegacy ports are free.
) else (
    echo [stop] WARNING: some DAFLegacy ports are still occupied by another process:
    netstat -ano | findstr /R "%PORT_PATTERNS%"
    echo [stop] Kill the owning PID with: taskkill /F /PID ^<pid^>
    exit /b 1
)

exit /b 0
