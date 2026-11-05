"""The work hours module's data and API: own hub on 47866 with temp data (TASKHUB_TEST_HOME gives it the setup route), nothing leaves this PC.
All seven days are working days of 8 h and the hub's clock is moved to the 15th, so the sums don't depend on the day this runs.
Around midnight a run can fail: today moves."""
import datetime, json, os, shutil, sqlite3, subprocess, sys, tempfile, time, urllib.error, urllib.request

os.environ.pop("TASKHUB_TOKEN", None)
EXE = sys.argv[1]
PORT = 47866
URL = f"http://127.0.0.1:{PORT}"
TOKEN = "worktime-test-token"
tmp = tempfile.mkdtemp(prefix="taskhub-worktime-")
data, home = os.path.join(tmp, "data"), os.path.join(tmp, "home")
os.makedirs(data)
os.makedirs(home)
DB = os.path.join(data, "worktime.db")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
H = 3600
SHIFT = 15 - datetime.date.today().day
TODAY = datetime.date.today() + datetime.timedelta(days=SHIFT)


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(),
                               headers={"X-TaskHub": "1", "Content-Type": "application/json", "Authorization": "Bearer " + TOKEN})
    try:
        with op.open(r, timeout=15) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"{}")
        except ValueError:
            return e.code, {}


def start_hub(env_home=True):
    with open(os.path.join(data, "config.json"), "a+", encoding="utf-8") as f:
        f.seek(0)
        text = f.read()
    cfg = json.loads(text) if text.strip() else {}
    cfg.update(token=TOKEN, toasts=False, autostart=False, update_check=False)
    with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
        json.dump(cfg, f)
    env = dict(os.environ, TASKHUB_NO_AUTOSTART="1")
    env.pop("TASKHUB_TEST_HOME", None)
    if env_home:
        env["TASKHUB_TEST_HOME"] = home
        env["TASKHUB_TEST_WORKTIME_SHIFT_DAYS"] = str(SHIFT)
    h = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=env)
    for _ in range(60):
        try:
            op.open(URL + "/api/health", timeout=1)
            return h
        except OSError:
            time.sleep(.5)
    raise SystemExit("the hub didn't start")


def day(n):
    return (TODAY - datetime.timedelta(days=n)).isoformat()


def at(n, hh, mm=0):
    """Local time n days ago, with its offset."""
    d = datetime.datetime.combine(TODAY - datetime.timedelta(days=n), datetime.time(hh, mm))
    return d.astimezone().isoformat(timespec="seconds")


def status():
    return call("GET", "/api/worktime/status")[1]


def pools():
    p = status()["pools"]
    return p["weekday_seconds"], p["weekend_seconds"], p["holiday_seconds"]


hub = start_hub()
try:
    # ---- off until it's turned on
    s, r = call("GET", "/api/worktime/status")
    check("off: the status says so", s == 200 and r == {"enabled": False}, (s, r))
    check("off: no file", not os.path.exists(DB))
    for method, path, body in (("POST", "/api/worktime/start", {}), ("POST", "/api/worktime/pause", {}), ("POST", "/api/worktime/finish", {}),
                               ("POST", "/api/worktime/sessions", {"work_date": day(1), "total_hours": 1}), ("GET", "/api/worktime/intervals", None),
                               ("GET", "/api/worktime/days", None), ("GET", f"/api/worktime/days/{day(1)}/history", None)):
        check(f"off: {method} {path} refused", call(method, path, body)[0] == 409)
    check("off: still no file", not os.path.exists(DB))

    # ---- turning it on: every value checked
    allweek = {"work_days": [1, 2, 3, 4, 5, 6, 7], "weekly_norm_hours": 56, "salary": 3000}
    bad = [{"nonsense": 1}, {"start_date": "yesterday"}, {"start_date": "2026-02-30"}, {"start_date": day(-1)},
           {"settings": "x"}, {"settings": {"nonsense": 1}}, {"settings": {"weekly_norm_hours": "abc"}}, {"settings": {"weekly_norm_hours": 0}},
           {"settings": {"weekly_norm_hours": 169}}, {"settings": {"work_days": []}}, {"settings": {"work_days": [8]}}, {"settings": {"work_days": [1, 1]}},
           {"settings": {"salary": -1}}, {"settings": {"salary_currency": "euro"}}, {"settings": {"manual_rate": 0}}, {"settings": {"weekend_mult": "abc"}},
           {"settings": {"holidays_reduce_norm": "yes"}}, {"settings": {"tolerance_pct": 101}}, {"opening": "x"}, {"opening": {"nonsense": 1}},
           {"opening": {"deficit_hours": "abc"}}, {"opening": {"deficit_hours": -1}}, {"valid_from": day(1)}]
    for b in bad:
        check(f"setup refused: {json.dumps(b)[:60]}", call("POST", "/api/worktime/setup", b)[0] == 400, b)
    check("a refused setup leaves it off", status() == {"enabled": False})

    s, r = call("POST", "/api/worktime/setup", {"start_date": day(3), "settings": allweek})
    check("turned on", s == 200 and r.get("enabled") is True, (s, r))
    check("the file is there", os.path.exists(DB))
    check("the start date", r.get("start_date") == day(3), r)
    check("no session", r.get("session") is None, r)
    check("three past days without work: the deficit", r.get("deficit_seconds") == 3 * 8 * H, r.get("deficit_seconds"))
    check("the currency", r["money"]["currency"] == "EUR", r["money"])
    check("start date can't be changed later", call("POST", "/api/worktime/setup", {"start_date": day(1)})[0] == 409)
    check("nor the opening balance", call("POST", "/api/worktime/setup", {"opening": {"deficit_hours": 1}})[0] == 409)

    # ---- filling in past days
    s, r = call("POST", "/api/worktime/sessions", {"work_date": day(3), "total_hours": 8})
    check("a total for a day", s == 200 and r["deficit_seconds"] == 2 * 8 * H, (s, r.get("deficit_seconds")))
    s, r = call("POST", "/api/worktime/sessions", {"work_date": day(2), "intervals": [{"start": at(2, 9), "end": at(2, 12)}, {"start": at(2, 13), "end": at(2, 18), "note": "after lunch"}]})
    check("start-end pairs", s == 200 and r["deficit_seconds"] == 8 * H, (s, r.get("deficit_seconds")))
    s, r = call("POST", "/api/worktime/sessions", {"work_date": day(1), "total_hours": 10})
    check("two hours over: the deficit is gone", s == 200 and r["deficit_seconds"] == 0, (s, r.get("deficit_seconds")))
    check("...and they go to the pool", pools() == (2 * H, 0, 0), pools())
    s, r = call("POST", "/api/worktime/sessions", {"work_date": day(1), "total_hours": 8})
    check("a second session adds to the date", s == 200 and pools() == (10 * H, 0, 0), (s, pools()))

    for b, why in (({"work_date": day(4), "total_hours": 1}, "before the start date"),
                   ({"work_date": day(-1), "total_hours": 1}, "in the future"),
                   ({"work_date": "x", "total_hours": 1}, "a bad date"), ({"total_hours": 1}, "no date"),
                   ({"work_date": day(1)}, "neither"), ({"work_date": day(1), "total_hours": 1, "intervals": [{"start": at(1, 1), "end": at(1, 2)}]}, "both"),
                   ({"work_date": day(1), "total_hours": 0}, "zero hours"), ({"work_date": day(1), "total_hours": "abc"}, "not a number"), ({"work_date": day(1), "total_hours": 25}, "too many"),
                   ({"work_date": day(1), "intervals": []}, "an empty list"), ({"work_date": day(1), "intervals": ["x"]}, "not an object"),
                   ({"work_date": day(1), "intervals": [{"start": at(1, 2), "end": at(1, 1)}]}, "the end first"),
                   ({"work_date": day(1), "intervals": [{"start": "09:00", "end": "10:00"}]}, "times without a date"),
                   ({"work_date": day(1), "intervals": [{"start": at(1, 1)[:19], "end": at(1, 2)[:19]}]}, "times without an offset"),
                   ({"work_date": day(1), "intervals": [{"start": at(1, 1), "end": at(1, 2), "x": 1}]}, "an unknown interval field"),
                   ({"work_date": day(1), "intervals": [{"start": at(3, 1), "end": at(3, 2)}]}, "a start on another date"),
                   ({"work_date": day(1), "total_hours": 1, "note": "x" * 501}, "a long note"), ({"work_date": day(1), "total_hours": 1, "x": 1}, "an unknown field")):
        s, r = call("POST", "/api/worktime/sessions", b)
        check(f"session refused: {why}", s == 400, (s, r))
    s, r = call("POST", "/api/worktime/sessions", {"work_date": day(2), "intervals": [{"start": at(2, 10), "end": at(2, 11)}]})
    check("an overlap would count hours twice: refused", s == 409, (s, r))
    before = len(call("GET", f"/api/worktime/intervals?from={day(3)}&to={day(0)}")[1]["intervals"])
    s, r = call("POST", "/api/worktime/sessions", {"work_date": day(2), "intervals": [{"start": at(2, 20), "end": at(2, 21)}, {"start": at(2, 20, 30), "end": at(2, 22)}]})
    check("an overlap inside the request: refused", s == 409, (s, r))
    check("...nothing of it stays", len(call("GET", f"/api/worktime/intervals?from={day(3)}&to={day(0)}")[1]["intervals"]) == before)

    # ---- past midnight counts for the start date
    s, r = call("POST", "/api/worktime/setup", {"valid_from": day(0)})
    check("a new version of the settings", s == 200, (s, r))
    s, r = call("GET", f"/api/worktime/intervals?from={day(1)}&to={day(1)}")
    s, r = call("POST", "/api/worktime/sessions", {"work_date": day(2), "intervals": [{"start": at(2, 23), "end": at(1, 1)}]})
    check("across midnight: taken", s == 200, (s, r))
    listed = call("GET", f"/api/worktime/intervals?from={day(2)}&to={day(2)}")[1]["intervals"]
    night = [x for x in listed if x["duration_seconds"] == 2 * H and x["start"]]
    check("...on the date it started", len(night) == 1 and night[0]["work_date"] == day(2) and night[0]["source"] == "manual", listed)

    # ---- the list
    s, r = call("GET", f"/api/worktime/intervals?from={day(3)}&to={day(1)}")
    check("list: all of them", s == 200 and len(r["intervals"]) == 6, r)
    check("list: a duration has no times", any(x["start"] is None and x["end"] is None and x["duration_seconds"] == 8 * H for x in r["intervals"]), r)
    check("list: the note", any(x["note"] == "after lunch" for x in r["intervals"]), r)
    check("list: oldest first", [x["work_date"] for x in r["intervals"]] == sorted(x["work_date"] for x in r["intervals"]), r)
    for q in ("from=x", f"from={day(1)}&to={day(3)}", "from=2020-01-01&to=2026-01-01"):
        check(f"list refused: {q}", call("GET", "/api/worktime/intervals?" + q)[0] == 400)

    # ---- adding to a session, editing, deleting: all recalculated
    session = night[0]["session_id"]
    baseline = status()
    s, r = call("POST", "/api/worktime/intervals", {"session_id": session, "duration_hours": 1})
    check("an interval added: one more hour in the pool", s == 200 and r["pools"]["weekday_seconds"] == baseline["pools"]["weekday_seconds"] + H, (s, r.get("pools")))
    added = [x for x in call("GET", f"/api/worktime/intervals?from={day(2)}&to={day(2)}")[1]["intervals"] if x["start"] is None][0]
    for b, why in (({"session_id": 999999, "duration_hours": 1}, "no such session"),):
        check(f"interval refused: {why}", call("POST", "/api/worktime/intervals", b)[0] == 404)
    for b, why in (({"session_id": "x", "duration_hours": 1}, "a bad session"), ({"session_id": session}, "no times"),
                   ({"session_id": session, "duration_hours": 1, "start": at(2, 1)}, "both"), ({"session_id": session, "duration_hours": 25}, "too many")):
        check(f"interval refused: {why}", call("POST", "/api/worktime/intervals", b)[0] == 400, b)

    s, r = call("PUT", f"/api/worktime/intervals/{added['id']}", {"duration_hours": 3})
    check("edited to 3 hours: recalculated", s == 200 and r["pools"]["weekday_seconds"] == baseline["pools"]["weekday_seconds"] + 3 * H, (s, r.get("pools")))
    s, r = call("PUT", f"/api/worktime/intervals/{added['id']}", {"start": at(2, 5), "end": at(2, 6), "note": "early"})
    check("a duration becomes times", s == 200 and r["pools"]["weekday_seconds"] == baseline["pools"]["weekday_seconds"] + H, (s, r.get("pools")))
    x = [i for i in call("GET", f"/api/worktime/intervals?from={day(2)}&to={day(2)}")[1]["intervals"] if i["id"] == added["id"]][0]
    check("...and the note", x["note"] == "early" and x["start"] is not None, x)
    check("edit refused: an overlap", call("PUT", f"/api/worktime/intervals/{added['id']}", {"end": at(2, 10)})[0] == 409)
    check("edit refused: nothing to change", call("PUT", f"/api/worktime/intervals/{added['id']}", {})[0] == 400)
    check("edit refused: an unknown field", call("PUT", f"/api/worktime/intervals/{added['id']}", {"x": 1})[0] == 400)
    check("edit refused: no such interval", call("PUT", "/api/worktime/intervals/999999", {"duration_hours": 1})[0] == 404)
    s, r = call("DELETE", f"/api/worktime/intervals/{added['id']}")
    check("deleted: back as it was", s == 200 and r["pools"] == baseline["pools"], (s, r.get("pools")))
    check("delete refused: already gone", call("DELETE", f"/api/worktime/intervals/{added['id']}")[0] == 404)

    # a closed session that loses its last interval is gone
    s, r = call("POST", "/api/worktime/sessions", {"work_date": day(3), "total_hours": 1})
    extra = [i for i in call("GET", f"/api/worktime/intervals?from={day(3)}&to={day(3)}")[1]["intervals"] if i["duration_seconds"] == H][0]
    check("a session of one interval", extra["session_id"] > 0)
    call("DELETE", f"/api/worktime/intervals/{extra['id']}")
    check("...its session goes with it", call("POST", "/api/worktime/intervals", {"session_id": extra["session_id"], "duration_hours": 1})[0] == 404)

    # ---- the buttons
    check("pause without a session", call("POST", "/api/worktime/pause", {})[0] == 409)
    check("resume without a session", call("POST", "/api/worktime/resume", {})[0] == 409)
    check("finish without a session", call("POST", "/api/worktime/finish", {})[0] == 409)
    s, r = call("POST", "/api/worktime/start", {})
    check("start", s == 200 and r["session"]["state"] == "running" and r["session"]["work_date"] == day(0), (s, r.get("session")))
    open_id = r["session"]["id"]
    check("a second start: refused", call("POST", "/api/worktime/start", {})[0] == 409)
    check("resume while running: refused", call("POST", "/api/worktime/resume", {})[0] == 409)
    running = [i for i in call("GET", f"/api/worktime/intervals?from={day(0)}")[1]["intervals"] if i["running"]]
    check("a running interval, from the API", len(running) == 1 and running[0]["source"] == "api" and running[0]["end"] is None, running)
    check("edit refused: a running interval's end", call("PUT", f"/api/worktime/intervals/{running[0]['id']}", {"end": at(0, 0)})[0] == 409)
    check("edit refused: a running interval to a duration", call("PUT", f"/api/worktime/intervals/{running[0]['id']}", {"duration_hours": 1})[0] == 409)
    check("delete refused: a running interval", call("DELETE", f"/api/worktime/intervals/{running[0]['id']}")[0] == 409)
    time.sleep(1.1)
    s, r = call("POST", "/api/worktime/pause", {})
    check("pause", s == 200 and r["session"]["state"] == "paused", (s, r.get("session")))
    check("a second pause: refused", call("POST", "/api/worktime/pause", {})[0] == 409)
    worked = r["session"]["worked_seconds"]
    check("the time was counted", worked >= 1, worked)
    time.sleep(1.1)
    check("paused: the time stands", status()["session"]["worked_seconds"] == worked)
    s, r = call("POST", "/api/worktime/resume", {})
    check("resume", s == 200 and r["session"]["state"] == "running" and r["session"]["id"] == open_id, (s, r.get("session")))
    left = status()["deficit_seconds"]
    check("an open day adds no deficit", left == 0, left)
    time.sleep(1.1)
    s, r = call("POST", "/api/worktime/finish", {})
    check("finish", s == 200 and r["session"] is None, (s, r.get("session")))
    short = 8 * H - sum(i["duration_seconds"] for i in call("GET", f"/api/worktime/intervals?from={day(0)}")[1]["intervals"])
    weekday = baseline["pools"]["weekday_seconds"]
    check("a closed short day draws on the weekday pool at once", r["pools"]["weekday_seconds"] == max(0, weekday - short) and r["deficit_seconds"] == max(0, short - weekday), r)
    s, r = call("POST", "/api/worktime/start", {})
    check("started again: a second session of the date", s == 200 and r["session"]["id"] != open_id, (s, r.get("session")))
    call("POST", "/api/worktime/finish", {})

    # ---- the bars and the money
    r = status()
    for bar in ("week", "month"):
        check(f"{bar} bar: the fields", all(k in r[bar] for k in ("worked_seconds", "full_seconds", "expected_seconds", "balance_seconds", "tolerance_seconds", "above_seconds", "fill", "state")), r[bar])
        check(f"{bar} bar: a state", r[bar]["state"] in ("under", "normal", "over"), r[bar])
    check("week bar: a whole week's norm from the start date on", r["week"]["full_seconds"] % (8 * H) == 0 and 0 < r["week"]["full_seconds"] <= 56 * H, r["week"])
    check("money: earned so far", r["money"]["earned"] > 0, r["money"])
    check("money: rounded", round(r["money"]["earned"], 2) == r["money"]["earned"], r["money"])

    # ---- what a day is
    s, r = call("PUT", f"/api/worktime/days?from={day(3)}&to={day(3)}", {"kind": "vacation"})
    check("a vacation", s == 200, (s, r))
    s, r = call("GET", f"/api/worktime/days?from={day(3)}&to={day(3)}")
    check("...listed", r.get("days") == [{"date": day(3), "kind": "vacation", "source": "manual"}], r)
    s, r = call("PUT", f"/api/worktime/days?from={day(3)}&to={day(3)}", {"kind": "normal"})
    check("back to normal", s == 200 and call("GET", f"/api/worktime/days?from={day(3)}&to={day(3)}")[1]["days"] == [], (s, r))
    ahead = day(-30)
    check("a vacation ahead", call("PUT", f"/api/worktime/days?from={ahead}&to={ahead}", {"kind": "vacation"})[0] == 200)
    call("PUT", f"/api/worktime/days?from={ahead}&to={ahead}", {"kind": "normal"})
    p0 = pools()
    s, r = call("PUT", f"/api/worktime/days?from={day(2)}&to={day(2)}", {"kind": "holiday"})
    check("a holiday: its hours go to the holiday pool", s == 200 and r["pools"]["holiday_seconds"] > 0, (s, r.get("pools")))
    call("PUT", f"/api/worktime/days?from={day(2)}&to={day(2)}", {"kind": "normal"})
    check("...and back", pools() == p0, (pools(), p0))
    for q, b, why in ((f"from={day(1)}", {"kind": "x"}, "a bad kind"), (f"from={day(1)}", {}, "no kind"), (f"from={day(1)}", {"kind": 1}, "a kind that's no text"),
                      ("from=x", {"kind": "holiday"}, "a bad date"), (f"from={day(1)}", {"kind": "holiday", "x": 1}, "an unknown field")):
        check(f"day refused: {why}", call("PUT", "/api/worktime/days?" + q, b)[0] == 400)

    # ---- the log: what changed, by hand, day by day
    s, r = call("GET", f"/api/worktime/days/{day(3)}/history")
    actions = [x["action"] for x in r.get("changes", [])]
    check("history: newest first", s == 200 and actions[:3] == ["day_normal", "day_vacation", "interval_delete"], actions)
    change = r["changes"][1]
    check("history: before", change["before"]["mark"] is None, change)
    check("history: after", change["after"]["mark"] == {"kind": "vacation", "source": "manual"}, change)
    check("history: when", change["at"].endswith("Z"), change)
    s, r = call("GET", f"/api/worktime/days/{day(2)}/history")
    edit = [x for x in r["changes"] if x["action"] == "interval_edit"]
    check("history: an edit holds the intervals before and after", edit and edit[0]["before"]["sessions"] != edit[0]["after"]["sessions"], edit[:1])
    s, r = call("GET", f"/api/worktime/days/{day(0)}/history")
    check("history: the buttons aren't manual changes", s == 200 and r["changes"] == [], r)
    check("history refused: a bad date", call("GET", "/api/worktime/days/2026-13-01/history")[0] == 400)

    # ---- a closed month is read-only. Closing comes with a later stage, so the row is put in by hand, in this test's own file
    ints = call("GET", f"/api/worktime/intervals?from={day(2)}&to={day(2)}")[1]["intervals"]
    open_before = status()
    con = sqlite3.connect(DB)
    con.execute("INSERT INTO month_close VALUES(?,0,0,1,0,0,0,0,0,'0','0','EUR',NULL)", (TODAY.strftime("%Y-%m"),))
    con.commit()
    con.close()
    for method, path, body, why in (("POST", "/api/worktime/sessions", {"work_date": day(1), "total_hours": 1}, "a new session"),
                                    ("POST", "/api/worktime/intervals", {"session_id": ints[0]["session_id"], "duration_hours": 1}, "a new interval"),
                                    ("PUT", f"/api/worktime/intervals/{ints[0]['id']}", {"note": "x"}, "an edit"),
                                    ("DELETE", f"/api/worktime/intervals/{ints[0]['id']}", None, "a delete"),
                                    ("PUT", f"/api/worktime/days?from={day(1)}&to={day(1)}", {"kind": "holiday"}, "a day's kind")):
        check(f"closed month refuses {why}", call(method, path, body)[0] == 409)
    check("closed month: nothing moved", call("GET", f"/api/worktime/intervals?from={day(2)}&to={day(2)}")[1]["intervals"] == ints)
    con = sqlite3.connect(DB)
    con.execute("DELETE FROM month_close")
    con.commit()
    con.close()
    check("reopened: as before", status()["pools"] == open_before["pools"], status()["pools"])

    # ---- what the panel follows: a mark in the task snapshot, and who pressed the button
    m0 = call("GET", "/api/tasks")[1].get("worktime")
    check("the snapshot carries a mark, no numbers", isinstance(m0, int), m0)
    v0 = call("GET", "/api/tasks")[1]["version"]
    s, r = call("POST", "/api/worktime/start", {"source": "panel"})
    check("a button from the panel", s == 200, (s, r))
    t = call("GET", "/api/tasks")[1]
    check("...moves the mark and wakes the long poll", t["worktime"] != m0 and t["version"] > v0, (t["worktime"], t["version"]))
    check("...is remembered as the panel's", [i for i in call("GET", f"/api/worktime/intervals?from={day(0)}")[1]["intervals"] if i["running"]][0]["source"] == "panel")
    call("POST", "/api/worktime/pause", {})
    check("resume: a bad source", call("POST", "/api/worktime/resume", {"source": "x"})[0] == 400)
    check("resume: an unknown field", call("POST", "/api/worktime/resume", {"x": 1})[0] == 400)
    call("POST", "/api/worktime/resume", {"source": "panel"})
    check("money: says whether a salary is set", status()["money"]["set"] is True)

    # ---- what the settings page does (messages from the page only; the test hub has a route to the same code)
    s, r = call("POST", "/api/test/worktime-page", {"worktime": False})
    check("off with a session open: refused, says why", s == 200 and "session" in r.get("error", ""), (s, r))
    check("...and it stays on", status()["enabled"] is True)
    call("POST", "/api/worktime/finish", {})
    for b, why in (({"worktime_salary": "abc"}, "salary not a number"), ({"worktime_salary": -1}, "salary below 0"), ({"worktime_currency": "euro"}, "a long currency"), ({"worktime_currency": 5}, "currency not text")):
        s, r = call("POST", "/api/test/worktime-page", b)
        check(f"page refuses {why}", s == 200 and r.get("error"), (s, r))
    s, r = call("POST", "/api/test/worktime-page", {"worktime_salary": 0, "worktime_currency": "uah"})
    check("page: salary and currency, the code in capitals", r.get("salary") == 0 and r.get("salary_currency") == "UAH", r)
    r = status()
    check("no salary: no amounts asked for", r["money"]["set"] is False and r["money"]["currency"] == "UAH", r["money"])
    call("POST", "/api/test/worktime-page", {"worktime_salary": 3000, "worktime_currency": "EUR"})
    keep = status()
    s, r = call("POST", "/api/test/worktime-page", {"worktime": False})
    check("off", r == {"enabled": False} and status() == {"enabled": False}, r)
    check("off: gone from the snapshot", call("GET", "/api/tasks")[1].get("worktime") is None)
    check("off: the file stays", os.path.exists(DB))
    check("off: the salary can't be set", call("POST", "/api/test/worktime-page", {"worktime_salary": 1}).__getitem__(1).get("error"))
    s, r = call("POST", "/api/test/worktime-page", {"worktime": True})
    check("on again: the same start date", r.get("enabled") is True and r.get("start_date") == day(3), r)
    check("on again: the same numbers", status()["pools"] == keep["pools"] and status()["deficit_seconds"] == keep["deficit_seconds"], status())

    # ---- the token and the guard hold here as everywhere
    req = urllib.request.Request(URL + "/api/worktime/status")
    try:
        op.open(req, timeout=5)
        code = 200
    except urllib.error.HTTPError as e:
        code = e.code
    check("no token: 401", code == 401, code)
    req = urllib.request.Request(URL + "/api/worktime/start", method="POST", data=b"{}", headers={"Authorization": "Bearer " + TOKEN})
    try:
        op.open(req, timeout=5)
        code = 200
    except urllib.error.HTTPError as e:
        code = e.code
    check("no X-TaskHub: 403", code == 403, code)

    # ---- it keeps across a restart, and an open session stays open
    call("POST", "/api/worktime/start", {})
    keep = status()
    hub.kill()
    hub.wait()
    hub = start_hub()
    r = status()
    check("after a restart: on", r.get("enabled") is True, r)
    check("after a restart: the session still runs, nothing closes it", r["session"] and r["session"]["id"] == keep["session"]["id"] and r["session"]["state"] == "running", r.get("session"))
    check("after a restart: the same balance", r["pools"] == keep["pools"] and r["deficit_seconds"] == keep["deficit_seconds"], r)
    call("POST", "/api/worktime/finish", {})

    # ---- the file gone or damaged: said so, and the module can still be turned off
    page = lambda b: call("POST", "/api/test/worktime-page", b)
    os.remove(DB)
    check("file deleted: the status says so", status() == {"enabled": True, "broken": True}, status())
    s, r = page({})
    check("file deleted: the page says so, it doesn't fail", s == 200 and r == {"enabled": True, "broken": True}, (s, r))
    s, r = page({"worktime": False})
    check("file deleted: can be turned off", s == 200 and r == {"enabled": False}, (s, r))
    s, r = page({"worktime": True})
    check("file deleted: on again starts anew", s == 200 and r.get("enabled") is True and "broken" not in r, (s, r))
    junk = b"this is not a database" * 100
    with open(DB, "wb") as f:
        f.write(junk)
    check("file damaged: the status says so", status() == {"enabled": True, "broken": True}, status())
    s, r = page({})
    check("file damaged: the page says so, it doesn't fail", s == 200 and r == {"enabled": True, "broken": True}, (s, r))
    check("file damaged: a button is refused", call("POST", "/api/worktime/start", {})[0] >= 400)
    s, r = page({"worktime": False})
    check("file damaged: can be turned off", s == 200 and r == {"enabled": False}, (s, r))
    s, r = page({"worktime": True})
    check("file damaged: on again is refused, says why", s == 200 and r.get("error"), (s, r))
    check("...and it stays off", status() == {"enabled": False}, status())
    with open(DB, "rb") as f:
        check("file damaged: left as it was, for a restore", f.read() == junk)
    os.remove(DB)
    s, r = page({"worktime": True})
    check("file moved away: on again", s == 200 and r.get("enabled") is True, (s, r))

    # ---- no setup route on a real hub
    hub.kill()
    hub.wait()
    hub = start_hub(env_home=False)
    check("a real hub has no setup route", call("POST", "/api/worktime/setup", {})[0] == 404)
    check("...and no route to the settings page's code", call("POST", "/api/test/worktime-page", {"worktime": False})[0] == 404)
    check("...the module still answers", status().get("enabled") is True)
finally:
    hub.kill()
    hub.wait()
    time.sleep(.3)
    shutil.rmtree(tmp, ignore_errors=True)

print(f"\nworktime: {'FAILED ' + str(fails) if fails else 'ALL PASSED'}")
sys.exit(1 if fails else 0)
