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
rem Every test's own temp folders go into one folder that is removed at the end: a WebView2 process can still hold files when a test deletes its folder.
rem The unpacked runtime of a single-file exe stays where it was, or each run would unpack it again.
set REALTEMP=%TEMP%
set RUNTEMP=%TEMP%\taskhub-tests-temp
if not defined DOTNET_BUNDLE_EXTRACT_BASE_DIR set DOTNET_BUNDLE_EXTRACT_BASE_DIR=%TEMP%\.net
if exist "%RUNTEMP%" rmdir /s /q "%RUNTEMP%"
mkdir "%RUNTEMP%"
for /f %%N in ('powershell -NoProfile -Command "@(Get-ChildItem -Directory -Path $env:REALTEMP -Filter 'taskhub*' | Where-Object { $_.Name -notin 'taskhub-tests','taskhub-tests-temp' }).Count"') do set TEMPBEFORE=%%N
set TEMP=%RUNTEMP%
set TMP=%RUNTEMP%
set TMPDIR=%RUNTEMP%
rem the test hub must not show Windows notifications (the burst test would queue dozens of them)
mkdir "%DATA%"
> "%DATA%\config.json" echo {"log_level": "debug", "toasts": false, "autostart": false, "toast_rate": 0, "toast_task_gap": 0, "telegram_api": "http://127.0.0.1:47897", "connectors": {"github": {"api": "http://127.0.0.1:47896"}, "runpod": {"api": "http://127.0.0.1:47891"}, "vastai": {"api": "http://127.0.0.1:47890"}, "aws": {"api": "http://127.0.0.1:47889"}}}
set TASKHUB_URL=http://127.0.0.1:47899
set TASKHUB_DATA=%DATA%
start "" /b "%EXE%" --port 47899 --data "%DATA%" --no-tray
rem wait until the hub answers (up to 30 s)
powershell -NoProfile -Command "for($i=0;$i -lt 60;$i++){ try{ Invoke-RestMethod http://127.0.0.1:47899/api/health -TimeoutSec 1 | Out-Null; exit 0 }catch{ Start-Sleep -Milliseconds 500 } }; exit 1" || (echo hub did not start & exit /b 2)
set RC=0
"%PYTHON%" api_test.py || set RC=1
"%PYTHON%" validation_test.py || set RC=1
"%PYTHON%" api_contract_test.py "%EXE%" || set RC=1
"%PYTHON%" parallel_test.py 8 || set RC=1
"%PYTHON%" integration_test.py "%EXE%" || set RC=1
"%PYTHON%" install_test.py "%EXE%" || set RC=1
"%PYTHON%" package_test.py "%EXE%" || set RC=1
"%PYTHON%" update_test.py "%EXE%" || set RC=1
for %%D in ("%EXE%") do "%PYTHON%" mcp_test.py "%%~dpDtaskhub.exe" || set RC=1
for %%D in ("%EXE%") do "%PYTHON%" blender_test.py "%%~dpDtaskhub.exe" || set RC=1
"%PYTHON%" telegram_test.py "%EXE%" || set RC=1
"%PYTHON%" discord_test.py "%EXE%" || set RC=1
"%PYTHON%" github_test.py || set RC=1
"%PYTHON%" github_prs_test.py || set RC=1
"%PYTHON%" github_askpass_test.py "%EXE%" || set RC=1
"%PYTHON%" runpod_test.py || set RC=1
"%PYTHON%" vastai_test.py || set RC=1
"%PYTHON%" guard_test.py "%EXE%" || set RC=1
"%PYTHON%" aws_sigv4_test.py "%EXE%" || set RC=1
"%PYTHON%" aws_test.py || set RC=1
"%PYTHON%" aws_http_test.py "%EXE%" || set RC=1
"%PYTHON%" hub_proof_test.py "%EXE%" || set RC=1
"%PYTHON%" cli_console_test.py "%EXE%" || set RC=1
"%PYTHON%" ssh_test.py "%EXE%" || set RC=1
"%PYTHON%" limits_test.py "%EXE%" || set RC=1
"%PYTHON%" hooks_offer_test.py "%EXE%" || set RC=1
"%PYTHON%" codex_limits_test.py "%EXE%" || set RC=1
"%PYTHON%" payload_bounds_test.py "%EXE%" || set RC=1
"%PYTHON%" hook_form_test.py "%EXE%" || set RC=1
"%PYTHON%" log_test.py "%EXE%" "%DATA%" || set RC=1
"%PYTHON%" i18n_test.py || set RC=1
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='TaskHubHost.exe'\" | Where-Object { $_.CommandLine -like '*47899*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"
rem WebView2 processes a killed test hub left behind: its own temp folders only, and only when the parent is gone
powershell -NoProfile -Command "$all = Get-CimInstance Win32_Process; $ids = @{}; $all | ForEach-Object { $ids[[int]$_.ProcessId] = 1 }; $all | Where-Object { $_.Name -eq 'msedgewebview2.exe' -and $_.CommandLine -like '*\Temp\taskhub-*' -and -not $ids[[int]$_.ParentProcessId] } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
rem the temp folder of this run; nothing may be left beside it
powershell -NoProfile -Command "for($i=0;$i -lt 20;$i++){ Remove-Item -LiteralPath $env:RUNTEMP -Recurse -Force -ErrorAction SilentlyContinue; if(-not (Test-Path -LiteralPath $env:RUNTEMP)){break}; Start-Sleep 1 }"
if exist "%RUNTEMP%" echo NOTE the temp folder of this run is still in use; the next run removes it
for /f %%N in ('powershell -NoProfile -Command "@(Get-ChildItem -Directory -Path $env:REALTEMP -Filter 'taskhub*' | Where-Object { $_.Name -notin 'taskhub-tests','taskhub-tests-temp' }).Count"') do set TEMPAFTER=%%N
if %TEMPAFTER% GTR %TEMPBEFORE% (
  echo FAIL the tests left folders in %REALTEMP%: %TEMPBEFORE% before, %TEMPAFTER% after
  set RC=1
) else (
  echo PASS no folders left in the temp folder
)
if %RC%==0 (echo ALL TESTS PASSED) else (echo TESTS FAILED)
exit /b %RC%
