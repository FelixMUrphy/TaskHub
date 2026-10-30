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

# ---- MCP: Claude Code (~/.claude.json) and Codex (~/.codex/config.toml)
claude_json = os.path.join(home, ".claude.json")
codex_toml = os.path.join(home, ".codex", "config.toml")
cj = {"userID": "u1", "projects": {"D:/x": {"allowedTools": []}}, "mcpServers": {"other": {"type": "stdio", "command": "other.exe"}}}
with open(claude_json, "w", encoding="utf-8") as f:
    json.dump(cj, f)
toml0 = 'model = "gpt"\r\nnotify = ["a.exe", "x"]\r\n\r\n[mcp_servers.node_repl]\r\ncommand = \'C:\\n\\node.exe\'\r\n\r\n[hooks.state.\'k\']\r\ntrusted_hash = "sha256:1"\r\n'
with open(codex_toml, "w", encoding="utf-8", newline="") as f:
    f.write(toml0)
check("enable MCP: exit 0", run("claude-mcp=on", "codex-mcp=on") == 0)
j = load(claude_json)
ts = j["mcpServers"].get("taskhub", {})
check("claude.json: other keys + servers kept, taskhub added", j["userID"] == "u1" and j["projects"] == cj["projects"]
      and j["mcpServers"]["other"] == cj["mcpServers"]["other"] and ts.get("args") == ["mcp", "--source", "claude"]
      and ts.get("command", "").lower().endswith("taskhub.exe") and ts.get("type") == "stdio", j)
with open(codex_toml, encoding="utf-8", newline="") as f:
    t1 = f.read()
check("config.toml: original kept verbatim + table appended (CRLF)", t1.startswith(toml0.rstrip("\r\n")) and "[mcp_servers.taskhub]\r\n" in t1
      and 'args = ["mcp", "--source", "codex"]' in t1 and "\n" not in t1.replace("\r\n", ""), t1)
m1, m2 = os.path.getmtime(claude_json), os.path.getmtime(codex_toml)
time.sleep(1.2)
run("claude-mcp=on", "codex-mcp=on")
check("MCP enable again: files untouched", os.path.getmtime(claude_json) == m1 and os.path.getmtime(codex_toml) == m2)
# Codex may re-sort keys of the table: still recognised as ours, no duplicate table
with open(codex_toml, "w", encoding="utf-8", newline="") as f:
    f.write(t1.replace('args = ["mcp", "--source", "codex"]\r\n', "").replace("[mcp_servers.taskhub]\r\n", '[mcp_servers.taskhub]\r\nargs = ["mcp", "--source", "codex"]\r\n'))
run("codex-mcp=on")
with open(codex_toml, encoding="utf-8") as f:
    check("config.toml: one taskhub table only", f.read().count("[mcp_servers.taskhub]") == 1)
check("disable MCP: exit 0", run("claude-mcp=off", "codex-mcp=off") == 0)
check("claude.json: back to original", load(claude_json) == cj, load(claude_json))
with open(codex_toml, encoding="utf-8", newline="") as f:
    t2 = f.read()
check("config.toml: back to original", t2.rstrip("\r\n") == toml0.rstrip("\r\n"), repr(t2))
# a foreign 'taskhub' server is never replaced
with open(codex_toml, "a", encoding="utf-8", newline="") as f:
    f.write("\r\n[mcp_servers.taskhub]\r\ncommand = 'someone-else.exe'\r\n")
check("foreign [mcp_servers.taskhub]: error exit", run("codex-mcp=on") == 1)
with open(codex_toml, "w", encoding="utf-8", newline="") as f:
    f.write(toml0)

# ---- Store edition uninstalled: its taskhub copy (store.json next to it) removes the entries when an agent starts it
cli = os.path.join(os.path.dirname(os.path.abspath(exe)), "taskhub.exe")
check("all four integrations on", run("claude=on", "codex=on", "claude-mcp=on", "codex-mcp=on") == 0)


def agent_run(cli_exe, *a):
    env = dict(os.environ, TASKHUB_TEST_HOME=home, TASKHUB_URL="http://127.0.0.1:9", TASKHUB_NO_AUTOSTART="1")
    return subprocess.run([cli_exe, *a], env=env, input=b"", capture_output=True, timeout=30)


p = agent_run(cli, "hook", "codex")
check("regular CLI (no store.json): hook leaves the entries", p.returncode == 0 and "hooks" in load(codex)
      and "taskhub" in load(claude_json).get("mcpServers", {}))
store_bin = tempfile.mkdtemp(prefix="taskhub-storebin-")
store_cli = os.path.join(store_bin, "taskhub.exe")
shutil.copy(cli, store_cli)
with open(os.path.join(store_bin, "store.json"), "w", encoding="utf-8") as f:
    json.dump({"package_family": "TaskHub.NotInstalled_0000000000000"}, f)
p = agent_run(store_cli, "hook", "codex")
check("store copy, package gone: hook exits 0", p.returncode == 0, p.stderr)
check("store copy: Claude hooks removed, user's own kept", load(claude) == original, load(claude))
check("store copy: Codex hooks removed", "hooks" not in load(codex), load(codex))
check("store copy: Claude MCP removed, other servers kept", load(claude_json) == cj, load(claude_json))
with open(codex_toml, encoding="utf-8", newline="") as f:
    check("store copy: Codex MCP table removed", f.read().rstrip("\r\n") == toml0.rstrip("\r\n"))
p = agent_run(store_cli, "mcp")
check("store copy: mcp after cleanup -> error exit with a message", p.returncode == 1 and b"uninstalled" in p.stderr, p.stderr)
check("store copy: agent files unchanged by a second run", load(claude) == original and load(claude_json) == cj)
shutil.rmtree(store_bin, ignore_errors=True)

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
