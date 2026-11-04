"""Discord bridge against a mock of Discord's REST API and gateway, as strict as the documentation (loopback only, port 47883; hub on 47882, temp data).
Argument: TaskHubHost.exe. Nothing here talks to Discord. What only a real bot can show is listed in .ai/discord-research.md."""
import base64, hashlib, json, os, re, shutil, socket, struct, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

EXE = sys.argv[1]
PORT, MOCK = 47882, 47883
CLOSED = 47881   # nothing listens here
URL = f"http://127.0.0.1:{PORT}"
TOKEN = "MTIzNDU2Nzg5MDEyMzQ1Njc4.GabcDe." + "x" * 38
OTHER_TOKEN = "T3RoZXJUb2tlbk90aGVyVG9rZW4.GabcDe." + "y" * 38
APP, OWNER, DM, STRANGER = "1234567890123456781", "1234567890123456789", "1234567890123456787", "1234567890123456785"   # past what a double holds
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
lock = threading.Condition()


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {str(extra)[:600]}"), flush=True)
    if not cond:
        fails += 1


def until(f, t=10):
    end = time.time() + t
    while time.time() < end:
        r = f()
        if r:
            return r
        time.sleep(.05)
    return f()


class M:
    """What Discord would know. Reset per scenario with care."""
    mutual = False; dm_refused = False; app_endpoint = ""; owner_in_team = False; dm_fault = 0
    remaining = 999; gateway_url = None; resume_url = None
    interval = 45000; ack = True
    expire_presses = False; msg_faults = []          # next POST message answers: 429 / 429-html / 502 / 401 / 403
    msgs = []; edits = []; callbacks = []; webhooks = []; bad = []; commands = []; dms = []; auth401 = 0; files = []; agents = []; msg_seq = 500; press_seq = 900
    conns = []; identifies = []; resumes = []; seen_presses = {}


def valid_components(rows):
    if rows is None:
        return None
    if not isinstance(rows, list) or len(rows) > 5:
        return "too many rows"
    ids, total = set(), 0
    for r in rows:
        if r.get("type") != 1:
            return "row type"
        kids = r.get("components") or []
        total += 1 + len(kids)
        if not kids:
            return "empty row"
        kinds = {k.get("type") for k in kids}
        if 3 in kinds and len(kids) != 1:
            return "select shares a row"
        if len(kids) > 5:
            return "more than 5 in a row"
        for k in kids:
            cid = k.get("custom_id", "")
            if not 1 <= len(cid) <= 100 or cid in ids:
                return "custom_id " + cid
            ids.add(cid)
            if k["type"] == 2 and (not 1 <= len(k.get("label", "")) <= 80 or k.get("style") not in (1, 2, 3, 4)):
                return "button " + str(k)
            if k["type"] == 3:
                o = k.get("options") or []
                if not 1 <= len(o) <= 25 or any(not 1 <= len(x["label"]) <= 100 or not 1 <= len(x["value"]) <= 100 for x in o) or len(k.get("placeholder", "")) > 150:
                    return "select " + str(k)[:200]
    return None if total <= 40 else "too many components"


def valid_message(b, need_content=True):
    if (b.get("flags") or 0) & 32768:
        return "components v2 flag: content would be refused"
    c = b.get("content")
    if need_content and not c and not b.get("components"):
        return "empty"
    if c is not None and len(c) > 2000:
        return "content over 2000"
    if b.get("allowed_mentions") != {"parse": []}:
        return "allowed_mentions missing"
    return valid_components(b.get("components"))


class Gw:
    def __init__(self, handler, path):
        self.h, self.path, self.events, self.alive, self.wlock = handler, path, [], True, threading.Lock()
        self.s = 0

    def frame(self, opcode, data):
        n = len(data)
        head = bytes([0x80 | opcode]) + (bytes([n]) if n < 126 else bytes([126]) + struct.pack(">H", n) if n < 65536 else bytes([127]) + struct.pack(">Q", n))
        with self.wlock:
            self.h.wfile.write(head + data)
            self.h.wfile.flush()

    def send(self, o):
        o.setdefault("s", None), o.setdefault("t", None)
        self.frame(1, json.dumps(o).encode())

    def dispatch(self, t, d):
        self.s += 1
        self.send({"op": 0, "s": self.s, "t": t, "d": d})

    def close(self, code):
        try:
            self.frame(8, struct.pack(">H", code))
        except (OSError, ValueError):   # the hub may have closed it already
            pass
        self.alive = False

    def drop(self):
        self.alive = False
        try:
            self.h.connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass

    def read(self):
        r = self.h.rfile
        a = r.read(2)
        if len(a) < 2:
            return None
        n = a[1] & 127
        if n == 126:
            n = struct.unpack(">H", r.read(2))[0]
        elif n == 127:
            n = struct.unpack(">Q", r.read(8))[0]
        key = r.read(4)
        data = bytes(b ^ key[i % 4] for i, b in enumerate(r.read(n)))
        return a[0] & 15, data

    def run(self):
        first = True
        self.send({"op": 10, "d": {"heartbeat_interval": M.interval}})
        while self.alive:
            f = self.read()
            if f is None:
                break
            code, data = f
            if code == 8:
                self.close_code = struct.unpack(">H", data[:2])[0] if len(data) >= 2 else None
                try:
                    self.frame(8, data)
                except OSError:
                    pass
                break
            if len(data) > 4096:
                self.close(4002)
                break
            m = json.loads(data)
            with lock:
                self.events.append(m)
                lock.notify_all()
            o = m.get("op")
            if first and o not in (1, 2, 6):
                self.close(4003)   # not authenticated
                break
            if o == 1:
                if M.ack:
                    self.send({"op": 11})
            elif o == 2:
                first = False
                d = m["d"]
                with lock:
                    M.identifies.append((time.time(), d))
                    M.remaining -= 1
                    lock.notify_all()
                if d.get("token") not in (TOKEN, OTHER_TOKEN) or "properties" not in d or not isinstance(d.get("intents"), int):
                    self.close(4004 if d.get("token") not in (TOKEN, OTHER_TOKEN) else 4002)
                    break
                self.dispatch("READY", {"v": 10, "user": {"id": "1", "username": "taskhub_bot", "discriminator": "0", "bot": True}, "guilds": [], "session_id": f"sess{len(M.identifies)}",
                                        "resume_gateway_url": M.resume_url or f"ws://127.0.0.1:{MOCK}/resume", "application": {"id": APP, "flags": 0}, "shard": None})
                self.dispatch("GUILD_CREATE", {"id": "77", "name": "noise", "unavailable": False, "extra": {"unknown": [1, 2, 3]}})
            elif o == 6:
                first = False
                with lock:
                    M.resumes.append(m["d"])
                    lock.notify_all()
                self.s = m["d"].get("seq") or 0
                self.dispatch("RESUMED", {})


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def reply(self, code, obj=None, headers=None, raw=None):
        body = raw if raw is not None else (b"" if obj is None else json.dumps(obj).encode())
        self.send_response(code)
        self.send_header("Content-Length", str(len(body)))
        if obj is not None:
            self.send_header("Content-Type", "application/json")
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n)

    def strict(self):
        """What every call needs. False: already answered."""
        ua = self.headers.get("User-Agent", "")
        with lock:
            M.agents.append(ua)
        if not re.match(r"^DiscordBot \(https://\S+, [\d.]+\)", ua):
            self.reply(403, {"message": "bad user agent"})   # Cloudflare's answer, in the docs' words "may be blocked"
            return False
        return True

    def authed(self):
        if self.headers.get("Authorization") not in (f"Bot {TOKEN}", f"Bot {OTHER_TOKEN}"):
            with lock:
                M.auth401 += 1
            self.reply(401, {"message": "401: Unauthorized", "code": 0})
            return False
        return True

    def do_GET(self):
        if self.headers.get("Upgrade", "").lower() == "websocket":
            return self.upgrade()
        if not self.strict() or not self.authed():
            return
        p = self.path
        if p == "/api/v10/users/@me":
            return self.reply(200, {"id": "1", "username": "taskhub_bot", "discriminator": "0", "bot": True})
        if p == "/api/v10/applications/@me":
            a = {"id": APP, "name": "TaskHub", "interactions_endpoint_url": M.app_endpoint or None, "flags": 0}
            if M.owner_in_team:
                a.update(owner={"id": "555", "username": "team"}, team={"id": "9", "owner_user_id": OWNER, "members": []})
            else:
                a["owner"] = {"id": OWNER, "username": "owner"}
            return self.reply(200, a)
        if p == "/api/v10/gateway/bot":
            return self.reply(200, {"url": M.gateway_url or f"ws://127.0.0.1:{MOCK}/", "shards": 1, "session_start_limit": {"total": 1000, "remaining": M.remaining, "reset_after": 1500, "max_concurrency": 1}})
        self.reply(404, {"message": "404: Not Found", "code": 0})

    def upgrade(self):
        q = self.path.split("?", 1)[1] if "?" in self.path else ""
        if "v=10" not in q or "encoding=json" not in q or not self.strict():
            return self.reply(400, {"message": "bad query"})
        key = self.headers.get("Sec-WebSocket-Key", "")
        accept = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
        self.wfile.write(f"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n".encode())
        self.wfile.flush()
        g = Gw(self, self.path)
        with lock:
            M.conns.append(g)
            lock.notify_all()
        try:
            g.run()
        except (OSError, ValueError):
            pass
        g.alive = False
        self.close_connection = True

    def do_POST(self):
        if not self.strict():
            self.body()
            return
        raw = self.body()
        p = self.path
        m = re.match(r"^/api/v10/interactions/(\d+)/([\w-]+)/callback$", p)
        if m:
            b = json.loads(raw)
            with lock:
                M.callbacks.append({"id": m.group(1), "token": m.group(2), "body": b, "auth": self.headers.get("Authorization")})
                lock.notify_all()
                late = M.expire_presses
                again = M.seen_presses.get(m.group(1))
                M.seen_presses[m.group(1)] = True
            if late:
                return self.reply(404, {"message": "Unknown interaction", "code": 10062})
            if again:
                return self.reply(400, {"message": "Interaction has already been acknowledged.", "code": 40060})
            t = b.get("type")
            d = b.get("data") or {}
            if t == 5 and (d.get("flags") or 0) & ~64:
                return self.reply(400, {"code": 50035, "message": "Invalid Form Body"})
            if t in (4, 7):
                err = valid_message(d, need_content=t == 4)
                if err:
                    with lock:
                        M.bad.append(("callback", err))
                    return self.reply(400, {"code": 50035, "message": "Invalid Form Body"})
            if t == 9:
                comps = d.get("components") or []
                if not 1 <= len(d.get("title", "")) <= 45 or not 1 <= len(comps) <= 5 or not 1 <= len(d.get("custom_id", "")) <= 100 \
                        or any(c.get("type") != 18 or not 1 <= len(c.get("label", "")) <= 45 or c.get("component", {}).get("type") != 4 or c["component"].get("style") not in (1, 2) for c in comps):
                    with lock:
                        M.bad.append(("modal", str(d)[:200]))
                    return self.reply(400, {"code": 50035, "message": "Invalid Form Body"})
            return self.reply(204)
        if not self.authed():
            return
        if p == "/api/v10/users/@me/channels":
            b = json.loads(raw)
            with lock:
                M.dms.append(b.get("recipient_id"))
            if M.dm_fault:
                return self.reply(M.dm_fault, raw=b"<html>Bad gateway</html>")
            if not M.mutual:
                return self.reply(400, {"message": "Cannot send messages to this user due to having no mutual guilds", "code": 50278})
            return self.reply(200, {"id": DM, "type": 1, "last_message_id": None, "recipients": [{"id": OWNER, "username": "owner", "discriminator": "0"}]})
        m = re.match(r"^/api/v10/channels/(\d+)/messages$", p)
        if m:
            ct = self.headers.get("Content-Type", "")
            png = None
            if ct.startswith("multipart/form-data"):
                head = re.search(rb'name="?payload_json"?.*?\r\n\r\n(.*?)\r\n--', raw, re.S)
                b = json.loads(head.group(1)) if head else {}
                f = re.search(rb'name="?files\[0\]"?.*?\r\n\r\n(.*)', raw, re.S)
                png = f.group(1) if f else None
            else:
                b = json.loads(raw)
            with lock:
                fault = M.msg_faults.pop(0) if M.msg_faults else None
            if fault == "429":
                return self.reply(429, {"message": "You are being rate limited.", "retry_after": 0.6, "global": False}, {"Retry-After": "1", "X-RateLimit-Scope": "user"})
            if fault == "429-html":
                return self.reply(429, raw=b"<html>Access denied</html>", headers={"Retry-After": "1", "Content-Type": "text/html"})
            if fault == "502":
                return self.reply(502, raw=b"<html>Bad gateway</html>")
            if fault == "403":
                return self.reply(403, {"message": "Cannot send messages to this user", "code": 50007})
            if fault == "401":
                return self.reply(401, {"message": "401: Unauthorized", "code": 0})
            err = valid_message(b) if png is None else None
            if err:
                with lock:
                    M.bad.append(("message", err, str(b)[:200]))
                return self.reply(400, {"code": 50035, "message": "Invalid Form Body", "errors": {"content": {"_errors": [{"code": "BASE_TYPE_MAX_LENGTH", "message": err}]}}})
            with lock:
                M.msg_seq += 1
                rec = dict(b, id=str(M.msg_seq), channel=m.group(1))
                if png is not None:
                    M.files.append((b, png))
                else:
                    M.msgs.append(rec)
                lock.notify_all()
            # a message object with the fields the client must be able to ignore
            return self.reply(200, {"id": str(M.msg_seq), "channel_id": m.group(1), "author": {"id": "1", "username": "taskhub_bot", "discriminator": "0", "bot": True}, "content": b.get("content", ""),
                                    "timestamp": "2026-11-03T00:00:00+00:00", "edited_timestamp": None, "attachments": [], "embeds": [], "mentions": [], "mention_roles": [], "tts": False, "mention_everyone": False, "pinned": False, "type": 0},
                              {"X-RateLimit-Remaining": "4", "X-RateLimit-Reset-After": "1.5", "X-RateLimit-Bucket": "abc"})
        self.reply(404, {"message": "404: Not Found", "code": 0})

    def do_PUT(self):
        if not self.strict():
            return
        raw = self.body()
        if not self.authed():
            return
        if self.path == f"/api/v10/applications/{APP}/commands":
            b = json.loads(raw)
            with lock:
                M.commands.append(b)
            return self.reply(200, b)
        self.reply(404, {"message": "404: Not Found", "code": 0})

    def do_PATCH(self):
        if not self.strict():
            return
        raw = self.body()
        b = json.loads(raw)
        m = re.match(r"^/api/v10/webhooks/(\d+)/([\w-]+)/messages/@original$", self.path)
        if m:
            with lock:
                M.webhooks.append({"app": m.group(1), "token": m.group(2), "body": b})
                lock.notify_all()
            return self.reply(200, {"id": "1", "content": b.get("content", "")})
        if not self.authed():
            return
        m = re.match(r"^/api/v10/channels/(\d+)/messages/(\d+)$", self.path)
        if m:
            err = valid_message(b, need_content=False)
            if err:
                with lock:
                    M.bad.append(("edit", err))
                return self.reply(400, {"code": 50035, "message": "Invalid Form Body"})
            with lock:
                M.edits.append(dict(b, id=m.group(2)))
                lock.notify_all()
            return self.reply(200, {"id": m.group(2), "content": b.get("content", "")})
        self.reply(404, {"message": "404: Not Found", "code": 0})


class Srv(ThreadingHTTPServer):
    daemon_threads = True

    def handle_error(self, request, client_address):
        pass   # the hub dropping its connections is part of the tests


srv = Srv(("127.0.0.1", MOCK), H)
threading.Thread(target=srv.serve_forever, daemon=True).start()


def call(method, path, body=None, url=URL, timeout=30):
    r = urllib.request.Request(url + path, method=method, data=None if body is None else json.dumps(body).encode(), headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=timeout) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"{}")
        except ValueError:
            return e.code, {}


def start(port, data, home, extra=None, env=None):
    cfg = {"toasts": False, "autostart": False, "update_check": False, "toast_rate": 0, "toast_task_gap": 0, "language": "en", "log_level": "info",
           "discord_api": f"http://127.0.0.1:{MOCK}/api/v10", "discord_identify_gap": 1}
    cfg.update(extra or {})
    p = os.path.join(data, "config.json")
    if not os.path.exists(p):
        with open(p, "w", encoding="utf-8") as f:
            json.dump(cfg, f)
    e = dict(os.environ, TASKHUB_TEST_HOME=home)
    e.update(env or {})
    for k, v in list(e.items()):
        if v is None:
            del e[k]
    hub = subprocess.Popen([EXE, "--port", str(port), "--data", data, "--no-tray"], env=e)
    for _ in range(80):
        try:
            call("GET", "/api/health", url=f"http://127.0.0.1:{port}", timeout=2)
            break
        except OSError:
            time.sleep(.25)
    return hub


def stop(hub):
    hub.kill()
    hub.wait(10)


def state():
    return call("GET", "/api/discord")[1]


def config(data):
    with open(os.path.join(data, "config.json"), encoding="utf-8") as f:
        return f.read()


def press(cid=None, who=OWNER, chan=DM, type_=3, data=None, component=2, dup_of=None):
    with lock:
        M.press_seq += 1
        pid = dup_of or str(1000000000000000000 + M.press_seq)
    d = {"id": pid, "application_id": APP, "type": type_, "token": f"itok-{pid}", "version": 1, "channel_id": chan, "context": 1, "attachment_size_limit": 26214400, "user": {"id": who, "username": "u", "discriminator": "0"}}
    if type_ == 3:
        d["message"] = {"id": "6", "channel_id": chan}
        d["data"] = dict({"custom_id": cid, "component_type": component}, **(data or {}))
    else:
        d["data"] = data or {}
    M.conns[-1].dispatch("INTERACTION_CREATE", d)
    return pid


def answer(pid, t=6):
    return until(lambda: next((c for c in M.callbacks if c["id"] == pid), None), t)


def buttons(msg):
    return [k for r in msg.get("components") or [] for k in r["components"]]


def by_label(msg, text):
    return next((k for k in buttons(msg) if text in k.get("label", "")), None)


def last_msg(pred, t=8):
    return until(lambda: next((m for m in reversed(M.msgs) if pred(m)), None), t)


data = tempfile.mkdtemp(prefix="taskhub-discord-data-")
home = tempfile.mkdtemp(prefix="taskhub-discord-home-")
no_vault_data = tempfile.mkdtemp(prefix="taskhub-discord-nov-")
try:
    # ---- no Credential Manager: the token is refused, never written to config.json. Also where CI has none.
    hub = start(PORT, no_vault_data, home, env={"TASKHUB_TEST_NO_VAULT": "1"})
    s, r = call("POST", "/api/discord", {"token": TOKEN})
    check("no Credential Manager: the token is refused", s == 400 and not state().get("configured"), (s, r))
    check("...and not in config.json", TOKEN not in config(no_vault_data) and "discord_channel" not in config(no_vault_data))
    stop(hub)

    NO_VAULT = vault.probe()
    if NO_VAULT:
        print(f"SKIP the rest of the Discord tests (Credential Manager unavailable: {NO_VAULT})")
        print("discord tests: " + ("OK" if not fails else f"{fails} FAILED"))
        sys.exit(1 if fails else 0)

    down_data = tempfile.mkdtemp(prefix="taskhub-discord-down-")
    hub = start(PORT, down_data, home, extra={"discord_api": f"http://127.0.0.1:{CLOSED}/api/v10"})
    s, r = call("POST", "/api/discord", {"token": TOKEN})
    check("Discord out of reach on Connect: a plain refusal, nothing kept", s == 400 and not state().get("configured") and not vault.exists(vault.target(down_data, "discord")), (s, r))
    stop(hub)
    shutil.rmtree(down_data, ignore_errors=True)

    hub = start(PORT, data, home)
    check("off until set up", state().get("status") == "off" and not M.conns)

    # ---- the token
    s, r = call("POST", "/api/discord", {"token": "not-a-token"})
    check("a token Discord refuses: an error, nothing kept", s == 400 and not state().get("configured") and not vault.exists(vault.target(data, "discord")), (s, r))
    check("...no connection", not M.conns)
    n401 = M.auth401
    s, r = call("POST", "/api/discord", {"token": "   "})
    check("an empty token isn't sent anywhere", s == 400 and M.auth401 == n401, (s, r))

    # ---- no shared server: the page says what to do, with the link
    s, r = call("POST", "/api/discord", {"token": TOKEN})
    check("token accepted: kept in the Credential Manager", s == 200 and r.get("configured") and vault.exists(vault.target(data, "discord")), r)
    check("not in config.json, not in the answer", TOKEN not in config(data) and TOKEN not in json.dumps(r) and TOKEN not in json.dumps(state()))
    check("no shared server: the advice, not linked", r.get("phase") == "server" and not r.get("linked"), r)
    check("the link adds this bot with no permissions", r.get("invite") == f"https://discord.com/oauth2/authorize?client_id={APP}&scope=bot&permissions=0&integration_type=0", r)
    check("the owner's id is whole (a string)", M.dms and M.dms[-1] == OWNER and f'"{OWNER}"' in config(data), M.dms)
    check("connects to the gateway", until(lambda: M.identifies, 8))
    ident = M.identifies[-1][1]
    check("Identify: the token, a documented intent, no privileged one", ident["token"] == TOKEN and ident["intents"] == 1 and set(ident["properties"]) == {"os", "browser", "device"}, ident)
    check("connected", until(lambda: state().get("status") == "connected"), state())
    check("the required User-Agent on REST and the gateway", all(a.startswith("DiscordBot (https://github.com/FelixMUrphy/TaskHub, ") for a in M.agents), set(M.agents))
    check("a notification before linking goes nowhere", call("POST", "/api/notify", {"title": "x", "message": "y"})[0] == 200 and not until(lambda: M.msgs, 1))

    # ---- the advice follows the reason
    M.dm_fault = 502
    s, r = call("POST", "/api/discord", {"retry": True})
    check("Discord failing: try again, not the advice about a server", s == 200 and r.get("phase") == "retry", (s, r))
    M.dm_fault = 0
    M.mutual = True
    M.msg_faults[:] = ["502"]
    s, r = call("POST", "/api/discord", {"retry": True})
    check("the first message fails: try again", s == 200 and r.get("phase") == "retry", (s, r))
    M.msg_faults[:] = ["403"]
    s, r = call("POST", "/api/discord", {"retry": True})
    check("the first message refused (50007): the advice", s == 200 and r.get("phase") == "server", (s, r))
    M.app_endpoint = "https://example.com/x"
    s, r = call("POST", "/api/discord", {"retry": True})
    check("Check again reads the Endpoint URL again", r.get("endpoint_set") is True, r)
    M.app_endpoint = ""
    s, r = call("POST", "/api/discord", {"retry": True})
    check("...and the warning clears", r.get("endpoint_set") is False and not r.get("warning"), r)

    # ---- the button links it
    s, r = call("POST", "/api/discord", {"retry": True})
    check("Check again: the button goes out", r.get("phase") == "confirm" and M.msgs, r)
    pair = buttons(M.msgs[-1])[0]
    check("it is a direct message with no ping", M.msgs[-1]["channel"] == DM and M.msgs[-1]["allowed_mentions"] == {"parse": []})
    pid = press(pair["custom_id"], who=STRANGER)
    a = answer(pid)
    check("someone else's press links nothing", a and a["body"]["type"] == 4 and a["body"]["data"]["flags"] == 64 and not state().get("linked"), a)
    pid = press("pair:0000", who=OWNER)
    check("a wrong code links nothing", answer(pid) and not state().get("linked"))
    M.expire_presses = True
    pid = press(pair["custom_id"])
    answer(pid)
    time.sleep(.3)
    check("a press that can't be answered any more links nothing", not state().get("linked"), state())
    M.expire_presses = False
    pid = press(pair["custom_id"])
    a = answer(pid)
    check("the owner's press: the message is updated, buttons gone", a and a["body"]["type"] == 7 and a["body"]["data"]["components"] == [], a)
    check("linked", until(lambda: state().get("linked")), state())
    check("the chat's id is whole", f'"{DM}"' in config(data))
    check("the commands are registered, for direct messages", until(lambda: M.commands) and {c["name"] for c in M.commands[-1]} == {"tasks", "mute", "unmute"} and all(c["contexts"] == [1] and 1 <= len(c["description"]) <= 100 for c in M.commands[-1]), M.commands)
    check("no authorization on the interaction's route", all(c["auth"] is None for c in M.callbacks))

    # ---- notifications
    n = len(M.msgs)
    call("POST", "/api/notify", {"title": "@everyone *bold* [x](http://evil.example)", "message": "<@123> `code` -> ok", "level": "warn"})
    m = last_msg(lambda x: "everyone" in x.get("content", ""))
    check("a notification arrives", m, M.msgs[n:])
    if m:
        check("no ping, no preview, the text stays text", m["allowed_mentions"] == {"parse": []} and m["flags"] & 4 and "\\*bold\\*" in m["content"] and "\\[x\\]\\(http\\:" in m["content"] and "\\<@123\\>" in m["content"], m)
    call("POST", "/api/notify", {"title": "T" * 300, "message": "*" * 3000})
    m = last_msg(lambda x: x.get("content", "").count("T") > 200)
    check("the longest notification fits 2000 and has no half escape", m and len(m["content"]) <= 2000, m and len(m["content"]))
    call("POST", "/api/discord", {"test": True})
    check("Send test", last_msg(lambda x: "Test message" in x.get("content", "")))

    # ---- buttons
    s, r = call("POST", "/api/tasks", {"name": "Render", "notify": {"start": True}, "controls": [
        {"id": "go", "label": "Go", "kind": "signal", "toast": True, "style": "primary"},
        {"id": "halt", "label": "Halt", "kind": "signal", "toast": True, "style": "danger", "confirm": "Really stop?"},
        {"id": "pick", "label": "Quality", "kind": "signal", "toast": True, "input": {"type": "select", "options": [f"opt{i}" for i in range(30)]}},
        {"id": "flag", "label": "Upload", "kind": "signal", "toast": True, "input": {"type": "bool"}},
        {"id": "num", "label": "Batch", "kind": "signal", "toast": True, "input": {"type": "number", "min": 1, "max": 9}},
        {"id": "txt", "label": "Name", "kind": "signal", "toast": True, "input": {"type": "text", "label": "New name"}},
    ]})
    tid = r["id"]
    m = last_msg(lambda x: x.get("components") and "Render" in x.get("content", ""))
    check("the task's buttons come with its notification", m, M.msgs[-3:])
    check("nothing the mock refused so far", not M.bad, M.bad)
    if not m:
        raise SystemExit
    sel = next(k for k in buttons(m) if k["type"] == 3)
    check("a list is a real list, at most 25", len(sel["options"]) == 25 and sel["placeholder"] == "Quality", sel)
    check("styles", by_label(m, "Go")["style"] == 1 and by_label(m, "Halt")["style"] == 4)

    def commands(t=3):
        return call("GET", f"/api/tasks/{tid}/commands?wait={t}")[1].get("commands", [])

    go = by_label(m, "Go")["custom_id"]
    pid = press(go, who=STRANGER)
    a = answer(pid)
    check("someone else's press: answered, nothing happens", a and a["body"]["type"] == 4 and not commands(1), a)
    pid = press(go, chan="999")
    a = answer(pid)
    check("a press from another chat: nothing happens", a and a["body"]["type"] == 4 and not commands(1), a)
    M.expire_presses = True
    pid = press(go)
    answer(pid)
    check("no answer in time: no action", not commands(1))
    M.expire_presses = False
    pid = press(go)
    a = answer(pid)
    check("a press: answered first, only the presser sees it", a and a["body"] == {"type": 5, "data": {"flags": 64}}, a)
    got = commands()
    check("...and reaches the task", len(got) == 1 and got[0].get("control") == "go", got)
    w = until(lambda: next((x for x in M.webhooks if x["token"] == f"itok-{pid}"), None))
    check("...and the result replaces the waiting line", w and w["app"] == APP and "go" in w["body"]["content"], M.webhooks)
    n = len(M.callbacks)
    press(go, dup_of=pid)
    time.sleep(.5)
    check("the same event twice: once", len(M.callbacks) == n and not commands(1), M.callbacks[n:])
    check("the interaction's token never as a login", all(c["auth"] is None for c in M.callbacks))

    pid = press(by_label(m, "Halt")["custom_id"])
    a = answer(pid)
    check("a confirm control asks first", a and a["body"]["type"] == 4 and a["body"]["data"]["flags"] == 64 and "Really stop" in a["body"]["data"]["content"] and not commands(1), a)
    yes = a["body"]["data"]["components"][0]["components"][0]
    pid = press(yes["custom_id"])
    answer(pid)
    got = commands()
    check("...Yes does it", len(got) == 1, got)

    pid = press(sel["custom_id"], component=3, data={"values": ["o7"]})
    answer(pid)
    got = commands()
    check("a list's pick arrives as its text", len(got) == 1 and got[0].get("value") == "opt7", got)
    pid = press(sel["custom_id"], component=3, data={"values": ["o99"]})
    a = answer(pid)
    check("a pick that isn't there: nothing happens", a and a["body"]["type"] == 4 and not commands(1), a)
    s, r = call("POST", "/api/tasks", {"name": "Lists", "notify": {"start": True}, "controls": [{"id": "l", "label": "Pick", "kind": "signal", "toast": True, "input": {"type": "select", "options": ["old0", "old1"]}}]})
    lt = r["id"]
    lm = last_msg(lambda x: "Lists" in x.get("content", "") and x.get("components"))
    n = len(M.msgs)
    call("POST", f"/api/tasks/{lt}/update", {"controls": [{"id": "l", "label": "Pick", "kind": "signal", "toast": True, "input": {"type": "select", "options": ["new0", "new1"]}}], "toast": "Lists again"})
    lm2 = last_msg(lambda x: "Lists again" in x.get("content", "") and x.get("components"))
    lsel = buttons(lm2)[0] if lm2 else None
    check("a list that changed: the new options are shown", lsel and lsel["options"][0]["label"] == "new0", lm2)
    pid = press(lsel["custom_id"], component=3, data={"values": ["o0"]})
    answer(pid)
    got = call("GET", f"/api/tasks/{lt}/commands?wait=3")[1].get("commands", [])
    check("...and a pick gives what the list shows", len(got) == 1 and got[0].get("value") == "new0", got)
    pid = press(buttons(lm)[0]["custom_id"], component=3, data={"values": ["o1"]})
    answer(pid)
    got = call("GET", f"/api/tasks/{lt}/commands?wait=3")[1].get("commands", [])
    check("...a pick in the older message's list isn't in the list any more: nothing happens", not got, got)
    call("POST", f"/api/tasks/{lt}/finish", {"state": "cancelled", "notify": False})

    pid = press(by_label(m, "Upload ✗")["custom_id"])
    answer(pid)
    got = commands()
    check("bool: two buttons, the value arrives", len(got) == 1 and got[0].get("value") is False, got)

    pid = press(by_label(m, "Batch")["custom_id"])
    a = answer(pid)
    check("a number: Discord's input window, as the first answer", a and a["body"]["type"] == 9 and a["body"]["data"]["components"][0]["type"] == 18, a)
    check("the mock accepted its shape", not M.bad, M.bad)
    mid = a["body"]["data"]["custom_id"]

    def submit(cid, value):
        return press(type_=5, data={"custom_id": cid, "components": [{"type": 18, "id": 1, "component": {"type": 4, "id": 2, "custom_id": "v", "value": value}}]})

    pid = submit(mid, "abc")
    a = answer(pid)
    check("not a number: said at once, nothing happens", a and a["body"]["type"] == 4 and "abc" in a["body"]["data"]["content"] and not commands(1), a)
    pid = submit(mid, "99")
    answer(pid)
    check("out of range: nothing happens", not commands(1))
    pid = submit(mid, "3,5")
    a = answer(pid)
    got = commands()
    check("a number: arrives", a and a["body"]["type"] == 5 and len(got) == 1 and got[0].get("value") == 3.5, (a, got))
    pid = press(by_label(m, "Name")["custom_id"])
    a = answer(pid)
    check("text: the field's own label", a and a["body"]["data"]["components"][0]["label"] == "New name", a)
    pid = submit(a["body"]["data"]["custom_id"], "hello **x**")
    answer(pid)
    got = commands()
    check("text: arrives unchanged", len(got) == 1 and got[0].get("value") == "hello **x**", got)
    pid = press("k999999")
    a = answer(pid)
    check("an old button: answered", a and a["body"]["type"] == 4, a)

    # a task with more controls than fit: 5 rows, the rest stays in the panel
    s, r = call("POST", "/api/tasks", {"name": "Many", "notify": {"start": True}, "controls": [{"id": f"c{i}", "label": f"C{i}", "kind": "signal", "toast": True} for i in range(40)]
                                       + [{"id": "s", "label": "S", "kind": "signal", "toast": True, "input": {"type": "select", "options": ["a"]}}]})
    m2 = last_msg(lambda x: "Many" in x.get("content", "") and x.get("components"))
    check("more controls than fit: 5 rows and accepted", m2 and len(m2["components"]) == 5 and len(buttons(m2)) == 25, m2 and len(buttons(m2)))
    check("nothing the mock refused", not M.bad, M.bad)
    call("POST", f"/api/tasks/{r['id']}/finish", {"state": "cancelled", "notify": False})

    # ---- slash commands
    pid = press(type_=2, data={"id": "1", "name": "tasks", "type": 1})
    a = answer(pid)
    check("/tasks lists them", a and a["body"]["type"] == 4 and "Render" in a["body"]["data"]["content"], a)
    pid = press(type_=2, data={"id": "1", "name": "tasks", "type": 1}, who=STRANGER)
    a = answer(pid)
    check("/tasks from someone else: no list", a and "Render" not in a["body"]["data"]["content"], a)
    pid = press(type_=2, data={"id": "2", "name": "mute", "type": 1})
    answer(pid)
    check("/mute", until(lambda: call("GET", "/api/tasks")[1].get("muted") is True, 3), call("GET", "/api/tasks")[1].get("muted"))
    pid = press(type_=2, data={"id": "3", "name": "unmute", "type": 1})
    answer(pid)
    check("/unmute", until(lambda: call("GET", "/api/tasks")[1].get("muted") is False, 3))

    # ---- progress: one silent message, edited; buttons gone at the end; the chart once
    s, r = call("POST", "/api/tasks", {"name": "Progress task", "total": 10, "unit": "frames", "widgets": [{"id": "w", "type": "chart", "title": "Loss"}]})
    pt = r["id"]
    call("POST", f"/api/tasks/{pt}/update", {"current": 2, "status": "step *2*", "widgets": {"w": 1.0}})
    pm = last_msg(lambda x: "Progress task" in x.get("content", "") and "█" in x.get("content", "") + "█" and x.get("flags", 0) & 4096, 10)
    check("one silent message", pm, M.msgs[-2:])
    time.sleep(5.5)
    call("POST", f"/api/tasks/{pt}/update", {"current": 7, "status": "step 7", "widgets": {"w": 2.0}})
    e = until(lambda: next((x for x in M.edits if pm and x["id"] == pm["id"] and "step 7" in x.get("content", "")), None), 10)
    check("it is edited in place, flags kept", e and e["flags"] & 4096 and e["flags"] & 4, e)
    call("POST", f"/api/tasks/{pt}/finish", {"state": "done", "message": "ok"})
    e = until(lambda: next((x for x in M.edits if pm and x["id"] == pm["id"] and "✅" in x.get("content", "")), None), 10)
    check("the final state, with no buttons", e and e["components"] == [], e)
    f = until(lambda: M.files, 8)
    check("the chart once: a real upload", f and f[0][1].startswith(b"\x89PNG") and f[0][0]["attachments"][0]["filename"] == "chart.png" and f[0][0]["allowed_mentions"] == {"parse": []}, f and f[0][0])
    check("nothing the mock refused", not M.bad, M.bad)

    # ---- limits and failures of sending
    for fault in ("429", "429-html", "502"):
        M.msg_faults[:] = [fault]
        n = len(M.msgs)
        t0 = time.time()
        call("POST", "/api/notify", {"title": "after " + fault, "message": "m"})
        m = last_msg(lambda x: ("after " + fault.replace("-", "\\-")) in x.get("content", ""), 12)
        check(f"{fault}: delivered, once", m and sum(("after " + fault.replace("-", "\\-")) in x.get("content", "") for x in M.msgs[n:]) == 1, M.msgs[n:])
        if fault.startswith("429"):
            check(f"{fault}: waited as asked", time.time() - t0 >= .9, time.time() - t0)
    M.msg_faults[:] = ["403"]
    call("POST", "/api/notify", {"title": "refused", "message": "m"})
    check("the chat refuses: the page says so", until(lambda: state().get("cannot_write")), state())
    n = len(M.msgs)
    call("POST", "/api/notify", {"title": "not sent", "message": "m"})
    check("...nothing more is tried", not until(lambda: len(M.msgs) > n, 1.5))
    call("POST", "/api/discord", {"test": True})
    check("...until the user acts", last_msg(lambda x: "Test message" in x.get("content", "")) and not state().get("cannot_write"), state())
    M.msg_faults[:] = ["403"]
    s, r = call("POST", "/api/discord", {"test": True})
    check("Send test refused: an error, not 'sent'", s == 400 and state().get("cannot_write"), (s, r))
    M.msg_faults[:] = ["502"]
    s, r = call("POST", "/api/discord", {"test": True})
    check("Send test failing: an error, and the chat isn't marked", s == 400 and not state().get("cannot_write"), (s, r))
    s, r = call("POST", "/api/discord", {"test": True})
    check("Send test through: OK", s == 200 and not state().get("cannot_write"), (s, r))

    # ---- the gateway
    def identifies():
        return len(M.identifies)

    c = M.conns[-1]
    n_conns, n_ids, n_res = len(M.conns), identifies(), len(M.resumes)
    c.send({"op": 1, "d": None})
    check("a beat on request", until(lambda: any(x.get("op") == 1 for x in c.events), 3), c.events[-3:])
    c.send({"op": 7, "d": None})
    check("op 7: resumes on the address READY gave, nothing new", until(lambda: len(M.resumes) > n_res, 8) and identifies() == n_ids and M.conns[-1].path.startswith("/resume"), [g.path for g in M.conns])
    res = M.resumes[-1]
    check("...with the session and the last sequence", res["token"] == TOKEN and res["session_id"].startswith("sess") and isinstance(res["seq"], int) and res["seq"] > 0, res)
    check("...and presses still work", answer(press(go)) is not None)
    commands(1)
    until(lambda: len(M.webhooks) > 0, 1)
    n_res = len(M.resumes)
    M.conns[-1].drop()
    check("a line that drops: resumes", until(lambda: len(M.resumes) > n_res, 12) and identifies() == n_ids, (len(M.resumes), identifies()))
    n_res = len(M.resumes)
    M.conns[-1].close(4000)
    check("close 4000: resumes", until(lambda: len(M.resumes) > n_res, 12) and identifies() == n_ids)

    t0 = time.time()
    M.conns[-1].send({"op": 9, "d": False})
    check("op 9 false: a new session", until(lambda: identifies() > n_ids, 15), identifies())
    n_ids = identifies()
    M.conns[-1].close(4009)
    check("close 4009: a new session", until(lambda: identifies() > n_ids, 15))
    gaps = [b[0] - a[0] for a, b in zip(M.identifies, M.identifies[1:])]
    check("new sessions keep their distance", gaps and min(gaps[-2:]) >= .95, gaps)
    n_ids = identifies()

    # a resume address that isn't Discord's: no token goes there, a new session instead
    M.resume_url = "wss://evil.example/"
    M.conns[-1].close(4009)
    check("(new session with a foreign resume address)", until(lambda: identifies() > n_ids, 15))
    n_ids, n_res = identifies(), len(M.resumes)
    M.resume_url = None
    M.conns[-1].close(4000)
    check("a foreign resume address isn't used: a new session on the usual one", until(lambda: identifies() > n_ids, 15) and len(M.resumes) == n_res and not M.conns[-1].path.startswith("/resume"), M.conns[-1].path)
    n_ids = identifies()

    # the daily limit on new sessions: holds, shows it, nothing sent
    M.remaining = 50
    M.conns[-1].close(4009)
    check("near the daily limit: on hold", until(lambda: state().get("status") == "on hold", 8), state())
    check("...no new session", identifies() == n_ids)
    M.remaining = 900
    check("...goes on after the reset", until(lambda: identifies() > n_ids, 15))
    n_ids = identifies()

    # a button from before a restart acts on nothing made after it
    before = {k["custom_id"] for x in M.msgs for k in buttons(x)}
    stop(hub)
    hub = start(PORT, data, home)
    check("(connected after the restart)", until(lambda: state().get("status") == "connected", 15), state())
    s, r = call("POST", "/api/tasks", {"name": "After restart", "notify": {"start": True}, "controls": [{"id": "other", "label": "Other", "kind": "signal", "toast": True}]})
    ar = r["id"]
    m3 = last_msg(lambda x: "After restart" in x.get("content", "") and x.get("components"))
    check("a button after a restart has an id never used before", m3 and buttons(m3)[0]["custom_id"] not in before, m3 and buttons(m3)[0])
    pid = press(go)
    a = answer(pid)
    got = call("GET", f"/api/tasks/{ar}/commands?wait=2")[1].get("commands", [])
    check("an old button after a restart: expired, the new task isn't touched", a and a["body"]["type"] == 4 and not got, (a, got))
    pid = press(buttons(m3)[0]["custom_id"]) if m3 else None
    answer(pid)
    got = call("GET", f"/api/tasks/{ar}/commands?wait=3")[1].get("commands", [])
    check("...its own button works", len(got) == 1 and got[0].get("control") == "other", got)
    call("POST", f"/api/tasks/{ar}/finish", {"state": "cancelled", "notify": False})
    n_ids = identifies()

    # a heartbeat that isn't answered: the connection is dead, resumes
    hub_conns = len(M.conns)
    stop(hub)
    M.interval, M.ack = 1000, False
    hub = start(PORT, data, home)
    check("after a restart: still linked, no new button", until(lambda: state().get("linked"), 5) and until(lambda: len(M.conns) > hub_conns, 5), state())
    check("the commands aren't sent again", len(M.commands) == 1, len(M.commands))
    n_res = len(M.resumes)
    check("no answer to the heartbeat: a new connection", until(lambda: len(M.conns) > hub_conns + 1, 12), len(M.conns))
    M.interval, M.ack = 45000, True
    until(lambda: state().get("status") == "connected", 15)

    # the endpoint set in the portal: said on the page
    M.app_endpoint = "https://example.com/x"
    stop(hub)
    hub = start(PORT, data, home)
    check("an Interactions Endpoint URL: a warning", until(lambda: state().get("endpoint_set") and "Endpoint" in (state().get("warning") or ""), 8), state())
    M.app_endpoint = ""
    call("POST", "/api/discord", {"test": True})
    check("...Send test reads it again and the warning clears", state().get("endpoint_set") is False and not state().get("warning"), state())

    # 4014 and 4004: don't reconnect
    until(lambda: state().get("status") == "connected", 15)
    n = len(M.conns)
    M.conns[-1].close(4014)
    check("close 4014: stops, and says so", until(lambda: state().get("status") == "closed 4014", 8) and not until(lambda: len(M.conns) > n, 3), state())
    stop(hub)
    hub = start(PORT, data, home)
    until(lambda: state().get("status") == "connected", 15)
    n = len(M.conns)
    M.conns[-1].close(4004)
    check("close 4004: the token is bad, stops", until(lambda: state().get("status") == "invalid token", 8) and not until(lambda: len(M.conns) > n, 3), state())
    check("...the warning says so (the settings tab shows it)", "token" in (state().get("warning") or "").lower(), state())
    # 401 on sending: stops asking
    stop(hub)
    hub = start(PORT, data, home)
    until(lambda: state().get("status") == "connected", 15)
    M.msg_faults[:] = ["401"]
    call("POST", "/api/notify", {"title": "bad", "message": "m"})
    check("401 on a message: says the token is bad", until(lambda: state().get("status") == "invalid token", 8), state())
    n = len(M.msgs)
    call("POST", "/api/notify", {"title": "no more", "message": "m"})
    check("...and stops asking", not until(lambda: len(M.msgs) > n, 1.5))
    n401 = M.auth401
    s, r = call("POST", "/api/discord", {"test": True})
    check("Send test with a dead token: an error, no new 401", s == 400 and M.auth401 == n401, (s, r, M.auth401 - n401))

    # a gateway address that isn't Discord's: refused before connecting
    stop(hub)
    M.gateway_url = "wss://evil.example/"
    n = len(M.conns)
    hub = start(PORT, data, home)
    check("a foreign gateway address: no connection", until(lambda: state().get("status") == "offline", 8) and len(M.conns) == n, state())
    M.gateway_url = None

    # a team's application: its owner is the one
    stop(hub)
    M.owner_in_team = True
    hub = start(PORT, data, home)
    call("POST", "/api/discord", {"token": OTHER_TOKEN})
    check("a team's application: the team's owner", M.dms[-1] == OWNER, M.dms[-1])
    M.owner_in_team = False

    # waiting for the button, then a restart: the code is gone, the page says to ask again
    stop(hub)
    hub = start(PORT, data, home)
    r = state()
    check("a restart while waiting: Check again, not the advice about a server", r.get("configured") and not r.get("linked") and r.get("phase") == "again", r)
    # Discord out of reach on Check again
    stop(hub)
    cfg = json.loads(config(data))
    good_api = cfg["discord_api"]
    cfg["discord_api"] = f"http://127.0.0.1:{CLOSED}/api/v10"
    with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
        json.dump(cfg, f)
    hub = start(PORT, data, home)
    s, r = call("POST", "/api/discord", {"retry": True})
    check("Discord out of reach on Check again: try again", s == 200 and r.get("phase") == "retry", (s, r))
    stop(hub)
    cfg = json.loads(config(data))
    cfg["discord_api"] = good_api
    with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
        json.dump(cfg, f)
    hub = start(PORT, data, home)

    # the log
    until(lambda: state().get("status") == "connected", 10)
    logs = ""
    for name in os.listdir(data):
        if name.startswith("taskhub") and name.endswith(".log") or ".log." in name:
            with open(os.path.join(data, name), encoding="utf-8", errors="replace") as f:
                logs += f.read()
    check("a log exists to check", "discord" in logs)
    check("no token in the log", TOKEN not in logs and OTHER_TOKEN not in logs and "GabcDe" not in logs)
    check("no interaction's token or address in the log", "itok-" not in logs and "/interactions/" not in logs and "/webhooks/" not in logs)
    check("no message text in the log", "everyone" not in logs and "Render" not in logs)

    # ---- disconnect
    call("POST", "/api/discord", {"disconnect": True})
    st = state()
    check("disconnect: the token is gone", not st.get("configured") and st.get("status") == "off" and not vault.exists(vault.target(data, "discord")), st)
    check("...and the ids", OWNER not in config(data) and DM not in config(data) and APP not in config(data))
    check("...and the connection", until(lambda: not M.conns[-1].alive or getattr(M.conns[-1], "close_code", None) == 1000, 5))
    n = len(M.msgs)
    call("POST", "/api/notify", {"title": "after disconnect", "message": "m"})
    check("...nothing is sent", not until(lambda: len(M.msgs) > n, 1.5))
    stop(hub)

    # setting it up over HTTP is only for the tests' hubs: an ordinary hub has no such route
    plain = tempfile.mkdtemp(prefix="taskhub-discord-plain-")
    hub = start(PORT, plain, home, env={"TASKHUB_TEST_HOME": None})
    s, r = call("POST", "/api/discord", {"token": TOKEN})
    check("no POST /api/discord on an ordinary hub", s in (404, 405), (s, r))
    s, r = call("GET", "/api/discord")
    check("GET says only the state", s == 200 and r.get("configured") is False, (s, r))
    stop(hub)
    shutil.rmtree(plain, ignore_errors=True)
    hub = None
finally:
    try:
        if hub:
            hub.kill()
            hub.wait(10)
    except Exception:
        pass
    srv.shutdown()
    try:
        import ctypes
        ctypes.windll.advapi32.CredDeleteW(vault.target(data, "discord"), 1, 0)
    except Exception:
        pass
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(home, ignore_errors=True)
    shutil.rmtree(no_vault_data, ignore_errors=True)

print("discord tests: " + ("OK" if not fails else f"{fails} FAILED"), flush=True)
sys.exit(1 if fails else 0)
