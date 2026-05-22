@echo off
chcp 65001 >nul
title CairnCoop Installer

echo.
echo  Starte CairnCoop Installer...
echo.

:: PowerShell Execution Policy temporaer umgehen (kein Admin noetig)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0CairnCoopSetup.ps1"

:: Falls PowerShell nicht verfuegbar
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  [FEHLER] PowerShell konnte nicht gestartet werden.
    echo  Bitte Windows PowerShell 5.1 oder neuer installieren.
    echo.
    pause
)
