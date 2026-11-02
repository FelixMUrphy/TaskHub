"""Plan limits (Claude Code): the status line entry in a temporary home folder (the user's own wrapped and restored exactly), `taskhub statusline`
(the user's output and exit code unchanged, only numbers reach the hub, a hub that is down or a command that hangs costs no more than a blank reading) and
the hub's side on its own port with temporary data (readings kept, warnings once per limit window, a new window by resets_at or a falling percentage,
off keeps nothing). Never the real home, the real configs or the real hub. Argument: TaskHubHost.exe."""
import hashlib, http.server, json, os, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request

EXE = sys.argv[1]
CLI = os.path.join(os.path.dirname(EXE), "taskhub.exe")
PORT = 47894
URL = f"http://127.0.0.1:{PORT}"
home = tempfile.mkdtemp(prefix="taskhub-limits-home-")
data = tempfile.mkdtemp(prefix="taskhub-limits-data-")
appdata = tempfile.mkdtemp(prefix="taskhub-limits-appdata-")
settings = os.path.join(home, ".claude", "settings.json")
side = os.path.join(home, ".claude", "statusline.taskhub.json")
store = os.path.join(data, "plan_limits.json")
ENV = dict(os.environ, TASKHUB_TEST_HOME=home, APPDATA=appdata, TASKHUB_TOKEN="test", TASKHUB_NO_AUTOSTART="1", TASKHUB_URL=URL)
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def load(p):
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def write(p, o):
    with open(p, "w", encoding="utf-8") as f:
        json.dump(o, f)


def integrations(*a):
    return subprocess.run([EXE, "--data", data, "--integrations", *a], env=ENV, capture_output=True, timeout=30).returncode


def statusline(stdin, env=None, argv=None, timeout=40):
    t0 = time.time()
    p = subprocess.run(argv or [CLI, "statusline"], input=stdin, env=env or ENV, capture_output=True, timeout=timeout)
    return p.stdout, p.returncode, time.time() - t0


def fwd(p):
    return p.replace("\\", "/")


# ---------------------------------------------------------------- the entry in ~/.claude/settings.json
check("no ~/.claude at all: on works", integrations("claude-limits=on") == 0 and "statusline" in load(settings)["statusLine"]["command"])
check("...and off", integrations("claude-limits=off") == 0 and "statusLine" not in load(settings), load(settings))
os.remove(settings)
os.remove(bak_ := settings + ".taskhub.bak") if os.path.exists(settings + ".taskhub.bak") else None
shutil.rmtree(os.path.dirname(settings))
check("no ~/.claude at all: off is no error", integrations("claude-limits=off") == 0)
os.makedirs(os.path.dirname(settings))
for cmd in ("node D:/taskhub-x/p.js statusline", "mine; taskhub statusline", "taskhub statusline --mine", "echo taskhub && C:/x/taskhub.exe statusline"):
    write(settings, {"statusLine": {"type": "command", "command": cmd}})
    integrations("claude-limits=off")
    check(f"only mentions our words, stays theirs at off: {cmd}", load(settings).get("statusLine", {}).get("command") == cmd, load(settings))
    integrations("claude-limits=on")
    check("...and is wrapped at on", load(side)["statusLine"]["command"] == cmd, os.path.exists(side))
    integrations("claude-limits=off")
    check("...and comes back", load(settings)["statusLine"]["command"] == cmd, load(settings))
for cmd in ("taskhub statusline", "taskhub.exe statusline", "C:/x/TaskHub.exe statusline", 'C:\\x\\taskhub.exe statusline', '"C:/x y/taskhub.exe" statusline'):
    write(settings, {"statusLine": {"type": "command", "command": cmd}})
    integrations("claude-limits=off")
    check(f"ours is recognised: {cmd}", "statusLine" not in load(settings), load(settings))
os.remove(settings)
os.remove(settings + ".taskhub.bak")
own = {"type": "command", "command": "echo mine", "padding": 3, "refreshInterval": 30}
original = {"model": "opus", "permissions": {"allow": ["Read"]}, "statusLine": own}
write(settings, original)
check("turn on: exit 0", integrations("claude-limits=on") == 0)
s = load(settings)
sl = s["statusLine"]
check("our command in place, Claude Code's type", sl["type"] == "command" and sl["command"].endswith(" statusline") and "taskhub" in sl["command"].lower(), sl)
check("no backslashes: Git Bash would eat them", "\\" not in sl["command"], sl)
check("their padding and refresh interval stay", sl.get("padding") == 3 and sl.get("refreshInterval") == 30, sl)
check("other settings kept", s["model"] == "opus" and s["permissions"] == original["permissions"], s)
check("their entry kept outside settings.json", load(side)["statusLine"] == own, load(side))
m = os.path.getmtime(settings)
time.sleep(.05)
check("on again: exit 0", integrations("claude-limits=on") == 0)
check("on again: nothing rewritten", os.path.getmtime(settings) == m)
check("on again: their entry still the one kept", load(side)["statusLine"] == own, load(side))
check("off: exit 0", integrations("claude-limits=off") == 0)
check("off: settings back exactly", load(settings) == original, load(settings))
check("off: side file gone", not os.path.exists(side))

del original["statusLine"]
write(settings, original)
integrations("claude-limits=on")
check("no status line of theirs: ours added, nothing kept", "statusLine" in load(settings) and not os.path.exists(side), load(settings))
integrations("claude-limits=off")
check("no status line of theirs: off removes the key", load(settings) == original, load(settings))

write(settings, dict(original, statusLine=own))
integrations("claude-limits=on")
changed = load(settings)
changed["statusLine"] = {"type": "command", "command": "echo changed later"}
write(settings, changed)
integrations("claude-limits=off")
check("they changed it meanwhile: off leaves theirs", load(settings)["statusLine"]["command"] == "echo changed later", load(settings))
check("they changed it meanwhile: side file gone", not os.path.exists(side))
integrations("claude-limits=on")
check("wrapping what is there now", load(side)["statusLine"]["command"] == "echo changed later", load(side))
integrations("claude-limits=off")

write(settings, dict(original, statusLine="not an object"))
check("a statusLine that isn't an object: an error, nothing touched", integrations("claude-limits=on") == 1 and load(settings)["statusLine"] == "not an object", load(settings))
write(settings, original)
bak = settings + ".taskhub.bak"
check("backup from before TaskHub changed the file", os.path.exists(bak))


# ---------------------------------------------------------------- `taskhub statusline`
class Capture(http.server.BaseHTTPRequestHandler):
    got = []

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        Capture.got.append((self.path, json.loads(body)))
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b"{}")

    def log_message(self, *a):
        pass


cap = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Capture)
threading.Thread(target=cap.serve_forever, daemon=True).start()
CAP_ENV = dict(ENV, TASKHUB_URL=f"http://127.0.0.1:{cap.server_address[1]}")
READING = {"five_hour": {"used_percentage": 62.5, "resets_at": 1800000000}, "seven_day": {"used_percentage": 41, "resets_at": 1800300000}}
INPUT = json.dumps({
    "cwd": "D:/secret/project", "session_id": "SESSION", "session_name": "NAME", "transcript_path": "D:/secret/transcript.jsonl",
    "workspace": {"current_dir": "D:/secret/project"}, "model": {"display_name": "M"},
    "rate_limits": {**READING, "spend_limit": {"used_percentage": 9}, "extra": "SECRET"},
}).encode()


def wrap(command):
    write(side, {"statusLine": {"type": "command", "command": command}})


if os.path.exists(side):
    os.remove(side)
out, rc, _ = statusline(INPUT, CAP_ENV)
check("no line of theirs: prints nothing, exit 0", out == b"" and rc == 0, (out, rc))
check("the hub gets exactly the two windows' numbers", Capture.got == [("/api/limits/claude", READING)], Capture.got)
Capture.got.clear()
out, rc, _ = statusline(json.dumps({"cwd": "x", "rate_limits": {}}).encode(), CAP_ENV)
statusline(json.dumps({"cwd": "x"}).encode(), CAP_ENV)
statusline(b"not json", CAP_ENV)
statusline(json.dumps({"rate_limits": {"five_hour": {"used_percentage": "62"}}}).encode(), CAP_ENV)
check("no numbers (other plans, before the first answer, junk): nothing sent, exit 0", Capture.got == [] and rc == 0, Capture.got)

git = shutil.which("git")
bash = next((b for b in (os.path.normpath(os.path.join(os.path.dirname(git), up, "bin", "bash.exe")) for up in ("..", "../..")) if os.path.exists(b)), None) if git else None
if bash:
    script = os.path.join(home, "line.py")
    seen = os.path.join(home, "seen.bin")
    TEXT = "\x1b[32mрядок ✓\x1b[0m one\nsecond 中文\n".encode("utf-8")
    with open(script, "w", encoding="utf-8") as f:
        f.write(f"import sys\nopen({seen!r},'wb').write(sys.stdin.buffer.read())\nsys.stdout.buffer.write({TEXT!r})\n")
    wrap(f'"{fwd(sys.executable)}" "{fwd(script)}"')
    Capture.got.clear()
    out, rc, _ = statusline(INPUT, CAP_ENV)
    check("their output byte for byte (colours, two rows, UTF-8)", out == TEXT and rc == 0, (out, rc))
    check("their command gets the input unchanged", open(seen, "rb").read() == INPUT)
    check("...and the hub still only the numbers", Capture.got == [("/api/limits/claude", READING)], Capture.got)
    wrap("echo x; exit 3")
    out, rc, _ = statusline(INPUT, CAP_ENV)
    check("their exit code stays: Claude Code treats it as before", rc == 3 and out.strip() == b"x", (out, rc))
    wrap('echo "bash:$BASH_VERSION"')
    out, _, _ = statusline(INPUT, CAP_ENV)
    check("no shell above us: Git Bash when installed", out.startswith(b"bash:") and len(out.strip()) > 5, out)
    # started the way Claude Code does it, from the entry itself
    os.remove(side)
    integrations("claude-limits=on")
    entry = load(settings)["statusLine"]["command"]
    wrap("echo through-bash")
    out, rc, _ = statusline(INPUT, CAP_ENV, [bash, "-c", entry])
    check("the entry runs in Git Bash", out.strip() == b"through-bash" and rc == 0, (out, rc))
    wrap('echo "shell:$BASH_VERSION"')
    out, _, _ = statusline(INPUT, CAP_ENV, [bash, "-c", entry])
    check("...and their line runs in that same bash", out.startswith(b"shell:") and len(out.strip()) > 6, out)
    out, rc, _ = statusline(INPUT, CAP_ENV, ["powershell.exe", "-NoProfile", "-Command", "$input | " + entry])
    check("the entry runs in PowerShell", out.strip().startswith(b"shell:") and rc == 0, (out, rc))
    integrations("claude-limits=off")

    wrap("echo fine")
    down = dict(ENV, TASKHUB_URL="http://127.0.0.1:47893")
    out, rc, took = statusline(INPUT, down)
    check("the hub is down: the line still shows, soon", out.strip() == b"fine" and rc == 0 and took < 3, (out, rc, took))

    wrap("echo partial; sleep 60")
    out, rc, took = statusline(INPUT, CAP_ENV)
    check("a command that hangs is ended, what it printed stays", out.strip() == b"partial" and rc == 0 and took < 25, (out, rc, took))
else:
    print("SKIP tests that need Git Bash: not found")

path = "C:\\Windows\\System32;C:\\Windows\\System32\\WindowsPowerShell\\v1.0"
wrap("echo $PSVersionTable.PSVersion.Major")
out, rc, _ = statusline(INPUT, dict(CAP_ENV, PATH=path))
check("no Git Bash: PowerShell", out.strip().isdigit() and rc == 0, (out, rc))
wrap("this-command-does-not-exist-anywhere")
out, rc, _ = statusline(b"", CAP_ENV)
check("their command fails: its failure, not ours", out == b"" and rc != 0, (out, rc))
wrap(f'"{fwd(CLI)}" statusline')
out, rc, took = statusline(INPUT, CAP_ENV)
check("their line calling this command: no going round in circles", took < 8 and out == b"", (out, rc, took))
os.remove(side)
cap.shutdown()


# ---------------------------------------------------------------- the hub
def call(method, path, body=None):
    req = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                 headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(req, timeout=10) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, None


hub = None


def start():
    global hub
    hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data], env=ENV)
    for _ in range(80):
        try:
            if call("GET", "/api/health")[0] == 200:
                return
        except Exception:
            time.sleep(.25)
    raise SystemExit("hub did not start")


def stop():
    hub.terminate()
    try:
        hub.wait(10)
    except Exception:
        hub.kill()
    time.sleep(.5)


def toasts():
    p = os.path.join(data, "taskhub.log")
    with open(p, encoding="utf-8", errors="replace") as f:
        return [l.split("TOAST ", 1)[1].strip() for l in f if "TOAST " in l and "plan limit" in l]


def report(pct, resets=1800000000, w="five_hour"):
    return call("POST", "/api/limits/claude", {w: {"used_percentage": pct, "resets_at": resets}})[0]


def waited(n):
    for _ in range(30):
        if len(toasts()) >= n:
            break
        time.sleep(.1)
    time.sleep(.2)
    return len(toasts())


cfg = os.path.join(data, "config.json")
c = load(cfg) if os.path.exists(cfg) else {}
c.update({"toasts": False, "autostart": False, "update_check": False, "language": "en"})
write(cfg, c)
integrations("claude-limits=on")
R = int(time.time()) + 7200
W = R + 86400 * 3
try:
    start()
    report(50, R)
    st, v = call("GET", "/api/limits")
    r = v["claude"]["five_hour"]
    check("a reading is kept and shown", st == 200 and r["used_percentage"] == 50 and r["resets_at"] == R and r["at"] > 0, v)
    check("the panel's state has it", call("GET", "/api/tasks")[1]["limits"]["claude"]["five_hour"]["used_percentage"] == 50)
    check("under the threshold: no warning", waited(0) == 0, toasts())
    check("the panel gets the thresholds", call("GET", "/api/tasks")[1]["limits_warn"] == [75, 90])
    report(74.5, R)
    check("just under 75%: no warning", waited(0) == 0, toasts())
    report(76, R)
    check("75% crossed: a warning", waited(1) == 1 and toasts()[0].startswith("[warn] Claude Code: plan limit: 5 h: 76%"), toasts())
    report(83, R)
    report(82.5, R)
    report(83, R)
    check("once per limit window", waited(2) == 1, toasts())
    report(85, R)
    report(83, R)
    report(85, R)
    report(83, R)
    check("two sessions, one with an older number: no repeated warning", waited(2) == 1, toasts())
    check("...and the highest stands", call("GET", "/api/limits")[1]["claude"]["five_hour"]["used_percentage"] == 85)
    report(91, R)
    check("90% crossed: its own warning, as an error", waited(2) == 2 and toasts()[1].startswith("[error] "), toasts())
    report(97, R)
    check("...once", waited(3) == 2, toasts())
    report(90, R - 3600)
    check("a session in an earlier window: ignored", waited(3) == 2 and call("GET", "/api/limits")[1]["claude"]["five_hour"]["used_percentage"] == 97)
    report(99, int(time.time()) - 100, "seven_day")
    check("a reset already past: no warning, not kept", waited(3) == 2 and "seven_day" not in call("GET", "/api/limits")[1]["claude"], toasts())
    report(81.5, R + 60)
    check("resets_at wobbling within a window: no new warning", waited(3) == 2, toasts())
    report(5, R + 18000)
    check("resets_at moved on: a new window, the lower number shown", call("GET", "/api/limits")[1]["claude"]["five_hour"]["used_percentage"] == 5)
    R += 18000
    report(82, R)
    check("...which warns again", waited(3) == 3, toasts())
    report(99, W, "seven_day")
    check("a jump over both thresholds: one warning, the higher", waited(4) == 4 and toasts()[3].startswith("[error] ") and "week: 99%" in toasts()[3], toasts())
    report(99.5, W, "seven_day")
    check("...not the lower one afterwards", waited(5) == 4, toasts())
    check("the other window is its own", call("GET", "/api/limits")[1]["claude"]["five_hour"]["used_percentage"] == 82)
    report(90, None, "seven_day")
    check("no reset time now and a fall: a reset, so it warns", waited(5) == 5, toasts())
    report(40, None, "seven_day")
    check("no reset time: a fall is a reset", call("GET", "/api/limits")[1]["claude"]["seven_day"]["used_percentage"] == 40)
    report(90, None, "seven_day")
    check("...which warns again", waited(6) == 6, toasts())
    report(90, W, "seven_day")

    check("percentage as text: refused", report("62", R) == 400)
    check("no window at all: refused", call("POST", "/api/limits/claude", {"cwd": "x"})[0] == 400)
    report(150, R)
    check("out of range: held to 100", call("GET", "/api/limits")[1]["claude"]["five_hour"]["used_percentage"] == 100)
    n = len(toasts())
    check("an absurd reset time: taken as none, the warning not lost", report(100, 1e18, "five_hour") == 200 and call("GET", "/api/limits")[1]["claude"]["five_hour"]["resets_at"] is None)
    report(100, R)
    call("POST", "/api/limits/claude", {"five_hour": {"used_percentage": 100, "resets_at": R, "cwd": "SECRET"}, "cwd": "SECRET", "session_id": "SECRET"})
    time.sleep(.2)
    text = open(store, encoding="utf-8").read()
    kept = json.loads(text)
    check("kept: only numbers of the known windows", "SECRET" not in text and set(kept["claude"]) == {"five_hour", "seven_day"}
          and set(kept["claude"]["five_hour"]) == {"pct", "resets_at", "at", "warned"}, text)
    check("another agent: no route", call("POST", "/api/limits/other", {"five_hour": {"used_percentage": 1}})[0] == 404)
    m0 = os.path.getmtime(store)
    time.sleep(.05)
    report(100, R)
    time.sleep(.2)
    check("the same numbers again: nothing rewritten", os.path.getmtime(store) == m0)
    n = len(toasts())
    stop()
    start()
    check("after a restart: the reading is there", call("GET", "/api/limits")[1]["claude"]["five_hour"]["used_percentage"] == 100)
    report(100, R)
    check("...and warnings already given aren't repeated", waited(n + 1) == n, toasts())
    stop()

    c = load(cfg)
    c["limits_warn"] = [50]
    write(cfg, c)
    start()
    report(60, R + 90000)
    check("own thresholds (limits_warn)", waited(n + 1) == n + 1, toasts())

    # the switch is the file, also while the hub runs
    good = load(settings)
    broken = open(settings, "rb").read()
    with open(settings, "w") as f:
        f.write("{ not json")
    check("settings being edited (unreadable): readings stay", "claude" in call("GET", "/api/limits")[1])
    write(settings, dict(good, statusLine={"type": "command", "command": "echo theirs"}))
    check("the user replaced the entry: nothing shown any more", call("GET", "/api/limits")[1] == {}, call("GET", "/api/limits"))
    check("...nothing kept", load(store) == {}, open(store, encoding="utf-8").read())
    st, r = call("POST", "/api/limits/claude", {"five_hour": {"used_percentage": 99, "resets_at": R}})
    check("...a reading is ignored", st == 200 and r.get("ignored") is True and call("GET", "/api/limits")[1] == {}, r)
    check("...no warning", waited(n + 2) == n + 1, toasts())
    write(settings, good)
    report(10, R + 90000)
    check("put back: readings again", call("GET", "/api/limits")[1]["claude"]["five_hour"]["used_percentage"] == 10)
    check("off from a script while the hub runs: exit 0", integrations("claude-limits=off") == 0)
    check("...nothing shown", call("GET", "/api/limits")[1] == {}, call("GET", "/api/limits"))
    time.sleep(.2)
    check("...nothing kept", not os.path.exists(store) or load(store) == {})
    integrations("claude-limits=on")
    report(11, R + 90000)
    check("on from a script while the hub runs", call("GET", "/api/limits")[1]["claude"]["five_hour"]["used_percentage"] == 11)
    stop()
    write(settings, dict(good, statusLine={"type": "command", "command": "echo theirs"}))
    start()
    check("replaced while the hub was away: nothing shown", call("GET", "/api/limits")[1] == {})
    check("...nothing kept", load(store) == {})
finally:
    if hub:
        stop()
    shutil.rmtree(home, ignore_errors=True)
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(appdata, ignore_errors=True)

print("limits_test:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
