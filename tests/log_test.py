"""The log: line format, start header, levels (no notification text or task name at the default level), a bad level, masking, rotation,
the CLI's own log. Own port and temporary folders: never the real hub or its log. Arguments: TaskHubHost.exe, the main test hub's data."""
import json, os, re, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.parse, urllib.request

EXE = sys.argv[1]
MAIN_DATA = sys.argv[2] if len(sys.argv) > 2 else None
CLI = os.path.join(os.path.dirname(EXE), "taskhub.exe")
PORT = 47887
URL = f"http://127.0.0.1:{PORT}"
data = tempfile.mkdtemp(prefix="taskhub-log-data-")
home = tempfile.mkdtemp(prefix="taskhub-log-home-")
LOG = os.path.join(data, "taskhub.log")
ENV = dict(os.environ, TASKHUB_TEST_HOME=home, TASKHUB_NO_AUTOSTART="1", TASKHUB_URL=URL, APPDATA=data)
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
LINE = re.compile(r"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} (DBG|INF|WRN|ERR) \S+\s+")
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(),
                               headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=10) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def read(path=LOG):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


def empty():
    for f in os.listdir(data):
        p = os.path.join(data, f)
        shutil.rmtree(p, ignore_errors=True) if os.path.isdir(p) else os.remove(p)


class Hub:
    """A hub on an empty log with this config."""

    def __init__(self, **cfg):
        self.cfg = cfg

    def __enter__(self):
        empty()
        with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
            json.dump({"toasts": False, "autostart": False, "toast_rate": 0, "update_check": False, **self.cfg}, f)
        self.p = subprocess.Popen([EXE, "--port", str(PORT), "--data", data], env=ENV)
        end = time.time() + 30
        while time.time() < end:
            try:
                if call("GET", "/api/health")[0] == 200:
                    return self
            except OSError:
                pass
            time.sleep(0.2)
        raise RuntimeError("hub did not start")

    def __exit__(self, *a):
        self.p.kill()
        self.p.wait(10)


NAME = "render-log-test-name"
TEXT = "notification-body-log-test"
NAMED_ID = "training-job-named-by-a-service"   # like a connector's task id, built from a job's name


def activity():
    code, r = call("POST", "/api/tasks", {"name": NAME})
    tid = r.get("id")
    call("POST", "/api/notify", {"title": "log test", "message": TEXT})
    call("POST", f"/api/tasks/{tid}/finish", {"message": TEXT + "-finish"})
    call("POST", "/api/tasks", {"name": NAME, "id": NAMED_ID})
    call("POST", f"/api/tasks/{NAMED_ID}/finish", {"state": "failed", "message": TEXT + "-finish"})
    time.sleep(1.5)
    return tid


try:
    with Hub():
        activity()
        log = read()
        lines = [l for l in log.splitlines() if l.strip()]
        check("log has lines", len(lines) > 3, log[:200])
        bad = [l for l in lines if not LINE.match(l) and not l.startswith((" ", "\t", "---"))]
        check("every line has the format", not bad, bad[:3])
        for field in ("TaskHub ", "edition=", "beta=", "windows=", "dotnet=", "language=", f"port={PORT}", "data=", "exe=", "args=", "monitor ", "webview2="):
            check("header has " + field.strip(), field in log, log[:600])
        check("default level has no DBG", " DBG " not in log, [l for l in lines if " DBG " in l][:3])
        check("default level has no notification text", TEXT not in log)
        check("default level has no task name", NAME not in log)
        check("default level has no id built from a name", NAMED_ID not in log, [l for l in lines if NAMED_ID in l][:2])
        check("a task is still followed: register and finish", " register " in log and " finish " in log)

    with Hub(log_level="debug"):
        activity()
        log = read()
        check("debug shows DBG lines", " DBG " in log)
        check("debug shows the notification text", TEXT in log, log[-400:])
        check("debug shows the task name", NAME in log, log[-400:])
        check("debug shows the id", NAMED_ID in log, log[-400:])
        # a line break in a path must not make a line of its own
        forged = "2026-01-01 00:00:00.000 ERR forged   text"
        call("POST", "/api/tasks%0A" + urllib.parse.quote(forged), {})
        time.sleep(.5)
        log = read()
        check("the forging request reached the log", "forged" in log, log[-300:])
        check("a line break can't forge a line", not any(l.startswith("2026-01-01") for l in log.splitlines()), log[-300:])

    with Hub(log_level="shouting"):
        activity()
        log = read()
        check("an unknown level keeps the default", " DBG " not in log and TEXT not in log)
        check("an unknown level changes nothing else", "edition=" in log)

    # masking and rotation: the same code path as every other line
    empty()
    with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
        json.dump({}, f)
    rc = subprocess.run([EXE, "--data", data, "--selftest-log", "6000"], env=ENV, timeout=120).returncode
    check("self test ran", rc == 0, rc)
    old, new = LOG + ".1", read()
    all_text = read(old) + new
    check("rotated", os.path.exists(old))
    check("main log stays under its limit", os.path.getsize(LOG) < 4_100_000, os.path.getsize(LOG))
    selftest = [l for l in all_text.splitlines() if " selftest " in l and "xxxxxxxx" not in l]
    check("self test wrote the secrets' lines", len(selftest) == 14, len(selftest))
    for secret in ("A" * 35, "B" * 35, "C" * 36, "D" * 60, "E" * 16, "F" * 40, "abcdefghijklmnop", "GGGGGGGG", "HHHHHHHH", "I" * 40, "J" * 12, "K" * 12, "ab" * 32, "L" * 25, "M" * 38):
        check("masked: " + secret[:4] + "...", secret not in all_text)

    # the CLI's log: hub down, a hook that can't reach it
    cli_data = os.path.join(home, "TaskHub")   # TASKHUB_TEST_HOME: the CLI never looks at the real data folder here
    os.makedirs(cli_data)
    cli_log = os.path.join(cli_data, "cli.log")
    payload = json.dumps({"hook_event_name": "Stop"})

    def hook(level):
        with open(os.path.join(cli_data, "config.json"), "w", encoding="utf-8") as f:
            json.dump({"log_level": level}, f)
        if os.path.exists(cli_log):
            os.remove(cli_log)
        return subprocess.run([CLI, "hook", "claude"], env=ENV, input=payload.encode(), capture_output=True, timeout=30)

    r = hook("info")
    check("hook stays silent and exits 0", r.returncode == 0 and not r.stdout and not r.stderr, r)
    check("no cli.log at the default level", not os.path.exists(cli_log))
    r = hook("debug")
    check("hook stays silent at debug too", r.returncode == 0 and not r.stdout and not r.stderr, r)
    check("cli.log says why at debug", "hook" in read(cli_log) and "not reachable" in read(cli_log), read(cli_log)[:300])
    check("cli.log has the line format", all(LINE.match(l) for l in read(cli_log).splitlines()), read(cli_log)[:300])

    # the whole test run's main log, at debug level, with the fake secrets the other tests used
    if MAIN_DATA:
        text = ""
        for name in os.listdir(MAIN_DATA):
            if name.startswith("taskhub.log"):
                text += read(os.path.join(MAIN_DATA, name))
        check("main test log is there", len(text) > 0, MAIN_DATA)
        for pat in (r"github_pat_[A-Za-z0-9_]{20,}", r"\bghp_[A-Za-z0-9]{20,}", r"\b\d{6,12}:[A-Za-z0-9_-]{30,}", r"\brpa_[A-Za-z0-9]{20,}", r"\bAKIA[A-Z0-9]{16}\b"):
            m = re.search(pat, text)
            check("no token in the main test log: " + pat[:18], not m, m.group(0)[:12] if m else "")
finally:
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(home, ignore_errors=True)

print("ALL LOG TESTS PASSED" if not fails else f"{fails} LOG TEST(S) FAILED", flush=True)
sys.exit(1 if fails else 0)
