@echo off
title Instalador Bahr Hidro BIM - Revit 2026
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Instalar_BahrHidro.ps1"
echo.
pause
