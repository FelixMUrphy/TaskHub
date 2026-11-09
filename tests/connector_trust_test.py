"""A connector's task can't be pointed elsewhere from the local API: its reference (meta.<connector>) and its buttons are the connector's, and a press acts on the machine in the task's id. Adopt after a restart still works. Own hub, own mock, temp folders only. Argument: TaskHubHost.exe."""
import json, os, re, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

EXE = sys.argv[1]
PORT, MOCK = 47846, 47845
URL = f"http://127.0.0.1:{PORT}"
KEY = "rpa_TRUSTWRITE" + "W" * 30
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
lock = threading.RLock()
pods = {"pA": "RUNNING", "pB": "RUNNING"}
stopped = []


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


class Runpod(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def end_headers(self):
        self.send_header("Connection", "close")
        super().end_headers()

    def reply(self, code, obj):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self.route("GET")

    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length") or 0))
        self.route("POST")

    def pod(self, pid):
        run = pods[pid] == "RUNNING"
        return {"id": pid, "name": pid.lower(), "status": pods[pid], "image": "x", "cost": 1.0, "gpu": {"id": "NVIDIA RTX A5000", "count": 1},
                "dataCenterId": "EU-RO-1", "ports": [], "mounts": {"persistent": {"size": 20, "path": "/workspace"}},
                "startedAt": "2026-10-31T10:00:00Z", "actions": ["stop"] if run else ["start"]}

    def route(self, m):
        p = self.path.split("?")[0]
        with lock:
            if self.headers.get("Authorization") != "Bearer " + KEY:
                return self.reply(401, {"detail": "invalid api key"})
            if p == "/graphql":
                return self.reply(200, {"data": {"myself": {"clientBalance": 50.0, "currentSpendPerHr": 2.0, "spendLimit": 80, "billingMode": "PREPAID"}}})
            if p == "/v2/pods":
                return self.reply(200, {"pods": [self.pod(x) for x in pods], "pagination": {"hasNextPage": False}})
            if p == "/v2/billing/pods":
                return self.reply(200, {"records": []})
            mm = re.match(r"^/v2/pods/([^/]+)$", p)
            if mm and m == "GET":
                return self.reply(200, self.pod(mm.group(1))) if mm.group(1) in pods else self.reply(404, {"detail": "not found"})
            mm = re.match(r"^/v2/pods/([^/]+)/action$", p)
            if mm and m == "POST":
                if mm.group(1) not in pods:
                    return self.reply(404, {"detail": "pod not found"})
                stopped.append(mm.group(1))
                pods[mm.group(1)] = "EXITED"
                return self.reply(200, self.pod(mm.group(1)))
        self.reply(404, {"detail": "no route"})


def call(method, path, body=None, timeout=30):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(), headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=timeout) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def until(pred, seconds=15):
    end = time.time() + seconds
    while time.time() < end:
        if pred():
            return True
        time.sleep(.1)
    return False


def task(tid):
    c, t = call("GET", "/api/tasks/" + tid)
    return t if c == 200 else {}


def ctl(t):
    return [c["id"] for c in t.get("controls", [])]


def start(data, home):
    h = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=dict(os.environ, TASKHUB_TEST_HOME=home))
    for _ in range(60):
        try:
            call("GET", "/api/health", timeout=2)
            return h
        except OSError:
            time.sleep(.5)
    return h


NO_VAULT = vault.probe()
if NO_VAULT:
    print(f"SKIP connector trust: Credential Manager unavailable here ({NO_VAULT})")
    raise SystemExit(0)

root = tempfile.mkdtemp(prefix="taskhub-connector-trust-")
ThreadingHTTPServer.request_queue_size = 64
server = ThreadingHTTPServer(("127.0.0.1", MOCK), Runpod)
threading.Thread(target=server.serve_forever, daemon=True).start()
data, home = tempfile.mkdtemp(dir=root), tempfile.mkdtemp(dir=root)
with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"toasts": False, "autostart": False, "update_check": False, "language": "en", "connectors": {"runpod": {"api": f"http://127.0.0.1:{MOCK}"}}}, f)
h = start(data, home)
try:
    call("POST", "/api/connectors/runpod", {"enabled": True, "key": KEY, "list_interval": 1, "detail_interval": 1, "billing_interval": 3600})
    check("(both machines have their tasks, with Stop)", until(lambda: "stop" in ctl(task("rp-pA")) and "stop" in ctl(task("rp-pB"))), (task("rp-pA"), task("rp-pB")))

    # the reference
    s, _ = call("POST", "/api/tasks/rp-pA/update", {"meta": {"runpod": {"id": "pB", "name": "pB"}, "mine": 1}})
    check("meta.runpod over the API isn't taken", (task("rp-pA").get("meta", {}).get("runpod") or {}).get("id") == "pA", (s, task("rp-pA").get("meta")))
    check("...the caller's own meta is", task("rp-pA").get("meta", {}).get("mine") == 1, task("rp-pA").get("meta"))

    # the buttons
    shell = {"id": "x", "label": "Stop", "kind": "shell", "command": "whoami"}
    for body, what in (({"add_controls": [shell]}, "add_controls"), ({"controls": [shell]}, "controls"), ({"remove_controls": ["stop"]}, "remove_controls")):
        s, _ = call("POST", "/api/tasks/rp-pB/update", body)
        check(f"{what} on a connector's task: refused", s == 403, s)
        check("...its buttons are as they were", "x" not in ctl(task("rp-pB")) and "stop" in ctl(task("rp-pB")), ctl(task("rp-pB")))
    s, _ = call("POST", "/api/tasks", {"id": "rp-pB", "name": "not a machine", "controls": [shell]})
    check("registering over a connector's task: refused", s == 403, s)
    check("...it stays the connector's", task("rp-pB").get("source") == "runpod" and "x" not in ctl(task("rp-pB")), task("rp-pB").get("source"))

    # a task made to look like a machine
    s, t = call("POST", "/api/tasks", {"id": "rp-fake", "name": "fake", "source": "runpod", "meta": {"runpod": {"id": "pB", "name": "pB"}}})
    s2, _ = call("POST", "/api/tasks/rp-fake/controls/stop", {})
    check("a task made to look like a machine stops nothing", s2 in (400, 404) and "pB" not in stopped, (s, s2, stopped))
    call("POST", "/api/tasks/rp-fake/finish", {"state": "cancelled", "notify": False})

    # a client's own task isn't touched by any of this
    s, t = call("POST", "/api/tasks", {"name": "mine", "source": "script", "meta": {"github": {"x": 1}, "ok": 2}})
    s2, _ = call("POST", f"/api/tasks/{t['id']}/update", {"add_controls": [{"id": "go", "label": "Go"}]})
    check("a client's own task keeps working", s2 == 200 and "go" in ctl(task(t["id"])) and task(t["id"]).get("meta", {}).get("ok") == 2, (s2, task(t["id"])))
    call("POST", f"/api/tasks/{t['id']}/finish", {"state": "done", "notify": False})

    # a connector's id can't be taken: deleted and registered again, or registered before the connector sees the machine
    s, _ = call("DELETE", "/api/tasks/rp-pB?force=1")
    s2, _ = call("POST", "/api/tasks", {"id": "rp-pB", "name": "not a machine", "source": "cli", "controls": [shell]})
    check("(deleted and registered by a client)", s == 200 and s2 == 200, (s, s2))
    check("the connector takes its machine's task back", until(lambda: task("rp-pB").get("source") == "runpod" and "x" not in ctl(task("rp-pB")) and "stop" in ctl(task("rp-pB"))), task("rp-pB"))
    s2, _ = call("POST", "/api/tasks", {"id": "rp-pC", "name": "first", "source": "cli", "controls": [shell]})
    with lock:
        pods["pC"] = "RUNNING"
    check("a task registered before the machine is seen: the machine's task replaces it", until(lambda: task("rp-pC").get("source") == "runpod" and "x" not in ctl(task("rp-pC")) and "stop" in ctl(task("rp-pC"))), task("rp-pC"))

    # a source is only a word a client chooses: it makes nothing the connector's
    s, _ = call("POST", "/api/tasks", {"id": "own-job", "name": "backup", "source": "aws"})
    s2, _ = call("POST", "/api/tasks", {"id": "own-job", "name": "backup", "source": "aws"})
    check("a client's task with a connector's name as source can run again", s == 200 and s2 == 200, (s, s2))
    s, _ = call("POST", "/api/tasks/own-job/update", {"add_controls": [{"id": "go", "label": "Go"}]})
    check("...and keeps its buttons its own", s == 200 and "go" in ctl(task("own-job")), s)
    call("POST", "/api/tasks/own-job/finish", {"state": "done", "notify": False})

    # a press acts on the machine in the task's id
    s, _ = call("POST", "/api/tasks/rp-pA/controls/stop", {})
    check("Stop on alpha's task stops alpha", s == 200 and stopped == ["pA"], (s, stopped))

    # Adopt: after a restart a machine that went away meanwhile is still recognised and finished
    time.sleep(7)   # state.json is written
    call("POST", "/api/connectors/runpod", {"enabled": False})
    time.sleep(.5)
    h.kill()
    h.wait()
    with lock:
        del pods["pB"]
    h = start(data, home)
    call("POST", "/api/connectors/runpod", {"enabled": True})
    check("after a restart: a machine that went away is finished as terminated", until(lambda: task("rp-pB").get("state") == "done" and "terminated" in str(task("rp-pB").get("message", "")).lower(), 20), task("rp-pB"))
finally:
    call("POST", "/api/connectors/runpod", {"forget_key": True})
    h.kill()
    h.wait()
    server.shutdown()
    shutil.rmtree(root, ignore_errors=True)

print("CONNECTOR TRUST:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
