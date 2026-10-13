@echo off
rem rvt-mcp - desinstallation / uninstall (vos donnees personnelles sont conservees sans -Purge)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
pause
