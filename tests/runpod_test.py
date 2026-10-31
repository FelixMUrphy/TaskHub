"""Runpod connector against a mock Runpod API (127.0.0.1:47891; the test hub's config.json points connectors.runpod.api
there). Pods become tasks (rp-<podId>), paging by cursor, live load from GET by id -> charts, GPU idle -> attention,
balance (GraphQL module) -> runway + low-balance warning, read-only vs write key (probe) -> buttons, stop / start /
restart, 409 and "GPU taken", delete only in the panel (never over HTTP), terminated pods, 429 + Retry-After, the key kept in the
Credential Manager (never config.json, never in a URL)."""
import json, os, re, threading, time, urllib.request, urllib.error
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
DATA = os.environ.get("TASKHUB_DATA", os.path.join(os.environ.get("TEMP", "."), "taskhub-tests"))
PORT = 47891
KEY_RO = "rpa_READONLY" + "R" * 30
KEY_RW = "rpa_READWRITE" + "W" * 30
PAGE = 2
fails = 0
lock = threading.RLock()
calls = []                     # (method, path, auth header)
faults = {"r429": 0, "gpu_taken": False, "force409": False}
revoked = set()            # keys the mock no longer accepts (401), as if revoked in the Runpod console
balance = {"clientBalance": 50.0, "currentSpendPerHr": 1.0, "spendLimit": 80}


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


def iso(t):
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))


now0 = time.time()


def mkpod(pid, name, status, gpu="NVIDIA GeForce RTX 4090", count=1, cost=0.69, util=(50,), started=None):
    return {"id": pid, "name": name, "status": status, "image": "runpod/pytorch:2.4", "cost": cost,
            "gpu": {"id": gpu, "count": count, "vcpuCount": 16, "memory": 64}, "cloud": "SECURE",
            "dataCenterId": "EU-RO-1", "ports": ["8888/http", "22/tcp"], "mounts": {},
            "createdAt": iso(now0 - 7200), "startedAt": iso(started or now0 - 600), "_util": list(util)}


pods = {
    "pod1": mkpod("pod1", "trainer", "RUNNING", count=2, util=(80, 90)),
    "pod2": mkpod("pod2", "old-box", "EXITED"),
    "pod3": mkpod("pod3", "render", "RUNNING", util=(70,)),
    "pod4": mkpod("pod4", "fresh", "PROVISIONING"),
}
ACTIONS = {"RUNNING": ["stop", "restart", "terminate"], "EXITED": ["start", "terminate"], "ERROR": ["start", "terminate"],
           "PROVISIONING": ["stop", "terminate"], "STARTING": ["stop", "terminate"]}


def view(p, detail):
    o = {k: v for k, v in p.items() if not k.startswith("_")}
    o["actions"] = ACTIONS.get(p["status"], [])
    o["runtime"] = None
    o["ssh"] = {"proxy": {"host": "ssh.runpod.io", "port": 22, "username": p["id"] + "-abc",
                          "command": f"ssh {p['id']}-abc@ssh.runpod.io"}, "direct": None}
    if detail and p["status"] == "RUNNING":
        o["runtime"] = {"uptime": 600, "gpus": [{"util": u, "memoryUtil": 40} for u in p["_util"]],
                        "cpu": {"util": 25}, "memory": {"util": 60}}
        o["ssh"]["direct"] = {"host": "1.2.3.4", "port": 22022, "username": "root", "command": "ssh root@1.2.3.4 -p 22022"}
    return o


class Api(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def reply(self, code, obj=None, headers=None):
        data = json.dumps(obj).encode() if obj is not None else b""
        self.send_response(code)
        self.send_header("Content-Type", "application/json" if code < 400 else "application/problem+json")
        self.send_header("RateLimit", '"minute";r=500;t=30, "hour";r=9000;t=1800')
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def problem(self, code, title, detail, headers=None):
        self.reply(code, {"title": title, "status": code, "detail": detail}, headers)

    def do_GET(self):
        self.route("GET", None)

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        self.route("POST", json.loads(self.rfile.read(n) or b"{}"))

    def do_DELETE(self):
        self.route("DELETE", None)

    def route(self, m, body):
        auth = self.headers.get("Authorization")
        path = self.path
        with lock:
            calls.append((m, path, auth))
        if auth not in ("Bearer " + KEY_RO, "Bearer " + KEY_RW) or auth in revoked:
            return self.problem(401, "Unauthorized", "invalid api key")
        rw = auth == "Bearer " + KEY_RW
        p = path.split("?")[0]
        q = dict(kv.split("=", 1) for kv in path.split("?", 1)[1].split("&")) if "?" in path else {}
        with lock:
            if m == "POST" and p == "/graphql":
                return self.reply(200, {"data": {"myself": dict(balance)}})
            if m == "GET" and p == "/v2/pods":
                if faults["r429"] > 0:
                    faults["r429"] -= 1
                    return self.problem(429, "Too Many Requests", "minute window exceeded", {"Retry-After": "3"})
                ids = sorted(pods)
                start = int(urllib.request.unquote(q.get("cursor", "c0"))[1:])
                lim = min(PAGE, int(q.get("limit", 100)))
                part = ids[start:start + lim]
                more = start + lim < len(ids)
                return self.reply(200, {"pods": [view(pods[i], False) for i in part],
                                        "pagination": {"nextCursor": f"c{start + lim}" if more else None, "hasNextPage": more}})
            if m == "GET" and p == "/v2/billing/pods":
                return self.reply(200, {"records": [{"podId": "pod1", "totalAmount": 1.25}, {"podId": "pod1", "totalAmount": 0.5},
                                                    {"podId": "pod2", "totalAmount": 3.0}], "metadata": {}})
            mm = re.match(r"^/v2/pods/([^/]+)(/action)?$", p)
            if mm:
                pid, act = mm.group(1), mm.group(2)
                if m == "GET" and not act:
                    return self.reply(200, view(pods[pid], True)) if pid in pods else self.problem(404, "Not Found", "pod not found")
                if m in ("POST", "DELETE") and not rw:
                    return self.problem(403, "Forbidden", "api key lacks pods:write")
                if pid not in pods:
                    return self.problem(404, "Not Found", "pod not found")
                pod = pods[pid]
                if m == "DELETE":
                    del pods[pid]
                    return self.reply(204)
                a = body.get("action")
                if faults["force409"] or a not in ACTIONS.get(pod["status"], []):
                    return self.problem(409, "Conflict", f"cannot {a} a pod in status {pod['status']}")
                if a == "start" and faults["gpu_taken"]:
                    return self.problem(400, "Bad Request", "There are no longer any instances available with enough GPUs")
                pod["status"] = {"stop": "EXITED", "start": "RUNNING", "restart": "RUNNING"}[a]
                if a == "start":
                    pod["startedAt"] = iso(time.time())
                return self.reply(200, view(pod, False))
        self.problem(404, "Not Found", "no route " + p)


op = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def call(method, path, body=None):
    req = urllib.request.Request(URL + path, json.dumps(body).encode() if body is not None else None, method=method,
                                 headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    try:
        with op.open(req, timeout=30) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


def task(tid):
    s, r = call("GET", f"/api/tasks/{tid}")
    return r if s == 200 else None


def wait(pred, timeout=15):
    end = time.time() + timeout
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(0.3)
    return None


def when(tid, cond, timeout=15):
    return wait(lambda: (lambda x: x if x and cond(x) else None)(task(tid)), timeout)


def state():
    return call("GET", "/api/connectors/runpod")[1]


def ctl_ids(t):
    return [c["id"] for c in (t or {}).get("controls", []) if t["state"] in c["states"]]


def row(cid):
    """The settings page's row of a connector (GET /api/connectors?view=settings) and the whole answer as text."""
    v = call("GET", "/api/connectors?view=settings")[1]
    return json.dumps(v, ensure_ascii=False), next((c for c in v.get("connectors", []) if c["id"] == cid), None)


def chart(t, wid):
    return next((w for w in (t or {}).get("widgets", []) if w["id"] == wid), None)


NO_VAULT = vault.probe()
if NO_VAULT:
    print(f"SKIP runpod connector tests: Credential Manager unavailable here ({NO_VAULT})")
    raise SystemExit(0)

ThreadingHTTPServer.request_queue_size = 64
srv = ThreadingHTTPServer(("127.0.0.1", PORT), Api)
threading.Thread(target=srv.serve_forever, daemon=True).start()
call("POST", "/api/settings", {"language": "en"})   # the task texts below are checked in English
TARGET = vault.target(DATA, "runpod")

# ---- settings and the key
call("POST", "/api/connectors/runpod", {"forget_key": True, "enabled": False})   # left by an aborted run
s, r = call("POST", "/api/connectors/runpod", {"key": "short"})
check("bad key format -> 400", s == 400, (s, r))
s, r = call("POST", "/api/connectors/runpod", {"key": "rpa_UNKNOWN" + "X" * 30})
check("key Runpod rejects -> 400, not saved", s == 400 and "rejected" in r.get("error", "") and not vault.exists(TARGET), (s, r))
s, r = call("POST", "/api/connectors/runpod", {"idle_minutes": "abc"})
check("idle_minutes 'abc' -> 400", s == 400, (s, r))
s, r = call("POST", "/api/connectors/runpod", {"low_balance": -1})
check("low_balance -1 -> 400", s == 400, (s, r))
s, r = call("POST", "/api/connectors/runpod", {"enabled": True, "list_interval": 1, "detail_interval": 1, "billing_interval": 2,
                                               "idle_minutes": 0.05, "low_runway_hours": 12, "key": KEY_RO})
check("read-only key saved, never returned", s == 200 and r.get("key_set") is True and KEY_RO not in json.dumps(r), (s, r))
check("key in the Credential Manager", vault.read(TARGET) == KEY_RO, TARGET)
cfg = open(os.path.join(DATA, "config.json"), encoding="utf-8").read()
check("key not in config.json", KEY_RO not in cfg and "rpa_" not in cfg, cfg[:300])
check("key checked right after saving -> 'Connected, balance $50.0'", r.get("key_check") == "ok"
      and r.get("key_check_text") == "Connected, balance $50.0", r)

# ---- pods -> tasks (both list pages), a pod already stopped is shown quietly
t1 = when("rp-pod1", lambda x: x["state"] == "running")
check("running pod -> running task rp-pod1 (source runpod)", t1 is not None and t1["source"] == "runpod" and t1["name"] == "trainer", t1)
check("pod on the second list page -> task (cursor paging)", when("rp-pod3", lambda x: x["state"] == "running") is not None, task("rp-pod3"))
t4 = when("rp-pod4", lambda x: x["state"] == "running")
check("provisioning pod -> active, 'Starting…'", t4 is not None and t4["status"].startswith("Starting"), t4 and t4["status"])
t2 = when("rp-pod2", lambda x: x["state"] == "done")
check("stopped pod first seen -> done 'stopped', already seen (no toast)", t2 is not None and t2["message"] == "stopped" and t2["seen"] is True, t2)
with lock:
    paged = [c for c in calls if c[1].startswith("/v2/pods?") and "cursor=c2" in c[1]]
check("list paged with nextCursor", len(paged) > 0, len(paged))
st = wait(lambda: (lambda x: x if x.get("can_act") is False else None)(state()))
check("read-only key: probe says no actions", st is not None, state())
with lock:
    probe = [c for c in calls if c[1] == "/v2/pods/pod_taskhub_probe/action"]
check("probe = action on a pod that can't exist", probe and probe[0][0] == "POST", probe)
check("read-only key: no action buttons", ctl_ids(task("rp-pod1")) == [] and ctl_ids(task("rp-pod2")) == [],
      (ctl_ids(task("rp-pod1")), ctl_ids(task("rp-pod2"))))
s, r = call("POST", "/api/tasks/rp-pod1/controls/stop", {})
check("read-only: stop not possible", s in (404, 403, 409), (s, r))

# ---- details: load -> charts, info (GPU, price, spent, SSH), Jupyter link through the https proxy
t1 = when("rp-pod1", lambda x: len((chart(x, "gpu") or {}).get("points", [])) >= 2)
g = chart(t1, "gpu")
check("GPU chart from our polls (max util of the GPUs, VRAM)", g is not None and g["points"][-1][1:] == [90, 40], g and g["points"][-2:])
ld = chart(t1, "load")
check("CPU / RAM chart", ld is not None and ld["points"] and ld["points"][-1][1:] == [25, 60], ld and ld["points"][-1:])
info = (chart(t1, "info") or {}).get("value") or {}
check("info: GPU 2 ×, region, price, SSH (direct)", info.get("GPU") == "2 × NVIDIA GeForce RTX 4090" and info.get("Region") == "EU-RO-1 secure"
      and info.get("Price") == "$0.69/h" and info.get("SSH") == "ssh root@1.2.3.4 -p 22022", info)
t1 = when("rp-pod1", lambda x: ((chart(x, "info") or {}).get("value") or {}).get("Spent") == "≈ $1.75")
info = (chart(t1, "info") or {}).get("value") or {}
check("spent so far from billing (≈ $1.75)", info.get("Spent") == "≈ $1.75", info)
check("Jupyter link via https proxy", any(l["url"] == "https://pod1-8888.proxy.runpod.net" and l["label"] == "Jupyter" for l in t1["links"]), t1["links"])
check("charts bounded", g["max_points"] == 240, g.get("max_points"))

# ---- balance (GraphQL module): runway, nothing low yet
st = wait(lambda: (lambda x: x if x.get("balance") else None)(state()))
b = (st or {}).get("balance") or {}
check("balance + spend rate from GraphQL myself, runway", b.get("amount") == 50 and b.get("spend_per_hr") == 1 and b.get("runway_hours") == 50
      and b.get("raw", {}).get("spendLimit") == 80, b)
check("not low at 50 h runway", st and st.get("low") is False, st and st.get("low"))
check("settings summary has balance and runway", st and "$50.0" in st.get("summary", "") and "left" in st["summary"], st and st.get("summary"))
with lock:
    gq = [c for c in calls if c[1] == "/graphql"]
check("GraphQL with the Bearer header only", gq and all(c[2] == "Bearer " + KEY_RO for c in gq), gq[:1])

# ---- GPU idle -> attention (once per idle period)
with lock:
    pods["pod1"]["_util"] = [1, 2]
t1 = when("rp-pod1", lambda x: "GPU idle" in (x.get("attention") or ""), timeout=20)
check("GPU idle -> attention 'GPU idle 1 min'", t1 is not None and t1["attention"].startswith("GPU idle 1 min"), task("rp-pod1").get("attention"))
time.sleep(3)
logs = [l["msg"] for l in task("rp-pod1")["log"] if l["msg"].startswith("GPU idle")]
check("idle warned once per idle period", len(logs) == 1, logs)
with lock:
    pods["pod1"]["_util"] = [60, 2]
check("GPU busy again -> attention cleared", when("rp-pod1", lambda x: not x.get("attention")) is not None, task("rp-pod1").get("attention"))

# ---- low balance -> warning (state + attention on running machines)
with lock:
    balance["clientBalance"] = 5.0
st = wait(lambda: (lambda x: x if x.get("low") else None)(state()))
check("runway 5 h < 12 h -> low", st is not None and st["balance"]["runway_hours"] == 5, state().get("balance"))
check("low balance -> attention on running machines", when("rp-pod3", lambda x: "Low balance" in (x.get("attention") or "")) is not None,
      task("rp-pod3").get("attention"))
with lock:
    balance["clientBalance"] = 50.0
check("balance back up -> not low, attention cleared", wait(lambda: state().get("low") is False)
      and when("rp-pod3", lambda x: not x.get("attention")) is not None, task("rp-pod3").get("attention"))

# ---- a write key: buttons appear
s, r = call("POST", "/api/connectors/runpod", {"key": KEY_RW})
check("write key saved", s == 200 and vault.read(TARGET) == KEY_RW, (s, r))
check("probe with a write key -> actions allowed", wait(lambda: state().get("can_act") is True) is not None, state().get("can_act"))
t1 = when("rp-pod1", lambda x: "stop" in ctl_ids(x))
check("running pod: stop, restart, delete", ctl_ids(t1) == ["stop", "restart", "delete"], ctl_ids(t1))
stop = next(c for c in t1["controls"] if c["id"] == "stop")
dele = next(c for c in t1["controls"] if c["id"] == "delete")
check("stop: confirm + in toasts / Telegram; delete: typed name, never in toasts", stop["toast"] and stop["confirm"]
      and not dele["toast"] and dele["input"]["type"] == "text" and "trainer" in dele["input"]["label"], (stop, dele))
t2 = when("rp-pod2", lambda x: "start" in ctl_ids(x))
check("stopped pod: start, delete", ctl_ids(t2) == ["start", "delete"], ctl_ids(t2))

# ---- actions
with lock:
    faults["force409"] = True
s, r = call("POST", "/api/tasks/rp-pod1/controls/restart", {})
check("409 from Runpod -> clear text", s == 409 and "current state" in r.get("error", ""), (s, r))
with lock:
    faults["force409"] = False
s, r = call("POST", "/api/tasks/rp-pod1/controls/stop", {})
check("stop -> Runpod action stop", s == 200 and any(c[0] == "POST" and c[1] == "/v2/pods/pod1/action" for c in calls), (s, r))
t1 = when("rp-pod1", lambda x: x["state"] == "done")
check("stopped -> task done 'stopped', not seen yet", t1 is not None and t1["message"] == "stopped" and t1["seen"] is False, t1)
s, r = call("POST", "/api/tasks/rp-pod1/controls/start", {})
check("start -> running again, same task id", s == 200 and when("rp-pod1", lambda x: x["state"] == "running") is not None, (s, r))
with lock:
    faults["gpu_taken"] = True
s, r = call("POST", "/api/tasks/rp-pod2/controls/start", {})
check("start with the GPU taken -> 'No free GPU' text", s == 409 and "No free GPU" in r.get("error", ""), (s, r))
with lock:
    faults["gpu_taken"] = False

# ---- delete: only through the panel's WebView2 bridge (typed name) - never over HTTP, not even with the exact name.
# (The bridge path itself can't be driven from Python; the name check sits behind it.)
check("delete marked panel-only", dele.get("panel_only") is True, dele)
for v in ("old-box", "old-bo", None):
    s, r = call("POST", "/api/tasks/rp-pod2/controls/delete", {} if v is None else {"value": v})
    check(f"HTTP delete (value {v!r}) -> 403, pod untouched", s == 403 and "pod2" in pods and "only in the TaskHub panel" in r.get("error", ""), (s, r))
with lock:
    check("no DELETE ever sent to Runpod", not any(c[0] == "DELETE" for c in calls))
    del pods["pod2"]   # deleted in Runpod's console
t2 = when("rp-pod2", lambda x: x["message"] == "terminated")
check("deleted pod -> task 'terminated', no buttons", t2 is not None and ctl_ids(t2) == [], t2 and (t2["message"], ctl_ids(t2)))

# ---- a pod terminated elsewhere: gone from the list -> finished 'terminated'
with lock:
    del pods["pod3"]
t3 = when("rp-pod3", lambda x: x["state"] == "done")
check("pod gone from the list -> done 'terminated'", t3 is not None and t3["message"] == "terminated", t3)

# ---- 429 + Retry-After: everything waits, then polls again
with lock:
    faults["r429"] = 1
st = wait(lambda: (lambda x: x if "rate limit" in (x.get("status") or "") else None)(state()))
check("429 -> paused, said so", st is not None, state().get("status"))
time.sleep(0.3)
with lock:
    n = len(calls)
time.sleep(1.5)
with lock:
    quiet = len(calls) == n
check("nothing sent while paused (Retry-After 3 s)", quiet, calls[n:])
check("polling again after Retry-After", wait(lambda: len(calls) > n, timeout=8) is not None, len(calls) - n)

# ---- the key never in a URL, always the Bearer header
with lock:
    bad = [c for c in calls if KEY_RO in c[1] or KEY_RW in c[1] or "api_key" in c[1]]
    hosts_ok = all(c[2] in ("Bearer " + KEY_RO, "Bearer " + KEY_RW, "Bearer rpa_UNKNOWN" + "X" * 30) for c in calls)
check("key only in the Authorization header", not bad and hosts_ok, bad[:2])

# ---- the settings page's row (2.9): never the key; off keeps the key, on again needs no re-entry; a revoked key
txt, rw = row("runpod")
check("settings rows never contain a key", rw is not None and KEY_RO not in txt and KEY_RW not in txt and "rpa_" not in txt, txt[:300])
check("settings row: on, key saved and working, 'Connected, balance $…'", rw and rw["enabled"] and rw["uses_key"] and rw["key_set"]
      and rw["key_check"] == "ok" and rw["summary"].startswith("Connected, balance $"), rw)
check("settings row: thresholds", rw and rw.get("thresholds", {}).get("low_runway_hours") == 12, rw)
check("how-to-get-a-key page: English", rw and rw["help_url"] == "https://github.com/FelixMUrphy/TaskHub/blob/main/docs/keys/runpod.md", rw)
call("POST", "/api/settings", {"language": "uk"})
txt, rw = row("runpod")
check("how-to page in Ukrainian (exists) + the row in Ukrainian", rw and rw["help_url"].endswith("/docs/keys/runpod.uk.md")
      and rw["summary"].startswith("Під’єднано, баланс $"), rw)
call("POST", "/api/settings", {"language": "de"})
txt, rw = row("runpod")
check("how-to page: German not there -> English", rw and rw["help_url"].endswith("/docs/keys/runpod.md"), rw)
call("POST", "/api/settings", {"language": "en"})
s, r = call("POST", "/api/connectors/runpod", {"enabled": False})
check("off keeps the key (Credential Manager)", s == 200 and r.get("status") == "off" and r.get("key_set") is True
      and vault.read(TARGET) == KEY_RW, r)
txt, rw = row("runpod")
check("off: the row has no state line", rw and rw["enabled"] is False and rw["summary"] == "", rw)
with lock:
    n = len(calls)
s, r = call("POST", "/api/connectors/runpod", {"enabled": True})
check("on again: polls with the saved key, no re-entry", s == 200 and wait(lambda: any(c[2] == "Bearer " + KEY_RW and c[1].startswith("/v2/pods?")
      for c in calls[n:])) and wait(lambda: state().get("status") == "ok"), state())
revoked.add("Bearer " + KEY_RW)
st = wait(lambda: (lambda x: x if x.get("key_check") == "revoked" else None)(state()))
check("key revoked at Runpod -> key_check 'revoked', said so", st and "no longer accepts" in (st.get("key_check_text") or ""), st)
txt, rw = row("runpod")
check("revoked: the row says so (the how-to link comes back)", rw and rw["key_check"] == "revoked" and rw["key_set"]
      and "no longer accepts" in rw["summary"], rw)
revoked.clear()
check("accepted again -> 'ok'", wait(lambda: state().get("key_check") == "ok"), state())

# ---- GraphQL balance module off
call("POST", "/api/connectors/runpod", {"graphql_balance": False})
time.sleep(0.5)
with lock:
    n = len([c for c in calls if c[1] == "/graphql"])
time.sleep(3)
with lock:
    n2 = len([c for c in calls if c[1] == "/graphql"])
check("graphql_balance off -> no GraphQL calls", n2 == n and state().get("graphql_balance") is False, (n, n2))

# ---- off / forget
s, r = call("POST", "/api/connectors/runpod", {"enabled": False, "forget_key": True})
check("disable + forget key (gone from the Credential Manager)", s == 200 and r.get("status") == "off" and r.get("key_set") is False
      and not vault.exists(TARGET), r)
with lock:
    n = len(calls)
time.sleep(3)
check("nothing polled when off", len(calls) == n, calls[n:])
call("POST", "/api/connectors/runpod", {"graphql_balance": True, "low_runway_hours": 12})
for tid in ["rp-pod1", "rp-pod2", "rp-pod3", "rp-pod4"]:
    call("DELETE", f"/api/tasks/{tid}?force=1")
# ---- no network when the key is saved (the mock is down): kept, said so, checked again later
srv.shutdown()
srv.server_close()
s, r = call("POST", "/api/connectors/runpod", {"key": KEY_RO})
check("no network on save -> key saved, 'Couldn't reach Runpod'", s == 200 and r.get("key_check") == "network"
      and "Couldn't reach Runpod" in (r.get("key_check_text") or "") and vault.read(TARGET) == KEY_RO, (s, r))
s, r = call("POST", "/api/connectors/runpod", {"forget_key": True})
check("remove deletes the key", s == 200 and r.get("key_set") is False and r.get("key_check") is None and not vault.exists(TARGET), r)
print(f"{fails} failure(s)")
raise SystemExit(1 if fails else 0)
