"""The card offering an agent's hooks (what is offered, one notification, nothing written, no way to choose over HTTP) and Stop held back
right after the agent spoke through MCP (real `taskhub mcp`, real working folder). Own port, temporary home and data: never the real
home, the real configs or the real hub. The click itself is a WebView2 message, not reachable from here. Argument: TaskHubHost.exe."""
import json, os, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.request

EXE = sys.argv[1]
CLI = os.path.join(os.path.dirname(EXE), "taskhub.exe")
PORT = 47888
URL = f"http://127.0.0.1:{PORT}"
home = tempfile.mkdtemp(prefix="taskhub-offer-home-")
data = tempfile.mkdtemp(prefix="taskhub-offer-data-")
project = tempfile.mkdtemp(prefix="taskhub-offer-project-")
# no claude / codex on PATH: on a developer's PC they would count as "used here"
ENV = {k: v for k, v in os.environ.items() if k.upper() != "PATH"}
ENV.update(PATH=os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32"), TASKHUB_TEST_HOME=home, TASKHUB_TOKEN="test",
           TASKHUB_NO_AUTOSTART="1", TASKHUB_URL=URL, APPDATA=data, TASKHUB_TEST_HOOK_OFFER="1")
cfg = os.path.join(data, "config.json")
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(),
                               headers={"X-TaskHub": "1", "Authorization": "Bearer test", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=10) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def log_lines():
    try:
        with open(os.path.join(data, "taskhub.log"), encoding="utf-8", errors="replace") as f:
            return f.readlines()
    except OSError:
        return []


def toasts(what=""):
    return [l.split("TOAST ", 1)[1].strip() for l in log_lines() if "TOAST " in l and what in l]


def write_cfg(**extra):
    o = json.load(open(cfg, encoding="utf-8")) if os.path.exists(cfg) else {"toasts": False, "autostart": False, "toast_rate": 0, "language": "en"}
    o.update(extra)
    with open(cfg, "w", encoding="utf-8") as f:
        json.dump(o, f)


def reset(*dirs, **cfg_values):
    """Stopped hub: a home with only these folders, this config."""
    shutil.rmtree(home, ignore_errors=True)
    os.makedirs(home)
    for d in dirs:
        os.makedirs(os.path.join(home, d))
    if os.path.exists(cfg):
        os.remove(cfg)
    for name in ("taskhub.log", "state.json"):
        if os.path.exists(os.path.join(data, name)):
            os.remove(os.path.join(data, name))
    write_cfg(**cfg_values)


class Hub:
    def __init__(self, env=None):
        self.env = env or ENV

    def __enter__(self):
        self.p = subprocess.Popen([EXE, "--port", str(PORT), "--data", data], env=self.env)
        for _ in range(100):
            try:
                if call("GET", "/api/health")[0] == 200:
                    break
            except OSError:
                pass
            time.sleep(.1)
        return self

    def __exit__(self, *a):
        self.p.kill()
        self.p.wait(10)


def offer(wait=8):
    """After the first watchdog tick (3 s): the rows in /api/tasks."""
    end = time.time() + wait
    rows = []
    while time.time() < end:
        rows = call("GET", "/api/tasks")[1].get("hooks_offer", [])
        if rows:
            return rows
        time.sleep(.2)
    return rows


def files_in_home():
    return sorted(os.path.relpath(os.path.join(r, f), home) for r, _, fs in os.walk(home) for f in fs)


try:
    # ---- what is offered
    reset()
    with Hub():
        check("no agent on this PC: no card", offer(5) == [], offer(0))
        check("...no notification", toasts("[local]") == [], toasts())
        check("field always there", call("GET", "/api/tasks")[1].get("hooks_offer") == [] and call("GET", "/api/tasks")[1].get("hooks_offer_tg") is False)

    reset(".codex")
    with Hub():
        rows = offer()
        check("agent found, no choice: offered", rows == [{"agent": "codex", "name": "Codex"}], rows)
        time.sleep(.5)
        check("...one notification naming it", len(toasts("[local]")) == 1 and "Codex" in toasts("[local]")[0], toasts())
        check("...nothing written in the agent's folder", files_in_home() == [], files_in_home())
        for path, body in (("/api/settings", {"integration_codex": True}), ("/api/hooks/codex", {"connect": True}), ("/api/settings", {"hooks_offer": True})):
            call("POST", path, body)
        check("...no way to connect over HTTP", files_in_home() == [], files_in_home())
    with Hub():
        check("after a restart still offered", offer() == [{"agent": "codex", "name": "Codex"}])
        time.sleep(.5)
        check("...not told again", len(toasts("[local]")) == 1, toasts())
    print("told:", json.load(open(cfg, encoding="utf-8")).get("hooks_offer_told"), flush=True)

    reset(".claude", ".codex")
    with Hub():
        rows = offer()
        check("two agents: both on the card", [r["agent"] for r in rows] == ["claude", "codex"], rows)
        time.sleep(.5)
        check("...one notification with both", len(toasts("[local]")) == 1 and "Claude Code, Codex" in toasts("[local]")[0], toasts())

    reset(".claude", ".codex", integration_claude=False)
    with Hub():
        rows = offer()
        check("'not now' for one agent, a new agent found: only the new one", [r["agent"] for r in rows] == ["codex"], rows)

    reset(".codex", integration_codex=False)
    with Hub():
        check("'not now' remembered: no card", offer(5) == [], offer(0))
        check("...no notification", toasts("[local]") == [], toasts())

    reset(".codex")
    with open(os.path.join(data, "state.json"), "w", encoding="utf-8") as f:
        json.dump({"muted": True, "tasks": []}, f)
    with Hub():
        check("muted: the card is there", len(offer()) == 1)
        time.sleep(.5)
        check("...the notification waits", toasts("[local]") == [], toasts())

    reset(".codex")
    subprocess.run([EXE, "--data", data, "--integrations", "codex=on"], env=ENV, capture_output=True, timeout=30)
    with Hub():
        check("hooks on: no card", offer(5) == [], offer(0))
    reset(".codex")
    subprocess.run([EXE, "--data", data, "--integrations", "codex=on"], env=ENV, capture_output=True, timeout=30)
    o = json.load(open(cfg, encoding="utf-8"))
    o.pop("integration_codex", None)
    json.dump(o, open(cfg, "w", encoding="utf-8"))
    with Hub():
        check("hooks on with no remembered choice: no card", offer(5) == [], offer(0))

    reset(".codex")
    with Hub({k: v for k, v in ENV.items() if k != "TASKHUB_TEST_HOOK_OFFER"}):
        check("a dev hub offers nothing", offer(5) == [], offer(0))
        check("...and tells nothing", toasts("[local]") == [], toasts())

    # ---- the setup window's switches: an agent that isn't here is no answer
    def setup_args(*switches):
        out = os.path.join(data, "setup_args.txt")
        subprocess.run([EXE, "--data", data, "--selftest-setup-args", out, *switches], env=ENV, capture_output=True, timeout=30)
        return open(out, encoding="utf-8").read().split()

    reset(".claude")
    check("a switch on is passed", setup_args("claude=on") == ["--claude"], setup_args("claude=on"))
    check("an agent here, switched off: the user's answer", setup_args("claude=off") == ["--no-claude"], setup_args("claude=off"))
    check("an agent not here, switch left off: no answer stored", setup_args("codex=off", "codex-mcp=off") == [], setup_args("codex=off", "codex-mcp=off"))
    check("an agent not here, switched on: passed", setup_args("codex=on") == ["--codex"], setup_args("codex=on"))
    reset(".claude", integration_codex=False)
    check("an agent not here with an earlier answer: kept as an update does", setup_args("codex=off") == ["--no-codex"], setup_args("codex=off"))

    # ---- Stop right after the agent spoke
    reset()
    n = [0]

    def stop(agent="claude", cwd=project, event="Stop", **extra):
        n[0] += 1
        before = len(toasts())
        marker = f"answer {n[0]}"
        call("POST", f"/api/hooks/{agent}", dict({"hook_event_name": event, "cwd": cwd, "session_id": "s", "prompt_id": str(n[0]),
                                                  "last_assistant_message": marker, "message": marker, "tool_name": "Bash"}, **extra))
        end = time.time() + 1.2
        while time.time() < end and len(toasts()) == before:
            time.sleep(.05)
        return len(toasts()) > before

    def spoke(agent="claude", cwd=project):
        check(f"notify from {agent}", call("POST", "/api/notify", {"title": "t", "message": "m", "source": agent, "agent_cwd": cwd})[0] == 200)
        time.sleep(.3)

    with Hub():
        check("Stop alone: a toast", stop())
        spoke()
        check("Stop right after a notify from that agent and folder: none", not stop())
        check("...it says so in the log, without the text", any("Stop: no toast" in l and "answer" not in l for l in log_lines()))
        check("...once: the next Stop has its toast", stop())
        spoke()
        check("another project: its toast", stop(cwd=project + "-other"))
        check("...this project's mark still counts", not stop())
        spoke()
        check("another agent: its toast", stop(agent="codex"))
        check("...this agent's mark still counts", not stop())
        spoke(cwd=project.upper() + "\\")
        check("same folder, other case and a trailing slash: none", not stop())
        spoke()
        check("a Notification is never held back", stop(event="Notification"))
        check("a PermissionRequest is never held back", stop(event="PermissionRequest"))
        check("...and the Stop after them still is", not stop())
        check("a notify with no agent: nothing held back", call("POST", "/api/notify", {"title": "t", "message": "m"})[0] == 200 and stop())
        call("POST", "/api/notify", {"title": "t", "message": "m", "source": "script", "agent_cwd": project})
        check("a notify from something else: nothing held back", stop())
        call("POST", "/api/notify", {"title": "t", "message": "m", "source": "claude"})
        check("a notify with no folder: nothing held back", stop())
        spoke(cwd=" ")
        check("an empty folder is no folder", stop(cwd=" "))

        tid = call("POST", "/api/tasks", {"name": "build", "source": "claude"})[1]["id"]
        check("finish", call("POST", f"/api/tasks/{tid}/finish", {"state": "done", "source": "claude", "agent_cwd": project})[0] == 200)
        time.sleep(.3)
        check("Stop right after a finished task: none", not stop())
        s = call("POST", "/api/tasks/no-such-task/finish", {"state": "done", "source": "claude", "agent_cwd": project})[0]
        check("a finish that failed marks nothing", s == 404 and stop(), s)
        check("finishing a finished task again told nothing: Stop has its toast",
              call("POST", f"/api/tasks/{tid}/finish", {"state": "done", "source": "claude", "agent_cwd": project})[0] == 200 and stop())
        quiet = call("POST", "/api/tasks", {"name": "quiet", "notify": {"finish": False}})[1]["id"]
        call("POST", f"/api/tasks/{quiet}/finish", {"state": "done", "source": "claude", "agent_cwd": project})
        check("a task that says nothing when it ends: Stop has its toast", stop())
        call("POST", "/api/notify", {"title": "t", "message": "m", "source": "claude", "agent_cwd": project + "x" * 2000})
        check("an absurd folder is no folder", stop(cwd=project + "x" * 2000))
        t = call("GET", f"/api/tasks/{tid}")[1]
        check("the task keeps neither field", "agent_cwd" not in t and t.get("state") == "done", t)

        # the real MCP server, in a real working folder
        def mcp(*msgs, cwd=project, source="claude"):
            lines = "".join(json.dumps(m) + "\n" for m in msgs)
            p = subprocess.Popen([CLI, "mcp", "--source", source], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=ENV, cwd=cwd)
            p.stdin.write(lines.encode())
            p.stdin.flush()
            out = [json.loads(p.stdout.readline()) for _ in msgs]
            p.stdin.close()
            p.wait(15)
            return out

        init = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18"}}
        said = "do not call notify just to say"
        check("hooks off: the agent isn't told to stay quiet", said not in mcp(init)[0]["result"]["instructions"])
        os.makedirs(os.path.join(home, ".claude"), exist_ok=True)
        with open(os.path.join(home, ".claude", "settings.json"), "w", encoding="utf-8") as f:
            f.write('{"hooks":{"Stop":[{"hooks":[{"type":"http","url":"http://127.0.0.1:47800/api/hooks/claude"}]}]}}')
        check("hooks on: it is", said in mcp(init)[0]["result"]["instructions"])
        check("...not another agent", said not in mcp(init, source="codex")[0]["result"]["instructions"])
        with open(os.path.join(home, ".claude", "settings.json"), "w", encoding="utf-8") as f:
            f.write("{ not json, no entry")
        check("a broken file: not on", said not in mcp(init)[0]["result"]["instructions"])

        r = mcp({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "notify", "arguments": {"title": "Done", "message": "built"}}})
        check("real MCP notify", r[0]["result"]["content"][0]["text"] == "shown", r)
        time.sleep(.3)
        check("Stop in the MCP server's folder right after it: none", not stop())
        r = mcp({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "notify", "arguments": {"title": "Done", "message": "built"}}}, cwd=data)
        time.sleep(.3)
        check("Stop in another folder: its toast", stop())
        stop(cwd=data)  # uses up that mark
        tid = call("POST", "/api/tasks", {"name": "x"})[1]["id"]
        mcp({"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "task_finish", "arguments": {"id": tid}}})
        time.sleep(.3)
        check("Stop after a real MCP task_finish: none", not stop())

        spoke()
        print("waiting 31 s for the mark to run out", flush=True)
        time.sleep(31)
        check("after 30 s the Stop has its toast", stop())
finally:
    shutil.rmtree(home, ignore_errors=True)
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(project, ignore_errors=True)

print("hooks offer tests: " + ("OK" if not fails else f"{fails} FAILED"), flush=True)
sys.exit(1 if fails else 0)
