"""The settings window in the real WebView2 panel, on an isolated hub (47898, temp data, test home), driven through CDP. By hand, not in
run_tests: it opens a real window on the desktop for about a minute and can take the focus. Keys and clicks go through CDP Input into the page.
Native.ForceForeground may fall back to an ALT tap: run it against an exe built without those two lines, so that no keystroke can leave the
test (tests/AGENTS.md). Pinned rules are checked on the page's own flag, so the desktop isn't re-fitted; the real AppBar path is checked by hand.
Usage: python -X utf8 settings_window_check.py <TaskHubHost.exe>. Screenshots go to a temp folder."""
import base64, ctypes, ctypes.wintypes as W, json, os, shutil, socket, struct, subprocess, sys, tempfile, time, urllib.request, urllib.parse

OUT = tempfile.mkdtemp(prefix="taskhub-setwin-shots-")
EXE = sys.argv[1]
BASE = "http://127.0.0.1:47898"
CDP = 9338
data = tempfile.mkdtemp(prefix="taskhub-setwin-")
home = tempfile.mkdtemp(prefix="taskhub-setwinhome-")
json.dump({"toasts": False, "autostart": False, "update_check": False, "language": "uk", "panel_autohide": False,
           "connectors": {"runpod": {"enabled": True}}}, open(os.path.join(data, "config.json"), "w"))
env = dict(os.environ, TASKHUB_TEST_HOME=home, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=f"--remote-debugging-port={CDP}")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, ok, info=None):
    global fails
    print(("ok   " if ok else "FAIL ") + name + ("" if ok else f"  -> {info!r}"), flush=True)
    fails += 0 if ok else 1


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

    def send(self, method, params):
        self.n += 1
        p = json.dumps({"id": self.n, "method": method, "params": params}).encode()
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
                return m["result"]

    def eval(self, expr):
        r = self.send("Runtime.evaluate", {"expression": expr, "returnByValue": True})
        if "exceptionDetails" in r: raise RuntimeError(r["exceptionDetails"])
        return r["result"].get("value")

    def shot(self, name):
        p = os.path.join(OUT, name)
        open(p, "wb").write(base64.b64decode(self.send("Page.captureScreenshot", {"format": "png"})["data"]))
        print("shot", p, flush=True)

    def click(self, sel):
        x, y = self.eval(f'(()=>{{const r=document.querySelector({json.dumps(sel)}).getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2]}})()')
        self.send("Input.dispatchMouseEvent", {"type": "mouseMoved", "x": x, "y": y})
        for t in ("mousePressed", "mouseReleased"):
            self.send("Input.dispatchMouseEvent", {"type": t, "x": x, "y": y, "button": "left", "clickCount": 1})

    def hover(self, sel):
        x, y = self.eval(f'(()=>{{const r=document.querySelector({json.dumps(sel)}).getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2]}})()')
        self.send("Input.dispatchMouseEvent", {"type": "mouseMoved", "x": x, "y": y})

    def key(self, name, code):
        for t in ("keyDown", "keyUp"):
            self.send("Input.dispatchKeyEvent", {"type": t, "key": name, "code": name, "windowsVirtualKeyCode": code})


user32 = ctypes.windll.user32
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))   # real pixels, as the hub (otherwise Windows scales the numbers it gives us)
user32.GetWindowRect.argtypes = [W.HWND, ctypes.POINTER(W.RECT)]
pid_hub = None


def window():
    """The hub's visible top-level panel window: (left, top, right, bottom)."""
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


def until(f, t=6):
    end = time.time() + t
    while time.time() < end:
        if f(): return True
        time.sleep(.05)
    return bool(f())


hub = subprocess.Popen([EXE, "--port", "47898", "--data", data], env=env)
pid_hub = hub.pid
try:
    for _ in range(60):
        try: call("GET", "/api/health"); break
        except Exception: time.sleep(.5)
    for line in subprocess.check_output("netstat -ano -p tcp", text=True).splitlines():   # the process really serving the port
        if ":47898 " in line and "LISTENING" in line:
            pid_hub = int(line.split()[-1])
    print("hub pid", hub.pid, "serving", pid_hub, flush=True)
    call("POST", "/api/tasks", {"id": "demo-render", "name": "Video render", "progress": 0.4, "status": "Frame 1204"})
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
    time.sleep(.6)
    tries = 0
    while window() is None and tries < 10:   # right after the start an open can come too early
        time.sleep(2); tries += 1
        call("POST", "/api/ui/open", {})
    time.sleep(.5)
    w0 = window()
    k = ws.eval("devicePixelRatio")
    print("panel", w0, "dpr", k, flush=True)
    open_ = lambda: ws.eval('document.body.classList.contains("setmode")')
    wide = lambda: ws.eval('document.body.classList.contains("setwide")')
    x_panel = lambda: ws.eval('document.querySelector("header").getBoundingClientRect().left')

    # opening
    ws.click("#cfg")
    expect = round(560 * k)
    check("gear: the window grows left by 560 DIP, the panel's right edge stays", until(lambda: window() == (w0[0] - expect, w0[1], w0[2], w0[3])), (window(), w0, expect))
    time.sleep(.3)
    check("the panel keeps its width and place inside the window", abs(ws.eval('document.querySelector("header").getBoundingClientRect().width') * k - (w0[2] - w0[0])) <= 2 and abs(x_panel() * k - expect) <= 2, (x_panel(), ws.eval('document.querySelector("header").getBoundingClientRect().width')))
    check("settings take the part left of it", abs(ws.eval('document.getElementById("set").getBoundingClientRect().width') * k - expect) <= 2)
    check("no arrow; a close button", ws.eval('!document.getElementById("setback") && !!document.getElementById("setclose")'))
    until(lambda: ws.eval('!!SET'))
    time.sleep(.3)
    tabs = ws.eval('[...document.querySelectorAll("#rail .rt")].map(b=>b.dataset.tab)')
    check("seven tabs, About last", tabs == ["look", "notif", "messengers", "connectors", "agents", "system", "about"], tabs)
    check("the focus is on the active tab", ws.eval('document.activeElement.classList.contains("rt") && document.activeElement.getAttribute("aria-selected")==="true"'))
    check("every tab has a name for a screen reader", ws.eval('[...document.querySelectorAll("#rail .rt")].every(b=>b.getAttribute("aria-label"))'))
    ws.shot("set_look.png")

    # names on hover, after a delay
    ws.hover("#st-connectors")
    time.sleep(.2)
    check("hover: no name yet at 0.2 s", not ws.eval('document.getElementById("settip").classList.contains("on")'))
    time.sleep(.5)
    check("hover: the name after 0.5 s", ws.eval('document.getElementById("settip").classList.contains("on") && document.getElementById("settip").textContent==="Конектори"'), ws.eval('document.getElementById("settip").textContent'))
    ws.shot("set_tip.png")

    # one section per tab; a dot on the connector tab (Runpod is on, without a key)
    for t, title in (("notif", "Сповіщення"), ("messengers", "Месенджери"), ("connectors", "Конектори"), ("agents", "AI-агенти"), ("system", "Система"), ("about", "Про TaskHub")):
        ws.click(f"#st-{t}")
        time.sleep(.15)
        check(f"tab {t}: its title", ws.eval('document.getElementById("settitle").textContent') == title, ws.eval('document.getElementById("settitle").textContent'))
        check(f"tab {t}: has content, the appearance card is gone", ws.eval('document.getElementById("sethost").innerText.trim().length>0 && document.getElementById("lookcard").style.display==="none"'))
        check(f"tab {t}: nothing overflows sideways", ws.eval('document.getElementById("setsc").scrollWidth<=document.getElementById("setsc").clientWidth'))
        ws.shot(f"set_{t}.png")
    check("a dot on Connectors: a connector without a key", ws.eval('!!document.querySelector("#st-connectors .dot") && document.getElementById("st-connectors").getAttribute("aria-label").includes("уваги")'), ws.eval('document.getElementById("st-connectors").outerHTML'))
    check("no dot where nothing is needed", not ws.eval('!!document.querySelector("#st-agents .dot")'))

    # text typed survives another tab and a redraw
    ws.click("#st-connectors")
    time.sleep(.2)
    until(lambda: ws.eval('!!document.querySelector("#sethost input[data-ckey=runpod]")'))
    ws.eval('document.querySelector("#sethost input[data-ckey=runpod]").value="half-typed"; 1')
    ws.click("#st-system")
    time.sleep(.2)
    ws.click("#st-connectors")
    time.sleep(.2)
    check("typed text survives a visit to another tab", ws.eval('document.querySelector("#sethost input[data-ckey=runpod]").value') == "half-typed")
    time.sleep(3.3)
    check("...and the state refreshing every 3 s", ws.eval('document.querySelector("#sethost input[data-ckey=runpod]").value') == "half-typed")

    # arrows
    ws.eval('document.getElementById("st-connectors").focus(); 1')
    ws.key("ArrowDown", 40)
    time.sleep(.2)
    check("arrow down: the next tab, focus on it", ws.eval('document.getElementById("settitle").textContent==="AI-агенти" && document.activeElement.id==="st-agents"'))
    ws.key("ArrowUp", 38)
    time.sleep(.2)
    check("arrow up: back", ws.eval('document.activeElement.id==="st-connectors"'))

    # closing, not pinned
    check("not pinned", not ws.eval("PANEL.panel_pinned"))
    ws.click("#setclose")
    check("close button: the window back to the panel's", until(lambda: window() == w0), (window(), w0))
    check("...the settings part is gone, the focus on the gear", until(lambda: not wide()) and ws.eval('document.activeElement.id==="cfg"'))
    time.sleep(.2)
    check("...the panel's content where it was", abs(x_panel() * k - expect) <= 2, x_panel())

    ws.click("#cfg")
    until(lambda: window() != w0)
    time.sleep(.4)
    check("reopened on the last tab", ws.eval('document.getElementById("settitle").textContent') == "Конектори")
    check("typed text still there within 2 minutes", ws.eval('document.querySelector("#sethost input[data-ckey=runpod]").value') == "half-typed")
    ws.click("#cfg")
    check("gear closes", until(lambda: window() == w0))

    ws.click("#cfg")
    until(lambda: window() != w0)
    time.sleep(.4)
    ws.click("#list")
    check("click on the panel: settings closed, the panel stays", until(lambda: window() == w0) and not open_())

    ws.click("#cfg")
    until(lambda: window() != w0)
    time.sleep(.4)
    ws.eval('document.getElementById("st-connectors").focus(); 1')
    ws.key("Escape", 27)
    check("Esc: settings closed, the panel stays", until(lambda: window() == w0) and ws.eval('!document.hidden') and window() is not None)
    ws.eval('wipeTyped(); 1')

    # closing, pinned rules (the page's own flag: the desktop isn't re-fitted)
    ws.click("#cfg")
    until(lambda: window() != w0)
    wd = None
    until(lambda: window() == (w0[0] - expect, w0[1], w0[2], w0[3]))
    wd = window()
    time.sleep(.3)
    ws.eval("PANEL.panel_pinned=true; 1")
    ws.click("#list")
    time.sleep(.5)
    check("pinned: a click on the panel doesn't close", open_() and window() == wd)
    ws.eval('document.getElementById("st-connectors").focus(); 1')
    ws.key("Escape", 27)
    time.sleep(.5)
    check("pinned: Esc doesn't close, nor hide the panel", open_() and window() == wd)
    ws.click("#cfg")
    time.sleep(.5)
    check("pinned: the gear doesn't close, the focus goes to the tabs", open_() and window() == wd and ws.eval('document.activeElement.classList.contains("rt")'))
    ws.click("#setclose")
    check("pinned: the close button closes", until(lambda: window() == w0) and not open_())
    ws.eval("PANEL.panel_pinned=false; 1")

    # collapsing: settings don't come back
    ws.click("#cfg")
    until(lambda: window() != w0)
    time.sleep(.4)
    call("POST", "/api/ui/hide", {})
    check("hide: everything goes", until(lambda: window() is None), window())
    check("...settings closed", until(lambda: not open_() and not wide()))
    call("POST", "/api/ui/open", {})
    check("open: the panel only, settings don't open", until(lambda: window() == w0), window())
    time.sleep(.4)
    check("...still closed, the tab remembered", not open_() and ws.eval('currentTab()') == "connectors")

    # the page reloaded with settings open: the window follows
    ws.click("#cfg")
    until(lambda: window() != w0)
    time.sleep(.4)
    ws.send("Page.reload", {})
    check("a reload with settings open: the window back to the panel's", until(lambda: window() == w0, 8), window())
    time.sleep(.5)

    # quick open / close
    ws.eval("openSet(); closeSet(); openSet(); closeSet(); 1")
    check("open / close quickly: ends closed", until(lambda: window() == w0 and not wide()), (window(), wide()))
    ws.eval("openSet(); closeSet(); openSet(); 1")
    check("close / open quickly: ends open", until(lambda: window() == (w0[0] - expect, w0[1], w0[2], w0[3]) and wide()), (window(), wide()))
    ws.eval("closeSet(); 1")
    until(lambda: window() == w0)

    # review findings
    ws.click("#cfg")
    until(lambda: window() != w0)
    time.sleep(.4)
    check("the snack lies above the settings", int(ws.eval('getComputedStyle(document.getElementById("snack")).zIndex') or 0) > 8 if ws.eval('getComputedStyle(document.getElementById("snack")).zIndex') != "auto" else False, ws.eval('getComputedStyle(document.getElementById("snack")).zIndex'))
    ws.click("#setclose")
    until(lambda: window() == w0)
    time.sleep(.3)
    # settings asked for while the panel is still sliding in: nothing drawn over the list until the host has made room
    call("POST", "/api/ui/hide", {})
    until(lambda: window() is None)
    time.sleep(.3)
    call("POST", "/api/ui/open", {})
    ws.eval("openSet(); 1")
    check("no settings over the list while the panel slides in", not wide(), (wide(), window()))
    until(lambda: window() == (w0[0] - expect, w0[1], w0[2], w0[3]))
    time.sleep(.4)
    check("...then they are there, the focus on the tab", wide() and ws.eval('document.activeElement.classList.contains("rt")'), ws.eval('document.activeElement.outerHTML.slice(0,80)'))
    ws.click("#setclose")
    until(lambda: window() == w0)
    time.sleep(.3)
    # the window isn't where the host last put it (a resolution or taskbar change on an unpinned panel): gear doesn't throw it back
    H = []
    @ctypes.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)
    def cb2(h, _):
        p = W.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid_hub and user32.IsWindowVisible(h):
            r = W.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            if r.bottom - r.top > 200:
                H.append(h)
        return True
    user32.EnumWindows(cb2, 0)
    user32.SetWindowPos.argtypes = [W.HWND, W.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, W.UINT]
    user32.SetWindowPos(H[0], None, w0[0] - 60, w0[1], w0[2] - w0[0], w0[3] - w0[1], 0x14)
    time.sleep(.3)
    moved = window()
    ws.click("#cfg")
    until(lambda: window() != moved)
    time.sleep(.6)
    now = window()
    check("stale place: the panel's right edge stays where it really is", now and now[2] == moved[2], (now, moved))
    ws.click("#setclose")
    time.sleep(.6)
    check("...also after closing", window() == moved, (window(), moved))

    # the 2 minutes
    ws.eval("wipeTyped(); 1")
    print("failures:", fails, flush=True)
finally:
    hub.terminate()
    try: hub.wait(10)
    except Exception: hub.kill()
    time.sleep(1)
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(home, ignore_errors=True)
sys.exit(1 if fails else 0)
