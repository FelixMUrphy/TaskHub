using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace TaskHub;

/// <summary>
/// Native WinRT toasts (Microsoft.Toolkit.Uwp.Notifications, COM activation for unpackaged apps).
/// Toasts carry an "Open panel" button, buttons for task controls declared with "toast": true (with input
/// boxes / combo boxes for controls that need a value), link buttons on finish, and optional in-place
/// progress toasts (data-bound progress bar updated with the same tag).
/// </summary>
sealed class Notifier : INotifier
{
    readonly Settings S;
    public Hub Hub;
    public Action<string> OpenPanel;           // task id or null
    public Action<string, string> Balloon;     // fallback: tray balloon (title, msg)
    readonly BlockingCollection<Action> queue = new();
    readonly HashSet<string> progressDismissed = new();
    readonly Dictionary<string, uint> progressSeq = new();
    int failures;
    string iconPath;

    public Notifier(Settings s)
    {
        S = s;
        var th = new Thread(Worker) { IsBackground = true, Name = "toasts" };
        th.Start();
    }

    public void Init(string iconPng)
    {
        iconPath = iconPng;
        try { ToastNotificationManagerCompat.OnActivated += OnActivated; }
        catch (Exception e) { Log.W("toast activation hook failed: " + e.Message); }
    }

    void Worker()
    {
        foreach (var a in queue.GetConsumingEnumerable())
        {
            try { a(); } catch (Exception e) { Log.W("toast worker: " + e.Message); }
        }
    }

    static string Tag(string tid, string prefix = "")
    {
        var t = Regex.Replace(tid ?? "", "[^A-Za-z0-9_.-]", "");
        if (t.Length > 40) t = t.Substring(0, 40);
        return prefix + t;
    }

    // ------------------------------------------------------------ plain / interactive toasts
    public void Toast(string title, string msg, string level, string taskId, JsonObject task)
    {
        Log.W($"TOAST [{level}] {title}: {msg}");
        queue.Add(() => ShowToast(J.Cut(string.IsNullOrEmpty(title) ? Settings.AppId : title, 150), J.Cut(msg ?? "", 500), level, taskId, task));
    }

    void ShowToast(string title, string msg, string level, string tid, JsonObject task)
    {
        if (failures >= 3) { Balloon?.Invoke(title, msg); return; }
        try
        {
            var b = new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddArgument("task", tid ?? "")
                .AddText(title);
            if (!string.IsNullOrEmpty(msg)) b.AddText(msg);
            if (iconPath != null && File.Exists(iconPath)) b.AddAppLogoOverride(new Uri(iconPath));
            if (level == "error")
            {
                b.SetToastDuration(ToastDuration.Long);
                b.AddAudio(new Uri("ms-winsoundevent:Notification.Reminder"));
            }
            int actions = 0, inputs = 0;
            var state = task != null ? J.Str(task["state"]) : null;
            if (task != null && S.Bool("toast_controls", true) && Hub.IsActive(state))
            {
                foreach (var c in (task["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (!J.Truthy(c["toast"])) continue;
                    var states = (c["states"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x));
                    if (!states.Contains(state)) continue;
                    if (actions >= 3) break;
                    var cid = J.Str(c["id"]);
                    if (c["input"] is JsonObject inp)
                    {
                        if (inputs >= 3) continue;
                        var iid = "v_" + cid;
                        var lbl = J.Truthy(inp["label"]) ? J.Str(inp["label"]) : J.Str(c["label"]);
                        var typ = J.Str(inp["type"]);
                        if (typ == "select")
                        {
                            var opts = (inp["options"] as JsonArray).Select(x => J.Str(x)).Take(5).ToList();
                            var def = J.Str(inp["default"]);
                            if (!opts.Contains(def)) def = opts[0];
                            b.AddComboBox(iid, lbl, def, opts.Select(o => (o, o)).ToArray());
                        }
                        else if (typ == "bool")
                        {
                            b.AddComboBox(iid, lbl, J.Truthy(inp["default"]) ? "true" : "false", ("true", L.T("yes")), ("false", L.T("no")));
                        }
                        else
                        {
                            var ph = J.Truthy(inp["placeholder"]) ? J.Str(inp["placeholder"]) : J.Str(inp["default"]);
                            b.AddInputTextBox(iid, ph, lbl);
                        }
                        inputs++;
                    }
                    b.AddButton(new ToastButton()
                        .SetContent(J.Cut(J.Str(c["label"]), 40))
                        .AddArgument("action", "ctl").AddArgument("task", tid).AddArgument("cid", cid));
                    actions++;
                }
            }
            if (task != null && Hub.IsFinal(state ?? ""))
            {
                var links = (task["links"] as JsonArray ?? new JsonArray()).ToList();
                for (int i = 0; i < links.Count && actions < 2; i++)
                {
                    var l = links[i];
                    var label = l is JsonObject lo ? (J.Truthy(lo["label"]) ? J.Str(lo["label"]) : J.Str(lo["url"])) : J.Str(l);
                    b.AddButton(new ToastButton().SetContent(J.Cut(label, 40))
                        .AddArgument("action", "link").AddArgument("task", tid).AddArgument("i", i));
                    actions++;
                }
            }
            b.AddButton(new ToastButton().SetContent(L.T("open_panel")).AddArgument("action", "open").AddArgument("task", tid ?? ""));
            var tag = Tag(tid ?? "n") + Guid.NewGuid().ToString("N")[..6];
            b.Show(t => { t.Tag = tag; t.Group = Settings.AppId; });
            failures = 0;
        }
        catch (Exception e)
        {
            failures++;
            Log.W("toast failed: " + e);
            Balloon?.Invoke(title, msg);
        }
    }

    // ------------------------------------------------------------ in-place progress toasts
    public void Progress(JsonObject t)
    {
        queue.Add(() => ShowProgress(t));
    }

    public void ProgressEnd(string tid)
    {
        queue.Add(() =>
        {
            var tag = Tag(tid, "p_");
            progressSeq.Remove(tag);
            progressDismissed.Remove(tag);
            try { ToastNotificationManagerCompat.History.Remove(tag, "progress"); } catch { }
        });
    }

    static IDictionary<string, string> ProgressValues(JsonObject t)
    {
        var p = J.Num(t["progress"]);
        string vs = "";
        if (J.Num(t["current"]) is double cur)
            vs = J.Fmt(cur) + (J.Num(t["total"]) is double tot ? " / " + J.Fmt(tot) : "") + (J.Truthy(t["unit"]) ? " " + J.Str(t["unit"]) : "");
        else if (p != null) vs = Math.Round(p.Value * 100) + "%";
        var status = J.Truthy(t["status"]) ? J.Str(t["status"]) : J.Truthy(t["stage"]) ? J.Str(t["stage"]) : J.Str(t["state"]);
        return new Dictionary<string, string>
        {
            ["ptitle"] = J.Cut(J.Str(t["name"]), 100),
            ["pv"] = p != null ? p.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "indeterminate",
            ["pvs"] = vs,
            ["pst"] = J.Cut(status, 100),
        };
    }

    void ShowProgress(JsonObject t)
    {
        var tid = J.Str(t["id"]);
        var tag = Tag(tid, "p_");
        if (progressDismissed.Contains(tag)) return;
        try
        {
            var vals = ProgressValues(t);
            if (progressSeq.TryGetValue(tag, out var seq))
            {
                seq++;
                progressSeq[tag] = seq;
                var r = ToastNotificationManagerCompat.CreateToastNotifier().Update(new NotificationData(vals, seq), tag, "progress");
                if (r == NotificationUpdateResult.NotificationNotFound) progressDismissed.Add(tag);   // user closed it
                return;
            }
            var b = new ToastContentBuilder()
                .AddArgument("action", "open").AddArgument("task", tid)
                .AddText(J.Cut(J.Truthy(t["source"]) ? $"TaskHub · {J.Str(t["source"])}" : "TaskHub", 60))
                .AddVisualChild(new AdaptiveProgressBar
                {
                    Title = new BindableString("ptitle"),
                    Value = new BindableProgressBarValue("pv"),
                    ValueStringOverride = new BindableString("pvs"),
                    Status = new BindableString("pst"),
                })
                .AddAudio(new ToastAudio { Silent = true });
            if (iconPath != null && File.Exists(iconPath)) b.AddAppLogoOverride(new Uri(iconPath));
            foreach (var c in (t["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                         .Where(c => J.Truthy(c["toast"]) && c["input"] == null).Take(2))
                b.AddButton(new ToastButton().SetContent(J.Cut(J.Str(c["label"]), 40))
                    .AddArgument("action", "ctl").AddArgument("task", tid).AddArgument("cid", J.Str(c["id"])));
            b.AddButton(new ToastButton().SetContent(L.T("open_panel")).AddArgument("action", "open").AddArgument("task", tid));
            progressSeq[tag] = 1;
            b.Show(n =>
            {
                n.Tag = tag;
                n.Group = "progress";
                n.Data = new NotificationData(vals, 1);
            });
        }
        catch (Exception e) { Log.W("progress toast failed: " + e.Message); }
    }

    // ------------------------------------------------------------ activation (button clicks)
    void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            args.TryGetValue("action", out string action);
            args.TryGetValue("task", out string tid);
            Log.W($"toast activated: {e.Argument}");
            if (string.IsNullOrEmpty(tid)) tid = null;
            switch (action)
            {
                case "ctl":
                    args.TryGetValue("cid", out string cid);
                    JsonNode value = null;
                    JsonObject ctl = null;
                    try
                    {
                        var task = Hub.GetTask(tid);
                        ctl = (task["controls"] as JsonArray).OfType<JsonObject>().FirstOrDefault(c => J.Str(c["id"]) == cid);
                    }
                    catch { }
                    if (ctl?["input"] is JsonObject inp && e.UserInput.TryGetValue("v_" + cid, out var raw))
                    {
                        var s = raw?.ToString() ?? "";
                        var typ = J.Str(inp["type"]);
                        if (typ == "number")
                            value = double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
                        else if (typ == "bool") value = s == "true";
                        else value = s;
                    }
                    try
                    {
                        Hub.Trigger(tid, cid, new JsonObject { ["value"] = value }, "toast");
                    }
                    catch (Exception ex)
                    {
                        Toast("TaskHub", $"«{cid}»: {ex.Message}", "warn", tid, null);
                    }
                    break;
                case "link":
                    int i = args.GetInt("i");
                    var t2 = Hub.GetTask(tid);
                    var l = (t2["links"] as JsonArray)?[i];
                    var url = l is JsonObject lo ? J.Str(lo["url"]) : J.Str(l);
                    if (!string.IsNullOrEmpty(url)) Hub.OpenTarget(url);
                    break;
                default:
                    OpenPanel?.Invoke(tid);
                    break;
            }
        }
        catch (Exception ex) { Log.W("toast activation error: " + ex); }
    }
}
