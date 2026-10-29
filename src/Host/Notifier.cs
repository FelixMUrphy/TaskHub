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
    public Func<bool> PanelOpen;               // true -> no popup banners, toasts go silently to the notification center
    public TelegramBridge Telegram;            // every notification is mirrored there (if linked)
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
    public void Toast(string title, string msg, string level, string taskId, JsonObject task, bool dismissOnly = false, bool controls = true)
    {
        try { Telegram?.Notify(title, msg, level, taskId, task, controls); } catch (Exception e) { Log.W("telegram: " + e.Message); }
        if (!S.Bool("toasts", true)) return;   // Windows toasts disabled (config.json "toasts": false)
        Log.W($"TOAST [{level}] {title}: {msg}");
        queue.Add(() => ShowToast(J.Cut(string.IsNullOrEmpty(title) ? Settings.AppId : title, 150), J.Cut(msg ?? "", 500), level, taskId, task, dismissOnly, controls));
    }

    public Action UpdateClicked;               // click on the "new version" toast

    /// <summary>"New version" toast: a click calls UpdateClicked. About this PC's install, so not mirrored to Telegram.</summary>
    public void UpdateToast(string title, string msg)
    {
        if (!S.Bool("toasts", true)) return;
        Log.W($"TOAST [update] {title}: {msg}");
        queue.Add(() => ShowToast(title, msg, "info", null, null, action: "update"));
    }

    /// <summary>Toasts currently shown (banner or notification center), so they can be re-rendered when values change.</summary>
    sealed record Shown(string Title, string Msg, string Level, string Tid, JsonObject Snap, bool DismissOnly = false, bool Controls = true,
        string Action = null);
    readonly Dictionary<string, Shown> shown = new();   // tag -> content; touched only on the worker thread

    void ShowToast(string title, string msg, string level, string tid, JsonObject task, bool dismissOnly = false, bool controls = true,
        string action = null)
    {
        if (failures >= 3) { Balloon?.Invoke(title, msg); return; }
        try
        {
            var e = new Shown(title, msg, level, tid, task, dismissOnly && tid == null, controls, action);
            var b = Build(e, task, out bool interactive);
            var tag = Tag(tid ?? "n") + Guid.NewGuid().ToString("N")[..6];
            bool quiet = Quiet();
            Post(b, tag, quiet);
            failures = 0;
            if (tid != null) { shown[tag] = e; if (shown.Count > 100) PruneShown(); }
            if (!quiet)
            {
                // shorter on-screen time than Windows' default: after N s re-post the toast silently (same tag)
                // -> the banner disappears, the notification stays in the notification center
                double secs = interactive ? (J.Num(S.Get("toast_seconds_interactive")) ?? 8) : (J.Num(S.Get("toast_seconds")) ?? 4);
                if (secs > 0)
                    _ = Task.Delay(TimeSpan.FromSeconds(secs)).ContinueWith(_ => queue.Add(() => Repost(tag, e)));
            }
        }
        catch (Exception ex)
        {
            failures++;
            Log.W("toast failed: " + ex);
            Balloon?.Invoke(title, msg);
        }
    }

    /// <summary>Forget toasts no longer in the notification center (dismissed, clicked, cleared) - otherwise quiet toasts,
    /// which get no delayed re-post, would stay in `shown` with their task snapshot for the hub's lifetime.</summary>
    void PruneShown()
    {
        try
        {
            var live = ToastNotificationManagerCompat.History.GetHistory()
                .Where(n => n.Group == Settings.AppId).Select(n => n.Tag).ToHashSet();
            foreach (var k in shown.Keys.Where(k => !live.Contains(k)).ToList()) shown.Remove(k);
        }
        catch (Exception ex) { Log.W("toast history: " + ex.Message); }
    }

    ToastContentBuilder Build(Shown e, JsonObject task, out bool interactive)
    {
        var tid = e.Tid;
        var b = new ToastContentBuilder()
            .AddArgument("action", e.Action ?? (e.DismissOnly ? "none" : "open"))   // "none": a click just closes the toast
            .AddArgument("task", tid ?? "")
            .AddText(e.Title);
        if (!string.IsNullOrEmpty(e.Msg)) b.AddText(e.Msg);
        if (iconPath != null && File.Exists(iconPath)) b.AddAppLogoOverride(new Uri(iconPath));
        if (e.Level == "error") b.AddAudio(new Uri("ms-winsoundevent:Notification.Reminder"));
        int actions = 0, inputs = 0;
        var state = task != null ? J.Str(task["state"]) : null;
        if (task != null && e.Controls && S.Bool("toast_controls", true) && Hub.IsActive(state))
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
                    var cur = c["last_value"] ?? inp["default"];   // current value, not the registration default
                    if (typ == "select")
                    {
                        var opts = (inp["options"] as JsonArray).Select(x => J.Str(x)).Take(5).ToList();
                        var def = J.Str(cur);
                        if (!opts.Contains(def)) def = opts[0];
                        b.AddComboBox(iid, lbl, def, opts.Select(o => (o, o)).ToArray());
                    }
                    else if (typ == "bool")
                        b.AddComboBox(iid, lbl, J.Truthy(cur) ? "true" : "false", ("true", L.T("yes")), ("false", L.T("no")));
                    else
                    {
                        var ph = J.Truthy(inp["placeholder"]) ? J.Str(inp["placeholder"]) : "";
                        b.AddToastInput(new ToastTextBox(iid) { Title = lbl, PlaceholderContent = ph, DefaultInput = cur == null ? "" : J.Str(cur) });
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
        if (!e.DismissOnly)
            b.AddButton(new ToastButton().SetContent(L.T("open_panel")).AddArgument("action", "open").AddArgument("task", tid ?? ""));
        interactive = actions > 0 || inputs > 0;
        return b;
    }

    static void Post(ToastContentBuilder b, string tag, bool quiet)
    {
        b.Show(t =>
        {
            t.Tag = tag; t.Group = Settings.AppId;
            t.SuppressPopup = quiet;
            t.Failed += (s, a) => Log.W($"toast[{tag}] failed: {a.ErrorCode}");
        });
    }

    bool Quiet()
    {
        if (S.Bool("toasts_when_panel_open", false)) return false;
        try { return PanelOpen?.Invoke() ?? false; } catch { return false; }
    }

    /// <summary>Silently re-render a shown toast from the task's current state (worker thread).</summary>
    void Repost(string tag, Shown e)
    {
        try
        {
            // only if it is still there (not clicked / dismissed by the user)
            if (!ToastNotificationManagerCompat.History.GetHistory().Any(n => n.Tag == tag && n.Group == Settings.AppId))
            {
                shown.Remove(tag);
                return;
            }
            JsonObject task = e.Snap;
            if (e.Tid != null) try { task = Hub.GetTask(e.Tid); } catch { }
            Post(Build(e, task, out _), tag, true);
        }
        catch (Exception ex) { Log.W("repost: " + ex.Message); }
    }

    /// <summary>A control value changed: refresh this task's toasts so their inputs show the current value.</summary>
    public void Refresh(string tid)
    {
        queue.Add(() =>
        {
            foreach (var (tag, e) in shown.Where(kv => kv.Value.Tid == tid).ToList()) Repost(tag, e);
        });
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
                n.SuppressPopup = Quiet();
            });
        }
        catch (Exception e) { Log.W("progress toast failed: " + e.Message); }
    }

    void ShowConfirm(string tid, JsonObject c, JsonNode value)
    {
        try
        {
            var cid = J.Str(c["id"]);
            var yes = new ToastButton().SetContent(L.T("yes"))
                .AddArgument("action", "ctl").AddArgument("task", tid).AddArgument("cid", cid).AddArgument("ok", "1");
            if (value != null) yes.AddArgument("val", J.Dump(value));
            var b = new ToastContentBuilder()
                .AddArgument("action", "none").AddArgument("task", tid)
                .AddText(J.Cut(J.Str(c["label"]) + (value != null ? " = " + J.Str(value) : ""), 150))
                .AddText(J.Cut(J.Str(c["confirm"]), 500))
                .AddButton(yes)
                .AddButton(new ToastButton().SetContent(L.T("no")).AddArgument("action", "none"));
            Post(b, Tag(tid) + "q" + Guid.NewGuid().ToString("N")[..6], false);
        }
        catch (Exception ex) { Log.W("confirm toast failed: " + ex.Message); }
    }

    // ------------------------------------------------------------ activation (button clicks)
    void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            args.TryGetValue("action", out string action);
            args.TryGetValue("task", out string tid);
            Log.W($"toast activated: {e.Argument} input=[{string.Join(",", e.UserInput.Select(kv => kv.Key + "=" + kv.Value))}]");
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
                    if (args.TryGetValue("val", out string vj)) value = J.Parse(vj);   // from a confirmation toast
                    else if (ctl?["input"] is JsonObject inp && e.UserInput.TryGetValue("v_" + cid, out var raw))
                    {
                        var s = raw?.ToString() ?? "";
                        var typ = J.Str(inp["type"]);
                        if (typ == "number")
                        {
                            if (!double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                            {
                                Toast("TaskHub", $"«{J.Str(ctl["label"])}»: {L.T("tg_bad_value", J.Cut(s, 100))}", "warn", tid, null);
                                break;
                            }
                            value = d;
                        }
                        else if (typ == "bool") value = s == "true";
                        else value = s;
                    }
                    // a control with "confirm" asks first, like the panel: a second toast with Yes / No
                    if (ctl != null && J.Truthy(ctl["confirm"]) && !args.Contains("ok"))
                    {
                        var (c2, v2) = ((JsonObject)ctl.DeepClone(), value?.DeepClone());
                        queue.Add(() => ShowConfirm(tid, c2, v2));
                        break;
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
                case "update":
                    UpdateClicked?.Invoke();
                    break;
                case "none":
                    break;      // informational toast (e.g. agent hooks): clicking only dismisses it
                default:
                    OpenPanel?.Invoke(tid);
                    break;
            }
        }
        catch (Exception ex) { Log.W("toast activation error: " + ex); }
    }
}
