"""Telegram bridge against a mock Bot API (127.0.0.1:47897; the test hub's config.json points telegram_api there)."""
import json, os, re, threading, time, urllib.request, urllib.error
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
TOKEN = "123456789:" + "A" * 35
fails = 0
sent, answers, updates = [], [], []
lock = threading.Condition()
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
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
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
                sent.append(body)
                lock.notify_all()
            return self.reply(200, {"ok": True, "result": {"message_id": len(sent)}})
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


def call(method, path, body=None):
    req = urllib.request.Request(URL + path, json.dumps(body).encode() if body is not None else None, method=method,
                                 headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    try:
        with op.open(req, timeout=20) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


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
s, r = call("POST", "/api/telegram", {"disconnect": True})
check("disconnect", s == 200 and r.get("configured") is False and r.get("linked") is False, r)
n1 = len(sent)
call("POST", "/api/notify", {"title": "after disconnect"})
time.sleep(1.5)
check("nothing sent after disconnect", len(sent) == n1, sent[n1:])
srv.shutdown()
print(f"{fails} failure(s)")
raise SystemExit(1 if fails else 0)
