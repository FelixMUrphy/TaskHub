"""What `taskhub` does to the console and to a command line: Ctrl+C from the panel ends a task as cancelled, the console's code page comes back,
an argument with & stays one argument when the command goes through cmd; a shell control's output is read in UTF-8 or the OEM code page.
Hidden consoles of their own (no keystrokes), own port and temporary folders."""
import ctypes, json, os, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.request

EXE = sys.argv[1]
CLI = os.path.join(os.path.dirname(EXE), "taskhub.exe")
PORT = 47873
URL = f"http://127.0.0.1:{PORT}"
tmp = tempfile.mkdtemp(prefix="taskhub-console-")
data = os.path.join(tmp, "data")
os.makedirs(data)
ENV = dict(os.environ, TASKHUB_TEST_HOME=tmp, TASKHUB_NO_AUTOSTART="1", TASKHUB_URL=URL)
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(),
                               headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=10) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def task(tid):
    code, t = call("GET", f"/api/tasks/{tid}")
    return t if code == 200 else None


def until(cond, timeout=20):
    end = time.time() + timeout
    while time.time() < end:
        v = cond()
        if v:
            return v
        time.sleep(0.1)
    return None


def hidden():
    si = subprocess.STARTUPINFO()
    si.dwFlags = subprocess.STARTF_USESHOWWINDOW
    si.wShowWindow = 0
    return {"creationflags": subprocess.CREATE_NEW_CONSOLE, "startupinfo": si}


with open(os.path.join(data, "config.json"), "w") as f:
    json.dump({"toasts": False, "autostart": False, "update_check": False, "toast_rate": 0}, f)
hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=ENV)
try:
    end = time.time() + 30
    while time.time() < end:
        try:
            if call("GET", "/api/health")[0] == 200:
                break
        except OSError:
            pass
        time.sleep(0.2)

    # ------------------------------------------------------------ Ctrl+C from the panel
    W = ("import ctypes,signal,subprocess,sys; ctypes.windll.kernel32.SetConsoleCtrlHandler(None,False); "
         "signal.signal(signal.SIGINT, signal.SIG_IGN); sys.exit(subprocess.call(sys.argv[1:]))")
    p = subprocess.Popen([sys.executable, "-c", W, CLI, "run", "--id", "cc", "--name", "ctrl c", "--quiet", "--", sys.executable, "-c", "import time; time.sleep(60)"], env=ENV, **hidden())
    t = until(lambda: (lambda x: x if x and any(c["id"] == "interrupt" for c in x.get("controls", [])) else None)(task("cc")))
    check("a run with Ctrl+C button", t is not None, t)
    time.sleep(1.5)
    code, r = call("POST", "/api/tasks/cc/controls/interrupt", {})
    check("Ctrl+C button pressed", code == 200, (code, r))
    t = until(lambda: (lambda x: x if x and x["state"] not in ("running", "stale") else None)(task("cc")), 25)
    check("Ctrl+C from the panel: the task is cancelled, not failed", t and t["state"] == "cancelled", t and (t["state"], t.get("message")))
    try:
        p.wait(10)
    except subprocess.TimeoutExpired:
        p.kill()

    # ------------------------------------------------------------ the console's code page comes back
    out = os.path.join(tmp, "cp.txt")
    q = subprocess.Popen(f'cmd /d /c "chcp 866>nul & "{CLI}" health & chcp>"{out}""', env=ENV, **hidden())
    q.wait(30)
    text = open(out).read() if os.path.exists(out) else ""
    check("the code page is back after a command", "866" in text, text)

    # ------------------------------------------------------------ through cmd, one argument stays one
    marker = os.path.join(tmp, "marker.txt")
    r = subprocess.run([CLI, "run", "--id", "qa", "--quiet", "--", "ver", f"x&type", f"nul>{marker}"], env=ENV, capture_output=True, timeout=30)
    time.sleep(0.5)
    check("an argument with & doesn't start a second command", not os.path.exists(marker), r)
    m2 = os.path.join(tmp, "marker2.txt")
    r = subprocess.run([CLI, "run", "--id", "qb", "--quiet", "--", "ver", "&", "type", "nul", ">", m2], env=ENV, capture_output=True, timeout=30)
    check("an operator on its own still works", os.path.exists(m2), r)

    # ------------------------------------------------------------ a shell control's output
    oem_cp = ctypes.windll.kernel32.GetOEMCP()
    oem = None
    for ch in "Юé":
        try:
            ch.encode(f"cp{oem_cp}")
            oem = ch
            break
        except (UnicodeEncodeError, LookupError):
            pass
    uf, of = os.path.join(tmp, "u.txt"), os.path.join(tmp, "o.txt")
    with open(uf, "wb") as f:
        f.write("utf-Приклад-é\r\n".encode("utf-8"))
    if oem:
        with open(of, "wb") as f:
            f.write(f"oem-{oem}-line\r\n".encode(f"cp{oem_cp}"))
    code, r = call("POST", "/api/tasks", {"id": "sh", "name": "shell", "controls": [{"id": "s", "label": "S", "kind": "shell", "command": f'type "{uf}" & type "{of}"'}]})
    call("POST", "/api/tasks/sh/controls/s", {})
    t = until(lambda: (lambda x: x if x and any("exit 0" in l["msg"] for l in x["log"]) else None)(task("sh")))
    log = [l["msg"] for l in (t or {}).get("log", [])]
    check("UTF-8 output stays readable", any("utf-Приклад-é" in l for l in log), log)
    if oem:
        check("OEM output is readable", any(f"oem-{oem}-line" in l for l in log), log)
    else:
        print("SKIP OEM output: no test character for code page", oem_cp)
finally:
    hub.kill()
    hub.wait(10)
    shutil.rmtree(tmp, ignore_errors=True)

print("CLI CONSOLE TESTS " + ("PASSED" if not fails else f"FAILED ({fails})"))
raise SystemExit(1 if fails else 0)
