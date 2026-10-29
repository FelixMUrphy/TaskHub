#!/usr/bin/env python3
"""
taskhub_client — stdlib-only client for TaskHub (http://127.0.0.1:47800).

Library (fail-soft: if the hub is down, all calls are silent no-ops):

    from taskhub_client import Task, W, C

    with Task("HFSS sweep", total=120, unit="pts", source="claude",
              widgets=[W.chart("s11", "S11, dB", series=["S11", "S21"]),
                       W.gauge("gpu", "GPU", unit="%", warn=80, crit=95),
                       W.led("lic", "License")],
              controls=[C.signal("save", "Save snapshot"),
                        C.signal("lr", "Set LR", input=C.number(default=1e-3))],
              pausable=True, milestones=[50]) as t:
        t.on("lr", lambda v: f"lr={v}")         # handler result -> shown in panel
        for i in range(120):
            t.wait_if_paused()
            if t.stop_requested: break
            t.progress(i + 1, status=f"point {i+1}",
                       widgets={"s11": {"S11": -12.3, "S21": -0.4}, "gpu": 71, "lic": "ok"})
            t.log("block done", toast=(i % 40 == 39))

CLI (any language / agent):
    python taskhub_client.py run --name "Build" -- make -j8
    python taskhub_client.py notify "Title" "Message"
    python taskhub_client.py list | show ID | control ID CID [--value V] | finish ID --state done
    python taskhub_client.py register --name X  -> prints id;  update ID --percent 40 --status ..
"""
import argparse
import json
import os
import re
import subprocess
import sys
import threading
import time
import traceback
import urllib.error
from urllib.request import ProxyHandler, Request, build_opener

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47800").rstrip("/")
TOKEN = os.environ.get("TASKHUB_TOKEN", "")
_opener = build_opener(ProxyHandler({}))  # never route localhost through a proxy


class HubError(Exception):
    def __init__(self, code, msg):
        super().__init__(f"{code}: {msg}")
        self.code = code


def request(method, path, body=None, timeout=5.0, url=None):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = Request((url or URL) + path, data=data, method=method,
                  headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    if TOKEN:
        req.add_header("Authorization", f"Bearer {TOKEN}")
    try:
        with _opener.open(req, timeout=timeout) as r:
            raw = r.read()
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        try:
            msg = json.loads(e.read()).get("error", "")
        except Exception:
            msg = str(e)
        raise HubError(e.code, msg)


def available(url=None):
    try:
        return request("GET", "/api/health", timeout=1.5, url=url).get("app") == "TaskHub"
    except Exception:
        return False


def notify(title, message="", level="info", url=None):
    """One-off toast. level: info | warn | error"""
    try:
        request("POST", "/api/notify", {"title": title, "message": message, "level": level}, url=url)
        return True
    except Exception:
        return False


# ------------------------------------------------------------ builders ---
class C:
    """Control builders. kind: signal (delivered to the process), shell, open, kill."""
    @staticmethod
    def signal(id, label=None, *, input=None, confirm=None, style=None, states=None, hint=None, toast=False):
        """toast=True: also show this control as a button in the task's Windows notifications.
        With input, the value is sent only when the button is pressed - use an action label ("Set LR")."""
        return _clean({"id": id, "label": label or id, "kind": "signal", "input": input,
                       "confirm": confirm, "style": style, "states": states, "hint": hint, "toast": toast})

    @staticmethod
    def shell(id, label, command, *, cwd=None, input=None, confirm=None, style=None, states=None, timeout=None,
              toast=False):
        """command: list (recommended) or string (runs via shell). Placeholders: {value} {pid} {task_id}"""
        return _clean({"id": id, "label": label, "kind": "shell", "command": command, "cwd": cwd,
                       "input": input, "confirm": confirm, "style": style, "states": states, "timeout": timeout,
                       "toast": toast})

    @staticmethod
    def open(id, label, target, *, states=None, toast=False):
        return _clean({"id": id, "label": label, "kind": "open", "target": target, "toast": toast,
                       "states": states or ["running", "paused", "stale", "done", "failed", "cancelled", "lost"]})

    @staticmethod
    def text(label="", default="", placeholder=None):
        return _clean({"type": "text", "label": label, "default": default, "placeholder": placeholder})

    @staticmethod
    def number(label="", default=None, min=None, max=None, step=None):
        return _clean({"type": "number", "label": label, "default": default, "min": min, "max": max, "step": step})

    @staticmethod
    def select(options, label="", default=None):
        return _clean({"type": "select", "label": label, "options": list(options), "default": default})

    @staticmethod
    def bool(label="", default=False):
        return {"type": "bool", "label": label, "default": default}


class W:
    """Widget builders. Values are sent later via update(widgets={id: value})."""
    @staticmethod
    def text(id, label="", value=None, mono=False, span=None):
        return _clean({"id": id, "type": "text", "label": label, "value": value, "mono": mono, "span": span})

    @staticmethod
    def progress(id, label="", total=None, unit="", color=None, warn=None, crit=None):
        """value: 0..1 fraction (or 0..100 if >1), or current count when total is set"""
        return _clean({"id": id, "type": "progress", "label": label, "total": total, "unit": unit,
                       "color": color, "warn": warn, "crit": crit, "span": 2})

    @staticmethod
    def gauge(id, label="", min=0, max=100, unit="", warn=None, crit=None, invert=False, decimals=None):
        return _clean({"id": id, "type": "gauge", "label": label, "min": min, "max": max, "unit": unit,
                       "warn": warn, "crit": crit, "invert": invert, "decimals": decimals})

    @staticmethod
    def stat(id, label="", unit="", decimals=None, warn=None, crit=None, invert=False):
        """Big number with ▲▼ delta vs previous value (invert: lower is better)."""
        return _clean({"id": id, "type": "stat", "label": label, "unit": unit, "decimals": decimals,
                       "warn": warn, "crit": crit, "invert": invert})

    @staticmethod
    def led(id, label="", text=None):
        """value: 'ok'|'warn'|'error'|'busy'|'off'|True|False or {'state':..,'text':..}"""
        return _clean({"id": id, "type": "led", "label": label, "text": text})

    @staticmethod
    def kv(id, label="", decimals=None, span=None):
        return _clean({"id": id, "type": "kv", "label": label, "decimals": decimals, "span": span})

    @staticmethod
    def table(id, label="", columns=None, decimals=None):
        """value: list of rows (lists or dicts) — replaces the whole table"""
        return _clean({"id": id, "type": "table", "label": label, "columns": columns, "decimals": decimals})

    @staticmethod
    def chart(id, label="", series=None, kind="line", unit="", max_points=120, y_min=None, y_max=None,
              warn=None, crit=None, height=None, decimals=None):
        """Mini chart. Each update APPENDS a point: number | {series: v} | [v1, v2, ...]
        kind: line | area | bar"""
        return _clean({"id": id, "type": "chart", "label": label, "series": series or [id], "kind": kind,
                       "unit": unit, "max_points": max_points, "y_min": y_min, "y_max": y_max,
                       "warn": warn, "crit": crit, "height": height, "decimals": decimals})


def _clean(d):
    return {k: v for k, v in d.items() if v is not None and v is not False or k in ("default",) and v is not None}


# ---------------------------------------------------------------- task ---
class Command:
    def __init__(self, task, d):
        self.task, self.id, self.control, self.value = task, d["id"], d["control"], d.get("value")
        self.acked = False

    def ack(self, ok=True, message=""):
        if not self.acked:
            self.acked = True
            self.task._call("POST", f"/api/tasks/{self.task.id}/commands/{self.id}/ack",
                            {"ok": ok, "message": message})

    def __repr__(self):
        return f"Command({self.control!r}, value={self.value!r})"


class Task:
    def __init__(self, name, *, total=None, unit="", source=None, description="", controls=(), widgets=(),
                 stoppable=True, pausable=False, pid=None, heartbeat_timeout=0, milestones=None,
                 notify=None, meta=None, links=None, id=None, url=None, poll=True, throttle=0.5,
                 kill_control=True, cwd=None, progress_toast=None, toast_controls=False):
        """progress_toast=True: one live Windows notification with a progress bar (C# hub).
        toast_controls=True: built-in Stop / Pause / Resume also appear as buttons in notifications."""
        self.url = (url or URL).rstrip("/")
        self.name, self.id = name, id
        self.throttle = throttle
        self.stop_requested = False
        self.paused = False
        self.finished = False
        self.online = False
        self._handlers = {}
        self._queue = []
        self._qlock = threading.Lock()
        self._pending = {}
        self._last_flush = 0.0
        self._last_try = 0.0
        self._flush_timer = None
        self._lock = threading.RLock()
        self._poll = poll
        ctr = list(controls)
        if stoppable:
            ctr.insert(0, C.signal("stop", "Stop", style="primary", confirm="Stop the task (graceful)?",
                                   hint="Graceful stop: sets task.stop_requested", toast=toast_controls))
        if pausable:
            ctr.insert(0, C.signal("resume", "Resume", states=["paused"], toast=toast_controls))
            ctr.insert(0, C.signal("pause", "Pause", states=["running", "stale"], toast=toast_controls))
        n = {"milestones": milestones or []}
        n.update(notify or {})
        self._reg = _clean({"name": name, "total": total, "unit": unit,
                            "source": source or os.environ.get("TASKHUB_SOURCE", ""),
                            "description": description, "controls": ctr, "widgets": list(widgets),
                            "pid": os.getpid() if pid is None else (pid or None),
                            "heartbeat_timeout": heartbeat_timeout, "notify": n, "meta": meta, "links": links,
                            "id": id, "kill_control": kill_control, "cwd": cwd or os.getcwd(),
                            "progress_toast": progress_toast})
        if not kill_control:
            self._reg["kill_control"] = False

    # ---- plumbing
    def _call(self, method, path, body=None, timeout=5.0):
        try:
            r = request(method, path, body, timeout=timeout, url=self.url)
            self.online = True
            return r
        except HubError as e:
            if e.code == 404 and self.id and not self.finished and "/api/tasks/" in path:
                if self._register():  # hub lost the task (restart) -> re-register and retry once
                    try:
                        return request(method, path, body, timeout=timeout, url=self.url)
                    except Exception:
                        return None
            return None
        except Exception:
            self.online = False
            return None

    def _register(self):
        self._last_try = time.time()
        body = dict(self._reg)
        if self.id:
            body["id"] = self.id
        try:
            r = request("POST", "/api/tasks", body, url=self.url)
            self.id = r["id"]
            self.online = True
            return True
        except Exception:
            self.online = False
            return False

    def start(self):
        self._register()
        if self._poll:
            threading.Thread(target=self._poller, daemon=True, name="taskhub-poll").start()
        return self

    def _ensure(self):
        if not self.online and time.time() - self._last_try > 10:
            self._register()
        return self.online

    def _poller(self):
        while not self.finished:
            if not self.id or not self.online:
                time.sleep(3)
                self._ensure()
                continue
            r = self._call("GET", f"/api/tasks/{self.id}/commands?wait=25", timeout=35)
            if r is None:
                time.sleep(3)
                continue
            for d in r.get("commands", []):
                self._dispatch(Command(self, d))

    def _dispatch(self, cmd):
        c = cmd.control
        if c == "stop":
            self.stop_requested = True
            cmd.ack(True, "stop requested")
        elif c == "pause":
            self.paused = True
            self.update(state="paused", force=True)
            cmd.ack(True, "paused")
        elif c == "resume":
            self.paused = False
            self.update(state="running", force=True)
            cmd.ack(True, "resumed")
        if c in ("stop", "pause", "resume") and c not in self._handlers:
            return
        h = self._handlers.get(c)
        if h:
            try:
                res = h(cmd.value) if _nargs(h) else h()
                if not cmd.acked:
                    cmd.ack(True, "" if res is None else str(res))
            except Exception as e:
                cmd.ack(False, f"{type(e).__name__}: {e}")
        else:
            with self._qlock:
                self._queue.append(cmd)

    # ---- public API
    def on(self, control_id, handler):
        """handler(value) or handler() — runs in the poller thread; return value is shown in panel."""
        self._handlers[control_id] = handler
        return self

    def pending(self):
        """Commands without a handler (call cmd.ack(ok, msg) after handling)."""
        with self._qlock:
            q, self._queue = self._queue, []
        return q

    def wait_if_paused(self, poll=0.5):
        while self.paused and not self.stop_requested:
            time.sleep(poll)

    def update(self, force=False, **fields):
        """Merge fields and send (throttled). Fields: progress(0..1), percent, current, total, status, stage,
        metrics{}, widgets{}, state, add_widgets[], add_controls[], remove_*[], links[]"""
        if self.finished:
            return
        with self._lock:
            for k, v in fields.items():
                if k in ("metrics", "widgets") and isinstance(v, dict):
                    cur = self._pending.setdefault(k, {})
                    for wk, wv in v.items():
                        ser = self._chart_series(wk) if k == "widgets" else None
                        if ser is not None and wv is not None:
                            # keep every chart point between throttled flushes, with its own timestamp
                            pts = wv if _is_batch(wv) else [wv]
                            cur.setdefault(wk, [])
                            if not isinstance(cur[wk], list):
                                cur[wk] = []
                            cur[wk].extend(_chart_point(ser, x) for x in pts)
                        else:
                            cur[wk] = wv
                elif k == "add_widgets" and isinstance(v, list):
                    ids = {w.get("id") for w in v}
                    self._reg["widgets"] = [w for w in self._reg.get("widgets", []) if w.get("id") not in ids] + list(v)
                    self._pending.setdefault("add_widgets", []).extend(v)
                elif k == "log":
                    lst = self._pending.setdefault("log", [])
                    lst.extend(v if isinstance(v, list) else [v])
                else:
                    self._pending[k] = v
            due = force or time.time() - self._last_flush >= self.throttle
            if due:
                self._flush()
            elif not self._flush_timer:
                self._flush_timer = threading.Timer(self.throttle, self._timed_flush)
                self._flush_timer.daemon = True
                self._flush_timer.start()

    def _chart_series(self, wid):
        for w in self._reg.get("widgets", []):
            if w.get("id") == wid and w.get("type") == "chart":
                return w.get("series") or [wid]
        return None

    def _timed_flush(self):
        with self._lock:
            self._flush_timer = None
            self._flush()

    def _flush(self):
        if not self._pending or not self._ensure():
            return
        body, self._pending = self._pending, {}
        self._last_flush = time.time()
        self._call("POST", f"/api/tasks/{self.id}/update", body)

    def progress(self, current=None, total=None, *, fraction=None, percent=None, status=None, stage=None,
                 metrics=None, widgets=None, force=False):
        f = {}
        if current is not None:
            f["current"] = current
        if total is not None:
            f["total"] = total
        if fraction is not None:
            f["progress"] = fraction
        if percent is not None:
            f["percent"] = percent
        if status is not None:
            f["status"] = status
        if stage is not None:
            f["stage"] = stage
        if metrics:
            f["metrics"] = metrics
        if widgets:
            f["widgets"] = widgets
        self.update(force=force, **f)

    def set(self, **widget_values):
        """Shortcut: t.set(gpu=71, loss=0.12)  -> widget values."""
        self.update(widgets=widget_values)

    def status(self, text, force=False):
        self.update(status=text, force=force)

    def metric(self, **kv):
        self.update(metrics=kv)

    def log(self, msg, level="info", toast=False):
        """Log line; toast=True also pops a Windows notification (use for 'block finished' events)."""
        if toast:
            with self._lock:
                self._flush()
            self._ensure() and self._call("POST", f"/api/tasks/{self.id}/log",
                                          {"msg": msg, "level": level, "toast": True})
        else:
            self.update(log=msg, level=level) if level != "info" else self.update(log=msg)

    def heartbeat(self):
        if self._ensure():
            self._call("POST", f"/api/tasks/{self.id}/heartbeat", {})

    def finish(self, state="done", message="", result=None, widgets=None):
        if self.finished:
            return
        with self._lock:
            if self._flush_timer:
                self._flush_timer.cancel()
                self._flush_timer = None
            self._flush()
            self.finished = True
        if self._ensure():
            self._call("POST", f"/api/tasks/{self.id}/finish",
                       _clean({"state": state, "message": message, "result": result, "widgets": widgets}))

    def done(self, message="", **kw):
        self.finish("done", message, **kw)

    def fail(self, message="", **kw):
        self.finish("failed", message, **kw)

    def cancel(self, message="", **kw):
        self.finish("cancelled", message, **kw)

    def __enter__(self):
        return self.start()

    def __exit__(self, et, ev, tb):
        if self.finished:
            return False
        if et is None:
            if self.stop_requested:
                self.cancel("stopped by user")
            else:
                self.done()
        elif issubclass(et, KeyboardInterrupt):
            self.cancel("KeyboardInterrupt")
        else:
            self.fail(f"{et.__name__}: {ev}", result={"traceback": "".join(traceback.format_tb(tb))[-3000:]})
        return False


def _nargs(fn):
    try:
        import inspect
        return len(inspect.signature(fn).parameters) > 0
    except Exception:
        return True


def _chart_point(series, v):
    if isinstance(v, dict):
        d = dict(v)
    elif isinstance(v, (list, tuple)):
        d = dict(zip(series, v))
    else:
        d = {series[0]: v}
    d.setdefault("t", round(time.time(), 3))
    return d


def _is_batch(v):
    return isinstance(v, list) and v and isinstance(v[0], (list, dict))


# ----------------------------------------------------------------- CLI ---
def _cli_run(a):
    """Wrap an arbitrary command: logs, progress parsing, kill / interrupt / stdin controls."""
    cmd = a.cmd[1:] if a.cmd and a.cmd[0] == "--" else a.cmd
    if not cmd:
        sys.exit("run: no command given (use: run --name X -- cmd args)")
    is_win = os.name == "nt"
    flags = 0x00000200 if is_win else 0  # CREATE_NEW_PROCESS_GROUP (for CTRL_BREAK)
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.PIPE,
                            cwd=a.cwd, creationflags=flags, start_new_session=not is_win)
    prog_rx = re.compile(a.progress_regex) if a.progress_regex else None
    if a.auto_progress and not prog_rx:
        prog_rx = re.compile(r"(?P<pct>\d{1,3}(?:\.\d+)?)\s?%|(?P<cur>\d+)\s?/\s?(?P<tot>\d+)")
    stat_rx = re.compile(a.status_regex) if a.status_regex else None
    widgets = [W.text("last", "Last line", mono=True, span=2)]
    t = Task(a.name or " ".join(cmd)[:80], source=a.source, pid=proc.pid, stoppable=False,
             description=a.description or " ".join(cmd)[:300], heartbeat_timeout=a.heartbeat,
             widgets=widgets, milestones=a.milestones, cwd=a.cwd or os.getcwd(),
             controls=[C.signal("interrupt", "Ctrl+C", confirm="Надіслати Ctrl+C / SIGINT?"),
                       C.signal("stdin", "Send input", input=C.text("stdin"))])
    t.start()

    def interrupt():
        import signal as s
        if is_win:
            proc.send_signal(s.CTRL_BREAK_EVENT)
        else:
            os.killpg(proc.pid, s.SIGINT)
        return "sent"

    def send_stdin(v):
        proc.stdin.write((str(v) + "\n").encode())
        proc.stdin.flush()
        return "written"
    t.on("interrupt", interrupt).on("stdin", send_stdin)

    buf = b""
    last_line = ""
    out = sys.stdout.buffer if not a.quiet else None
    while True:
        chunk = proc.stdout.read1(4096) if hasattr(proc.stdout, "read1") else proc.stdout.read(1)
        if not chunk:
            break
        if out:
            out.write(chunk)
            out.flush()
        buf += chunk
        parts = re.split(rb"[\r\n]", buf)
        buf = parts.pop()
        for p in parts:
            line = p.decode("utf-8", "replace").strip()
            if not line:
                continue
            last_line = line
            if prog_rx:
                m = prog_rx.search(line)
                if m:
                    g = m.groupdict()
                    if g.get("pct"):
                        t.update(percent=float(g["pct"]))
                    elif g.get("cur") and g.get("tot"):
                        t.update(current=int(g["cur"]), total=int(g["tot"]))
                    elif m.groups():
                        try:
                            t.update(percent=float(m.group(1)))
                        except ValueError:
                            pass
            if stat_rx:
                m = stat_rx.search(line)
                if m:
                    t.update(status=m.group(1) if m.groups() else m.group(0))
            t.update(log=line, widgets={"last": line[:300]})
    rc = proc.wait()
    msg = f"exit code {rc}" + (f" · {last_line[:200]}" if last_line else "")
    t.finish("done" if rc == 0 else "failed", msg)
    sys.exit(rc)


def main():
    ap = argparse.ArgumentParser(prog="taskhub_client", description="TaskHub CLI")
    ap.add_argument("--url", default=None)
    sp = ap.add_subparsers(dest="cmd_name", required=True)

    r = sp.add_parser("run", help="run & monitor a command")
    r.add_argument("--name")
    r.add_argument("--source", default=os.environ.get("TASKHUB_SOURCE", "cli"))
    r.add_argument("--description")
    r.add_argument("--cwd")
    r.add_argument("--progress-regex", help="regex; named groups pct, or cur+tot, or first group = percent")
    r.add_argument("--auto-progress", action="store_true", help="detect 'NN%%' or 'a/b' in output")
    r.add_argument("--status-regex", help="regex; group 1 (or match) becomes status")
    r.add_argument("--heartbeat", type=float, default=0, help="stale alert after N s without output")
    r.add_argument("--milestones", type=float, nargs="*")
    r.add_argument("--quiet", action="store_true", help="don't echo child output")
    r.add_argument("cmd", nargs=argparse.REMAINDER)

    n = sp.add_parser("notify")
    n.add_argument("title")
    n.add_argument("message", nargs="?", default="")
    n.add_argument("--level", default="info", choices=["info", "warn", "error"])

    sp.add_parser("list")
    sp.add_parser("health")
    s = sp.add_parser("show")
    s.add_argument("id")
    c = sp.add_parser("control")
    c.add_argument("id")
    c.add_argument("control")
    c.add_argument("--value")
    g = sp.add_parser("register", help="register a task, print its id (for shell scripts)")
    g.add_argument("--name", required=True)
    g.add_argument("--source", default=os.environ.get("TASKHUB_SOURCE", "cli"))
    g.add_argument("--total", type=float)
    g.add_argument("--pid", type=int)
    g.add_argument("--json", help="extra registration JSON (controls, widgets...)")
    u = sp.add_parser("update")
    u.add_argument("id")
    u.add_argument("--percent", type=float)
    u.add_argument("--current", type=float)
    u.add_argument("--status")
    u.add_argument("--stage")
    u.add_argument("--log")
    u.add_argument("--toast", action="store_true")
    u.add_argument("--json", help="raw update JSON merged in (e.g. widgets)")
    f = sp.add_parser("finish")
    f.add_argument("id")
    f.add_argument("--state", default="done", choices=["done", "failed", "cancelled"])
    f.add_argument("--message", default="")
    o = sp.add_parser("panel", help="open the panel")
    o.add_argument("id", nargs="?")

    a = ap.parse_args()
    url = a.url
    pr = lambda x: print(json.dumps(x, ensure_ascii=False, indent=2))
    try:
        if a.cmd_name == "run":
            if url:
                global URL
                URL = url.rstrip("/")
            return _cli_run(a)
        if a.cmd_name == "notify":
            return pr(request("POST", "/api/notify", {"title": a.title, "message": a.message, "level": a.level}, url=url))
        if a.cmd_name == "health":
            return pr(request("GET", "/api/health", url=url))
        if a.cmd_name == "list":
            d = request("GET", "/api/tasks?brief=1", url=url)
            for t in d["tasks"]:
                p = f"{t['progress'] * 100:5.1f}%" if t.get("progress") is not None else "   -  "
                print(f"{t['id']:10} {t['state']:9} {p}  {t['name'][:50]:50}  {t.get('status', '')[:60]}")
            return
        if a.cmd_name == "show":
            return pr(request("GET", f"/api/tasks/{a.id}", url=url))
        if a.cmd_name == "control":
            v = a.value
            if v is not None:
                try:
                    v = json.loads(v)
                except ValueError:
                    pass
            return pr(request("POST", f"/api/tasks/{a.id}/controls/{a.control}",
                              {} if v is None else {"value": v}, url=url))
        if a.cmd_name == "register":
            b = {"name": a.name, "source": a.source}
            if a.total:
                b["total"] = a.total
            if a.pid:
                b["pid"] = a.pid
            if a.json:
                b.update(json.loads(a.json))
            print(request("POST", "/api/tasks", b, url=url)["id"])
            return
        if a.cmd_name == "update":
            b = {k: v for k, v in (("percent", a.percent), ("current", a.current), ("status", a.status),
                                   ("stage", a.stage), ("log", a.log)) if v is not None}
            if a.toast:
                b["toast"] = True
            if a.json:
                b.update(json.loads(a.json))
            return pr(request("POST", f"/api/tasks/{a.id}/update", b, url=url))
        if a.cmd_name == "finish":
            return pr(request("POST", f"/api/tasks/{a.id}/finish", {"state": a.state, "message": a.message}, url=url))
        if a.cmd_name == "panel":
            return pr(request("POST", "/api/ui/open", {"task": a.id}, url=url))
    except HubError as e:
        sys.exit(f"TaskHub error {e}")
    except OSError as e:
        sys.exit(f"TaskHub not reachable at {url or URL}: {e}")


if __name__ == "__main__":
    main()
