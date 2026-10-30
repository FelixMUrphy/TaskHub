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
/// TaskHub adds / removes exactly its own entries and keeps everything else; before the first change of a file the
/// original is copied to *.taskhub.bak. Entries that are already right are never rewritten (Codex trusts hooks by
/// their definition, so churn would make it ask again).
/// </summary>
static class Integrations
{
    public enum Agent { Claude, Codex }
    public enum Kind { Hooks, Mcp }
    public static readonly Agent[] All = { Agent.Claude, Agent.Codex };
    public static readonly Kind[] Kinds = { Kind.Hooks, Kind.Mcp };

    static string Home => AgentFiles.Home;

    public static string FileOf(Agent a, Kind k = Kind.Hooks) => (a, k) switch
    {
        (Agent.Claude, Kind.Hooks) => AgentFiles.ClaudeHooks,
        (Agent.Claude, Kind.Mcp) => AgentFiles.ClaudeMcp,
        (Agent.Codex, Kind.Hooks) => AgentFiles.CodexHooks,
        _ => AgentFiles.CodexMcp,
    };
    public static string CfgKey(Agent a, Kind k = Kind.Hooks) =>
        (k == Kind.Hooks ? "integration_" : "mcp_") + (a == Agent.Claude ? "claude" : "codex");
    public static string Name(Agent a) => a == Agent.Claude ? "Claude Code" : "Codex";
    public static string ArgName(Agent a, Kind k) => (a == Agent.Claude ? "claude" : "codex") + (k == Kind.Mcp ? "-mcp" : "");

    /// <summary>What the user has to do in the agent after enabling.</summary>
    public static string Hint(Agent a, Kind k) => (a, k) switch
    {
        (Agent.Codex, Kind.Hooks) => L.T("codex_trust"),
        (Agent.Codex, Kind.Mcp) => L.T("hint_codex_mcp"),
        (Agent.Claude, Kind.Mcp) => L.T("hint_claude_mcp"),
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
            catch (Exception e) { Log.W("taskhub copy: " + e.Message); p = Install.PackagedCli; }
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
            if (k == Kind.Hooks) return AgentFiles.HasHooks(AgentFiles.LoadJson(FileOf(a)), a == Agent.Claude);
            if (a == Agent.Claude) return AgentFiles.IsOurMcp((AgentFiles.LoadJson(FileOf(a, k))["mcpServers"] as JsonObject)?["taskhub"] as JsonObject);
            return AgentFiles.TomlBlock(AgentFiles.ReadText(FileOf(a, k))).ours;
        }
        catch { return false; }
    }

    /// <summary>Add (on) or remove (off) TaskHub's entries. Returns true if a file was changed.</summary>
    public static bool Apply(Agent a, Kind k, bool on, Settings s)
    {
        var changed = k == Kind.Hooks ? ApplyHooks(a, on, s) : a == Agent.Claude ? ApplyClaudeMcp(on) : ApplyCodexMcp(on);
        if (changed) Log.W($"integration {Name(a)} {k}: {(on ? "enabled" : "disabled")} ({FileOf(a, k)})");
        return changed;
    }

    /// <summary>Turn on/off and remember the choice. Returns an error message or null.</summary>
    public static string Set(Agent a, Kind k, bool on, Settings s)
    {
        try
        {
            Apply(a, k, on, s);
            s.Set((CfgKey(a, k), on));
            return null;
        }
        catch (Exception e)
        {
            Log.W($"integration {Name(a)} {k}: {e}");
            return L.T("int_error", Name(a), FileOf(a, k)) + "\n" + e.Message;
        }
    }

    /// <summary>Installed hub on start: keep enabled integrations correct (port or install folder may have changed).</summary>
    public static void Ensure(Settings s)
    {
        foreach (var a in All)
            foreach (var k in Kinds)
                if (s.Get(CfgKey(a, k)) is JsonValue v && v.TryGetValue<bool>(out var on) && on)
                    try { Apply(a, k, true, s); } catch (Exception e) { Log.W($"integration {Name(a)} {k}: {e.Message}"); }
    }

    /// <summary>Uninstall: remove all of TaskHub's entries, whatever the settings say.</summary>
    public static void RemoveAll(Settings s)
    {
        foreach (var a in All)
            foreach (var k in Kinds)
                try { Apply(a, k, false, s); } catch (Exception e) { Log.W($"integration {Name(a)} {k}: {e.Message}"); }
    }

    // ------------------------------------------------------------ hooks (JSON)
    static List<(string ev, string matcher, JsonObject handler)> DesiredHooks(Agent a, Settings s)
    {
        if (a == Agent.Claude)
        {
            var headers = new JsonObject { ["X-TaskHub"] = "1" };
            if (!string.IsNullOrEmpty(s.Token)) headers["Authorization"] = "Bearer " + s.Token;
            JsonObject H() => new()
            {
                ["type"] = "http", ["url"] = $"http://127.0.0.1:{s.Port}/api/hooks/claude",
                ["headers"] = headers.DeepClone(), ["timeout"] = 5,
            };
            return new() { ("Stop", null, H()), ("Notification", "permission_prompt|elicitation_dialog", H()) };
        }
        var cli = CliPath();
        var cmd = (cli.Contains(' ') ? "\"" + cli + "\"" : cli) + " hook codex";
        JsonObject C() => new() { ["type"] = "command", ["command"] = cmd, ["timeout"] = 10 };
        return new() { ("Stop", null, C()), ("PermissionRequest", null, C()) };
    }

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
