"""Codex's plan limits with a stand-in `codex app-server` that speaks the protocol measured on Codex 0.166.0: off starts nothing, on reads and keeps only
numbers, only while Codex runs, windows by length, warnings, a broken or silent Codex, what the panel is told, finding the program. Own port and temporary folders: never the real
home, Codex or hub. Argument: TaskHubHost.exe."""
import json, os, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.request

EXE = sys.argv[1]
PORT = 47885
URL = f"http://127.0.0.1:{PORT}"
data = tempfile.mkdtemp(prefix="taskhub-codexlim-data-")
home = tempfile.mkdtemp(prefix="taskhub-codexlim-home-")
rig = tempfile.mkdtemp(prefix="taskhub-codexlim-rig-")
FAKE, STARTS, MODE, FLAG = (os.path.join(rig, x) for x in ("fake.py", "starts.txt", "mode.txt", "running.txt"))
SOON = int(time.time()) + 3600
LATER = int(time.time()) + 86400
SECRET_ID = "RateLimitResetCredit_not_to_be_kept"
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
with open(FAKE, "w", encoding="utf-8") as f:
    f.write(r'''
import json, sys, time
open(sys.argv[0].replace("fake.py", "starts.txt"), "a").write("x")
mode = open(sys.argv[0].replace("fake.py", "mode.txt")).read().split()
say = lambda o: print(json.dumps(o), flush=True)
say({"method": "remoteControl/status/changed", "params": {}})
for line in sys.stdin:
    m = json.loads(line)
    if m.get("method") == "initialize":
        say({"id": 77, "method": "server/askedSomething", "params": {}})
        say({"id": m["id"], "result": {"userAgent": "fake"}})
    elif m.get("method") == "account/rateLimits/read":
        if mode[0] == "hang":
            time.sleep(120)
        if mode[0] == "error":
            say({"id": m["id"], "error": {"code": -32000, "message": "account someone@example.com is not logged in"}})
        elif mode[0] == "login":
            say({"id": m["id"], "error": {"code": -32600, "message": "codex account authentication required to read rate limits"}})
        elif mode[0] == "exit":
            sys.exit(0)
        elif mode[0] == "garbage":
            print("not json", flush=True)
            say({"id": m["id"], "result": {"rateLimits": {"primary": {"usedPercent": 5, "windowDurationMins": 77, "resetsAt": 1}}}})
        else:
            say({"id": m["id"], "result": json.loads(mode[1])})
''')


def limits(*windows):
    return {"rateLimits": {"planType": "plus", **dict(zip(("primary", "secondary"), windows))}, "rateLimitResetCredits": {"credits": [{"id": SECRET_ID}]}}


def w(pct, mins, resets):
    return {"usedPercent": pct, "windowDurationMins": mins, "resetsAt": resets}


def mode(name, result=None):
    with open(MODE, "w") as f:
        f.write(name + " " + json.dumps(result or {}, separators=(",", ":")))


def starts():
    return os.path.getsize(STARTS) if os.path.exists(STARTS) else 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(), headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=10) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def view():
    return call("GET", "/api/limits")[1]


def until(f, secs=12):
    end = time.time() + secs
    while time.time() < end:
        if f():
            return True
        time.sleep(.2)
    return False


def log():
    try:
        with open(os.path.join(data, "taskhub.log"), encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


class Hub:
    def __init__(self, on, every=3, **extra):
        self.on, self.every, self.extra = on, every, extra

    def __enter__(self):
        with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
            json.dump({"log_level": "debug", "toasts": False, "autostart": False, "update_check": False, "toast_rate": 0, "toast_task_gap": 0, "language": "en", "limits_codex": self.on}, f)
        env = dict(os.environ, TASKHUB_TEST_HOME=home, TASKHUB_TEST_CODEX=f"{sys.executable}|{FAKE}", TASKHUB_TEST_CODEX_RUNNING=FLAG, TASKHUB_TEST_CODEX_EVERY=str(self.every), TASKHUB_TEST_CODEX_PROBE="1")
        env.update(self.extra)
        self.p = subprocess.Popen([EXE, "--port", str(PORT), "--data", data], env=env)
        assert until(lambda: self.up(), 30)
        return self

    def up(self):
        try:
            return call("GET", "/api/health")[0] == 200
        except OSError:
            return False

    def __exit__(self, *a):
        self.p.kill()
        self.p.wait(10)


def integrations(*a):
    return subprocess.run([EXE, "--port", str(PORT), "--data", data, "--integrations", *a], env=dict(os.environ, TASKHUB_TEST_HOME=home), capture_output=True, timeout=30).returncode


def touch(path):
    open(path, "w").close()


try:
    touch(FLAG)
    mode("ok", limits(w(12, 300, SOON), w(44, 10080, LATER)))

    with Hub(False):
        time.sleep(7)
        check("off: Codex isn't started", starts() == 0, starts())
        check("off: nothing shown", "codex" not in view(), view())

    with Hub(True):
        check("on: read", until(lambda: "codex" in view()), view())
        v = view()["codex"]
        check("5 hours", v["five_hour"]["used_percentage"] == 12 and v["five_hour"]["resets_at"] == SOON, v)
        check("week", v["seven_day"]["used_percentage"] == 44 and v["seven_day"]["resets_at"] == LATER, v)
        check("in the panel's data", call("GET", "/api/tasks")[1]["limits"]["codex"]["seven_day"]["used_percentage"] == 44)
        saved = open(os.path.join(data, "plan_limits.json"), encoding="utf-8").read()
        check("only numbers kept", SECRET_ID not in saved and "plus" not in saved, saved)
        check("nothing of it in the log", SECRET_ID not in log())
        check("no way to report it over HTTP", call("POST", "/api/limits/codex", {"five_hour": {"used_percentage": 99}})[0] == 404)

        # Codex closed: no more starts; open again: reads go on
        os.remove(FLAG)
        time.sleep(4)
        n = starts()
        time.sleep(7)
        check("Codex not running: not started", starts() == n, (n, starts()))
        touch(FLAG)
        check("Codex running again: read", until(lambda: starts() > n), starts())

        # a plan with no 5-hour window has the week first
        os.remove(os.path.join(data, "plan_limits.json"))
    os.path.exists(os.path.join(data, "plan_limits.json")) and os.remove(os.path.join(data, "plan_limits.json"))
    mode("ok", limits(w(30, 10080, LATER)))
    with Hub(True):
        check("week first: read", until(lambda: "codex" in view()), view())
        v = view()["codex"]
        check("week first: by length, not place", "five_hour" not in v and v["seven_day"]["used_percentage"] == 30, v)

        # warnings, once per window, from the same code as Claude's
        mode("ok", limits(w(80, 300, SOON), w(30, 10080, LATER)))
        check("75%: warned", until(lambda: "Codex: plan limit" in log()), log()[-400:])
        mode("ok", limits(w(95, 300, SOON), w(30, 10080, LATER)))
        check("90%: warned again", until(lambda: log().count("Codex: plan limit") == 2), log()[-400:])
        n = starts()
        check("reads go on", until(lambda: starts() > n + 1))
        check("no third warning", log().count("Codex: plan limit") == 2, log()[-400:])
        v = view()["codex"]["five_hour"]["used_percentage"]

        # Claude's own reading is ignored here (its switch is off) and doesn't disturb Codex's
        check("Claude's switch is its own", call("POST", "/api/limits/claude", {"five_hour": {"used_percentage": 50}})[1].get("ignored") is True and "codex" in view())

        # whatever goes wrong: the last numbers stay, the hub goes on, no account text in the log
        for bad, said in (("error", "codex: refused, code -32000"), ("exit", "codex: ended without an answer"), ("garbage", "codex: no known window")):
            mode(bad)
            check(f"{bad}: said in the log", until(lambda: said in log()), log()[-400:])
            check(f"{bad}: last numbers stay", view()["codex"]["five_hour"]["used_percentage"] == v, view())
        check("the error's text isn't logged", "someone@example.com" not in log())

    # a Codex that never answers is ended
    mode("hang")
    with Hub(True, TASKHUB_TEST_CODEX_DEADLINE="2"):
        check("silent: given up", until(lambda: "codex: no answer in time" in log(), 15), log()[-400:])
        check("silent: hub answers", call("GET", "/api/health")[0] == 200)
        n = starts()
        check("silent: still tries later", until(lambda: starts() > n, 15), starts())

    # off forgets, and leaves Claude's alone: restart with the switch off
    # Codex opened after the hub: seen soon, the 10 minutes start from a real read
    os.remove(FLAG)
    os.path.exists(os.path.join(data, "plan_limits.json")) and os.remove(os.path.join(data, "plan_limits.json"))
    mode("ok", limits(w(10, 300, SOON)))
    with Hub(True, every=600):
        time.sleep(6)
        check("Codex closed: nothing yet", "codex" not in view() and starts() >= 0, view())
        touch(FLAG)
        check("Codex opened after the hub: read soon", until(lambda: "codex" in view(), 10), view())
        n = starts()
        time.sleep(5)
        check("...then not again for the full wait", starts() == n, (n, starts()))

    # one source: a reading replaces the last, unlike Claude's several sessions
    os.remove(os.path.join(data, "plan_limits.json"))
    mode("ok", limits(w(80, 300, SOON), w(44, 10080, LATER)))
    with Hub(True, every=1):
        shown = lambda: view().get("codex", {}).get("five_hour", {}).get("used_percentage")
        check("one source: read", until(lambda: shown() == 80), view())
        mode("ok", limits(w(30, 300, SOON), w(44, 10080, LATER)))
        check("one source: lower in the same window shown (reset credits)", until(lambda: shown() == 30), view())
        mode("ok", limits(w(95, 300, SOON), w(44, 10080, LATER)))
        check("one source: warned above again", until(lambda: "Codex: plan limit" in log()), log()[-400:])
        mode("ok", limits(w(5, 300, SOON - 1800), w(44, 10080, LATER)))
        check("one source: an earlier reset taken", until(lambda: shown() == 5 and view()["codex"]["five_hour"]["resets_at"] == SOON - 1800), view())
        mode("ok", limits(w(44, 10080, LATER)))
        check("one source: a window the plan no longer has goes", until(lambda: "five_hour" not in view()["codex"]), view())

        # from a script while the hub runs: refused, since the hub would go on and write its value back
        before = open(os.path.join(data, "config.json"), encoding="utf-8").read()
        check("hub running: --integrations refused", integrations("codex-limits=off") == 1)
        check("hub running: config as it was", open(os.path.join(data, "config.json"), encoding="utf-8").read() == before)
        check("hub running: still reading", "codex" in view())

    # with no numbers the panel is told why, and a good read clears it
    os.remove(os.path.join(data, "plan_limits.json"))
    problem = lambda: call("GET", "/api/tasks")[1].get("limits_problem")
    mode("login")
    with Hub(True, every=1):
        check("not signed in: told", until(lambda: problem() == {"codex": "login"}), problem())
        mode("error")
        check("refused otherwise: told", until(lambda: problem() == {"codex": "failed"}), problem())
        mode("exit")
        check("ended early: told", until(lambda: problem() == {"codex": "failed"}), problem())
        mode("ok", limits(w(10, 300, SOON)))
        check("a good read clears it", until(lambda: problem() == {} and "codex" in view()), (problem(), view()))
    empty = os.path.join(rig, "nothing")
    os.makedirs(empty)
    with Hub(True, every=1, TASKHUB_TEST_CODEX="", PATH=empty):
        check("no program: told", until(lambda: problem() == {"codex": "not_found"}), problem())
    with Hub(False):
        check("switch off: not told", problem() == {}, problem())

    with Hub(False):
        check("off: forgotten", until(lambda: "codex" not in view(), 5), view())
        check("off: not in the file", "codex" not in open(os.path.join(data, "plan_limits.json"), encoding="utf-8").read())

    # by hand or from a script: each switch forgets only its own agent's readings, an empty file goes
    kept = os.path.join(data, "plan_limits.json")

    def config():
        return json.load(open(os.path.join(data, "config.json"), encoding="utf-8"))

    both = {"claude": {"five_hour": {"used_percentage": 1}}, "codex": {"five_hour": {"used_percentage": 2}}}
    check("from a script: on", integrations("codex-limits=on") == 0 and config()["limits_codex"] is True, config())
    json.dump(both, open(kept, "w"))
    check("Claude off: exit 0", integrations("claude-limits=off") == 0)
    check("Claude off: Codex's readings stay", list(json.load(open(kept))) == ["codex"], open(kept).read())
    check("Claude off: Codex's switch stays", config()["limits_codex"] is True)
    json.dump(both, open(kept, "w"))
    check("Codex off: exit 0", integrations("codex-limits=off") == 0 and config()["limits_codex"] is False, config())
    check("Codex off: Claude's readings stay", list(json.load(open(kept))) == ["claude"], open(kept).read())
    check("Codex off again: unchanged, no error", integrations("codex-limits=off") == 0)
    integrations("claude-limits=off")
    check("nothing left: no file", not os.path.exists(kept))

    # finding the program
    out = os.path.join(rig, "found.txt")

    def locate(*dirs, cwd=None):
        env = dict(os.environ, TASKHUB_TEST_HOME=home, PATH=";".join(dirs))
        subprocess.run([EXE, "--data", data, "--selftest-codex-locate", out], env=env, timeout=30, cwd=cwd)
        return open(out, encoding="utf-8").read()

    npm = os.path.join(rig, "npm")
    native = os.path.join(npm, "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x", "codex")
    os.makedirs(native)
    touch(os.path.join(native, "codex.exe"))
    alone = os.path.join(rig, "alone")
    os.makedirs(alone)
    touch(os.path.join(alone, "codex.exe"))
    check("not on PATH: none", locate(rig) == "", locate(rig))
    check("npm folder with no shim: none", locate(npm) == "", locate(npm))
    touch(os.path.join(npm, "codex.cmd"))
    check("npm: the program inside the package, not the shim", locate(npm) == os.path.join(native, "codex.exe"), locate(npm))
    check("a codex.exe on PATH", locate(alone, npm) == os.path.join(alone, "codex.exe"), locate(alone, npm))
    check("a relative PATH entry doesn't count", locate("alone", cwd=rig) == "", locate("alone", cwd=rig))
    check("a bad PATH entry doesn't stop it", locate("C:\\no\\such\\folder", '"', alone) == os.path.join(alone, "codex.exe"))
finally:
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(home, ignore_errors=True)
    shutil.rmtree(rig, ignore_errors=True)

print("codex limits tests: " + ("OK" if not fails else f"{fails} FAILED"), flush=True)
sys.exit(1 if fails else 0)
