"""Work hours reminders: work not started, a long pause, a session still open late, the month's notices; the buttons through the task's controls
(as the panel, toasts, Telegram and Discord press them). Own hub on 47880 with temp data and the clock moved to the 15th. A test hub checks only
when the test route asks, at the hub's clock plus an offset, with a given idle time and lock state. Nothing leaves this PC."""
import datetime, json, os, re, shutil, sqlite3, subprocess, sys, tempfile, time, urllib.error, urllib.request

os.environ.pop("TASKHUB_TOKEN", None)
# the checks of one day step a few minutes ahead of the clock: a run begun just before midnight waits for it (at most 10 min)
_n = datetime.datetime.now()
_left = (datetime.datetime.combine(_n.date() + datetime.timedelta(days=1), datetime.time()) - _n).total_seconds()
if _left < 600:
    print(f"waiting {int(_left) + 5} s for midnight", flush=True)
    time.sleep(_left + 5)
EXE = sys.argv[1]
PORT = 47880
URL = f"http://127.0.0.1:{PORT}"
TOKEN = "worktime-remind-test-token"
tmp = tempfile.mkdtemp(prefix="taskhub-wtremind-")
data, home = os.path.join(tmp, "data"), os.path.join(tmp, "home")
os.makedirs(data)
os.makedirs(home)
DB = os.path.join(data, "worktime.db")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
SHIFT = 15 - datetime.date.today().day
TODAY = datetime.date.today() + datetime.timedelta(days=SHIFT)
TID = "worktime-reminder"


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


def tick(offset=0, idle=0, locked=False):
    st, r = call("POST", "/api/test/worktime-remind", {"offset": offset, "idle": idle, "locked": locked})
    if "sent" not in r:
        print("tick:", st, r, flush=True)
        r.setdefault("sent", [])
    return r


def task():
    return next((t for t in call("GET", "/api/tasks")[1]["tasks"] if t["id"] == TID), None)


def press(name, full=None):
    """A reminder's button by its name; the id also carries its session or day (and time)."""
    t = task()
    cid = full or next((c["id"] for c in (t or {}).get("controls", []) if c["id"].split(".")[0] == name), name)
    return call("POST", f"/api/tasks/{TID}/controls/{cid}", {})


def page(b):
    return call("POST", "/api/test/worktime-page", b)[1]


def ids(r, kind):
    return [b["id"].split(".")[0] for x in r["sent"] if x["kind"] == kind for b in x["buttons"]]


def log():
    p = os.path.join(data, "taskhub.log")
    return open(p, encoding="utf-8", errors="replace").read() if os.path.exists(p) else ""


def local_now():
    """The hub's clock as a local datetime (the day moved like the hub's)."""
    return datetime.datetime.now() + datetime.timedelta(days=SHIFT)


def to_midnight():
    n = local_now()
    return int((datetime.datetime.combine(n.date() + datetime.timedelta(days=1), datetime.time()) - n).total_seconds())


with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"token": TOKEN, "toasts": False, "autostart": False, "update_check": False, "log_level": "debug"}, f)
env = dict(os.environ, TASKHUB_NO_AUTOSTART="1", TASKHUB_TEST_HOME=home, TASKHUB_TEST_WORKTIME_SHIFT_DAYS=str(SHIFT))
hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=env)
try:
    for _ in range(60):
        try:
            op.open(URL + "/api/health", timeout=1)
            break
        except OSError:
            time.sleep(.5)
    check("off: nothing to remind of", tick(0)["sent"] == [] and task() is None)
    call("POST", "/api/worktime/setup", {"start_date": TODAY.replace(day=1).isoformat(), "settings": {"work_days": [1, 2, 3, 4, 5, 6, 7], "salary": 3000}})
    s = page({})["settings"]
    check("defaults: start reminder off, a pause of 2 h, open after 23:00, every 30 min, messengers on",
          s.get("remind_start") is None and s.get("remind_paused_hours") == 2 and s.get("remind_open_after") == "23:00"
          and s.get("remind_repeat_minutes") == 30 and s.get("remind_messengers") is True, s)
    for b, why in (({"remind_start": "9:00"}, "a time without its zero"), ({"remind_open_after": "24:00"}, "24:00"), ({"remind_paused_hours": 0}, "a zero pause"),
                   ({"remind_repeat_minutes": 1}, "a repeat under 5 min"), ({"remind_messengers": "yes"}, "a word for a switch")):
        check(f"settings refuse {why}", page({"worktime_settings": b}).get("error"))
    check("no start reminder while it's off", tick(0)["sent"] == [])

    # ---- work not started: Not working today. Another client took the reminder's task id first: it isn't adopted
    st, _ = call("POST", "/api/tasks", {"id": TID, "name": "not a reminder", "source": "someone", "links": [{"label": "x", "url": "https://example.com"}]})
    check("(another client registers the id first)", st in (200, 201), st)
    page({"worktime_settings": {"remind_start": "00:00", "remind_open_after": None}})
    r = tick(0, idle=0)
    t = task()
    check("the reminder's task is TaskHub's own, not the other client's", t and t["source"] == "worktime" and not t.get("links") and t["name"] != "not a reminder", t)
    check("work not started: reminded, with its three buttons", ids(r, "start") == ["start_now", "start_from", "not_today"], r)
    sent = r["sent"][0]
    check("...at the PC: a toast", sent["at_pc"] is True, sent)
    check("...worded with the time, as its button writes it", sent["buttons"][1]["label"].split(" ", 2)[-1] in sent["message"], sent)
    t = task()
    check("...a task with the buttons on the panel, toasts and messengers",
          t and t["source"] == "worktime" and [c["id"].split(".")[0] for c in t["controls"]] == ["start_now", "start_from", "not_today"] and all(c.get("toast") for c in t["controls"]), t)
    check("...not sent again on the next check", tick(60)["sent"] == [])
    st, b = press("not_today")
    check("Not working today: taken", st == 200, (st, b))
    check("...the task goes", task() is None)
    r = tick(120)
    check("...no reminder today", r["sent"] == [] and task() is None, (r, task()))
    check("...a button pressed again: stale", press("not_today")[0] in (404, 409))
    con = sqlite3.connect(DB)
    con.execute("DELETE FROM meta WHERE key='remind_skip_day'")
    con.commit()
    con.close()

    # ---- away and locked: to the messengers, a toast when none is linked
    toasts = log().count("(toast only)")
    r = tick(0, idle=0, locked=True)
    check("locked: away from the PC", r["sent"] and r["sent"][0]["at_pc"] is False and r["sent"][0]["to"] == "all", r)
    end = time.time() + 10
    while time.time() < end and log().count("(toast only)") == toasts:
        time.sleep(.2)
    check("...no messenger linked: a toast after all", log().count("(toast only)") == toasts + 1 and "(messengers only)" not in log(), log()[-2000:])

    # ---- Start from <time>
    st, b = press("start_from")
    check("Start from: a session from the reminder's time", st == 200 and b["status"]["session"] and b["status"]["session"]["state"] == "running", (st, b))
    ints = call("GET", f"/api/worktime/intervals?from={TODAY.isoformat()}&to={TODAY.isoformat()}")[1]["intervals"]
    start = datetime.datetime.combine(TODAY, datetime.time())
    check("...started at 00:00 today", ints and datetime.datetime.fromisoformat(ints[0]["start"].replace("Z", "+00:00")).astimezone().replace(tzinfo=None) == start, ints)
    check("...the task goes", task() is None)
    check("...a session today: no start reminder", tick(0)["sent"] == [])

    # ---- a long pause: Finish at the pause (the start reminder off: an offset past midnight is a new day without a session)
    page({"worktime_settings": {"remind_start": None}})
    call("POST", "/api/worktime/pause", {})
    paused_at = call("GET", f"/api/worktime/intervals?from={TODAY.isoformat()}&to={TODAY.isoformat()}")[1]["intervals"][0]["end"]
    check("paused 1 h: no reminder yet", tick(3600)["sent"] == [])
    r = tick(2 * 3600 + 5, idle=4000)
    check("paused over 2 h: reminded, Finish now / Finish at / Still working", ids(r, "open") == ["finish_now", "finish_at", "still"], r)
    check("...away: for the messengers", r["sent"][0]["at_pc"] is False, r)
    check("...no amount in it", not re.search(r"\d+[.,]\d{2}|€|EUR", r["sent"][0]["message"] + r["sent"][0]["title"]), r["sent"][0])
    check("...not again before the repeat time", tick(2 * 3600 + 5 + 600)["sent"] == [])
    r = tick(2 * 3600 + 5 + 1800)
    check("...again after 30 min, until answered", ids(r, "open"), r)
    old = next(c["id"] for c in task()["controls"] if c["id"].startswith("finish_at."))
    st, b = press("finish_at")
    check("Finish at: the session closed", st == 200 and b["status"]["session"] is None, (st, b))
    con = sqlite3.connect(DB)
    fin = con.execute("SELECT finished_utc FROM sessions WHERE open=0").fetchall()
    con.close()
    pa = int(datetime.datetime.fromisoformat(paused_at.replace("Z", "+00:00")).timestamp())
    check("...at the moment of the pause", fin and fin[-1][0] == pa, (fin, pa))
    r = tick(2 * 3600 + 5 + 1900)
    check("...the task goes, nothing due", task() is None and r["sent"] == [], (task(), r))

    # ---- still open late: Still working snoozes, Finish now finishes
    page({"worktime_settings": {"remind_open_after": "23:00", "remind_paused_hours": None}})
    call("POST", "/api/worktime/start", {})
    r = tick(0)
    check("a session just begun isn't 'still open', whatever the hour", not [x for x in r["sent"] if x["kind"] == "open"], r)
    late = to_midnight() + 3660   # 01:01 the next day: past 23:00 of its day and over an hour since its start, whatever the hour of this run
    r = tick(late, idle=0)
    check("open past its day: reminded", ids(r, "open") and ids(r, "open")[0] == "finish_now", r)
    check("...at the PC, no Finish at (the last input is now)", "finish_at" not in ids(r, "open") and r["sent"][0]["at_pc"] is True, r)
    st, b = press("still")
    check("Still working: taken", st == 200, (st, b))
    check("...quiet for 2 h", tick(late + 3600)["sent"] == [] and task() is None)
    r = tick(late + 2 * 3600 + 60, idle=3000)
    check("...then reminded again, with Finish at the last input", ids(r, "open") == ["finish_now", "finish_at", "still"], r)
    st, b = press("finish_now")
    check("Finish now: the session closed", st == 200 and b["status"]["session"] is None, (st, b))
    call("POST", "/api/worktime/start", {})
    r = tick(late + 4 * 3600, idle=3000)
    st, b = press("finish_at", old)
    check("a button of an older session's reminder: refused (not among the task's buttons), the new session stays open", st in (404, 409) and call("GET", "/api/worktime/status")[1]["session"], (old, st, b, r))
    # a restart: the reminder task kept by the hub goes, what it asked is checked again
    check("...its reminder is up", task() is not None)
    hub.kill()
    hub.wait()
    hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=env)
    for _ in range(60):
        try:
            op.open(URL + "/api/health", timeout=1)
            break
        except OSError:
            time.sleep(.5)
    check("after a restart: no reminder left with dead buttons", task() is None, task())
    call("POST", "/api/worktime/finish", {})
    check("...a stale button refused", press("finish_now")[0] in (404, 409))

    # ---- the month's notices: a month to close, from 10:00, once a day, not after the page was opened
    con = sqlite3.connect(DB)
    prev = (TODAY.replace(day=1) - datetime.timedelta(days=1)).replace(day=1)
    con.execute("UPDATE meta SET value=? WHERE key='start_date'", (prev.isoformat(),))
    con.execute("UPDATE settings_history SET valid_from=? WHERE valid_from=(SELECT min(valid_from) FROM settings_history)", (prev.isoformat(),))
    con.commit()
    con.close()
    page({})   # the page was opened: nothing today
    ten = int((datetime.datetime.combine(local_now().date(), datetime.time(10, 30)) - local_now()).total_seconds())
    check("the page opened today: no notice", not [x for x in tick(ten)["sent"] if x["kind"] == "need"])
    r = tick(ten + 86400)
    need = [x for x in r["sent"] if x["kind"] == "need"]
    check("next day from 10:00: the month to close said, no buttons", need and not need[0]["buttons"] and need[0]["title"], r)
    check("...once a day", not [x for x in tick(ten + 86400 + 600)["sent"] if x["kind"] == "need"])
    check("...no task for a notice", task() is None)
    # ---- a session started on a vacation day: the mark kept by default, removed when asked (the panel asks)
    T0 = TODAY.isoformat()
    call("PUT", f"/api/worktime/days?from={T0}&to={T0}", {"kind": "vacation"})
    check("vacation today: said in the status", call("GET", "/api/worktime/status")[1].get("today_vacation") is True)
    kinds = lambda: [x["kind"] for x in call("GET", f"/api/worktime/days?from={T0}&to={T0}")[1]["days"]]
    st, _ = call("POST", "/api/worktime/start", {})
    check("start without an answer: as before, the mark stays", st == 200 and kinds() == ["vacation"], (st, kinds()))
    call("POST", "/api/worktime/finish", {})
    st, _ = call("POST", "/api/worktime/start", {"source": "panel", "vacation": "keep"})
    check("keep: the mark stays (hours as overtime)", st == 200 and kinds() == ["vacation"], (st, kinds()))
    call("POST", "/api/worktime/finish", {})
    check("a bad answer: refused", call("POST", "/api/worktime/start", {"vacation": "maybe"})[0] == 400)
    st, j = call("POST", "/api/worktime/start", {"source": "panel", "vacation": "unmark"})
    check("unmark: the mark removed, the session started", st == 200 and kinds() == [] and j["session"] and j.get("today_vacation") is False, (st, kinds(), j.get("today_vacation")))
    hist = call("GET", f"/api/worktime/days/{T0}/history")[1]
    check("...logged like a day change", "day_normal" in json.dumps(hist), hist)
    call("POST", "/api/worktime/finish", {})
    check("no reminder ERR in the log", "ERR" not in "\n".join(l for l in log().splitlines() if "worktime" in l), log()[-1500:])
finally:
    hub.kill()
    hub.wait()
    time.sleep(.5)
    shutil.rmtree(tmp, ignore_errors=True)
print("worktime reminders: " + ("ALL PASSED" if fails == 0 else f"{fails} FAILED"))
sys.exit(1 if fails else 0)
