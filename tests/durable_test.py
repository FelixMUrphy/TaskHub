"""A damaged config.json or state.json isn't lost or overwritten, the user is told; an empty agent file isn't replaced. Own hub, own home, temp folders only. Argument: TaskHubHost.exe."""
import glob, json, os, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.request

EXE = sys.argv[1]
PORT = 47857
URL = f"http://127.0.0.1:{PORT}"
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
BROKEN = b'{"keep_finished": 77, "toasts": false,, "autostart": false, "connectors": {"github": {"interval": 123}}'
GOOD = {"toasts": False, "autostart": False, "update_check": False, "language": "en", "keep_finished": 77}


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None, timeout=30):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(), headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=timeout) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def start(data, home):
    h = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=dict(os.environ, TASKHUB_TEST_HOME=home))
    for _ in range(60):
        try:
            call("GET", "/api/health", timeout=2)
            return h
        except OSError:
            time.sleep(.5)
    return h


def parsed(path):
    """The file's JSON object, or {} when it is missing or broken: a failed check, not a crash that hides the checks after it."""
    try:
        with open(path, "rb") as f:
            v = json.loads(f.read())
        return v if isinstance(v, dict) else {}
    except (OSError, ValueError):
        return {}


def stop(h):
    h.kill()
    h.wait()
    time.sleep(.3)


def read(path):
    with open(path, "rb") as f:
        return f.read()


def bad_copies(path):
    return glob.glob(path + ".bad-*")


def warned(task):
    code, t = call("GET", f"/api/tasks/{task}")
    return code == 200 and t.get("state") == "failed"


def save_config_change():
    return call("POST", "/api/connectors/github", {"interval": 60})[0]


root = tempfile.mkdtemp(prefix="taskhub-durable-")
try:
    # ---- damaged, no earlier good copy
    for name, content in (("a typo", BROKEN), ("an empty file", b""), ("not an object", b"[1, 2]")):
        data, home = tempfile.mkdtemp(dir=root), tempfile.mkdtemp(dir=root)
        cfg = os.path.join(data, "config.json")
        with open(cfg, "wb") as f:
            f.write(content)
        h = start(data, home)
        try:
            check(f"{name}: the hub starts", call("GET", "/api/health")[0] == 200)
            check(f"{name}: the panel says so", warned("taskhub-config-damaged"), call("GET", "/api/tasks/taskhub-config-damaged"))
            code = save_config_change()
            time.sleep(.5)
            check(f"{name}: a change isn't written over it", read(cfg) == content, read(cfg)[:120])
            copies = bad_copies(cfg)
            check(f"{name}: a copy stays beside it", len(copies) == 1 and read(copies[0]) == content, copies)
        finally:
            stop(h)
        # the user corrects the file: it counts again
        with open(cfg, "w", encoding="utf-8") as f:
            json.dump(GOOD, f)
        h = start(data, home)
        try:
            before = read(cfg)
            save_config_change()
            time.sleep(.5)
            after = json.loads(read(cfg))
            check(f"{name}: corrected by hand, changes are saved", after.get("keep_finished") == 77 and read(cfg) != before, after)
        finally:
            stop(h)

    # ---- damaged, with the copy of the last good one
    data, home = tempfile.mkdtemp(dir=root), tempfile.mkdtemp(dir=root)
    cfg = os.path.join(data, "config.json")
    with open(cfg, "w", encoding="utf-8") as f:
        json.dump(GOOD, f)
    h = start(data, home)
    save_config_change()
    stop(h)
    check("a save keeps the previous version", os.path.exists(cfg + ".bak") and parsed(cfg + ".bak").get("keep_finished") == 77)
    with open(cfg, "wb") as f:
        f.write(BROKEN)
    h = start(data, home)
    try:
        time.sleep(.5)
        restored = parsed(cfg)
        check("damaged with a good copy: settings come back", restored.get("keep_finished") == 77, restored)
        check("...the damaged file is kept", len(bad_copies(cfg)) == 1 and read(bad_copies(cfg)[0]) == BROKEN)
        check("...the panel says so", warned("taskhub-config-damaged"))
        check("...changes work", save_config_change() == 200)
        time.sleep(.5)
        check("...the good copy isn't replaced by the damaged file", os.path.exists(cfg + ".bak") and b",," not in read(cfg + ".bak"))
    finally:
        stop(h)

    # ---- state.json
    data, home = tempfile.mkdtemp(dir=root), tempfile.mkdtemp(dir=root)
    with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
        json.dump(GOOD, f)
    state = os.path.join(data, "state.json")
    h = start(data, home)
    call("POST", "/api/tasks", {"id": "first", "name": "first"})
    time.sleep(7)
    call("POST", "/api/tasks", {"id": "second", "name": "second"})
    time.sleep(7)
    stop(h)
    with open(state, "wb") as f:
        f.write(b'{"tasks": [{"id": "half')
    h = start(data, home)
    try:
        check("a damaged state.json: the tasks of the last good copy are back", call("GET", "/api/tasks/first")[0] == 200, call("GET", "/api/tasks")[1])
        check("...the damaged file is kept", len(bad_copies(state)) == 1 and read(bad_copies(state)[0]).startswith(b'{"tasks": [{"id": "half'))
        check("...the panel says so", warned("taskhub-state-damaged"))
    finally:
        stop(h)

    # ---- an empty agent file: another program may be writing it
    data, home = tempfile.mkdtemp(dir=root), tempfile.mkdtemp(dir=root)
    claude = os.path.join(home, ".claude.json")
    with open(claude, "wb"):
        pass
    r = subprocess.run([EXE, "--data", data, "--integrations", "claude-mcp=on"], env=dict(os.environ, TASKHUB_TEST_HOME=home), capture_output=True, timeout=30)
    check("an empty ~/.claude.json isn't written over", read(claude) == b"", read(claude)[:120])
    check("...and it's reported as an error", r.returncode != 0, r.returncode)
    # a file with content: the version from just before each write stays
    with open(claude, "w", encoding="utf-8") as f:
        json.dump({"mine": 1}, f)
    r = subprocess.run([EXE, "--data", data, "--integrations", "claude-mcp=on"], env=dict(os.environ, TASKHUB_TEST_HOME=home), capture_output=True, timeout=30)
    check("a file with content: TaskHub's entry goes in", r.returncode == 0 and parsed(claude).get("mine") == 1, (r.returncode, read(claude)[:200]))
    check("...the version before the write is kept", parsed(claude + ".taskhub.last") == {"mine": 1})
finally:
    shutil.rmtree(root, ignore_errors=True)

print("DURABLE:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
