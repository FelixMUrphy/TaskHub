"""Balance guard (2.9) of the machine connectors against mocks: Runpod (127.0.0.1:47891), Vast.ai (47890) and the Telegram
Bot API (47897). Config validation (warn > stop >= 0, defaults, off by default, old low-balance keys untouched); the
warning once per crossing with balance / burn / hours, re-armed after a top-up; a pending stop -> Telegram message with
Stop now / Snooze, Snooze moves the deadline (capped at the projected $0), Stop now stops, no answer -> stopped after the
(fractional, mock) grace time; a top-up cancels it; stale buttons answer "already handled"; "never auto-stop" machines
and stopped ones are left alone; never a DELETE / destroy; balance unreadable -> warning, nothing stopped at the deadline,
"readable again"; a read-only key only warns; one machine failing to stop is logged as failed, the others still stop;
GET /api/guard/log (storage while stopped ≈). Only the isolated test hub, mocks and test-only Credential Manager names."""
import json, os, re, threading, time, urllib.request, urllib.error, urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
DATA = os.environ.get("TASKHUB_DATA", os.path.join(os.environ.get("TEMP", "."), "taskhub-tests"))
RP_KEY_RO = "rpa_GUARDREADONLY" + "R" * 30
RP_KEY_RW = "rpa_GUARDWRITE" + "W" * 30
VA_KEY = "va_GUARDWRITE" + "w" * 52
TG_TOKEN = "123456789:" + "G" * 35
CHAT = 4242
fails = 0
lock = threading.RLock()
calls = []                     # (service, method, path)
faults = {"gql500": False, "stop500": set()}
balance = {"clientBalance": 50.0, "currentSpendPerHr": 1.0, "spendLimit": 80}


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


def mkpod(pid, name, status, size=20):
    return {"id": pid, "name": name, "status": status, "image": "runpod/pytorch:2.4", "cost": 0.5,
            "gpu": {"id": "NVIDIA RTX A5000", "count": 1}, "dataCenterId": "EU-RO-1", "ports": [],
            "mounts": {"persistent": {"size": size, "path": "/workspace"}}, "startedAt": "2026-10-31T10:00:00Z"}


pods = {"pA": mkpod("pA", "alpha", "RUNNING"), "pB": mkpod("pB", "beta", "RUNNING"), "pC": mkpod("pC", "gamma", "EXITED")}
ACTIONS = {"RUNNING": ["stop", "restart", "terminate"], "EXITED": ["start", "terminate"]}
inst = {201: {"id": 201, "label": "render-box", "actual_status": "running", "intended_status": "running", "cur_state": "running",
              "gpu_name": "RTX 4090", "num_gpus": 1, "dph_total": 0.4, "start_date": time.time() - 600, "gpu_util": 50.0,
              "disk_space": 60.0, "storage_cost": 0.3}}
user = {"balance": 0.0, "credit": 50.0}


class Mock(BaseHTTPRequestHandler):
    svc = "?"

    def log_message(self, *a):
        pass

    def end_headers(self):
        self.send_header("Connection", "close")   # see .ai/gotchas.md: HTTP/1.0 mocks must say so
        super().end_headers()

    def reply(self, code, obj=None):
        data = json.dumps(obj).encode() if obj is not None else b""
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(n) or b"{}")

    def do_GET(self):
        self.route("GET", None)

    def do_POST(self):
        self.route("POST", self.body())

    def do_PUT(self):
        self.route("PUT", self.body())

    def do_DELETE(self):
        self.route("DELETE", None)


class Runpod(Mock):
    def route(self, m, body):
        auth = self.headers.get("Authorization")
        p = self.path.split("?")[0]
        with lock:
            calls.append(("rp", m, p))
            if auth not in ("Bearer " + RP_KEY_RO, "Bearer " + RP_KEY_RW):
                return self.reply(401, {"detail": "invalid api key"})
            rw = auth == "Bearer " + RP_KEY_RW
            if m == "POST" and p == "/graphql":
                if faults["gql500"]:
                    return self.reply(500, {"errors": [{"message": "internal"}]})
                return self.reply(200, {"data": {"myself": dict(balance)}})
            if m == "GET" and p == "/v2/pods":
                return self.reply(200, {"pods": [dict(v, actions=ACTIONS.get(v["status"], [])) for v in pods.values()],
                                        "pagination": {"hasNextPage": False}})
            if m == "GET" and p == "/v2/billing/pods":
                return self.reply(200, {"records": [{"podId": "pA", "totalAmount": 1.5}]})
            mm = re.match(r"^/v2/pods/([^/]+)(/action)?$", p)
            if mm:
                pid, act = mm.group(1), mm.group(2)
                if m == "GET" and not act:
                    return self.reply(200, dict(pods[pid], actions=[])) if pid in pods else self.reply(404, {"detail": "not found"})
                if m in ("POST", "DELETE") and not rw:
                    return self.reply(403, {"detail": "api key lacks pods:write"})
                if pid not in pods:
                    return self.reply(404, {"detail": "pod not found"})
                if m == "DELETE":
                    del pods[pid]
                    return self.reply(204)
                if pid in faults["stop500"]:
                    return self.reply(500, {"detail": "internal error"})
                a = (body or {}).get("action")
                if a not in ACTIONS.get(pods[pid]["status"], []):
                    return self.reply(409, {"detail": "cannot " + str(a)})
                pods[pid]["status"] = "EXITED" if a == "stop" else "RUNNING"
                return self.reply(200, pods[pid])
        self.reply(404, {"detail": "no route"})


class Vast(Mock):
    def route(self, m, body):
        p = self.path.split("?")[0]
        with lock:
            calls.append(("va", m, p))
            if self.headers.get("Authorization") != "Bearer " + VA_KEY:
                return self.reply(401, {"success": False, "msg": "Invalid user key"})
            if m == "GET" and p == "/api/v1/instances/":
                return self.reply(200, {"instances": list(inst.values()), "next_token": None})
            if m == "GET" and p == "/api/v0/charges/":
                return self.reply(200, {"results": [{"source": "instance-201", "amount": 2.0}], "next_token": None})
            if m == "GET" and p == "/api/v0/users/current":
                return self.reply(200, dict(user))
            mm = re.match(r"^/api/v0/instances/(\d+)/$", p)
            if mm:
                i = int(mm.group(1))
                if m == "GET":
                    return self.reply(200, {"instances": inst.get(i)})
                if i not in inst:
                    return self.reply(200, {"success": False, "msg": f"Instance {i} not found."})
                if m == "DELETE":
                    del inst[i]
                    return self.reply(200, {"success": True})
                if (body or {}).get("state") == "stopped":
                    inst[i].update(intended_status="stopped", actual_status="exited", cur_state="stopped")
                return self.reply(200, {"success": True})
        self.reply(404, {"detail": "no route"})


# ---- the Telegram Bot API mock: only what the bridge uses
sent, answers, updates = [], [], []
tg = threading.Condition()
uid = [1000]


class Bot(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def end_headers(self):
        self.send_header("Connection", "close")
        super().end_headers()

    def reply(self, obj):
        data = json.dumps(obj).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        m = re.match(r"^/bot([^/]+)/(\w+)$", self.path)
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        body = {} if "multipart" in (self.headers.get("Content-Type") or "") else json.loads(raw or b"{}")
        method = m.group(2) if m and m.group(1) == TG_TOKEN else None
        if method == "getMe":
            return self.reply({"ok": True, "result": {"id": 1, "is_bot": True, "username": "guard_test_bot"}})
        if method == "getUpdates":
            off = body.get("offset", 0)
            with tg:
                if off == -1:
                    return self.reply({"ok": True, "result": []})
                tg.wait_for(lambda: any(u["update_id"] >= off for u in updates), timeout=min(body.get("timeout", 0), 2))
                return self.reply({"ok": True, "result": [u for u in updates if u["update_id"] >= off]})
        if method == "sendMessage":
            with tg:
                sent.append(body)
                tg.notify_all()
            return self.reply({"ok": True, "result": {"message_id": len(sent)}})
        if method == "answerCallbackQuery":
            with tg:
                answers.append(body)
                tg.notify_all()
            return self.reply({"ok": True, "result": True})
        self.reply({"ok": True, "result": True})


def push(update):
    with tg:
        uid[0] += 1
        update["update_id"] = uid[0]
        updates.append(update)
        tg.notify_all()


def wait_sent(pred, start=0, timeout=15):
    end = time.time() + timeout
    with tg:
        while time.time() < end:
            for s in sent[start:]:
                if pred(s):
                    return s
            tg.wait(0.2)
    return None


def n_sent(pred, start=0):
    with tg:
        return len([s for s in sent[start:] if pred(s)])


def buttons(msg):
    return {b["text"]: b["callback_data"] for row in (msg.get("reply_markup") or {}).get("inline_keyboard", []) for b in row}


def press(data, cid):
    n = len(answers)
    push({"callback_query": {"id": cid, "from": {"id": CHAT}, "message": {"message_id": 9, "chat": {"id": CHAT}}, "data": data}})
    end = time.time() + 10
    with tg:
        while time.time() < end:
            for a in answers[n:]:
                if a.get("callback_query_id") == cid:
                    return a.get("text", "")
            tg.wait(0.2)
    return None


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


def guard(cid="runpod"):
    return call("GET", f"/api/connectors/{cid}")[1].get("guard") or {}


def actions(svc, start=0, method=None):
    with lock:
        return [c for c in calls[start:] if c[0] == svc and (c[1] == method if method else c[1] in ("POST", "PUT", "DELETE"))
                and ("/action" in c[2] or c[2].startswith("/api/v0/instances/") or c[1] == "DELETE")]


def stops(svc, start=0):
    return [c[2] for c in actions(svc, start) if c[1] != "DELETE" and "pod_taskhub_probe" not in c[2] and c[2] != "/api/v0/instances/0/"]


NO_VAULT = vault.probe()
if NO_VAULT:
    print(f"SKIP balance guard tests: Credential Manager unavailable here ({NO_VAULT})")
    raise SystemExit(0)

ThreadingHTTPServer.request_queue_size = 64
servers = []
for port, h in ((47891, Runpod), (47890, Vast), (47897, Bot)):
    s0 = ThreadingHTTPServer(("127.0.0.1", port), h)
    threading.Thread(target=s0.serve_forever, daemon=True).start()
    servers.append(s0)
call("POST", "/api/settings", {"language": "en"})

# ---- Telegram linked to the test hub (the guard's messages and buttons go there)
s, r = call("POST", "/api/telegram", {"token": TG_TOKEN})
code = (r.get("link") or "").rsplit("start=", 1)[-1]
push({"message": {"message_id": 1, "chat": {"id": CHAT, "type": "private"}, "from": {"id": CHAT}, "text": "/start " + code}})
check("Telegram linked for the guard test", wait_sent(lambda m: m["chat_id"] == CHAT) is not None, (s, r))

# ---- config: validation, defaults, off by default, old keys untouched
call("POST", "/api/connectors/runpod", {"forget_key": True, "enabled": False, "guard": False, "guard_exempt": []})
st = call("GET", "/api/connectors/runpod")[1]
g = st.get("guard") or {}
check("guard off by default; defaults warn $10 / stop $2 / grace 10 / snooze 15", g.get("enabled") is False and g.get("warn") == 10
      and g.get("stop") == 2 and g.get("grace_min") == 10 and g.get("snooze_min") == 15 and g.get("pending") is None, g)
old = (st.get("low_runway_hours"), st.get("low_balance"))
for body, what in (({"guard_warn": 5, "guard_stop": 10}, "warn below stop"), ({"guard_warn": 10, "guard_stop": 10}, "warn = stop"),
                   ({"guard_stop": -1}, "stop < 0"), ({"guard_stop": 50}, "stop above the default warn"),
                   ({"guard_warn": "abc"}, "warn not a number"), ({"guard_grace_min": 0}, "grace 0"),
                   ({"guard_snooze_min": 1000}, "snooze 1000"), ({"guard_exempt": "pA"}, "exempt not a list")):
    s, r = call("POST", "/api/connectors/runpod", body)
    check(f"guard config: {what} -> 400, clear error", s == 400 and "guard_" in r.get("error", ""), (s, r))
check("rejected guard config changed nothing", guard() == g, guard())
s, r = call("POST", "/api/connectors/runpod", {"enabled": True, "key": RP_KEY_RW, "list_interval": 1, "detail_interval": 1,
                                               "billing_interval": 1, "guard": True, "guard_warn": 20, "guard_stop": 10,
                                               "guard_grace_min": 0.5, "guard_snooze_min": 0.1, "guard_exempt": ["pB"]})
g = r.get("guard") or {}
check("guard on: warn $20, stop $10, grace 0.5, snooze 0.1, exempt pB", s == 200 and g.get("enabled") is True and g.get("warn") == 20
      and g.get("stop") == 10 and g.get("grace_min") == 0.5 and g.get("snooze_min") == 0.1 and g.get("exempt") == ["pB"], (s, r))
check("old low_runway_hours / low_balance untouched", (r.get("low_runway_hours"), r.get("low_balance")) == old, (r.get("low_runway_hours"), old))
check("machines -> tasks", wait(lambda: (task("rp-pA") or {}).get("state") == "running" and (task("rp-pB") or {}).get("state") == "running"),
      (task("rp-pA"), task("rp-pB")))
check("actions allowed (write key)", wait(lambda: call("GET", "/api/connectors/runpod")[1].get("can_act") is True), guard())
check("burn = Runpod's currentSpendPerHr; hours to stop = (50 - 10) / 1", wait(lambda: guard().get("burn_per_hr") == 1
      and guard().get("hours_to_stop") == 40), guard())
v = call("GET", "/api/connectors?view=settings")[1]
rw = next((c for c in v.get("connectors", []) if c["id"] == "runpod"), {})
gr = rw.get("guard") or {}
check("settings row: forecast 'reaches the stop threshold $10.0 at …'", "reaches the stop threshold $10.0 at" in (gr.get("forecast") or ""), gr)
check("settings row: machines with 'never auto-stop' (beta on)", any(m["id"] == "pB" and m["exempt"] for m in gr.get("machines", []))
      and any(m["id"] == "pA" and not m["exempt"] for m in gr.get("machines", [])), gr.get("machines"))
check("exempt machine says so on its card", wait(lambda: ((next((w for w in (task("rp-pB") or {}).get("widgets", []) if w["id"] == "info"), {})
      .get("value") or {}).get("Auto-stop") == "never")), task("rp-pB"))

# ---- warning: once per crossing, with balance / burn / hours; re-armed after a top-up
n0 = len(sent)
with lock:
    balance["clientBalance"] = 15.0
w1 = wait_sent(lambda m: "below the warning threshold" in m["text"], n0)
check("warning -> Telegram: balance, burn, hours until the stop threshold", w1 is not None and "$15.0" in w1["text"]
      and "$1.00/h" in w1["text"] and "5 h" in w1["text"] and "$10.0" in w1["text"], w1)
time.sleep(4)
check("warning only once per crossing", n_sent(lambda m: "below the warning threshold" in m["text"], n0) == 1,
      n_sent(lambda m: "below the warning threshold" in m["text"], n0))
check("no old low-balance warning while the guard is on", n_sent(lambda m: "balance is low" in m["text"], n0) == 0)
with lock:
    balance["clientBalance"] = 50.0
check("top-up -> warning re-armed", wait(lambda: guard().get("warned") is False), guard())
n1 = len(sent)
with lock:
    balance["clientBalance"] = 16.0
check("warning again after the top-up", wait_sent(lambda m: "below the warning threshold" in m["text"], n1) is not None)

# ---- pending stop #1: message with both buttons; snooze moves the deadline (capped at the projected $0); top-up cancels
a0 = len(calls)
n2 = len(sent)
with lock:
    balance["clientBalance"] = 8.0
p1 = wait_sent(lambda m: "reached the auto-stop threshold" in m["text"], n2)
b1 = buttons(p1 or {})
check("pending stop -> Telegram with Stop now / Snooze, names the machines (not the exempt one)", p1 is not None
      and "Stop now" in b1 and "Snooze 0.1 min" in b1 and "alpha" in p1["text"] and "beta" not in p1["text"] and "gamma" not in p1["text"], p1)
g = wait(lambda: guard().get("pending"))
check("pending: deadline ≈ now + grace (30 s), machines [pA]", g and abs(g["deadline"] - time.time() - 30) < 6 and g["machines"] == ["pA"], g)
gt = task("rp--guard")
check("pending stop shown as a task with both buttons", gt and gt["state"] == "running" and gt["source"] == "runpod"
      and [c["id"].split(".")[0] for c in gt["controls"]] == ["gstop", "gsnooze"] and "auto-stop at" in gt.get("attention", ""), gt)
d0 = g["deadline"] if g else 0
ans = press(b1.get("Snooze 0.1 min"), "snooze1")
g2 = guard().get("pending") or {}
check("Snooze (Telegram) -> deadline + 6 s, the reply says until when", ans and "snoozed until" in ans and abs(g2.get("deadline", 0) - d0 - 6) < 1.5, (ans, g2, d0))
with lock:
    balance["currentSpendPerHr"] = 3600.0   # $1 a second: $0 in 8 s
check("burn rises", wait(lambda: guard().get("burn_per_hr") == 3600), guard())
ans = press(b1.get("Snooze 0.1 min"), "snooze2")
g3 = guard().get("pending") or {}
check("Snooze capped at the projected $0 (said so)", ans and "Not later" in ans and g3.get("capped") is True
      and g3.get("deadline", 1e12) <= time.time() + 8.5, (ans, g3))
with lock:
    balance["clientBalance"], balance["currentSpendPerHr"] = 50.0, 1.0
check("top-up above stop -> pending stop cancelled, said so", wait_sent(lambda m: "auto-stop cancelled" in m["text"], n2) is not None
      and wait(lambda: guard().get("pending") is None), guard())
check("cancelled: the guard task finished, no buttons", wait(lambda: (task("rp--guard") or {}).get("state") == "done"
      and not task("rp--guard")["controls"]), task("rp--guard"))
check("stale button -> 'Already handled', nothing stopped", "Already handled" in (press(b1.get("Stop now"), "stale1") or "")
      and stops("rp", a0) == [], stops("rp", a0))

# ---- pending stop #2: Stop now (Telegram) stops alpha only (beta never auto-stop, gamma already stopped)
check("re-armed", wait(lambda: guard().get("warned") is False), guard())
n3 = len(sent)
with lock:
    balance["clientBalance"] = 8.0
p2 = wait_sent(lambda m: "reached the auto-stop threshold" in m["text"], n3)
b2 = buttons(p2 or {})
check("pending stop #2 with new buttons", p2 is not None and b2.get("Stop now") and b2.get("Stop now") != b1.get("Stop now"), b2)
ans = press(b2.get("Stop now"), "stopnow")
check("Stop now -> 'Stopping now…'", ans and "Stopping" in ans, ans)
done = wait_sent(lambda m: "Auto-stop — stopped: 1, failed: 0" in m["text"], n3, timeout=20)
check("summary: alpha stopped, storage while stopped ≈ $0.13/day per the Runpod price list", done is not None and "alpha" in done["text"]
      and "≈ $0.13/day" in done["text"] and "Runpod price list" in done["text"], done)
check("only alpha stopped (POST action stop); beta / gamma untouched", stops("rp", a0) == ["/v2/pods/pA/action"], stops("rp", a0))
check("alpha's task finished 'stopped'", wait(lambda: (task("rp-pA") or {}).get("state") == "done"), task("rp-pA"))
s, r = call("GET", "/api/guard/log?provider=runpod")
e = (r.get("entries") or [{}])[0]
check("log: when, machine, why, spent, result, storage ≈", s == 200 and e.get("machine_id") == "pA" and e.get("name") == "alpha"
      and e.get("trigger") == "stop_now" and e.get("result") == "stopped" and e.get("threshold") == 10 and e.get("balance") == 8
      and e.get("burn_per_hr") == 1 and e.get("spent") == 1.5 and abs((e.get("storage_per_day") or 0) - 20 * 0.2 / 30) < 1e-3
      and e.get("storage_approx") is True and e.get("t"), r)

# ---- pending stop #3: no answer -> stopped after the (fractional) grace; one machine fails, the other still stops
with lock:
    pods["pA"]["status"] = "RUNNING"
    pods["pD"] = mkpod("pD", "delta", "RUNNING", size=0)
    faults["stop500"].add("pD")
    balance["clientBalance"] = 50.0
call("POST", "/api/connectors/runpod", {"guard_grace_min": 0.05})
check("alpha / delta running", wait(lambda: (task("rp-pA") or {}).get("state") == "running" and (task("rp-pD") or {}).get("state") == "running"),
      (task("rp-pA"), task("rp-pD")))
a3 = len(calls)
n4 = len(sent)
with lock:
    balance["clientBalance"] = 8.0
check("pending stop #3", wait_sent(lambda m: "reached the auto-stop threshold" in m["text"], n4) is not None)
done = wait_sent(lambda m: "Auto-stop — stopped: 1, failed: 1" in m["text"], n4, timeout=25)
check("no answer -> auto-stop after the grace: alpha stopped, delta failed (reported, not success)", done is not None
      and re.search(r"delta — failed: HTTP 500", done["text"]) and "alpha — stopped" in done["text"], done)
check("both stop requests sent, beta still untouched", sorted(stops("rp", a3)) == ["/v2/pods/pA/action", "/v2/pods/pD/action"], stops("rp", a3))
r = call("GET", "/api/guard/log?provider=runpod&limit=2")[1]
check("log: delta failed with the error, alpha stopped (auto)", len(r.get("entries", [])) == 2
      and any(x["machine_id"] == "pD" and x["result"] == "failed" and "500" in (x.get("error") or "") for x in r["entries"])
      and any(x["machine_id"] == "pA" and x["result"] == "stopped" and x["trigger"] == "auto" for x in r["entries"]), r)
check("guard task failed (one machine failed)", wait(lambda: (task("rp--guard") or {}).get("state") == "failed"), task("rp--guard"))
time.sleep(3)
check("the failed machine is not tried again in the same crossing", guard().get("pending") is None and stops("rp", a3).count("/v2/pods/pD/action") == 1,
      (guard(), stops("rp", a3)))

# ---- balance unreadable: warning, nothing stopped at the deadline, readable again
with lock:
    faults["stop500"].clear()
    pods["pA"]["status"] = "RUNNING"
    del pods["pD"]
    balance["clientBalance"] = 50.0
check("alpha running again, re-armed", wait(lambda: (task("rp-pA") or {}).get("state") == "running" and guard().get("warned") is False), guard())
a4 = len(calls)
n5 = len(sent)
with lock:
    balance["clientBalance"] = 8.0
check("pending stop #4", wait(lambda: guard().get("pending")) is not None, guard())
with lock:
    faults["gql500"] = True
held = wait_sent(lambda m: "auto-stop is held" in m["text"], n5, timeout=20)
check("deadline with the balance unreadable -> held, said so", held is not None and guard().get("pending", {}).get("held") is True, (held, guard()))
out = wait_sent(lambda m: "can't be read (HTTP 500)" in m["text"], n5, timeout=15)
check("balance unreadable for > 2 polls -> one warning", out is not None, sent[n5:])
time.sleep(3)
check("…only one per outage; nothing stopped while unreadable", n_sent(lambda m: "can't be read (" in m["text"], n5) == 1 and stops("rp", a4) == [],
      (n_sent(lambda m: "can't be read (" in m["text"], n5), stops("rp", a4)))
with lock:
    faults["gql500"] = False
    balance["clientBalance"] = 50.0
check("readable again -> said so", wait_sent(lambda m: "can be read again" in m["text"], n5) is not None)
check("readable again + top-up -> held stop cancelled, nothing stopped", wait(lambda: guard().get("pending") is None) and stops("rp", a4) == [],
      (guard(), stops("rp", a4)))

# ---- read-only key: warning only, no stop attempt
s, r = call("POST", "/api/connectors/runpod", {"key": RP_KEY_RO})
check("read-only key -> can_act false", s == 200 and wait(lambda: call("GET", "/api/connectors/runpod")[1].get("can_act") is False), r)
v = call("GET", "/api/connectors?view=settings")[1]
gr = next((c for c in v.get("connectors", []) if c["id"] == "runpod"), {}).get("guard") or {}
check("settings row says the guard can only warn", "can only warn" in (gr.get("note") or ""), gr)
check("re-armed", wait(lambda: guard().get("warned") is False), guard())
a5 = len(calls)
n6 = len(sent)
with lock:
    balance["clientBalance"] = 8.0
ro = wait_sent(lambda m: "below the auto-stop threshold" in m["text"], n6)
check("read-only: 'below the auto-stop threshold' + can only warn", ro is not None and "can only warn" in ro["text"], ro)
time.sleep(3)
check("read-only: no pending stop, no stop request", guard().get("pending") is None and stops("rp", a5) == [], (guard(), stops("rp", a5)))
with lock:
    check("never a DELETE (terminate) sent to Runpod", not any(c[0] == "rp" and c[1] == "DELETE" for c in calls))

# ---- Vast.ai: auto-stop = PUT state stopped (never destroy), storage_cost × disk_space ≈ per day
with lock:
    balance["clientBalance"] = 50.0
call("POST", "/api/connectors/runpod", {"guard": False, "enabled": False, "forget_key": True, "guard_exempt": []})
s, r = call("POST", "/api/connectors/vastai", {"enabled": True, "key": VA_KEY, "list_interval": 1, "detail_interval": 1, "billing_interval": 1,
                                               "guard": True, "guard_warn": 20, "guard_stop": 10, "guard_grace_min": 0.05})
check("Vast.ai: guard on", s == 200 and (r.get("guard") or {}).get("enabled") is True, (s, r))
check("Vast.ai: instance running, actions allowed", wait(lambda: (task("va-201") or {}).get("state") == "running"
      and call("GET", "/api/connectors/vastai")[1].get("can_act") is True), task("va-201"))
v0 = len(calls)
n7 = len(sent)
with lock:
    user["credit"] = 8.0
done = wait_sent(lambda m: "Auto-stop — stopped: 1, failed: 0" in m["text"] and "render-box" in m["text"], n7, timeout=25)
check("Vast.ai: auto-stop, storage while stopped ≈ $0.60/day per the Vast.ai price list", done is not None
      and "≈ $0.60/day" in done["text"] and "Vast.ai price list" in done["text"], done)
with lock:
    va = [c for c in calls[v0:] if c[0] == "va" and c[1] in ("PUT", "DELETE") and c[2] != "/api/v0/instances/0/"]
check("Vast.ai: PUT /instances/201/ only, never DELETE (destroy)", va == [("va", "PUT", "/api/v0/instances/201/")], va)
r = call("GET", "/api/guard/log?provider=vastai")[1]
e = (r.get("entries") or [{}])[0]
check("log: Vast.ai entry, storage 0.3 × 60 / 30", e.get("provider") == "vastai" and abs((e.get("storage_per_day") or 0) - 0.6) < 1e-6, r)
r = call("GET", "/api/guard/log")[1]
check("log: all providers, newest first", [x["provider"] for x in r.get("entries", [])][:1] == ["vastai"] and len(r["entries"]) == 4, r)

# ---- clean up (the test hub only)
call("POST", "/api/connectors/vastai", {"guard": False, "enabled": False, "forget_key": True})
for tid in ["rp-pA", "rp-pB", "rp-pC", "rp-pD", "rp--guard", "va-201", "va--guard"]:
    call("DELETE", f"/api/tasks/{tid}?force=1")
call("POST", "/api/telegram", {"disconnect": True})
for s0 in servers:
    s0.shutdown()
    s0.server_close()
print(f"{fails} failure(s)")
raise SystemExit(1 if fails else 0)
