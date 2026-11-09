"""One refused action doesn't take auto-stop and the Stop button away for a day. A mock Runpod answers 403 as told: the service's JSON or a challenge page. Own hub, own mock, temp folders only. Argument: TaskHubHost.exe."""
import json, os, re, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

EXE = sys.argv[1]
PORT, MOCK = 47852, 47851
URL = f"http://127.0.0.1:{PORT}"
KEY = "rpa_FORBIDWRITE" + "W" * 30
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
lock = threading.RLock()
PAGE = b"<html><body>Checking your browser</body></html>"


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


class World:
    def __init__(self, balance=50.0, stop=(), probe=()):
        self.balance = balance
        self.stop_answers = list(stop)     # "json" / "page" for the next stops, then it works
        self.probe_answers = list(probe)   # the same for the next probes
        self.running = True
        self.stops = []


world = None


class Runpod(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def end_headers(self):
        self.send_header("Connection", "close")
        super().end_headers()

    def reply(self, code, obj=None, raw=None):
        data = raw if raw is not None else json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "text/html" if raw is not None else "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def refuse(self, how):
        return self.reply(403, raw=PAGE) if how == "page" else self.reply(403, {"detail": "forbidden for this key"})

    def do_GET(self):
        self.route("GET", None)

    def do_POST(self):
        self.route("POST", json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}"))

    def pod(self):
        return {"id": "pA", "name": "alpha", "status": "RUNNING" if world.running else "EXITED", "image": "x", "cost": 1.0,
                "gpu": {"id": "NVIDIA RTX A5000", "count": 1}, "dataCenterId": "EU-RO-1", "ports": [],
                "mounts": {"persistent": {"size": 20, "path": "/workspace"}}, "startedAt": "2026-10-31T10:00:00Z",
                "actions": ["stop"] if world.running else ["start"]}

    def route(self, m, body):
        p = self.path.split("?")[0]
        with lock:
            if self.headers.get("Authorization") != "Bearer " + KEY:
                return self.reply(401, {"detail": "invalid api key"})
            if m == "POST" and p == "/graphql":
                return self.reply(200, {"data": {"myself": {"clientBalance": world.balance, "currentSpendPerHr": 1.0 if world.running else 0, "spendLimit": 80}}})
            if m == "GET" and p == "/v2/pods":
                return self.reply(200, {"pods": [self.pod()], "pagination": {"hasNextPage": False}})
            if m == "GET" and p == "/v2/billing/pods":
                return self.reply(200, {"records": []})
            if m == "GET" and p == "/v2/pods/pA":
                return self.reply(200, self.pod())
            mm = re.match(r"^/v2/pods/([^/]+)/action$", p)
            if m == "POST" and mm:
                if mm.group(1) != "pA":
                    if world.probe_answers:
                        return self.refuse(world.probe_answers.pop(0))
                    return self.reply(404, {"detail": "pod not found"})
                if world.stop_answers:
                    world.stops.append("refused")
                    return self.refuse(world.stop_answers.pop(0))
                world.stops.append("ok")
                world.running = False
                return self.reply(200, self.pod())
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


def can_act():
    return call("GET", "/api/connectors/runpod")[1].get("can_act")


def stop_button():
    c = call("GET", "/api/tasks/rp-pA")
    return c[0] == 200 and any(x["id"] == "stop" for x in c[1].get("controls", []))


class Hub:
    def __init__(self, w, **guard):
        global world
        world = w
        self.data, home = tempfile.mkdtemp(dir=root), tempfile.mkdtemp(dir=root)
        with open(os.path.join(self.data, "config.json"), "w", encoding="utf-8") as f:
            json.dump({"log_level": "debug", "toasts": False, "autostart": False, "update_check": False, "language": "en", "connectors": {"runpod": {"api": f"http://127.0.0.1:{MOCK}"}}}, f)
        self.h = subprocess.Popen([EXE, "--port", str(PORT), "--data", self.data, "--no-tray"], env=dict(os.environ, TASKHUB_TEST_HOME=home))
        for _ in range(60):
            try:
                call("GET", "/api/health", timeout=2)
                break
            except OSError:
                time.sleep(.5)
        self.status = call("POST", "/api/connectors/runpod", dict({"enabled": True, "key": KEY, "list_interval": 1, "detail_interval": 1, "billing_interval": 1}, **guard))[0]

    def log(self):
        with open(os.path.join(self.data, "taskhub.log"), encoding="utf-8", errors="replace") as f:
            return f.read()

    def __enter__(self):
        return self

    def __exit__(self, *a):
        call("POST", "/api/connectors/runpod", {"forget_key": True})
        self.h.kill()
        self.h.wait()
        time.sleep(.3)


GUARD = {"guard": True, "guard_warn": 20, "guard_stop": 2, "guard_grace_min": 0.02, "guard_snooze_min": 0.02}

NO_VAULT = vault.probe()
if NO_VAULT:
    print(f"SKIP refused actions: Credential Manager unavailable here ({NO_VAULT})")
    raise SystemExit(0)

root = tempfile.mkdtemp(prefix="taskhub-guard-403-")
ThreadingHTTPServer.request_queue_size = 64
server = ThreadingHTTPServer(("127.0.0.1", MOCK), Runpod)
threading.Thread(target=server.serve_forever, daemon=True).start()
try:
    # the guard's stop refused once, by the service: said out loud, asked again, then the machine stops after all
    w = World(balance=1.0, stop=["json"])
    with Hub(w, **GUARD) as hub:
        check("a refused auto-stop: the machine is stopped after all", until(lambda: not w.running, 20), w.stops)
        check("...actions are back", until(lambda: can_act() is True), can_act())
        log = hub.log()
        check("...the refusal was said out loud", "auto-stop and the Stop button are off" in log)
        check("...and the way back", "auto-stop and the Stop button are back" in log)

    # the manual Stop refused once: the button comes back
    w = World(stop=["json"])
    with Hub(w, **GUARD) as hub:
        check("(the button is there)", until(stop_button))
        s, _ = call("POST", "/api/tasks/rp-pA/controls/stop", {})
        check("a refused Stop: an error to the one who pressed", s == 409, s)
        check("...the button comes back by itself", until(lambda: can_act() is True and stop_button()), (can_act(), stop_button()))
        s, _ = call("POST", "/api/tasks/rp-pA/controls/stop", {})
        check("...and works", s == 200 and until(lambda: not w.running), s)

    # a challenge page on Stop says nothing about the key
    w = World(stop=["page"])
    with Hub(w, **GUARD) as hub:
        check("(the button is there)", until(stop_button))
        s, _ = call("POST", "/api/tasks/rp-pA/controls/stop", {})
        check("a page instead of an answer: an error", s == 409, s)
        check("...actions stay as they were", can_act() is True and stop_button(), can_act())
        check("...nothing said about auto-stop", "auto-stop and the Stop button are off" not in hub.log())

    # the probe refused once, by the service: asked again in minutes (scaled: seconds), not tomorrow
    w = World(probe=["json"])
    with Hub(w, **GUARD):
        check("a refused probe: asked again soon", until(lambda: can_act() is True), can_act())

    # a page on the probe: not "read-only"
    w = World(probe=["page"])
    with Hub(w, **GUARD):
        time.sleep(1.5)
        check("a page on the probe doesn't say the key can't act", can_act() is not False, can_act())

    # a key that really can't act stays that way, and no alarm: it never could
    w = World(probe=["json"] * 1000)
    with Hub(w, **GUARD) as hub:
        check("a read-only key stays read-only", until(lambda: can_act() is False) and (time.sleep(3) or can_act() is False), can_act())
        check("...without an alarm", "auto-stop and the Stop button are off" not in hub.log())
finally:
    server.shutdown()
    shutil.rmtree(root, ignore_errors=True)

print("GUARD 403:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
