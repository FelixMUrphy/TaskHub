using System.Collections.Concurrent;
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
    readonly BlockingCollection<JsonObject> outbox = new(500);
    readonly Dictionary<string, (string tid, string cid, JsonNode value)> callbacks = new();
    int cbSeq;
    CancellationTokenSource cts;
    public string BotName { get; private set; }
    public string PairCode { get; private set; }
    public string Status { get; private set; } = "off";

    public TelegramBridge(Settings s, Hub h)
    {
        S = s; hub = h;
        new Thread(SendWorker) { IsBackground = true, Name = "telegram-send" }.Start();
    }

    string Token => J.Str(S.Cfg["telegram_token"]);
    long Chat => J.Num(S.Cfg["telegram_chat"]) is double d ? (long)d : 0;
    string Api(string method) => (J.Str(S.Cfg["telegram_api"]) is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.telegram.org")
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
        S.Cfg["telegram_token"] = token;
        S.Cfg["telegram_chat"] = 0;
        S.SaveCfg();
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
        S.Cfg.Remove("telegram_token");
        S.Cfg.Remove("telegram_chat");
        S.SaveCfg();
        PairCode = null;
        Start();
    }

    public void SetAwayOnly(bool on) { S.Cfg["telegram_away_only"] = on; S.SaveCfg(); }

    public JsonObject State() => new()
    {
        ["configured"] = Token.Length > 0, ["linked"] = Linked, ["bot"] = BotName, ["status"] = Status,
        ["away_only"] = AwayOnly,
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
        var text = icon + "<b>" + Esc(title) + "</b>" + (string.IsNullOrEmpty(msg) ? "" : "\n" + Esc(msg));
        Enqueue(Chat, text, controls ? Keyboard(tid, task) : null);
    }

    JsonObject Keyboard(string tid, JsonObject task)
    {
        if (tid == null || task == null) return null;
        var state = J.Str(task["state"]);
        if (!Hub.IsActive(state)) return null;
        var rows = new JsonArray();
        foreach (var c in (task["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (!J.Truthy(c["toast"]) || rows.Count >= 4) continue;
            if (!(c["states"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).Contains(state)) continue;
            var cid = J.Str(c["id"]);
            var label = J.Cut(J.Str(c["label"]), 30);
            var row = new JsonArray();
            if (c["input"] is JsonObject inp)
            {
                var typ = J.Str(inp["type"]);
                if (typ == "select")
                    foreach (var o in (inp["options"] as JsonArray ?? new JsonArray()).Take(4))
                        row.Add(Button(label + ": " + J.Str(o), tid, cid, o?.DeepClone()));
                else if (typ == "bool")
                {
                    row.Add(Button(label + ": " + L.T("yes"), tid, cid, true));
                    row.Add(Button(label + ": " + L.T("no"), tid, cid, false));
                }
                else continue;   // free text / number: only in the panel or the Windows notification
            }
            else row.Add(Button(label, tid, cid, null));
            rows.Add(row);
        }
        return rows.Count == 0 ? null : new JsonObject { ["inline_keyboard"] = rows };
    }

    JsonObject Button(string text, string tid, string cid, JsonNode value)
    {
        string key;
        lock (callbacks)
        {
            key = "k" + (++cbSeq);
            callbacks[key] = (tid, cid, value);
            if (callbacks.Count > 500) callbacks.Remove("k" + (cbSeq - 500));
        }
        return new JsonObject { ["text"] = text, ["callback_data"] = key };
    }

    void Enqueue(long chat, string text, JsonObject keyboard)
    {
        if (chat == 0) return;
        var m = new JsonObject { ["chat_id"] = chat, ["text"] = J.Cut(text, 3900), ["parse_mode"] = "HTML", ["disable_web_page_preview"] = true };
        if (keyboard != null) m["reply_markup"] = keyboard;
        outbox.TryAdd(m);
    }

    void SendWorker()
    {
        foreach (var m in outbox.GetConsumingEnumerable())
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var (code, _) = Call("sendMessage", m, 20).GetAwaiter().GetResult();
                    if (code == 429) { Thread.Sleep(3000); continue; }
                    if (code != 200) Log.W($"telegram send: HTTP {code}");
                    break;
                }
                catch (Exception e) { Log.W("telegram send: " + e.Message); Thread.Sleep(2000); }
            }
        }
    }

    // ------------------------------------------------------------ incoming
    async Task Loop(CancellationToken ct)
    {
        long offset = 0;
        bool first = true;
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
                try { await Task.Delay(15000, ct); } catch { return; }
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
                (string tid, string cid, JsonNode value) cb;
                bool found;
                lock (callbacks) found = callbacks.TryGetValue(J.Str(cq["data"]), out cb);
                if (!found) answer = L.T("tg_expired");
                else
                {
                    try
                    {
                        hub.Trigger(cb.tid, cb.cid, new JsonObject { ["value"] = cb.value?.DeepClone() }, "telegram");
                        answer = "✓ " + cb.cid + (cb.value != null ? " = " + J.Str(cb.value) : "");
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
            S.Cfg["telegram_chat"] = from;
            S.SaveCfg();
            PairCode = null;
            Status = "connected";
            Log.W("telegram: linked");
            Enqueue(from, "✅ " + Esc(L.T("tg_linked", Environment.MachineName)), null);
            return;
        }
        if (from != Chat || from == 0) return;   // only the linked chat may talk to TaskHub
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

    static string Esc(string s) => WebUtility.HtmlEncode(s ?? "");

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    static double IdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref li) ? (Environment.TickCount - (int)li.dwTime) / 1000.0 : 0;
    }
}
