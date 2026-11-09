"""One request can't hold up the hub, and a task can't grow without bound: batches, milestones, meta, task count; a number too big for the rate doesn't stop saving. Own hub. Argument: TaskHubHost.exe."""
import json, os, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request

EXE = sys.argv[1]
PORT = 47858
URL = f"http://127.0.0.1:{PORT}"
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
SLOW = 2.0   # s: what a panel's request may wait while someone else's big one is handled


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None, timeout=150):
    data = None if body is None else json.dumps(body, separators=(",", ":")).encode()
    r = urllib.request.Request(URL + path, method=method, data=data, headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=timeout) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def during(path, body):
    """Sends the request; returns (seconds it took, status, longest wait of another request meanwhile)."""
    res = {}

    def send():
        t = time.time()
        try:
            res["code"] = call("POST", path, body)[0]
        except OSError as e:
            res["code"] = repr(e)
        res["took"] = time.time() - t

    th = threading.Thread(target=send)
    th.start()
    worst = 0
    while th.is_alive():
        t = time.time()
        try:
            call("GET", "/api/tasks?brief=1", timeout=150)
        except OSError:
            pass
        worst = max(worst, time.time() - t)
        time.sleep(.02)
    th.join()
    return res["took"], res["code"], worst


data = tempfile.mkdtemp(prefix="taskhub-bounds2-data-")
home = tempfile.mkdtemp(prefix="taskhub-bounds2-home-")
with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"toasts": False, "autostart": False, "update_check": False, "toast_rate": 0, "toast_task_gap": 0, "language": "en"}, f)
hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=dict(os.environ, TASKHUB_TEST_HOME=home))
try:
    for _ in range(60):
        try:
            call("GET", "/api/health", timeout=2)
            break
        except OSError:
            time.sleep(.5)

    # ---- one big request
    call("POST", "/api/tasks", {"id": "big", "name": "big", "widgets": [{"id": "c", "type": "chart", "series": ["a"]}]})
    took, code, worst = during("/api/tasks/big/log", {"lines": ["a"] * 450000})
    check("a 2 MB log batch doesn't hold up the hub", worst < SLOW, f"other request waited {worst:.1f} s, batch {took:.1f} s, status {code}")
    log = call("GET", "/api/tasks/big")[1].get("log", [])
    check("...the log keeps its limit", len(log) <= 400, len(log))
    took, code, worst = during("/api/tasks/big/update", {"widgets": {"c": [[1]] * 450000}})
    check("a 2 MB chart batch doesn't hold up the hub", worst < SLOW, f"other request waited {worst:.1f} s, batch {took:.1f} s, status {code}")
    w = call("GET", "/api/tasks/big")[1]["widgets"][0]
    check("...the chart keeps its limit", len(w["points"]) <= w["max_points"], len(w["points"]))

    # ---- what a task may hold
    code, _ = call("POST", "/api/tasks", {"id": "ms", "name": "ms", "notify": {"milestones": [25, 50, 75]}})
    check("a few milestones are fine", code == 200, code)
    code, _ = call("POST", "/api/tasks", {"id": "ms2", "name": "ms", "notify": {"milestones": [i / 100 for i in range(100, 40100)]}})
    check("40000 milestones are refused", code == 400, code)
    code, _ = call("POST", "/api/tasks", {"id": "m1", "name": "m", "meta": {"x": "a" * 70000}})
    check("meta over the limit at the start: refused", code == 400, code)
    call("POST", "/api/tasks", {"id": "m2", "name": "m", "meta": {"x": "a" * 40000}})
    code, _ = call("POST", "/api/tasks/m2/update", {"meta": {"y": "a" * 40000}})
    check("meta growing over the limit by updates: refused", code == 400, code)
    code, _ = call("POST", "/api/tasks/m2/update", {"meta": {"x": "b"}})
    check("...a small change is fine", code == 200, code)
    code, _ = call("POST", "/api/tasks", {"id": "mt", "name": "m"})
    code, _ = call("POST", "/api/tasks/mt/update", {"metrics": {f"k{i}": i for i in range(300)}})
    check("300 metrics are refused", code == 400, code)
    code, _ = call("POST", "/api/tasks", {"id": "lk", "name": "l", "links": ["https://example.org"] * 100})
    check("100 links are refused", code == 400, code)
    code, _ = call("POST", "/api/tasks", {"id": "ct", "name": "c", "controls": [{"id": f"c{i}"} for i in range(100)]})
    check("100 controls are refused", code == 400, code)
    code, _ = call("POST", "/api/tasks", {"id": "wv", "name": "w", "widgets": [{"id": "t", "type": "table", "columns": ["a"], "value": [["a" * 70000]]}]})
    check("a widget value over the limit is refused", code == 400, code)

    # ---- a rate too big for a number
    call("POST", "/api/tasks", {"id": "r", "name": "r", "total": 10})
    call("POST", "/api/tasks/r/update", {"current": -1e308})
    time.sleep(6)
    call("POST", "/api/tasks/r/update", {"current": 1e308})
    code, r = call("GET", "/api/tasks/r")
    check("a rate too big for a number: the task can be read", code == 200, code)
    state = os.path.join(data, "state.json")
    before = os.path.getmtime(state) if os.path.exists(state) else 0
    call("POST", "/api/tasks/r/update", {"status": "after"})
    time.sleep(7)
    check("...and the state is still saved", os.path.exists(state) and os.path.getmtime(state) > before)
    with open(state, encoding="utf-8") as f:
        check("...what was saved reads back", any(t["id"] == "r" for t in json.load(f)["tasks"]))

    # ---- number of tasks
    last = None
    for i in range(2100):
        last = call("POST", "/api/tasks", {"id": f"n{i}", "name": "n"})[0]
        if last != 200:
            break
    check("the active tasks have a limit", last == 400, (i, last))
    code, _ = call("POST", "/api/tasks", {"id": "n0", "name": "again"})
    check("...an existing id can still start again", code == 200, code)
finally:
    hub.kill()
    hub.wait()
    time.sleep(.5)
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(home, ignore_errors=True)

print("BOUNDS:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
