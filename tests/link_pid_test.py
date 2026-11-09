"""A file link on a toast is only shown, as in the panel, and a pid that isn't a Windows process doesn't end a task as lost. Own hub. Argument: TaskHubHost.exe.
The toast part presses the button through the hub's test route: Windows' own activation isn't exercised."""
import json, os, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.request

EXE = sys.argv[1]
PORT = 47859
URL = f"http://127.0.0.1:{PORT}"
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None, timeout=30):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(), headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=timeout) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


data = tempfile.mkdtemp(prefix="taskhub-linkpid-data-")
home = tempfile.mkdtemp(prefix="taskhub-linkpid-home-")
with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"toasts": False, "autostart": False, "update_check": False, "language": "en", "log_level": "debug"}, f)
hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=dict(os.environ, TASKHUB_TEST_HOME=home))
try:
    for _ in range(60):
        try:
            call("GET", "/api/health", timeout=2)
            break
        except OSError:
            time.sleep(.5)

    # ---- pid
    call("POST", "/api/tasks", {"id": "wsl", "name": "pid from WSL", "pid": 99999996})
    call("POST", "/api/tasks", {"id": "hb", "name": "pid from WSL, heartbeat", "pid": 99999996, "heartbeat_timeout": 1})
    p = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    call("POST", "/api/tasks", {"id": "real", "name": "a real process", "pid": p.pid, "notify": {"finish": False}})
    time.sleep(1)
    p.kill()
    p.wait()
    state = lambda t: call("GET", f"/api/tasks/{t}")[1].get("state")
    time.sleep(9)   # three watchdog rounds
    check("a pid that isn't a Windows process: not lost", state("wsl") == "running", state("wsl"))
    check("...the heartbeat still counts", state("hb") == "stale", state("hb"))
    check("a real process that ended: still lost", state("real") == "lost", state("real"))

    # ---- toast link
    marker = os.path.join(data, "ran.txt")
    script = os.path.join(data, "evil.cmd")
    with open(script, "w") as f:
        f.write(f'@echo ran> "{marker}"\n')
    call("POST", "/api/tasks", {"id": "lnk", "name": "with a file link", "links": [{"label": "Report", "url": "file:///" + script.replace("\\", "/")}]})
    call("POST", "/api/tasks/lnk/finish", {"state": "done"})
    code, _ = call("POST", "/api/test/toast", {"argument": "action=link;task=lnk;i=0"})
    check("test route answers", code == 200, code)
    time.sleep(3)
    check("a file link on a toast isn't run", not os.path.exists(marker))
    with open(os.path.join(data, "taskhub.log"), encoding="utf-8", errors="replace") as f:
        logged = f.read()
    check("...it would be shown in its folder", "would show in Explorer" in logged)
finally:
    hub.kill()
    hub.wait()
    time.sleep(.5)
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(home, ignore_errors=True)

print("LINK AND PID:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
