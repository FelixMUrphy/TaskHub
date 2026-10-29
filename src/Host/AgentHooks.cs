using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Adapters for AI-agent hook payloads, posted as-is:
///   POST /api/hooks/claude  - Claude Code HTTP hooks (Stop, Notification, PermissionRequest)
///   POST /api/hooks/codex   - Codex hooks via `taskhub hook codex` (Stop, PermissionRequest) and the legacy
///                             `notify` payload ({"type": "agent-turn-complete", "last-assistant-message": ...})
/// Each becomes an informational toast (a click only dismisses it). Always answers {} so the agent never
/// blocks or changes behaviour because of TaskHub.
/// </summary>
static class AgentHooks
{
    public static JsonObject Handle(Hub hub, string agent, JsonObject b)
    {
        try
        {
            var name = agent == "codex" ? "Codex" : "Claude Code";
            var ev = J.Str(b["hook_event_name"]);
            if (ev.Length == 0 && J.Str(b["type"]) == "agent-turn-complete") ev = "Stop";   // Codex legacy notify
            var project = Project(J.Str(b["cwd"]));
            var title = name + (project.Length > 0 ? " · " + project : "");
            switch (ev)
            {
                case "Stop":
                {
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
        catch (Exception e) { Log.W(agent + " hook: " + e.Message); }
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
