"""Exercise the TaskHub HTTP API (TASKHUB_URL, default the test hub on 47899). Prints PASS/FAIL per check.
Run via tests\run_tests.cmd - it starts an isolated hub; do not point it at your main hub (it clears finished tasks)."""
import json, os, sys, threading, time, urllib.request, urllib.error

U = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899").rstrip("/")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def call(method, path, body=None, headers=None, raw=False, timeout=10):
    h = {"Content-Type": "application/json"}
    if method != "GET":
        h["X-TaskHub"] = "1"
    h.update(headers or {})
    data = json.dumps(body).encode() if body is not None else (b"{}" if method != "GET" else None)
    req = urllib.request.Request(U + path, data=data, method=method, headers=h)
    try:
        r = op.open(req, timeout=timeout)
        b = r.read()
        return r.status, (b if raw else json.loads(b or b"null"))
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


s, h = call("GET", "/api/health")
check("health", s == 200 and h["app"] == "TaskHub", h)

# guards
s, r = call("POST", "/api/notify", {"title": "x"}, headers={"X-TaskHub": ""})
check("csrf header required", s == 403, (s, r))
s, r = call("GET", "/api/health", headers={"Origin": "http://evil.com"})
check("cross-origin rejected", s == 403, (s, r))
s, r = call("GET", "/api/health", headers={"Host": "evil.com"})
check("bad host rejected", s == 403, (s, r))
# Claude Code HTTP hooks: always 200 + {} (never blocks the agent)
for ev in ({"hook_event_name": "Stop", "cwd": "C:\\proj\\demo", "stop_hook_active": False,
            "last_assistant_message": "## Done\n**Fixed** the `bug` in [Api.cs](x)\n```cs\ncode\n```"},
           {"hook_event_name": "Notification", "cwd": "/home/u/demo", "message": "Claude needs your permission to use Bash",
            "notification_type": "permission_prompt"},
           {"hook_event_name": "PreToolUse", "tool_name": "Bash"}, {}):
    s, r = call("POST", "/api/hooks/claude", ev)
    check(f"claude hook {ev.get('hook_event_name', 'empty')}", s == 200 and r == {}, (s, r))
for ev in ({"hook_event_name": "Stop", "cwd": "C:\\proj\\demo", "last_assistant_message": "Готово: тести пройшли"},
           {"hook_event_name": "PermissionRequest", "tool_name": "Bash", "tool_input": {"command": "rm -rf build"}},
           {"type": "agent-turn-complete", "last-assistant-message": "All tests pass"}):
    s, r = call("POST", "/api/hooks/codex", ev)
    check(f"codex hook {ev.get('hook_event_name') or ev.get('type')}", s == 200 and r == {}, (s, r))
dup = {"hook_event_name": "Stop", "session_id": "s1", "prompt_id": "p1", "last_assistant_message": "x"}
s1, r1 = call("POST", "/api/hooks/claude", dup)
s2, r2 = call("POST", "/api/hooks/claude", dup)
check("duplicate hook event accepted silently", s1 == s2 == 200 and r1 == r2 == {}, (s1, r1, s2, r2))
s, r = call("POST", "/api/hooks/other", {})
check("unknown agent -> 404", s == 404, (s, r))
# CLI: `taskhub hook` is silent and always exits 0 (stdin JSON, JSON as last arg, garbage, hub down)
import subprocess, os
cli = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app", "taskhub.exe")
if os.path.exists(cli):
    env = dict(os.environ)
    for name, argv, inp, url in (("stdin", ["hook", "codex"], '{"hook_event_name":"Stop","last_assistant_message":"Привіт"}', None),
                                 ("last arg", ["hook", "codex", '{"type":"agent-turn-complete"}'], None, None),
                                 ("garbage", ["hook", "claude"], "not json", None),
                                 ("hub down", ["hook", "claude"], '{"hook_event_name":"Stop"}', "http://127.0.0.1:1")):
        e = dict(env, **({"TASKHUB_URL": url} if url else {}))
        p = subprocess.run([cli] + argv, input=(inp or "").encode("utf-8"), capture_output=True, env=e, timeout=15)
        check(f"cli hook {name}: exit 0, no output", p.returncode == 0 and not p.stdout and not p.stderr, (p.returncode, p.stdout, p.stderr))
s, r = call("GET", "/api/nope")
check("404 route", s == 404 and "error" in r, (s, r))
s, r = call("POST", "/api/tasks", None, raw=False)
check("register without name ok (defaults)", s == 200, (s, r))
call("DELETE", f"/api/tasks/{r.get('id')}?force=1")

# register with widgets + controls
reg = {
    "id": "t-api", "name": "API test", "source": "test", "total": 10, "unit": "it",
    "notify": {"finish": False, "fail": False, "milestones": [50]},
    "widgets": [
        {"id": "c", "type": "chart", "label": "Loss", "series": ["a", "b"]},
        {"id": "g", "type": "gauge", "label": "GPU", "unit": "%", "warn": 80},
        {"id": "s", "type": "stat", "label": "Speed"},
        {"id": "l", "type": "led", "label": "Lic"},
        {"id": "k", "type": "kv", "label": "KV"},
        {"id": "tb", "type": "table", "columns": ["x", "y"]},
        {"id": "p", "type": "progress", "label": "Sub", "total": 5},
        {"id": "tx", "type": "text", "label": "Txt", "mono": True},
    ],
    "controls": [
        {"id": "sig", "label": "Signal", "kind": "signal", "input": {"type": "number", "default": 1}},
        {"id": "sh", "label": "Shell", "kind": "shell", "command": "echo hello {value} {task_id}"},
        {"id": "sel", "label": "Sel", "kind": "signal", "input": {"type": "select", "options": ["a", "b"]}, "toast": True},
    ],
}
s, r = call("POST", "/api/tasks", reg)
check("register", s == 200 and r["id"] == "t-api", (s, r))
s, r = call("POST", "/api/tasks", {"name": "bad", "widgets": [{"id": "x", "type": "nope"}]})
check("bad widget type -> 400", s == 400, (s, r))
s, r = call("POST", "/api/tasks", {"name": "bad", "id": "bad id!"})
check("bad id -> 400", s == 400, (s, r))

s, r = call("POST", "/api/tasks/t-api/update", {"current": 5, "status": "half", "stage": "st",
            "metrics": {"m1": 1.5, "m2": "x"}, "log": ["l1", "l2"],
            "widgets": {"c": [{"t": 1, "a": 1, "b": 2}, {"t": 2, "a": 2, "b": 3}], "g": 85, "s": 10, "l": "ok",
                        "k": {"a": 1}, "tb": [[1, 2], [3, 4]], "p": 3, "tx": "hello"}})
check("update", s == 200, (s, r))
call("POST", "/api/tasks/t-api/update", {"widgets": {"s": 12, "c": {"a": 5, "b": 6}}})
s, t = call("GET", "/api/tasks/t-api")
w = {x["id"]: x for x in t["widgets"]}
check("progress from current/total", abs(t["progress"] - 0.5) < 1e-9, t["progress"])
check("milestone hit", 50 in t["milestones_hit"], t["milestones_hit"])
check("chart batch + append", len(w["c"]["points"]) == 3 and w["c"]["points"][0][0] == 1 and w["c"]["points"][2][1:] == [5, 6], w["c"]["points"])
check("stat prev", w["s"]["value"] == 12 and w["s"]["prev"] == 10, w["s"])
check("metrics", t["metrics"] == {"m1": 1.5, "m2": "x"}, t["metrics"])
check("log lines", [l["msg"] for l in t["log"]][-2:] == ["l1", "l2"], t["log"][-3:])
check("kill control absent w/o pid", not any(c["id"] == "kill" for c in t["controls"]), t["controls"])

# log endpoint, heartbeat
s, r = call("POST", "/api/tasks/t-api/log", {"msg": "warned", "level": "warn"})
check("log endpoint", s == 200)
s, r = call("POST", "/api/tasks/t-api/heartbeat")
check("heartbeat", s == 200)

# add/remove widgets & controls
call("POST", "/api/tasks/t-api/update", {"add_widgets": [{"id": "new", "type": "text"}], "remove_widgets": ["tx"],
                                         "add_controls": [{"id": "op", "kind": "open", "target": "https://example.com"}],
                                         "remove_controls": ["sh"]})
s, t = call("GET", "/api/tasks/t-api")
ids = [x["id"] for x in t["widgets"]]
check("add/remove widgets", "new" in ids and "tx" not in ids, ids)
cids = [x["id"] for x in t["controls"]]
check("add/remove controls", "op" in cids and "sh" not in cids, cids)
call("POST", "/api/tasks/t-api/update", {"add_controls": [{"id": "sh", "label": "Shell", "kind": "shell", "command": "echo hello {value} {task_id}"}]})

# signal command round trip with long-poll
got = {}
def poller():
    s2, r2 = call("GET", "/api/tasks/t-api/commands?wait=10", timeout=20)
    got["r"] = r2
th = threading.Thread(target=poller); th.start()
time.sleep(0.5)
s, r = call("POST", "/api/tasks/t-api/controls/sig", {"value": 42})
th.join(15)
cmds = got.get("r", {}).get("commands", [])
check("signal delivered via long-poll", s == 200 and cmds and cmds[0]["value"] == 42 and cmds[0]["control"] == "sig", got)
if cmds:
    s, r = call("POST", f"/api/tasks/t-api/commands/{cmds[0]['id']}/ack", {"ok": True, "message": "done!"})
    s, t = call("GET", "/api/tasks/t-api")
    hist = [c for c in t["commands"] if c["id"] == cmds[0]["id"]]
    check("ack recorded", hist and hist[0]["status"] == "ok" and hist[0]["reply"] == "done!", hist)

# shell control
s, r = call("POST", "/api/tasks/t-api/controls/sh", {"value": "world"})
check("shell started", s == 200 and r.get("started"), r)
time.sleep(2.5)
s, t = call("GET", "/api/tasks/t-api")
check("shell output in log", any("hello world t-api" in l["msg"] for l in t["log"]), [l["msg"] for l in t["log"][-4:]])
s, r = call("POST", "/api/tasks/t-api/controls/nope", {})
check("unknown control 404", s == 404, (s, r))

# long-poll of task list wakes on change
s, snap = call("GET", "/api/tasks")
v = snap["version"]
def bump():
    time.sleep(1)
    call("POST", "/api/tasks/t-api/update", {"status": "wake"})
threading.Thread(target=bump).start()
t0 = time.time()
s, snap2 = call("GET", f"/api/tasks?since={v}&wait=20", timeout=30)
dt = time.time() - t0
check("long-poll wakes on change", snap2["version"] > v and dt < 5, (dt, snap2["version"], v))
s, snap3 = call("GET", "/api/tasks?active=1&brief=1")
check("active+brief", all(len(x["log"]) <= 5 for x in snap3["tasks"]) and all(x["state"] in ("running", "paused", "stale") for x in snap3["tasks"]), "")

# pause state, delete active refused
call("POST", "/api/tasks/t-api/update", {"state": "paused"})
s, t = call("GET", "/api/tasks/t-api")
check("paused", t["state"] == "paused", t["state"])
s, r = call("DELETE", "/api/tasks/t-api")
check("delete active -> 409", s == 409, (s, r))

# finish
s, r = call("POST", "/api/tasks/t-api/finish", {"state": "done", "message": "ok", "result": {"x": 1}})
s, t = call("GET", "/api/tasks/t-api")
check("finish", t["state"] == "done" and t["progress"] == 1.0 and t["result"] == {"x": 1}, t["state"])
s, r = call("POST", "/api/tasks/t-api/finish", {"state": "weird"})
check("bad finish state -> 400", s == 400, (s, r))
s, r = call("POST", "/api/tasks/t-api/controls/sig", {"value": 1})
check("control not available when done -> 409", s == 409, (s, r))

# pid / kill / lost
import subprocess
p = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
s, r = call("POST", "/api/tasks", {"id": "t-kill", "name": "kill me", "pid": p.pid, "notify": {"finish": False}})
s, t = call("GET", "/api/tasks/t-kill")
check("auto kill control", any(c["id"] == "kill" for c in t["controls"]), t["controls"])
s, r = call("POST", "/api/tasks/t-kill/controls/kill")
time.sleep(0.5)
s, t = call("GET", "/api/tasks/t-kill")
check("kill -> cancelled + process dead", t["state"] == "cancelled" and p.poll() is not None, (t["state"], p.poll()))

p2 = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(1)"])
call("POST", "/api/tasks", {"id": "t-lost", "name": "will be lost", "pid": p2.pid, "notify": {"fail": False}})
p2.wait()
time.sleep(4.5)
s, t = call("GET", "/api/tasks/t-lost")
check("watchdog -> lost", t["state"] == "lost", t["state"])

# heartbeat -> stale
call("POST", "/api/tasks", {"id": "t-stale", "name": "stale", "heartbeat_timeout": 1, "notify": {"stale": False}})
time.sleep(4.5)
s, t = call("GET", "/api/tasks/t-stale")
check("watchdog -> stale", t["state"] == "stale", t["state"])
call("POST", "/api/tasks/t-stale/update", {"status": "back"})
s, t = call("GET", "/api/tasks/t-stale")
check("stale -> running on update", t["state"] == "running", t["state"])
call("POST", "/api/tasks/t-stale/finish", {"state": "cancelled"})

# re-register keeps log + chart
call("POST", "/api/tasks", {"id": "t-api", "name": "API test again", "widgets": [{"id": "c", "type": "chart", "series": ["a", "b"]}]})
s, t = call("GET", "/api/tasks/t-api")
check("re-register keeps chart + log", len(t["widgets"][0]["points"]) == 3 and any(l["msg"] == "l1" for l in t["log"]), len(t["widgets"][0]["points"]))
call("POST", "/api/tasks/t-api/finish", {"state": "failed", "message": "boom"})

# misc
s, r = call("POST", "/api/settings", {"muted": True})
check("mute", r.get("muted") is True, r)
s, r = call("POST", "/api/settings", {"muted": False})
check("unmute", r.get("muted") is False, r)
# project (panel groups) and attention ("needs you": top of the panel); attention is cleared when the task ends
call("POST", "/api/tasks", {"id": "t-prj", "name": "prj", "project": "shop-api", "attention": "approve deploy"})
s, snap = call("GET", "/api/tasks")
tp = next(t for t in snap["tasks"] if t["id"] == "t-prj")
check("project / attention on register", tp.get("project") == "shop-api" and tp.get("attention") == "approve deploy", tp)
call("POST", "/api/tasks/t-prj/update", {"attention": "", "project": "web"})
s, snap = call("GET", "/api/tasks")
tp = next(t for t in snap["tasks"] if t["id"] == "t-prj")
check("project / attention on update", tp.get("project") == "web" and tp.get("attention") == "", tp)
call("POST", "/api/tasks/t-prj/update", {"attention": "again"})
call("POST", "/api/tasks/t-prj/finish", {"state": "done"})
s, snap = call("GET", "/api/tasks")
tp = next(t for t in snap["tasks"] if t["id"] == "t-prj")
check("attention cleared on finish", tp.get("attention") == "", tp)
call("DELETE", "/api/tasks/t-prj")
# seen: per task, by the panel showing it (or all at once with seen: true)
for i in ("t-seen1", "t-seen2"):
    call("POST", "/api/tasks", {"id": i, "name": i})
    call("POST", f"/api/tasks/{i}/finish", {"state": "done"})
s, snap = call("GET", "/api/tasks")
st = {t["id"]: t.get("seen") for t in snap["tasks"] if t["id"].startswith("t-seen")}
check("seen: a finished task is unseen until shown", st == {"t-seen1": False, "t-seen2": False}, st)
call("POST", "/api/settings", {"seen": ["t-seen1"]})
s, snap = call("GET", "/api/tasks")
st = {t["id"]: t.get("seen") for t in snap["tasks"] if t["id"].startswith("t-seen")}
check("seen: only the given ids", st == {"t-seen1": True, "t-seen2": False}, st)
for i in ("t-seen1", "t-seen2"):
    call("DELETE", f"/api/tasks/{i}")
# speed / ETA from the recent updates, not since the start: a job resuming at 9000 of 10000
call("POST", "/api/tasks", {"id": "t-pace", "name": "pace", "total": 10000, "unit": "frames"})
call("POST", "/api/tasks/t-pace/update", {"current": 9000})
t0 = time.time()
time.sleep(3.2)
s, r = call("POST", "/api/tasks/t-pace/update", {"current": 9032})
dt = time.time() - t0
s, snap = call("GET", "/api/tasks")
tp = next(t for t in snap["tasks"] if t["id"] == "t-pace")
check("pace: rate from the recent updates (~10 frames/s, not 9032 / elapsed)", abs(tp.get("rate", 0) - 32 / dt) < 1.5, tp.get("rate"))
check("pace: ETA from the current rate", abs(tp.get("eta_at", 0) - time.time() - 968 / tp.get("rate", 1)) < 15, (tp.get("eta_at"), time.time()))
call("POST", "/api/tasks/t-pace/update", {"state": "paused"})
s, snap = call("GET", "/api/tasks")
tp = next(t for t in snap["tasks"] if t["id"] == "t-pace")
check("pace: nothing while paused", "rate" not in tp and "eta_at" not in tp, tp)
call("POST", "/api/tasks/t-pace/finish", {"state": "done"})
call("DELETE", "/api/tasks/t-pace")
# panel look: theme / accent / panel and card transparency / pin (config.json, shown in the snapshot)
s, snap = call("GET", "/api/tasks")
check("panel settings: defaults in the snapshot", snap.get("panel") == {"theme": "system", "accent": "system", "panel_transparency": 30,
      "card_transparency": 30, "panel_group": "project", "panel_pinned": False}, snap.get("panel"))
s, r = call("POST", "/api/settings", {"theme": "dark", "accent": "taskhub", "panel_transparency": 72.4, "card_transparency": "55", "panel_group": "source", "panel_pinned": True})
check("panel settings: set", s == 200 and r.get("panel") == {"theme": "dark", "accent": "taskhub", "panel_transparency": 72,
      "card_transparency": 55, "panel_group": "source", "panel_pinned": True}, r)
cfg_path = os.path.join(os.environ.get("TEMP", ""), "taskhub-tests", "config.json")
if os.path.exists(cfg_path):
    with open(cfg_path, encoding="utf-8") as f:
        cfg = json.load(f)
    check("panel settings: saved in config.json", (cfg.get("theme"), cfg.get("accent"), cfg.get("panel_transparency"), cfg.get("card_transparency"),
           cfg.get("panel_pinned")) == ("dark", "taskhub", 72, 55, True), cfg)
for bad in ({"theme": "blue"}, {"accent": "red"}, {"panel_transparency": 101}, {"panel_transparency": -1}, {"panel_transparency": "abc"}, {"card_transparency": 101}, {"card_transparency": "x"}, {"panel_group": "repo"}):
    s, r = call("POST", "/api/settings", bad)
    check(f"panel settings: {bad} -> 400", s == 400, (s, r))
s, snap = call("GET", "/api/tasks")
check("panel settings: invalid values change nothing", snap["panel"]["theme"] == "dark" and snap["panel"]["panel_transparency"] == 72
      and snap["panel"]["card_transparency"] == 55, snap["panel"])
s, r = call("POST", "/api/settings", {"theme": "system", "accent": "system", "panel_transparency": 30, "card_transparency": 30,
                                      "panel_group": "project", "panel_pinned": False})
check("panel settings: back to defaults", r.get("panel") == {"theme": "system", "accent": "system", "panel_transparency": 30,
      "card_transparency": 30, "panel_group": "project", "panel_pinned": False}, r)
s, r = call("GET", "/api/help", raw=True)
check("help served", s == 200 and b"TaskHub API" in r, s)
s, r = call("GET", "/ui", raw=True)
check("ui served", s == 200 and b"const NATIVE" in r and b"__TOKEN__" not in r, s)
s, r = call("GET", "/icon.png", raw=True)
check("icon served", s == 200 and r[:4] == b"\x89PNG", s)
s, r = call("POST", "/api/clear")
s, snap = call("GET", "/api/tasks")
check("clear finished", not any(t["state"] in ("done", "failed", "cancelled", "lost") for t in snap["tasks"]), [t["state"] for t in snap["tasks"]])
s, r = call("POST", "/api/tasks", {"name": "body", "id": "t-bom"}, headers={})
call("DELETE", "/api/tasks/t-bom?force=1")
print("\n%d failure(s)" % fails)
sys.exit(1 if fails else 0)
