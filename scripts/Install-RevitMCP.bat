@echo off
rem rvt-mcp - installation en double-clic / double-click install
rem Installe le complement Revit (2022-2027 detectes), le serveur rvt-mcp.exe et
rem connecte automatiquement Claude Desktop, Claude Code, Codex (et autres clients detectes).
rem Chaque config client modifiee est sauvegardee en <config>.bak. Fermez Revit et Claude Desktop avant.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath %~dp0 -Recurse | Unblock-File"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
echo Termine. Ouvrez Revit 2026 : onglet Add-Ins ^> RvtMcp ^> "Chat IA". / Done.
pause
