using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>
/// One-click AI-agent integrations. Two independent kinds per agent:
///   Hooks - the agent notifies TaskHub when it finishes a response or needs permission (toasts):
///           Claude Code: ~/.claude/settings.json (2 HTTP hooks)   Codex: ~/.codex/hooks.json (2 command hooks)
///   Mcp   - the agent gets TaskHub tools (notify, task_start/update/finish, task_commands) via `taskhub mcp`:
///           Claude Code: ~/.claude.json "mcpServers.taskhub"      Codex: ~/.codex/config.toml [mcp_servers.taskhub]
/// (Codex's Limits: Hub.CodexLimits, a switch in TaskHub's own config; nothing in Codex's files.)
/// A third, Limits, is only ever turned on by hand in the settings (not offered at install): the plan's limit readings through
///           `taskhub statusline`, Claude Code's "statusLine" in ~/.claude/settings.json, which wraps the user's own (Hub.Limits).
/// TaskHub adds / removes exactly its own entries and keeps everything else; before the first change of a file the
/// original is copied to *.taskhub.bak. Entries that are already right are never rewritten (Codex trusts hooks by
/// their definition, so churn would make it ask again).
/// </summary>
static class Integrations
{
    public enum Agent { Claude, Codex }
    public enum Kind { Hooks, Mcp, Limits }
    public static readonly Agent[] All = { Agent.Claude, Agent.Codex };
    public static readonly Kind[] Kinds = { Kind.Hooks, Kind.Mcp };

    static string Home => AgentFiles.Home;

    public static string FileOf(Agent a, Kind k = Kind.Hooks) => (a, k) switch
    {
        (Agent.Claude, Kind.Hooks or Kind.Limits) => AgentFiles.ClaudeHooks,
        (Agent.Claude, Kind.Mcp) => AgentFiles.ClaudeMcp,
        (Agent.Codex, Kind.Hooks) => AgentFiles.CodexHooks,
        (Agent.Codex, Kind.Limits) => "config.json",
        _ => AgentFiles.CodexMcp,
    };
    public static string CfgKey(Agent a, Kind k = Kind.Hooks) =>
        (k == Kind.Hooks ? "integration_" : "mcp_") + (a == Agent.Claude ? "claude" : "codex");
    public static string Name(Agent a) => a == Agent.Claude ? "Claude Code" : "Codex";
    public static string ArgName(Agent a, Kind k) => (a == Agent.Claude ? "claude" : "codex") + (k == Kind.Mcp ? "-mcp" : k == Kind.Limits ? "-limits" : "");

    /// <summary>What the user has to do in the agent after enabling.</summary>
    public static string Hint(Agent a, Kind k) => (a, k) switch
    {
        (Agent.Codex, Kind.Hooks) => L.T("codex_trust"),
        (Agent.Codex, Kind.Mcp) => L.T("hint_codex_mcp"),
        (Agent.Claude, Kind.Mcp) => L.T("hint_claude_mcp"),
        (Agent.Codex, Kind.Limits) => null,
        (_, Kind.Limits) => L.T("hint_limits"),
        _ => L.T("hint_claude"),
    };

    /// <summary>The agent seems to be used on this PC (its config folder or executable exists).</summary>
    public static bool Detected(Agent a)
    {
        try
        {
            return a == Agent.Claude
                ? Directory.Exists(Path.Combine(Home, ".claude")) || File.Exists(Path.Combine(Home, ".claude.json")) || OnPath("claude")
                : Directory.Exists(Path.Combine(Home, ".codex")) || OnPath("codex");
        }
        catch { return false; }
    }

    static bool OnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Any(d =>
    {
        try { return d.Trim().Length > 0 && (File.Exists(Path.Combine(d.Trim(), exe + ".exe")) || File.Exists(Path.Combine(d.Trim(), exe + ".cmd"))); }
        catch { return false; }
    });

    static string CliPath()
    {
        // Store edition: its copy outside the package that cleans up after an uninstall (Install.EnsureStoreCli), else
        // the package's `taskhub` command - never the versioned package folder (changes on every update)
        string p;
        if (Install.IsPackaged)
            try { p = Install.EnsureStoreCli(); }
            catch (Exception e) { Log.W("agents", "taskhub copy: " + e.Message); p = Install.PackagedCli; }
        else p = Path.Combine(AppContext.BaseDirectory, "taskhub.exe");
        if (!p.Contains(' ')) return p;
        var sb = new StringBuilder(1024);   // 8.3 name: no quoting problems in whatever shell the agent uses
        return GetShortPathName(p, sb, sb.Capacity) > 0 ? sb.ToString() : p;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetShortPathName(string longPath, StringBuilder shortPath, int size);

    // ------------------------------------------------------------ public API
    public static bool Enabled(Agent a, Kind k = Kind.Hooks)
    {
        try
        {
            if (k == Kind.Limits) return a == Agent.Claude ? AgentFiles.IsOurStatusLine(AgentFiles.LoadJson(FileOf(a, k))["statusLine"] as JsonObject) : L.S?.Bool("limits_codex") == true;
            if (k == Kind.Hooks) return AgentFiles.HasHooks(AgentFiles.LoadJson(FileOf(a)), a == Agent.Claude);
            if (a == Agent.Claude) return AgentFiles.IsOurMcp((AgentFiles.LoadJson(FileOf(a, k))["mcpServers"] as JsonObject)?["taskhub"] as JsonObject);
            return AgentFiles.TomlBlock(AgentFiles.ReadText(FileOf(a, k))).ours;
        }
        catch { return false; }
    }

    /// <summary>Add (on) or remove (off) TaskHub's entries. Returns true if a file was changed.</summary>
    public static bool Apply(Agent a, Kind k, bool on, Settings s)
    {
        var changed = k == Kind.Limits ? (a == Agent.Claude ? ApplyStatusLine(on, s) : ApplyCodexLimits(on, s)) : k == Kind.Hooks ? ApplyHooks(a, on, s) : a == Agent.Claude ? ApplyClaudeMcp(on) : ApplyCodexMcp(on);
        if (changed) Log.I("agents", $"integration {Name(a)} {k}: {(on ? "enabled" : "disabled")} ({FileOf(a, k)})");
        return changed;
    }

    /// <summary>Turn on/off and remember the choice. Returns an error message or null.</summary>
    public static string Set(Agent a, Kind k, bool on, Settings s)
    {
        try
        {
            Apply(a, k, on, s);
            if (Remembered(k)) s.Set((CfgKey(a, k), on));
            return null;
        }
        catch (Exception e)
        {
            Log.E("agents", $"integration {Name(a)} {k}: {e}");
            return L.T("int_error", Name(a), FileOf(a, k)) + "\n" + e.Message;
        }
    }

    /// <summary>Plan limits have no remembered choice: the agent's file is the only record of it, so the two can't disagree.</summary>
    public static bool Remembered(Kind k) => k != Kind.Limits;

    /// <summary>Agents to offer hooks to: used on this PC, hooks off, and no choice made (any earlier choice, also "not now", is a remembered value).</summary>
    public static List<Agent> HookOffer(Settings s) =>
        All.Where(a => s.Get(CfgKey(a)) == null && Detected(a) && !Enabled(a)).ToList();

    /// <summary>The setup window's switches as arguments of the install step. A switch that simply stayed off for an agent that isn't here
    /// is no answer: stored as "no", the panel would never offer that agent once it is installed.</summary>
    public static string SetupArgs(IEnumerable<(Agent agent, Kind kind, bool on)> choices, Settings s) =>
        string.Concat(choices
            .Where(x => x.on || Detected(x.agent) || Enabled(x.agent, x.kind) || s.Get(CfgKey(x.agent, x.kind)) != null)
            .Select(x => (x.on ? " --" : " --no-") + ArgName(x.agent, x.kind)));

    /// <summary>Tests only: `--selftest-setup-args OUT claude=on codex=off ...` writes what SetupArgs makes of those switches.</summary>
    public static int SelfTestSetupArgs(Settings s, string[] a)
    {
        var choices = new List<(Agent, Kind, bool)>();
        foreach (var x in a.Skip(1))
            foreach (var ag in All)
                foreach (var k in Kinds)
                    if (x.StartsWith(ArgName(ag, k) + "=")) choices.Add((ag, k, x.EndsWith("=on")));
        File.WriteAllText(a[0], SetupArgs(choices, s));
        return 0;
    }

    /// <summary>Installed hub on start: keep enabled integrations correct (port or install folder may have changed).</summary>
    public static void Ensure(Settings s)
    {
        foreach (var a in All)
            foreach (var k in Kinds)
                if (s.Get(CfgKey(a, k)) is JsonValue v && v.TryGetValue<bool>(out var on) && on)
                    try { Apply(a, k, true, s); } catch (Exception e) { Log.W("agents", $"integration {Name(a)} {k}: {e.Message}"); }
        // the status line is never put back over what the user has there now: only ours is kept correct
        try
        {
            if (AgentFiles.IsOurStatusLine(AgentFiles.LoadJson(AgentFiles.ClaudeHooks)["statusLine"] as JsonObject)) Apply(Agent.Claude, Kind.Limits, true, s);
        }
        catch (Exception e) { Log.W("agents", $"integration {Name(Agent.Claude)} {Kind.Limits}: {e.Message}"); }
    }

    /// <summary>Uninstall: remove all of TaskHub's entries, whatever the settings say.</summary>
    public static void RemoveAll(Settings s)
    {
        foreach (var a in All)
            foreach (var k in Kinds)
                try { Apply(a, k, false, s); } catch (Exception e) { Log.W("agents", $"integration {Name(a)} {k}: {e.Message}"); }
        foreach (var a in All)
            try { Apply(a, Kind.Limits, false, s); } catch (Exception e) { Log.W("agents", $"integration {Name(a)} {Kind.Limits}: {e.Message}"); }
    }

    // ------------------------------------------------------------ hooks (JSON)
    static List<(string ev, string matcher, JsonObject handler)> DesiredHooks(Agent a, Settings s)
    {
        if (a == Agent.Claude)
        {
            // a command, not a URL: whoever answers a URL with the hub down can steer the agent; this CLI says nothing back. A process holding
            // the port with the hub down can still read what the CLI sends: the agent's event, as with any local API. With args there is no shell, so neither Git Bash nor PowerShell has to read the path. No token in the agent's file.
            var exe = CliPath();
            JsonObject H() => new() { ["type"] = "command", ["command"] = exe, ["args"] = new JsonArray("hook", "claude"), ["timeout"] = 10 };
            return new() { ("Stop", null, H()), ("Notification", "permission_prompt|elicitation_dialog", H()) };
        }
        var cmd = CodexCommand(CliPath());
        JsonObject C() => new() { ["type"] = "command", ["command"] = cmd, ["timeout"] = 10 };
        return new() { ("Stop", null, C()), ("PermissionRequest", null, C()) };
    }

    /// <summary>Codex wraps a command in PowerShell (run 2026-11-02), which won't run a quoted first word without `&`. Without a space it stays
    /// byte for byte what it was: a changed entry loses Codex's trust until /hooks.</summary>
    public static string CodexCommand(string cli) => (cli.Contains(' ') ? "& \"" + cli + "\"" : cli) + " hook codex";

    static string Sig(string ev, string matcher, JsonObject h) => ev + "|" + (matcher ?? "") + "|" + h.ToJsonString();

    static List<string> HookEntries(Agent a, JsonObject root)
    {
        var r = new List<string>();
        if (root["hooks"] is not JsonObject hooks) return r;
        foreach (var (ev, arr) in hooks)
            foreach (var g in (arr as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                foreach (var h in (g["hooks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (AgentFiles.IsOurHook(a == Agent.Claude, h)) r.Add(Sig(ev, J.Str(g["matcher"]), h));
        return r;
    }

    static bool ApplyHooks(Agent a, bool on, Settings s)
    {
        var path = FileOf(a);
        if (!on && !File.Exists(path)) return false;
        var root = AgentFiles.LoadJson(path);
        var want = on ? DesiredHooks(a, s) : new();
        if (HookEntries(a, root).SequenceEqual(want.Select(w => Sig(w.ev, w.matcher, w.handler)))) return false;

        AgentFiles.StripHooks(root, a == Agent.Claude);
        if (on)
        {
            if (root["hooks"] is not JsonObject hooks) root["hooks"] = hooks = new JsonObject();
            foreach (var (ev, matcher, handler) in want)
            {
                if (hooks[ev] is not JsonArray arr) hooks[ev] = arr = new JsonArray();
                var g = new JsonObject();
                if (matcher != null) g["matcher"] = matcher;
                g["hooks"] = new JsonArray(handler);
                arr.Add(g);
            }
        }
        AgentFiles.SaveJson(path, root);
        return true;
    }

    static bool ApplyCodexLimits(bool on, Settings s)
    {
        if (!on) ForgetLimits(s, "codex");
        if (s.Bool("limits_codex") == on) return false;
        s.Set(("limits_codex", on));
        return true;
    }

    /// <summary>One agent's readings out of the kept file, the other's stay: each has its own switch. An empty or broken file goes.</summary>
    static void ForgetLimits(Settings s, string agent)
    {
        var path = s.File_(Hub.LimitsFile);
        if (!File.Exists(path)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
            {
                o.Remove(agent);
                if (o.Count > 0)
                {
                    File.WriteAllText(path, o.ToJsonString());
                    return;
                }
            }
        }
        catch (Exception e) { Log.W("limits", "plan_limits.json: " + e.Message); }
        File.Delete(path);
    }

    // ------------------------------------------------------------ status line: Claude Code (~/.claude/settings.json)
    /// <summary>The command as both of Claude Code's shells read it (Git Bash, else PowerShell): forward slashes, bash would eat
    /// backslashes. CliPath gives an 8.3 path, so the quotes (which PowerShell would not run) stay for PCs without 8.3 names.</summary>
    static string StatusLineCommand()
    {
        var p = CliPath().Replace('\\', '/');
        return (p.Contains(' ') ? "\"" + p + "\"" : p) + " statusline";
    }

    /// <summary>The user has a status line of their own now (ours wraps it, or it is there to be wrapped).</summary>
    public static bool HasOwnStatusLine()
    {
        try
        {
            var cur = AgentFiles.LoadJson(AgentFiles.ClaudeHooks)["statusLine"] as JsonObject;
            return AgentFiles.IsOurStatusLine(cur) ? AgentFiles.WrappedStatusLine() != null : cur != null;
        }
        catch { return false; }
    }

    static bool ApplyStatusLine(bool on, Settings s)
    {
        if (!on)
        {
            var removed = AgentFiles.RemoveStatusLine();
            ForgetLimits(s, "claude");   // also from a script or an uninstall; a running hub follows the settings file
            return removed;
        }
        var path = AgentFiles.ClaudeHooks;
        var root = AgentFiles.LoadJson(path);
        var node = root["statusLine"];
        if (node != null && node is not JsonObject) throw new InvalidDataException("\"statusLine\" in " + path + " is not an object");
        var cur = node as JsonObject;
        var cmd = StatusLineCommand();
        if (AgentFiles.IsOurStatusLine(cur))
        {
            if (J.Str(cur["command"]) == cmd) return false;
            cur["command"] = cmd;
        }
        else
        {
            AgentFiles.SaveWrappedStatusLine(cur);   // first: if the next step fails nothing is lost; the other way round the user's line could be
            var ours = cur != null ? (JsonObject)cur.DeepClone() : new JsonObject();   // their padding and refresh interval stay
            ours["type"] = "command";
            ours["command"] = cmd;
            root["statusLine"] = ours;
        }
        AgentFiles.SaveJson(path, root);
        return true;
    }

    // ------------------------------------------------------------ MCP: Claude Code (~/.claude.json)
    static JsonObject DesiredClaudeMcp() => new()
    {
        ["type"] = "stdio", ["command"] = CliPath(), ["args"] = new JsonArray("mcp", "--source", "claude"), ["env"] = new JsonObject(),
    };

    static bool ApplyClaudeMcp(bool on)
    {
        if (!on) return AgentFiles.RemoveClaudeMcp();
        var path = FileOf(Agent.Claude, Kind.Mcp);
        var root = AgentFiles.LoadJson(path);
        var servers = root["mcpServers"] as JsonObject;
        var cur = servers?["taskhub"] as JsonObject;
        if (cur != null && !AgentFiles.IsOurMcp(cur)) throw new InvalidOperationException("an MCP server named 'taskhub' that TaskHub did not add already exists");
        var want = DesiredClaudeMcp();
        if (cur != null && cur.ToJsonString() == want.ToJsonString()) return false;
        if (servers == null) root["mcpServers"] = servers = new JsonObject();
        servers["taskhub"] = want;
        AgentFiles.SaveJson(path, root);
        return true;
    }

    // ------------------------------------------------------------ MCP: Codex (~/.codex/config.toml)
    static List<string> DesiredToml() => new()
    {
        "[mcp_servers.taskhub]",
        "args = [\"mcp\", \"--source\", \"codex\"]",
        "command = " + TomlStr(CliPath()),
    };

    static string TomlStr(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
            sb.Append(ch switch
            {
                '\\' => "\\\\", '"' => "\\\"", '\t' => "\\t", '\n' => "\\n", '\r' => "\\r",
                < ' ' or '\x7f' => $"\\u{(int)ch:X4}",
                _ => ch.ToString(),
            });
        return sb.Append('"').ToString();
    }

    static bool ApplyCodexMcp(bool on)
    {
        if (!on) return AgentFiles.RemoveCodexMcp();
        var lines = AgentFiles.ReadText(FileOf(Agent.Codex, Kind.Mcp));
        var (start, count, exists, ours, block) = AgentFiles.TomlBlock(lines);
        if (exists && !ours) throw new InvalidOperationException("an MCP server [mcp_servers.taskhub] that TaskHub did not add already exists");
        var want = DesiredToml();
        static string Norm(IEnumerable<string> b) => string.Join("\n", b.Select(l => Regex.Replace(l.Trim(), @"\s+", " ")).Where(l => l.Length > 0).OrderBy(l => l));
        if (exists && Norm(block) == Norm(want)) return false;
        AgentFiles.WriteToml(lines, (start, count, exists), want, AgentFiles.TomlNewline());
        return true;
    }
}
