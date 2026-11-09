"""Runs one test script for run_tests.cmd: passes its output through, its exit code back, and adds its PASS / FAIL / SKIP lines to the run's totals.
Usage: run_one.py TOTALS_FOLDER script.py [args]"""
import os, subprocess, sys

folder, cmd = sys.argv[1], sys.argv[2:]
counts = {b"PASS": 0, b"FAIL": 0, b"SKIP": 0}
skips = []
p = subprocess.Popen([sys.executable] + cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
for line in p.stdout:
    sys.stdout.buffer.write(line)
    sys.stdout.buffer.flush()
    head = line[:4]
    if head in counts and line[4:5] in (b" ", b"\r", b"\n"):
        counts[head] += 1
        if head == b"SKIP":
            skips.append(line.decode("utf-8", "replace").strip())
rc = p.wait()
os.makedirs(folder, exist_ok=True)
with open(os.path.join(folder, "totals.txt"), "a", encoding="utf-8") as f:
    f.write(f"{counts[b'PASS']} {counts[b'FAIL']} {counts[b'SKIP']} {rc} {cmd[0]}\n")
with open(os.path.join(folder, "skips.txt"), "a", encoding="utf-8") as f:
    for s in skips:
        f.write(f"{cmd[0]}: {s}\n")
sys.exit(rc)
