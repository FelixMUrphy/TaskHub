using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Adapters for AI-agent hook payloads. POST /api/hooks/claude receives Claude Code HTTP-hook input as-is
/// (hook_event_name, cwd, message, last_assistant_message, ...) and turns it into a notification.
/// Always answers {} so the agent never blocks or changes behaviour because of TaskHub.
/// </summary>
static class AgentHooks
{
    public static JsonObject Claude(Hub hub, JsonObject b)
    {
        try
        {
            var ev = J.Str(b["hook_event_name"]);
            var project = Project(J.Str(b["cwd"]));
            var title = "Claude Code" + (project.Length > 0 ? " · " + project : "");
            switch (ev)
            {
                case "Notification":
                {
                    var msg = J.Str(b["message"]);
                    hub.Notify(new JsonObject
                    {
                        ["title"] = title,
                        ["message"] = msg.Length > 0 ? J.Cut(msg, 250) : L.T("agent_input"),
                        ["level"] = "warn",
                    });
                    break;
                }
                case "Stop":
                {
                    var last = Plain(J.Str(b["last_assistant_message"]));
                    hub.Notify(new JsonObject
                    {
                        ["title"] = title + " — " + L.T("agent_done"),
                        ["message"] = last.Length > 0 ? J.Cut(last, 200) : L.T("agent_done"),
                    });
                    break;
                }
            }
        }
        catch (Exception e) { Log.W("claude hook: " + e.Message); }
        return new JsonObject();
    }

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
