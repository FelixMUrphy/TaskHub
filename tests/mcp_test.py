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
check("tools/list", set(names) == {"notify", "task_start", "task_update", "task_finish", "task_commands", "task_list"}, names)
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
tool("task_finish", id=tid2)
txt, err, _ = tool("task_finish", id=tid, state="done", message="готово")
check("task_finish", txt == "finished" and not err, txt)
txt, err, _ = tool("task_update", id="nope-404")
check("unknown task -> isError", err, txt)
r = rpc("tools/call", {"name": "nope", "arguments": {}})
check("unknown tool -> JSON-RPC error", "error" in r, r)
r = rpc("bogus/method")
check("unknown method -> -32601", r.get("error", {}).get("code") == -32601, r)

p.stdin.close()
p.wait(timeout=10)
check("exits on stdin close", p.returncode == 0, p.returncode)
check("nothing on stderr", not p.stderr.read(), "")
print(f"{fails} failure(s)")
sys.exit(1 if fails else 0)
