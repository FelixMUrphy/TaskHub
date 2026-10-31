"""GitHub Actions connector against a mock GitHub API (127.0.0.1:47896; the test hub's config.json points
connectors.github.api there). Runs become tasks, progress by steps, ETA from earlier runs, failed step + log tail,
cancel / re-run buttons, artifacts / release links, ETag polling, the token kept out of config.json and responses."""
import hashlib, json, os, re, threading, time, urllib.request, urllib.error
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import vault

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
DATA = os.environ.get("TASKHUB_DATA", os.path.join(os.environ.get("TEMP", "."), "taskhub-tests"))
PORT = 47896
TOKEN = "github_pat_" + "A" * 60
fails = 0
lock = threading.RLock()
calls = []                 # (method, path, auth header)
stats = {"200": 0, "304": 0}
faults = {"secondary": 0}  # next N runs-list calls: 403 secondary rate limit (remaining > 0, no retry-after)
PAGE = 20                  # the mock pages runs lists by 20, whatever per_page asks: paging via the Link header
EXP = (time.time() + 3 * 86400)
EXP_S = time.strftime("%Y-%m-%d %H:%M:%S UTC", time.gmtime(EXP))


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


def iso(t):
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))


def step(name, status="queued", concl=None):
    return {"name": name, "status": status, "conclusion": concl, "number": 1}


now0 = time.time()
runs = {}


def mkrun(rid, status, concl=None, branch="main", started=None, title="Fix the parser", wf=7, event="push"):
    runs[rid] = {"id": rid, "name": "CI", "display_title": title, "head_branch": branch, "head_sha": "abc1234def",
                 "event": event, "status": status, "conclusion": concl, "workflow_id": wf, "run_attempt": 1,
                 "actor": {"login": "alice"}, "html_url": f"http://127.0.0.1:{PORT}/o/r/actions/runs/{rid}",
                 "created_at": iso(started or now0), "run_started_at": iso(started or now0), "updated_at": iso(time.time()),
                 "jobs": []}
    return runs[rid]


mkrun(100, "completed", "success", started=now0 - 3600)
r101 = mkrun(101, "in_progress", started=now0 - 30)
r101["jobs"] = [{"id": 9001, "name": "build", "status": "completed", "conclusion": "success",
                 "steps": [step("Checkout", "completed", "success"), step("Build", "completed", "success")]},
                {"id": 9002, "name": "test", "status": "in_progress", "conclusion": None,
                 "steps": [step("Checkout", "completed", "success"), step("Run pytest", "in_progress"), step("Upload", "queued")]}]
history = [{"id": 50 + i, "run_started_at": iso(now0 - 9000 + i * 600), "updated_at": iso(now0 - 9000 + i * 600 + d),
            "conclusion": "success"} for i, d in enumerate([100, 120, 140])]
LOG = "﻿2026-10-30T10:00:00.0000000Z ##[group]Run pytest\n" + "".join(
    f"2026-10-30T10:00:0{i % 10}.0000000Z line {i}\n" for i in range(60)) + \
      "2026-10-30T10:01:00.0000000Z E   AssertionError: 2 != 3\n2026-10-30T10:01:01.0000000Z ##[error]Process completed with exit code 1.\n"


class Api(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def reply(self, code, obj=None, headers=None):
        data = json.dumps(obj).encode() if obj is not None else b""
        etag = '"' + hashlib.md5(data).hexdigest() + '"'
        if code == 200 and self.command == "GET" and self.headers.get("If-None-Match") == etag:
            with lock:
                stats["304"] += 1
            self.send_response(304)
            self.send_header("ETag", etag)
            self.end_headers()
            return
        if code == 200:
            with lock:
                stats["200"] += 1
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("ETag", etag)
        self.send_header("x-ratelimit-remaining", "4990")
        self.send_header("x-ratelimit-reset", str(int(time.time()) + 3600))
        self.send_header("github-authentication-token-expiration", EXP_S)
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self.route("GET")

    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length") or 0))
        self.route("POST")

    def route(self, m):
        path = self.path
        auth = self.headers.get("Authorization")
        with lock:
            calls.append((m, path, auth))
        if path.startswith("/logblob/"):   # the log storage: must never get the token
            body = LOG.encode()
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if auth != "Bearer " + TOKEN:
            return self.reply(401, {"message": "Bad credentials"})
        if path == "/user":
            return self.reply(200, {"login": "tester"})
        if path.startswith("/repos/o/missing/"):
            return self.reply(404, {"message": "Not Found"})
        if path.startswith("/repos/o/old/"):   # renamed: GitHub answers 301 to the new place
            return self.reply(301, {"message": "Moved Permanently"},
                              {"Location": f"http://127.0.0.1:{PORT}/repos/o/r/" + path[len("/repos/o/old/"):]})
        p = path.split("?")[0]
        q = dict(kv.split("=", 1) for kv in path.split("?", 1)[1].split("&")) if "?" in path else {}
        mm = re.match(r"^/repos/o/r/actions/(.*)$", p)
        if not mm:
            mr = re.match(r"^/repos/o/r/releases/tags/(.+)$", p)
            if mr and mr.group(1) == "v1.2.3":
                return self.reply(200, {"name": "TaskHub 1.2.3", "html_url": "https://github.com/o/r/releases/tag/v1.2.3"})
            return self.reply(404, {"message": "Not Found"})
        rest = mm.group(1)
        with lock:
            if m == "GET" and rest == "runs":
                if faults["secondary"] > 0:
                    faults["secondary"] -= 1
                    return self.reply(403, {"message": "You have exceeded a secondary rate limit. Please wait a few minutes."})
                lst = [dict({k: v for k, v in r.items() if k != "jobs"}, repository={"full_name": "o/r"})
                       for r in sorted(runs.values(), key=lambda r: -r["id"])]
                page = int(q.get("page", 1))
                part = lst[(page - 1) * PAGE:page * PAGE]
                hdr = {}
                if page * PAGE < len(lst):
                    hdr["Link"] = f'<http://127.0.0.1:{PORT}/repos/o/r/actions/runs?per_page=100&page={page + 1}>; rel="next"'
                return self.reply(200, {"total_count": len(lst), "workflow_runs": part}, hdr)
            if m == "GET" and rest == "workflows/7/runs":
                return self.reply(200, {"workflow_runs": history})
            x = re.match(r"^runs/(\d+)(?:/(.*))?$", rest)
            if x:
                r = runs.get(int(x.group(1)))
                sub = x.group(2) or ""
                if r is None:
                    return self.reply(404, {"message": "Not Found"})
                if m == "GET" and sub == "":
                    return self.reply(200, {k: v for k, v in r.items() if k != "jobs"})
                if m == "GET" and re.match(r"^attempts/\d+/jobs$", sub):
                    return self.reply(200, {"total_count": len(r["jobs"]), "jobs": r["jobs"]})
                if m == "GET" and sub == "artifacts":
                    return self.reply(200, {"artifacts": [{"id": 5, "name": "setup-exe", "expired": False}]})
                if m == "POST" and sub == "cancel":
                    return self.reply(202, {})
                if m == "POST" and sub in ("rerun", "rerun-failed-jobs"):
                    r["run_attempt"] += 1
                    r["status"], r["conclusion"] = "queued", None
                    r["jobs"] = []
                    return self.reply(201, {})
            x = re.match(r"^jobs/(\d+)/logs$", rest)
            if m == "GET" and x:
                return self.reply(302, None, {"Location": f"http://127.0.0.1:{PORT}/logblob/{x.group(1)}"})
        self.reply(404, {"message": "Not Found"})


op = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def call(method, path, body=None):
    req = urllib.request.Request(URL + path, json.dumps(body).encode() if body is not None else None, method=method,
                                 headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    try:
        with op.open(req, timeout=30) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


def task(tid):
    s, r = call("GET", f"/api/tasks/{tid}")
    return r if s == 200 else None


def wait(pred, timeout=15):
    end = time.time() + timeout
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(0.3)
    return None


ThreadingHTTPServer.request_queue_size = 64
srv = ThreadingHTTPServer(("127.0.0.1", PORT), Api)
threading.Thread(target=srv.serve_forever, daemon=True).start()

call("POST", "/api/settings", {"language": "en"})   # the task texts below are checked in English

# ---- settings and the token
call("POST", "/api/connectors/github", {"forget_token": True, "enabled": False})   # a token left by an aborted run
s, r = call("POST", "/api/tasks", {"name": "x", "controls": [{"id": "c", "kind": "connector"}]})
check("API clients cannot make 'connector' buttons", s == 400, (s, r))
s, r = call("POST", "/api/connectors/github", {"token": "not-a-token"})
check("bad token format -> 400", s == 400, (s, r))
s, r = call("POST", "/api/connectors/github", {"token": "github_pat_" + "B" * 60})
check("token GitHub rejects -> 400, not saved", s == 400 and "rejected" in r.get("error", ""), (s, r))
s, r = call("GET", "/api/connectors/github")
check("nothing saved after a rejected token", r.get("token_set") is False, r)
s, r = call("POST", "/api/connectors/github", {"repos": ["o/r", "https://github.com/o/missing.git"], "enabled": True,
                                                "interval": 3, "active_interval": 2, "logs": True, "notify_success": True})
check("settings saved, repo URL normalised", s == 200 and r.get("repos") == ["o/r", "o/missing"], (s, r))
s, r = call("POST", "/api/connectors/github", {"repos": ["not a repo"]})
check("bad repo -> 400", s == 400, (s, r))
s, r = call("POST", "/api/connectors/github", {"token": TOKEN})
check("good token -> saved, login known, token never returned", s == 200 and r.get("token_set") is True
      and r.get("login") == "tester" and TOKEN not in json.dumps(r), (s, r))
cfg = open(os.path.join(DATA, "config.json"), encoding="utf-8").read()
check("token not in config.json", TOKEN not in cfg and "github_pat_" not in cfg, cfg[:300])
check("token checked right after saving -> 'Connected as @tester'", r.get("token_check") == "ok"
      and r.get("token_check_text") == "Connected as @tester", r)

# ---- a run in progress becomes a task; the finished old one does not
t = wait(lambda: task("gh-101"))
check("running run -> task gh-101", t is not None and t["state"] == "running" and t["source"] == "github", t)
check("old finished run is not shown", task("gh-100") is None)
if t:
    check("project = repo, description: repo · branch · sha · actor · event",
          t["project"] == "r" and "o/r · main · abc1234 · @alice · push" in t["description"], (t["project"], t["description"]))
    check("name = workflow · title", t["name"] == "CI · Fix the parser", t["name"])
    t = wait(lambda: (lambda x: x if x and x.get("progress") is not None else None)(task("gh-101")))
    check("progress by steps (3 of 5 done)", t and abs(t["progress"] - 0.6) < 1e-6, t and t.get("progress"))
    check("stage = job › step", t and t.get("stage") == "test › Run pytest", t and t.get("stage"))
    check("ETA from the usual duration (median 120 s)", t and t.get("expected_duration") == 120 and
          abs(t.get("eta_at", 0) - (now0 - 30 + 120)) < 3, t and (t.get("expected_duration"), t.get("eta_at"), now0))
    check("started = run start on GitHub", t and abs(t["started"] - (now0 - 30)) < 2, t and t["started"])
    ids = [c["id"] for c in t["controls"]] if t else []
    check("buttons: open, cancel, re-run failed, re-run", ids == ["open", "cancel", "rerun_failed", "rerun"], ids)
s, r = call("GET", "/api/connectors/github")
check("repo without access reported", r.get("repo_status", {}).get("o/missing") == "not found or no access", r)
check("token expiry known (warning ahead)", abs((r.get("token_expires") or 0) - EXP) < 2, r.get("token_expires"))

# ---- failure: job, step, log tail (log storage reached without the token)
with lock:
    r101["status"], r101["conclusion"] = "completed", "failure"
    j = r101["jobs"][1]
    j["status"], j["conclusion"] = "completed", "failure"
    j["steps"][1].update(status="completed", conclusion="failure")
    j["steps"][2].update(status="completed", conclusion="skipped")
t = wait(lambda: (lambda x: x if x and x["state"] == "failed" else None)(task("gh-101")))
check("failed run -> failed task with job › step", t is not None and t["message"] == "test › Run pytest", t and t["message"])
if t:
    logs = [l["msg"] for l in t["log"] if l["level"] == "error"]
    check("log tail in the task (no timestamps, last lines)", any("AssertionError: 2 != 3" in l for l in logs)
          and any(l.startswith("error: Process completed") for l in logs) and not any(re.match(r"^\d{4}-", l) for l in logs)
          and len([l for l in logs if l.startswith("line ")]) <= 30, logs[-5:])
with lock:
    blob = [c for c in calls if c[1].startswith("/logblob/")]
check("log storage fetched without the token", blob and all(c[2] is None for c in blob), blob)

# ---- re-run failed jobs from TaskHub: a new attempt of the same task
s, r = call("POST", "/api/tasks/gh-101/controls/rerun_failed", {})
check("re-run failed -> GitHub called", s == 200 and any(c[0] == "POST" and c[1].endswith("/runs/101/rerun-failed-jobs") for c in calls), (s, r))
t = wait(lambda: (lambda x: x if x and x["state"] == "running" else None)(task("gh-101")))
check("task running again, attempt 2", t is not None and "attempt 2" in t["description"], t and (t["state"], t["description"]))
with lock:
    r101["jobs"] = [{"id": 9003, "name": "test", "status": "completed", "conclusion": "success",
                     "steps": [step("Run pytest", "completed", "success")]}]
    r101["status"], r101["conclusion"] = "completed", "success"
t = wait(lambda: (lambda x: x if x and x["state"] == "done" else None)(task("gh-101")))
check("second attempt succeeded -> done", t is not None, task("gh-101"))

# ---- a new run, cancelled from TaskHub
with lock:
    r102 = mkrun(102, "queued", title="Add feature")
t = wait(lambda: task("gh-102"))
check("new queued run -> task", t is not None and t["state"] == "running" and t.get("status") == "Queued", t and t.get("status"))
s, r = call("POST", "/api/tasks/gh-102/controls/cancel", {})
check("cancel -> GitHub called", s == 200 and any(c[0] == "POST" and c[1].endswith("/runs/102/cancel") for c in calls), (s, r))
with lock:
    r102["status"], r102["conclusion"] = "completed", "cancelled"
t = wait(lambda: (lambda x: x if x and x["state"] == "cancelled" else None)(task("gh-102")))
check("cancelled on GitHub -> cancelled task", t is not None, task("gh-102"))

# ---- a short tag run seen only once finished: done at once, with artifact and release links
with lock:
    mkrun(103, "completed", "success", branch="v1.2.3", title="Release v1.2.3")
t = wait(lambda: (lambda x: x if x and x["state"] == "done" else None)(task("gh-103")))
labels = [l["label"] for l in t["links"]] if t else []
check("finished run -> done with links: GitHub, artifact, release", labels == ["GitHub", "📦 setup-exe", "🏷 TaskHub 1.2.3"], labels)
check("artifact link on the run page", t and t["links"][1]["url"].endswith("/actions/runs/103/artifacts/5"), t and t["links"])

check("polling uses ETags (304 answers)", stats["304"] > 0, stats)
with lock:
    hosts = {c[1].split("/")[1] for c in calls}
check("only the API and its log link were called", hosts <= {"user", "repos", "logblob"}, hosts)

# ---- a run that needs approval (action_required): waiting for you, followed until it completes (2.9)
with lock:
    r104 = mkrun(104, "completed", "action_required", title="PR from a fork")
t = wait(lambda: task("gh-104"))
check("action_required -> running task waiting for approval", t is not None and t["state"] == "running"
      and t.get("attention") == "Waiting for approval on GitHub", t and (t["state"], t.get("attention")))
with lock:
    r104["status"], r104["conclusion"] = "in_progress", None
time.sleep(4)
check("approved -> still followed, attention cleared",
      (lambda x: x and x["state"] == "running" and not x.get("attention"))(wait(lambda: (lambda x: x if x and not x.get("attention") else None)(task("gh-104")))),
      task("gh-104"))
with lock:
    r104["status"], r104["conclusion"] = "completed", "success"
check("approved run completes -> done", wait(lambda: (lambda x: x if x and x["state"] == "done" else None)(task("gh-104"))) is not None,
      task("gh-104"))

# ---- a burst of new runs, more than one page: the next page is fetched too (Link header)
with lock:
    for i in range(30):
        mkrun(200 + i, "completed", "success", title=f"Burst {i}")
t = wait(lambda: task("gh-200") and task("gh-229"), timeout=30)
with lock:
    paged = [c for c in calls if "/actions/runs?" in c[1] and "page=2" in c[1]]
check("30 new runs (pages of 20) -> all shown, page 2 fetched", t is not None and paged, (task("gh-200") is not None, len(paged)))
check("only the API host called for page 2", all(c[2] == "Bearer " + TOKEN for c in paged), paged[:2])

# ---- renamed repository: 301 followed on the API host, the new name reported
s, r = call("POST", "/api/connectors/github", {"repos": ["o/r", "o/old"]})
r = wait(lambda: (lambda x: x if (x.get("repo_status") or {}).get("o/old", "").startswith("repository moved") else None)(
    call("GET", "/api/connectors/github")[1]))
check("301 for a repo -> 'repository moved to o/r'", r is not None and r["repo_status"]["o/old"] == "repository moved to o/r",
      call("GET", "/api/connectors/github")[1].get("repo_status"))
with lock:
    moved = [c for c in calls if c[1].startswith("/repos/o/old/")]
check("redirect followed with the token only to the API host", moved and all(c[2] == "Bearer " + TOKEN for c in calls
      if c[1].startswith("/repos/o/r/actions/runs?")), len(moved))

# ---- secondary rate limit: 403 "rate limit" with requests left -> everything pauses (>= 60 s), not "no access"
s, r = call("POST", "/api/connectors/github", {"repos": ["o/r"]})
with lock:
    faults["secondary"] = 1
r = wait(lambda: (lambda x: x if "rate limit" in (x.get("status") or "") else None)(call("GET", "/api/connectors/github")[1]))
check("secondary rate limit -> paused, said so", r is not None and r["repo_status"].get("o/r") != "no access (Actions: read)", r)
time.sleep(0.5)
n = len(calls)
time.sleep(4)
check("nothing polled while paused", len(calls) == n, calls[n:])
call("POST", "/api/connectors/github", {"enabled": True})   # a restart clears the pause
check("polling again after a restart", wait(lambda: len(calls) > n), len(calls) - n)

# ---- the settings page's row (2.9): never the token; counts in each language's plural forms; off keeps the token
NO_VAULT = vault.probe()
GT = vault.target(DATA, "github")


def row():
    v = call("GET", "/api/connectors?view=settings")[1]
    return json.dumps(v, ensure_ascii=False), next((c for c in v.get("connectors", []) if c["id"] == "github"), None)


txt, rw = row()
check("settings rows never contain the token", rw is not None and TOKEN not in txt and "github_pat_" not in txt, txt[:300])
check("settings row: on, token saved and working, 'Connected as @tester · N repositories'", rw and rw["enabled"] and rw["key_set"]
      and rw["key_check"] == "ok" and rw["summary"].startswith("Connected as @tester") and " repositor" in rw["summary"]
      and rw["help_url"].endswith("/docs/keys/github.md") and rw.get("setup") == "github", rw)
saved = call("GET", "/api/connectors/github")[1]["repos"]
PL = {
    "en": {1: "1 repository", 2: "2 repositories", 5: "5 repositories"},
    "de": {1: "1 Repository", 2: "2 Repositorys"},
    "fr": {1: "1 dépôt", 2: "2 dépôts"},
    "uk": {1: "1 репозиторій", 2: "2 репозиторії", 5: "5 репозиторіїв", 11: "11 репозиторіїв", 12: "12 репозиторіїв",
           21: "21 репозиторій", 22: "22 репозиторії"},
    "pl": {1: "1 repozytorium", 2: "2 repozytoria", 5: "5 repozytoriów", 12: "12 repozytoriów", 21: "21 repozytoriów",
           22: "22 repozytoria"},
    "ja": {1: "1 個のリポジトリ", 5: "5 個のリポジトリ"},
    "zh": {2: "2 个仓库"},
}
for n in sorted({n for d in PL.values() for n in d}):
    call("POST", "/api/connectors/github", {"repos": [f"o/p{i}" for i in range(n)]})
    for lang, d in PL.items():
        if n not in d:
            continue
        call("POST", "/api/settings", {"language": lang})
        txt, rw = row()
        got = rw and rw["summary"]
        check(f"plural: {lang} {n} -> '{d[n]}'", got and re.search(r"(^| · )" + re.escape(d[n]) + r"( · |$)", got), got)
        if lang == "uk" and n == 1:
            check("how-to page in Ukrainian", rw["help_url"].endswith("/docs/keys/github.uk.md"), rw["help_url"])
call("POST", "/api/settings", {"language": "en"})
call("POST", "/api/connectors/github", {"repos": saved})
s, r = call("POST", "/api/connectors/github", {"enabled": False})
check("off keeps the token", s == 200 and r.get("status") == "off" and r.get("token_set") is True
      and (bool(NO_VAULT) or vault.read(GT) == TOKEN), r)
n = len(calls)
s, r = call("POST", "/api/connectors/github", {"enabled": True})
check("on again: polls with the saved token, no re-entry", s == 200 and wait(lambda: any(c[2] == "Bearer " + TOKEN for c in calls[n:])),
      calls[n:][:3])

# ---- off / forget
s, r = call("POST", "/api/connectors/github", {"enabled": False, "forget_token": True})
check("disable + forget token", s == 200 and r.get("status") == "off" and r.get("token_set") is False, r)
n = len(calls)
time.sleep(4)
check("nothing polled when off", len(calls) == n, calls[n:])
for tid in ["gh-101", "gh-102", "gh-103", "gh-104"] + [f"gh-{200 + i}" for i in range(30)]:
    call("DELETE", f"/api/tasks/{tid}?force=1")
# ---- no network when the token is saved (the mock is down): kept, said so
srv.shutdown()
srv.server_close()
s, r = call("POST", "/api/connectors/github", {"token": TOKEN})
check("no network on save -> token saved, 'Couldn't reach GitHub'", s == 200 and r.get("token_check") == "network"
      and "Couldn't reach GitHub" in (r.get("token_check_text") or "") and r.get("token_set") is True, (s, r))
s, r = call("POST", "/api/connectors/github", {"forget_token": True})
check("remove deletes the token", s == 200 and r.get("token_set") is False and r.get("token_check") is None
      and (bool(NO_VAULT) or not vault.exists(GT)), r)
print(f"{fails} failure(s)")
raise SystemExit(1 if fails else 0)
