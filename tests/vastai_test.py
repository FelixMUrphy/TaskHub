"""Vast.ai connector against a mock Vast.ai API (127.0.0.1:47890; the test hub's config.json points connectors.vastai.api
there). Instances become tasks (va-<id>), v1 list paged by next_token -> after_token, load from the list / GET by id
({"instances": {...}}) -> GPU / VRAM and CPU / RAM / disk charts, null metrics, uptime from start_date (not `duration`),
GPU idle -> attention, spent from /api/v0/charges (instance-<id>, paged), balance + credit from /api/v0/users/current ->
runway + low-balance warning, read-only vs write key (probe; 200 {"success": false} answers) -> buttons, stop / start /
reboot, start stuck in "Scheduling" -> clear text, delete only in the panel (never over HTTP), destroyed instances, 429
without headers -> 60 s back-off doubling, the key kept in the Credential Manager (never config.json, never in a URL)."""
import json, os, re, threading, time, urllib.request, urllib.error, urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
DATA = os.environ.get("TASKHUB_DATA", os.path.join(os.environ.get("TEMP", "."), "taskhub-tests"))
PORT = 47890
KEY_RO = "va_READONLY" + "r" * 52
KEY_RW = "va_READWRITE" + "w" * 52
USER_KEY = "THE_USERS_OWN_API_KEY_" + "k" * 30   # /users/current also returns the key: must never show up anywhere
PAGE = 2
fails = 0
lock = threading.RLock()
calls = []                     # (method, path, auth header, body)
faults = {"r429": 0, "busy": False, "no_gpu": False, "schedule": False}
user = {"balance": 0.0, "credit": 50.0, "can_pay": True, "api_key": USER_KEY, "email": "someone@example.com"}


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


now0 = time.time()


def mk(iid, label, actual, intended="running", gpu="RTX 4090", n=1, dph=0.4, util=50.0, null_metrics=False):
    o = {"id": iid, "label": label, "actual_status": actual, "intended_status": intended,
         "cur_state": "running" if intended == "running" else "stopped", "status_msg": "",
         "gpu_name": gpu, "num_gpus": n, "geolocation": "Norway, NO", "image_uuid": "pytorch/pytorch:latest",
         "dph_total": dph, "start_date": now0 - 600, "duration": 999999.0, "uptime_mins": None,
         "gpu_util": util, "vmem_usage": 12000.0, "gpu_totalram": 24000, "gpu_ram": 24000, "cpu_util": 0.25,
         "mem_usage": 16000.0, "mem_limit": 64000.0, "cpu_ram": 64000, "disk_util": 0.4, "disk_usage": 20.0,
         "disk_space": 50.0, "ssh_host": "ssh5.vast.ai", "ssh_port": 31234, "public_ipaddr": "1.2.3.4",
         "ports": {"22/tcp": [{"HostIp": "0.0.0.0", "HostPort": "40022"}]}, "jupyter_token": "secret-token"}
    if null_metrics:
        for k in ("gpu_util", "vmem_usage", "cpu_util", "mem_usage", "disk_util", "disk_usage", "uptime_mins"):
            o[k] = None
    return o


inst = {
    101: mk(101, "trainer", "running", n=2, util=80.0),
    102: mk(102, "old-box", "exited", intended="stopped"),
    103: mk(103, None, "running", null_metrics=True),
    104: mk(104, "fresh", "loading"),
    105: mk(105, "render", "running", util=70.0),
}
charges = [{"type": "instance", "source": "instance-101", "amount": 1.25, "items": []},
           {"type": "instance", "source": "instance-102", "amount": 3.0, "items": []},
           {"type": "volume", "source": "volume-7", "amount": 9.0, "items": []},
           {"type": "instance", "source": "instance-101", "amount": 0.5, "items": []}]


class Api(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def reply(self, code, obj=None):
        data = json.dumps(obj).encode() if obj is not None else b""
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self.route("GET", None)

    def do_PUT(self):
        n = int(self.headers.get("Content-Length") or 0)
        self.route("PUT", json.loads(self.rfile.read(n) or b"{}"))

    def do_DELETE(self):
        self.route("DELETE", None)

    def route(self, m, body):
        auth = self.headers.get("Authorization")
        path = self.path
        with lock:
            calls.append((m, path, auth, body))
        if auth not in ("Bearer " + KEY_RO, "Bearer " + KEY_RW):
            return self.reply(401, {"success": False, "error": "auth_error", "msg": "Invalid user key"})
        rw = auth == "Bearer " + KEY_RW
        p = path.split("?")[0]
        q = dict(urllib.parse.parse_qsl(path.split("?", 1)[1])) if "?" in path else {}
        with lock:
            if faults["r429"] > 0:
                faults["r429"] -= 1
                return self.reply(429, {"detail": "API requests too frequent endpoint threshold=2.0"})
            if m == "GET" and p == "/api/v1/instances/":
                ids = sorted(inst)
                start = int(q.get("after_token", "t0")[1:])
                lim = min(PAGE, int(q.get("limit", 25)))
                part = ids[start:start + lim]
                more = start + lim < len(ids)
                return self.reply(200, {"instances": [inst[i] for i in part], "next_token": f"t{start + lim}" if more else None})
            if m == "GET" and p == "/api/v0/charges/":
                start = int(q.get("after_token", "t0")[1:])
                more = start + 2 < len(charges)
                return self.reply(200, {"results": charges[start:start + 2], "count": 2, "total": len(charges),
                                        "next_token": f"t{start + 2}" if more else None})
            if m == "GET" and p == "/api/v0/users/current":
                return self.reply(200, dict(user))
            mr = re.match(r"^/api/v0/instances/reboot/(\d+)/$", p)
            if mr and m == "PUT":
                if not rw:
                    return self.reply(403, {"success": False, "error": "forbidden", "msg": "API key lacks instance_write"})
                i = int(mr.group(1))
                if i not in inst:
                    return self.reply(404, {"success": False, "error": "no_such_instance", "message": "Instance not found"})
                return self.reply(200, {"success": True})
            mm = re.match(r"^/api/v0/instances/(\d+)/$", p)
            if mm:
                i = int(mm.group(1))
                if m == "GET":
                    return self.reply(200, {"instances": inst.get(i)})
                if not rw:
                    return self.reply(403, {"success": False, "error": "forbidden", "msg": "API key lacks instance_write"})
                if i not in inst:   # Vast sometimes answers 200 with success false
                    return self.reply(200, {"success": False, "error": "no_such_instance", "msg": f"Instance {i} not found."})
                o = inst[i]
                if m == "DELETE":
                    del inst[i]
                    return self.reply(200, {"success": True})
                st = (body or {}).get("state")
                if faults["busy"]:
                    return self.reply(200, {"success": False, "error": "invalid_args", "msg": "instance is busy changing state"})
                if st == "stopped":
                    o["intended_status"], o["actual_status"], o["cur_state"] = "stopped", "exited", "stopped"
                elif st == "running":
                    if faults["no_gpu"]:
                        return self.reply(200, {"success": False, "msg": "Required resources are not available on this machine"})
                    o["intended_status"], o["cur_state"] = "running", "running"
                    if faults["schedule"]:
                        o["actual_status"], o["status_msg"] = "scheduling", "Scheduling: waiting for resources"
                    else:
                        o["actual_status"], o["start_date"] = "running", time.time()
                return self.reply(200, {"success": True})
        self.reply(404, {"detail": "no route " + p})


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
    return call("GET", "/api/connectors/vastai")[1]


def ctl_ids(t):
    return [c["id"] for c in (t or {}).get("controls", []) if t["state"] in c["states"]]


def chart(t, wid):
    return next((w for w in (t or {}).get("widgets", []) if w["id"] == wid), None)


def info_of(t):
    return (chart(t, "info") or {}).get("value") or {}


def paused_for():
    """Seconds until the time in 'rate limit, waiting until HH:MM:SS'."""
    m = re.search(r"until (\d\d):(\d\d):(\d\d)", state().get("status") or "")
    if not m:
        return None
    lt = time.localtime()
    t = time.mktime((lt.tm_year, lt.tm_mon, lt.tm_mday, int(m.group(1)), int(m.group(2)), int(m.group(3)), 0, 0, -1))
    return (t - time.time()) % 86400


NO_VAULT = vault.probe()
if NO_VAULT:
    print(f"SKIP vastai connector tests: Credential Manager unavailable here ({NO_VAULT})")
    raise SystemExit(0)

ThreadingHTTPServer.request_queue_size = 64
srv = ThreadingHTTPServer(("127.0.0.1", PORT), Api)
threading.Thread(target=srv.serve_forever, daemon=True).start()
call("POST", "/api/settings", {"language": "en"})   # the task texts below are checked in English
TARGET = vault.target(DATA, "vastai")

# ---- settings and the key
call("POST", "/api/connectors/vastai", {"forget_key": True, "enabled": False})   # left by an aborted run
s, r = call("POST", "/api/connectors/vastai", {"key": "short"})
check("bad key format -> 400", s == 400, (s, r))
s, r = call("POST", "/api/connectors/vastai", {"key": "va_UNKNOWN" + "x" * 50})
check("key Vast.ai rejects -> 400, not saved", s == 400 and "rejected" in r.get("error", "") and not vault.exists(TARGET), (s, r))
s, r = call("POST", "/api/connectors/vastai", {"idle_minutes": "abc"})
check("idle_minutes 'abc' -> 400", s == 400, (s, r))
s, r = call("POST", "/api/connectors/vastai", {"low_runway_hours": 1000})
check("low_runway_hours 1000 -> 400", s == 400, (s, r))
s, r = call("POST", "/api/connectors/vastai", {"enabled": True, "list_interval": 1, "detail_interval": 1, "billing_interval": 2,
                                               "idle_minutes": 0.05, "low_runway_hours": 12, "key": KEY_RO})
check("read-only key saved, never returned", s == 200 and r.get("key_set") is True and KEY_RO not in json.dumps(r), (s, r))
check("key in the Credential Manager", vault.read(TARGET) == KEY_RO, TARGET)
cfg = open(os.path.join(DATA, "config.json"), encoding="utf-8").read()
check("key not in config.json", KEY_RO not in cfg and "va_READ" not in cfg, cfg[:300])

# ---- instances -> tasks (all three list pages), a stopped one is shown quietly
t1 = when("va-101", lambda x: x["state"] == "running")
check("running instance -> running task va-101 (source vastai, label as name)", t1 is not None and t1["source"] == "vastai"
      and t1["name"] == "trainer" and t1["project"] == "Vast.ai", t1)
check("instance on the third list page -> task (after_token paging)", when("va-105", lambda x: x["state"] == "running") is not None, task("va-105"))
t4 = when("va-104", lambda x: x["state"] == "running")
check("loading instance -> active, 'Starting…'", t4 is not None and t4["status"].startswith("Starting"), t4 and t4["status"])
t2 = when("va-102", lambda x: x["state"] == "done")
check("stopped instance first seen -> done 'stopped', already seen", t2 is not None and t2["message"] == "stopped" and t2["seen"] is True, t2)
t3 = when("va-103", lambda x: x["state"] == "running")
check("no label -> named 'instance 103'", t3 is not None and t3["name"] == "instance 103", t3 and t3["name"])
with lock:
    lists = [c for c in calls if c[1].startswith("/api/v1/instances/?") and "order_by=" in c[1]]
check("list: limit 25, then after_token from next_token", lists and "limit=25" in lists[0][1]
      and any("after_token=t4" in c[1] for c in lists), [c[1] for c in lists[:3]])
st = wait(lambda: (lambda x: x if x.get("can_act") is False else None)(state()))
check("read-only key: probe says no actions", st is not None, state())
with lock:
    probe = [c for c in calls if c[1] == "/api/v0/instances/0/"]
check("probe = label PUT on instance 0", probe and probe[0][0] == "PUT" and "label" in (probe[0][3] or {}) and "state" not in (probe[0][3] or {}), probe[:1])
check("read-only key: no action buttons", ctl_ids(task("va-101")) == [] and ctl_ids(task("va-102")) == [],
      (ctl_ids(task("va-101")), ctl_ids(task("va-102"))))
s, r = call("POST", "/api/tasks/va-101/controls/stop", {})
check("read-only: stop not possible", s in (404, 403, 409), (s, r))

# ---- load -> charts (GPU %, VRAM MB / total, CPU fraction, RAM MB / limit, disk), info, uptime from start_date
t1 = when("va-101", lambda x: len((chart(x, "gpu") or {}).get("points", [])) >= 2)
g = chart(t1, "gpu")
check("GPU chart: util, VRAM 12000 / 24000 MB = 50 %", g is not None and g["points"][-1][1:] == [80, 50], g and g["points"][-2:])
ld = chart(t1, "load")
check("CPU / RAM / disk chart (3 series)", ld is not None and len(ld["series"]) == 3 and ld["points"][-1][1:] == [25, 25, 40],
      ld and (ld["series"], ld["points"][-1:]))
info = info_of(t1)
check("info: GPU 2 ×, region, image, price, SSH", info.get("GPU") == "2 × RTX 4090" and info.get("Region") == "Norway, NO"
      and info.get("Image") == "pytorch/pytorch:latest" and info.get("Price") == "$0.40/h"
      and info.get("SSH") == "ssh -p 31234 root@ssh5.vast.ai", info)
check("uptime from start_date, not `duration`", re.match(r"^1[01]m \d\ds$", info.get("Uptime", "")) is not None, info.get("Uptime"))
with lock:
    det = [c for c in calls if c[0] == "GET" and c[1] == "/api/v0/instances/101/"]
check("running instance also read by id", len(det) > 0, len(det))
check("https link only (console), no plain-http / token links", all(l["url"].startswith("https://") and "token" not in l["url"]
      for l in t1["links"]) and len(t1["links"]) >= 1, t1["links"])
t3 = when("va-103", lambda x: len((chart(x, "gpu") or {}).get("points", [])) >= 1, timeout=8)
g3 = chart(t3 or task("va-103"), "gpu")
check("null metrics -> empty chart points, task still fine", t3 is None or all(v is None for pt in g3["points"] for v in pt[1:]),
      g3 and g3["points"][-2:])
check("null metrics -> no idle warning", not (task("va-103") or {}).get("attention"), task("va-103").get("attention"))
t1 = when("va-101", lambda x: info_of(x).get("Spent") == "≈ $1.75")
check("spent so far from charges (instance-101, two pages, ≈ $1.75)", t1 is not None, info_of(task("va-101")))
with lock:
    ch = [c for c in calls if c[1].startswith("/api/v0/charges/")]
check("charges paged with after_token", any("after_token=t2" in c[1] for c in ch), [c[1] for c in ch[:3]])

# ---- balance + credit: both shown, both raw; runway from credit
st = wait(lambda: (lambda x: x if x.get("balance") else None)(state()))
b = (st or {}).get("balance") or {}
check("credit as amount, runway = 50 / (4 active × $0.40/h)", b.get("amount") == 50 and abs((b.get("runway_hours") or 0) - 31.25) < 0.01, b)
check("raw balance and credit kept", b.get("raw", {}).get("balance") == 0 and b.get("raw", {}).get("credit") == 50, b.get("raw"))
check("both labelled in the summary", st and "Balance $0.00" in st.get("summary", "") and "Credit $50.0" in st["summary"], st and st.get("summary"))
dump = json.dumps(state()) + json.dumps(task("va-101")) + open(os.path.join(DATA, "config.json"), encoding="utf-8").read()
check("the user's API key from /users/current never kept", USER_KEY not in dump and "someone@example.com" not in dump)

# ---- GPU idle -> attention (once per idle period)
with lock:
    inst[101]["gpu_util"] = 1.0
t1 = when("va-101", lambda x: "GPU idle" in (x.get("attention") or ""), timeout=20)
check("GPU idle -> attention 'GPU idle 1 min'", t1 is not None and t1["attention"].startswith("GPU idle 1 min"), task("va-101").get("attention"))
time.sleep(3)
logs = [l["msg"] for l in task("va-101")["log"] if l["msg"].startswith("GPU idle")]
check("idle warned once per idle period", len(logs) == 1, logs)
with lock:
    inst[101]["gpu_util"] = 60.0
check("GPU busy again -> attention cleared", when("va-101", lambda x: not x.get("attention")) is not None, task("va-101").get("attention"))

# ---- low credit -> warning
with lock:
    user["credit"] = 2.0
st = wait(lambda: (lambda x: x if x.get("low") else None)(state()))
check("runway 1.25 h < 12 h -> low", st is not None and abs(st["balance"]["runway_hours"] - 1.25) < 0.01, state().get("balance"))
check("low balance -> attention on running machines", when("va-105", lambda x: "Low balance" in (x.get("attention") or "")) is not None,
      task("va-105").get("attention"))
with lock:
    user["credit"] = 50.0
check("credit back up -> not low", wait(lambda: state().get("low") is False) and when("va-105", lambda x: not x.get("attention")) is not None,
      task("va-105").get("attention"))

# ---- a write key: buttons
s, r = call("POST", "/api/connectors/vastai", {"key": KEY_RW})
check("write key saved", s == 200 and vault.read(TARGET) == KEY_RW, (s, r))
check("probe (200 success:false 'not found') -> actions allowed", wait(lambda: state().get("can_act") is True) is not None, state().get("can_act"))
t1 = when("va-101", lambda x: "stop" in ctl_ids(x))
check("running instance: stop, reboot, delete", ctl_ids(t1) == ["stop", "restart", "delete"]
      and next(c for c in t1["controls"] if c["id"] == "restart")["label"] == "Reboot", ctl_ids(t1))
dele = next(c for c in t1["controls"] if c["id"] == "delete")
stop = next(c for c in t1["controls"] if c["id"] == "stop")
check("stop in toasts with confirm; delete typed name, panel only", stop["toast"] and stop["confirm"] and not dele["toast"]
      and dele.get("panel_only") is True and "trainer" in dele["input"]["label"], (stop, dele))
t2 = when("va-102", lambda x: "start" in ctl_ids(x))
check("stopped instance: start, delete", ctl_ids(t2) == ["start", "delete"], ctl_ids(t2))

# ---- actions
with lock:
    faults["busy"] = True
s, r = call("POST", "/api/tasks/va-101/controls/stop", {})
check("200 success:false -> error with Vast's msg", s == 409 and "busy changing state" in r.get("error", ""), (s, r))
with lock:
    faults["busy"] = False
s, r = call("POST", "/api/tasks/va-101/controls/restart", {})
check("reboot -> PUT /instances/reboot/101/", s == 200 and any(c[0] == "PUT" and c[1] == "/api/v0/instances/reboot/101/" for c in calls), (s, r))
s, r = call("POST", "/api/tasks/va-101/controls/stop", {})
check("stop -> PUT state stopped", s == 200 and any(c[0] == "PUT" and c[1] == "/api/v0/instances/101/" and (c[3] or {}).get("state") == "stopped"
                                                     for c in calls), (s, r))
t1 = when("va-101", lambda x: x["state"] == "done")
check("stopped -> task done 'stopped', not seen yet", t1 is not None and t1["message"] == "stopped" and t1["seen"] is False, t1)
s, r = call("POST", "/api/tasks/va-101/controls/start", {})
check("start -> running again, same task id", s == 200 and when("va-101", lambda x: x["state"] == "running") is not None, (s, r))
with lock:
    faults["no_gpu"] = True
s, r = call("POST", "/api/tasks/va-102/controls/start", {})
check("start without resources -> 'No free GPU' text", s == 409 and "No free GPU" in r.get("error", ""), (s, r))
with lock:
    faults["no_gpu"] = False
    faults["schedule"] = True
s, r = call("POST", "/api/tasks/va-102/controls/start", {})
check("start accepted while the GPU is taken", s == 200, (s, r))
t2 = when("va-102", lambda x: x["state"] == "running" and "waiting for its GPU" in x.get("status", ""), timeout=15)
check("stuck in Scheduling -> active with a clear text", t2 is not None, task("va-102") and task("va-102")["status"])
with lock:
    faults["schedule"] = False
s, r = call("POST", "/api/tasks/va-102/controls/stop", {})
check("stop the scheduling instance", s == 200 and when("va-102", lambda x: x["state"] == "done") is not None, (s, r))

# ---- delete: only through the panel's WebView2 bridge - never over HTTP, not even with the exact name
for v in ("old-box", "old-bo", None):
    s, r = call("POST", "/api/tasks/va-102/controls/delete", {} if v is None else {"value": v})
    check(f"HTTP delete (value {v!r}) -> 403, instance untouched", s == 403 and 102 in inst and "only in the TaskHub panel" in r.get("error", ""), (s, r))
with lock:
    check("no DELETE ever sent to Vast.ai", not any(c[0] == "DELETE" for c in calls))
    del inst[102]   # destroyed in Vast's console
t2 = when("va-102", lambda x: x["message"] == "terminated")
check("destroyed instance -> task 'terminated', no buttons", t2 is not None and ctl_ids(t2) == [], t2 and (t2["message"], ctl_ids(t2)))
with lock:
    del inst[105]
t5 = when("va-105", lambda x: x["state"] == "done")
check("running instance gone from the list -> done 'terminated'", t5 is not None and t5["message"] == "terminated", t5)

# ---- 429 without any header: everything waits 60 s, the next 429 doubles it
with lock:
    faults["r429"] = 1
st = wait(lambda: (lambda x: x if "rate limit" in (x.get("status") or "") else None)(state()))
check("429 -> paused, said so", st is not None, state().get("status"))
pf = paused_for()
check("no Retry-After -> waits about 60 s", pf is not None and 50 <= pf <= 61, pf)
time.sleep(0.3)
with lock:
    n = len(calls)
time.sleep(2)
with lock:
    quiet = len(calls) == n
check("nothing polled while paused", quiet, calls[n:])
with lock:
    faults["r429"] = 1
s, r = call("POST", "/api/tasks/va-101/controls/stop", {})
check("an action answered 429 -> clear text", s == 502 and "Too many requests" in r.get("error", ""), (s, r))
pf = paused_for()
check("second 429 in a row -> back-off doubles (about 120 s)", pf is not None and 110 <= pf <= 121, pf)
call("POST", "/api/connectors/vastai", {"enabled": True})   # a restart of the connector clears the pause
with lock:
    n = len(calls)
check("polling again after the connector restarts", wait(lambda: len(calls) > n, timeout=8) is not None)

# ---- the key never in a URL, always the Bearer header
with lock:
    bad = [c for c in calls if KEY_RO in c[1] or KEY_RW in c[1] or "api_key" in c[1]]
    hdr_ok = all(c[2] in ("Bearer " + KEY_RO, "Bearer " + KEY_RW, "Bearer va_UNKNOWN" + "x" * 50) for c in calls)
check("key only in the Authorization header", not bad and hdr_ok, bad[:2])

# ---- Runpod and Vast.ai side by side: separate credentials
check("separate Credential Manager entries", vault.target(DATA, "vastai") != vault.target(DATA, "runpod"))

# ---- off / forget
s, r = call("POST", "/api/connectors/vastai", {"enabled": False, "forget_key": True})
check("disable + forget key (gone from the Credential Manager)", s == 200 and r.get("status") == "off" and r.get("key_set") is False
      and not vault.exists(TARGET), r)
with lock:
    n = len(calls)
time.sleep(3)
check("nothing polled when off", len(calls) == n, calls[n:])
call("POST", "/api/connectors/vastai", {"low_runway_hours": 12})
for tid in ["va-101", "va-102", "va-103", "va-104", "va-105"]:
    call("DELETE", f"/api/tasks/{tid}?force=1")
srv.shutdown()
print(f"{fails} failure(s)")
raise SystemExit(1 if fails else 0)
