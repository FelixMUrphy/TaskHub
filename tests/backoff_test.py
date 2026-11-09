"""Telegram's long poll waits after an answer it can't use, and GitHub's memory of runs doesn't bring old runs back as new. Own hub, own mocks. Argument: TaskHubHost.exe."""
import hashlib, json, os, re, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

EXE = sys.argv[1]
PORT, TG, GH = 47863, 47862, 47861
URL = f"http://127.0.0.1:{PORT}"
TG_TOKEN = "123456789:" + "A" * 35
GH_TOKEN = "github_pat_" + "B" * 60
REPOS = 35
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
lock = threading.Lock()
fault = {"code": None, "retry_after": None}
polls = []                      # times of getUpdates
runs = {}                       # repo -> {id: run}


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None, timeout=30):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(), headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=timeout) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


class Base(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"

    def log_message(self, *a):
        pass

    def end_headers(self):
        self.send_header("Connection", "close")   # see telegram_test.py: an HTTP/1.0 mock must close
        super().end_headers()

    def out(self, code, obj, headers=None):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class Bot(Base):
    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        method = self.path.rsplit("/", 1)[-1]
        if method == "getMe":
            return self.out(200, {"ok": True, "result": {"id": 1, "is_bot": True, "username": "test_bot"}})
        if method == "getUpdates":
            if json.loads(raw or b"{}").get("offset") == -1:
                return self.out(200, {"ok": True, "result": []})
            with lock:
                polls.append(time.time())
                code, after = fault["code"], fault["retry_after"]
            if code:
                body = {"ok": False, "error_code": code, "description": "fault"}
                if after:
                    body["parameters"] = {"retry_after": after}
                return self.out(code, body)
            time.sleep(.5)
            return self.out(200, {"ok": True, "result": []})
        self.out(200, {"ok": True, "result": {"message_id": 1}})


def mkrun(rid, repo, status="completed", concl="failure", attempt=1):
    return {"id": rid, "name": "CI", "display_title": f"run {rid}", "head_branch": "main", "head_sha": "abc1234def", "event": "push", "status": status, "conclusion": concl,
            "workflow_id": 7, "run_attempt": attempt, "actor": {"login": "a"}, "html_url": f"http://127.0.0.1:{GH}/{repo}/actions/runs/{rid}",
            "created_at": "2026-11-01T10:00:00Z", "run_started_at": "2026-11-01T10:00:00Z", "updated_at": "2026-11-01T10:05:00Z"}


class Api(Base):
    def do_GET(self):
        p = self.path.split("?")[0]
        limits = {"x-ratelimit-remaining": "4990", "x-ratelimit-reset": str(int(time.time()) + 3600)}
        if p == "/user":
            return self.out(200, {"login": "tester"}, limits)
        m = re.match(r"^/repos/(o/r\d+)/actions/runs$", p)
        if m:
            with lock:
                page = sorted(runs.get(m.group(1), {}).values(), key=lambda x: -x["id"])[:100]
            body = {"total_count": len(page), "workflow_runs": page}
            etag = '"' + hashlib.md5(json.dumps(body).encode()).hexdigest() + '"'
            if self.headers.get("If-None-Match") == etag:
                self.send_response(304)
                self.send_header("ETag", etag)
                self.end_headers()
                return
            return self.out(200, body, dict(limits, ETag=etag))
        m = re.match(r"^/repos/(o/r\d+)/actions/runs/(\d+)$", p)
        if m:
            with lock:
                run = runs.get(m.group(1), {}).get(int(m.group(2)))
            return self.out(200 if run else 404, run or {"message": "Not Found"}, limits)
        if p.endswith("/jobs"):
            return self.out(200, {"total_count": 0, "jobs": []}, limits)
        if p.endswith("/artifacts"):
            return self.out(200, {"total_count": 0, "artifacts": []}, limits)
        if "/workflows/" in p:
            return self.out(200, {"total_count": 0, "workflow_runs": []}, limits)
        self.out(404, {"message": "Not Found"}, limits)


def serve(handler, port):
    s = ThreadingHTTPServer(("127.0.0.1", port), handler)
    threading.Thread(target=s.serve_forever, daemon=True).start()
    return s


def wait(cond, secs):
    end = time.time() + secs
    while time.time() < end:
        if cond():
            return True
        time.sleep(.1)
    return cond()


servers = [serve(Bot, TG), serve(Api, GH)]
data = tempfile.mkdtemp(prefix="taskhub-backoff-data-")
home = tempfile.mkdtemp(prefix="taskhub-backoff-home-")
with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"toasts": False, "autostart": False, "update_check": False, "toast_rate": 0, "toast_task_gap": 0, "language": "en", "log_level": "debug", "keep_finished": 100000,
               "telegram_api": f"http://127.0.0.1:{TG}", "connectors": {"github": {"api": f"http://127.0.0.1:{GH}"}}}, f)
hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=dict(os.environ, TASKHUB_TEST_HOME=home))
try:
    for _ in range(60):
        try:
            call("GET", "/api/health", timeout=2)
            break
        except OSError:
            time.sleep(.5)

    # ---- Telegram
    status = lambda: call("GET", "/api/telegram")[1].get("status", "")
    call("POST", "/api/telegram", {"token": TG_TOKEN})
    check("telegram: polling", wait(lambda: len(polls) >= 2, 15), len(polls))

    def rate(code, after=None, secs=6):
        with lock:
            fault["code"], fault["retry_after"] = code, after
        time.sleep(.7)   # the poll in flight ends
        with lock:
            polls.clear()
        time.sleep(secs)
        with lock:
            return list(polls)

    got = rate(429, 4, 9)   # first: with no back-off built up, the waits are retry_after's own
    gaps = [b - a for a, b in zip(got, got[1:])]
    check("telegram: 429 honours retry_after", 2 <= len(got) <= 3 and all(g >= 3.9 for g in gaps), (len(got), gaps))
    for code in (500, 400, 503):
        got = rate(code)
        check(f"telegram: HTTP {code} doesn't hot-loop", len(got) <= 4, f"{len(got)} getUpdates in 6 s")
        check(f"telegram: HTTP {code} isn't called connected", status().startswith("offline"), status())
    with lock:
        fault["code"] = None
    check("telegram: recovers after the faults", wait(lambda: not status().startswith("offline"), 40), status())
    with open(os.path.join(data, "taskhub.log"), encoding="utf-8", errors="replace") as f:
        logged = f.read()
    check("telegram: the failure is in the log", re.search(r"telegram.*HTTP (500|400|503|429)", logged) is not None)
    call("POST", "/api/telegram", {"disconnect": True})

    # ---- GitHub: more runs than the old global memory kept, quiet repositories among them
    names = [f"o/r{i}" for i in range(REPOS)]
    with lock:
        for i, repo in enumerate(names):
            runs[repo] = {i * 1000 + k: mkrun(i * 1000 + k, repo) for k in range(1, 101)}
    code, _ = call("POST", "/api/connectors/github", {"enabled": True, "repos": names, "interval": 2, "token": GH_TOKEN, "notify_success": False})
    check("github: configured", code == 200, code)
    tasks = lambda: [t for t in call("GET", "/api/tasks")[1]["tasks"] if t.get("source") == "github"]
    conn = lambda: call("GET", "/api/connectors/github")[1]
    wait(lambda: len(conn().get("repo_status") or {}) >= REPOS and all(v == "ok" for v in conn()["repo_status"].values()), 60)
    time.sleep(5)
    check("github: the first look adds no finished runs", len(tasks()) == 0, len(tasks()))
    with lock:
        runs["o/r0"][900000] = mkrun(900000, "o/r0")
    wait(lambda: any("900000" in t["id"] for t in tasks()), 45)
    time.sleep(8)
    got = tasks()
    check("github: one new run in a quiet repository is one task", len(got) == 1 and "900000" in got[0]["id"], (len(got), [t["id"] for t in got][:5]))
    with lock:
        runs["o/r0"][50] = mkrun(50, "o/r0", "in_progress", None, 2)
    check("github: a re-run of an old run is still followed", wait(lambda: any(t["id"].endswith("50") and t["state"] == "running" for t in tasks()), 45), [t["id"] for t in tasks()][:5])
    call("POST", "/api/connectors/github", {"enabled": False, "forget_token": True})
finally:
    hub.kill()
    hub.wait()
    for s in servers:
        s.shutdown()
    time.sleep(.5)
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(home, ignore_errors=True)

print("BACKOFF:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
