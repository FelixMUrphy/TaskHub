"""Telegram is set up only on the settings page: over the API of a hub that isn't a test hub, no pairing and no pairing link; the old chat is told when another one is linked. Own hubs, own mock, temp folders only. Argument: TaskHubHost.exe."""
import json, os, re, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

EXE = sys.argv[1]
PORT, MOCK = 47844, 47843
URL = f"http://127.0.0.1:{PORT}"
TOKEN = "123456789:" + "T" * 35
OLD, NEW = 4101, 4102
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
sent, updates = [], []
tg = threading.Condition()
uid = [1000]


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


class Bot(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def end_headers(self):
        self.send_header("Connection", "close")
        super().end_headers()

    def reply(self, obj):
        data = json.dumps(obj).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        m = re.match(r"^/bot([^/]+)/(\w+)$", self.path)
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        body = {} if "multipart" in (self.headers.get("Content-Type") or "") else json.loads(raw or b"{}")
        method = m.group(2) if m else None
        if method == "getMe":
            return self.reply({"ok": True, "result": {"id": 1, "is_bot": True, "username": "gate_test_bot"}})
        if method == "getUpdates":
            off = body.get("offset", 0)
            with tg:
                if off == -1:
                    return self.reply({"ok": True, "result": []})
                tg.wait_for(lambda: any(u["update_id"] >= off for u in updates), timeout=min(body.get("timeout", 0), 1))
                return self.reply({"ok": True, "result": [u for u in updates if u["update_id"] >= off]})
        if method == "sendMessage":
            with tg:
                sent.append(body)
                tg.notify_all()
            return self.reply({"ok": True, "result": {"message_id": len(sent)}})
        self.reply({"ok": True, "result": True})


def call(method, path, body=None, timeout=30):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(), headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=timeout) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def until(pred, seconds=15):
    end = time.time() + seconds
    while time.time() < end:
        if pred():
            return True
        time.sleep(.1)
    return False


def start(test_home):
    data = tempfile.mkdtemp(dir=root)
    with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
        json.dump({"toasts": False, "autostart": False, "update_check": False, "language": "en", "telegram_api": f"http://127.0.0.1:{MOCK}"}, f)
    env = {k: v for k, v in os.environ.items() if k != "TASKHUB_TEST_HOME"}
    if test_home:
        env["TASKHUB_TEST_HOME"] = tempfile.mkdtemp(dir=root)
    h = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=env)
    for _ in range(60):
        try:
            call("GET", "/api/health", timeout=2)
            break
        except OSError:
            time.sleep(.5)
    return h


def stop(h):
    h.kill()
    h.wait()
    time.sleep(.3)


def link_chat(code, chat):
    with tg:
        uid[0] += 1
        updates.append({"update_id": uid[0], "message": {"message_id": 1, "chat": {"id": chat, "type": "private"}, "from": {"id": chat}, "text": "/start " + code}})
        tg.notify_all()


NO_VAULT = vault.probe()
if NO_VAULT:
    print(f"SKIP Telegram gate: Credential Manager unavailable here ({NO_VAULT})")
    raise SystemExit(0)

root = tempfile.mkdtemp(prefix="taskhub-telegram-gate-")
ThreadingHTTPServer.request_queue_size = 64
server = ThreadingHTTPServer(("127.0.0.1", MOCK), Bot)
threading.Thread(target=server.serve_forever, daemon=True).start()
try:
    # a hub as a user runs it: the settings page is the only way in. The token is one no test keeps, and it isn't saved here
    h = start(False)
    try:
        s, r = call("POST", "/api/telegram", {"token": TOKEN})
        check("a real hub: POST /api/telegram isn't there", s == 404 and not r.get("link"), (s, r))
        s, r = call("POST", "/api/telegram", {"relink": True})
        check("...nor relink", s == 404, (s, r))
        s, r = call("GET", "/api/telegram")
        check("...the state can still be read, without a link", s == 200 and "link" not in r, (s, r))
    finally:
        if call("POST", "/api/telegram", {"disconnect": True})[0] == 200:   # the old build took the token: don't leave it
            pass
        stop(h)

    # a test hub: the old chat is told when another one is linked
    h = start(True)
    try:
        s, r = call("POST", "/api/telegram", {"token": TOKEN})
        check("a test hub: pairing works", s == 200 and "start=" in (r.get("link") or ""), (s, r))
        link_chat(r["link"].rsplit("start=", 1)[-1], OLD)
        check("(the first chat is linked)", until(lambda: call("GET", "/api/telegram")[1].get("linked")), call("GET", "/api/telegram")[1])
        s, r = call("POST", "/api/telegram", {"relink": True})
        link_chat(r["link"].rsplit("start=", 1)[-1], NEW)
        check("another chat linked: the new one hears", until(lambda: any(m["chat_id"] == NEW for m in sent)), sent[-3:])
        check("...and the old one is told", until(lambda: any(m["chat_id"] == OLD and "another Telegram chat" in m.get("text", "") for m in sent)), sent[-3:])
    finally:
        call("POST", "/api/telegram", {"disconnect": True})
        stop(h)
finally:
    server.shutdown()
    shutil.rmtree(root, ignore_errors=True)

print("TELEGRAM GATE:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
