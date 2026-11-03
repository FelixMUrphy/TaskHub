"""What a local process can post is bounded: a reset time no window can have doesn't freeze the limits row, and a path naming another machine isn't touched
(no connection outside SafeHttp, no stall). Own port and temporary folders. Argument: TaskHubHost.exe. The address is from the block reserved for documentation."""
import json, os, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.request

EXE = sys.argv[1]
PORT = 47884
URL = f"http://127.0.0.1:{PORT}"
data = tempfile.mkdtemp(prefix="taskhub-bounds-data-")
home = tempfile.mkdtemp(prefix="taskhub-bounds-home-")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None, timeout=60):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(), headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=timeout) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


os.makedirs(os.path.join(home, ".claude"))
with open(os.path.join(home, ".claude", "settings.json"), "w", encoding="utf-8") as f:
    json.dump({"statusLine": {"type": "command", "command": "C:/x/taskhub.exe statusline"}}, f)
with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"toasts": False, "autostart": False, "update_check": False, "toast_rate": 0, "toast_task_gap": 0, "language": "en"}, f)
hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data], env=dict(os.environ, TASKHUB_TEST_HOME=home))
try:
    for _ in range(60):
        try:
            call("GET", "/api/health", timeout=2)
            break
        except OSError:
            time.sleep(.5)
    now = time.time()
    shown = lambda w: call("GET", "/api/limits")[1].get("claude", {}).get(w)

    # a reset no window can have: real readings would count as "an earlier window" for ever
    call("POST", "/api/limits/claude", {"five_hour": {"used_percentage": 100, "resets_at": 253402300799}, "seven_day": {"used_percentage": 100, "resets_at": 253402300799}})
    call("POST", "/api/limits/claude", {"five_hour": {"used_percentage": 12, "resets_at": now + 3600}, "seven_day": {"used_percentage": 34, "resets_at": now + 86400}})
    check("a reset far past the 5 hours doesn't stay", (shown("five_hour") or {}).get("used_percentage") == 12, shown("five_hour"))
    check("a reset far past the week doesn't stay", (shown("seven_day") or {}).get("used_percentage") == 34, shown("seven_day"))
    call("POST", "/api/limits/claude", {"five_hour": {"used_percentage": 50, "resets_at": now + 18000 + 86400}})
    check("a day past the window: the number stays, the time doesn't", (shown("five_hour") or {}).get("used_percentage") == 50 and not (shown("five_hour") or {}).get("resets_at"), shown("five_hour"))
    call("POST", "/api/limits/claude", {"five_hour": {"used_percentage": 20, "resets_at": now + 3600}, "seven_day": {"used_percentage": 40, "resets_at": now + 604800}})
    check("right at the end of the window: taken", (shown("five_hour") or {}).get("used_percentage") == 20 and (shown("seven_day") or {}).get("used_percentage") == 40, (shown("five_hour"), shown("seven_day")))

    # a path on another machine, with a short-name mark that makes .NET ask the file system
    for cwd in ("\\\\192.0.2.1\\x\\a~1", "//192.0.2.1/x/a~1"):
        t = time.time()
        code, _ = call("POST", "/api/notify", {"title": "t", "message": "m", "source": "claude", "agent_cwd": cwd})
        took = time.time() - t
        check(f"{cwd}: answered at once", code == 200 and took < 2, (code, round(took, 1)))
    t = time.time()
    code, _ = call("POST", "/api/hooks/claude", {"hook_event_name": "Stop", "cwd": "\\\\192.0.2.1\\x\\b~1", "session_id": "s"})
    check("a Stop from such a folder: answered at once", code == 200 and time.time() - t < 2, (code, round(time.time() - t, 1)))
    netstat = subprocess.run(["netstat", "-n"], capture_output=True, text=True).stdout
    check("no connection to that machine", "192.0.2.1" not in netstat)
finally:
    hub.kill()
    hub.wait(10)
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(home, ignore_errors=True)

print("payload bounds tests: " + ("OK" if not fails else f"{fails} FAILED"), flush=True)
sys.exit(1 if fails else 0)
