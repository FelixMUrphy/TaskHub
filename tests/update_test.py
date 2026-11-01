"""Update check: a version found earlier must not stay on the settings page's "Update to X" after a newer one is out.
The hub keeps running while the panel is hidden, so the settings page re-checks quietly when the last check is older than
15 minutes (only with automatic checks on), and "Update to X" always opens the newest release page.
Runs Updater against a local mock of the GitHub releases API (TaskHubHost --selftest-update, only with TASKHUB_TEST_HOME);
nothing goes to GitHub.
Usage: update_test.py PATH\\TaskHubHost.exe"""
import json, os, subprocess, sys, tempfile, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app", "TaskHubHost.exe")
PORT = 47888
fails = 0
state = {"seq": ["v99.0.0"], "hits": 0}   # the tags the mock answers, in order; the last one repeats


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


class Mock(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        state["hits"] += 1
        tag = state["seq"].pop(0) if len(state["seq"]) > 1 else state["seq"][0]
        b = json.dumps({"tag_name": tag, "html_url": "https://github.com/FelixMUrphy/TaskHub/releases/tag/" + tag}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(b)


srv = ThreadingHTTPServer(("127.0.0.1", PORT), Mock)
threading.Thread(target=srv.serve_forever, daemon=True).start()
home = tempfile.mkdtemp(prefix="taskhub-upd-home-")
data = tempfile.mkdtemp(prefix="taskhub-upd-data-")
env = dict(os.environ, TASKHUB_TEST_HOME=home)


def run(steps):
    r = subprocess.run([EXE, "--selftest-update", f"http://127.0.0.1:{PORT}/repos/x/releases/latest", "--data", data],
                       input="\n".join(steps), env=env, capture_output=True, text=True, timeout=120)
    return r.returncode, [l.strip() for l in r.stdout.splitlines() if l.strip()]


try:
    # 1) a manual check finds 99.0.0
    rc, out = run(["manual"])
    check("manual check finds the release", rc == 0 and out[-1] == "available 99.0.0", (rc, out))
    # 2) a newer release, the page refreshed at once: the check is fresh, no request (rate limits, no load)
    state["seq"] = ["v99.0.0", "v99.0.1"]; hits = state["hits"]
    rc, out = run(["manual", "recheck"])
    check("fresh check: no re-check", out[-2:] == ["recheck skipped", "available 99.0.0"] and state["hits"] == hits + 1, (out, state))
    # 3) the user's bug: 99.0.0 offered, the panel hidden for a while (hub running), 99.0.2 released -> the open page
    #    re-checks quietly and offers the newest
    state["seq"] = ["v99.0.0", "v99.0.2"]
    rc, out = run(["manual", "age", "recheck"])
    check("stale offer: the open page re-checks and shows the newest", out[-2:] == ["recheck ran", "available 99.0.2"], out)
    # 4) the latest release is not newer than the running version: the offer goes
    state["seq"] = ["v99.0.0", "v0.0.1"]
    rc, out = run(["manual", "age", "recheck"])
    check("not newer: the offer goes", out[-1] == "available none", out)
    # 5) "Update to X" pressed: the next refresh re-checks even though the check is fresh
    state["seq"] = ["v99.0.0", "v99.0.3"]
    rc, out = run(["manual", "recheck", "open", "recheck"])
    check("after Update to X: re-checked", out[-2:] == ["recheck ran", "available 99.0.3"], out)
    # 6) automatic checks off: the page never goes online on its own
    hits = state["hits"]
    rc, out = run(["auto-off", "age", "recheck"])
    check("automatic checks off: no quiet re-check", "recheck skipped" in out and state["hits"] == hits, (out, state))
finally:
    srv.shutdown()
print(f"{fails} failure(s)")
raise SystemExit(1 if fails else 0)
