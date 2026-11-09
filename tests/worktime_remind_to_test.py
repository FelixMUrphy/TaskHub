"""Work hours reminders: where they go (remind_to). A Telegram stand-in is linked on an own test hub (47836, mock 47835, temp data); Discord isn't
linked. A reminder away from the PC goes to Telegram for "telegram" and "all", nowhere but a toast for "none", and as a toast for "discord" because
Discord isn't linked. The older remind_messengers switch is read and written as remind_to. Argument: TaskHubHost.exe. Nothing leaves this PC."""
import datetime, json, os, re, shutil, sqlite3, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

os.environ.pop("TASKHUB_TOKEN", None)
EXE = sys.argv[1]
PORT, MOCK = 47836, 47835
URL = f"http://127.0.0.1:{PORT}"
TOKEN = "worktime-remind-to-test-token"
BOT = "123456789:" + "R" * 35
CHAT = 5101
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
sent, updates = [], []
tg = threading.Condition()
SHIFT = 15 - datetime.date.today().day
TODAY = datetime.date.today() + datetime.timedelta(days=SHIFT)


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
        try:
            self.wfile.write(data)
        except OSError:
            pass   # the hub went away mid-poll (the end of the test)

    def do_POST(self):
        m = re.match(r"^/bot([^/]+)/(\w+)$", self.path)
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        body = {} if "multipart" in (self.headers.get("Content-Type") or "") else json.loads(raw or b"{}")
        method = m.group(2) if m else None
        if method == "getMe":
            return self.reply({"ok": True, "result": {"id": 1, "is_bot": True, "username": "remind_to_test_bot"}})
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


def call(method, path, body=None):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(),
                               headers={"X-TaskHub": "1", "Content-Type": "application/json", "Authorization": "Bearer " + TOKEN})
    try:
        with op.open(r, timeout=30) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"{}")
        except ValueError:
            return e.code, {}


def until(pred, seconds=15):
    end = time.time() + seconds
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(.1)
    return None


NO_VAULT = vault.probe()
if NO_VAULT:
    print(f"SKIP reminder destinations: Credential Manager unavailable here ({NO_VAULT})")
    raise SystemExit(0)

tmp = tempfile.mkdtemp(prefix="taskhub-wtremindto-")
data, home = os.path.join(tmp, "data"), os.path.join(tmp, "home")
os.makedirs(data)
os.makedirs(home)
DB = os.path.join(data, "worktime.db")
with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"token": TOKEN, "toasts": False, "autostart": False, "update_check": False, "language": "en", "toast_rate": 0, "toast_task_gap": 0,
               "telegram_api": f"http://127.0.0.1:{MOCK}"}, f)
ThreadingHTTPServer.request_queue_size = 64
server = ThreadingHTTPServer(("127.0.0.1", MOCK), Bot)
threading.Thread(target=server.serve_forever, daemon=True).start()
env = dict(os.environ, TASKHUB_NO_AUTOSTART="1", TASKHUB_TEST_HOME=home, TASKHUB_TEST_WORKTIME_SHIFT_DAYS=str(SHIFT))
hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=env)
page = lambda b: call("POST", "/api/test/worktime-page", b)[1]
tick = lambda offset: call("POST", "/api/test/worktime-remind", {"offset": offset, "idle": 4000, "locked": True})[1]
log = lambda: open(os.path.join(data, "taskhub.log"), encoding="utf-8", errors="replace").read() if os.path.exists(os.path.join(data, "taskhub.log")) else ""
try:
    for _ in range(60):
        try:
            op.open(URL + "/api/health", timeout=1)
            break
        except OSError:
            time.sleep(.5)
    # Telegram linked to the stand-in's chat
    s, r = call("POST", "/api/telegram", {"token": BOT})
    check("(Telegram paired with the stand-in)", s == 200 and "start=" in (r.get("link") or ""), (s, r))
    with tg:
        updates.append({"update_id": 1001, "message": {"message_id": 1, "chat": {"id": CHAT, "type": "private"}, "from": {"id": CHAT},
                                                        "text": "/start " + r["link"].rsplit("start=", 1)[-1]}})
        tg.notify_all()
    check("(Telegram linked)", until(lambda: call("GET", "/api/telegram")[1].get("linked")), call("GET", "/api/telegram")[1])

    call("POST", "/api/worktime/setup", {"start_date": TODAY.replace(day=1).isoformat(), "settings": {"work_days": [1, 2, 3, 4, 5, 6, 7], "salary": 3000}})
    st = page({})["settings"]
    check("default: every linked messenger, the older switch on", st.get("remind_to") == "all" and st.get("remind_messengers") is True, st)
    check("an unknown destination refused", page({"worktime_settings": {"remind_to": "slack"}}).get("error"))
    st = page({"worktime_settings": {"remind_messengers": False}})["settings"]
    check("the older switch off: none", st.get("remind_to") == "none" and st.get("remind_messengers") is False, st)
    st = page({"worktime_settings": {"remind_to": "discord"}})["settings"]
    check("a messenger chosen: the older switch on", st.get("remind_to") == "discord" and st.get("remind_messengers") is True, st)
    # both keys sent back together, as a client that read them all does: the one changed counts
    st = page({"worktime_settings": {"remind_to": "discord", "remind_messengers": False}})["settings"]
    check("both sent, only the older switch changed: it counts", st.get("remind_to") == "none" and st.get("remind_messengers") is False, st)
    st = page({"worktime_settings": {"remind_to": "telegram", "remind_messengers": False}})["settings"]
    check("both sent, remind_to changed: it counts", st.get("remind_to") == "telegram" and st.get("remind_messengers") is True, st)
    st = page({"worktime_settings": {"remind_messengers": True}})["settings"]
    check("the older switch on while a messenger is chosen: the choice stays", st.get("remind_to") == "telegram", st)
    # settings saved before remind_to: read from the older switch
    con = sqlite3.connect(DB)
    (vf, js), = con.execute("SELECT valid_from, json FROM settings_history ORDER BY valid_from DESC LIMIT 1").fetchall()
    old = {k: v for k, v in json.loads(js).items() if k != "remind_to"}
    old["remind_messengers"] = False
    con.execute("UPDATE settings_history SET json=? WHERE valid_from=?", (json.dumps(old), vf))
    con.commit()
    con.close()
    check("saved before it existed, the switch off: read as none", page({})["settings"].get("remind_to") == "none", page({})["settings"])

    # a session paused long, the user away: each destination in turn (the repeat time apart, so each tick sends)
    page({"worktime_settings": {"remind_start": None, "remind_open_after": None, "remind_paused_hours": 1, "remind_repeat_minutes": 5}})
    call("POST", "/api/worktime/start", {})
    call("POST", "/api/worktime/pause", {})
    at = 2 * 3600
    for to, telegram, why in (("none", False, "none: only a toast"), ("telegram", True, "Telegram: there"), ("discord", False, "Discord, not linked: a toast instead"),
                              ("all", True, "every linked one: Telegram")):
        page({"worktime_settings": {"remind_to": to}})
        n, toasts = len(sent), log().count("(toast only)")
        at += 600
        r = tick(at)
        check(f"{to}: a reminder sent, marked for {to}", r.get("sent") and r["sent"][0]["to"] == to and r["sent"][0]["at_pc"] is False, r)
        if telegram:
            got = until(lambda: [m for m in sent[n:] if m.get("chat_id") == CHAT])
            check(f"...{why}", got and "reply_markup" in got[0], sent[n:])
        else:
            check(f"...{why}", until(lambda: log().count("(toast only)") > toasts), log()[-1500:])
            time.sleep(1)
            check(f"...{to}: nothing in Telegram", not [m for m in sent[n:] if m.get("chat_id") == CHAT], sent[n:])
finally:
    try:
        call("POST", "/api/telegram", {"disconnect": True})
    except OSError:
        pass
    hub.kill()
    hub.wait()
    server.shutdown()
    time.sleep(.5)
    shutil.rmtree(tmp, ignore_errors=True)
print("worktime reminder destinations: " + ("ALL PASSED" if fails == 0 else f"{fails} FAILED"))
sys.exit(1 if fails else 0)
