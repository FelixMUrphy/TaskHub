"""How agent hooks are written and run: Claude Code's as a command with no shell and no URL or token in its settings (an older HTTP entry is replaced,
the user's own kept), Codex's as PowerShell can run it. The written command is really run against an isolated hub. Own port, temporary home and data:
never the real home, configs or hub. Argument: TaskHubHost.exe."""
import json, os, shutil, subprocess, sys, tempfile, time, urllib.request

EXE = sys.argv[1]
CLI = os.path.join(os.path.dirname(EXE), "taskhub.exe")
PORT = 47886
URL = f"http://127.0.0.1:{PORT}"
home = tempfile.mkdtemp(prefix="taskhub-hookform-home-")
data = tempfile.mkdtemp(prefix="taskhub-hookform-data-")
spaced = tempfile.mkdtemp(prefix="taskhub hookform ")
ENV = dict(os.environ, TASKHUB_TEST_HOME=home, TASKHUB_NO_AUTOSTART="1", TASKHUB_URL=URL)
claude = os.path.join(home, ".claude", "settings.json")
os.makedirs(os.path.dirname(claude))
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0
TOKEN = "hook-form-test-token"
MINE = {"hooks": [{"type": "command", "command": "echo mine"}]}
OLD = {"type": "http", "url": "http://127.0.0.1:47800/api/hooks/claude", "headers": {"X-TaskHub": "1", "Authorization": "Bearer " + TOKEN}, "timeout": 5}


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def integrations(*a):
    return subprocess.run([EXE, "--data", data, "--integrations", *a], env=ENV, capture_output=True, timeout=30).returncode


def load():
    with open(claude, encoding="utf-8") as f:
        return json.load(f)


def save(o):
    with open(claude, "w", encoding="utf-8") as f:
        json.dump(o, f)


def toasts():
    try:
        with open(os.path.join(data, "taskhub.log"), encoding="utf-8", errors="replace") as f:
            return [l for l in f if "TOAST " in l]
    except OSError:
        return []


def told(marker):
    end = time.time() + 3
    while time.time() < end:
        if any(marker in l for l in toasts()):
            return True
        time.sleep(.1)
    return False


def event(n):
    return json.dumps({"hook_event_name": "Stop", "cwd": data, "session_id": "s", "prompt_id": n, "last_assistant_message": "answer " + n}).encode()


with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"log_level": "debug", "toasts": False, "autostart": False, "update_check": False, "toast_rate": 0, "toast_task_gap": 0, "language": "en"}, f)

hub = None
try:
    # an older entry becomes the new one; the user's is kept
    save({"model": "opus", "hooks": {"Stop": [MINE, {"hooks": [OLD]}], "Notification": [{"matcher": "permission_prompt|elicitation_dialog", "hooks": [OLD]}]}})
    check("connect: exit 0", integrations("claude=on", "codex=on") == 0)
    c = load()
    stop = c["hooks"]["Stop"]
    check("user's own hook and other settings kept", stop[0] == MINE and c["model"] == "opus", c)
    check("one entry of ours on Stop", len(stop) == 2, stop)
    h = stop[1]["hooks"][0]
    check("a command with no shell", h["type"] == "command" and h["args"] == ["hook", "claude"] and h["command"].lower().endswith("taskhub.exe"), h)
    check("Notification too, with its matcher", c["hooks"]["Notification"][0]["matcher"] == "permission_prompt|elicitation_dialog" and c["hooks"]["Notification"][0]["hooks"][0]["args"] == ["hook", "claude"], c)
    text = open(claude, encoding="utf-8").read()
    check("no URL and no token left in the settings", "/api/hooks" not in text and TOKEN not in text, text)
    m = os.path.getmtime(claude)
    time.sleep(1.2)
    check("connect again: exit 0", integrations("claude=on") == 0)
    check("connect again: file untouched", os.path.getmtime(claude) == m)

    hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data], env=ENV)
    for _ in range(100):
        try:
            if op.open(URL + "/api/health", timeout=2).status == 200:
                break
        except OSError:
            time.sleep(.2)

    # what is written really runs, with no shell, and says nothing to the agent
    r = subprocess.run([h["command"], *h["args"]], env=ENV, input=event("1"), capture_output=True, timeout=30)
    check("Claude: silent, exit 0", r.returncode == 0 and not r.stdout and not r.stderr, r)
    check("Claude: its notification arrives", told("answer 1"), toasts()[-2:])

    # Codex: as PowerShell runs it (Codex wraps commands in PowerShell)
    shutil.copy(CLI, spaced)
    out = os.path.join(data, "cmd.txt")

    def command(path):
        subprocess.run([EXE, "--data", data, "--selftest-codex-command", out, path], env=ENV, timeout=30)
        return open(out, encoding="utf-8").read()

    plain = r"C:\Tools\taskhub.exe"
    check("Codex, no space: the same bytes as before (its trust is kept)", command(plain) == plain + " hook codex", command(plain))
    cmd = command(os.path.join(spaced, "taskhub.exe"))
    r = subprocess.run(["powershell", "-NoProfile", "-Command", cmd], env=ENV, input=event("2"), capture_output=True, timeout=60)
    check("Codex, a space in the path: PowerShell runs it", r.returncode == 0 and not r.stderr, (cmd, r))
    check("Codex: its notification arrives", told("answer 2"), toasts()[-2:])

    hub.kill()
    hub.wait(10)
    hub = None

    # hub down: still silent, and nothing for the agent to act on
    r = subprocess.run([h["command"], *h["args"]], env=ENV, input=event("3"), capture_output=True, timeout=30)
    check("hub down: silent, exit 0", r.returncode == 0 and not r.stdout and not r.stderr, r)

    # both forms are ours when removing: new, and an older one left behind
    check("disconnect: exit 0", integrations("claude=off") == 0)
    c = load()
    check("disconnect: only the user's left", c["hooks"]["Stop"] == [MINE] and "Notification" not in c["hooks"], c)
    save({"hooks": {"Stop": [MINE, {"hooks": [OLD]}]}})
    check("an older entry is removed too: exit 0", integrations("claude=off") == 0)
    check("an older entry is removed too", load()["hooks"]["Stop"] == [MINE], load())
    save({"hooks": {"Stop": [{"hooks": [{"type": "command", "command": "C:/x/other.exe", "args": ["hook", "claude"]}]}]}})
    integrations("claude=off")
    check("someone else's command with the same args isn't ours", len(load().get("hooks", {}).get("Stop", [])) == 1, load())
finally:
    if hub:
        hub.kill()
        hub.wait(10)
    shutil.rmtree(home, ignore_errors=True)
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(spaced, ignore_errors=True)

print("hook form tests: " + ("OK" if not fails else f"{fails} FAILED"), flush=True)
sys.exit(1 if fails else 0)
