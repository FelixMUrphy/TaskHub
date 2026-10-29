"""TaskHub demo: every widget type + controls. Run while the hub is up:  python demo.py"""
import math
import random
import time

from taskhub_client import C, Task, W

blocks = 10
with Task("Demo: parameter sweep", total=200, unit="pts", source="demo",
          description="Synthetic task showing all widgets and controls",
          widgets=[
              W.progress("block", "Block", total=blocks),
              W.chart("s", "Response", series=["A", "B"], unit="dB", max_points=150),
              W.gauge("gpu", "GPU load", unit="%", warn=80, crit=95),
              W.gauge("vram", "VRAM", max=24, unit="GB", decimals=1, warn=20, crit=23),
              W.stat("loss", "Loss", decimals=4, invert=True),
              W.led("lic", "License server"),
              W.chart("thr", "Throughput", kind="bar", unit="pts/s", max_points=40),
              W.kv("cfg", "Config"),
              W.table("best", "Best points", columns=["x", "A, dB", "ratio"]),
              W.text("cur", "Current file", mono=True, span=2),
          ],
          controls=[
              C.signal("snapshot", "Save snapshot"),
              C.signal("step", "Set step", input=C.number("MHz", default=5, min=1, max=50)),
              C.signal("mode", "Mode", input=C.select(["fast", "accurate", "debug"], default="fast")),
              C.open("folder", "Open folder", "."),
          ],
          pausable=True, milestones=[50], heartbeat_timeout=30) as t:
    step = 5
    t.on("snapshot", lambda: f"saved snapshot_{int(time.time())}.npz")

    def set_step(v):
        global step
        step = v
        return f"step = {v} MHz"
    t.on("step", set_step)
    t.on("mode", lambda v: f"mode -> {v}")
    t.set(cfg={"method": "grid", "points": 200, "x_start": 1.7, "x_stop": 2.2}, lic="ok")

    best = []
    loss = 1.0
    for i in range(200):
        t.wait_if_paused()
        if t.stop_requested:
            break
        f = 1.7 + i * 0.0025
        s11 = -3 - 22 * math.exp(-((f - 1.95) / 0.04) ** 2) + random.uniform(-0.4, 0.4)
        s21 = -0.3 - 0.4 * random.random()
        loss *= 0.985
        vswr = (1 + 10 ** (s11 / 20)) / (1 - 10 ** (s11 / 20))
        best = sorted(best + [[round(f, 4), round(s11, 2), round(vswr, 3)]], key=lambda r: r[1])[:4]
        blk = i // (200 // blocks)
        t.progress(i + 1, status=f"x = {f:.4f}", stage=f"block {blk + 1}/{blocks}", widgets={
            "s": {"A": s11, "B": s21},
            "gpu": 60 + 35 * abs(math.sin(i / 15)),
            "vram": 14 + 9 * abs(math.sin(i / 40)),
            "loss": loss,
            "thr": 8 + random.uniform(-2, 2),
            "best": best,
            "block": blk + (i % 20) / 20,
            "cur": f"results/sweep_{blk:02d}/pt_{i:03d}.s2p",
            "lic": "warn" if 120 < i < 140 else "ok",
        })
        if i % 20 == 19:
            t.log(f"block {blk + 1} finished", toast=(blk + 1) in (5,))
        for cmd in t.pending():
            cmd.ack(True, "unhandled")
        time.sleep(0.15)
    if not t.stop_requested:
        t.done("sweep complete", widgets={"lic": "off"})
