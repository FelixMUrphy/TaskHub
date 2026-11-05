"""Work hours: closing a month, reopening it, and the month's holidays from a stand-in for nagerholidays.com. Own hub on 47867 with temp data and the
clock moved to the 15th; the stand-in listens on 47868 and the hub reaches it only because TASKHUB_TEST_HOME is set. Nothing leaves this PC."""
import datetime, http.server, json, os, shutil, sqlite3, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request

os.environ.pop("TASKHUB_TOKEN", None)
EXE = sys.argv[1]
PORT, MOCK = 47867, 47868
URL = f"http://127.0.0.1:{PORT}"
TOKEN = "worktime-month-test-token"
tmp = tempfile.mkdtemp(prefix="taskhub-wtmonth-")
data, home = os.path.join(tmp, "data"), os.path.join(tmp, "home")
os.makedirs(data)
os.makedirs(home)
DB = os.path.join(data, "worktime.db")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
H = 3600
SHIFT = 15 - datetime.date.today().day
TODAY = datetime.date.today() + datetime.timedelta(days=SHIFT)
FIRST = TODAY.replace(day=1)
PREV = (FIRST - datetime.timedelta(days=1)).replace(day=1)
NEXT = (FIRST + datetime.timedelta(days=40)).replace(day=1)
M, PM, NM = FIRST.strftime("%Y-%m"), PREV.strftime("%Y-%m"), NEXT.strftime("%Y-%m")


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


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


# ---- the stand-in: the format of https://nagerholidays.com/api/v4/Holidays/{CC}/{Year} (a bare array; subdivisionCodes null when nationwide)
hits = []
down = [False]


def holidays(cc, year):
    d = lambda mo, dd: f"{year:04d}-{mo:02d}-{dd:02d}"
    h = lambda date, name, national=True, subs=None, types=("Public",): {"date": date, "name": name, "countryCode": cc, "nationalHoliday": national,
                                                                         "subdivisionCodes": subs, "holidayTypes": list(types)}
    if cc == "UA":
        return [h(d(1, 1), "New Year's Day"), h(d(FIRST.month, 3), "Independence Day"), h(d(FIRST.month, 20), "A bank day", types=("Bank",)),
                h(d(FIRST.month, 21), "Columbus Day", types=("Bank",))]
    if cc == "FR":
        return [h(d(NEXT.month, 2), "A French day")]
    if cc == "DE":
        return [h(d(FIRST.month, 4), "Epiphany", False, ["DE-BW", "DE-BY"]), h(d(FIRST.month, 5), "Reformation Day", False, ["DE-SN"]),
                h(d(FIRST.month, 6), "School day", False, ["DE-BY"], ("School",))]
    return None


class Mock(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        hits.append(self.path)
        if down[0]:
            self.send_response(503)
            self.end_headers()
            return
        parts = self.path.strip("/").split("/")
        body = None
        if len(parts) == 5 and parts[:3] == ["api", "v4", "Holidays"] and parts[4].isdigit():
            body = holidays(parts[3].upper(), int(parts[4]))
        if body is None:
            self.send_response(404)
            self.end_headers()
            return
        out = json.dumps(body, separators=(",", ":")).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(out)))
        self.end_headers()
        self.wfile.write(out)

    def log_message(self, *a):
        pass


mock = http.server.ThreadingHTTPServer(("127.0.0.1", MOCK), Mock)
threading.Thread(target=mock.serve_forever, daemon=True).start()

with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"token": TOKEN, "toasts": False, "autostart": False, "update_check": False, "language": "uk"}, f)
env = dict(os.environ, TASKHUB_NO_AUTOSTART="1", TASKHUB_TEST_HOME=home, TASKHUB_TEST_WORKTIME_SHIFT_DAYS=str(SHIFT), TASKHUB_TEST_NAGER=f"http://127.0.0.1:{MOCK}")
hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=env)
try:
    for _ in range(60):
        try:
            op.open(URL + "/api/health", timeout=1)
            break
        except OSError:
            time.sleep(.5)
    status = lambda: call("GET", "/api/worktime/status")[1]
    page = lambda b: call("POST", "/api/test/worktime-page", b)[1]

    # every day is a working day of 8 h; last month: day 1 a holiday worked 4 h (all to the holiday pool), day 2 only 5 h (3 h short), the rest full
    s, r = call("POST", "/api/worktime/setup", {"start_date": PREV.isoformat(), "settings": {"work_days": [1, 2, 3, 4, 5, 6, 7], "weekly_norm_hours": 56, "salary": 3000}})
    check("setup", s == 200, (s, r))
    call("PUT", f"/api/worktime/days?from={PREV.isoformat()}&to={PREV.isoformat()}", {"kind": "holiday"})
    d = PREV
    while d < TODAY:
        hours = 4 if d == PREV else 5 if d == PREV + datetime.timedelta(days=1) else 8
        s, r = call("POST", "/api/worktime/sessions", {"work_date": d.isoformat(), "total_hours": hours})
        if s != 200:
            check("a day filled", False, (d, s, r))
        d += datetime.timedelta(days=1)
    st = status()
    check("last month waits for its closing", st.get("unclosed_months") == [PM], st.get("unclosed_months"))
    check("no country: no holidays asked, nothing fetched", st.get("holidays_to_confirm") is None and not hits, (st.get("holidays_to_confirm"), hits))

    # ---- closing
    s, o = call("GET", f"/api/worktime/months/{PM}")
    check("the month as the closing sees it", s == 200 and o["deficit_seconds"] == 3 * H and o["holiday_seconds"] == 4 * H and o["weekend_seconds"] == 0
          and o["max_cover_seconds"] == 3 * H and o["can_close"] and not o["closed"] and o["deduct_unclosed"] is True, (s, o))
    s, o = call("GET", f"/api/worktime/months/{M}")
    check("this month can't be closed yet", s == 200 and not o["can_close"], o)
    check("...refused", call("POST", f"/api/worktime/months/{M}/close", {})[0] == 409)
    check("more than can be covered: refused", call("POST", f"/api/worktime/months/{PM}/close", {"cover_hours": 5})[0] == 400)
    check("carry must be true or false", call("POST", f"/api/worktime/months/{PM}/close", {"carry": "x"})[0] == 400)
    check("an unknown field", call("POST", f"/api/worktime/months/{PM}/close", {"x": 1})[0] == 400)
    check("a bad month", call("GET", "/api/worktime/months/2026-13")[0] == 400)
    s, p = call("POST", f"/api/worktime/months/{PM}/close?preview=1", {"cover_hours": 2, "carry": False})
    check("preview of a closing", s == 200 and p["before"]["deficit_seconds"] == 3 * H and p["after"]["deficit_seconds"] == 0, (s, p))
    check("...saves nothing", sqlite3.connect(DB).execute("SELECT count(*) FROM month_close").fetchone()[0] == 0)
    s, r = call("POST", f"/api/worktime/months/{PM}/close", {"cover_hours": 2, "carry": False})
    check("closed: 2 h covered with holiday hours, 1 h not carried", s == 200 and r["unclosed_months"] == [] and r["deficit_seconds"] == 0, (s, r))
    s, o = call("GET", f"/api/worktime/months/{PM}")
    c = o.get("close") or {}
    check("...what was decided is kept", o["closed"] and o["can_reopen"] and c.get("closed_by_holiday_seconds") == 2 * H and c.get("written_off_seconds") == 1 * H
          and c.get("paid_holiday_seconds") == 2 * H and c.get("carry") is False and c.get("currency") == "EUR", o)
    check("...and its numbers are shown from before it", o["deficit_seconds"] == 3 * H and o["holiday_seconds"] == 4 * H, o)
    check("a closed month: a day can't change", call("POST", "/api/worktime/sessions", {"work_date": (PREV + datetime.timedelta(days=5)).isoformat(), "total_hours": 1})[0] == 409)
    check("closed twice: refused", call("POST", f"/api/worktime/months/{PM}/close", {})[0] == 409)
    s, p = call("POST", f"/api/worktime/months/{PM}/reopen?preview=1", {})
    check("preview of reopening", s == 200 and p["after"]["deficit_seconds"] == 3 * H, (s, p))
    check("...saves nothing", status()["unclosed_months"] == [])
    s, r = call("POST", f"/api/worktime/months/{PM}/reopen", {})
    check("reopened: asked again", s == 200 and r["unclosed_months"] == [PM], (s, r))
    check("reopening a month that isn't closed: refused", call("POST", f"/api/worktime/months/{PM}/reopen", {})[0] == 409)
    s, r = call("POST", f"/api/worktime/months/{PM}/close", {"cover_hours": 2})
    check("closed again, the rest carried", s == 200 and r["deficit_seconds"] == 1 * H, (s, r))

    # ---- holidays
    for b, why in (({"country": "ukr"}, "a long country"), ({"country": "DE", "region": "UA-30"}, "a region of another country"), ({"region": "DE-BY"}, "a region without a country")):
        check(f"settings refuse {why}", page({"worktime_settings": b}).get("error"))
    page({"worktime_settings": {"country": "UA"}})
    check("a country: this month's holidays are asked", status().get("holidays_to_confirm") == M, status().get("holidays_to_confirm"))
    s, h = call("GET", f"/api/worktime/holidays?month={M}")
    names = {x["date"]: x for x in h.get("holidays", [])}
    d3 = FIRST.replace(day=3).isoformat()
    check("the list is fetched", s == 200 and h["source"] == "ok" and len(hits) == 1 and f"/Holidays/UA/{FIRST.year}" in hits[0], (s, h, hits))
    check("only days off of this month: not bank days, not other months", list(names) == [d3], list(names))
    check("a built-in name in the UI language, the source's kept", names[d3]["name"] == "День Незалежності України" and names[d3]["source_name"] == "Independence Day", names.get(d3))
    check("nothing applied before the answer", not names[d3]["marked"] and h["reviewed"] is False)
    call("GET", f"/api/worktime/holidays?month={M}")
    check("the list is kept for a week: not fetched again", len(hits) == 1, hits)
    check("a date outside the month: refused", call("POST", f"/api/worktime/holidays/{M}/confirm", {"dates": [PREV.isoformat()]})[0] == 400)
    check("an own day without a name: refused", call("POST", f"/api/worktime/holidays/{M}/confirm", {"own": [{"date": FIRST.replace(day=25).isoformat(), "name": " "}]})[0] == 400)
    own = FIRST.replace(day=25).isoformat()
    body = {"dates": [d3], "own": [{"date": own, "name": "Company day"}]}
    s, p = call("POST", f"/api/worktime/holidays/{M}/confirm?preview=1", body)
    check("preview of the answer", s == 200 and "before" in p and "after" in p, (s, p))
    check("...saves nothing", call("GET", f"/api/worktime/days?from={d3}&to={own}")[1]["days"] == [] and status().get("holidays_to_confirm") == M)
    s, r = call("POST", f"/api/worktime/holidays/{M}/confirm", body)
    check("confirmed", s == 200 and r.get("holidays_to_confirm") is None, (s, r))
    marks = {x["date"]: x for x in call("GET", f"/api/worktime/days?from={d3}&to={own}")[1]["days"]}
    check("...the kept day is an official holiday, the own one manual", marks.get(d3, {}).get("source") == "official" and marks.get(own, {}).get("source") == "manual", marks)
    check("a day shows its holiday's name", call("GET", f"/api/worktime/days/{d3}")[1].get("name") == "День Незалежності України")
    check("...the user's own name as typed", call("GET", f"/api/worktime/days/{own}")[1].get("name") == "Company day")
    bd = {x["date"]: x for x in call("GET", f"/api/worktime/breakdown?from={M}-01&to={own}")[1]["marks"]}
    check("the calendar has the names", bd.get(own, {}).get("name") == "Company day", bd)
    s, r = call("POST", f"/api/worktime/holidays/{M}/confirm", {"dates": [], "own": []})
    check("answered again without the official day: its mark goes, the own day stays",
          s == 200 and [x["date"] for x in call("GET", f"/api/worktime/days?from={d3}&to={own}")[1]["days"]] == [own], call("GET", f"/api/worktime/days?from={d3}&to={own}")[1])

    page({"worktime_settings": {"country": "DE", "region": "DE-BY"}})
    s, h = call("GET", f"/api/worktime/holidays?month={M}")
    check("a region: its holidays and the nationwide ones, not other regions' or school days", [x["source_name"] for x in h.get("holidays", [])] == ["Epiphany"], h)
    check("...not translated where there's no built-in name", h["holidays"][0]["name"] == "Epiphany", h)
    page({"worktime_settings": {"country": "XX", "region": None}})
    n = len(hits)
    s, h = call("GET", f"/api/worktime/holidays?month={NM}")
    check("a country the source doesn't cover: said so, not as a failure", s == 200 and h["source"] == "unknown" and h["holidays"] == [], (s, h))
    call("GET", f"/api/worktime/holidays?month={NM}")
    check("...and not asked of the source again", len(hits) == n + 1, hits[n:])
    down[0] = True
    page({"worktime_settings": {"country": "FR"}})
    s, h = call("GET", f"/api/worktime/holidays?month={NM}")
    check("the source away: unavailable", h.get("source") == "unavailable", h)
    own2 = NEXT.replace(day=9).isoformat()
    s, r = call("POST", f"/api/worktime/holidays/{NM}/confirm", {"own": [{"date": own2, "name": "Own"}]})
    check("without a list, own days are kept", s == 200 and [x["date"] for x in call("GET", f"/api/worktime/days?from={own2}&to={own2}")[1]["days"]] == [own2], (s, r))
    check("...and the month stays asked", call("GET", f"/api/worktime/holidays?month={NM}")[1].get("reviewed") is False)
    down[0] = False
    n = len(hits)
    check("a failure isn't retried at once by itself", call("GET", f"/api/worktime/holidays?month={NM}")[1].get("source") == "unavailable" and len(hits) == n, hits[n:])
    s, h = call("GET", f"/api/worktime/holidays?month={NM}&refresh=1")
    check("...but Retry asks now", h.get("source") == "ok" and [x["source_name"] for x in h["holidays"]] == ["A French day"] and len(hits) == n + 1, (h, hits[n:]))
    check("...with the own day listed", [x["date"] for x in h.get("own", [])] == [own2], h.get("own"))
    page({"worktime_settings": {"country": None}})
    check("country removed: nothing asked", status().get("holidays_to_confirm") is None)

    # ---- the file stays readable by the TaskHub before this one: no new schema version, the new tables just added
    con = sqlite3.connect(DB)
    check("schema version unchanged", con.execute("PRAGMA user_version").fetchone()[0] == 1)
    check("the new tables", {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")} >= {"holiday_cache", "holiday_review", "day_names"})
    con.close()
finally:
    hub.kill()
    hub.wait()
    mock.shutdown()
    time.sleep(.5)
    shutil.rmtree(tmp, ignore_errors=True)
print("worktime month: " + ("ALL PASSED" if fails == 0 else f"{fails} FAILED"))
sys.exit(1 if fails else 0)
