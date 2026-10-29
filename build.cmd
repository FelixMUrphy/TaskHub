@echo off
rem Builds TaskHubHost.exe + taskhub.exe into .\app (self-contained win-x64: no .NET install needed)
rem and packs dist\TaskHub-<version>-win-x64.zip for distribution.
rem   build.cmd          -> app\ + zip
rem   build.cmd nozip    -> app\ only
setlocal
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set VER=2.0.0
set PUB=-c Release -r win-x64 --self-contained true -p:DebugType=none -p:PublishReadyToRun=false --nologo -v q
dotnet publish src\Host\TaskHubHost.csproj %PUB% -o app || exit /b 1
dotnet publish src\Cli\taskhub.csproj %PUB% -o app || exit /b 1
echo build ok: %~dp0app
if /i "%~1"=="nozip" exit /b 0
if not exist dist mkdir dist
if exist dist\TaskHub-%VER%-win-x64.zip del dist\TaskHub-%VER%-win-x64.zip
powershell -NoProfile -Command "$src='%~dp0app'; $tmp=Join-Path $env:TEMP 'TaskHub-pack'; if(Test-Path $tmp){Remove-Item $tmp -Recurse -Force}; New-Item -ItemType Directory $tmp | Out-Null; Copy-Item \"$src\*\" $tmp -Recurse -Exclude 'TaskHubHost.settings.json'; Compress-Archive -Path \"$tmp\*\" -DestinationPath '%~dp0dist\TaskHub-%VER%-win-x64.zip' -CompressionLevel Optimal; Remove-Item $tmp -Recurse -Force" || exit /b 1
echo zip ok: %~dp0dist\TaskHub-%VER%-win-x64.zip
