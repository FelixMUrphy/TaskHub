"""`taskhub mcp` (stdio MCP server) against the test hub (TASKHUB_URL). Usage: python mcp_test.py [path\\to\\taskhub.exe]"""
import json, os, subprocess, sys

cli = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app", "taskhub.exe")
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


p = subprocess.Popen([cli, "mcp", "--source", "mcptest"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
n = 0


def rpc(method, params=None, notify=False):
    global n
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None:
        msg["params"] = params
    if not notify:
        n += 1
        msg["id"] = n
    p.stdin.write((json.dumps(msg, ensure_ascii=False) + "\n").encode("utf-8"))
    p.stdin.flush()
    if notify:
        return None
    line = p.stdout.readline().decode("utf-8")
    return json.loads(line)


def tool(_tool, **args):
    r = rpc("tools/call", {"name": _tool, "arguments": args})
    res = r.get("result", {})
    return res.get("content", [{}])[0].get("text", ""), res.get("isError", False), r


r = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "test", "version": "1"}})
res = r.get("result", {})
check("initialize", res.get("serverInfo", {}).get("name") == "taskhub" and "tools" in res.get("capabilities", {})
      and res.get("protocolVersion") == "2025-06-18" and "Do NOT add start or intermediate" in res.get("instructions", ""), r)
rpc("notifications/initialized", notify=True)
r = rpc("tools/list")
names = [t["name"] for t in r.get("result", {}).get("tools", [])]
check("tools/list", set(names) == {"notify", "task_start", "task_update", "task_finish", "task_commands", "task_wait", "task_list"}, names)
check("tool schemas", all(t["inputSchema"]["type"] == "object" for t in r["result"]["tools"]), r)

txt, err, _ = tool("notify", title="MCP test", message="привіт")
check("notify", txt == "shown" and not err, txt)
tid, err, raw = tool("task_start", name="MCP задача", total=10, unit="steps",
                     controls=[{"id": "go", "label": "Go"}])
check("task_start -> id", tid and not err, raw)
txt, err, _ = tool("task_update", id=tid, current=4, status="кроки")
check("task_update", txt == "ok" and not err, txt)
lst, err, _ = tool("task_list")
items = json.loads(lst) if lst.startswith("[") else []
mine = [t for t in items if t["id"] == tid]
check("task_list shows the task with progress", mine and abs(mine[0]["progress"] - 0.4) < 1e-6 and mine[0]["status"] == "кроки", lst)

# a button press arrives through task_commands
import urllib.request
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
url = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
req = urllib.request.Request(f"{url}/api/tasks/{tid}/controls/go", b"{}", method="POST",
                             headers={"Content-Type": "application/json", "X-TaskHub": "1"})
op.open(req, timeout=5).read()
txt, err, _ = tool("task_commands", id=tid, wait_seconds=2)
check("task_commands returns the press", '"control":"go"' in txt and not err, txt)
txt, err, _ = tool("task_commands", id=tid)
check("task_commands: acknowledged, not repeated", txt == "no pending commands", txt)

full = json.loads(op.open(f"{url}/api/tasks/{tid}", timeout=5).read())
check("defaults: no start / milestone notifications", full["notify"]["start"] is False and full["notify"]["milestones"] == [], full["notify"])
tid2, err, raw = tool("task_start", name="with notifications", notify_start=True, notify_milestones=[50])
full2 = json.loads(op.open(f"{url}/api/tasks/{tid2}", timeout=5).read())
check("notify_start / notify_milestones passed through", full2["notify"]["start"] is True and full2["notify"]["milestones"] == [50], full2["notify"])
# task_wait: still running after wait_seconds -> finished false; a finish from another client ends the wait
import threading, time
t0 = time.time()
txt, err, _ = tool("task_wait", id=tid2, wait_seconds=1)
w = json.loads(txt) if txt.startswith("{") else {}
check("task_wait: running task -> finished false after ~wait_seconds", not err and w.get("finished") is False
      and w.get("state") == "running" and 0.8 < time.time() - t0 < 8, (txt, time.time() - t0))


def finish_later():
    time.sleep(1.5)
    req = urllib.request.Request(f"{url}/api/tasks/{tid2}/finish", json.dumps({"state": "failed", "message": "зламалось"}).encode(),
                                 method="POST", headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    op.open(req, timeout=5).read()


threading.Thread(target=finish_later, daemon=True).start()
t0 = time.time()
txt, err, _ = tool("task_wait", id=tid2, wait_seconds=20)
w = json.loads(txt) if txt.startswith("{") else {}
check("task_wait returns when the task finishes, with state and message", not err and w.get("finished") is True
      and w.get("state") == "failed" and w.get("message") == "зламалось" and time.time() - t0 < 10, (txt, time.time() - t0))
txt, err, _ = tool("task_wait", id="nope-404", wait_seconds=1)
check("task_wait: unknown task -> isError", err, txt)
txt, err, _ = tool("task_finish", id=tid, state="done", message="готово")
check("task_finish", txt == "finished" and not err, txt)
txt, err, _ = tool("task_update", id="nope-404")
check("unknown task -> isError", err, txt)
r = rpc("tools/call", {"name": "nope", "arguments": {}})
check("unknown tool -> JSON-RPC error", "error" in r, r)
r = rpc("bogus/method")
check("unknown method -> -32601", r.get("error", {}).get("code") == -32601, r)

# ---- calls side by side: a task_wait must not hold other requests; a cancelled call gets no answer, the server goes on
def send(msg):
    p.stdin.write((json.dumps(msg, ensure_ascii=False) + "\n").encode("utf-8"))
    p.stdin.flush()


def read_line(timeout):
    box = []
    th = threading.Thread(target=lambda: box.append(p.stdout.readline()), daemon=True)
    th.start()
    th.join(timeout)
    return json.loads(box[0].decode("utf-8")) if box and box[0] else None


txt, err, _ = tool("task_start", name="mcp parallel")
tid3 = txt
send({"jsonrpc": "2.0", "id": 9001, "method": "tools/call", "params": {"name": "task_wait", "arguments": {"id": tid3, "wait_seconds": 20}}})
t0 = time.time()
send({"jsonrpc": "2.0", "id": 9002, "method": "ping"})
first = read_line(10)
check("ping answered while a task_wait waits", first is not None and first.get("id") == 9002 and time.time() - t0 < 3,
      (first, time.time() - t0))
send({"jsonrpc": "2.0", "id": 9003, "method": "tools/call", "params": {"name": "task_list", "arguments": {}}})
nxt = read_line(10)
check("a tool call answered while a task_wait waits", nxt is not None and nxt.get("id") == 9003 and time.time() - t0 < 5,
      (nxt, time.time() - t0))
send({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 9001, "reason": "test"}})
time.sleep(1)
send({"jsonrpc": "2.0", "id": 9004, "method": "ping"})
nxt = read_line(10)
check("cancelled task_wait: no answer for it, the next request is answered", nxt is not None and nxt.get("id") == 9004, nxt)
nxt = read_line(2)
check("…and nothing comes later for the cancelled call", nxt is None, nxt)
# finished over HTTP: a reader thread from the last check may still wait on stdout
op.open(urllib.request.Request(f"{url}/api/tasks/{tid3}/finish", json.dumps({"state": "done"}).encode(), method="POST",
                               headers={"Content-Type": "application/json", "X-TaskHub": "1"}), timeout=5).read()

p.stdin.close()
p.wait(timeout=10)
check("exits on stdin close", p.returncode == 0, p.returncode)
check("nothing on stderr", not p.stderr.read(), "")

# ---- the parent ends while the stdin pipe stays open (another process holds it): the server ends too
import ctypes
r_fd, w_fd = os.pipe()
parent = subprocess.Popen([sys.executable, "-c", "import subprocess, sys, time; c = subprocess.Popen([sys.argv[1], 'mcp', '--source', 'mcptest'], "
                           "stdin=sys.stdin, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL); print(c.pid, flush=True); time.sleep(120)", cli],
                          stdin=r_fd, stdout=subprocess.PIPE)
os.close(r_fd)
cpid = int(parent.stdout.readline())
k32 = ctypes.windll.kernel32
h = k32.OpenProcess(0x00100000 | 0x1000, False, cpid)   # SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
time.sleep(11)   # a parent that ends in the first 10 s counts as a launcher (kept running); this one is the client
alive_before = k32.WaitForSingleObject(h, 0) == 0x102   # WAIT_TIMEOUT: still running
parent.kill()
parent.wait()
ended = k32.WaitForSingleObject(h, 10000) == 0
if not ended:
    subprocess.run(["taskkill", "/F", "/PID", str(cpid)], capture_output=True)
k32.CloseHandle(h)
os.close(w_fd)
check("exits when its parent ends, though stdin stays open", h and alive_before and ended, (cpid, alive_before, ended))

# ---- a launcher that starts the server and leaves at once is not the client: the server keeps running until stdin ends
r_fd, w_fd = os.pipe()
launcher = subprocess.Popen([sys.executable, "-c", "import subprocess, sys; c = subprocess.Popen([sys.argv[1], 'mcp', '--source', 'mcptest'], "
                             "stdin=sys.stdin, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL); print(c.pid, flush=True)", cli],
                            stdin=r_fd, stdout=subprocess.PIPE)
os.close(r_fd)
cpid = int(launcher.stdout.readline())
h = k32.OpenProcess(0x00100000 | 0x1000, False, cpid)
launcher.wait()
time.sleep(3)
kept = k32.WaitForSingleObject(h, 0) == 0x102
os.close(w_fd)   # stdin EOF
gone = k32.WaitForSingleObject(h, 10000) == 0
if not gone:
    subprocess.run(["taskkill", "/F", "/PID", str(cpid)], capture_output=True)
k32.CloseHandle(h)
check("a launcher that leaves at once doesn't end it; stdin EOF does", h and kept and gone, (cpid, kept, gone))
print(f"{fails} failure(s)")
sys.exit(1 if fails else 0)
