"""Input validation of the TaskHub API (TASKHUB_URL, default the test hub on 47899): non-finite numbers, widget attributes
that end up in the panel's HTML, control values, atomic updates, shell controls. Prints PASS/FAIL per check.
Run via tests\\run_tests.cmd - it starts an isolated hub."""
import json, os, sys, time, urllib.request, urllib.error

U = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899").rstrip("/")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def call(method, path, body=None):
    h = {"Content-Type": "application/json"}
    if method != "GET":
        h["X-TaskHub"] = "1"
    data = json.dumps(body).encode() if body is not None else (b"{}" if method != "GET" else None)
    req = urllib.request.Request(U + path, data=data, method=method, headers=h)
    try:
        r = op.open(req, timeout=10)
        return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        b = e.read()
        try:
            return e.code, json.loads(b)
        except Exception:
            return e.code, b


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


# ---- NaN / Infinity as numeric strings must not reach the state (they can't be serialized -> /api/tasks would be 500)
call("POST", "/api/tasks", {"id": "v-nan", "name": "nan", "total": 10})
for body in ({"progress": "NaN"}, {"percent": "Infinity"}, {"current": "-Infinity"}):
    call("POST", "/api/tasks/v-nan/update", body)
call("POST", "/api/tasks", {"id": "v-nan2", "name": "nan", "heartbeat_timeout": "NaN"})
s, _ = call("GET", "/api/tasks")
check("NaN / Infinity strings: task list still 200", s == 200, s)

# ---- widget attributes go into the panel's style / SVG / input attributes: only typed values may get through
evil = 'red"><img src=x onerror=alert(1)>'
call("POST", "/api/tasks", {"id": "v-xss", "name": "xss", "widgets": [
    {"id": "p", "type": "progress", "value": 5, "total": 10, "color": evil},
    {"id": "g", "type": "gauge", "value": 5, "color": "#ff8800"},
    {"id": "c", "type": "chart", "height": '60"><script>', "decimals": "2"}],
    "controls": [{"id": "n", "label": "N", "input": {"type": "number", "min": '0" onfocus="x', "max": 10}}]})
_, t = call("GET", "/api/tasks/v-xss")
w = {x["id"]: x for x in t["widgets"]}
check("widget: markup in color dropped", "color" not in w["p"], w["p"])
check("widget: plain CSS color kept", w["g"].get("color") == "#ff8800", w["g"])
check("widget: non-numeric height dropped", "height" not in w["c"], w["c"])
check("widget: numeric string decimals -> number", w["c"].get("decimals") == 2, w["c"])
inp = t["controls"][0]["input"]
check("number input: non-numeric min dropped, max kept", "min" not in inp and inp.get("max") == 10, inp)
call("POST", "/api/tasks/v-xss/update", {"widgets": {"p": {"value": 6, "color": evil}}})
_, t = call("GET", "/api/tasks/v-xss")
check("widget value update: markup in color dropped", "color" not in t["widgets"][0], t["widgets"][0])

# ---- control values are checked by the hub (same rules for panel, toasts, Telegram, API)
call("POST", "/api/tasks", {"id": "v-val", "name": "values", "controls": [
    {"id": "num", "input": {"type": "number", "min": 0, "max": 10}},
    {"id": "sel", "input": {"type": "select", "options": ["a", "b"]}},
    {"id": "flag", "input": {"type": "bool"}},
    {"id": "txt", "input": {"type": "text"}}]})
for cid, v, want in [("num", 5, 200), ("num", 42, 400), ("num", "abc", 400), ("num", "NaN", 400),
                     ("sel", "a", 200), ("sel", "zzz", 400), ("flag", True, 200), ("flag", "yes", 400),
                     ("txt", 12, 200)]:
    s, r = call("POST", f"/api/tasks/v-val/controls/{cid}", {"value": v})
    check(f"control {cid}={v!r} -> {want}", s == want, (s, r))
_, cmds = call("GET", "/api/tasks/v-val/commands?wait=0")
vals = [c["value"] for c in cmds["commands"]]
check("only valid values queued, text as a string", vals == [5, "a", True, "12"], vals)

# ---- a rejected update changes nothing
call("POST", "/api/tasks", {"id": "v-atom", "name": "atomic"})
call("POST", "/api/tasks/v-atom/update", {"status": "before"})
s, _ = call("POST", "/api/tasks/v-atom/update", {"status": "changed", "controls": [{"id": "bad id"}]})
_, t = call("GET", "/api/tasks/v-atom")
check("invalid update -> 400, status unchanged", s == 400 and t["status"] == "before", (s, t["status"]))

# ---- shell controls: only the tail of a long output is kept; the timeout holds
call("POST", "/api/tasks", {"id": "v-sh", "name": "shell", "controls": [
    {"id": "big", "kind": "shell", "command": "for /L %i in (1,1,3000) do @echo line %i", "timeout": 30},
    {"id": "slow", "kind": "shell", "command": "ping -n 30 127.0.0.1 >nul", "timeout": 1}]})
call("POST", "/api/tasks/v-sh/controls/big", {})
call("POST", "/api/tasks/v-sh/controls/slow", {})
msgs = []
for _ in range(60):
    time.sleep(0.25)
    _, t = call("GET", "/api/tasks/v-sh")
    msgs = [l["msg"] for l in t["log"]]
    if "[big] exit 0" in msgs and any(m.startswith("[slow] failed") for m in msgs):
        break
check("shell: last line and exit code logged", "[big] line 3000" in msgs and "[big] exit 0" in msgs, msgs[-5:])
check("shell: output tail only (25 lines)", sum(m.startswith("[big] line ") for m in msgs) == 25, len(msgs))
check("shell: timeout after 1s", "[slow] failed: timeout after 1s" in msgs, msgs[-5:])

for tid in ("v-nan", "v-nan2", "v-xss", "v-val", "v-atom", "v-sh"):
    call("DELETE", f"/api/tasks/{tid}?force=1")
print("\n%d failure(s)" % fails)
sys.exit(1 if fails else 0)
