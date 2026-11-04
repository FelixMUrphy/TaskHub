"""Runs the xUnit tests of tests/unit (the pure decisions, at their production constants) for run_tests.cmd. Without the SDK: a SKIP on a developer's machine, a FAIL under CI."""
import os, re, shutil, subprocess, sys

here = os.path.dirname(os.path.abspath(__file__))
if not shutil.which("dotnet"):
    print(("FAIL " if os.environ.get("CI") else "SKIP ") + "unit tests: no dotnet here")
    sys.exit(1 if os.environ.get("CI") else 0)
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
r = subprocess.run(["dotnet", "test", os.path.join(here, "unit", "TaskHub.UnitTests.csproj"), "-c", "Release", "--nologo"], env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=here)
m = re.search(r"Passed:\s*(\d+)", r.stdout)
passed = int(m.group(1)) if m else 0
if r.returncode == 0 and passed > 0:
    print(f"PASS unit tests: {passed} passed")
    sys.exit(0)
print(r.stdout[-4000:])
print(r.stderr[-1000:])
print(f"FAIL unit tests: exit {r.returncode}, {passed} passed")
sys.exit(1)
