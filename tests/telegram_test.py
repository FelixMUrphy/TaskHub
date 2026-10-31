"""Telegram bridge against a mock Bot API (127.0.0.1:47897; the test hub's config.json points telegram_api there)."""
import ctypes, hashlib, json, os, re, subprocess, sys, tempfile, threading, time, urllib.request, urllib.error
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
DATA = os.environ.get("TASKHUB_DATA", "")
EXE = sys.argv[1] if len(sys.argv) > 1 else None
TOKEN = "123456789:" + "A" * 35
fails = 0
sent, answers, updates, edits, photos = [], [], [], [], []
rejected, limited = [], []      # 400s the mock answered; times of 429 answers
faults = {"429": 0}             # next N sendMessage calls answer 429 (retry_after 1 s)
lock = threading.Condition()
ENTITY = re.compile(r"&(?!(lt|gt|amp|quot|#\d+|#x[0-9a-fA-F]+);)")


def html_error(text):
    """What Telegram would refuse in an HTML message: unknown tags, a bare & / cut entity, > 4096 after parsing, bad UTF-16."""
    try:
        text.encode("utf-8")
    except UnicodeEncodeError:
        return "Bad Request: strings must be encoded in UTF-8"
    if re.search(r"<(?!/?(b|i|u|s|code|pre|a)[ >])", text) or ENTITY.search(text) or re.search(r"<[^>]*$", text):
        return "Bad Request: can't parse entities"
    if "FORCE_PARSE_ERR" in text:
        return "Bad Request: can't parse entities: forced by the test"
    plain = re.sub(r"<[^>]*>", "", text).replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", '"').replace("&amp;", "&")
    if len(plain) > 4096:
        return "Bad Request: message is too long"
    return None
uid = [100]


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


class Bot(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_POST(self):
        m = re.match(r"^/bot([^/]+)/(\w+)$", self.path)
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        body = {"_multipart": raw} if "multipart" in (self.headers.get("Content-Type") or "") else json.loads(raw or b"{}")
        if not m or m.group(1) != TOKEN:
            return self.reply(401, {"ok": False, "description": "Unauthorized"})
        method = m.group(2)
        if method == "getMe":
            return self.reply(200, {"ok": True, "result": {"id": 1, "is_bot": True, "username": "test_bot"}})
        if method == "getUpdates":
            off = body.get("offset", 0)
            with lock:
                if off == -1:
                    return self.reply(200, {"ok": True, "result": []})
                lock.wait_for(lambda: any(u["update_id"] >= off for u in updates), timeout=min(body.get("timeout", 0), 2))
                res = [u for u in updates if u["update_id"] >= off]
            return self.reply(200, {"ok": True, "result": res})
        if method == "sendMessage":
            with lock:
                if faults["429"] > 0:
                    faults["429"] -= 1
                    limited.append(time.time())
                    return self.reply(429, {"ok": False, "error_code": 429, "description": "Too Many Requests: retry after 1",
                                            "parameters": {"retry_after": 1}})
        if method in ("sendMessage", "editMessageText"):
            text = body.get("text", "")
            err = html_error(text) if body.get("parse_mode") == "HTML" else (None if len(text) <= 4096 else "Bad Request: message is too long")
            if err:
                with lock:
                    rejected.append((err, text[:80]))
                return self.reply(400, {"ok": False, "error_code": 400, "description": err})
        if method == "sendMessage":
            with lock:
                sent.append(dict(body, _t=time.time()))
                lock.notify_all()
            return self.reply(200, {"ok": True, "result": {"message_id": len(sent)}})
        if method == "editMessageText":
            with lock:
                edits.append(body)
                lock.notify_all()
            return self.reply(200, {"ok": True, "result": True})
        if method == "sendPhoto":
            with lock:
                photos.append(body.get("_multipart", b""))
                lock.notify_all()
            return self.reply(200, {"ok": True, "result": {"message_id": 999}})
        if method == "answerCallbackQuery":
            with lock:
                answers.append(body)
                lock.notify_all()
            return self.reply(200, {"ok": True, "result": True})
        self.reply(404, {"ok": False})

    def reply(self, code, obj):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def push(update):
    with lock:
        uid[0] += 1
        update["update_id"] = uid[0]
        updates.append(update)
        lock.notify_all()


def msg(chat, text):
    push({"message": {"message_id": 1, "chat": {"id": chat, "type": "private"}, "from": {"id": chat}, "text": text}})


def wait_sent(pred, timeout=8):
    end = time.time() + timeout
    with lock:
        while time.time() < end:
            for s in sent:
                if pred(s):
                    return s
            lock.wait(0.2)
    return None


op = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def cred_target(data):
    """The hub's Credential Manager name for the bot token of a hub with this data folder (Secrets.Target)."""
    tag = hashlib.sha256(os.path.abspath(data).rstrip("\\").lower().encode()).hexdigest().upper()[:10]
    return "TaskHub/telegram@" + tag


def cred_exists(target):
    adv = ctypes.WinDLL("advapi32")
    p = ctypes.c_void_p()
    ok = adv.CredReadW(ctypes.c_wchar_p(target), 1, 0, ctypes.byref(p))
    if ok:
        adv.CredFree(p)
    return bool(ok)


def config(data):
    with open(os.path.join(data, "config.json"), encoding="utf-8") as f:
        return json.load(f)


def call(method, path, body=None, url=None):
    req = urllib.request.Request((url or URL) + path, json.dumps(body).encode() if body is not None else None, method=method,
                                 headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    try:
        with op.open(req, timeout=20) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


ThreadingHTTPServer.request_queue_size = 64   # the hub opens several connections at once (default 5 -> refused on Windows)
srv = ThreadingHTTPServer(("127.0.0.1", 47897), Bot)
threading.Thread(target=srv.serve_forever, daemon=True).start()

s, r = call("POST", "/api/telegram", {"token": "not a token"})
check("bad token format -> 400", s == 400, (s, r))
s, r = call("POST", "/api/telegram", {"token": TOKEN})
code = (r.get("link") or "").rsplit("start=", 1)[-1]
check("pair -> t.me link with code", s == 200 and r.get("link", "").startswith("https://t.me/test_bot?start=") and len(code) >= 8, (s, r))
check("token never returned", TOKEN not in json.dumps(r), r)

msg(777, "/start wrongcode")
msg(555, "/start " + code)
hello = wait_sent(lambda m: m["chat_id"] == 555)
check("linked: welcome message to the chat", hello is not None, sent)
time.sleep(0.5)
s, r = call("GET", "/api/telegram")
check("state linked", r.get("linked") is True and r.get("status") == "connected", r)
check("nothing sent to the wrong chat", not any(m["chat_id"] == 777 for m in sent), sent)

call("POST", "/api/notify", {"title": "Build <ok>", "message": "a & b"})
m = wait_sent(lambda m: "Build &lt;ok&gt;" in m["text"])
check("notification mirrored, HTML-escaped", m is not None and "a &amp; b" in m["text"] and m["parse_mode"] == "HTML", sent[-1:])

s, r = call("POST", "/api/tasks", {"name": "TG task", "notify": {"start": True},
                                   "controls": [{"id": "go", "label": "Go", "kind": "signal", "toast": True},
                                                {"id": "mode", "label": "Mode", "kind": "signal", "toast": True,
                                                 "input": {"type": "select", "options": ["fast", "slow"]}}]})
tid = r.get("id")
m = wait_sent(lambda m: "TG task" in m["text"] and "reply_markup" in m)
kb = m["reply_markup"]["inline_keyboard"] if m else []
labels = [b["text"] for row in kb for b in row]
check("task toast -> inline buttons (signal + select options)", labels == ["Go", "Mode: fast", "Mode: slow"], labels)
if m:
    data = {b["text"]: b["callback_data"] for row in kb for b in row}
    push({"callback_query": {"id": "cb1", "from": {"id": 555}, "message": {"message_id": 9, "chat": {"id": 555}}, "data": data["Mode: slow"]}})
    end = time.time() + 8
    while time.time() < end and not answers:
        time.sleep(0.2)
    s, r = call("GET", f"/api/tasks/{tid}/commands?wait=3")
    cmds = r.get("commands", [])
    check("button press -> control triggered with value", any(c["control"] == "mode" and c.get("value") == "slow" for c in cmds), r)
    check("callback answered", answers and answers[0]["callback_query_id"] == "cb1", answers)

n0 = len(sent)
msg(777, "/tasks")
msg(555, "/tasks")
m = wait_sent(lambda m: m["chat_id"] == 555 and "TG task" in m["text"] and "reply_markup" not in m)
check("/tasks from the linked chat lists tasks", m is not None, sent[n0:])
check("/tasks from another chat ignored", not any(x["chat_id"] == 777 for x in sent), sent)

call("DELETE", f"/api/tasks/{tid}?force=1")

# no double icon: a failure title already starts with one
call("POST", "/api/notify", {"title": "❌ Boom", "message": "x", "level": "error"})
m = wait_sent(lambda m: "Boom" in m["text"])
check("error title with its own icon -> one icon", m is not None and m["text"].startswith("<b>❌ Boom"), m)

# number / text controls: the button asks for a value, the reply goes to the task; bool = two buttons; long selects wrap
s, r = call("POST", "/api/tasks", {"name": "Inputs", "notify": {"start": True}, "controls": [
    {"id": "spd", "label": "Speed", "kind": "signal", "toast": True, "input": {"type": "number", "min": 1, "max": 10}},
    {"id": "note", "label": "Note", "kind": "signal", "toast": True, "input": {"type": "text"}},
    {"id": "dbg", "label": "Debug", "kind": "signal", "toast": True, "input": {"type": "bool"}},
    {"id": "col", "label": "Colour", "kind": "signal", "toast": True,
     "input": {"type": "select", "options": ["crimson", "emerald", "ultramarine"]}}]})
itid = r.get("id")
m = wait_sent(lambda m: "Inputs" in m["text"] and "reply_markup" in m)
kb = m["reply_markup"]["inline_keyboard"] if m else []
rows = [[b["text"] for b in row] for row in kb]
check("text/number -> 'value' buttons, bool -> two buttons", ["✏️ Speed…"] in rows and ["✏️ Note…"] in rows
      and ["Debug ✓", "Debug ✗"] in rows, rows)
check("long select options wrap to several rows", sum(1 for row in rows if row and row[0].startswith("Colour")) >= 2
      and all(len(row) <= 3 for row in rows), rows)
data = {b["text"]: b["callback_data"] for row in kb for b in row}


def ask(button):
    n = len(sent)
    push({"callback_query": {"id": "cb-" + button, "from": {"id": 555}, "message": {"message_id": 9, "chat": {"id": 555}},
                             "data": data.get(button, "x")}})
    return wait_sent(lambda x: sent.index(x) >= n and x.get("reply_markup", {}).get("force_reply") is True)


pr = ask("✏️ Speed…")
check("number button -> prompt with force_reply", pr is not None and "Speed" in pr["text"], sent[-2:])
pid_ = sent.index(pr) + 1 if pr else 0
n = len(sent)
push({"message": {"message_id": 50, "chat": {"id": 555}, "from": {"id": 555}, "text": "abc",
                  "reply_to_message": {"message_id": pid_}}})
check("not a number -> warning, nothing sent to the task", wait_sent(lambda x: sent.index(x) >= n and "⚠️" in x["text"]) is not None, sent[n:])
push({"message": {"message_id": 51, "chat": {"id": 555}, "from": {"id": 555}, "text": "42",
                  "reply_to_message": {"message_id": pid_}}})
time.sleep(1)
push({"message": {"message_id": 52, "chat": {"id": 555}, "from": {"id": 555}, "text": "7,5",
                  "reply_to_message": {"message_id": pid_}}})
ok = wait_sent(lambda x: x["text"].startswith("✓") and "7.5" in x["text"])
s, r = call("GET", f"/api/tasks/{itid}/commands?wait=3")
vals = [c.get("value") for c in r.get("commands", []) if c["control"] == "spd"]
check("reply '7,5' -> value 7.5; out of range (42) rejected", ok is not None and vals == [7.5], (vals, sent[-3:]))
pr = ask("✏️ Note…")
n = len(sent)
push({"message": {"message_id": 53, "chat": {"id": 555}, "from": {"id": 555}, "text": "hello there"}})   # typed without "reply"
wait_sent(lambda x: sent.index(x) >= n and x["text"].startswith("✓"))
s, r = call("GET", f"/api/tasks/{itid}/commands?wait=3")
vals = [c.get("value") for c in r.get("commands", []) if c["control"] == "note"]
check("text value (even without 'reply') -> delivered", vals == ["hello there"], vals)
n = len(sent)
msg(555, "/tasks")
check("commands still work after a prompt", wait_sent(lambda x: sent.index(x) >= n and "Inputs" in x["text"]) is not None, sent[n:])
call("DELETE", f"/api/tasks/{itid}?force=1")

# live progress: one silent message per task, edited in place; charts as one photo when the task finishes
s, r = call("POST", "/api/tasks", {"name": "Train run", "total": 10, "unit": "ep",
                                   "controls": [{"id": "halt", "label": "Halt", "kind": "signal", "toast": True}],
                                   "widgets": [{"id": "loss", "type": "chart", "label": "Loss", "series": ["train", "val"]}]})
ptid = r.get("id")
call("POST", f"/api/tasks/{ptid}/update", {"current": 3, "status": "epoch 3", "widgets": {"loss": [1.0, 1.3]}})
pm = wait_sent(lambda m: "Train run" in m["text"] and m.get("disable_notification") is True)
check("progress: silent message with bar and counter", pm is not None and "30%" in pm["text"] and "3/10 ep" in pm["text"]
      and "█" in pm["text"] and "&lt;1m" in pm["text"], sent[-3:])
check("no chart while the task runs", not photos, len(photos))
check("progress message has the task's buttons", pm is not None and
      [b["text"] for row in pm.get("reply_markup", {}).get("inline_keyboard", []) for b in row] == ["Halt"], pm)
for i, v in enumerate([0.9, 0.7, 0.5]):
    call("POST", f"/api/tasks/{ptid}/update", {"current": 4 + i, "widgets": {"loss": [v, v + 0.2]}})
call("POST", f"/api/tasks/{ptid}/finish", {"message": "ok"})
end = time.time() + 10
while time.time() < end and not (photos and any("Train run" in e.get("text", "") and "100%" in e.get("text", "") for e in edits)):
    time.sleep(0.2)
fe = [e for e in edits if "Train run" in e.get("text", "")]
check("progress message edited in place to the final state", pm is not None and fe and fe[-1]["message_id"] == sent.index(pm) + 1
      and "100%" in fe[-1]["text"] and "✅" in fe[-1]["text"] and "reply_markup" not in fe[-1], edits)
time.sleep(2.5)
if photos and os.environ.get("TG_DUMP"):   # save the rendered chart to look at it
    b = photos[0]
    open(os.environ["TG_DUMP"], "wb").write(b[b.index(b"\x89PNG"):b.index(b"IEND") + 8])
check("chart sent once, as a PNG, after the finish", len(photos) == 1 and b"\x89PNG" in photos[0] and b"Train run" in photos[0], len(photos))
check("finish notification still sent", wait_sent(lambda m: "Train run" in m["text"] and "✅" in m["text"]) is not None, sent[-3:])
s, r = call("POST", "/api/telegram", {"progress": False})
check("progress option can be switched off", r.get("progress") is False, r)
call("POST", "/api/telegram", {"progress": True})
call("DELETE", f"/api/tasks/{ptid}?force=1")
# ---- reliability (2.9): 429 honoured and the message retried; long texts cut before escaping; plain-text fallback
with lock:
    faults["429"] = 2
call("POST", "/api/notify", {"title": "after 429"})
m = wait_sent(lambda m: m["text"].startswith("<b>after 429"), timeout=15)
check("429 -> retried after retry_after, not dropped", m is not None and len(limited) == 2 and m["_t"] - limited[0] >= 1.8,
      (m and m["_t"] - limited[0], limited))
nr = len(rejected)
long_msg = ("a & b < c > d \"q\" 😀 Ünïcödé " * 300)   # ~9000 characters, entities and emoji everywhere
call("POST", "/api/notify", {"title": "x" + "😀" * 300, "message": long_msg})
m = wait_sent(lambda m: m["text"].startswith("<b>x😀"), timeout=15)
check("long text with entities / emoji delivered as HTML (cut before escaping)", m is not None and m.get("parse_mode") == "HTML"
      and "&amp;" in m["text"] and "😀" in m["text"] and "&#" not in m["text"], (rejected[nr:], m and m["text"][:120]))
check("no 400 from cut tags / entities / half emoji", len(rejected) == nr, rejected[nr:])
call("POST", "/api/notify", {"title": "FORCE_PARSE_ERR <tag>", "message": "x & y"})
m = wait_sent(lambda m: "FORCE_PARSE_ERR" in m["text"], timeout=10)
check("'can't parse entities' -> sent again as plain text", m is not None and "parse_mode" not in m
      and m["text"].startswith("FORCE_PARSE_ERR <tag>") and "x & y" in m["text"], m)
many = []
for i in range(40):
    s, r = call("POST", "/api/tasks", {"name": f"Batch & job {i} 😀 " + "n" * 150, "status": "step <" + "s" * 200 + ">"})
    many.append(r.get("id"))
n = len(sent)
msg(555, "/tasks")
m = wait_sent(lambda x: sent.index(x) >= n and "Batch &amp; job" in x["text"], timeout=15)
check("/tasks with many long tasks -> delivered, capped with '… +N'", m is not None and m.get("parse_mode") == "HTML"
      and "… +" in m["text"], (rejected[nr:], m and m["text"][-60:]))
for t in many:
    call("DELETE", f"/api/tasks/{t}?force=1")

# relink to another chat (e.g. the phone account) without re-entering the token
s, r = call("POST", "/api/telegram", {"relink": True})
code2 = (r.get("link") or "").rsplit("start=", 1)[-1]
check("relink -> new code, not linked yet", s == 200 and code2 and code2 != code and r.get("linked") is False, r)
msg(888, "/start " + code2)
check("relinked chat gets the welcome", wait_sent(lambda m: m["chat_id"] == 888) is not None, sent[-2:])
n2 = len(sent)
call("POST", "/api/notify", {"title": "to the phone"})
m = wait_sent(lambda m: "to the phone" in m["text"])
check("after relink only the new chat gets notifications", m is not None and m["chat_id"] == 888, sent[n2:])
if DATA:
    check("token in the Credential Manager, not in config.json",
          cred_exists(cred_target(DATA)) and "telegram_token" not in config(DATA), config(DATA).keys())
s, r = call("POST", "/api/telegram", {"disconnect": True})
if DATA:
    check("disconnect removes the token from the Credential Manager", not cred_exists(cred_target(DATA)))
check("disconnect", s == 200 and r.get("configured") is False and r.get("linked") is False, r)
n1 = len(sent)
call("POST", "/api/notify", {"title": "after disconnect"})
time.sleep(1.5)
check("nothing sent after disconnect", len(sent) == n1, sent[n1:])
# a config.json from before 2.9 (token in plain text): moved to the Credential Manager on start, removed from config.json
if EXE:
    data2 = tempfile.mkdtemp(prefix="taskhub-tgmigrate-")
    with open(os.path.join(data2, "config.json"), "w", encoding="utf-8") as f:
        json.dump({"toasts": False, "autostart": False, "telegram_api": "http://127.0.0.1:47897",
                   "telegram_token": TOKEN, "telegram_chat": 999}, f)
    url2 = "http://127.0.0.1:47894"
    hub2 = subprocess.Popen([EXE, "--port", "47894", "--data", data2, "--no-tray"])
    try:
        up = False
        for _ in range(60):
            try:
                up = call("GET", "/api/health", url=url2)[0] == 200
            except OSError:
                pass
            if up:
                break
            time.sleep(0.5)
        check("migration hub started", up)
        s, r = call("GET", "/api/telegram", url=url2)
        check("old token still works after the move", s == 200 and r.get("configured") and r.get("linked"), r)
        check("old token moved: in the Credential Manager, gone from config.json",
              cred_exists(cred_target(data2)) and "telegram_token" not in config(data2), config(data2))
        n3 = len(sent)
        call("POST", "/api/notify", {"title": "after the move"}, url=url2)
        m = wait_sent(lambda m: "after the move" in m["text"])
        check("notifications still reach the linked chat", m is not None and m["chat_id"] == 999, sent[n3:])
        call("POST", "/api/telegram", {"disconnect": True}, url=url2)
        check("migrated token removed on disconnect", not cred_exists(cred_target(data2)))
    finally:
        hub2.kill()
        hub2.wait(10)

srv.shutdown()
print(f"{fails} failure(s)")
raise SystemExit(1 if fails else 0)
