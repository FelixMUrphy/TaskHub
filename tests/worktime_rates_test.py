"""Work hours: amounts in a display currency with rates from stand-ins for the ECB daily file, the NBP table A and the NBU list. Own hub on 47860 with temp data, the
clock moved to the 15th; the stand-in listens on 47854 and the hub reaches it only because TASKHUB_TEST_HOME is set. Nothing leaves this PC."""
import datetime, http.server, json, os, shutil, sqlite3, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request

os.environ.pop("TASKHUB_TOKEN", None)
EXE = sys.argv[1]
PORT, MOCK = 47860, 47854
URL = f"http://127.0.0.1:{PORT}"
TOKEN = "worktime-rates-test-token"
tmp = tempfile.mkdtemp(prefix="taskhub-wtrates-")
data, home = os.path.join(tmp, "data"), os.path.join(tmp, "home")
os.makedirs(data)
os.makedirs(home)
DB = os.path.join(data, "worktime.db")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
SHIFT = 15 - datetime.date.today().day
TODAY = datetime.date.today() + datetime.timedelta(days=SHIFT)
RDATE = TODAY - datetime.timedelta(days=1)


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


def until(cond, timeout=20):
    end = time.time() + timeout
    while time.time() < end:
        v = cond()
        if v:
            return v
        time.sleep(.2)
    return None


# ---- the stand-ins, in the sources' own formats (gotchas: ECB namespaced XML per EUR; NBU UAH per unit, dd.MM.yyyy, metals and XDR in the list)
hits = {"ecb": 0, "nbu": 0, "nbp": 0}
down = [False]
blocked = set()   # sources answering 403, as Cloudflare does to some networks
ECB = f"""<?xml version="1.0" encoding="UTF-8"?>
<gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
<gesmes:subject>Reference rates</gesmes:subject><gesmes:Sender><gesmes:name>European Central Bank</gesmes:name></gesmes:Sender>
<Cube><Cube time='{RDATE.isoformat()}'><Cube currency='USD' rate='1.1000'/><Cube currency='PLN' rate='4.4000'/><Cube currency='JPY' rate='160.00'/></Cube></Cube>
</gesmes:Envelope>"""
NBU = json.dumps([{"r030": 840, "txt": "Долар США", "rate": 41.5, "cc": "USD", "exchangedate": RDATE.strftime("%d.%m.%Y")},
                  {"r030": 978, "txt": "Євро", "rate": 45.0, "cc": "EUR", "exchangedate": RDATE.strftime("%d.%m.%Y")},
                  {"r030": 959, "txt": "Золото", "rate": 170000.0, "cc": "XAU", "exchangedate": RDATE.strftime("%d.%m.%Y"), "special": "Y"}], ensure_ascii=False)


# NBP: a bare array with the newest table A, PLN for one unit, today's (it is published at noon)
NBP = json.dumps([{"table": "A", "no": "1/A/NBP/2026", "effectiveDate": TODAY.isoformat(), "rates": [
    {"currency": "euro", "code": "EUR", "mid": 4.4}, {"currency": "dolar amerykański", "code": "USD", "mid": 4.0},
    {"currency": "hrywna (Ukraina)", "code": "UAH", "mid": 0.1}, {"currency": "SDR (MFW)", "code": "XDR", "mid": 5.3}]}], ensure_ascii=False)


class Mock(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        src = "nbu" if "statdirectory" in self.path or self.path.startswith("/nbu") else "nbp" if self.path.startswith("/nbp") else "ecb"
        hits[src] += 1
        if down[0] or src in blocked:
            self.send_response(403 if src in blocked else 503)
            self.end_headers()
            return
        out = {"nbu": NBU, "nbp": NBP, "ecb": ECB}[src].encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/xml" if src == "ecb" else "application/json")
        self.send_header("Content-Length", str(len(out)))
        self.end_headers()
        self.wfile.write(out)

    def log_message(self, *a):
        pass


mock = http.server.ThreadingHTTPServer(("127.0.0.1", MOCK), Mock)
threading.Thread(target=mock.serve_forever, daemon=True).start()
with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"token": TOKEN, "toasts": False, "autostart": False, "update_check": False}, f)
env = dict(os.environ, TASKHUB_NO_AUTOSTART="1", TASKHUB_TEST_HOME=home, TASKHUB_TEST_WORKTIME_SHIFT_DAYS=str(SHIFT),
           TASKHUB_TEST_ECB=f"http://127.0.0.1:{MOCK}/ecb/eurofxref-daily.xml", TASKHUB_TEST_NBU=f"http://127.0.0.1:{MOCK}/nbu/statdirectory/exchange?json",
           TASKHUB_TEST_NBP=f"http://127.0.0.1:{MOCK}/nbp/api/exchangerates/tables/A/?format=json")
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
    first = TODAY.replace(day=1)
    call("POST", "/api/worktime/setup", {"start_date": first.isoformat(), "settings": {"salary": 3000, "salary_currency": "EUR"}})
    for n in range(1, TODAY.day):
        call("POST", "/api/worktime/sessions", {"work_date": first.replace(day=n).isoformat(), "total_hours": 8})
    # settings saved before the rate keys existed (4.1.0): read with their defaults, a change still taken
    con = sqlite3.connect(DB)
    (vf, js), = con.execute("SELECT valid_from, json FROM settings_history").fetchall()
    old = {k: v for k, v in json.loads(js).items() if k not in ("country", "region", "display_currency", "rate_source", "rate_time", "fx_manual_rate")}
    con.execute("UPDATE settings_history SET json=? WHERE valid_from=?", (json.dumps(old), vf))
    con.commit()
    con.close()
    r = page({"worktime_settings": {"salary": 3000}})
    check("settings saved before the rate keys: a change is taken", "error" not in r and r["settings"].get("rate_source") == "ecb", r)
    m = status()["money"]
    check("no display currency: none asked, nothing fetched", m.get("display") is None and hits == {"ecb": 0, "nbu": 0, "nbp": 0}, (m, hits))
    check("the page's list: no rated currencies before any rates", r.get("rate_currencies") is None, r.get("rate_currencies"))
    for b, why in (({"display_currency": "dollar"}, "a long code"), ({"rate_source": "fed"}, "an unknown source"), ({"rate_time": "25:00"}, "a bad time"), ({"fx_manual_rate": 0}, "a zero rate")):
        check(f"settings refuse {why}", page({"worktime_settings": b}).get("error"))
    page({"worktime_settings": {"display_currency": "USD"}})
    status()   # the first ask starts the refresh in the background
    d = until(lambda: (status()["money"].get("display") or {}).get("rate") and status()["money"]["display"])
    earned = status()["money"]["earned"]
    check("rates from the ECB, in the background, no other source asked", d and hits["ecb"] == 1 and hits["nbu"] == 0 and hits["nbp"] == 0, (d, hits))
    check("...the chosen source, no stand-in", d and d["source"] == "ecb" and d["chosen"] == "ecb" and d["fallback"] is False and d["chosen_error"] is None, d)
    check("the amounts converted", d and abs(d["earned"] - round(earned * 1.1, 2)) < 0.02 and d["currency"] == "USD" and d["source"] == "ecb", (d, earned))
    check("...the rate's own date, not stale", d and d["rate_date"] == RDATE.isoformat() and d["stale"] is False and d["manual"] is False, d)
    status()
    time.sleep(1)
    check("refreshed once a day, not on every ask", hits["ecb"] == 1, hits)
    check("the page's list: the currencies the source has a rate for, with its base", page({}).get("rate_currencies") == ["EUR", "JPY", "PLN", "USD"], page({}).get("rate_currencies"))
    page({"worktime_settings": {"display_currency": "PLN"}})
    d = status()["money"]["display"]
    check("another currency from the same list, no new fetch", abs(d["rate"] - 4.4) < 1e-9 and hits["ecb"] == 1, (d, hits))
    page({"worktime_settings": {"display_currency": "UAH"}})
    status()
    d = until(lambda: status()["money"]["display"].get("rate") and status()["money"]["display"])
    check("a currency the ECB hasn't got: the NBP stands in, said so", d and d["source"] == "nbp" and d["fallback"] is True and d["chosen"] == "ecb" and hits["nbp"] == 1, (d, hits))
    check("...its rate through the zloty", d and abs(d["rate"] - 4.4 / 0.1) < 1e-6 and d["rate_date"] == TODAY.isoformat(), d)
    check("...the NBU not asked once the NBP had the pair", hits["nbu"] == 0, hits)
    n = dict(hits)
    page({"worktime_settings": {"display_currency": "CHF"}})
    status()
    d = until(lambda: status()["money"]["display"].get("why") == "unsupported" and status()["money"]["display"])
    check("a currency no source has: said, as not covered", d and d.get("missing") is True and "earned" not in d, d)
    check("...sources known to lack it not asked", hits["nbp"] == n["nbp"], (hits, n))
    page({"worktime_settings": {"fx_manual_rate": 44}})
    d = status()["money"]["display"]
    check("...then the own rate stands in", d.get("manual") is True and abs(d["rate"] - 44) < 1e-9 and d.get("rate_date") is None, d)
    page({"worktime_settings": {"display_currency": "UAH", "fx_manual_rate": None}})
    d = status()["money"]["display"]
    check("a source's rate wins over the own one", d.get("manual") is False and d["source"] == "nbp", d)
    n = dict(hits)
    page({"worktime_settings": {"rate_source": "nbu", "display_currency": "USD", "fx_manual_rate": None}})
    status()
    d = until(lambda: status()["money"]["display"].get("source") == "nbu" and status()["money"]["display"].get("rate") and status()["money"]["display"])
    check("a newly chosen source: its rate used (its list came already, while it stood by for CHF)", d and d["source"] == "nbu" and d["fallback"] is False, (d, hits))
    check("...a cross rate through the hryvnia", d and abs(d["rate"] - 45.0 / 41.5) < 1e-6, d)
    # the chosen source refuses this network (Cloudflare 403 behind a VPN): another one stands in, the panel says why
    blocked.add("nbu")
    con = sqlite3.connect(DB)
    con.execute("DELETE FROM meta WHERE key LIKE 'rates_%ok%'")
    con.commit()
    con.close()
    n = dict(hits)
    page({"worktime_settings": {"display_currency": "UAH", "salary_currency": "EUR"}})
    status()
    d = until(lambda: status()["money"]["display"].get("chosen_error") and status()["money"]["display"])
    check("the chosen source blocked: said as blocked", d and d["chosen_error"] == "blocked" and d["chosen"] == "nbu", d)
    check("...the NBP's rate used, the ECB (no hryvnia) not asked", d and d["source"] == "nbp" and d["fallback"] is True and hits["ecb"] == n["ecb"] and hits["nbp"] == n["nbp"] + 1, (d, hits, n))
    check("...not stale: a source gave the pair", d and d["stale"] is False, d)
    blocked.discard("nbu")
    page({"worktime_settings": {"salary_currency": "EUR"}})
    rc = page({}).get("rate_currencies") or []
    check("...the page's list: what any source has, with the bases, no metals or SDR", rc == ["EUR", "JPY", "PLN", "UAH", "USD"], rc)
    con = sqlite3.connect(DB)
    check("metals left out of the NBU list", con.execute("SELECT count(*) FROM rates WHERE currency='XAU'").fetchone()[0] == 0)
    check("rates kept with their date and source", con.execute("SELECT count(*) FROM rates WHERE source='ecb' AND date=?", (RDATE.isoformat(),)).fetchone()[0] == 3)
    con.close()
    page({"worktime_settings": {"display_currency": "EUR"}})
    check("the salary's own currency: no conversion", status()["money"].get("display") is None)
    page({"worktime_settings": {"display_currency": None}})
    check("cleared: none", status()["money"].get("display") is None)
    # a failed refresh: the old rate stays, said stale
    down[0] = True
    con = sqlite3.connect(DB)
    con.execute("DELETE FROM meta WHERE key LIKE 'rates_%ok%'")
    con.commit()
    con.close()
    n = hits["ecb"]   # before the change: the status it answers with already starts the refresh
    page({"worktime_settings": {"display_currency": "USD", "rate_source": "ecb"}})
    status()
    until(lambda: hits["ecb"] > n, 10)
    d = until(lambda: status()["money"]["display"].get("stale") and status()["money"]["display"], 10)
    check("a failed refresh: the freshest kept rate used (the NBP's), said stale and why", d and abs(d["rate"] - 1.1) < 1e-6 and d["source"] == "nbp" and d["rate_date"] == TODAY.isoformat()
          and d["chosen_error"] == "unavailable", d)
    status()
    time.sleep(1)
    check("...retried later, not on every ask", hits["ecb"] == n + 1, hits)
finally:
    hub.kill()
    hub.wait()
    mock.shutdown()
    time.sleep(.5)
    shutil.rmtree(tmp, ignore_errors=True)
print("worktime rates: " + ("ALL PASSED" if fails == 0 else f"{fails} FAILED"))
sys.exit(1 if fails else 0)
