using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Telegram bridge: every notification TaskHub shows is also sent to the user's own Telegram bot, with the task's
/// notification buttons as inline buttons (presses come back as control triggers). Commands: /tasks /mute /unmute /help.
/// Setup: the user creates a bot with @BotFather, pastes its token; TaskHub shows a t.me deep link with a one-time code,
/// the user presses Start and that chat becomes the only one the bot talks to.
/// Config: telegram_token, telegram_chat, telegram_away_only (+ telegram_away_minutes), telegram_api (tests).
/// </summary>
sealed class TelegramBridge
{
    readonly Settings S;
    readonly Hub hub;
    readonly HttpClient http = new(new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(75) };
    readonly BlockingCollection<(string method, JsonObject body, byte[] png)> outbox = new(500);
    sealed class Cb { public string Tid, Cid, Sig; public JsonNode Value; public bool Ask, Confirmed; }
    readonly Dictionary<string, Cb> callbacks = new();          // callback_data key -> control (+ value)
    readonly Dictionary<string, string> cbIndex = new();        // same control/value -> same key (buttons stay valid)
    readonly Dictionary<long, (string tid, string cid)> asks = new();   // "reply with a value" prompt message -> control
    (string tid, string cid, double t)? lastAsk;
    int cbSeq;
    CancellationTokenSource cts;
    public string BotName { get; private set; }
    public string PairCode { get; private set; }
    public string Status { get; private set; } = "off";

    public TelegramBridge(Settings s, Hub h)
    {
        S = s; hub = h;
        new Thread(SendWorker) { IsBackground = true, Name = "telegram-send" }.Start();
        new Thread(ProgressWorker) { IsBackground = true, Name = "telegram-progress" }.Start();
    }

    string Token => J.Str(S.Get("telegram_token"));
    long Chat => J.Num(S.Get("telegram_chat")) is double d ? (long)d : 0;
    string Api(string method) => (J.Str(S.Get("telegram_api")) is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.telegram.org")
                                 + "/bot" + Token + "/" + method;
    public bool Linked => Token.Length > 0 && Chat != 0;
    public bool AwayOnly => S.Bool("telegram_away_only", false);

    // ------------------------------------------------------------ lifecycle
    public void Start()
    {
        cts?.Cancel();
        if (Token.Length == 0) { Status = "off"; BotName = null; return; }
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        Status = "connecting";
        _ = Task.Run(() => Loop(ct));
    }

    /// <summary>Save a new bot token and start pairing; returns status incl. the t.me link to press.</summary>
    public JsonObject Pair(string token)
    {
        token = (token ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(token, @"^\d{5,}:[A-Za-z0-9_-]{30,}$"))
            throw new ApiError(400, "this does not look like a bot token from @BotFather (123456789:AA...)");
        S.Set(("telegram_token", token), ("telegram_chat", 0));
        PairCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        BotName = null;
        Start();
        for (int i = 0; i < 100 && BotName == null && Status != "invalid token"; i++) Thread.Sleep(100);
        if (Status == "invalid token") throw new ApiError(400, "Telegram rejected the token");
        return State();
    }

    /// <summary>Keep the bot, link a different chat (e.g. the account on the phone): new one-time code.</summary>
    public JsonObject Relink()
    {
        if (Token.Length == 0) throw new ApiError(400, "no bot token yet");
        return Pair(Token);
    }

    public void Disconnect()
    {
        S.Set(("telegram_token", null), ("telegram_chat", null));
        PairCode = null;
        Start();
    }

    public void SetAwayOnly(bool on) => S.Set(("telegram_away_only", on));

    public JsonObject State() => new()
    {
        ["configured"] = Token.Length > 0, ["linked"] = Linked, ["bot"] = BotName, ["status"] = Status,
        ["away_only"] = AwayOnly, ["progress"] = ShowProgress,
        ["link"] = PairCode != null && BotName != null ? $"https://t.me/{BotName}?start={PairCode}" : null,
    };

    public void Test() => Enqueue(Chat, "✅ <b>TaskHub</b>\n" + Esc(L.T("tg_test_msg", Environment.MachineName)), null);

    // ------------------------------------------------------------ outgoing
    /// <summary>Called for every notification TaskHub shows (already filtered by mute).</summary>
    public void Notify(string title, string msg, string level, string tid, JsonObject task, bool controls)
    {
        if (!Linked) return;
        if (AwayOnly && IdleSeconds() < 60 * S.Int("telegram_away_minutes", 5)) return;
        var icon = level == "error" ? "❌ " : level == "warn" ? "⚠️ " : "";
        if (title != null && (title.StartsWith("❌") || title.StartsWith("⚠"))) icon = "";
        var text = icon + "<b>" + Esc(title) + "</b>" + (string.IsNullOrEmpty(msg) ? "" : "\n" + Esc(msg));
        Enqueue(Chat, text, controls ? Keyboard(tid, task) : null);
        // charts: only once, when the task has finished (not on every change)
        if (task == null || !ShowProgress || !Hub.IsFinal(J.Str(task["state"]))) return;
        var charts = ChartImage.Pick(task);
        if (charts.Count == 0) return;
        lock (chartsSent)
        {
            if (chartsSent.Count > 200) chartsSent.Clear();
            if (!chartsSent.Add(tid + "@" + J.Str(task["finished"]))) return;
        }
        try
        {
            var png = ChartImage.Render(J.Str(task["name"]), charts);
            outbox.TryAdd(("sendPhoto", new JsonObject { ["chat_id"] = Chat, ["caption"] = "📈 " + J.Cut(J.Str(task["name"]), 200) }, png));
        }
        catch (Exception e) { Log.W("telegram chart: " + e.Message); }
    }

    JsonObject Keyboard(string tid, JsonObject task)
    {
        if (tid == null || task == null) return null;
        var state = J.Str(task["state"]);
        if (!Hub.IsActive(state)) return null;
        var rows = new JsonArray();
        foreach (var c in (task["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (!J.Truthy(c["toast"]) || rows.Count >= 10) continue;
            if (!(c["states"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).Contains(state)) continue;
            var cid = J.Str(c["id"]);
            var label = J.Cut(J.Str(c["label"]), 30);
            if (c["input"] is JsonObject inp)
            {
                var typ = J.Str(inp["type"]);
                if (typ == "select")
                {
                    // narrow phone screens: max 3 per row and ~30 characters per row, the rest wraps
                    var row = new JsonArray();
                    int len = 0;
                    foreach (var o in (inp["options"] as JsonArray ?? new JsonArray()).Take(9))
                    {
                        var text = label + ": " + J.Str(o);
                        if (row.Count > 0 && (row.Count >= 3 || len + text.Length > 30)) { rows.Add(row); row = new JsonArray(); len = 0; }
                        row.Add(Button(text, tid, cid, o?.DeepClone()));
                        len += text.Length;
                    }
                    if (row.Count > 0) rows.Add(row);
                }
                else if (typ == "bool")
                    rows.Add(new JsonArray(Button(label + " ✓", tid, cid, true), Button(label + " ✗", tid, cid, false)));
                else   // text / number: the bot asks for the value, the user replies to that message
                    rows.Add(new JsonArray(Button("✏️ " + label + "…", tid, cid, null, ask: true)));
            }
            else rows.Add(new JsonArray(Button(label, tid, cid, null)));
        }
        return rows.Count == 0 ? null : new JsonObject { ["inline_keyboard"] = rows };
    }

    JsonObject Button(string text, string tid, string cid, JsonNode value, bool ask = false, bool confirmed = false)
    {
        var sig = tid + "\n" + cid + "\n" + J.Dump(value) + "\n" + ask + "\n" + confirmed;
        string key;
        lock (callbacks)
        {
            if (!cbIndex.TryGetValue(sig, out key) || !callbacks.ContainsKey(key))
            {
                key = "k" + (++cbSeq);
                callbacks[key] = new Cb { Tid = tid, Cid = cid, Value = value, Ask = ask, Confirmed = confirmed, Sig = sig };
                cbIndex[sig] = key;
                if (callbacks.Remove("k" + (cbSeq - 1000), out var old)) cbIndex.Remove(old.Sig);
            }
        }
        return new JsonObject { ["text"] = text, ["callback_data"] = key };
    }

    void Enqueue(long chat, string text, JsonObject keyboard)
    {
        if (chat == 0) return;
        var m = new JsonObject { ["chat_id"] = chat, ["text"] = J.Cut(text, 3900), ["parse_mode"] = "HTML", ["disable_web_page_preview"] = true };
        if (keyboard != null) m["reply_markup"] = keyboard;
        outbox.TryAdd(("sendMessage", m, null));
    }

    void SendWorker()
    {
        foreach (var (method, m, png) in outbox.GetConsumingEnumerable())
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var (code, _) = (png != null ? CallPhoto(m, png) : Call(method, m, 20)).GetAwaiter().GetResult();
                    if (code == 429) { Thread.Sleep(3000); continue; }
                    if (code != 200) Log.W($"telegram {method}: HTTP {code}");
                    break;
                }
                catch (Exception e) { Log.W($"telegram {method}: " + e.Message); Thread.Sleep(2000); }
            }
        }
    }

    // ------------------------------------------------------------ live progress: one silent message per task, edited in place
    sealed class Live { public long Chat, Msg; public string Text; public double Last; }
    readonly Dictionary<string, Live> live = new();          // progress thread only
    readonly HashSet<string> chartsSent = new();
    public bool ShowProgress => S.Bool("telegram_progress", true);
    public void SetProgress(bool on) => S.Set(("telegram_progress", on));

    void ProgressWorker()
    {
        while (true)
        {
            Thread.Sleep(2000);
            try { ProgressTick(); } catch (Exception e) { Log.W("telegram progress: " + e.Message); }
        }
    }

    void ProgressTick()
    {
        if (!Linked || !ShowProgress) { live.Clear(); return; }
        var now = J.Now();
        var tasks = hub.Brief().GroupBy(t => J.Str(t["id"])).ToDictionary(g => g.Key, g => g.First());
        foreach (var (tid, p) in live.ToList())
        {
            if (p.Chat != Chat) { live.Remove(tid); continue; }
            tasks.TryGetValue(tid, out var t);
            if (t != null && Hub.IsActive(J.Str(t["state"]))) continue;
            if (t != null && p.Msg != 0) Edit(p, ProgressText(t, now), null, now);   // final state, buttons removed
            live.Remove(tid);
        }
        bool atPc = AwayOnly && IdleSeconds() < 60 * S.Int("telegram_away_minutes", 5);
        foreach (var (tid, t) in tasks)
        {
            if (!Hub.IsActive(J.Str(t["state"])) || J.Num(t["progress"]) == null) continue;
            var text = ProgressText(t, now);
            var kb = Keyboard(tid, t);
            if (live.TryGetValue(tid, out var p))
            {
                if (p.Msg != 0 && text + J.Dump(kb) != p.Text && now - p.Last >= 5) Edit(p, text, kb, now);
                continue;
            }
            if (hub.Muted || atPc || live.Count >= 5) continue;
            var body = new JsonObject
            {
                ["chat_id"] = Chat, ["text"] = text, ["parse_mode"] = "HTML", ["disable_notification"] = true,
            };
            if (kb != null) body["reply_markup"] = kb;
            var (code, r) = Call("sendMessage", body, 20).GetAwaiter().GetResult();
            if (code == 429) return;
            if (code == 200 && J.Num(r?["result"]?["message_id"]) is double id)
                live[tid] = new Live { Chat = Chat, Msg = (long)id, Text = text + J.Dump(kb), Last = now };
            else ProgressError("send", code, r);
        }
    }

    void Edit(Live p, string text, JsonObject kb, double now)
    {
        var body = new JsonObject
        {
            ["chat_id"] = p.Chat, ["message_id"] = p.Msg, ["text"] = text, ["parse_mode"] = "HTML",
        };
        if (kb != null) body["reply_markup"] = kb.DeepClone();
        var (code, r) = Call("editMessageText", body, 20).GetAwaiter().GetResult();
        p.Last = now;
        if (code == 200 || J.Str(r?["description"]).Contains("not modified")) p.Text = text + J.Dump(kb);
        else
        {
            ProgressError("edit", code, r);
            if (code == 400) p.Msg = 0;   // e.g. the user deleted the message: stop updating it
        }
    }

    string lastProgressError;
    void ProgressError(string what, int code, JsonObject r)
    {
        var e = $"telegram progress {what}: HTTP {code} {J.Str(r?["description"])}";
        if (e != lastProgressError) Log.W(lastProgressError = e);
    }

    static string ProgressText(JsonObject t, double now)
    {
        var st = J.Str(t["state"]);
        var icon = st switch
        {
            "running" => "⏳", "paused" => "⏸", "stale" => "⚠️", "done" => "✅", "cancelled" => "⏹", _ => "❌",
        };
        double p = st == "done" ? 1 : Math.Clamp(J.Num(t["progress"]) ?? 0, 0, 1);
        int n = (int)Math.Round(p * 12);
        var sb = new StringBuilder();
        sb.Append(icon).Append(" <b>").Append(Esc(J.Cut(J.Str(t["name"]), 120))).Append("</b>\n");
        sb.Append("<code>").Append(new string('█', n)).Append(new string('░', 12 - n)).Append("</code> ")
          .Append((int)Math.Floor(p * 100)).Append('%');
        if (J.Num(t["current"]) is double cur && J.Num(t["total"]) is double tot)
        {
            sb.Append("  ·  ").Append(J.Fmt(st == "done" ? tot : cur)).Append('/').Append(J.Fmt(tot));
            if (J.Str(t["unit"]) is { Length: > 0 } u) sb.Append(' ').Append(Esc(u));
        }
        var status = J.Str(t["status"]).Length > 0 ? J.Str(t["status"]) : J.Str(t["stage"]);
        if (status.Length > 0) sb.Append('\n').Append(Esc(J.Cut(status, 150)));
        double start = J.Num(t["started"]) ?? now;
        double end = Hub.IsFinal(st) ? J.Num(t["finished"]) ?? now : now;
        sb.Append("\n⏱ ").Append(Esc(Coarse(end - start)));
        if (st == "running" && p < 1 && J.Num(t["eta_at"]) is double eta) sb.Append("  ·  ETA ").Append(Esc(Coarse(Math.Max(0, eta - now))));
        return sb.ToString();
    }

    static string Coarse(double s)   // minutes resolution: the message is not edited every second
    {
        if (s < 60) return "<1m";
        long m = (long)(s / 60);
        return m < 60 ? $"{m}m" : $"{m / 60}h {m % 60:00}m";
    }

    // ------------------------------------------------------------ incoming
    async Task Loop(CancellationToken ct)
    {
        long offset = 0;
        bool first = true;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (BotName == null)
                {
                    var (code, me) = await Call("getMe", null, 15);
                    if (code is 401 or 404) { Status = "invalid token"; Log.W("telegram: token rejected"); return; }
                    BotName = J.Str(me?["result"]?["username"]);
                    if (first)   // skip messages that arrived while TaskHub was not running
                    {
                        var (_, old) = await Call("getUpdates", new JsonObject { ["offset"] = -1, ["timeout"] = 0 }, 15);
                        foreach (var u in (old?["result"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            offset = Math.Max(offset, (long)(J.Num(u["update_id"]) ?? 0) + 1);
                        first = false;
                    }
                }
                Status = Linked ? "connected" : "waiting for /start";
                var (c2, r) = await Call("getUpdates", new JsonObject
                {
                    ["offset"] = offset, ["timeout"] = 50, ["allowed_updates"] = new JsonArray("message", "callback_query"),
                }, 70, ct);
                if (c2 is 401 or 404) { Status = "invalid token"; return; }
                if (c2 == 409) { Status = "another program uses this bot (webhook / getUpdates)"; await Task.Delay(30000, ct); continue; }
                failures = 0;
                foreach (var u in (r?["result"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    offset = Math.Max(offset, (long)(J.Num(u["update_id"]) ?? 0) + 1);
                    try { Handle(u); } catch (Exception e) { Log.W("telegram update: " + e.Message); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Status = "offline: " + e.GetBaseException().Message;
                // a dropped connection is usually gone at once: retry soon, back off only if it keeps failing
                var wait = TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(failures++, 4)));
                try { await Task.Delay(wait, ct); } catch { return; }
            }
        }
    }

    void Handle(JsonObject u)
    {
        if (u["callback_query"] is JsonObject cq)
        {
            long chat = (long)(J.Num(cq["message"]?["chat"]?["id"]) ?? 0);
            string answer;
            if (chat != Chat || chat == 0) answer = "not linked";
            else
            {
                Cb cb;
                bool found;
                lock (callbacks) found = callbacks.TryGetValue(J.Str(cq["data"]), out cb);
                if (!found) answer = L.T("tg_expired");
                else
                {
                    try
                    {
                        if (cb.Ask) { Ask(chat, cb.Tid, cb.Cid); answer = "✏️"; }
                        else if (!cb.Confirmed && Confirm(chat, cb.Tid, cb.Cid, cb.Value)) answer = "❓";
                        else
                        {
                            hub.Trigger(cb.Tid, cb.Cid, new JsonObject { ["value"] = cb.Value?.DeepClone() }, "telegram");
                            answer = "✓ " + cb.Cid + (cb.Value != null ? " = " + J.Str(cb.Value) : "");
                        }
                    }
                    catch (Exception e) { answer = e.Message; }
                }
            }
            _ = Call("answerCallbackQuery", new JsonObject { ["callback_query_id"] = cq["id"]?.DeepClone(), ["text"] = J.Cut(answer, 180) }, 15);
            return;
        }
        if (u["message"] is not JsonObject msg) return;
        long from = (long)(J.Num(msg["chat"]?["id"]) ?? 0);
        var text = J.Str(msg["text"]).Trim();
        var cmd = text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
        var arg = text.Contains(' ') ? text.Split(' ', 2)[1].Trim() : "";

        if (cmd == "/start" && PairCode != null && arg == PairCode)
        {
            if (J.Str(msg["chat"]?["type"]) != "private")
            {
                Enqueue(from, "⚠️ " + Esc(L.T("tg_private_only")), null);
                return;
            }
            S.Set(("telegram_chat", from));
            PairCode = null;
            Status = "connected";
            Log.W("telegram: linked");
            Enqueue(from, "✅ " + Esc(L.T("tg_linked", Environment.MachineName)), null);
            return;
        }
        if (from != Chat || from == 0) return;   // only the linked chat may talk to TaskHub
        if (text.Length > 0 && !text.StartsWith('/'))   // a value for a text / number control?
        {
            long replyTo = (long)(J.Num(msg["reply_to_message"]?["message_id"]) ?? 0);
            (string tid, string cid) target = default;
            bool have;
            lock (asks)
            {
                have = replyTo != 0 && asks.TryGetValue(replyTo, out target);
                if (!have && lastAsk is { } la && J.Now() - la.t < 600) { target = (la.tid, la.cid); have = true; }
            }
            if (have) { SubmitValue(from, target.tid, target.cid, text); return; }
        }
        switch (cmd)
        {
            case "/tasks":
            {
                var tasks = (hub.Snapshot(activeOnly: true, brief: true)["tasks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                var lines = tasks.Select(t => "• <b>" + Esc(J.Str(t["name"])) + "</b> — " + Esc(J.Str(t["state"]))
                    + (J.Num(t["progress"]) is double p ? $" {Math.Round(p * 100)}%" : "")
                    + (J.Str(t["status"]).Length > 0 ? "\n   " + Esc(J.Cut(J.Str(t["status"]), 120)) : ""));
                Enqueue(from, tasks.Count == 0 ? Esc(L.T("tg_no_tasks")) : string.Join("\n", lines), null);
                break;
            }
            case "/mute": hub.SetMuted(true); Enqueue(from, "🔕 " + Esc(L.T("muted")), null); break;
            case "/unmute": hub.SetMuted(false); Enqueue(from, "🔔 OK", null); break;
            default: Enqueue(from, Esc(L.T("tg_help")), null); break;
        }
    }

    static JsonObject FindControl(JsonObject task, string cid) =>
        (task["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(x => J.Str(x["id"]) == cid)
        ?? throw new ApiError(404, $"control '{cid}' not found");

    /// <summary>Text / number control pressed: ask for the value; the reply to this message goes to the task.</summary>
    void Ask(long chat, string tid, string cid)
    {
        var c = FindControl(hub.GetTask(tid), cid);
        var inp = c["input"] as JsonObject;
        string label = J.Str(c["label"]), il = J.Str(inp?["label"]);
        var what = label + (il.Length > 0 && il != label ? " (" + il + ")" : "");
        if (J.Str(inp?["type"]) == "number" && (J.Num(inp["min"]) is double || J.Num(inp["max"]) is double))
            what += $" [{(J.Num(inp["min"]) is double mn ? J.Fmt(mn) : "")} … {(J.Num(inp["max"]) is double mx ? J.Fmt(mx) : "")}]";
        var cur = J.Str(c["last_value"]).Length > 0 ? J.Str(c["last_value"]) : J.Str(inp?["default"]);
        var text = "✏️ " + Esc(L.T("tg_ask", what)) + (cur.Length > 0 ? "\n<code>" + Esc(J.Cut(cur, 200)) + "</code>" : "");
        var (code, r) = Call("sendMessage", new JsonObject
        {
            ["chat_id"] = chat, ["text"] = text, ["parse_mode"] = "HTML",
            ["reply_markup"] = new JsonObject { ["force_reply"] = true, ["input_field_placeholder"] = J.Cut(il.Length > 0 ? il : label, 60) },
        }, 20).GetAwaiter().GetResult();
        lock (asks)
        {
            if (code == 200 && J.Num(r?["result"]?["message_id"]) is double id)
            {
                if (asks.Count > 100) asks.Clear();
                asks[(long)id] = (tid, cid);
            }
            lastAsk = (tid, cid, J.Now());
        }
    }

    void SubmitValue(long chat, string tid, string cid, string raw)
    {
        try
        {
            var c = FindControl(hub.GetTask(tid), cid);
            var inp = c["input"] as JsonObject;
            JsonNode value;
            if (J.Str(inp?["type"]) == "number")
            {
                if (!double.TryParse(raw.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)
                    || (J.Num(inp["min"]) is double mn && d < mn) || (J.Num(inp["max"]) is double mx && d > mx))
                {
                    Enqueue(chat, "⚠️ " + Esc(L.T("tg_bad_value", J.Cut(raw, 100))), null);
                    return;
                }
                value = d;
            }
            else value = J.Cut(raw, 2000);
            if (Confirm(chat, tid, cid, value)) { lock (asks) lastAsk = null; return; }
            hub.Trigger(tid, cid, new JsonObject { ["value"] = value }, "telegram");
            lock (asks) lastAsk = null;
            Enqueue(chat, "✓ " + Esc(J.Str(c["label"]) + " = " + J.Cut(J.Str(value), 200)), null);
        }
        catch (Exception e) { Enqueue(chat, "⚠️ " + Esc(e.Message), null); }
    }

    /// <summary>A control with "confirm" (like the panel's dialog): sends the question with a Yes button that carries the
    /// value and triggers the control. Returns false if the control needs no confirmation.</summary>
    bool Confirm(long chat, string tid, string cid, JsonNode value)
    {
        var c = FindControl(hub.GetTask(tid), cid);
        var q = J.Str(c["confirm"]);
        if (q.Length == 0) return false;
        var what = J.Str(c["label"]) + (value != null ? " = " + J.Cut(J.Str(value), 200) : "");
        Enqueue(chat, "❓ <b>" + Esc(what) + "</b>\n" + Esc(q), new JsonObject
        {
            ["inline_keyboard"] = new JsonArray(new JsonArray(Button("✓ " + L.T("yes"), tid, cid, value?.DeepClone(), confirmed: true))),
        });
        return true;
    }

    // ------------------------------------------------------------ helpers
    async Task<(int code, JsonObject body)> Call(string method, JsonObject body, int timeoutSec, CancellationToken ct = default)
    {
        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts2.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        using var req = new HttpRequestMessage(HttpMethod.Post, Api(method))
        { Content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json") };
        using var r = await http.SendAsync(req, cts2.Token);
        var text = await r.Content.ReadAsStringAsync(cts2.Token);
        JsonObject o = null;
        try { o = J.Parse(text) as JsonObject; } catch { }
        return ((int)r.StatusCode, o);
    }

    async Task<(int code, JsonObject body)> CallPhoto(JsonObject m, byte[] png)
    {
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(((long)(J.Num(m["chat_id"]) ?? 0)).ToString()), "chat_id");
        form.Add(new StringContent(J.Str(m["caption"])), "caption");
        form.Add(new StringContent("true"), "disable_notification");
        var img = new ByteArrayContent(png);
        img.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(img, "photo", "chart.png");
        using var r = await http.PostAsync(Api("sendPhoto"), form, cts2.Token);
        var text = await r.Content.ReadAsStringAsync(cts2.Token);
        JsonObject o = null;
        try { o = J.Parse(text) as JsonObject; } catch { }
        return ((int)r.StatusCode, o);
    }

    static string Esc(string s) => WebUtility.HtmlEncode(s ?? "");

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    static double IdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref li) ? (Environment.TickCount - (int)li.dwTime) / 1000.0 : 0;
    }
}
