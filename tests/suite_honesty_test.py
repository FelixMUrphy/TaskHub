"""The test run itself can fail: parallel_test exits 1 when it finds lost updates, and run_summary fails a run on an unexpected ERR line or on money checks skipped under CI."""
import json, os, shutil, subprocess, sys, tempfile, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import run_summary

PORT = 47864
HERE = os.path.dirname(os.path.abspath(__file__))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


class LosingHub(BaseHTTPRequestHandler):
    """Answers like a hub that takes everything and then has lost the log and the chart."""
    protocol_version = "HTTP/1.0"

    def log_message(self, *a):
        pass

    def reply(self, obj):
        data = json.dumps(obj).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length") or 0))
        self.reply({"ok": True})

    def do_GET(self):
        self.reply({"tasks": [{"id": f"par-{i}", "state": "done", "log": [], "current": 0, "widgets": [{"id": "c", "points": []}]} for i in range(2)]})


srv = ThreadingHTTPServer(("127.0.0.1", PORT), LosingHub)
threading.Thread(target=srv.serve_forever, daemon=True).start()
r = subprocess.run([sys.executable, os.path.join(HERE, "parallel_test.py"), "2"], capture_output=True, text=True, encoding="utf-8", errors="replace",
                   env=dict(os.environ, TASKHUB_URL=f"http://127.0.0.1:{PORT}", PYTHONUTF8="1"), timeout=120)
srv.shutdown()
check("parallel_test: lost updates are reported", "RESULT: FAIL" in r.stdout, r.stdout[-300:] + r.stderr[-300:])
check("parallel_test: lost updates fail the run (exit 1)", r.returncode == 1, r.returncode)

folder = tempfile.mkdtemp(prefix="taskhub-honesty-")
data = os.path.join(folder, "data")
os.makedirs(data)
try:
    def write(name, text):
        with open(os.path.join(folder, name), "w", encoding="utf-8") as f:
            f.write(text)

    def log(text):
        with open(os.path.join(data, "taskhub.log"), "w", encoding="utf-8") as f:
            f.write(text)

    write("totals.txt", "3 1 0 0 a_test.py\n")
    write("skips.txt", "")
    log("")
    check("summary: a FAIL line in a script that exited 0 fails", run_summary.main(folder, data, False) == 1)
    write("totals.txt", "3 0 1 0 a_test.py\n")
    write("skips.txt", "a_test.py: SKIP x (Credential Manager unavailable: no)\n")
    log("2026-11-04 01:00:00.000 INF hub started\n2026-11-04 01:00:01.000 WRN hub a warning\n")
    check("summary: a clean run passes", run_summary.main(folder, data, False) == 0)
    check("summary: the same skip under CI fails", run_summary.main(folder, data, True) == 1)
    write("skips.txt", "a_test.py: SKIP kill failure: the hub could kill the protected process\n")
    check("summary: another skip under CI passes", run_summary.main(folder, data, True) == 0)
    log("2026-11-04 01:00:00.000 ERR hub watchdog error\n")
    check("summary: an ERR line fails", run_summary.main(folder, data, False) == 1)
    log("2026-11-04 01:00:00.000 INF hub the text ERR later in a line\n")
    check("summary: ERR only as the level", run_summary.main(folder, data, False) == 0)
finally:
    shutil.rmtree(folder, ignore_errors=True)

print("SUITE HONESTY:", "ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
