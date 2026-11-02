"""Measures what is really on screen during the panel's slides: paints the panel magenta and settings cyan on an isolated hub (47898), then reads
rows of pixels from the screen (GDI BitBlt, read only) as fast as it can. Counts frames with colour where it must not be.
Argument: the exe. No keystrokes: everything through CDP and the HTTP API of the isolated hub."""
import base64, ctypes, ctypes.wintypes as W, json, os, shutil, socket, struct, subprocess, sys, tempfile, threading, time, urllib.request, urllib.parse

EXE = sys.argv[1]
BASE = "http://127.0.0.1:47898"
CDP = 9338
data = tempfile.mkdtemp(prefix="taskhub-slide-")
home = tempfile.mkdtemp(prefix="taskhub-slidehome-")
json.dump({"toasts": False, "autostart": False, "update_check": False, "language": "uk", "panel_autohide": False}, open(os.path.join(data, "config.json"), "w"))
env = dict(os.environ, TASKHUB_TEST_HOME=home, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=f"--remote-debugging-port={CDP}")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
user32, gdi32 = ctypes.windll.user32, ctypes.windll.gdi32
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
user32.GetWindowRect.argtypes = [W.HWND, ctypes.POINTER(W.RECT)]
for f in ("GetDC", "CreateCompatibleDC", "CreateCompatibleBitmap", "SelectObject"):
    pass
user32.GetDC.restype = W.HDC
user32.GetDC.argtypes = [W.HWND]
gdi32.CreateCompatibleDC.restype = W.HDC
gdi32.CreateCompatibleDC.argtypes = [W.HDC]
gdi32.CreateCompatibleBitmap.restype = W.HBITMAP
gdi32.CreateCompatibleBitmap.argtypes = [W.HDC, ctypes.c_int, ctypes.c_int]
gdi32.SelectObject.restype = W.HGDIOBJ
gdi32.SelectObject.argtypes = [W.HDC, W.HGDIOBJ]
gdi32.BitBlt.argtypes = [W.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, W.HDC, ctypes.c_int, ctypes.c_int, W.DWORD]
gdi32.DeleteObject.argtypes = [W.HGDIOBJ]
gdi32.DeleteDC.argtypes = [W.HDC]
user32.ReleaseDC.argtypes = [W.HWND, W.HDC]
gdi32.GetBitmapBits.argtypes = [W.HBITMAP, ctypes.c_long, ctypes.c_void_p]


def call(method, path, body=None):
    req = urllib.request.Request(BASE + path, method=method, data=None if body is None else json.dumps(body).encode(), headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    with op.open(req, timeout=20) as r:
        return json.loads(r.read() or b"null")


class WS:
    def __init__(self, url):
        u = urllib.parse.urlparse(url)
        self.s = socket.create_connection((u.hostname, u.port), timeout=20)
        key = base64.b64encode(os.urandom(16)).decode()
        self.s.sendall((f"GET {u.path} HTTP/1.1\r\nHost: {u.hostname}:{u.port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
        buf = b""
        while b"\r\n\r\n" not in buf:
            buf += self.s.recv(4096)
        self.n = 0

    def _read(self, n):
        b = b""
        while len(b) < n:
            b += self.s.recv(n - len(b))
        return b

    def eval(self, expr):
        self.n += 1
        p = json.dumps({"id": self.n, "method": "Runtime.evaluate", "params": {"expression": expr, "returnByValue": True}}).encode()
        mask = os.urandom(4)
        head = b"\x81" + (bytes([0x80 | len(p)]) if len(p) < 126 else bytes([0x80 | 126]) + struct.pack(">H", len(p)))
        self.s.sendall(head + mask + bytes(c ^ mask[i % 4] for i, c in enumerate(p)))
        while True:
            h = self._read(2)
            ln = h[1] & 0x7f
            if ln == 126: ln = struct.unpack(">H", self._read(2))[0]
            elif ln == 127: ln = struct.unpack(">Q", self._read(8))[0]
            m = json.loads(self._read(ln))
            if m.get("id") == self.n:
                return m["result"]["result"].get("value")


pid_hub = None


def window():
    found = []
    @ctypes.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)
    def cb(h, _):
        p = W.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid_hub and user32.IsWindowVisible(h):
            r = W.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            if r.bottom - r.top > 200:
                found.append((r.left, r.top, r.right, r.bottom))
        return True
    user32.EnumWindows(cb, 0)
    return found[0] if found else None


def until(f, t=8):
    end = time.time() + t
    while time.time() < end:
        if f(): return True
        time.sleep(.03)
    return bool(f())


def sample(x0, x1, y, seconds, action):
    """Reads the row [x0, x1) at y for `seconds`, runs action just after the start. Returns a list of (t, magenta px, cyan px)."""
    w = x1 - x0
    sdc = user32.GetDC(None)
    mdc = gdi32.CreateCompatibleDC(sdc)
    bmp = gdi32.CreateCompatibleBitmap(sdc, w, 1)
    gdi32.SelectObject(mdc, bmp)
    buf = ctypes.create_string_buffer(w * 4)
    out = []
    threading.Timer(.05, action).start()
    t0 = time.perf_counter()
    while (t := time.perf_counter() - t0) < seconds:
        gdi32.BitBlt(mdc, 0, 0, w, 1, sdc, x0, y, 0x00CC0020 | 0x40000000)
        gdi32.GetBitmapBits(bmp, w * 4, buf)
        raw = buf.raw
        m = c = 0
        for i in range(0, w * 4, 4):   # BGRA
            b, g, r = raw[i], raw[i + 1], raw[i + 2]
            if r > 180 and b > 180 and g < 90: m += 1
            elif g > 180 and b > 180 and r < 90: c += 1
        out.append((round(t, 3), m, c))
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(mdc); user32.ReleaseDC(None, sdc)
    return out


def report(name, rows, key):
    idx = {"magenta": 1, "cyan": 2}[key]
    hit = [r for r in rows if r[idx] > 0]
    print(f"{name}: {len(rows)} reads over {rows[-1][0] if rows else 0:.2f}s; {key} in {len(hit)} reads, max {max([r[idx] for r in rows] or [0])} px" + (f", from {hit[0][0]} to {hit[-1][0]} s" if hit else ""), flush=True)


hub = subprocess.Popen([EXE, "--port", "47898", "--data", data], env=env)
pid_hub = hub.pid
try:
    for _ in range(60):
        try: call("GET", "/api/health"); break
        except Exception: time.sleep(.5)
    call("POST", "/api/ui/open", {})
    page = None
    for _ in range(60):
        try:
            page = next((t for t in json.load(op.open(f"http://127.0.0.1:{CDP}/json", timeout=2)) if t.get("type") == "page" and "/ui" in t.get("url", "")), None)
            if page: break
        except Exception: pass
        time.sleep(.5)
    ws = WS(page["webSocketDebuggerUrl"])
    until(lambda: ws.eval('typeof NATIVE!=="undefined" && NATIVE && !!document.getElementById("cfg")'))
    tries = 0
    while window() is None and tries < 10:
        time.sleep(2); tries += 1
        call("POST", "/api/ui/open", {})
    time.sleep(.6)
    ws.eval('''(()=>{const s=document.createElement("style");s.textContent="html,html.native body,body{background:#f0f !important} #set{background:#0ff !important} #list,header,#set *{visibility:hidden}";document.head.appendChild(s);})()''')
    time.sleep(.5)
    w0 = window()
    L, T, R, B = w0
    y = (T + B) // 2
    vx = user32.GetSystemMetrics(76) + user32.GetSystemMetrics(78)   # right end of the virtual screen
    print("panel", w0, "virtual screen right", vx, flush=True)
    has_right = vx > R
    open_set = lambda: ws.eval("openSet(); 1")
    close_set = lambda: ws.eval("closeSet(); 1")
    wide = (L - round(560 * ws.eval("devicePixelRatio")), T, R, B)

    for round_ in range(3):
        print("--- round", round_, flush=True)
        if has_right:
            rows = sample(R, min(R + 300, vx), y, .7, lambda: call("POST", "/api/ui/hide", {}))
            report("hide, panel only: on the monitor to the right", rows, "magenta")
            until(lambda: window() is None)
            time.sleep(.2)
            rows = sample(R, min(R + 300, vx), y, .7, lambda: call("POST", "/api/ui/open", {}))
            report("open, panel only: on the monitor to the right", rows, "magenta")
            until(lambda: window() == w0)
            time.sleep(.3)
        rows = sample(L, R, y, .8, open_set)
        report("settings open: settings' colour inside the panel's strip", rows, "cyan")
        bad = [r for r in rows if r[1] < (R - L) - 4 and r[0] > .05]
        print(f"   panel strip not fully panel-coloured in {len(bad)} reads", flush=True)
        until(lambda: window() == wide)
        time.sleep(.4)
        rows = sample(L, R, y, .8, close_set)
        report("settings close: settings' colour inside the panel's strip", rows, "cyan")
        bad = [r for r in rows if r[1] < (R - L) - 4 and r[0] > .05]
        print(f"   panel strip not fully panel-coloured in {len(bad)} reads", flush=True)
        until(lambda: window() == w0)
        time.sleep(.4)
        if has_right:
            ws.eval("openSet(); 1")
            until(lambda: window() == wide)
            time.sleep(.4)
            rows = sample(R, min(R + 300, vx), y, .7, lambda: call("POST", "/api/ui/hide", {}))
            report("hide with settings open: panel on the monitor to the right", rows, "magenta")
            until(lambda: window() is None)
            time.sleep(.2)
            call("POST", "/api/ui/open", {})
            until(lambda: window() == w0)
            time.sleep(.4)
finally:
    hub.terminate()
    try: hub.wait(10)
    except Exception: hub.kill()
    time.sleep(1)
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(home, ignore_errors=True)
