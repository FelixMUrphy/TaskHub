@echo off
rem Builds the single-file setup exe: dist\TaskHub-<version>-setup.exe
rem (self-contained win-x64: .NET runtime, panel, docs, Python client and the taskhub CLI are all inside).
rem Also leaves app\TaskHubHost.exe + app\taskhub.exe for development / tests.
setlocal
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set VER=2.4.2
set SF=-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none --nologo -v q
rem 1) CLI (trimmed + compressed single file; short-lived, so decompression RAM does not matter) -> obj\cli\taskhub.exe,
rem    embedded into the hub as payload
dotnet publish src\Cli\taskhub.csproj %SF% -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -p:InvariantGlobalization=true -o obj\cli || exit /b 1
rem 2) hub: single file WITHOUT compression - assemblies are memory-mapped instead of unpacked into RAM
rem    (~100 MB working set instead of ~200 MB; the exe is bigger)
if exist app rmdir /s /q app
dotnet publish src\Host\TaskHubHost.csproj %SF% -p:IncludeNativeLibrariesForSelfExtract=true -o app || exit /b 1
copy /y obj\cli\taskhub.exe app\taskhub.exe >nul
if not exist dist mkdir dist
del /q dist\* 2>nul
copy /y app\TaskHubHost.exe dist\TaskHub-%VER%-setup.exe >nul
echo build ok: %~dp0dist\TaskHub-%VER%-setup.exe
