"""The local API wants a token that TaskHub generates: a client with no credentials gets 401 and runs nothing, the panel's page carries no token, and the CLI, hooks and Python client find it by themselves. Own hub, temp folders only; this test takes TASKHUB_TOKEN away so the real path is what runs. Arguments: TaskHubHost.exe taskhub.exe."""
import json, os, re, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.request

EXE, CLI = sys.argv[1], sys.argv[2]
HERE = os.path.dirname(os.path.abspath(__file__))
PORT = 47843
URL = f"http://127.0.0.1:{PORT}"
os.environ.pop("TASKHUB_TOKEN", None)   # the suite's own, see sitecustomize.py
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None, token=None, timeout=10):
    h = {"X-TaskHub": "1", "Content-Type": "application/json"}
    if token:
        h["Authorization"] = "Bearer " + token
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(), headers=h)
    try:
        with op.open(r, timeout=timeout) as f:
            raw = f.read()
            try:
                return f.status, json.loads(raw or b"{}")
            except ValueError:
                return f.status, raw.decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, {}


root = tempfile.mkdtemp(prefix="taskhub-token-")
home = os.path.join(root, "home")
data = os.path.join(home, "TaskHub")
os.makedirs(data)
with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"toasts": False, "autostart": False, "update_check": False, "language": "en", "port": PORT}, f)
env = dict(os.environ, TASKHUB_TEST_HOME=home)
env.pop("TASKHUB_URL", None)
env.pop("PYTHONPATH", None)
marker = os.path.join(root, "ran.txt")
shell = {"id": "go", "label": "Go", "kind": "shell", "command": f'cmd /c echo ran> "{marker}"'}


def start():
    h = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=env)
    for _ in range(60):
        try:
            call("GET", "/api/health", timeout=2)
            break
        except OSError:
            time.sleep(.5)
    return h


def cli(*args, **kw):
    return subprocess.run([CLI, *args], env=kw.get("env", env), capture_output=True, text=True, timeout=30, input=kw.get("input"))


hub = start()
try:
    # ---- a client with no credentials
    s, _ = call("POST", "/api/tasks", {"id": "no-cred", "name": "x", "controls": [shell]})
    check("no token: a task isn't created", s == 401, s)
    s2, _ = call("POST", "/api/tasks/no-cred/controls/go", {})
    time.sleep(1.5)
    check("no token: a shell button runs nothing", s2 == 401 and not os.path.exists(marker), (s2, os.path.exists(marker)))
    for method, path in (("GET", "/api/tasks"), ("GET", "/api/connectors"), ("GET", "/api/settings"), ("GET", "/api/guard/log"), ("POST", "/api/notify"), ("POST", "/api/settings"), ("POST", "/api/ui/open")):
        check(f"no token: {method} {path} is 401", call(method, path, {} if method == "POST" else None)[0] == 401)
    check("a wrong token is 401", call("GET", "/api/tasks", token="0" * 64)[0] == 401)
    check("health needs no token", call("GET", "/api/health")[0] == 200)
    check("the proof needs no token", call("GET", "/api/hub-proof?nonce=" + "a" * 32)[0] == 200)

    # ---- where the token lives
    path = os.path.join(data, "api.token")
    check("the token is generated", os.path.exists(path))
    token = open(path, encoding="ascii").read().strip()
    check("...long and random", re.fullmatch(r"[0-9a-fA-F]{64}", token) is not None, len(token))
    acl = subprocess.run(["icacls", path], capture_output=True, text=True).stdout
    check("...only this user can read it", "Everyone" not in acl and "BUILTIN\\Users" not in acl and "Authenticated Users" not in acl and os.environ["USERNAME"].lower() in acl.lower(), acl)
    conf = open(os.path.join(data, "config.json"), encoding="utf-8").read()
    check("...it isn't in config.json", token not in conf)

    # ---- the page
    s, html = call("GET", "/ui")
    check("/ui answers without a token", s == 200 and "<html" in str(html).lower(), s)
    check("...and doesn't carry it", token not in str(html) and "__TOKEN__" not in str(html))
    check("...nor the placeholder's old use", "const TOKEN = \"" not in str(html))

    # ---- with the token
    s, t = call("POST", "/api/tasks", {"id": "with", "name": "x", "controls": [shell]}, token=token)
    check("with the token: a task is created", s == 200, s)
    s, _ = call("POST", "/api/tasks/with/controls/go", {}, token=token)
    until = time.time() + 8
    while time.time() < until and not os.path.exists(marker):
        time.sleep(.1)
    check("...and its button works", s == 200 and os.path.exists(marker), s)

    # ---- the clients find it themselves
    r = cli("register", "--id", "from-cli", "--name", "from cli")
    check("the CLI finds the token", r.returncode == 0 and call("GET", "/api/tasks/from-cli", token=token)[0] == 200, r)
    r = cli("hook", "claude", input=json.dumps({"hook_event_name": "Notification", "message": "hi", "session_id": "s1", "cwd": root}))
    check("a hook: silent, exit 0", r.returncode == 0 and not r.stdout and not r.stderr, r)
    r = cli("token")
    check("`taskhub token` prints it", r.returncode == 0 and r.stdout.strip() == token, r.returncode)
    r = cli("ui", "--print")
    check("`taskhub ui --print`: the page, the token only in the fragment", r.returncode == 0 and r.stdout.strip() == f"{URL}/ui#token={token}", r.returncode)
    py = subprocess.run([sys.executable, "-c", "import taskhub_client as t; print(t.request('GET', '/api/tasks/from-cli')['id'])"],
                        env=dict(env, PYTHONPATH=os.path.join(HERE, "..", "clients", "python"), TASKHUB_URL=URL), capture_output=True, text=True, timeout=30)
    check("the Python client finds the token", py.returncode == 0 and py.stdout.strip() == "from-cli", (py.stdout, py.stderr[-300:]))
    mcp_in = "".join(json.dumps(m) + chr(10) for m in ({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "test", "version": "1"}}},
                                                       {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "task_list", "arguments": {}}}))
    m = subprocess.Popen([CLI, "mcp"], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    m.stdin.write(mcp_in)
    m.stdin.flush()
    lines = [m.stdout.readline(), m.stdout.readline()]
    m.stdin.close()
    m.wait(timeout=15)
    r = subprocess.CompletedProcess(m.args, m.returncode, "".join(lines), "")
    check("the MCP server finds the token", "from-cli" in r.stdout, (r.stdout[-300:], r.stderr[-200:]))
    r = cli("register", "--id", "own-url", "--name", "x", env=dict(env, TASKHUB_URL=URL))
    check("TASKHUB_URL at this hub: the CLI proves it and still finds the token", r.returncode == 0, r)

    # ---- nothing goes to whoever else answers
    other = subprocess.run([CLI, "--url", "http://127.0.0.1:9", "list"], env=dict(env, TASKHUB_NO_AUTOSTART="1"), capture_output=True, text=True, timeout=30)
    check("an address that isn't the hub: an error, no crash", other.returncode != 0, other.returncode)

    # ---- a token file that came with loose rights is closed at start
    hub.kill()
    hub.wait()
    os.remove(path)
    with open(path, "w", encoding="ascii") as f:
        f.write(token)
    loose = subprocess.run(["icacls", path], capture_output=True, text=True).stdout
    hub = start()
    acl = subprocess.run(["icacls", path], capture_output=True, text=True).stdout
    check("a token file with inherited rights gets this user's only", "(I)" in loose and "(I)" not in acl and "BUILTIN\\Users" not in acl, (loose, acl))
    check("...the token stays", call("GET", "/api/tasks", token=token)[0] == 200)

    # ---- a limited caller's id stays an id: MCP can't reach another route through it
    token = call("GET", "/api/hub-proof?nonce=" + "a" * 32) and open(path, encoding="ascii").read().strip()
    mark2 = os.path.join(root, "ran2.txt")
    s, _ = call("POST", "/api/tasks", {"id": "victim", "name": "x", "controls": [{"id": "go", "label": "Go", "kind": "shell", "command": f'cmd /c echo ran> "{mark2}"'}]}, token=token)
    check("(a task with a button)", s == 200, s)
    tools = [{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "1"}}}]
    bad = ["victim/controls/go?", "../tasks/victim/controls/go?", "..", "."]
    for i, b in enumerate(bad):
        tools.append({"jsonrpc": "2.0", "id": 10 + i, "method": "tools/call", "params": {"name": "task_update", "arguments": {"id": b, "status": "x"}}})
        tools.append({"jsonrpc": "2.0", "id": 30 + i, "method": "tools/call", "params": {"name": "task_finish", "arguments": {"id": b, "state": "done"}}})
    tools.append({"jsonrpc": "2.0", "id": 90, "method": "tools/call", "params": {"name": "task_update", "arguments": {"id": "victim", "status": "plain id works"}}})
    m = subprocess.Popen([CLI, "mcp"], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
    m.stdin.write("".join(json.dumps(x) + chr(10) for x in tools))
    m.stdin.flush()
    replies = [m.stdout.readline() for _ in tools]
    m.stdin.close()
    m.wait(timeout=15)
    time.sleep(1.5)
    check("MCP: an id with a path in it doesn't press a button", not os.path.exists(mark2), replies[1:5])
    check("...each one is refused", all('"result":{"content"' not in r or '"isError":true' in r for r in replies[1:-1]), replies[1:-1])
    check("...a plain id still works", call("GET", "/api/tasks/victim", token=token)[1].get("status") == "plain id works", replies[-1])

    # ---- a token file another account could read is closed too, even when it isn't inheriting
    hub.kill()
    hub.wait()
    subprocess.run(["icacls", path, "/inheritance:r", "/grant:r", "*S-1-1-0:(R)", f"{os.environ['USERNAME']}:(F)"], capture_output=True)
    loose = subprocess.run(["icacls", path], capture_output=True, text=True).stdout
    hub = start()
    acl = subprocess.run(["icacls", path], capture_output=True, text=True).stdout
    check("a protected token file that lets Everyone read it is closed", "Everyone" in loose and "Everyone" not in acl, (loose, acl))

    # ---- a token the hub takes, the clients take in the same form
    hub.kill()
    hub.wait()
    with open(path, "w", encoding="ascii") as f:
        f.write(token.lower())
    hub = start()
    r = cli("register", "--id", "lower-file", "--name", "x")
    check("a hand-written lower-case token file: the CLI still gets in", r.returncode == 0, r)
    r = cli("token")
    check("...`taskhub token` prints what works", r.returncode == 0 and call("GET", "/api/tasks", token=r.stdout.strip())[0] == 200, r)
    py = subprocess.run([sys.executable, "-c", "import taskhub_client as t; print(t.request('GET', '/api/tasks/lower-file')['id'])"],
                        env=dict(env, PYTHONPATH=os.path.join(HERE, "..", "clients", "python"), TASKHUB_URL=URL), capture_output=True, text=True, timeout=30)
    check("...the Python client too", py.returncode == 0 and py.stdout.strip() == "lower-file", (py.stdout, py.stderr[-300:]))
    hub.kill()
    hub.wait()
    conf = json.load(open(os.path.join(data, "config.json"), encoding="utf-8"))
    conf["token"] = 12345
    json.dump(conf, open(os.path.join(data, "config.json"), "w", encoding="utf-8"))
    hub = start()
    r = cli("register", "--id", "num-token", "--name", "x")
    check("a number as the config token: the CLI uses it like the hub", r.returncode == 0, r)
    py = subprocess.run([sys.executable, "-c", "import taskhub_client as t; print(t.request('GET', '/api/tasks/num-token')['id'])"],
                        env=dict(env, PYTHONPATH=os.path.join(HERE, "..", "clients", "python"), TASKHUB_URL=URL), capture_output=True, text=True, timeout=30)
    check("...the Python client too", py.returncode == 0 and py.stdout.strip() == "num-token", (py.stdout, py.stderr[-300:]))
    del conf["token"]
    hub.kill()
    hub.wait()
    json.dump(conf, open(os.path.join(data, "config.json"), "w", encoding="utf-8"))
    hub = start()

    # ---- commands that hand out the token say what's wrong
    token = open(path, encoding="ascii").read().strip()
    dead = dict(env, TASKHUB_NO_AUTOSTART="1", TASKHUB_TOKEN="abc")
    r = subprocess.run([CLI, "--url", "http://127.0.0.1:9", "ui", "--print"], env=dead, capture_output=True, text=True, timeout=30)
    check("`taskhub ui` with the hub down: says so, gives no dead page", r.returncode != 0 and "127.0.0.1:9" not in r.stdout and "not running" in r.stderr, r)
    r = subprocess.run([CLI, "--url", "http://127.0.0.1:9", "token"], env=dict(env, TASKHUB_NO_AUTOSTART="1"), capture_output=True, text=True, timeout=30)
    check("`taskhub token` with the hub down: says so, not 'no token'", r.returncode != 0 and "not running" in r.stderr, r)

    # ---- help about a child doesn't promise the API without the token
    r = cli("--help")
    check("help: a child uses the taskhub command or `taskhub token`", "taskhub token" in r.stdout and "so it can call the API itself" not in r.stdout, r.stdout[-400:])

    # ---- a user's own token
    hub.kill()
    hub.wait()
    conf = json.load(open(os.path.join(data, "config.json"), encoding="utf-8"))
    conf["token"] = "my own token"
    json.dump(conf, open(os.path.join(data, "config.json"), "w", encoding="utf-8"))
    hub = start()
    check("a token in config.json replaces the generated one", call("GET", "/api/tasks", token="my own token")[0] == 200 and call("GET", "/api/tasks", token=token)[0] == 401)
    r = cli("register", "--id", "own-token", "--name", "x")
    check("...the CLI uses it", r.returncode == 0, r)
finally:
    hub.kill()
    hub.wait()
    shutil.rmtree(root, ignore_errors=True)

print("TOKEN:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
