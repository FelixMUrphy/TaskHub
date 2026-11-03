"""The CLI checks who holds the hub's port before it sends anything: with TaskHub closed, another program on that port must get neither an
agent's event nor the token. Own port and temporary folders (TASKHUB_TEST_HOME): never the real hub or real agent settings."""
import hashlib, hmac, http.server, json, os, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request

EXE = sys.argv[1]
CLI = os.path.join(os.path.dirname(EXE), "taskhub.exe")
PORT = 47877
URL = f"http://127.0.0.1:{PORT}"
home = tempfile.mkdtemp(prefix="taskhub-proof-home-")
data = os.path.join(home, "TaskHub")
os.makedirs(data)
KEY = os.path.join(data, "hub.key")
TOKEN = "proof-test-token-not-a-real-one"
MARK = "last-message-of-the-agent"
ENV = {k: v for k, v in os.environ.items() if k not in ("TASKHUB_URL", "TASKHUB_TOKEN")}
ENV.update(TASKHUB_TEST_HOME=home, TASKHUB_NO_AUTOSTART="1", APPDATA=data)
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None, headers=None):
    h = {"X-TaskHub": "1", "Content-Type": "application/json", **(headers or {})}
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(), headers=h)
    try:
        with op.open(r, timeout=10) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def config(**over):
    with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
        json.dump({"port": PORT, "toasts": False, "autostart": False, "update_check": False, **over}, f)


def start_hub():
    p = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=ENV)
    end = time.time() + 30
    while time.time() < end:
        try:
            if call("GET", "/api/health")[0] == 200:
                return p
        except OSError:
            pass
        time.sleep(0.2)
    raise RuntimeError("hub did not start")


def stop(p):
    p.kill()
    p.wait(10)


class Listener:
    """Another program on the hub's port. `answer(path)` gives (status, JSON) for the proof request."""

    def __init__(self, answer=None):
        self.seen = []
        seen, self.answer = self.seen, answer or (lambda path: (404, {}))
        outer = self

        class H(http.server.BaseHTTPRequestHandler):
            def reply(self):
                n = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(n).decode("utf-8", "replace") if n else ""
                seen.append({"method": self.command, "path": self.path, "auth": self.headers.get("Authorization"), "body": body})
                status, obj = outer.answer(self.path) if "hub-proof" in self.path else (200, {"app": "TaskHub"})
                b = json.dumps(obj).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(b)))
                self.end_headers()
                self.wfile.write(b)
            do_GET = do_POST = do_DELETE = reply

            def log_message(self, *a):
                pass

        self.srv = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.srv.shutdown()
        self.srv.server_close()

    def leaked(self):
        return [q for q in self.seen if q["auth"] or MARK in q["body"] or "hub-proof" not in q["path"]]


def hook():
    payload = json.dumps({"hook_event_name": "Stop", "session_id": "s1", "cwd": "C:/demo", "last_assistant_message": MARK})
    return subprocess.run([CLI, "hook", "claude"], env=ENV, input=payload.encode(), capture_output=True, timeout=30)


def notify(env=None):
    return subprocess.run([CLI, "notify", "title-" + MARK], env=env or ENV, capture_output=True, timeout=30)


hub = None
try:
    # ------------------------------------------------------------ the real hub proves itself
    config(token=TOKEN)
    hub = start_hub()
    check("the hub made its key", os.path.exists(KEY) and len(open(KEY).read().strip()) == 64)
    if not os.path.exists(KEY):   # so that one failure doesn't hide the checks below
        with open(KEY, "w") as f:
            f.write("ab" * 32)
    key = open(KEY).read().strip()
    acl = subprocess.run(["icacls", KEY], capture_output=True, text=True).stdout
    check("only this user can read the key", "Everyone" not in acl and "BUILTIN\\Users" not in acl and "Authenticated Users" not in acl, acl)
    nonce = "a" * 32
    code, r = call("GET", f"/api/hub-proof?nonce={nonce}")
    good = hmac.new(bytes.fromhex(key), f"taskhub-hub-proof|{PORT}|{nonce}".encode(), hashlib.sha256).hexdigest()
    check("the proof needs no token and is what the key gives", code == 200 and r.get("proof") == good, (code, r))
    check("another nonce, another proof", call("GET", f"/api/hub-proof?nonce={'b' * 32}")[1].get("proof") not in (None, good))
    for bad in ("", "?nonce=", "?nonce=short", "?nonce=" + "z" * 32, "?nonce=" + "a" * 500):
        check("a bad nonce is refused: " + bad[:14], call("GET", "/api/hub-proof" + bad)[0] == 400)
    code, _ = call("GET", "/api/tasks")
    check("the rest of the API still wants the token", code == 401, code)
    r = notify()
    check("the CLI talks to the real hub, with the token", r.returncode == 0, r)
    r = hook()
    check("a hook on the real hub: silent, exit 0", r.returncode == 0 and not r.stdout and not r.stderr, r)
    stop(hub)
    hub = start_hub()
    check("the key stays across a restart", open(KEY).read().strip() == key)
    stop(hub)

    # ------------------------------------------------------------ hub closed, another program on its port
    answers = {
        "doesn't know the route": lambda path: (404, {}),
        "answers without a proof": lambda path: (200, {"ok": True}),
        "answers a made-up proof": lambda path: (200, {"proof": "0" * 64}),
        "answers junk": lambda path: (200, {"proof": 5}),
    }
    for name, ans in answers.items():
        with Listener(ans) as other:
            r = hook()
            check(f"{name}: the hook stays silent, exit 0", r.returncode == 0 and not r.stdout and not r.stderr, r)
            check(f"{name}: it gets no event and no token", not other.leaked(), other.leaked())
            r = notify()
            check(f"{name}: the CLI says so and exits with an error", r.returncode != 0 and b"isn't TaskHub" in r.stderr + r.stdout, r)
            check(f"{name}: the CLI's message and token stay here", not other.leaked(), other.leaked())

    # a proof made for another port can't be replayed through a relay
    with Listener(lambda path: (200, {"proof": hmac.new(bytes.fromhex(key), f"taskhub-hub-proof|47800|{path.split('=')[-1]}".encode(), hashlib.sha256).hexdigest()})) as other:
        r = hook()
        check("a proof for another port is refused", not other.leaked(), other.leaked())

    # ------------------------------------------------------------ where nothing can be checked it works as before
    with Listener() as other:
        env = dict(ENV, TASKHUB_URL=URL)
        r = notify(env)
        check("TASKHUB_URL set: the user chose the address, no check", r.returncode == 0 and any(MARK in q["body"] for q in other.seen) and not any("hub-proof" in q["path"] for q in other.seen), (r, other.seen))
    os.remove(KEY)
    with Listener() as other:
        r = hook()
        check("no key file (not visible to this copy): as before", r.returncode == 0 and any(MARK in q["body"] for q in other.seen), other.seen)
    with open(KEY, "w") as f:
        f.write("not a key")
    hub = start_hub()
    check("a damaged key is replaced", len(open(KEY).read().strip()) == 64)
    check("...and the CLI works with the new one", notify().returncode == 0)
    stop(hub)
finally:
    if hub and hub.poll() is None:
        stop(hub)
    shutil.rmtree(home, ignore_errors=True)

print("HUB PROOF TESTS " + ("PASSED" if not fails else f"FAILED ({fails})"))
raise SystemExit(1 if fails else 0)
