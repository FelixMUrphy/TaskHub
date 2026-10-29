@echo off
rem Runs the automated tests against an isolated hub (port 47899, temp data dir, no tray) and stops it afterwards.
rem Usage: tests\run_tests.cmd [path\to\TaskHubHost.exe]   (default: ..\app\TaskHubHost.exe)
rem Needs Python 3.9+ (stdlib only). Set PYTHON=... to choose the interpreter.
setlocal
cd /d "%~dp0"
set EXE=%~1
if "%EXE%"=="" set EXE=%~dp0..\app\TaskHubHost.exe
if "%PYTHON%"=="" set PYTHON=python
set DATA=%TEMP%\taskhub-tests
if exist "%DATA%" rmdir /s /q "%DATA%"
rem the test hub must not show Windows notifications (the burst test would queue dozens of them)
mkdir "%DATA%"
> "%DATA%\config.json" echo {"toasts": false, "autostart": false}
set TASKHUB_URL=http://127.0.0.1:47899
start "" /b "%EXE%" --port 47899 --data "%DATA%" --no-tray
rem wait until the hub answers (up to 30 s)
powershell -NoProfile -Command "for($i=0;$i -lt 60;$i++){ try{ Invoke-RestMethod http://127.0.0.1:47899/api/health -TimeoutSec 1 | Out-Null; exit 0 }catch{ Start-Sleep -Milliseconds 500 } }; exit 1" || (echo hub did not start & exit /b 2)
set RC=0
"%PYTHON%" api_test.py || set RC=1
"%PYTHON%" parallel_test.py 8 || set RC=1
"%PYTHON%" integration_test.py "%EXE%" || set RC=1
for %%D in ("%EXE%") do "%PYTHON%" mcp_test.py "%%~dpDtaskhub.exe" || set RC=1
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='TaskHubHost.exe'\" | Where-Object { $_.CommandLine -like '*47899*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"
if %RC%==0 (echo ALL TESTS PASSED) else (echo TESTS FAILED)
exit /b %RC%
