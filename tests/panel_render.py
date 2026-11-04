"""The panel's page in a hidden Edge: an isolated hub (47898, temp data, test home, no tray), its /ui opened in headless Edge with a temp profile, driven
through CDP (9339); demo tasks over the hub's API. For each language: a screenshot and checks (no script errors, no sideways scroll, no raw text keys,
the tasks are shown). What the host adds (window, AppBar, slides) isn't here: tests/settings_window_check.py covers that. By hand, not in run_tests.
Nothing shows on the screen, no keystrokes. Usage: python -X utf8 panel_render.py <TaskHubHost.exe> [--all | --lang en,uk] [--width 420] [--height 900] [--out FOLDER]"""
import argparse, base64, json, os, re, shutil, socket, struct, subprocess, sys, tempfile, time, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
LANGS = ["en", "uk", "es", "de", "fr", "it", "pt", "pl", "zh", "ja"]
PORT, CDP = 47898, 9339
EDGE = os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), "Microsoft", "Edge", "Application", "msedge.exe")
BASE = f"http://127.0.0.1:{PORT}"
os.environ.setdefault("TASKHUB_TOKEN", "panel-render-token")   # the hub reads it; sitecustomize.py isn't there by hand, see the requests below
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, ok, info=None):
    global fails
    print(("ok   " if ok else "FAIL ") + name + ("" if ok else f"  -> {info!r}"), flush=True)
    fails += 0 if ok else 1
    return ok


def call(method, path, body=None):
    req = urllib.request.Request(BASE + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                 headers={"X-TaskHub": "1", "Content-Type": "application/json", "Authorization": "Bearer " + os.environ["TASKHUB_TOKEN"]})
    with op.open(req, timeout=20) as r:
        return json.loads(r.read() or b"null")


def get_json(url):
    with op.open(url, timeout=5) as r:
        return json.loads(r.read())


class WS:
    """The little of the DevTools protocol this needs; script errors are kept as they arrive."""

    def __init__(self, url):
        u = urllib.parse.urlparse(url)
        self.s = socket.create_connection((u.hostname, u.port), timeout=30)
        key = base64.b64encode(os.urandom(16)).decode()
        self.s.sendall((f"GET {u.path} HTTP/1.1\r\nHost: {u.hostname}:{u.port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                        f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
        buf = b""
        while b"\r\n\r\n" not in buf:
            buf += self.s.recv(4096)
        self.n = 0
        self.errors = []

    def _read(self, n):
        b = b""
        while len(b) < n:
            b += self.s.recv(n - len(b))
        return b

    def send(self, method, params=None):
        self.n += 1
        p = json.dumps({"id": self.n, "method": method, "params": params or {}}).encode()
        mask = os.urandom(4)
        head = b"\x81" + (bytes([0x80 | len(p)]) if len(p) < 126 else bytes([0x80 | 126]) + struct.pack(">H", len(p)))
        self.s.sendall(head + mask + bytes(c ^ mask[i % 4] for i, c in enumerate(p)))
        while True:
            h = self._read(2)
            ln = h[1] & 0x7f
            if ln == 126:
                ln = struct.unpack(">H", self._read(2))[0]
            elif ln == 127:
                ln = struct.unpack(">Q", self._read(8))[0]
            m = json.loads(self._read(ln))
            if m.get("method") == "Runtime.exceptionThrown":
                self.errors.append(m["params"]["exceptionDetails"].get("text"))
            if m.get("id") == self.n:
                if "error" in m:
                    raise RuntimeError(m["error"])
                return m["result"]

    def eval(self, expr):
        r = self.send("Runtime.evaluate", {"expression": expr, "returnByValue": True})
        if "exceptionDetails" in r:
            raise RuntimeError(r["exceptionDetails"])
        return r["result"].get("value")


def until(cond, timeout=30, every=0.3):
    end = time.time() + timeout
    while time.time() < end:
        try:
            v = cond()
            if v:
                return v
        except (OSError, ValueError, RuntimeError):
            pass
        time.sleep(every)
    return None


def text_keys():
    with open(os.path.join(HERE, "..", "i18n", "panel.json"), encoding="utf-8") as f:
        d = json.load(f)
    out = set()
    for v in d.values():
        if isinstance(v, dict):
            out.update(k for k in v if "_" in k)
    return out


def render(exe, lang, a, keys):
    data = tempfile.mkdtemp(prefix="taskhub-render-")
    home = tempfile.mkdtemp(prefix="taskhub-renderhome-")
    with open(os.path.join(data, "config.json"), "w") as f:
        json.dump({"toasts": False, "autostart": False, "update_check": False, "language": lang}, f)
    env = dict(os.environ, TASKHUB_TEST_HOME=home)
    env.pop("TASKHUB_URL", None)
    hub = subprocess.Popen([exe, "--port", str(PORT), "--data", data, "--no-tray"], env=env)
    profile = tempfile.mkdtemp(prefix="taskhub-render-edge-")
    edge = None
    ws = None
    try:
        if not check(f"{lang}: hub started", until(lambda: call("GET", "/api/health").get("ok"))):
            return
        call("POST", "/api/tasks", {"id": "demo-render", "name": "Video render", "total": 240, "unit": "frames"})
        call("POST", "/api/tasks/demo-render/update", {"current": 96, "status": "frame 96 of 240"})
        call("POST", "/api/tasks", {"id": "demo-build", "name": "Release build", "status": "linking"})
        call("POST", "/api/tasks", {"id": "demo-backup", "name": "Nightly backup"})
        call("POST", "/api/tasks/demo-backup/finish", {"state": "done", "message": "42 GB copied"})
        edge = subprocess.Popen([EDGE, "--headless=new", "--disable-gpu", f"--remote-debugging-port={CDP}", f"--user-data-dir={profile}", "--no-first-run",
                                 "--disable-extensions", f"--window-size={a.width},{a.height}", "about:blank"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        page = until(lambda: next((t for t in get_json(f"http://127.0.0.1:{CDP}/json") if t.get("type") == "page" and t.get("webSocketDebuggerUrl")), None), 45)
        if not check(f"{lang}: the page can be reached", page):
            return
        ws = WS(page["webSocketDebuggerUrl"])
        ws.send("Runtime.enable")
        ws.send("Page.enable")
        ws.send("Emulation.setDeviceMetricsOverride", {"width": a.width, "height": a.height, "deviceScaleFactor": 1, "mobile": False})
        ws.send("Page.navigate", {"url": BASE + "/ui#token=" + os.environ["TASKHUB_TOKEN"]})
        seen = until(lambda: ws.eval('document.readyState === "complete" && document.body.innerText.includes("Video render")'))
        check(f"{lang}: the tasks are shown", seen, ws.eval("document.body.innerText.slice(0, 300)"))
        time.sleep(0.7)
        shot = ws.send("Page.captureScreenshot", {"format": "png", "fromSurface": True})["data"]
        path = os.path.join(a.out, f"panel-{lang}.png")
        with open(path, "wb") as f:
            f.write(base64.b64decode(shot))
        print("     shot", path, flush=True)
        check(f"{lang}: a real picture", len(base64.b64decode(shot)) > 5000, len(shot))
        wide = ws.eval("[document.scrollingElement.scrollWidth, document.scrollingElement.clientWidth]")
        check(f"{lang}: no sideways scroll", wide[0] <= wide[1], wide)
        words = set(re.findall(r"[a-z0-9]+(?:_[a-z0-9]+)+", ws.eval("document.body.innerText")))
        check(f"{lang}: no raw text keys", not (words & keys), sorted(words & keys))
        check(f"{lang}: no script errors", not ws.errors, ws.errors)
        ws.eval('sessionStorage.clear(); ""')
        ws.send("Page.navigate", {"url": BASE + "/ui"})
        until(lambda: ws.eval('document.readyState === "complete" && typeof api === "function"'))
        ws.eval('window.__no = null; api("POST", "/api/notify", {}).then(() => window.__no = ["ok"], e => window.__no = [e.message, T("no_token")]); ""')
        no = until(lambda: ws.eval("window.__no"))
        check(f"{lang}: no token: a button's error is the translated line", no and len(no) == 2 and no[0] == no[1], no)
    finally:
        if ws:
            ws.s.close()
        hub.kill()
        hub.wait(10)
        if edge:
            edge.kill()
            edge.wait(10)
        # only this run's Edge: its temp profile is on the command line
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | Where-Object { $_.CommandLine -like '*" + os.path.basename(profile) +
                        "*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"], capture_output=True, timeout=60)
        time.sleep(0.5)
        shutil.rmtree(profile, ignore_errors=True)
        shutil.rmtree(data, ignore_errors=True)
        shutil.rmtree(home, ignore_errors=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("exe")
    p.add_argument("--lang", default="en,uk")
    p.add_argument("--all", action="store_true")
    p.add_argument("--width", type=int, default=420)
    p.add_argument("--height", type=int, default=900)
    p.add_argument("--out", default=None)
    a = p.parse_args()
    a.out = a.out or tempfile.mkdtemp(prefix="taskhub-render-shots-")
    os.makedirs(a.out, exist_ok=True)
    keys = text_keys()
    for lang in LANGS if a.all else a.lang.split(","):
        render(os.path.abspath(a.exe), lang.strip(), a, keys)
    print("PANEL RENDER " + ("PASSED" if not fails else f"FAILED ({fails})"), "- pictures in", a.out)
    raise SystemExit(1 if fails else 0)


if __name__ == "__main__":
    main()
