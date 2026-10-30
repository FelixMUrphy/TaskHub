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

# ---- shell controls: a typed value is plain text for cmd.exe (2.9: TASKHUB_VALUE + delayed expansion), never extra commands
import tempfile
shdir = tempfile.mkdtemp(prefix="taskhub-shinj-")
call("POST", "/api/tasks", {"id": "v-inj", "name": "inject", "controls": [
    {"id": "plain", "kind": "shell", "command": "echo [{value}]", "cwd": shdir, "input": {"type": "text"}},
    {"id": "quoted", "kind": "shell", "command": 'echo "[{value}]"', "cwd": shdir, "input": {"type": "text"}},
    {"id": "lst", "kind": "shell", "cwd": shdir, "input": {"type": "text"},
     "command": [sys.executable, "-c", "import os,sys;print('ARG', sys.argv[1], 'ENV', os.environ.get('TASKHUB_VALUE'))", "{value}"]}]})


def run_ctl(cid, value):
    """Presses a shell control and returns the log lines of that run."""
    def ends(t):
        return sum(l["msg"].startswith(f"[{cid}] exit") or l["msg"].startswith(f"[{cid}] failed") for l in t["log"])
    _, t0 = call("GET", "/api/tasks/v-inj")
    n0 = ends(t0)
    s, r = call("POST", f"/api/tasks/v-inj/controls/{cid}", {"value": value})
    if s != 200:
        return [f"HTTP {s} {r}"]
    for _ in range(80):
        time.sleep(0.15)
        _, t = call("GET", "/api/tasks/v-inj")
        if ends(t) > n0:
            msgs = [l["msg"] for l in t["log"]]
            start = max(i for i, m in enumerate(msgs) if m.startswith("⇢ "))
            return msgs[start + 1:]
    return ["timeout"]


evil = ["a & echo PWNED > pwn.txt", "%PATH%", '"quoted"', "^", "|", "<", ">", "!PATH!", 'x"&echo PWNED>pwn2.txt&"',
        "a | echo PWNED > pwn3.txt", "^& echo PWNED > pwn4.txt"]
for v in evil:
    out = run_ctl("plain", v)
    check(f"shell value as text: {v!r}", f"[plain] [{v}]" in out and "[plain] exit 0" in out, out)
    out = run_ctl("quoted", v)
    check(f"shell value in quotes as text: {v!r}", f'[quoted] "[{v}]"' in out and "[quoted] exit 0" in out, out)
out = run_ctl("plain", "")
check("shell: empty value -> nothing", "[plain] []" in out, out)
out = run_ctl("lst", 'a & b "c" %PATH%')
check("shell list: value is one argument + TASKHUB_VALUE", '[lst] ARG a & b "c" %PATH% ENV a & b "c" %PATH%' in out, out)
check("shell: no injected command ran", not [f for f in os.listdir(shdir) if f.startswith("pwn")], os.listdir(shdir))

# list form: {value} can't choose the program; .bat / .cmd programs can't take {value}
for cmd in (["{value}"], ["x.bat", "{value}"], ["C:/t/run.CMD. ", "--x={value}"]):
    s, r = call("POST", "/api/tasks", {"id": "v-inj2", "name": "x", "controls": [{"id": "c", "kind": "shell", "command": cmd}]})
    check(f"shell list {cmd!r} -> 400", s == 400, (s, r))
s, r = call("POST", "/api/tasks", {"id": "v-inj2", "name": "x", "controls": [{"id": "c", "kind": "shell", "command": ["x.bat", "fixed", "{task_id}"]}]})
check("shell list .bat without {value} -> 200", s == 200, (s, r))

# ---- links: only http / https / mailto / file or an absolute path (2.9); others -> 400, an update stays atomic
good = ["https://example.com/x", "http://127.0.0.1:8080/", "mailto:a@example.com", "file:///E:/proj/out", "E:/proj/out",
        "\\\\server\\share\\x", {"label": "r", "url": "file:///C:/Windows"}, {"label": "empty", "url": ""}]
s, r = call("POST", "/api/tasks", {"id": "v-lnk", "name": "links", "links": good})
check("links: allowed schemes -> 200", s == 200, (s, r))
for bad in ("javascript:alert(1)", "ms-settings:privacy", "taskhub://x", "search-ms:query=x", "relative/path.exe",
            {"label": "x", "url": "vbscript:x"}, 5):
    s, r = call("POST", "/api/tasks", {"id": "v-lnk2", "name": "links", "links": [bad]})
    check(f"links: {bad!r} -> 400", s == 400, (s, r))
s, r = call("POST", "/api/tasks", {"id": "v-lnk2", "name": "links", "links": {"url": "https://x"}})
check("links: not a list -> 400", s == 400, (s, r))
s, _ = call("POST", "/api/tasks/v-lnk/update", {"status": "changed", "links": ["https://ok", "javascript:x"]})
_, t = call("GET", "/api/tasks/v-lnk")
check("links: bad update -> 400, nothing changed", s == 400 and t["status"] == "" and len(t["links"]) == len(good), (s, t["status"], t["links"]))
s, _ = call("POST", "/api/tasks/v-lnk/update", {"links": ["https://example.org"]})
_, t = call("GET", "/api/tasks/v-lnk")
check("links: good update replaces", s == 200 and t["links"] == ["https://example.org"], t["links"])

# ---- a pid whose start time can't be read (not a Windows pid, e.g. from WSL): no automatic Kill, a declared one refused
s, r = call("POST", "/api/tasks", {"id": "v-pid", "name": "wsl pid", "pid": 99999996})
_, t = call("GET", "/api/tasks/v-pid")
check("unknown pid: no automatic Kill control", s == 200 and not any(c["id"] == "kill" for c in t["controls"]), t and t["controls"])
call("POST", "/api/tasks", {"id": "v-pid2", "name": "wsl pid", "pid": 99999996, "controls": [{"id": "k", "kind": "kill"}]})
s, r = call("POST", "/api/tasks/v-pid2/controls/k", {})
_, t = call("GET", "/api/tasks/v-pid2")
check("unknown pid: Kill refused (409), task not cancelled", s == 409 and "creation time" in str(r) and t["state"] != "cancelled", (s, r, t["state"]))
call("POST", "/api/tasks", {"id": "v-pid3", "name": "own pid", "pid": os.getpid()})
_, t = call("GET", "/api/tasks/v-pid3")
check("Windows pid: automatic Kill control", any(c["id"] == "kill" for c in t["controls"]), t["controls"])

# ---- python client: its own pid only on Windows Python (WSL / MSYS / Cygwin pids aren't Windows pids)
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "clients", "python"))
import taskhub_client
real_platform = sys.platform
try:
    sys.platform = "linux"
    t_l = taskhub_client.Task("x", poll=False)
    sys.platform = "win32"
    t_w = taskhub_client.Task("x", poll=False)
finally:
    sys.platform = real_platform
check("python client: no pid outside Windows", "pid" not in t_l._reg, t_l._reg.get("pid"))
check("python client: own pid on Windows", t_w._reg.get("pid") == os.getpid(), t_w._reg.get("pid"))

for tid in ("v-nan", "v-nan2", "v-xss", "v-val", "v-atom", "v-sh", "v-inj", "v-inj2", "v-lnk", "v-lnk2", "v-pid", "v-pid2", "v-pid3"):
    call("DELETE", f"/api/tasks/{tid}?force=1")
print("\n%d failure(s)" % fails)
sys.exit(1 if fails else 0)
