@echo off
setlocal
cd /d "%~dp0"

echo [1/4] Cleaning old build output...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
if exist publish rmdir /s /q publish
if exist NetScopePLC.exe del /f /q NetScopePLC.exe
if exist netscope_native.obj del /f /q netscope_native.obj

echo [2/4] Building native scan core...
call "%ProgramFiles%\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 (
  echo ERROR: VS 18 Enterprise C++ toolchain not found
  exit /b 1
)
cl /nologo /O2 /utf-8 netscope_native.c /link /SUBSYSTEM:CONSOLE iphlpapi.lib ws2_32.lib /OUT:NetScopeNative.exe
if errorlevel 1 exit /b 1
if exist netscope_native.obj del /f /q netscope_native.obj

echo [2.5/4] Building rounded app icon...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\make-ico.ps1"
if errorlevel 1 exit /b 1

echo [3/4] Publishing single-file WPF app...
dotnet publish NetScopePLC.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
if errorlevel 1 exit /b 1
if exist publish\NetScopePLC.pdb del /f /q publish\NetScopePLC.pdb
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj

echo [4/4] Done: %~dp0publish\NetScopePLC.exe
if /I "%~1"=="run" (
  echo Launching as admin...
  powershell -NoProfile -Command "Start-Process -FilePath '%~dp0publish\NetScopePLC.exe' -Verb RunAs"
)
