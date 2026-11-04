"""What the guard doesn't know it doesn't take as a fact: an answer without the machine list isn't "no machines", a failed billing-mode question is asked again and said at a stop, Vast's idle stop waits for a GPU seen busy. Own hub, own mocks, temp folders only. Argument: TaskHubHost.exe."""
import json, os, re, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

EXE = sys.argv[1]
PORT, RP, VA = 47850, 47849, 47848
URL = f"http://127.0.0.1:{PORT}"
RP_KEY = "rpa_UNKNOWNWRITE" + "W" * 30
VA_KEY = "va_UNKNOWNWRITE" + "w" * 50
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
lock = threading.RLock()


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


class World:
    def __init__(self, balance=50.0, gpu=50.0, mode="PREPAID"):
        self.balance, self.gpu, self.mode = balance, gpu, mode
        self.running = True
        self.list_answer = None    # what the list says instead of the truth
        self.mode_asked = 0
        self.stops = 0


world = None


class Base(BaseHTTPRequestHandler):
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
        self.route("GET", None)

    def body(self):
        return json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")

    def do_POST(self):
        self.route("POST", self.body())

    def do_PUT(self):
        self.route("PUT", self.body())


class Runpod(Base):
    def pod(self):
        return {"id": "pA", "name": "alpha", "status": "RUNNING" if world.running else "EXITED", "image": "x", "cost": 1.0,
                "gpu": {"id": "NVIDIA RTX A5000", "count": 1}, "dataCenterId": "EU-RO-1", "ports": [],
                "mounts": {"persistent": {"size": 20, "path": "/workspace"}}, "startedAt": "2026-10-31T10:00:00Z",
                "actions": ["stop"] if world.running else ["start"]}

    def route(self, m, body):
        p = self.path.split("?")[0]
        with lock:
            if self.headers.get("Authorization") != "Bearer " + RP_KEY:
                return self.reply(401, {"detail": "invalid api key"})
            if m == "POST" and p == "/graphql":
                if "billingMode" in body.get("query", ""):
                    world.mode_asked += 1
                    if world.mode is None:
                        return self.reply(200, {"errors": [{"message": "try later"}]})
                    return self.reply(200, {"data": {"myself": {"billingMode": world.mode}}})
                return self.reply(200, {"data": {"myself": {"clientBalance": world.balance, "currentSpendPerHr": 1.0 if world.running else 0, "spendLimit": 80}}})
            if m == "GET" and p == "/v2/pods":
                if world.list_answer is not None:
                    return self.reply(200, world.list_answer)
                return self.reply(200, {"pods": [self.pod()], "pagination": {"hasNextPage": False}})
            if m == "GET" and p == "/v2/billing/pods":
                return self.reply(200, {"records": []})
            if m == "GET" and p == "/v2/pods/pA":
                return self.reply(200, self.pod())
            mm = re.match(r"^/v2/pods/([^/]+)/action$", p)
            if m == "POST" and mm:
                if mm.group(1) != "pA":
                    return self.reply(404, {"detail": "pod not found"})
                world.stops += 1
                world.running = False
                return self.reply(200, self.pod())
        self.reply(404, {"detail": "no route"})


class Vast(Base):
    def inst(self):
        return {"id": 201, "label": "render-box", "actual_status": "running" if world.running else "exited", "intended_status": "running" if world.running else "stopped",
                "cur_state": "running" if world.running else "stopped", "gpu_name": "RTX 4090", "num_gpus": 1, "dph_total": 0.4, "start_date": time.time() - 600,
                "gpu_util": world.gpu, "disk_space": 60.0, "storage_cost": 0.3}

    def route(self, m, body):
        p = self.path.split("?")[0]
        with lock:
            if self.headers.get("Authorization") != "Bearer " + VA_KEY:
                return self.reply(401, {"success": False, "msg": "Invalid user key"})
            if m == "GET" and p == "/api/v1/instances/":
                if world.list_answer is not None:
                    return self.reply(200, world.list_answer)
                return self.reply(200, {"instances": [self.inst()], "next_token": None})
            if m == "GET" and p == "/api/v0/charges/":
                return self.reply(200, {"results": [], "next_token": None})
            if m == "GET" and p == "/api/v0/users/current/":
                return self.reply(200, {"balance": 0.0, "credit": world.balance})
            mm = re.match(r"^/api/v0/instances/(\d+)/$", p)
            if mm:
                if m == "GET":
                    return self.reply(200, {"instances": self.inst()})
                if int(mm.group(1)) != 201:
                    return self.reply(200, {"success": False, "msg": "Instance not found."})
                if (body or {}).get("state") == "stopped":
                    world.stops += 1
                    world.running = False
                return self.reply(200, {"success": True})
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


class Hub:
    def __init__(self, w, conn, **cfg):
        global world
        world = w
        self.conn = conn
        self.data, home = tempfile.mkdtemp(dir=root), tempfile.mkdtemp(dir=root)
        with open(os.path.join(self.data, "config.json"), "w", encoding="utf-8") as f:
            json.dump({"log_level": "debug", "toasts": False, "autostart": False, "update_check": False, "language": "en",
                       "connectors": {"runpod": {"api": f"http://127.0.0.1:{RP}"}, "vastai": {"api": f"http://127.0.0.1:{VA}"}}}, f)
        self.h = subprocess.Popen([EXE, "--port", str(PORT), "--data", self.data, "--no-tray"], env=dict(os.environ, TASKHUB_TEST_HOME=home))
        for _ in range(60):
            try:
                call("GET", "/api/health", timeout=2)
                break
            except OSError:
                time.sleep(.5)
        base = {"enabled": True, "key": RP_KEY if conn == "runpod" else VA_KEY, "list_interval": 1, "detail_interval": 1, "billing_interval": 1}
        self.status = call("POST", f"/api/connectors/{conn}", dict(base, **cfg))
        check(f"({conn} takes its settings)", self.status[0] == 200, self.status)

    def log(self):
        with open(os.path.join(self.data, "taskhub.log"), encoding="utf-8", errors="replace") as f:
            return f.read()

    def __enter__(self):
        return self

    def __exit__(self, *a):
        call("POST", f"/api/connectors/{self.conn}", {"forget_key": True})
        self.h.kill()
        self.h.wait()
        time.sleep(.3)


GUARD = {"guard": True, "guard_warn": 20, "guard_stop": 2, "guard_grace_min": 0.02, "guard_snooze_min": 0.02}

NO_VAULT = vault.probe()
if NO_VAULT:
    print(f"SKIP unknown states: Credential Manager unavailable here ({NO_VAULT})")
    raise SystemExit(0)

root = tempfile.mkdtemp(prefix="taskhub-guard-unknown-")
ThreadingHTTPServer.request_queue_size = 64
servers = []
for port, handler in ((RP, Runpod), (VA, Vast)):
    srv = ThreadingHTTPServer(("127.0.0.1", port), handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    servers.append(srv)
try:
    # ---- answers that aren't a machine list
    for conn, tid, answer, what in (("runpod", "rp-pA", {"oops": 1}, "Runpod: an answer without pods"),
                                    ("runpod", "rp-pA", {"pods": [], "pagination": {"hasNextPage": True}}, "Runpod: a next page without a cursor"),
                                    ("vastai", "va-201", {"success": False, "msg": "try later"}, "Vast.ai: 200 with success false"),
                                    ("vastai", "va-201", {"oops": 1}, "Vast.ai: an answer without instances")):
        w = World()
        with Hub(w, conn):
            ok = until(lambda: task(tid).get("state") == "running")
            check(f"({what}: the machine is there)", ok, task(tid))
            w.list_answer = answer
            time.sleep(4)
            check(f"{what}: the machine isn't gone", task(tid).get("state") == "running", (task(tid).get("state"), task(tid).get("message")))
            check(f"...nor said to be terminated", "terminated" not in str(task(tid).get("message", "")).lower(), task(tid).get("message"))

    # ---- a stop that is due while the list makes no sense isn't used up
    w = World(balance=1.0)
    with Hub(w, "runpod", **dict(GUARD, guard_grace_min=0.05)) as hub:
        check("(a stop is pending)", until(lambda: task("rp--guard").get("state") == "running"), task("rp--guard"))
        w.list_answer = {"oops": 1}
        time.sleep(5)
        w.list_answer = None
        check("a bad list during a pending stop: the machine is stopped all the same", until(lambda: not w.running, 20), w.stops)
        check("...no stop that stopped nothing", ": 0 stopped, 0 failed" not in hub.log())

    # ---- the billing mode
    w = World(balance=1.0, mode=None)
    with Hub(w, "runpod", **dict(GUARD, guard_grace_min=0.2)) as hub:
        time.sleep(5)
        n = w.mode_asked
        check("a billing mode that can't be read is asked at each reading", n >= 4, n)
        check("...the announced stop says so", "billing mode" in hub.log(), hub.log()[-400:])
        w.mode = "POSTPAID"
        check("...the answer arrives: a pending stop goes away on a post-paid account", until(lambda: task("rp--guard").get("state") != "running") and not w.stops, (task("rp--guard").get("state"), w.stops))

    # the last known mode stays while the question fails
    w = World(balance=50.0, mode="POSTPAID")
    with Hub(w, "runpod", **GUARD) as hub:
        check("(post-paid is known)", until(lambda: w.mode_asked >= 1))
        time.sleep(.5)
        w.mode = None
        w.balance = 1.0
        time.sleep(4)
        check("a failed question keeps the last known mode: nothing is stopped", w.running and not w.stops, w.stops)

    # ---- Vast: idle stop only after a GPU seen busy (as a fraction, every busy GPU would look idle)
    w = World(gpu=0.0)
    idle = {"idle_minutes": 0.02, "idle_util": 5, "idle_stop_minutes": 0.02, "guard_grace_min": 0.02}
    with Hub(w, "vastai", **idle):
        time.sleep(6)
        check("Vast: a GPU never seen busy isn't stopped as idle", w.running and not w.stops, w.stops)
        lines = call("GET", "/api/tasks/va-201/log")
        said = "hasn't been seen busy" in json.dumps(task("va-201")) or "hasn't been seen busy" in json.dumps(lines[1])
        check("...the task says it waits", said, lines[0])
        w.gpu = 80.0
        time.sleep(2.5)
        w.gpu = 0.0
        check("...seen busy, then idle: stopped", until(lambda: not w.running, 20), w.stops)

finally:
    for srv in servers:
        srv.shutdown()
    shutil.rmtree(root, ignore_errors=True)

print("GUARD UNKNOWN:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
