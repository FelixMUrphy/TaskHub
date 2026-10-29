"""AI-agent integrations (Claude Code / Codex hook entries) against a temporary home folder - never the real one.
Runs `TaskHubHost.exe --integrations ...` with TASKHUB_TEST_HOME. Usage: python integration_test.py [path\\to\\TaskHubHost.exe]"""
import json, os, shutil, subprocess, sys, tempfile, time

exe = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app", "TaskHubHost.exe")
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


home = tempfile.mkdtemp(prefix="taskhub-home-")
data = tempfile.mkdtemp(prefix="taskhub-data-")
claude = os.path.join(home, ".claude", "settings.json")
codex = os.path.join(home, ".codex", "hooks.json")
os.makedirs(os.path.dirname(claude))
original = {"model": "opus", "permissions": {"allow": ["Bash(git *)"]},
            "hooks": {"Stop": [{"hooks": [{"type": "command", "command": "echo mine"}]}]}}
with open(claude, "w", encoding="utf-8") as f:
    json.dump(original, f)


def run(*a):
    env = dict(os.environ, TASKHUB_TEST_HOME=home)
    p = subprocess.run([exe, "--data", data, "--integrations", *a], env=env, capture_output=True, timeout=30)
    return p.returncode


def load(p):
    with open(p, encoding="utf-8") as f:
        return json.load(f)


check("enable both: exit 0", run("claude=on", "codex=on") == 0)
c = load(claude)
check("claude: other settings kept", c.get("model") == "opus" and c.get("permissions") == original["permissions"], c)
stop = c["hooks"]["Stop"]
check("claude: user's own Stop hook kept + ours added", len(stop) == 2 and stop[0] == original["hooks"]["Stop"][0]
      and stop[1]["hooks"][0]["type"] == "http" and stop[1]["hooks"][0]["url"].endswith("/api/hooks/claude"), stop)
notif = c["hooks"].get("Notification", [])
check("claude: Notification hook with matcher", len(notif) == 1 and notif[0].get("matcher") == "permission_prompt|elicitation_dialog"
      and notif[0]["hooks"][0]["headers"] == {"X-TaskHub": "1"}, notif)
check("claude: backup of the original", load(claude + ".taskhub.bak") == original)
x = load(codex)
cmds = [g["hooks"][0]["command"] for ev in ("Stop", "PermissionRequest") for g in x["hooks"].get(ev, [])]
check("codex: Stop + PermissionRequest command hooks", len(cmds) == 2 and all(cm.endswith(" hook codex") and "taskhub" in cm.lower() for cm in cmds), x)

m1, m2 = os.path.getmtime(claude), os.path.getmtime(codex)
time.sleep(1.2)
check("enable again: exit 0", run("claude=on", "codex=on") == 0)
check("enable again: files untouched (no churn -> Codex trust kept)", os.path.getmtime(claude) == m1 and os.path.getmtime(codex) == m2)

check("disable both: exit 0", run("claude=off", "codex=off") == 0)
check("claude: back to the original content", load(claude) == original, load(claude))
check("codex: our hooks removed", "hooks" not in load(codex), load(codex))

# broken JSON is never overwritten
with open(claude, "w", encoding="utf-8") as f:
    f.write("{ this is not json")
check("invalid settings.json: error exit", run("claude=on") == 1)
with open(claude, encoding="utf-8") as f:
    check("invalid settings.json: left untouched", f.read() == "{ this is not json")

shutil.rmtree(home, ignore_errors=True)
shutil.rmtree(data, ignore_errors=True)
print(f"{fails} failure(s)")
sys.exit(1 if fails else 0)
