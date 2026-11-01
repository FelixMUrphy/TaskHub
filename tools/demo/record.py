"""Scripted TaskHub demo: progress -> chart -> notification -> confirmation in Telegram, recorded to MP4 / GIF, plus
1920x1080 stills (README, Store screenshots).

Everything runs on fake data and in isolation:
- an own hub (port 47898, data folder %TEMP%\\taskhub-demo, no tray, Windows notifications off) - never the real hub
  on 47800 and never the user's config;
- a mock Telegram Bot API on 127.0.0.1:47893 (nothing goes to Telegram; the bot token is a dummy);
- headless Microsoft Edge renders tools/demo/scene.html (a desktop with a terminal, the hub's real panel /ui, the
  notification and a phone chat built from what the hub sent to the mock bot) and is driven over the DevTools protocol.
  No window is shown and no keystrokes are sent.

Usage:  python tools/demo/record.py [path\\to\\TaskHubHost.exe] [--out DIR] [--no-video]
Needs:  Python 3.9+ (stdlib), Microsoft Edge, ffmpeg on PATH (only for the video / GIF).
Output: DIR (default dist\\demo): taskhub-demo.mp4, taskhub-demo.gif, still-*.png (1920x1080), still-panel.png.
"""
import base64, json, os, re, shutil, socket, struct, subprocess, sys, tempfile, threading, time
import urllib.error, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
HUB_PORT_ = 47898
os.environ["TASKHUB_URL"] = f"http://127.0.0.1:{HUB_PORT_}"   # the client reads it on import
sys.path.insert(0, os.path.join(ROOT, "clients", "python"))
sys.path.insert(0, os.path.join(ROOT, "tests"))
from taskhub_client import C, Task, W   # noqa: E402
import vault                              # noqa: E402

HUB_PORT, BOT_PORT, CDP_PORT = 47898, 47893, 47889
HUB = f"http://127.0.0.1:{HUB_PORT}"
TOKEN = "123456789:" + "D" * 35          # dummy, only the mock bot sees it
CHAT = 4242
W_, H_ = 1920, 1080

args = [a for a in sys.argv[1:]]
EXE = next((a for a in args if a.lower().endswith(".exe")), os.path.join(ROOT, "app", "TaskHubHost.exe"))
OUT = args[args.index("--out") + 1] if "--out" in args else os.path.join(ROOT, "dist", "demo")
VIDEO = "--no-video" not in args
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))


# ---------------------------------------------------------------------------------------------- mock Telegram Bot API
chat, updates, lock = [], [], threading.Condition()   # chat: [{id, v, text, buttons, data, me, time}]


class Bot(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def end_headers(self):
        self.send_header("Connection", "close")   # see tests/telegram_test.py: .NET pools HTTP/1.0 connections otherwise
        super().end_headers()

    def do_POST(self):
        m = re.match(r"^/bot([^/]+)/(\w+)$", self.path)
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        body = {} if "multipart" in (self.headers.get("Content-Type") or "") else json.loads(raw or b"{}")
        if not m or m.group(1) != TOKEN:
            return self.reply({"ok": False, "description": "Unauthorized"}, 401)
        meth = m.group(2)
        if meth == "getMe":
            return self.reply({"ok": True, "result": {"id": 1, "is_bot": True, "username": "taskhub_demo_bot"}})
        if meth == "getUpdates":
            off = body.get("offset", 0)
            with lock:
                if off != -1:
                    lock.wait_for(lambda: any(u["update_id"] >= off for u in updates), timeout=min(body.get("timeout", 0), 2))
                res = [u for u in updates if u["update_id"] >= off] if off != -1 else []
            return self.reply({"ok": True, "result": res})
        if meth == "sendMessage":
            kb = (body.get("reply_markup") or {}).get("inline_keyboard") or []
            with lock:
                mid = len(chat) + 1
                chat.append({"id": mid, "v": 0, "text": body.get("text", ""), "time": time.strftime("%H:%M"),
                             "buttons": [[b["text"] for b in r] for r in kb],
                             "data": {b["text"]: b.get("callback_data") for r in kb for b in r}})
                lock.notify_all()
            return self.reply({"ok": True, "result": {"message_id": mid}})
        if meth == "editMessageText":
            kb = (body.get("reply_markup") or {}).get("inline_keyboard")
            with lock:
                for c in chat:
                    if c["id"] == body.get("message_id"):
                        c["text"], c["v"] = body.get("text", c["text"]), c["v"] + 1
                        if kb is not None:
                            c["buttons"] = [[b["text"] for b in r] for r in kb]
                            c["data"] = {b["text"]: b.get("callback_data") for r in kb for b in r}
                lock.notify_all()
            return self.reply({"ok": True, "result": True})
        if meth == "editMessageReplyMarkup":
            with lock:
                for c in chat:
                    if c["id"] == body.get("message_id"):
                        c["buttons"], c["data"], c["v"] = [], {}, c["v"] + 1
            return self.reply({"ok": True, "result": True})
        if meth == "sendPhoto":
            return self.reply({"ok": True, "result": {"message_id": 900}})
        return self.reply({"ok": True, "result": True})   # answerCallbackQuery, setMyCommands, ...

    def reply(self, obj, code=200):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def press(button):
    """The phone user taps an inline button: a callback_query update for the hub."""
    with lock:
        msg = next((c for c in reversed(chat) if button in c["data"]), None)
        if not msg:
            raise RuntimeError("no button " + button)
        updates.append({"update_id": 1000 + len(updates), "callback_query": {
            "id": "cb%d" % len(updates), "from": {"id": CHAT}, "message": {"message_id": msg["id"], "chat": {"id": CHAT}},
            "data": msg["data"][button]}})
        lock.notify_all()


def wait_chat(pred, timeout=15):
    end = time.time() + timeout
    with lock:
        while time.time() < end:
            hit = next((c for c in reversed(chat) if pred(c)), None)
            if hit:
                return hit
            lock.wait(0.2)
    raise RuntimeError("the hub sent no matching Telegram message")


# ------------------------------------------------------------------------------------- minimal DevTools (CDP) client
class Cdp:
    """Just enough of a WebSocket client (RFC 6455, text frames, no extensions) to talk to Edge's DevTools endpoint."""

    def __init__(self, ws_url):
        m = re.match(r"ws://([^/:]+):(\d+)(/.*)", ws_url)
        self.s = socket.create_connection((m.group(1), int(m.group(2))), timeout=60)
        key = base64.b64encode(os.urandom(16)).decode()
        self.s.sendall((f"GET {m.group(3)} HTTP/1.1\r\nHost: {m.group(1)}:{m.group(2)}\r\nUpgrade: websocket\r\n"
                        f"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
        head = b""
        while b"\r\n\r\n" not in head:
            head += self.s.recv(1)
        if b" 101 " not in head.split(b"\r\n")[0]:
            raise RuntimeError("websocket handshake failed: " + head.decode(errors="replace"))
        self.n, self.lock = 0, threading.Lock()

    def _send(self, text):
        data = text.encode()
        hdr = bytearray([0x81])
        n = len(data)
        hdr += bytes([0x80 | n]) if n < 126 else (bytes([0x80 | 126]) + struct.pack(">H", n) if n < 65536
                                                   else bytes([0x80 | 127]) + struct.pack(">Q", n))
        mask = os.urandom(4)
        self.s.sendall(bytes(hdr) + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))

    def _exact(self, n):
        buf = bytearray()
        while len(buf) < n:
            chunk = self.s.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("devtools closed")
            buf += chunk
        return bytes(buf)

    def _recv(self):
        msg = b""
        while True:
            b1, b2 = self._exact(2)
            n = b2 & 0x7F
            n = struct.unpack(">H", self._exact(2))[0] if n == 126 else struct.unpack(">Q", self._exact(8))[0] if n == 127 else n
            payload = self._exact(n)
            if b1 & 0x0F == 0x9:          # ping
                continue
            msg += payload
            if b1 & 0x80:
                return msg.decode()

    def call(self, method, **params):
        with self.lock:
            self.n += 1
            me = self.n
            self._send(json.dumps({"id": me, "method": method, "params": params}))
            while True:
                r = json.loads(self._recv())
                if r.get("id") == me:
                    if "error" in r:
                        raise RuntimeError(f"{method}: {r['error']}")
                    return r.get("result", {})

    def js(self, expr):
        r = self.call("Runtime.evaluate", expression=expr, returnByValue=True, awaitPromise=True)
        return (r.get("result") or {}).get("value")


# ------------------------------------------------------------------------------------------------------------ helpers
def http(method, path, body=None):
    req = urllib.request.Request(HUB + path, json.dumps(body).encode() if body is not None else None, method=method,
                                 headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    with op.open(req, timeout=10) as r:
        return json.loads(r.read() or b"{}")


def wait_url(url, timeout=30):
    end = time.time() + timeout
    while time.time() < end:
        try:
            with op.open(url, timeout=1) as r:
                return r.read()
        except (OSError, urllib.error.URLError):
            time.sleep(0.3)
    raise RuntimeError("no answer from " + url)


def find_edge():
    for p in (os.path.expandvars(r"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
              os.path.expandvars(r"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe")):
        if os.path.exists(p):
            return p
    raise RuntimeError("Microsoft Edge not found")


class Recorder:
    """Screenshots of the scene at ~FPS frames a second while the scenario runs; stills on request."""

    def __init__(self, cdp, frames_dir, fps):
        self.cdp, self.dir, self.fps, self.frames, self.on = cdp, frames_dir, fps, [], False

    def _loop(self):
        nxt = time.time()
        while self.on:
            r = self.cdp.call("Page.captureScreenshot", format="jpeg", quality=90)
            path = os.path.join(self.dir, "f%05d.jpg" % len(self.frames))
            with open(path, "wb") as f:
                f.write(base64.b64decode(r["data"]))
            self.frames.append((path, time.time()))
            nxt += 1 / self.fps
            time.sleep(max(0, nxt - time.time()))

    def start(self):
        if VIDEO:
            self.on = True
            self.t = threading.Thread(target=self._loop, daemon=True)
            self.t.start()

    def stop(self):
        if self.on:
            self.on = False
            self.t.join()

    def still(self, name, panel=False):
        """A 1920x1080 still; panel=True: only the docked panel (470 px wide, above the taskbar)."""
        clip = {"clip": {"x": W_ - 470, "y": 0, "width": 470, "height": H_ - 48, "scale": 1}} if panel else {}
        r = self.cdp.call("Page.captureScreenshot", format="png", **clip)
        with open(os.path.join(OUT, f"still-{name}.png"), "wb") as f:
            f.write(base64.b64decode(r["data"]))


def encode(frames, work):
    """Real-time playback (each frame lasts until the next one) -> MP4 (H.264) and a 960 px GIF."""
    ff = shutil.which("ffmpeg")
    if not ff or len(frames) < 2:
        print("ffmpeg not found or no frames: video skipped")
        return
    lst = os.path.join(work, "frames.txt")
    with open(lst, "w", encoding="utf-8") as f:
        for (p, t), (_, t2) in zip(frames, frames[1:] + [(None, frames[-1][1] + 1.5)]):
            f.write(f"file '{p}'\nduration {max(0.04, t2 - t):.3f}\n")
        f.write(f"file '{frames[-1][0]}'\n")
    mp4, gif = os.path.join(OUT, "taskhub-demo.mp4"), os.path.join(OUT, "taskhub-demo.gif")
    subprocess.run([ff, "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", lst, "-vf", "fps=30,format=yuv420p",
                    "-c:v", "libx264", "-crf", "20", "-movflags", "+faststart", mp4], check=True)
    subprocess.run([ff, "-y", "-loglevel", "error", "-i", mp4, "-vf",
                    "fps=10,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];"
                    "[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle", gif], check=True)
    for p in (mp4, gif):
        print(f"{os.path.basename(p)}: {os.path.getsize(p) / 1e6:.1f} MB")


# ----------------------------------------------------------------------------------------------------------- scenario
def scenario(cdp, rec):
    S = lambda expr: cdp.js("scene." + expr)
    term = lambda *lines: S("term(%s)" % json.dumps([list(x) for x in lines]))
    show_chat = lambda: S("chat(%s)" % json.dumps([{k: c[k] for k in ("id", "v", "text", "buttons", "time")} for c in chat]))

    # background tasks already in the panel
    bk = Task("Nightly backup D: -> NAS", source="powershell", url=HUB, cwd="D:/ops/backup", poll=False, throttle=0).start()
    bk.progress(1, total=1, status="1.2 TB, 0 errors")
    bk.done("1.2 TB copied, 0 errors")
    tr = Task("Fine-tune text classifier", total=40, unit="epochs", source="codex", url=HUB, cwd="C:/Projects/demo/classifier",
              widgets=[W.chart("loss", "Loss", series=["train", "val"], max_points=60)], poll=False, throttle=0).start()
    for e in range(14):
        tr.progress(e + 1, status=f"epoch {e + 1}", widgets={"loss": {"train": 1.3 * 0.86 ** e + 0.2, "val": 1.4 * 0.88 ** e + 0.27}})

    term(("p", "PS C:\\Projects\\demo\\render> "), ("c", "python render_scene.py city_flythrough --frames 120"),
         ("d", "TaskHub: task 'Render: city_flythrough' registered"))
    rec.start()
    time.sleep(1.2)

    frames = 120
    rnd = Task("Render: city_flythrough", total=frames, unit="frames", source="script", url=HUB, cwd="C:/Projects/demo/render",
               description="Cycles, 1920x1080, 256 samples",
               widgets=[W.chart("spf", "Seconds per frame", kind="bar", unit="s", max_points=60),
                        W.gauge("vram", "VRAM", max=24, unit="GB", decimals=1, warn=20, crit=23)],
               throttle=0).start()
    upload = threading.Event()
    rnd.on("upload", lambda cmd: upload.set())
    # Telegram: one live progress message (edited in place)
    S("phone(true)")
    import math
    for f in range(1, frames + 1, 3):
        sec = 21 + 4 * math.sin(f / 9) + (f % 7) * 0.3
        rnd.progress(f, status=f"frame {f}/{frames}", widgets={"spf": round(sec, 1), "vram": 14.2 + 1.6 * math.sin(f / 15)})
        if f % 15 == 1:
            term(("", f"Fra:{f} Mem:2.31G | Time:00:{int(sec):02d}.{f % 100:02d} | Rendering 256 / 256 samples"),
                 ("", f"Saved: 'out/f_{f:04d}.png'"))
        if f % 9 == 1:
            ep = min(40, 14 + f // 9)
            tr.progress(ep, status=f"epoch {ep}", widgets={"loss": {"train": 1.3 * 0.86 ** (14 + f // 9) + 0.2,
                                                               "val": 1.4 * 0.88 ** (14 + f // 9) + 0.27}})
        if chat:
            show_chat()
        time.sleep(0.18)
    rnd.progress(frames, status=f"{frames} frames rendered")
    rec.still("1-progress")
    rec.still("panel", panel=True)

    # the task asks: a notification with its buttons, the same buttons in Telegram
    rnd.update(add_controls=[C.signal("upload", "Upload to NAS", style="primary", toast=True),
                             C.signal("skip", "Keep local", toast=True)],
               attention="Render finished: upload 120 frames to the NAS?",
               toast="Render finished: 120 frames. Upload them to the NAS?")
    term(("g", "Render done: 120 frames"), ("d", "waiting for your answer in TaskHub (panel, notification or Telegram)..."))
    ask = wait_chat(lambda c: "Upload to NAS" in c["data"])
    show_chat()
    S("toast(%s, %s)" % (json.dumps(ask["text"]), json.dumps([b for r in ask["buttons"] for b in r] + ["Open panel"])))
    time.sleep(2.6)
    rec.still("2-question")
    S("toast(null)")
    time.sleep(0.8)

    # confirmation from the phone
    S("tap('Upload to NAS')")
    time.sleep(0.5)
    n0 = len(chat)
    press("Upload to NAS")
    # a control with no confirm question runs at once; if the hub asks "Yes?" first, tap it
    end = time.time() + 8
    while time.time() < end and not upload.wait(0.3):
        yes = next((c for c in chat[n0:] if any("Yes" in b for r in c["buttons"] for b in r)), None)
        if yes:
            show_chat()
            b = next(b for r in yes["buttons"] for b in r if "Yes" in b)
            time.sleep(0.6)
            S("tap(%s)" % json.dumps(b))
            press(b)
            n0 = len(chat)
    if not upload.is_set():
        raise RuntimeError("the button press did not reach the task")
    show_chat()
    term(("c", "> upload confirmed from Telegram"), ("", "uploading out/f_0001..0120.png -> \\\\nas\\renders\\city_flythrough"))
    rnd.update(attention="", stage="upload", status="uploading 120 frames", remove_controls=["upload", "skip"])
    for i in range(1, 11):
        rnd.update(status=f"uploading {i * 12}/120 frames")
        if chat:
            show_chat()
        time.sleep(0.25)
    rnd.done("120 frames rendered and uploaded to the NAS")
    term(("g", "done: 120 frames rendered and uploaded"))
    fin = wait_chat(lambda c: "uploaded to the NAS" in c["text"])
    show_chat()
    S("toast(%s, [])" % json.dumps(fin["text"]))
    time.sleep(2.8)
    rec.still("3-done")
    S("toast(null)")
    time.sleep(0.8)
    rec.stop()


def main():
    if not os.path.exists(EXE):
        sys.exit("TaskHubHost.exe not found (build.cmd first): " + EXE)
    os.makedirs(OUT, exist_ok=True)
    data = os.path.join(tempfile.gettempdir(), "taskhub-demo")
    shutil.rmtree(data, ignore_errors=True)
    os.makedirs(data)
    with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
        json.dump({"toasts": False, "autostart": False, "update_check": False, "language": "en", "theme": "dark",
                   "toast_rate": 0, "toast_task_gap": 0, "telegram_api": f"http://127.0.0.1:{BOT_PORT}",
                   "telegram_token": TOKEN, "telegram_chat": CHAT}, f)
    work = tempfile.mkdtemp(prefix="taskhub-demo-frames-")
    ThreadingHTTPServer.request_queue_size = 64
    bot = ThreadingHTTPServer(("127.0.0.1", BOT_PORT), Bot)
    bot.handle_error = lambda *a: None   # the hub is killed mid long-poll at the end: a reset connection is expected
    threading.Thread(target=bot.serve_forever, daemon=True).start()
    hub = subprocess.Popen([EXE, "--port", str(HUB_PORT), "--data", data, "--no-tray"])
    edge = None
    try:
        wait_url(HUB + "/api/health")
        edge = subprocess.Popen([find_edge(), "--headless=new", f"--remote-debugging-port={CDP_PORT}",
                                 "--user-data-dir=" + os.path.join(work, "edge"), f"--window-size={W_},{H_}",
                                 "--hide-scrollbars", "--force-device-scale-factor=1", "--no-first-run",
                                 "--disable-extensions", "--mute-audio", "about:blank"])
        targets = json.loads(wait_url(f"http://127.0.0.1:{CDP_PORT}/json/list"))
        page = next(t for t in targets if t.get("type") == "page")
        cdp = Cdp(page["webSocketDebuggerUrl"])
        cdp.call("Emulation.setDeviceMetricsOverride", width=W_, height=H_, deviceScaleFactor=1, mobile=False)
        cdp.call("Emulation.setEmulatedMedia", features=[{"name": "prefers-color-scheme", "value": "dark"}])
        scene = "file:///" + os.path.join(HERE, "scene.html").replace("\\", "/") + "?hub=" + HUB
        cdp.call("Page.enable")
        cdp.call("Page.navigate", url=scene)
        for _ in range(50):
            if cdp.js("typeof scene === 'object' && document.readyState === 'complete'"):
                break
            time.sleep(0.2)
        time.sleep(1.5)   # the panel's first load
        rec = Recorder(cdp, work, fps=8)
        scenario(cdp, rec)
        if VIDEO:
            encode(rec.frames, work)
        print("output:", OUT)
    finally:
        if edge:
            edge.kill()
        hub.kill()
        bot.shutdown()
        try:   # the dummy token the demo hub moved into the Credential Manager
            vault.delete(vault.target(data, "telegram"))
        except Exception:
            pass
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
