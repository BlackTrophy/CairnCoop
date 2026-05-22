@echo off
chcp 65001 >nul
title CairnCoop Deinstallation

echo.
echo  Starte CairnCoop Deinstallation...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0CairnCoopSetup.ps1" -Uninstall

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  [FEHLER] Deinstallation fehlgeschlagen.
    pause
)
