"""Store (MSIX) vs exe edition detection: only TaskHub's own package - the folder the exe lives in - makes the Store edition.
A process running with ANOTHER package's identity (e.g. the exe edition's uninstaller started from Settings -> Apps inside
the Settings app's package context) is still the exe edition, so --uninstall works there.
The package folder is simulated with TASKHUB_TEST_PACKAGE_PATH (honoured only with TASKHUB_TEST_HOME).
Usage: package_test.py PATH\\TaskHubHost.exe"""
import os, subprocess, sys, tempfile

EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app", "TaskHubHost.exe")
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


home = tempfile.mkdtemp(prefix="taskhub-pkg-home-")
data = tempfile.mkdtemp(prefix="taskhub-pkg-data-")


def edition(pkg_path, *extra):
    env = dict(os.environ, TASKHUB_TEST_HOME=home)
    env.pop("TASKHUB_TEST_PACKAGE_PATH", None)
    if pkg_path is not None:
        env["TASKHUB_TEST_PACKAGE_PATH"] = pkg_path
    r = subprocess.run([EXE, "--status", "--port", "47893", "--data", data, *extra], env=env, capture_output=True, text=True, timeout=60)
    line = next((l for l in r.stdout.splitlines() if l.startswith("Edition: ")), "")
    return line[len("Edition: "):].strip(), r


exe_dir = os.path.dirname(os.path.abspath(EXE))
e, r = edition(None)
check("no package identity -> exe edition", e == "exe", (r.returncode, r.stdout, r.stderr))
e, r = edition(r"C:\Windows\ImmersiveControlPanel")
check("another package's identity (Settings app context) -> still the exe edition", e == "exe", r.stdout)
e, r = edition(os.path.dirname(exe_dir))
check("a package folder above the exe's folder -> store edition", e == "store", r.stdout)
e, r = edition(exe_dir)
check("TaskHub's own package (the exe's folder) -> store edition", e == "store", r.stdout)
e, r = edition(exe_dir + "x")
check("a folder whose name only starts the same -> exe edition", e == "exe", r.stdout)
print(f"{fails} failure(s)")
raise SystemExit(1 if fails else 0)
