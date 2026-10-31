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
    public Action<string, string> Balloon;     // fallback: tray balloon (title, msg)
    public Func<bool> PanelOpen;               // true -> no popup banners, toasts go silently to the notification center
    public TelegramBridge Telegram;            // every notification is mirrored there (if linked)
    readonly BlockingCollection<Action> queue = new();
    readonly HashSet<string> progressDismissed = new();
    readonly Dictionary<string, uint> progressSeq = new();
    int failures;                              // toasts failed in a row (worker thread)
    DateTime toastsOffUntil;                   // after 3 failures: tray balloons for a while, then toasts are tried again
    const int ContentTooLarge = unchecked((int)0x803E0115);   // payload > 5 KB: this toast's fault, not the system's
    string iconPath;

    // a toast click can start the hub (COM activation) before the tray wires the panel: the click waits here
    readonly object activationLock = new();
    Action<string> openPanel;
    bool openPending;
    string openPendingTask;

    /// <summary>Opens the panel (task id or null) on the UI thread. Set by the tray; a click that came earlier is replayed then.</summary>
    public Action<string> OpenPanel
    {
        set
        {
            bool replay;
            string tid;
            lock (activationLock)
            {
                openPanel = value;
                replay = value != null && openPending;
                tid = openPendingTask;
                if (replay) { openPending = false; openPendingTask = null; }
            }
            if (replay) value(tid);
        }
    }

    void DoOpenPanel(string tid)
    {
        Action<string> a;
        lock (activationLock)
        {
            a = openPanel;
            if (a == null) { openPending = true; openPendingTask = tid; return; }
        }
        a(tid);
    }

    // ------------------------------------------------------------ Windows' own switch for our notifications
    string systemSetting = "unknown";
    DateTime systemSettingAt;

    /// <summary>"enabled", "disabled_app" (Settings -> Notifications -> TaskHub), "disabled_user" (all notifications off),
    /// "disabled_policy", "disabled_manifest" or "unknown". Turned off there, toasts are dropped silently (Show doesn't
    /// fail). Cached for 30 s.</summary>
    public string SystemSetting
    {
        get
        {
            lock (activationLock)
            {
                if ((DateTime.UtcNow - systemSettingAt).TotalSeconds < 30) return systemSetting;
                systemSettingAt = DateTime.UtcNow;
            }
            string v;
            try
            {
                v = ToastNotificationManagerCompat.CreateToastNotifier().Setting switch
                {
                    NotificationSetting.Enabled => "enabled",
                    NotificationSetting.DisabledForApplication => "disabled_app",
                    NotificationSetting.DisabledForUser => "disabled_user",
                    NotificationSetting.DisabledByGroupPolicy => "disabled_policy",
                    NotificationSetting.DisabledByManifest => "disabled_manifest",
                    _ => "unknown",
                };
            }
            catch (Exception e) { v = "unknown"; Log.W("toast setting: " + e.Message); }
            lock (activationLock) systemSetting = v;
            return v;
        }
    }

    public bool SystemDisabled => SystemSetting.StartsWith("disabled");

    public Notifier(Settings s)
    {
        S = s;
        var th = new Thread(Worker) { IsBackground = true, Name = "toasts" };
        th.Start();
        new Thread(Dispatcher) { IsBackground = true, Name = "toast-gate" }.Start();
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

    // ------------------------------------------------------------ rate limit (toasts and the Telegram mirror)
    // A burst (a loop calling /api/notify, toast:true on every update) must not flood Windows / Telegram or pile up
    // memory: notifications wait in a bounded list; one task gets one every toast_task_gap seconds (newer ones replace
    // the waiting one), all together toast_rate per minute (token bucket; while it is empty, waiting notifications
    // without a task coalesce too). Finished / failed tasks and errors are never replaced or dropped first.
    sealed class Pending
    {
        public string Title, Msg, Level, Tid;
        public JsonObject Task;
        public bool DismissOnly, Controls, Final;
        public int More;                       // notifications folded into this one
    }
    const int MaxPending = 100;
    readonly object gate = new();
    readonly LinkedList<Pending> pending = new();
    readonly Dictionary<string, double> lastOfTask = new();
    double tokens = -1, tokensAt;
    int dropped;

    /// <summary>Notifications waiting for the rate limit plus toasts waiting to be shown (GET /api/health "toast_queue").</summary>
    public int QueueLength { get { lock (gate) return pending.Count + queue.Count; } }

    double Rate => J.Num(S.Get("toast_rate")) is double r && r >= 0 ? r : 10;
    double TaskGap => J.Num(S.Get("toast_task_gap")) is double g && g >= 0 ? g : 2;

    public void Toast(string title, string msg, string level, string taskId, JsonObject task, bool dismissOnly = false, bool controls = true)
    {
        var p = new Pending
        {
            Title = title, Msg = msg, Level = level, Tid = taskId, Task = task, DismissOnly = dismissOnly, Controls = controls,
            Final = level == "error" || (task != null && Hub.IsFinal(J.Str(task["state"]))),
        };
        lock (gate)
        {
            Refill(J.Now());
            // the same task (or, while the limit holds them back, any task-less one) already waiting: the newer replaces it
            bool held = Rate > 0 && tokens < 1;
            for (var x = pending.First; x != null; x = x.Next)
            {
                var o = x.Value;
                if (o.Final || p.Final && o.Tid == null) continue;
                if (taskId != null ? o.Tid == taskId : held && o.Tid == null && !p.Final)
                {
                    p.More = o.More + 1;
                    x.Value = p;
                    Monitor.PulseAll(gate);
                    return;
                }
            }
            pending.AddLast(p);
            while (pending.Count > MaxPending)
            {
                var victim = pending.First;
                for (var x = pending.First; x != null; x = x.Next)
                    if (!x.Value.Final) { victim = x; break; }
                dropped += 1 + victim.Value.More;
                pending.Remove(victim);
            }
            Monitor.PulseAll(gate);
        }
    }

    void Refill(double now)
    {
        var rate = Rate;
        if (rate <= 0) return;
        double burst = Math.Max(1, rate);
        if (tokens < 0) { tokens = burst; tokensAt = now; return; }
        tokens = Math.Min(burst, tokens + (now - tokensAt) * rate / 60);
        tokensAt = now;
    }

    /// <summary>Hands waiting notifications on as the limits allow (own thread).</summary>
    void Dispatcher()
    {
        while (true)
        {
            Pending p = null;
            int more = 0;
            try
            {
                lock (gate)
                {
                    while (p == null)
                    {
                        var now = J.Now();
                        Refill(now);
                        double gap = TaskGap, wait = 60;
                        bool limited = Rate > 0;
                        for (var x = pending.First; x != null; x = x.Next)
                        {
                            var o = x.Value;
                            double ready = 0;
                            if (o.Tid != null && lastOfTask.TryGetValue(o.Tid, out var last)) ready = last + gap - now;
                            // finished / failed tasks and errors don't wait for the global limit (they still use it up)
                            if (limited && !o.Final && tokens < 1) ready = Math.Max(ready, (1 - tokens) * 60 / Rate);
                            if (ready <= 0) { p = o; pending.Remove(x); break; }
                            wait = Math.Min(wait, ready);
                        }
                        if (p != null) break;
                        Monitor.Wait(gate, TimeSpan.FromSeconds(Math.Clamp(wait, 0.05, 60)));
                    }
                    if (Rate > 0) tokens = Math.Max(0, tokens - 1);
                    if (p.Tid != null)
                    {
                        lastOfTask[p.Tid] = J.Now();
                        if (lastOfTask.Count > 500)
                            foreach (var k in lastOfTask.Where(kv => J.Now() - kv.Value > 60).Select(kv => kv.Key).ToList()) lastOfTask.Remove(k);
                    }
                    more = p.More + dropped;
                    dropped = 0;
                }
                var msg = more > 0 ? (string.IsNullOrEmpty(p.Msg) ? "" : p.Msg + "\n") + L.N("toast_more", more) : p.Msg;
                Deliver(p.Title, msg, p.Level, p.Tid, p.Task, p.DismissOnly, p.Controls);
            }
            catch (Exception e) { Log.W("toast gate: " + e.Message); }
        }
    }

    void Deliver(string title, string msg, string level, string taskId, JsonObject task, bool dismissOnly, bool controls)
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
        if (DateTime.UtcNow < toastsOffUntil) { Balloon?.Invoke(title, msg); return; }
        // notifications turned off in Windows: a failure would go unseen - the tray balloon instead
        if (level == "error" && SystemDisabled) { Balloon?.Invoke(title, msg); return; }
        try
        {
            var e = new Shown(title, msg, level, tid, task, dismissOnly && tid == null, controls, action);
            var b = Build(e, task, out bool interactive);
            if (TooLarge(b))   // > 5 KB is refused: without the task's buttons / inputs then
            {
                e = e with { Controls = false };
                b = Build(e, task, out interactive);
            }
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
            // only system failures count towards the switch (not a payload that was too large); after 3 in a row
            // balloons for 10 minutes, then toasts get another chance
            if (ex.HResult != ContentTooLarge && ++failures >= 3)
            {
                failures = 0;
                toastsOffUntil = DateTime.UtcNow.AddMinutes(10);
                Log.W("toasts failing: tray balloons for 10 minutes");
            }
            Log.W("toast failed: " + ex);
            Balloon?.Invoke(title, msg);
        }
    }

    /// <summary>Windows refuses a toast over 5 KB (0x803E0115); leave room for the attributes Show adds.</summary>
    static bool TooLarge(ToastContentBuilder b)
    {
        try { return b.GetToastContent().GetContent().Length > 4500; }
        catch { return false; }
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
                    var lbl = J.Cut(J.Truthy(inp["label"]) ? J.Str(inp["label"]) : J.Str(c["label"]), 60);
                    var typ = J.Str(inp["type"]);
                    var cur = c["last_value"] ?? inp["default"];   // current value, not the registration default
                    if (typ == "select")
                    {
                        var opts = (inp["options"] as JsonArray).Select(x => J.Str(x)).Take(5).ToList();
                        var def = J.Str(cur);
                        if (!opts.Contains(def)) def = opts[0];
                        // the option itself is the id (it is the value sent back); only the shown text is shortened
                        b.AddComboBox(iid, lbl, def, opts.Select(o => (o, J.Cut(o, 60))).ToArray());
                    }
                    else if (typ == "bool")
                        b.AddComboBox(iid, lbl, J.Truthy(cur) ? "true" : "false", ("true", L.T("yes")), ("false", L.T("no")));
                    else
                    {
                        var ph = J.Truthy(inp["placeholder"]) ? J.Cut(J.Str(inp["placeholder"]), 60) : "";
                        // the last value can be up to 2000 characters; the whole toast must stay under 5 KB
                        b.AddToastInput(new ToastTextBox(iid) { Title = lbl, PlaceholderContent = ph, DefaultInput = cur == null ? "" : J.Cut(J.Str(cur), 200) });
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
            if (value != null)
            {
                // the value rides in the button; a long one (text up to 2000 characters) would push the toast over 5 KB,
                // so it waits here and the button carries a key (lost only if the hub restarts in between)
                var dump = J.Dump(value);
                if (dump.Length <= 500) yes.AddArgument("val", dump);
                else
                {
                    var key = Guid.NewGuid().ToString("N")[..12];
                    lock (confirmValues)
                    {
                        if (confirmValues.Count > 50) confirmValues.Clear();
                        confirmValues[key] = dump;
                    }
                    yes.AddArgument("valkey", key);
                }
            }
            var b = new ToastContentBuilder()
                .AddArgument("action", "none").AddArgument("task", tid)
                .AddText(J.Cut(J.Cut(J.Str(c["label"]), 60) + (value != null ? " = " + J.Cut(J.Str(value), 100) : ""), 150))
                .AddText(J.Cut(J.Str(c["confirm"]), 500))
                .AddButton(yes)
                .AddButton(new ToastButton().SetContent(L.T("no")).AddArgument("action", "none"));
            Post(b, Tag(tid) + "q" + Guid.NewGuid().ToString("N")[..6], false);
        }
        catch (Exception ex) { Log.W("confirm toast failed: " + ex.Message); }
    }

    readonly Dictionary<string, string> confirmValues = new();   // long values of confirmation toasts (key -> JSON)

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
                    else if (args.TryGetValue("valkey", out string vk))
                    {
                        string kept;
                        lock (confirmValues) confirmValues.Remove(vk, out kept);
                        if (kept == null) { DoOpenPanel(tid); break; }   // the hub restarted meanwhile: the panel asks again
                        value = J.Parse(kept);
                    }
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
                    DoOpenPanel(tid);
                    break;
            }
        }
        catch (Exception ex) { Log.W("toast activation error: " + ex); }
    }
}
