"""taskhub run presets: Blender's background-render output (a fake blender prints the lines of Blender 4.x and 5.0 as in
Blender's source) -> frame / samples status, overall progress, s/frame chart, saved files; "Error:" with exit code 0 ->
failed task and exit code 1; detection by the file name, --preset blender / none, an unknown preset; plain commands
unchanged (directives, exit code). Usage: blender_test.py PATH\\taskhub.exe"""
import json, os, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
CLI = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app", "taskhub.exe")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


def call(method, path, body=None):
    req = urllib.request.Request(URL + path, json.dumps(body).encode() if body is not None else None, method=method,
                                 headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    try:
        with op.open(req, timeout=30) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


def task(tid):
    s, r = call("GET", f"/api/tasks/{tid}")
    return r if s == 200 else None


def widget(t, wid):
    return next((w for w in (t or {}).get("widgets") or [] if w.get("id") == wid), None)


# ---- the fake blender: prints what Blender prints, scenario by its arguments / FAKE_BLENDER env
FAKE = r'''
import os, sys, time
a = sys.argv[1:]
mode = os.environ.get("FAKE_BLENDER", "v4")
pause = float(os.environ.get("FAKE_PAUSE", "0"))
def out(s):
    print(s, flush=True)
def frame4(f, eevee=False):
    for s in (32, 64, 96, 128):
        if eevee:
            out(f"Fra:{f} Mem:210.33M (Peak 260.10M) | Time:00:01.02 | Rendering {s // 2} / 64 samples")
        else:
            rem = "" if s == 32 else "Remaining:00:20.10 | "
            out(f"Fra:{f} Mem:210.33M (Peak 260.10M) | Time:00:03.21 | {rem}Mem:120.40M, Peak:130.10M | Scene, ViewLayer | Sample {s}/128")
        if s == 64:
            time.sleep(pause)
    out(f"Saved: 'C:\\out\\frame_{f:04d}.png'")
    out(f"Time: 00:0{f}.50 (Saving: 00:00.05)")
    out("")
out("Blender 4.2.3 LTS (hash 0e7f0b6d6a9d built 2024-10-15 00:00:00)")
if mode == "v4":
    for f in range(1, 4):
        frame4(f)
elif mode == "eevee":
    frame4(7, eevee=True)
elif mode == "v5":
    out("00:00.120  render           | Rendering animation (frames 10..11)")
    for f in (10, 11):
        out(f"00:01.000  render           | Rendering frame {f}")
        for s in (50, 100):
            out(f"00:02.000  render           | Fra: {f} | Remaining: 00:10.00 | Mem: 120M | Sample {s}/100")
        out(f"00:03.000  render           | Saved: 'C:\\out\\v5_{f}.png'")
        out(f"00:03.100  render           | Time: 00:02.00 (Saving: 00:00.01)")
        time.sleep(pause)   # the runner sends every ~0.35 s: one chart point per send
elif mode == "error":
    out("Fra:1 Mem:10.00M (Peak 10.00M) | Time:00:00.01 | Syncing Cube")
    out("Error: Cannot render, no camera")
out("")
out("Blender quit")
sys.exit(int(os.environ.get("FAKE_RC", "0")))
'''
tmp = tempfile.mkdtemp(prefix="taskhub-blender-")
fake = os.path.join(tmp, "fake_blender.py")
open(fake, "w", encoding="utf-8").write(FAKE)
blender_cmd = os.path.join(tmp, "blender.cmd")   # detection by the file name: blender(.exe / .cmd)
open(blender_cmd, "w").write(f'@"{sys.executable}" "{fake}" %*\r\n')


def run(tid, argv, env=None, preset=None, extra=()):
    e = dict(os.environ, TASKHUB_URL=URL, **(env or {}))
    cmd = [CLI, "run", "--id", tid, "--name", "render " + tid, "--quiet", *extra] + (["--preset", preset] if preset else []) + ["--"] + argv
    return subprocess.run(cmd, env=e, capture_output=True, text=True, timeout=120)


# 1) Blender 4.x Cycles, -s 1 -e 3 -a: 3 frames, progress while rendering, chart, last saved, message
seen = {}


def watch(tid, stop):
    while not stop.is_set():
        t = task(tid)
        if t and t.get("state") == "running" and t.get("status"):
            seen.setdefault(t["status"], t.get("progress"))
        time.sleep(0.1)


stop = threading.Event()
th = threading.Thread(target=watch, args=("bl4", stop), daemon=True)
th.start()
r = run("bl4", [blender_cmd, "-b", "scene.blend", "-s", "1", "-e", "3", "-a"], env={"FAKE_PAUSE": "1.2"})
stop.set(); th.join(2)
t = task("bl4")
check("v4 cycles: exit 0, task done", r.returncode == 0 and t and t["state"] == "done", (r.returncode, r.stderr, t and t["state"]))
check("v4: message '3 frames · last: …frame_0003.png'", t and t.get("message", "").startswith("3 frames · last: ")
      and t["message"].endswith("frame_0003.png"), t and t.get("message"))
mid = seen.get("Frame 2 · 2/3 · Sample 64/128")
check("v4: while rendering 'Frame 2 · 2/3 · Sample 64/128' at 50% overall", mid is not None and abs(mid - 0.5) < 0.01, seen)
spf = widget(t, "blender_spf")
check("v4: s/frame chart got 3 points (1.5, 2.5, 3.5 s)", spf and [p[1] if isinstance(p, list) else p.get("y", p.get("v"))
      for p in spf.get("points", [])][-3:] == [1.5, 2.5, 3.5], spf)
check("v4: 'Last saved' widget = the last file", (widget(t, "blender_saved") or {}).get("value", "").endswith("frame_0003.png"), widget(t, "blender_saved"))

# 2) EEVEE, -f 7: one frame
r = run("ble", [blender_cmd, "-b", "scene.blend", "-f", "7"], env={"FAKE_BLENDER": "eevee"})
t = task("ble")
check("eevee -f 7: done, progress 1, '1 frame · …'", r.returncode == 0 and t and t["state"] == "done" and t.get("progress") == 1
      and t.get("message", "").startswith("1 frame · "), t)

# 3) Blender 5.0 lines (CLOG prefix, range from 'Rendering animation (frames 10..11)'), explicit --preset with python
r = run("bl5", [sys.executable, fake, "-b", "scene.blend", "-a"], env={"FAKE_BLENDER": "v5", "FAKE_PAUSE": "0.8"}, preset="blender")
t = task("bl5")
check("v5 via --preset blender: 2 frames, done", r.returncode == 0 and t and t["state"] == "done"
      and t.get("message", "").startswith("2 frames · last: ") and t.get("progress") == 1, t)
check("v5: s/frame chart points", len((widget(t, "blender_spf") or {}).get("points", [])) == 2, widget(t, "blender_spf"))

# 4) a failed render that exits with 0
r = run("blx", [blender_cmd, "-b", "scene.blend", "-f", "1"], env={"FAKE_BLENDER": "error"})
t = task("blx")
check("error with exit 0 -> task failed, 'Blender: Cannot render, no camera'", t and t["state"] == "failed"
      and t.get("message") == "Blender: Cannot render, no camera", t and (t["state"], t.get("message")))
check("…and taskhub run exits with 1", r.returncode == 1, r.returncode)
lines = t.get("log") or []
check("…the Error line is logged as an error", any(x.get("level") == "error" and "no camera" in x.get("msg", "") for x in lines), lines[-5:])

# 5) real non-zero exit code stays as it is
r = run("blr", [blender_cmd, "-b", "scene.blend", "-f", "1"], env={"FAKE_BLENDER": "error", "FAKE_RC": "3"})
check("blender exit 3 -> exit 3, failed", r.returncode == 3 and task("blr")["state"] == "failed", r.returncode)

# 6) --preset none / own regex: no Blender parsing; unknown preset -> usage error
r = run("bln", [blender_cmd, "-b", "scene.blend", "-f", "1"], env={"FAKE_BLENDER": "error"}, preset="none")
t = task("bln")
check("--preset none: exit 0 stays done, no Blender widgets", r.returncode == 0 and t["state"] == "done" and widget(t, "blender_spf") is None, t)
r = run("blg", [blender_cmd, "-b", "scene.blend", "-f", "1"], env={"FAKE_BLENDER": "error"}, extra=["--auto-progress"])
check("--auto-progress given: preset not applied", r.returncode == 0 and task("blg")["state"] == "done", r.returncode)
r = run("blu", [blender_cmd, "-b", "x"], preset="maya")
check("unknown preset -> exit 2 with the known names", r.returncode == 2 and "blender" in r.stderr and task("blu") is None, (r.returncode, r.stderr))

# 7) a plain command: directives, exit code, message as before
r = run("plain", [sys.executable, "-c", "print('::progress 1/2'); print('half'); raise SystemExit(4)"])
t = task("plain")
check("plain command: exit 4, failed, 'exit code 4 · half'", r.returncode == 4 and t["state"] == "failed" and t.get("message") == "exit code 4 · half"
      and t.get("current") == 1 and t.get("total") == 2 and widget(t, "blender_spf") is None, t)

print("blender tests:", "OK" if fails == 0 else f"{fails} FAILED")
raise SystemExit(1 if fails else 0)
