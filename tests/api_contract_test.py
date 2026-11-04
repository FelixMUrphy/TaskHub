"""What API.md promises, checked from the outside: own hub on 47865, temp data. Written from a check by a reader who had only the docs.
A request the API can't honour gets a 400 that says why; it never answers 200 and does nothing."""
import importlib.util, json, os, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request

EXE = os.path.abspath(sys.argv[1])
CLI = os.path.join(os.path.dirname(EXE), "taskhub.exe")
PORT = 47865
URL = f"http://127.0.0.1:{PORT}"
HERE = os.path.dirname(os.path.abspath(__file__))
tmp = tempfile.mkdtemp(prefix="taskhub-contract-")
ENV = dict(os.environ, TASKHUB_TEST_HOME=tmp, TASKHUB_NO_AUTOSTART="1", TASKHUB_URL=URL)
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def raw(method, path, data=None):
    r = urllib.request.Request(URL + path, method=method, data=data, headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=30) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        body = e.read()
        try:
            return e.code, json.loads(body or b"{}")
        except ValueError:
            return e.code, {"raw": body[:200]}
    except urllib.error.URLError as e:
        return 0, {"error": str(e)}


def call(method, path, body=None):
    return raw(method, path, None if body is None else json.dumps(body).encode())


def task(tid):
    return call("GET", f"/api/tasks/{tid}")[1]


def until(cond, timeout=20):
    end = time.time() + timeout
    while time.time() < end:
        v = cond()
        if v:
            return v
        time.sleep(0.1)
    return None


def refused(name, method, path, body, says=None):
    s, r = call(method, path, body)
    check(name + " -> 400", s == 400, (s, r))
    if says:
        check(name + ": the answer says why", says in r.get("error", ""), r)


def new_task(tid, **kw):
    s, r = call("POST", "/api/tasks", dict({"id": tid, "name": tid, "notify": {"finish": False, "fail": False}}, **kw))
    assert s == 200, (s, r)
    return tid


def logged(tid, text):
    return any(text in str(l.get("msg")) for l in task(tid).get("log", []))


hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", os.path.join(tmp, "data"), "--no-tray"], env=ENV)
try:
    check("hub up", until(lambda: call("GET", "/api/health")[0] == 200), "no answer")

    # ---- registration
    refused("register without a name", "POST", "/api/tasks", {}, "name")
    refused("register with title for name", "POST", "/api/tasks", {"title": "x"}, "name")
    refused("register with an empty name", "POST", "/api/tasks", {"name": ""}, "name")
    refused("register, total is not a number", "POST", "/api/tasks", {"name": "x", "total": "abc"}, "total")
    refused("register, widgets is not a list", "POST", "/api/tasks", {"name": "x", "widgets": {"id": "a"}}, "widgets")
    refused("register, controls is not a list", "POST", "/api/tasks", {"name": "x", "controls": "go"}, "controls")
    refused("register, notify is not an object", "POST", "/api/tasks", {"name": "x", "notify": "yes"}, "notify")
    s, r = call("POST", "/api/tasks", {"name": "nan total", "total": "NaN", "id": "reg-nan"})
    check("register: a NaN total is ignored", s == 200 and task("reg-nan")["total"] is None, (s, r, task("reg-nan").get("total")))
    s, r = call("POST", "/api/tasks", {"name": "extra", "id": "reg-extra", "some_new_field": 1})
    check("register: a field not known yet beside the known ones is no error", s == 200, (s, r))

    # ---- update: mistakes are said, nothing changes
    new_task("u", widgets=[{"id": "g", "type": "gauge"}, {"id": "led", "type": "led"}, {"id": "st", "type": "stat"}, {"id": "p", "type": "progress"}, {"id": "txt", "type": "text"}])
    call("POST", "/api/tasks/u/update", {"progress": 0.5, "status": "before"})
    refused("update state done", "POST", "/api/tasks/u/update", {"state": "done"}, "finish")
    refused("update state banana", "POST", "/api/tasks/u/update", {"state": "banana"}, "finish")
    refused("update, a field nobody knows", "POST", "/api/tasks/u/update", {"percentage": 50}, "percentage")
    refused("update, progress is text", "POST", "/api/tasks/u/update", {"progress": "half"}, "progress")
    refused("update, percent is text", "POST", "/api/tasks/u/update", {"percent": "half"}, "percent")
    refused("update, current is text", "POST", "/api/tasks/u/update", {"current": "abc"}, "current")
    refused("update, total is text", "POST", "/api/tasks/u/update", {"total": "abc"}, "total")
    refused("update, percent is a bool", "POST", "/api/tasks/u/update", {"percent": True}, "percent")
    refused("update, metrics is not an object", "POST", "/api/tasks/u/update", {"metrics": "gpu"}, "metrics")
    refused("update, widgets is not an object", "POST", "/api/tasks/u/update", {"widgets": "gpu"}, "widgets")
    refused("update, add_widgets is not a list", "POST", "/api/tasks/u/update", {"add_widgets": {"id": "x"}}, "add_widgets")
    refused("update, a level nobody knows", "POST", "/api/tasks/u/update", {"log": "x", "level": "banana"}, "level")
    refused("update, a widget not declared", "POST", "/api/tasks/u/update", {"status": "lost?", "widgets": {"nope": 1}}, "nope")
    t = task("u")
    check("a refused update changed nothing", t["progress"] == 0.5 and t["status"] == "before" and t["state"] == "running", t)
    check("an empty update stays valid (all fields optional)", call("POST", "/api/tasks/u/update", {})[0] == 200)
    check("update: a field not known yet beside known ones is no error", call("POST", "/api/tasks/u/update", {"status": "ok", "some_new_field": 1})[0] == 200)
    check("update: a widget added in the same request takes a value", call("POST", "/api/tasks/u/update", {"add_widgets": [{"id": "new", "type": "text"}], "widgets": {"new": "v"}})[0] == 200)
    check("update: 'level' valid", call("POST", "/api/tasks/u/update", {"log": "w", "level": "warn"})[0] == 200)
    check("update: numeric strings are numbers", call("POST", "/api/tasks/u/update", {"total": "10", "current": "5"})[0] == 200 and task("u")["total"] == 10 and task("u")["current"] == 5 and task("u")["progress"] == 0.5, task("u"))
    call("POST", "/api/tasks/u/update", {"total": None, "current": None, "progress": 0.5})

    # ---- NaN / Infinity: ignored, the rest of the update stays (API.md)
    for bad in ("NaN", "Infinity", "-Infinity", "inf"):
        s, r = call("POST", "/api/tasks/u/update", {"progress": bad, "percent": bad, "current": bad, "total": bad, "status": "after " + bad})
        t = task("u")
        check(f"{bad}: accepted, the rest applied", s == 200 and t["status"] == "after " + bad, (s, r, t["status"]))
        check(f"{bad}: progress kept", t["progress"] == 0.5, t["progress"])
        check(f"{bad}: current and total not stored", t["current"] is None and t["total"] is None, (t["current"], t["total"]))
        call("POST", "/api/tasks/u/update", {"widgets": {"g": 7, "st": 8, "p": 0.25}})
        s, r = call("POST", "/api/tasks/u/update", {"widgets": {"g": bad, "st": bad, "p": {"value": bad}}})
        w = {x["id"]: x for x in task("u")["widgets"]}
        check(f"{bad}: numeric widgets keep their value", s == 200 and w["g"].get("value") == 7 and w["st"].get("value") == 8 and w["p"].get("value") == 0.25, (s, w["g"], w["st"], w["p"]))
    call("POST", "/api/tasks/u/update", {"widgets": {"txt": "NaN"}})
    check("a text widget may say NaN", {x["id"]: x for x in task("u")["widgets"]}["txt"].get("value") == "NaN")
    s, _ = raw("POST", "/api/tasks/u/update", b'{"progress": NaN, "status": "raw"}')
    check("a raw NaN is not JSON: 400", s == 400, s)

    # the Python client turns NaN into "not sent", so the rest of its update isn't lost
    spec = importlib.util.spec_from_file_location("taskhub_client", os.path.join(HERE, "..", "clients", "python", "taskhub_client.py"))
    client = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(client)
    def py_update(tid, body):
        try:
            client.request("POST", f"/api/tasks/{tid}/update", body, url=URL)
        except client.HubError as e:
            print("NOTE the client's request failed:", e, flush=True)

    py_update("u", {"status": "from python", "progress": float("nan"), "metrics": {"loss": float("inf"), "acc": 1}, "widgets": {"g": float("nan")}})
    t = task("u")
    check("Python client: NaN doesn't lose the update", t["status"] == "from python" and t["metrics"].get("acc") == 1 and "loss" not in t["metrics"] and t["progress"] == 0.5, t["status"])
    new_task("chart", widgets=[{"id": "c", "type": "chart", "series": ["a", "b"]}])
    py_update("chart", {"widgets": {"c": [1, float("nan")]}})
    pts = task("chart")["widgets"][0]["points"]
    check("Python client: NaN in a chart row keeps the row", len(pts) == 1 and pts[0][1] == 1 and pts[0][2] is None, pts)

    # ---- LED: the form the docs give
    call("POST", "/api/tasks/u/update", {"widgets": {"led": {"state": "warn", "text": "hot"}}})
    w = {x["id"]: x for x in task("u")["widgets"]}["led"]
    check("LED {state, text}", w.get("value") == "warn" and w.get("text") == "hot", w)
    call("POST", "/api/tasks/u/update", {"widgets": {"led": "ok"}})
    check("LED plain value", {x["id"]: x for x in task("u")["widgets"]}["led"].get("value") == "ok")

    # ---- log, notify
    new_task("l")
    refused("log with no text", "POST", "/api/tasks/l/log", {}, "msg")
    refused("log with an empty text", "POST", "/api/tasks/l/log", {"msg": ""}, "msg")
    refused("log, a level nobody knows", "POST", "/api/tasks/l/log", {"msg": "x", "level": "banana"}, "level")
    check("log msg", call("POST", "/api/tasks/l/log", {"msg": "one"})[0] == 200 and logged("l", "one"))
    check("log message", call("POST", "/api/tasks/l/log", {"message": "two"})[0] == 200 and logged("l", "two"), task("l")["log"])
    check("log list", call("POST", "/api/tasks/l/log", {"msg": ["three", "four"], "level": "warn"})[0] == 200 and logged("l", "four"))
    refused("notify with nothing", "POST", "/api/notify", {}, "title")
    refused("notify, a level nobody knows", "POST", "/api/notify", {"title": "x", "level": "banana"}, "level")
    check("notify with a title only", call("POST", "/api/notify", {"title": "x"})[0] == 200)
    check("notify with a message only", call("POST", "/api/notify", {"message": "x"})[0] == 200)

    # ---- finish: a state is needed, and the first real one stands
    new_task("f")
    refused("finish without a state", "POST", "/api/tasks/f/finish", {}, "state")
    refused("finish lost", "POST", "/api/tasks/f/finish", {"state": "lost"}, "state")
    refused("finish banana", "POST", "/api/tasks/f/finish", {"state": "banana"}, "state")
    check("a refused finish leaves it running", task("f")["state"] == "running")
    s, r = call("POST", "/api/tasks/f/finish", {"state": "failed", "message": "boom"})
    check("finish says the state", s == 200 and r.get("state") == "failed", (s, r))
    s, r = call("POST", "/api/tasks/f/finish", {"state": "done", "message": "second"})
    t = task("f")
    check("a second finish changes nothing", t["state"] == "failed" and t["message"] == "boom", (t["state"], t["message"]))
    check("...and says what stands", s == 200 and r.get("state") == "failed" and r.get("already_finished") is True, (s, r))
    check("...and no second line in the log", sum("finished:" in str(l.get("msg")) for l in t["log"]) == 1, t["log"])

    # ---- lost: a process that ended while its finish is on the way isn't lost; one that never reports is
    results = {}

    def late_finish(i):
        tid = f"race{i}"
        p = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(0.5)"])
        new_task(tid, pid=p.pid, kill_control=False)
        p.wait()
        time.sleep(2)   # the time `taskhub run` may take between the end of its process and its finish
        call("POST", f"/api/tasks/{tid}/finish", {"state": "done"})
        results[tid] = task(tid)

    threads = [threading.Thread(target=late_finish, args=(i,)) for i in range(12)]
    for i, th in enumerate(threads):
        th.start()
        time.sleep(0.3)   # spread over the watchdog's 3 s, so its tick falls inside some of the windows
    for th in threads:
        th.join()
    falsely = [k for k, v in results.items() if any("finished: lost" in str(l.get("msg")) for l in v["log"])]
    check("a finish 2 s after the process ended: never called lost", not falsely, falsely)
    check("...and the tasks are done", all(v["state"] == "done" for v in results.values()), {k: v["state"] for k, v in results.items()})
    p = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(0.5)"])
    new_task("never", pid=p.pid, kill_control=False)
    p.wait()
    check("a process that never reports: lost", until(lambda: task("never")["state"] == "lost", 12), task("never")["state"])
    call("POST", "/api/tasks/never/finish", {"state": "done", "message": "late after all"})
    t = task("never")
    check("a real finish after lost still counts", t["state"] == "done" and t["message"] == "late after all", (t["state"], t["message"]))

    # ---- taskhub run: Kill stays cancelled; many short runs are never lost
    p = subprocess.Popen([CLI, "run", "--name", "kill me", "--id", "rk", "--quiet", "--", sys.executable, "-c", "import time; time.sleep(60)"], env=ENV, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    check("run registered", until(lambda: task("rk").get("pid")), "no task")
    s, r = call("POST", "/api/tasks/rk/controls/kill")
    check("Kill accepted", s == 200, (s, r))
    p.wait(timeout=20)
    t = task("rk")
    check("Kill on a run task stays cancelled (API.md)", t["state"] == "cancelled" and "killed" in t["message"], (t["state"], t["message"]))

    # ---- taskhub run: the regex of API.md, a wrong regex, ::widget with an id not declared
    r = subprocess.run([CLI, "run", "--name", "py regex", "--id", "rr", "--quiet", "--progress-regex", r"\[(?P<cur>\d+)/(?P<tot>\d+)\]", "--", sys.executable, "-c", "print('[3/4]', flush=True)"],
                       env=ENV, capture_output=True, text=True, timeout=60)
    t = task("rr")
    check("run: a Python-style regex (the docs' example) works", r.returncode == 0 and t.get("current") == 3 and t.get("total") == 4, (r.returncode, r.stderr[-300:], t.get("current")))
    r = subprocess.run([CLI, "run", "--name", "bad regex", "--progress-regex", "(", "--", sys.executable, "-c", "print(1)"], env=ENV, capture_output=True, text=True, timeout=60)
    check("run: a wrong regex: a clean error, nothing run", r.returncode == 2 and "--progress-regex" in r.stderr and "Unhandled" not in r.stderr and "   at " not in r.stderr, (r.returncode, r.stderr[-300:]))
    r = subprocess.run([CLI, "run", "--name", "bad regex", "--status-regex", "[", "--", sys.executable, "-c", "print(1)"], env=ENV, capture_output=True, text=True, timeout=60)
    check("run: a wrong status regex too", r.returncode == 2 and "--status-regex" in r.stderr, (r.returncode, r.stderr[-300:]))
    r = subprocess.run([CLI, "run", "--name", "widgets", "--id", "rw", "--quiet", "--", sys.executable, "-c", "print('::widget gpu=71\\n::status after\\nplain line', flush=True)"],
                       env=ENV, capture_output=True, text=True, timeout=60)
    t = task("rw")
    w = {x["id"]: x for x in t["widgets"]}
    check("run: ::widget with a new id shows, the rest isn't lost", "gpu" in w and w["gpu"].get("value") == 71 and t["status"] == "after" and logged("rw", "plain line"), (w.keys(), t["status"], t["log"]))
    check("run: its Ctrl+C question is in English", all("Надіслати" not in str(c.get("confirm")) for c in t["controls"]), t["controls"])

    # ---- the Python client's own run
    r = subprocess.run([sys.executable, os.path.join(HERE, "..", "clients", "python", "taskhub_client.py"), "--url", URL, "run", "--name", "py", "--quiet", "--progress-regex", "(", "--", sys.executable, "-c", "print(1)"],
                       env=ENV, capture_output=True, text=True, timeout=60)
    check("Python run: a wrong regex: a clean error", r.returncode != 0 and "Traceback" not in r.stderr and "progress-regex" in r.stderr, (r.returncode, r.stderr[-300:]))
    r = subprocess.run([sys.executable, os.path.join(HERE, "..", "clients", "python", "taskhub_client.py"), "--url", URL, "run", "--name", "py net", "--quiet", "--progress-regex", r"(?<pct>\d+)%", "--", sys.executable, "-c", "print('40%', flush=True)"],
                       env=ENV, capture_output=True, text=True, timeout=60)
    check("Python run: a .NET-style regex works", r.returncode == 0, (r.returncode, r.stderr[-300:]))
    confirm = [c for t in call("GET", "/api/tasks")[1]["tasks"] if t["name"] == "py net" for c in t["controls"] if c["id"] == "interrupt"]
    check("Python run: its Ctrl+C question is in English", confirm and "Надіслати" not in str(confirm[0].get("confirm")), confirm)

    # ---- every route to an operation has the same protection: /log can only log
    new_task("lg", source="github", widgets=[{"id": "w", "type": "text"}])
    call("POST", "/api/tasks/lg/update", {"status": "kept"})
    s, r = call("POST", "/api/tasks/lg/log", {"msg": "only this", "status": "changed", "progress": 0.9, "metrics": {"a": 1}, "widgets": {"w": "changed"},
                                              "add_controls": [{"id": "sneak", "label": "x", "kind": "signal"}],
                                              "meta": {"github": {"repo": "victim/other", "run_id": 999}, "github_pr": {"repo": "victim/other", "number": 1}}})
    t = task("lg")
    check("/log: the text is logged", s == 200 and logged("lg", "only this"), (s, r))
    check("/log can't set meta.github or meta.github_pr", "github" not in t["meta"] and "github_pr" not in t["meta"], t["meta"])
    check("/log changes nothing but the log", t["status"] == "kept" and t["progress"] is None and not t["metrics"] and t["widgets"][0].get("value") is None
          and not any(c["id"] == "sneak" for c in t["controls"]), t)

    # ---- bad shapes are a 400, and the answer stays short
    s, r = call("POST", "/api/tasks/u/update", {"add_widgets": [1], "widgets": {"a": 1}})
    check("add_widgets with a number beside values -> 400", s == 400, (s, r))
    s, r = raw("POST", "/api/tasks/u/update", b'{"status": "a", "status": "b"}')
    check("a key sent twice -> 400", s == 400, (s, r))
    huge = "x" * 200000
    for name, body in (("control id", {"name": "x", "controls": [{"id": huge, "label": "x"}]}), ("control kind", {"name": "x", "controls": [{"id": "c", "label": "x", "kind": huge}]}),
                       ("widget id", {"name": "x", "widgets": [{"id": huge, "type": "text"}]}), ("widget type", {"name": "x", "widgets": [{"id": "w", "type": huge}]})):
        s, r = call("POST", "/api/tasks", body)
        check(f"a huge {name}: 400 with a short answer", s == 400 and len(r.get("error", "")) < 1000, (s, len(r.get("error", ""))))

    # ---- widgets don't grow without end
    new_task("many")
    s, r = call("POST", "/api/tasks/many/update", {"add_widgets": [{"id": f"w{i}", "type": "text"} for i in range(50)]})
    check("more than 40 widgets through update -> 400", s == 400 and "40" in r.get("error", "") and not task("many")["widgets"], (s, r))
    s, r = call("POST", "/api/tasks/many/update", {"add_widgets": [{"id": f"w{i}", "type": "text"} for i in range(40)]})
    check("40 widgets are fine", s == 200 and len(task("many")["widgets"]) == 40, (s, r))
    check("replacing one of them isn't one more", call("POST", "/api/tasks/many/update", {"add_widgets": [{"id": "w0", "type": "stat"}]})[0] == 200)
    check("the 41st isn't", call("POST", "/api/tasks/many/update", {"add_widgets": [{"id": "w40", "type": "text"}]})[0] == 400)
    prog = "print('\\n'.join(f'::widget w{i}={i}' for i in range(60)) + '\\n::status after\\nlast plain', flush=True)"
    r = subprocess.run([CLI, "run", "--name", "many", "--id", "rm", "--quiet", "--", sys.executable, "-c", prog], env=ENV, capture_output=True, text=True, timeout=60)
    t = task("rm")
    check("run: ::widget stops at 40 widgets, the rest isn't lost", len(t["widgets"]) == 40 and t["status"] == "after" and logged("rm", "last plain"), (len(t["widgets"]), t["status"]))

    # ---- taskhub run: a ::widget the hub would refuse is an ordinary line, and costs nothing else
    prog = "print('::widget a b=1\\n::status after bad\\n::widget good=2\\nplain', flush=True)"
    r = subprocess.run([CLI, "run", "--name", "bad widget", "--id", "rb", "--quiet", "--", sys.executable, "-c", prog], env=ENV, capture_output=True, text=True, timeout=60)
    t = task("rb")
    w = {x["id"]: x for x in t["widgets"]}
    check("run: a ::widget with a bad id loses nothing", t["status"] == "after bad" and logged("rb", "plain") and logged("rb", "::widget a b=1") and w.get("good", {}).get("value") == 2, (w.keys(), t["status"], t["log"]))

    # ---- taskhub run: a regex that never ends can't hold up a command
    try:
        r = subprocess.run([CLI, "run", "--name", "slow regex", "--id", "rs", "--quiet", "--status-regex", "(a+)+$", "--", sys.executable, "-c", "print('a' * 60 + '!', flush=True)"],
                           env=ENV, capture_output=True, text=True, timeout=40)
        check("run: a catastrophic regex doesn't hang it", r.returncode == 0 and task("rs")["state"] == "done", (r.returncode, r.stderr[-200:]))
    except subprocess.TimeoutExpired:
        check("run: a catastrophic regex doesn't hang it", False, "still running after 40 s")

    # ---- the smaller answers
    new_task("sig", controls=[{"id": "go", "label": "Go", "kind": "signal"}, {"id": "kill2", "label": "Kill", "kind": "kill"}])
    s, r = call("POST", "/api/tasks/sig/commands/nope/ack", {"ok": True})
    check("ack of a command that doesn't exist -> 404", s == 404, (s, r))
    s, r = call("POST", "/api/tasks/sig/controls/kill2")
    check("kill with no process -> 409, as documented", s == 409, (s, r))
    s, r = call("GET", "/api/connectors/nope")
    check("a connector that doesn't exist -> 404", s == 404, (s, r))
finally:
    hub.kill()
    hub.wait()
    time.sleep(0.5)
    shutil.rmtree(tmp, ignore_errors=True)

print("api contract tests:", "ALL PASSED" if not fails else f"{fails} FAILED", flush=True)
sys.exit(1 if fails else 0)
