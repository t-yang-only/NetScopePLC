@echo off
setlocal
cd /d "%~dp0"
call "%ProgramFiles%\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cl /nologo /O2 /utf-8 netscope_native.c /link /SUBSYSTEM:CONSOLE iphlpapi.lib ws2_32.lib /OUT:NetScopeNative.exe
if errorlevel 1 exit /b 1
dotnet publish NetScopePLC.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
if errorlevel 1 exit /b 1
copy /Y publish\NetScopePLC.exe NetScopePLC.exe >nul
copy /Y NetScopeNative.exe publish\NetScopeNative.exe >nul
echo Built NetScopePLC.exe and NetScopeNative.exe
