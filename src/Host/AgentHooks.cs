using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Adapters for AI-agent hook payloads, posted as-is:
///   POST /api/hooks/claude  - Claude Code via `taskhub hook claude` (Stop, Notification), HTTP entries from before 3.1 and the plugin
///   POST /api/hooks/codex   - Codex hooks via `taskhub hook codex` (Stop, PermissionRequest) and the legacy
///                             `notify` payload ({"type": "agent-turn-complete", "last-assistant-message": ...})
/// Each becomes an informational toast (a click only dismisses it). Always answers {} so the agent never
/// blocks or changes behaviour because of TaskHub.
/// </summary>
static class AgentHooks
{
    static readonly Dictionary<string, DateTime> recent = new();

    /// <summary>The same event delivered twice (e.g. TaskHub's own hooks + the plugin) -> one toast.</summary>
    static bool Duplicate(string agent, JsonObject b)
    {
        var key = string.Join("|", agent, J.Str(b["hook_event_name"] ?? b["type"]), J.Str(b["session_id"] ?? b["thread-id"]),
            J.Str(b["turn_id"] ?? b["turn-id"] ?? b["prompt_id"]), J.Str(b["message"]), J.Str(b["tool_name"]));
        var now = DateTime.UtcNow;
        lock (recent)
        {
            foreach (var k in recent.Where(kv => now - kv.Value > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList()) recent.Remove(k);
            if (recent.ContainsKey(key)) return true;
            recent[key] = now;
            return false;
        }
    }

    // ------------------------------------------------------------ the agent said it itself
    // An agent that has just notified or finished a task through MCP would get "response ready" on top of it when its turn ends.
    // A session is not known on the MCP side, so: the same agent and project folder, once, within this time. Two sessions in
    // one folder can cost one "response ready"; a session that moved to a subfolder gets both. Only Stop is ever held back.
    static readonly TimeSpan SpokeFor = TimeSpan.FromSeconds(30);
    static readonly Dictionary<string, DateTime> spoke = new();

    /// <summary>One folder, one key: GetFullPath expands an 8.3 name (the MCP server reports the long one, an agent what it was started with).</summary>
    static string SpokeKey(string agent, string cwd)
    {
        var path = cwd.Trim();
        // not for a path on another machine: GetFullPath asks the file system about a name with `~`, which would connect there (measured: 42 s on an address that doesn't answer)
        if (!path.StartsWith(@"\\") && !path.StartsWith("//") && !path.StartsWith(@"\/") && !path.StartsWith(@"/\"))
        try { path = Path.GetFullPath(path); } catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException) { }
        return agent + "|" + path.TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>A notification or a finished task from TaskHub's MCP server: "source" is the agent, "agent_cwd" the folder it runs in.</summary>
    public static void AgentSpoke(JsonObject b)
    {
        var agent = J.Str(b["source"]);
        var cwd = J.Str(b["agent_cwd"]);
        if (agent is not ("claude" or "codex") || cwd.Trim().Length == 0 || cwd.Length > 1024) return;
        lock (spoke)
        {
            var now = DateTime.UtcNow;
            foreach (var k in spoke.Where(kv => now - kv.Value > SpokeFor).Select(kv => kv.Key).ToList()) spoke.Remove(k);
            spoke[SpokeKey(agent, cwd)] = now;
        }
    }

    static bool JustSpoke(string agent, string cwd)
    {
        if (cwd.Trim().Length == 0) return false;
        var key = SpokeKey(agent, cwd);
        lock (spoke) return spoke.Remove(key, out var at) && DateTime.UtcNow - at <= SpokeFor;
    }

    public static JsonObject Handle(Hub hub, string agent, JsonObject b)
    {
        try
        {
            if (Duplicate(agent, b)) return new JsonObject();
            var name = agent == "codex" ? "Codex" : "Claude Code";
            var ev = J.Str(b["hook_event_name"]);
            if (ev.Length == 0 && J.Str(b["type"]) == "agent-turn-complete") ev = "Stop";   // Codex legacy notify
            var project = Project(J.Str(b["cwd"]));
            var title = name + (project.Length > 0 ? " · " + project : "");
            switch (ev)
            {
                case "Stop":
                {
                    if (JustSpoke(agent, J.Str(b["cwd"]))) { Log.D("agents", name + " Stop: no toast, the agent has just notified from this folder"); break; }
                    var last = Plain(J.Str(b["last_assistant_message"] ?? b["last-assistant-message"]));
                    Toast(hub, title + " — " + L.T("agent_done"), last.Length > 0 ? J.Cut(last, 200) : L.T("agent_done"), "info");
                    break;
                }
                case "Notification":
                {
                    var msg = J.Str(b["message"]);
                    Toast(hub, title, msg.Length > 0 ? J.Cut(msg, 250) : L.T("agent_input"), "warn");
                    break;
                }
                case "PermissionRequest":
                {
                    var tool = J.Str(b["tool_name"]);
                    var desc = b["tool_input"] is JsonObject ti ? J.Str(ti["description"] ?? ti["command"]) : "";
                    var msg = L.T("agent_input") + (tool.Length > 0 ? ": " + tool : "") + (desc.Length > 0 ? " — " + desc : "");
                    Toast(hub, title, J.Cut(msg, 250), "warn");
                    break;
                }
            }
        }
        catch (Exception e) { Log.W("agents", agent + " hook: " + e.Message); }
        return new JsonObject();
    }

    static void Toast(Hub hub, string title, string msg, string level) =>
        hub.Notify(new JsonObject { ["title"] = title, ["message"] = msg, ["level"] = level, ["click"] = "dismiss" });

    static string Project(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "";
        try { return Path.GetFileName(cwd.TrimEnd('\\', '/')) ?? ""; } catch { return ""; }
    }

    /// <summary>Markdown -> one plain line for a toast.</summary>
    static string Plain(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var s = Regex.Replace(md, @"```.*?```", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"(?m)^\s{0,3}(#{1,6}\s*|[-*+]\s+|>\s*|\d+\.\s+)", "");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }
}
