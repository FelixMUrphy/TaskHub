"""Parallel workers stress test: N processes report to TaskHub at once; verifies nothing is lost."""
import json, multiprocessing as mp, random, sys, threading, time, urllib.request

import os
U = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899").rstrip("/")
N = int(sys.argv[1]) if len(sys.argv) > 1 else 8
STEPS = 40


def call(method, path, body=None, timeout=15):
    op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    data = json.dumps(body or {}).encode() if method != "GET" else None
    r = urllib.request.Request(U + path, data=data, method=method,
                               headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    t0 = time.perf_counter()
    res = json.loads(op.open(r, timeout=timeout).read() or b"null")
    return res, time.perf_counter() - t0


def worker(i, q):
    import os
    random.seed(i)
    tid = f"par-{i}"
    final = ["done", "done", "done", "failed", "cancelled"][i % 5]
    lat, errs, logs = [], 0, 0
    try:
        _, dt = call("POST", "/api/tasks", {
            "id": tid, "name": f"Воркер {i + 1}", "source": "stress", "total": STEPS, "pid": os.getpid(),
            "notify": {"start": True, "milestones": [50]},
            "widgets": [{"id": "c", "type": "chart", "label": "val"}, {"id": "g", "type": "gauge", "label": "load", "unit": "%"}],
            "controls": [{"id": "stop", "label": "Stop", "kind": "signal", "toast": True}]})
        lat.append(dt)
        for s in range(1, STEPS + 1):
            body = {"current": s, "status": f"w{i + 1} step {s}", "log": f"w{i + 1} line {s}",
                    "widgets": {"c": random.random() * 100, "g": random.randint(0, 100)}}
            if s % 20 == 0:
                body["toast"] = f"Воркер {i + 1}: крок {s}"
            try:
                _, dt = call("POST", f"/api/tasks/{tid}/update", body)
                lat.append(dt); logs += 1
            except Exception:
                errs += 1
            time.sleep(random.uniform(0.05, 0.25))
        _, dt = call("POST", f"/api/tasks/{tid}/finish", {"state": final, "message": f"worker {i + 1} -> {final}"})
        lat.append(dt)
    except Exception as e:
        errs += 1
        print("worker", i, "error", e, flush=True)
    q.put((i, final, logs, errs, lat))


def burst(k=20):
    res = []
    def one(j):
        try:
            _, dt = call("POST", "/api/notify", {"title": f"Пакет {j + 1}/{k}", "message": "одночасне сповіщення", "force": True})
            res.append(dt)
        except Exception as e:
            res.append(None)
    ths = [threading.Thread(target=one, args=(j,)) for j in range(k)]
    [t.start() for t in ths]; [t.join() for t in ths]
    return res


if __name__ == "__main__":
    t0 = time.time()
    q = mp.Queue()
    ps = [mp.Process(target=worker, args=(i, q)) for i in range(N)]
    [p.start() for p in ps]
    time.sleep(2)
    b = burst(20)
    [p.join() for p in ps]
    results = [q.get() for _ in range(N)]
    time.sleep(1)
    snap, _ = call("GET", "/api/tasks")
    tasks = {t["id"]: t for t in snap["tasks"]}
    ok = True
    print(f"{N} workers, {STEPS} updates each, wall {time.time() - t0:.1f}s")
    all_lat = []
    for i, final, logs, errs, lat in sorted(results):
        all_lat += lat
        t = tasks.get(f"par-{i}")
        got_lines = sum(1 for l in t["log"] if l["msg"].startswith(f"w{i + 1} line")) if t else -1
        pts = len(next(w for w in t["widgets"] if w["id"] == "c")["points"]) if t else -1
        good = t is not None and t["state"] == final and got_lines == STEPS and errs == 0 and t["current"] == STEPS and pts == STEPS
        ok &= good
        print(f"  worker {i + 1}: state={t and t['state']:<9} expected={final:<9} log={got_lines}/{STEPS} chart={pts} http_errors={errs} {'OK' if good else 'FAIL'}")
    bad = sum(1 for x in b if x is None)
    print(f"burst: 20 parallel /api/notify -> {20 - bad} ok, {bad} failed, max {max(x for x in b if x) * 1000:.0f} ms")
    all_lat.sort()
    print(f"latency: n={len(all_lat)} median {all_lat[len(all_lat) // 2] * 1000:.1f} ms, p95 {all_lat[int(len(all_lat) * .95)] * 1000:.1f} ms, max {all_lat[-1] * 1000:.1f} ms")
    print("RESULT:", "PASS" if ok and bad == 0 else "FAIL")
