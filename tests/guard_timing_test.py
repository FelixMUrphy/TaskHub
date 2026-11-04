"""The balance guard is in time when the way to $0 is shorter than the reading interval. A mock Runpod spends real money by the clock, scaled: $360/h, a reading interval of 30 s (the service's real default is 600 s at far lower burns), balance booked continuously or in 30 s steps. Own hub, own mock, temp folders only. Argument: TaskHubHost.exe."""
import json, os, re, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

EXE = sys.argv[1]
PORT, MOCK = 47856, 47855
URL = f"http://127.0.0.1:{PORT}"
KEY = "rpa_TIMINGWRITE" + "W" * 30
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
lock = threading.RLock()


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


class World:
    """The account: what it truly has, what the service shows, and every stop that came in."""

    def __init__(self, start, burn=360.0, step=0, fail_stops=0):
        self.start, self.burn, self.step, self.fail_stops = start, burn, step, fail_stops
        self.t0 = None             # the first reading starts the clock
        self.frozen = None         # the true balance from the moment the machine stopped
        self.running = True
        self.topup = 0.0
        self.booked_at = None      # a scripted figure, see set_booked
        self.booked_value = None
        self.stops = []            # (seconds, true balance, answered)
        self.reads = []

    def clock(self):
        return time.time() - self.t0 if self.t0 else 0

    def true_balance(self):
        if self.frozen is not None:
            return self.frozen + self.topup
        return self.start - self.burn * self.clock() / 3600 + self.topup

    def shown(self):
        if self.booked_value is not None:
            return self.booked_value
        if not self.step:
            return self.true_balance()
        return self.start - self.burn * (self.clock() // self.step) * self.step / 3600 + self.topup

    def set_booked(self, value):
        with lock:
            self.booked_value = value


world = None


class Runpod(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def end_headers(self):
        self.send_header("Connection", "close")
        super().end_headers()

    def reply(self, code, obj=None):
        data = json.dumps(obj).encode() if obj is not None else b""
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self.route("GET", None)

    def do_POST(self):
        self.route("POST", json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}"))

    def pod(self):
        return {"id": "pA", "name": "alpha", "status": "RUNNING" if world.running else "EXITED", "image": "x", "cost": world.burn,
                "gpu": {"id": "NVIDIA RTX A5000", "count": 1}, "dataCenterId": "EU-RO-1", "ports": [],
                "mounts": {"persistent": {"size": 20, "path": "/workspace"}}, "startedAt": "2026-10-31T10:00:00Z",
                "actions": ["stop"] if world.running else ["start"]}

    def route(self, m, body):
        p = self.path.split("?")[0]
        with lock:
            if self.headers.get("Authorization") != "Bearer " + KEY:
                return self.reply(401, {"detail": "invalid api key"})
            if m == "POST" and p == "/graphql":
                if world.t0 is None:
                    world.t0 = time.time()
                world.reads.append(world.clock())
                return self.reply(200, {"data": {"myself": {"clientBalance": round(world.shown(), 4), "currentSpendPerHr": world.burn if world.running else 0, "spendLimit": 80}}})
            if m == "GET" and p == "/v2/pods":
                return self.reply(200, {"pods": [self.pod()], "pagination": {"hasNextPage": False}})
            if m == "GET" and p == "/v2/billing/pods":
                return self.reply(200, {"records": []})
            if m == "GET" and p == "/v2/pods/pA":
                return self.reply(200, self.pod())
            mm = re.match(r"^/v2/pods/([^/]+)/action$", p)
            if m == "POST" and mm:
                if mm.group(1) != "pA":
                    return self.reply(404, {"detail": "pod not found"})
                if world.fail_stops > 0:
                    world.fail_stops -= 1
                    world.stops.append((world.clock(), world.true_balance(), 500))
                    return self.reply(500, {"detail": "internal error"})
                world.stops.append((world.clock(), world.true_balance(), 200))
                world.frozen = world.true_balance() - world.topup
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


def scenario(w, seconds, grace=0.02, until=None):
    """A hub of its own, the guard on against this account; returns when `until` holds or the time is up."""
    global world
    world = w
    data, home = tempfile.mkdtemp(dir=root), tempfile.mkdtemp(dir=root)
    with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
        json.dump({"toasts": False, "autostart": False, "update_check": False, "language": "en", "connectors": {"runpod": {"api": f"http://127.0.0.1:{MOCK}"}}}, f)
    h = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=dict(os.environ, TASKHUB_TEST_HOME=home))
    try:
        for _ in range(60):
            try:
                call("GET", "/api/health", timeout=2)
                break
            except OSError:
                time.sleep(.5)
        s, r = call("POST", "/api/connectors/runpod", {"enabled": True, "key": KEY, "list_interval": 1, "detail_interval": 1, "billing_interval": 30,
                                                        "guard": True, "guard_warn": 20, "guard_stop": 2, "guard_grace_min": grace, "guard_snooze_min": 0.02})
        if s != 200:
            check("the guard takes its settings", False, (s, r))
        end = time.time() + seconds
        while time.time() < end and not (until and until()):
            time.sleep(.1)
        return call("GET", "/api/connectors/runpod")[1].get("guard") or {}
    finally:
        call("POST", "/api/connectors/runpod", {"forget_key": True})
        h.kill()
        h.wait()
        time.sleep(.3)


NO_VAULT = vault.probe()
if NO_VAULT:
    print(f"SKIP guard timing: Credential Manager unavailable here ({NO_VAULT})")
    raise SystemExit(0)

root = tempfile.mkdtemp(prefix="taskhub-guard-timing-")
ThreadingHTTPServer.request_queue_size = 64
server = ThreadingHTTPServer(("127.0.0.1", MOCK), Runpod)
threading.Thread(target=server.serve_forever, daemon=True).start()
try:
    # $2.90, $0.10 a second, stop at $2: past the threshold after 9 s, at $0 after 29 s, and the next reading is due after 30 s
    w = World(2.9)
    scenario(w, 45, until=lambda: w.stops)
    check("a balance that moves: stopped with money left", w.stops and w.stops[0][1] > 0, w.stops)

    # the same, booked in 30 s steps: the figure stays $2.90 for all of that time, so only carrying it down sees the threshold
    w = World(2.9, step=30)
    scenario(w, 45, until=lambda: w.stops)
    check("a balance booked in steps: stopped with money left", w.stops and w.stops[0][1] > 0, w.stops)
    check("...the readings came sooner than the interval", len([r for r in w.reads if 1 < r < 25]) >= 3, w.reads)

    # far from the threshold nothing changes: the configured interval
    w = World(50, burn=1.0)
    scenario(w, 8)
    check("far from the threshold: no extra readings", len(w.reads) <= 3 and not w.stops, w.reads)

    # a stop that fails keeps being tried about a reading apart, not an interval
    w = World(1.0, fail_stops=2)
    scenario(w, 45, until=lambda: not w.running)
    gaps = [b[0] - a[0] for a, b in zip(w.stops, w.stops[1:])]
    check("a failed stop: tried again soon, each time", len(w.stops) == 3 and gaps and max(gaps) < 10 and not w.running, w.stops)

    # a pending stop that came from the estimate: a step above the estimate is no top-up, money coming in is
    w = World(2.9, step=1000)
    def after_cross():
        if w.clock() > 11:
            w.set_booked(2.5)
        return w.stops
    scenario(w, 45, grace=0.5, until=after_cross)
    check("a booked step above the estimate doesn't cancel the stop", bool(w.stops), w.reads[-5:])

    w = World(2.9, step=1000)
    def topped():
        if w.clock() > 11:
            w.set_booked(50)
        return False
    g = scenario(w, 18, grace=0.5, until=topped)
    check("a top-up cancels it", not w.stops and g.get("pending") is None, (w.stops, g))
finally:
    server.shutdown()
    shutil.rmtree(root, ignore_errors=True)

print("GUARD TIMING:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
