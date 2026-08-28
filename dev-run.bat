@echo off
setlocal
cd /d "%~dp0"

powershell -NoProfile -Command "Get-Process -Name NetScopePLC -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 1"

echo [dev-run] Building Debug...
dotnet build NetScopePLC.csproj -c Debug -v q
if errorlevel 1 (
  echo [dev-run] BUILD FAILED
  exit /b 1
)

set "EXE=%~dp0bin\Debug\net10.0-windows\win-x64\NetScopePLC.exe"
set "CWD=%~dp0bin\Debug\net10.0-windows\win-x64"
if not exist "%EXE%" (
  echo [dev-run] EXE not found: %EXE%
  exit /b 1
)

echo [dev-run] Launching as admin...
powershell -NoProfile -Command "Get-Process -Name NetScopePLC -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Process -FilePath '%EXE%' -WorkingDirectory '%CWD%' -Verb RunAs"
