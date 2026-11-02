using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>Notifications, focus and the read side: snapshot, brief list, task, summary.</summary>
sealed partial class Hub
{
    // ------------------------------------------------------------ misc
    public JsonObject Notify(JsonObject b)
    {
        var title = J.Truthy(b["title"]) ? J.Str(b["title"]) : Settings.AppId;
        var msg = J.Truthy(b["message"]) ? J.Str(b["message"]) : J.Str(b["msg"]);
        var level = J.Truthy(b["level"]) ? J.Str(b["level"]) : "info";
        string tid = J.Truthy(b["task"]) ? J.Str(b["task"]) : null;
        JsonObject snap = null;
        lock (Lock)
            if (tid != null && tasks.TryGetValue(tid, out var t)) snap = ToastSnap(t);
        // "force" overrides the user's mute, but not "toasts": false in config.json (notifications disabled entirely)
        bool dismiss = snap == null && J.Str(b["click"]) == "dismiss";
        if (!Muted || J.Truthy(b["force"])) Notifier?.Toast(title, msg, level, tid, snap, dismiss);
        return new JsonObject { ["ok"] = true };
    }

    public void SetFocus(string tid)
    {
        lock (Lock)
        {
            focus = new JsonObject { ["task"] = string.IsNullOrEmpty(tid) ? null : tid, ["seq"] = (J.Num(focus["seq"]) ?? 0) + 1 };
            Bump();
        }
    }

    public JsonObject Snapshot(bool activeOnly = false, bool brief = false)
    {
        lock (Lock)
        {
            var list = tasks.Values
                .OrderBy(t => IsActive(St(t)) ? 0 : 1)
                .ThenByDescending(t => J.Num(t["finished"]) ?? J.Num(t["started"]) ?? 0)
                .Where(t => !activeOnly || IsActive(St(t)))
                .Select(t =>
                {
                    var c = (JsonObject)t.DeepClone();
                    if (brief && c["log"] is JsonArray lg && lg.Count > 5)
                        c["log"] = new JsonArray(lg.Skip(lg.Count - 5).Select(x => x.DeepClone()).ToArray());
                    return (JsonNode)c;
                }).ToArray();
            return new JsonObject
            {
                ["version"] = Version, ["now"] = J.Now(), ["muted"] = Muted,
                ["lang"] = L.Lang, ["language"] = L.Setting,
                ["panel"] = PanelSettings(),
                ["limits"] = LimitsView(),
                ["limits_warn"] = new JsonArray(LimitThresholds().Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                ["focus"] = focus.DeepClone(),
                ["server"] = new JsonObject { ["version"] = Settings.Version, ["pid"] = Environment.ProcessId, ["impl"] = "dotnet" },
                ["tasks"] = new JsonArray(list),
            };
        }
    }

    /// <summary>Light view of all tasks (no log / widgets) - for the Telegram progress messages.</summary>
    public List<JsonObject> Brief()
    {
        lock (Lock)
            return tasks.Values.Select(t =>
            {
                var o = new JsonObject();
                foreach (var k in new[] { "id", "name", "state", "progress", "current", "total", "unit", "status", "stage", "started", "finished", "controls", "eta_at" })
                    o[k] = J.C(t[k]);
                return o;
            }).ToList();
    }

    public JsonObject GetTask(string tid)
    {
        lock (Lock) return (JsonObject)Get(tid).DeepClone();
    }

    public (int total, int active) Counts()
    {
        lock (Lock) return (tasks.Count, tasks.Values.Count(t => IsActive(St(t))));
    }

    public sealed class Summary
    {
        public List<JsonObject> ActiveT = new(), Failed = new(), Stale = new(), RecentDone = new();
        public double? Progress;
    }

    public Summary GetSummary()
    {
        lock (Lock)
        {
            var s = new Summary();
            var now = J.Now();
            foreach (var t in tasks.Values)
            {
                var st = St(t);
                if (IsActive(st)) s.ActiveT.Add((JsonObject)t.DeepClone());
                if (st == "stale") s.Stale.Add((JsonObject)t.DeepClone());
                if (st is "failed" or "lost" && !J.Truthy(t["seen"])) s.Failed.Add((JsonObject)t.DeepClone());
                if (st == "done" && now - (J.Num(t["finished"]) ?? 0) < 600) s.RecentDone.Add((JsonObject)t.DeepClone());
            }
            var pr = s.ActiveT.Select(t => J.Num(t["progress"])).Where(x => x != null).Select(x => x.Value).ToList();
            s.Progress = pr.Count > 0 ? pr.Average() : null;
            return s;
        }
    }
}
