"""GitHub pull requests, read-only (connectors.github pr_reviews / pr_dependabot) against a mock GitHub API on 127.0.0.1:47896
(the test hub's config.json points connectors.github.api there; run after github_test.py, which uses the same port).
Review requests come from the search API (review-requested:<login>), Dependabot PRs from each watched repository's open PR
list; each is a task with a link and a notification; when it leaves the list the task ends with what happened (reviewed /
merged / closed); a search rate limit pauses only the PR searches; nothing is ever written to GitHub."""
import hashlib, json, os, threading, time, urllib.error, urllib.parse, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
PORT = 47896
TOKEN = "github_pat_" + "P" * 60
fails = 0
lock = threading.RLock()
calls = []   # (method, path)
H = f"http://127.0.0.1:{PORT}"


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


def pr(repo, n, title, author, draft=False):
    return {"id": hash((repo, n)) & 0xFFFFFFF, "number": n, "title": title, "user": {"login": author}, "draft": draft,
            "html_url": f"{H}/{repo}/pull/{n}", "repository_url": f"{H}/repos/{repo}", "pull_request": {"url": "x"},
            "base": {"repo": {"full_name": repo}}, "state": "open", "merged_at": None}


state = {
    "reviews": [pr("o/x", 5, "Add retry to the uploader", "bob")],
    "pulls": {"o/r": [pr("o/r", 12, "Bump requests from 2.31.0 to 2.32.3", "dependabot[bot]"),
                      pr("o/r", 13, "Refactor the parser", "carol")]},
    "closed": {},          # "repo#n" -> {"state": "closed", "merged_at": ...}
    "search_limited": 0,   # next N searches: 403 rate limit of the search bucket
}


class Api(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def end_headers(self):
        self.send_header("Connection", "close")
        super().end_headers()

    def reply(self, code, obj=None, headers=None):
        data = json.dumps(obj).encode() if obj is not None else b""
        etag = '"' + hashlib.md5(data).hexdigest() + '"'
        if code == 200 and self.headers.get("If-None-Match") == etag:
            self.send_response(304)
            self.send_header("ETag", etag)
            self.end_headers()
            return
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("ETag", etag)
        for k, v in (headers or {"x-ratelimit-resource": "core", "x-ratelimit-remaining": "4990",
                                  "x-ratelimit-reset": str(int(time.time()) + 3600)}).items():
            self.send_header(k, v)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self.route("GET")

    def do_POST(self):
        self.route("POST")

    def do_PUT(self):
        self.route("PUT")

    def do_PATCH(self):
        self.route("PATCH")

    def do_DELETE(self):
        self.route("DELETE")

    def route(self, m):
        with lock:
            calls.append((m, self.path))
        if self.headers.get("Authorization") != "Bearer " + TOKEN:
            return self.reply(401, {"message": "Bad credentials"})
        u = urllib.parse.urlparse(self.path)
        p, q = u.path, urllib.parse.parse_qs(u.query)
        if m != "GET":
            return self.reply(405, {"message": "read-only test"})
        if p == "/user":
            return self.reply(200, {"login": "tester"})
        if p == "/search/issues":
            with lock:
                if state["search_limited"] > 0:
                    state["search_limited"] -= 1
                    return self.reply(403, {"message": "API rate limit exceeded for user."},
                                      {"x-ratelimit-resource": "search", "x-ratelimit-remaining": "0",
                                       "x-ratelimit-reset": str(int(time.time()) + 4)})
                items = list(state["reviews"]) if "review-requested:tester" in q.get("q", [""])[0] else []
            return self.reply(200, {"total_count": len(items), "incomplete_results": False, "items": items},
                              {"x-ratelimit-resource": "search", "x-ratelimit-remaining": "29",
                               "x-ratelimit-reset": str(int(time.time()) + 60)})
        if p.endswith("/actions/runs"):
            return self.reply(200, {"total_count": 0, "workflow_runs": []})
        parts = p.strip("/").split("/")
        if len(parts) == 4 and parts[0] == "repos" and parts[3] == "pulls":
            with lock:
                return self.reply(200, list(state["pulls"].get(parts[1] + "/" + parts[2], [])))
        if len(parts) == 5 and parts[0] == "repos" and parts[3] == "pulls":
            key = parts[1] + "/" + parts[2] + "#" + parts[4]
            with lock:
                st = state["closed"].get(key, {"state": "open", "merged_at": None})
            return self.reply(200, {"number": int(parts[4]), **st})
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


def tid(repo, n):
    return "ghpr-" + hashlib.sha256(f"{repo.lower()}#{n}".encode()).hexdigest()[:12]


def task(t):
    s, r = call("GET", f"/api/tasks/{t}")
    return r if s == 200 else None


def wait(pred, timeout=20):
    end = time.time() + timeout
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(0.3)
    return None


def n_calls(pred, start=0):
    with lock:
        return len([c for c in calls[start:] if pred(c)])


ThreadingHTTPServer.request_queue_size = 64
srv = ThreadingHTTPServer(("127.0.0.1", PORT), Api)
threading.Thread(target=srv.serve_forever, daemon=True).start()
call("POST", "/api/settings", {"language": "en"})

REV, DEP, HUMAN = tid("o/x", 5), tid("o/r", 12), tid("o/r", 13)
for t in (REV, DEP, HUMAN, tid("o/r", 14), tid("o/x", 6)):
    call("DELETE", f"/api/tasks/{t}?force=1")

# ---- off by default: Actions only, no searches, no PR lists
call("POST", "/api/connectors/github", {"forget_token": True, "enabled": False})
s, r = call("POST", "/api/connectors/github", {"token": TOKEN, "repos": ["o/r"], "interval": 2, "enabled": True,
                                                "pr_reviews": False, "pr_dependabot": False})
check("PRs off by default (both false)", s == 200 and r.get("pr_reviews") is False and r.get("pr_dependabot") is False, (s, r))
time.sleep(5)
check("off: no search, no PR list", n_calls(lambda c: c[1].startswith("/search/") or "/pulls" in c[1]) == 0, calls[-5:])

# ---- on: a review request and a Dependabot PR become tasks; a person's PR in the repo is not shown
n0 = len(calls)
s, r = call("POST", "/api/connectors/github", {"pr_reviews": True, "pr_dependabot": True})
check("PRs on", s == 200 and r.get("pr_reviews") is True and r.get("pr_dependabot") is True, (s, r))
t = wait(lambda: task(REV))
check("review request -> task 'Review · Add retry to the uploader'", t and t["state"] == "running" and t["source"] == "github"
      and t["name"] == "Review · Add retry to the uploader", t)
if t:
    check("…description 'o/x #5 · @bob', project 'x', status and attention", t["description"] == "o/x #5 · @bob" and t["project"] == "x"
          and t.get("status") == "Waiting for your review" and t.get("attention") == "Your review is requested", t)
    check("…link and Open on GitHub button, no other buttons", any(l.get("url") == f"{H}/o/x/pull/5" for l in t.get("links") or [])
          and [c["id"] for c in t.get("controls") or []] == ["open"], (t.get("links"), t.get("controls")))
    check("…logged 'Review requested: o/x #5'", any("Review requested: o/x #5" in x.get("msg", "") for x in t.get("log") or []), t.get("log"))
d = wait(lambda: task(DEP))
check("Dependabot PR -> task 'Dependabot · Bump requests …', no attention", d and d["name"].startswith("Dependabot · Bump requests")
      and not d.get("attention"), d)
check("a person's PR in the repo is not shown", task(HUMAN) is None)
with lock:
    sq = [c[1] for c in calls[n0:] if c[1].startswith("/search/issues")]
check("search asks review-requested:<login> (not @me)", sq and all("review-requested%3Atester" in x for x in sq), sq[:2])

# ---- a Dependabot PR that also asks for my review: one task, a review request
with lock:
    dep14 = pr("o/r", 14, "Bump urllib3 from 1.26 to 2.2", "dependabot[bot]")
    state["pulls"]["o/r"].append(dep14)
    state["reviews"].append(dep14)
t14 = wait(lambda: task(tid("o/r", 14)))
check("Dependabot PR asking for my review -> one task, 'Review · …'", t14 and t14["name"].startswith("Review · Bump urllib3"), t14)

# ---- the review request goes away (reviewed), the PR still open -> 'No longer waiting for your review'
with lock:
    state["reviews"] = [x for x in state["reviews"] if x["number"] != 5]
t = wait(lambda: (task(REV) or {}).get("state") == "done" and task(REV))
check("reviewed -> done, 'No longer waiting for your review'", t and t.get("message") == "No longer waiting for your review", t)

# ---- the Dependabot PR is merged -> 'Merged'
with lock:
    state["pulls"]["o/r"] = [x for x in state["pulls"]["o/r"] if x["number"] != 12]
    state["closed"]["o/r#12"] = {"state": "closed", "merged_at": "2026-11-01T10:00:00Z"}
d = wait(lambda: (task(DEP) or {}).get("state") == "done" and task(DEP))
check("Dependabot PR merged -> done, 'Merged'", d and d.get("message") == "Merged", d)

# ---- a search rate limit pauses only the searches: open tasks stay, Actions polling goes on
with lock:
    state["search_limited"] = 1
n1 = len(calls)
time.sleep(6)
check("search rate limit: the review task stays open", (task(tid("o/r", 14)) or {}).get("state") == "running", task(tid("o/r", 14)))
check("…Actions polling goes on", n_calls(lambda c: c[1].endswith("/actions/runs?per_page=100"), n1) >= 1, calls[n1:][:6])
s, r = call("GET", "/api/connectors/github")
check("…the connector itself is not paused", r.get("status") == "ok", r.get("status"))

# ---- pr_reviews off: review tasks end; one that is also an open Dependabot PR stays, as a Dependabot PR
with lock:
    state["reviews"].append(pr("o/x", 6, "Speed up the cache", "bob"))
R6 = tid("o/x", 6)
check("another review request -> task", wait(lambda: task(R6)) is not None)
s, r = call("POST", "/api/connectors/github", {"pr_reviews": False})
t6 = wait(lambda: (task(R6) or {}).get("state") == "done" and task(R6))
check("pr_reviews off -> review task ends 'Pull requests turned off in TaskHub'", t6 and t6.get("message") == "Pull requests turned off in TaskHub", t6)
t14 = task(tid("o/r", 14))
check("…the review request that is also Dependabot's stays open, now 'Dependabot · …'", t14 and t14["state"] == "running"
      and t14["name"].startswith("Dependabot · Bump urllib3") and t14.get("status") == "Open" and not t14.get("attention"), t14)

# ---- the connector turned off: every PR task ends
s, r = call("POST", "/api/connectors/github", {"enabled": False})
d = wait(lambda: (task(tid("o/r", 14)) or {}).get("state") == "done" and task(tid("o/r", 14)))
check("connector off -> PR tasks end", d and d.get("message") == "Pull requests turned off in TaskHub", d)

# ---- read-only: nothing but GET ever reached GitHub
with lock:
    writes = [c for c in calls if c[0] != "GET"]
check("read-only: only GET requests", writes == [], writes[:5])

call("POST", "/api/connectors/github", {"forget_token": True, "pr_reviews": False, "pr_dependabot": False, "repos": []})
for t in (REV, DEP, HUMAN, tid("o/r", 14), tid("o/x", 6)):
    call("DELETE", f"/api/tasks/{t}?force=1")
srv.shutdown()
srv.server_close()
print(f"{fails} failure(s)")
raise SystemExit(1 if fails else 0)
