@echo off
rem Runs the automated tests against an isolated hub (port 47899, temp data dir, no tray) and stops it afterwards.
rem Usage: tests\run_tests.cmd [path\to\TaskHubHost.exe]   (default: ..\app\TaskHubHost.exe)
rem Needs Python 3.9+ (stdlib only). Set PYTHON=... to choose the interpreter.
setlocal
cd /d "%~dp0"
set EXE=%~1
if "%EXE%"=="" set EXE=%~dp0..\app\TaskHubHost.exe
if "%PYTHON%"=="" set PYTHON=python
rem as in CI: the fakes print non-Latin text through pipes, which the console code page can't carry
set PYTHONUTF8=1
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
rem one token for this run's hubs (they read TASKHUB_TOKEN); sitecustomize.py sends it on the tests' own requests. token_test.py takes it away and checks the real path
set TASKHUB_TOKEN=taskhub-test-run-token
set PYTHONPATH=%~dp0
set TASKHUB_DATA=%DATA%
rem the settings a user changes only on the settings page answer over HTTP here, as on every isolated test hub
set TASKHUB_TEST_HOME=%DATA%\home
mkdir "%TASKHUB_TEST_HOME%"
start "" /b "%EXE%" --port 47899 --data "%DATA%" --no-tray
set TASKHUB_TEST_HOME=
rem wait until the hub answers (up to 30 s)
powershell -NoProfile -Command "for($i=0;$i -lt 60;$i++){ try{ Invoke-RestMethod http://127.0.0.1:47899/api/health -TimeoutSec 1 | Out-Null; exit 0 }catch{ Start-Sleep -Milliseconds 500 } }; exit 1" || (echo hub did not start & exit /b 2)
set RC=0
"%PYTHON%" run_one.py "%DATA%\results" api_test.py || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" validation_test.py || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" api_contract_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" parallel_test.py 8 || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" integration_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" install_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" package_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" update_test.py "%EXE%" || set RC=1
for %%D in ("%EXE%") do "%PYTHON%" run_one.py "%DATA%\results" mcp_test.py "%%~dpDtaskhub.exe" || set RC=1
for %%D in ("%EXE%") do "%PYTHON%" run_one.py "%DATA%\results" blender_test.py "%%~dpDtaskhub.exe" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" telegram_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" discord_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" github_test.py || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" github_prs_test.py || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" github_askpass_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" runpod_test.py || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" vastai_test.py || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" guard_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" aws_sigv4_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" aws_test.py || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" aws_http_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" hub_proof_test.py "%EXE%" || set RC=1
for %%D in ("%EXE%") do "%PYTHON%" run_one.py "%DATA%\results" token_test.py "%EXE%" "%%~dpDtaskhub.exe" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" cli_console_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" ssh_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" worktime_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" worktime_month_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" worktime_rates_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" limits_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" hooks_offer_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" codex_limits_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" payload_bounds_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" hook_form_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" log_test.py "%EXE%" "%DATA%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" i18n_test.py || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" suite_honesty_test.py || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" backoff_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" link_pid_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" bounds_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" durable_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" guard_timing_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" guard_403_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" guard_unknown_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" connector_trust_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" telegram_gate_test.py "%EXE%" || set RC=1
"%PYTHON%" run_one.py "%DATA%\results" unit_test.py || set RC=1
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='TaskHubHost.exe'\" | Where-Object { $_.CommandLine -like '*47899*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"
rem WebView2 processes a killed test hub left behind: its own temp folders only, and only when the parent is gone
powershell -NoProfile -Command "$all = Get-CimInstance Win32_Process; $ids = @{}; $all | ForEach-Object { $ids[[int]$_.ProcessId] = 1 }; $all | Where-Object { $_.Name -eq 'msedgewebview2.exe' -and $_.CommandLine -like '*\Temp\taskhub-*' -and -not $ids[[int]$_.ParentProcessId] } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
"%PYTHON%" run_summary.py "%DATA%\results" "%DATA%" || set RC=1
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
