"""Interactive demo for the MAIN hub (default 47800): long TaskHub test (~20 min): all widget types, panel + toast controls, progress toast, milestones."""
import json, math, os, random, threading, time, urllib.request

U = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47800").rstrip("/")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def call(method, path, body=None, timeout=10):
    data = json.dumps(body or {}).encode() if method != "GET" else None
    r = urllib.request.Request(U + path, data=data, method=method,
                               headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    return json.loads(op.open(r, timeout=timeout).read() or b"null")


TOTAL = 600  # steps, 2 s each at speed 1
reg = {
    "id": "long-test", "name": "Довгий тест TaskHub", "source": "claude", "total": TOTAL, "unit": "крок",
    "description": "Перевірка панелі, віджетів, кнопок і сповіщень (~20 хв)",
    "pid": os.getpid(), "heartbeat_timeout": 60, "progress_toast": True,
    "notify": {"start": True, "milestones": [25, 50, 75]},
    "links": [{"label": "Тека TaskHub", "url": "file:///D:/Tools/TaskHub2"}],
    "widgets": [
        {"id": "loss", "type": "chart", "label": "Loss", "series": ["train", "val"], "kind": "line"},
        {"id": "thr", "type": "chart", "label": "Throughput", "unit": "it/s", "kind": "bar", "max_points": 40},
        {"id": "gpu", "type": "gauge", "label": "GPU", "unit": "%", "warn": 80, "crit": 95},
        {"id": "temp", "type": "gauge", "label": "Temp", "unit": "°C", "min": 30, "max": 100, "warn": 75, "crit": 90},
        {"id": "speed", "type": "stat", "label": "Швидкість", "unit": "x"},
        {"id": "err", "type": "stat", "label": "Помилки", "invert": True},
        {"id": "lic", "type": "led", "label": "Ліцензія"},
        {"id": "net", "type": "led", "label": "Мережа"},
        {"id": "sub", "type": "progress", "label": "Підетап", "total": 20},
        {"id": "kv", "type": "kv", "label": "Параметри"},
        {"id": "tbl", "type": "table", "label": "Останні точки", "columns": ["крок", "train", "val"]},
        {"id": "msg", "type": "text", "label": "Останнє повідомлення", "mono": True, "span": 2},
    ],
    "controls": [
        {"id": "pause", "label": "Пауза", "kind": "signal", "states": ["running", "stale"], "toast": True},
        {"id": "resume", "label": "Продовжити", "kind": "signal", "states": ["paused"], "style": "primary", "toast": True},
        {"id": "speed", "label": "Змінити швидкість", "kind": "signal", "toast": True,
         "input": {"type": "select", "options": ["0.5", "1", "2", "5"], "default": "1", "label": "Множник"}},
        {"id": "note", "label": "Додати нотатку", "kind": "signal", "input": {"type": "text", "placeholder": "текст"}},
        {"id": "jump", "label": "Перейти", "kind": "signal", "input": {"type": "number", "min": 0, "max": TOTAL, "default": 300}},
        {"id": "toast", "label": "Надіслати сповіщення", "kind": "signal", "input": {"type": "bool", "default": True, "label": "З кнопками?"}},
        {"id": "dir", "label": "Відкрити теку", "kind": "open", "target": "D:\\Tools\\TaskHub2"},
        {"id": "ping", "label": "ping (shell)", "kind": "shell", "command": "ping -n 2 127.0.0.1"},
        {"id": "stop", "label": "Стоп", "kind": "signal", "style": "danger", "confirm": "Зупинити тест?", "toast": True},
    ],
}
call("POST", "/api/tasks", reg)

state = {"step": 0, "paused": False, "speed": 1.0, "stop": False}


def poller():
    while not state["stop"]:
        try:
            cmds = call("GET", "/api/tasks/long-test/commands?wait=25", timeout=40)["commands"]
        except Exception:
            time.sleep(2); continue
        for c in cmds:
            cid, v, msg = c["control"], c.get("value"), "ok"
            if cid == "pause":
                state["paused"] = True; call("POST", "/api/tasks/long-test/update", {"state": "paused", "status": "на паузі"}); msg = "paused"
            elif cid == "resume":
                state["paused"] = False; call("POST", "/api/tasks/long-test/update", {"state": "running"}); msg = "resumed"
            elif cid == "speed":
                state["speed"] = float(v or 1); msg = f"speed x{state['speed']}"
            elif cid == "note":
                call("POST", "/api/tasks/long-test/log", {"msg": f"нотатка: {v}"}); msg = "logged"
            elif cid == "jump":
                state["step"] = int(v or 0); msg = f"step {state['step']}"
            elif cid == "toast":
                call("POST", "/api/tasks/long-test/update", {"toast": "Тестове сповіщення з кнопками" if v else True, "log": "toast test"}) if v else \
                    call("POST", "/api/notify", {"title": "TaskHub", "message": "Просте сповіщення без кнопок"})
                msg = "toast sent"
            elif cid == "stop":
                state["stop"] = True; msg = "stopping"
            call("POST", f"/api/tasks/long-test/commands/{c['id']}/ack", {"ok": True, "message": msg})


threading.Thread(target=poller, daemon=True).start()
pts, rows = [], []
t0 = time.time()
while state["step"] < TOTAL and not state["stop"]:
    if state["paused"]:
        time.sleep(0.5); continue
    s = state["step"] = state["step"] + 1
    tr = 2.0 * math.exp(-s / 150) + random.uniform(0, 0.05)
    va = tr + 0.1 + random.uniform(0, 0.08)
    rows = (rows + [[s, round(tr, 3), round(va, 3)]])[-6:]
    upd = {
        "current": s, "status": f"крок {s} / {TOTAL}", "stage": ["прогрів", "навчання", "валідація", "фініш"][min(3, s * 4 // TOTAL)],
        "widgets": {
            "loss": {"train": tr, "val": va}, "thr": round(random.uniform(40, 60) * state["speed"], 1),
            "gpu": round(60 + 35 * abs(math.sin(s / 20)) + random.uniform(-3, 3), 1),
            "temp": round(55 + 30 * abs(math.sin(s / 45)), 1), "speed": state["speed"],
            "err": s // 97, "lic": "ok", "net": ["ok", "ok", "ok", "warn", "busy"][s % 5],
            "sub": s % 20 + 1, "kv": {"lr": round(1e-3 * math.exp(-s / 300), 6), "batch": 32, "epoch": s // 50},
            "tbl": rows, "msg": f"[{time.strftime('%H:%M:%S')}] step {s}: train={tr:.3f} val={va:.3f}",
        },
        "metrics": {"elapsed_s": int(time.time() - t0)},
    }
    if s % 25 == 0:
        upd["log"] = f"checkpoint at step {s}"
    if s % 150 == 0:
        upd["log"] = f"етап {s // 150} завершено"; upd["toast"] = f"Етап {s // 150} з 4 завершено"
    try:
        call("POST", "/api/tasks/long-test/update", upd)
    except Exception:
        pass
    time.sleep(2.0 / state["speed"])

call("POST", "/api/tasks/long-test/finish", {"state": "cancelled" if state["stop"] else "done",
                                            "message": "зупинено кнопкою" if state["stop"] else f"{TOTAL} кроків за {int(time.time() - t0)} с"})
