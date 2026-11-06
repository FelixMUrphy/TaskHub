"""Work hours export of a period: an .xlsx (day by day and a summary; amounts only when asked) and a short summary to a linked messenger, which never
carries amounts. Own test hub (47838, temp data; a Telegram stand-in on 47837 when Credential Manager is usable). The Save dialog of a test hub writes to
the test home's exports folder. Argument: TaskHubHost.exe. Nothing leaves this PC."""
import datetime, json, os, re, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request, zipfile
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

os.environ.pop("TASKHUB_TOKEN", None)
EXE = sys.argv[1]
PORT, MOCK = 47838, 47837
URL = f"http://127.0.0.1:{PORT}"
TOKEN = "worktime-export-test-token"
BOT = "123456789:" + "X" * 35
CHAT = 5202
NS = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
sent, updates = [], []
tg = threading.Condition()
SHIFT = 15 - datetime.date.today().day
TODAY = datetime.date.today() + datetime.timedelta(days=SHIFT)
START = TODAY.replace(day=1)
SALARY = 4321


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
            pass

    def do_POST(self):
        m = re.match(r"^/bot([^/]+)/(\w+)$", self.path)
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        body = {} if "multipart" in (self.headers.get("Content-Type") or "") else json.loads(raw or b"{}")
        method = m.group(2) if m else None
        if method == "getMe":
            return self.reply({"ok": True, "result": {"id": 1, "is_bot": True, "username": "export_test_bot"}})
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


def sheets(path):
    """{sheet name: [[cell text or float, ...] per row]}, cells placed by their column."""
    out = {}
    with zipfile.ZipFile(path) as z:
        wb = ET.fromstring(z.read("xl/workbook.xml"))
        names = [s.get("name") for s in wb.find("m:sheets", NS)]
        for i, n in enumerate(names, 1):
            rows = []
            for row in ET.fromstring(z.read(f"xl/worksheets/sheet{i}.xml")).iter(f"{{{NS['m']}}}row"):
                cells = {}
                for c in row:
                    col = re.match(r"[A-Z]+", c.get("r")).group(0)
                    idx = 0
                    for ch in col:
                        idx = idx * 26 + ord(ch) - 64
                    t = c.find("m:is/m:t", NS)
                    v = c.find("m:v", NS)
                    cells[idx - 1] = t.text if t is not None else float(v.text)
                rows.append([cells.get(k, "") for k in range(max(cells) + 1)] if cells else [])
            out[n] = rows
    return out


tmp = tempfile.mkdtemp(prefix="taskhub-wtexport-")
data, home = os.path.join(tmp, "data"), os.path.join(tmp, "home")
os.makedirs(data)
os.makedirs(home)
EXPORTS = os.path.join(home, "exports")
with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"token": TOKEN, "toasts": False, "autostart": False, "update_check": False, "language": "en", "toast_rate": 0, "toast_task_gap": 0,
               "telegram_api": f"http://127.0.0.1:{MOCK}"}, f)
ThreadingHTTPServer.request_queue_size = 64
server = ThreadingHTTPServer(("127.0.0.1", MOCK), Bot)
threading.Thread(target=server.serve_forever, daemon=True).start()
env = dict(os.environ, TASKHUB_NO_AUTOSTART="1", TASKHUB_TEST_HOME=home, TASKHUB_TEST_WORKTIME_SHIFT_DAYS=str(SHIFT))
hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=env)
do = lambda b: call("POST", "/api/test/worktime-page", dict(b, settings="do", what="wt_export"))[1]
try:
    for _ in range(60):
        try:
            op.open(URL + "/api/health", timeout=1)
            break
        except OSError:
            time.sleep(.5)
    s, r = call("POST", "/api/worktime/setup", {"start_date": START.isoformat(), "settings": {"work_days": [1, 2, 3, 4, 5], "weekly_norm_hours": 40, "salary": SALARY}})
    check("(set up from the 1st)", s == 200, (s, r))
    # weekdays 8 h, the first weekday 10 h (2 h overtime), the first weekend day 3 h (weekend overtime), today nothing yet
    hours = {}
    first_wd = next(START + datetime.timedelta(days=i) for i in range(7) if (START + datetime.timedelta(days=i)).weekday() < 5)
    first_we = next(START + datetime.timedelta(days=i) for i in range(7) if (START + datetime.timedelta(days=i)).weekday() >= 5)
    d = START
    while d < TODAY:
        if d.weekday() < 5:
            hours[d] = 10 if d == first_wd else 8
        elif d == first_we:
            hours[d] = 3
        d += datetime.timedelta(days=1)
    for h in sorted(set(hours.values())):
        s, r = call("PUT", "/api/worktime/days/hours", {"dates": [x.isoformat() for x, v in hours.items() if v == h], "total_hours": h})
        check(f"(days of {h} h set)", s == 200, (s, r))

    # ---- refused
    check("the period's end before its start refused", "ends before" in (do({"from": TODAY.isoformat(), "to": START.isoformat(), "dest": "excel"}).get("error") or ""))
    check("a date not a date refused", "both dates" in (do({"from": "soon", "to": TODAY.isoformat(), "dest": "excel"}).get("error") or ""))
    check("more than three years refused", "years" in (do({"from": "2000-01-01", "to": TODAY.isoformat(), "dest": "excel"}).get("error") or ""))
    check("an unknown destination refused", do({"from": START.isoformat(), "to": TODAY.isoformat(), "dest": "email"}).get("error"))
    check("a messenger not linked refused, said so", "isn't linked" in (do({"from": START.isoformat(), "to": TODAY.isoformat(), "dest": "discord"}).get("error") or ""))
    check("...and nothing written", not os.path.exists(EXPORTS) or not os.listdir(EXPORTS))

    # ---- Excel, no amounts; the period starts before the module did: only counted days
    r = do({"from": (START - datetime.timedelta(days=10)).isoformat(), "to": TODAY.isoformat(), "dest": "excel"})
    files = os.listdir(EXPORTS) if os.path.exists(EXPORTS) else []
    check("Excel: saved, said with the file's name", r.get("info", "").startswith("Saved: ") and len(files) == 1 and r["info"].endswith(files[0]), (r, files))
    path = os.path.join(EXPORTS, files[0]) if files else None
    check("...named by the period, .xlsx", files and files[0].endswith(".xlsx") and (START - datetime.timedelta(days=10)).isoformat() in files[0], files)
    book = sheets(path) if path else {}
    check("...two sheets: Summary, Days", list(book) == ["Summary", "Days"], list(book))
    days = book.get("Days", [])
    check("...a header row", days and days[0][:5] == ["Date", "Weekday", "Type of day", "Norm", "Worked"], days[:1])
    body = days[1:-1]
    serial = lambda x: (x - datetime.date(1899, 12, 30)).days
    check("...one row a day, from the start (not before) to today", [r_[0] for r_ in body] == [float(serial(START + datetime.timedelta(days=i))) for i in range((TODAY - START).days + 1)],
          [r_[0] for r_ in body][:3])
    by = {datetime.date(1899, 12, 30) + datetime.timedelta(days=int(r_[0])): r_ for r_ in body}
    hrs = lambda v: round(v * 24, 4) if isinstance(v, float) else v
    check("...hours as fractions of a day: the 10 h day", hrs(by[first_wd][4]) == 10 and hrs(by[first_wd][3]) == 8, by.get(first_wd))
    check("...its 2 h overtime, of weekdays", hrs(by[first_wd][7]) == 2 and by[first_wd][8] == "weekdays", by.get(first_wd))
    check("...the weekend day: a day off, 3 h of weekend overtime", by[first_we][2] == "Day off" and hrs(by[first_we][7]) == 3 and by[first_we][8] == "weekends", by.get(first_we))
    total = days[-1]
    check("...a total row: worked = the hours set", total[0] == "Total" and hrs(total[4]) == sum(hours.values()), total)
    summ = book.get("Summary", [])
    flat = [c for row in summ for c in row]
    check("...the summary: the period in the title", summ and summ[0] and START.strftime("%Y") in summ[0][0], summ[:1])
    check("...overtime by kind", any(row[:1] == ["Overtime, weekends"] and hrs(row[1]) == 3 for row in summ), summ)
    check("...no amounts", "Salary part" not in flat and float(SALARY) not in flat, flat)

    # ---- Excel with amounts
    r = do({"from": START.isoformat(), "to": TODAY.isoformat(), "dest": "excel", "amounts": True})
    f2 = sorted(os.listdir(EXPORTS))
    path2 = os.path.join(EXPORTS, next(x for x in f2 if x.startswith(re.sub(r"\.xlsx$", "", r.get("info", "")[7:])))) if r.get("info") else None
    summ2 = sheets(path2)["Summary"] if path2 and os.path.exists(path2) else []
    head = next((row for row in summ2 if row[:1] == ["Month"]), None)
    check("Excel with amounts: a table of months", head and head[:5] == ["Month", "Salary part", "Overtime pay", "Total", "Currency"], summ2)
    mon = summ2[summ2.index(head) + 1] if head else []
    check("...this month, not closed yet, in EUR, as numbers", mon and "not closed yet" in mon[0] and isinstance(mon[1], float) and mon[1] > 0 and mon[4] == "EUR", mon)
    check("...the total = salary part + overtime pay", mon and abs(mon[3] - (mon[1] + mon[2])) < .01, mon)
    check("...said per whole month", any("whole month" in str(c) for row in summ2 for c in row), summ2)
    # amounts asked for a messenger: never sent (checked below with Telegram)

    # ---- the file to replace held open by another program (as Excel does, no sharing): said in words, no temp file left beside it
    import ctypes
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.CreateFileW.restype = ctypes.c_void_p
    held = k32.CreateFileW(path2, 0x80000000, 0, None, 3, 0x80, None)   # GENERIC_READ, no sharing, OPEN_EXISTING
    check("(the file held open)", held not in (None, ctypes.c_void_p(-1).value), ctypes.get_last_error())
    try:
        r = do({"from": START.isoformat(), "to": TODAY.isoformat(), "dest": "excel", "amounts": True})
    finally:
        k32.CloseHandle(ctypes.c_void_p(held))
    check("a file open elsewhere: said to close it", "open in another program" in (r.get("error") or ""), r)
    check("...no temp file left beside it", not [x for x in os.listdir(EXPORTS) if x.endswith(".tmp")], os.listdir(EXPORTS))
    f2 = sorted(os.listdir(EXPORTS))

    # ---- a messenger: the summary only
    NO_VAULT = vault.probe()
    if NO_VAULT:
        print(f"SKIP the messenger part: Credential Manager unavailable here ({NO_VAULT})")
    else:
        s, r = call("POST", "/api/telegram", {"token": BOT})
        check("(Telegram paired with the stand-in)", s == 200 and "start=" in (r.get("link") or ""), (s, r))
        with tg:
            updates.append({"update_id": 2001, "message": {"message_id": 1, "chat": {"id": CHAT, "type": "private"}, "from": {"id": CHAT},
                                                            "text": "/start " + r["link"].rsplit("start=", 1)[-1]}})
            tg.notify_all()
        check("(Telegram linked)", until(lambda: call("GET", "/api/telegram")[1].get("linked")), call("GET", "/api/telegram")[1])
        until(lambda: [m for m in sent if m.get("chat_id") == CHAT])   # the greeting
        n = len(sent)
        r = do({"from": START.isoformat(), "to": TODAY.isoformat(), "dest": "telegram", "amounts": True})
        check("Telegram: sent, said so", r.get("info") == "Sent to Telegram.", r)
        got = until(lambda: [m for m in sent[n:] if m.get("chat_id") == CHAT])
        text = got[0]["text"] if got else ""
        check("...the summary: worked against the norm, overtime by kind", "Worked " in text and "Overtime " in text and "weekends 3:00" in text, text)
        check("...no amounts, even when asked", "EUR" not in text and str(SALARY) not in text and "€" not in text and "Salary" not in text, text)
        check("...no day list, no buttons", text.count("\n") <= 5 and "reply_markup" not in got[0], got)
        check("...nothing written to the exports folder", sorted(os.listdir(EXPORTS)) == f2, os.listdir(EXPORTS))
finally:
    try:
        call("POST", "/api/telegram", {"disconnect": True})
    except OSError:
        pass
    hub.kill()
    hub.wait()
    server.shutdown()
    time.sleep(.5)
    if os.environ.get("WT_EXPORT_KEEP") and os.path.exists(EXPORTS):   # a manual check: the files kept to open in Excel
        shutil.copytree(EXPORTS, os.environ["WT_EXPORT_KEEP"], dirs_exist_ok=True)
    shutil.rmtree(tmp, ignore_errors=True)
print("worktime export: " + ("ALL PASSED" if fails == 0 else f"{fails} FAILED"))
sys.exit(1 if fails else 0)
