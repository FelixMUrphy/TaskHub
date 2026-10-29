@echo off
rem Builds TaskHubHost.exe + taskhub.exe into .\app (framework-dependent, needs .NET 8 Desktop + ASP.NET Core runtimes)
setlocal
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
dotnet publish src\Host\TaskHubHost.csproj -c Release -o app --nologo -v q -p:DebugType=none || exit /b 1
dotnet publish src\Cli\taskhub.csproj -c Release -o app --nologo -v q -p:DebugType=none || exit /b 1
echo build ok: %~dp0app
