using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Discord bridge: every notification TaskHub shows is also sent as a direct message from the user's own bot, with the
/// task's notification buttons (presses come back as control triggers). Slash commands: /tasks /mute /unmute.
/// Setup: the user creates an application, pastes the bot's token, adds the bot to a server of their own (a bot can't
/// write to someone it shares no server with) and presses the button in the bot's first message. Only the application's
/// owner can be linked, and only that person's presses in that chat count. The token lives only in the Credential Manager.
/// Config: discord_user, discord_channel, discord_app, discord_away_only (+ discord_away_minutes), discord_progress,
/// discord_api and discord_identify_gap (tests, loopback only).
/// Discord.Rest.cs: calls, pacing, the outbox. Discord.Gateway.cs: the connection that brings the presses.
/// </summary>
sealed partial class DiscordBridge
{
    const string Secret = "discord";
    readonly Settings S;
    readonly Hub hub;
    volatile string token = "";
    volatile bool vaultFailed, endpointSet;
    volatile string botName;
    CancellationTokenSource cts;

    // ids are 64-bit strings: never through a number
    string User => J.Str(S.Get("discord_user"));
    string Channel => J.Str(S.Get("discord_channel"));
    string App => J.Str(S.Get("discord_app"));
    public bool Linked => token.Length > 0 && Channel.Length > 0;
    public bool AwayOnly => S.Bool("discord_away_only", false);
    public bool ShowProgress => S.Bool("discord_progress", true);
    public void SetAwayOnly(bool on) => S.Set(("discord_away_only", on));
    public void SetProgress(bool on) => S.Set(("discord_progress", on));

    /// <summary>off | connecting | connected | invalid token | offline | on hold | closed N: English, also in the API.</summary>
    public volatile string Status = "off";
    /// <summary>Until linked: "server" (no way to write yet: the bot needs a shared server) or "confirm" (the button was sent).</summary>
    volatile string phase;
    volatile string pairCode, pendingChannel;

    public DiscordBridge(Settings s, Hub h)
    {
        S = s; hub = h;
        http = new SafeHttp(() => new[] { ApiBase.Host }, 30, UserAgent);
        try { token = Secrets.Read(S, Secret) ?? ""; }
        catch (Exception e) { vaultFailed = true; Log.W("discord", "token not read: " + e.Message); }
        new Thread(SendWorker) { IsBackground = true, Name = "discord-send" }.Start();
        new Thread(ProgressWorker) { IsBackground = true, Name = "discord-progress" }.Start();
    }

    // ------------------------------------------------------------ lifecycle
    public void Start()
    {
        cts?.Cancel();
        cts = null;
        ResetSession();
        tokenBad = false;
        channelBlocked = false;
        if (token.Length == 0) { Status = "off"; botName = null; return; }
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        Status = "connecting";
        _ = Task.Run(() => GatewayLoop(ct));
    }

    /// <summary>Checks the token with Discord, keeps it in the Credential Manager and starts linking. Nothing is kept unless
    /// Discord accepted it and the Credential Manager really holds it; there is no fallback to config.json.</summary>
    public JsonObject Pair(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) throw new ApiError(400, "no token") { Ui = L.T("dc_rejected") };
        var (app, owner, name) = ReadApplication(value);
        try { Secrets.Write(S, Secret, value); vaultFailed = false; }
        catch (Exception e)
        {
            vaultFailed = true;
            Log.W("discord", "token not saved: " + e.Message);
            throw new ApiError(400, "credential manager unavailable") { Ui = L.T("dc_vault") };
        }
        token = value;
        botName = name;
        lastIdentify = 0;   // the limit on new sessions is per token
        S.Set(("discord_app", app), ("discord_user", owner), ("discord_channel", null));
        Start();
        SendPairing();
        return State();
    }

    /// <summary>The application's id and owner, and the bot's name. A token Discord refuses: an error for the page.</summary>
    (string app, string owner, string name) ReadApplication(string t)
    {
        Resp Ask(string path)
        {
            try { return CallSync(HttpMethod.Get, path, null, t); }
            catch (Exception e) when (e is not ApiError) { throw NoAnswer(e); }
        }
        var me = Ask("/users/@me");
        if (me.Code == 401) throw new ApiError(400, "Discord rejected the token") { Ui = L.T("dc_rejected") };
        if (me.Code != 200) throw NoAnswer(null);
        var a = Ask("/applications/@me");
        if (a.Code != 200) throw NoAnswer(null);
        endpointSet = J.Str(a.Json?["interactions_endpoint_url"]).Length > 0;
        var owner = J.Str(a.Json?["team"]?["owner_user_id"]) is { Length: > 0 } teamOwner ? teamOwner : J.Str(a.Json?["owner"]?["id"]);
        var id = J.Str(a.Json?["id"]);
        if (owner.Length == 0 || id.Length == 0) throw new ApiError(400, "no application owner") { Ui = L.T("dc_rejected") };
        return (id, owner, J.Str(me.Json?["username"]));
    }

    static ApiError NoAnswer(Exception e)
    {
        if (e != null) Log.W("discord", "no answer: " + e.GetType().Name);
        return new ApiError(400, "Discord did not answer") { Ui = L.T("dc_no_answer") };
    }

    /// <summary>Opens the direct message and sends the button that links it. What can't be sent yet leaves the page's advice.</summary>
    void SendPairing()
    {
        channelBlocked = false;
        try
        {
            var dm = CallSync(HttpMethod.Post, "/users/@me/channels", new JsonObject { ["recipient_id"] = User });
            var channel = J.Str(dm.Json?["id"]);
            if (dm.Code is < 200 or >= 300 || channel.Length == 0) { Pairing(dm); return; }
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var r = CallSync(HttpMethod.Post, $"/channels/{channel}/messages", Message(L.T("dc_confirm_msg", Environment.MachineName), new JsonArray(new JsonObject
            {
                ["type"] = 1,
                ["components"] = new JsonArray(new JsonObject { ["type"] = 2, ["style"] = 1, ["label"] = J.Cut(L.T("dc_confirm_btn"), 80), ["custom_id"] = "pair:" + code }),
            }), 0));
            if (r.Code is < 200 or >= 300) { Pairing(r); return; }
            pendingChannel = channel;
            pairCode = code;
            phase = "confirm";
            Log.I("discord", "waiting for the button in the first message");
        }
        catch (Exception e) when (e is not ApiError)
        {
            phase = "retry";
            pendingChannel = pairCode = null;
            Log.W("discord", "first message not sent: " + e.GetType().Name);
        }
    }

    void Pairing(Resp r)
    {
        var code = J.Str(r.Json?["code"]);
        pendingChannel = pairCode = null;
        Log.W("discord", $"first message not sent: HTTP {r.Code} code {code}");
        if (r.Code == 401) throw new ApiError(400, "Discord rejected the token") { Ui = L.T("dc_token_bad") };
        // 50278: no shared server; 50007 / 403: direct messages from it are off. Anything else isn't about a server.
        phase = r.Code == 403 || code is "50007" or "50278" ? "server" : "retry";
    }

    /// <summary>"Check again" on the page: after adding the bot to a server, or to get the button once more.</summary>
    public JsonObject Retry()
    {
        if (token.Length == 0) throw new ApiError(400, "no token") { Ui = L.T("dc_rejected") };
        if (Status == "invalid token") Start();
        ReadEndpointSetting(default).GetAwaiter().GetResult();
        S.Set(("discord_channel", null));
        SendPairing();
        return State();
    }

    public void Disconnect()
    {
        if (!Secrets.Delete(S, Secret)) throw new ApiError(400, "token not deleted") { Ui = L.T("dc_vault") };
        token = "";
        vaultFailed = endpointSet = false;
        botName = phase = pairCode = pendingChannel = null;
        S.Set(("discord_app", null), ("discord_user", null), ("discord_channel", null), ("discord_cmds", null));
        Start();
    }

    public JsonObject State() => new()
    {
        ["configured"] = token.Length > 0, ["linked"] = Linked, ["bot"] = botName, ["status"] = Status,
        // again: a button sent before a restart is gone with its code, so the page asks to send a new one
        ["phase"] = Linked || token.Length == 0 ? null : phase ?? "again", ["away_only"] = AwayOnly, ["progress"] = ShowProgress,
        ["invite"] = token.Length > 0 && !Linked && App.Length > 0
            ? $"https://discord.com/oauth2/authorize?client_id={App}&scope=bot&permissions=0&integration_type=0" : null,
        ["endpoint_set"] = endpointSet, ["cannot_write"] = channelBlocked,
        ["warning"] = vaultFailed ? L.T("dc_vault") : Status == "invalid token" ? L.T("dc_token_bad")
            : endpointSet ? L.T("dc_endpoint") : channelBlocked ? L.T("dc_st_blocked") : null,
    };

    /// <summary>Sends at once and throws what went wrong, for the page: "sent" means Discord took it.</summary>
    public void Test()
    {
        if (Status == "invalid token") throw new ApiError(400, "Discord rejected the token") { Ui = L.T("dc_token_bad") };
        if (Blocked) throw NoAnswer(null);
        ReadEndpointSetting(default).GetAwaiter().GetResult();
        channelBlocked = false;
        Resp r;
        try
        {
            Pace();
            r = CallSync(HttpMethod.Post, $"/channels/{Channel}/messages", Message("✅ **TaskHub**\n" + Esc(L.T("tg_test_msg", Environment.MachineName)), null, 0));
        }
        catch (Exception e) when (e is not ApiError) { throw NoAnswer(e); }
        if (r.Code is >= 200 and < 300) return;
        NoteFailure("message", r);
        if (r.Code == 401) throw new ApiError(400, "Discord rejected the token") { Ui = L.T("dc_token_bad") };
        if (r.Code == 403) throw new ApiError(400, "Discord refused the message") { Ui = L.T("dc_st_blocked") };
        throw NoAnswer(null);
    }

    // ------------------------------------------------------------ outgoing
    /// <summary>Called for every notification TaskHub shows (already filtered by mute).</summary>
    public void Notify(string title, string msg, string level, string tid, JsonObject task, bool controls)
    {
        if (!Linked) return;
        if (AwayOnly && TelegramBridge.IdleSeconds() < 60 * S.Int("discord_away_minutes", 5)) return;
        var icon = level == "error" ? "❌ " : level == "warn" ? "⚠️ " : "";
        if (title != null && (title.StartsWith("❌") || title.StartsWith("⚠"))) icon = "";
        var text = icon + "**" + Esc(J.Cut(title, 256)) + "**" + (string.IsNullOrEmpty(msg) ? "" : "\n" + Esc(J.Cut(msg, 1500)));
        Enqueue(text, controls ? Components(tid, task) : null);
        // charts: only once, when the task has finished
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
            outbox.TryAdd((Message("📈 " + Esc(J.Cut(J.Str(task["name"]), 200)), null, Silent), png));
        }
        catch (Exception e) { Log.W("discord", "chart: " + e.Message); }
    }

    /// <summary>A message body: never a ping (a task named @everyone stays text), no link previews.</summary>
    static JsonObject Message(string text, JsonArray components, int flags)
    {
        var m = new JsonObject
        {
            ["content"] = Limit(text), ["flags"] = NoEmbeds | flags,
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
        if (components != null) m["components"] = components;
        return m;
    }

    void Enqueue(string text, JsonArray components) => outbox.TryAdd((Message(text, components, 0), null));

    /// <summary>Discord allows 2000 characters in a message.</summary>
    static string Limit(string s)
    {
        if (s.Length <= MaxContent) return s;
        s = J.Cut(s, MaxContent);
        // a cut escape would hide nothing, but a lone \ at the end would show
        return s.EndsWith('\\') && !s.EndsWith("\\\\") ? s[..^1] : s;
    }

    /// <summary>Every character Discord's markdown could take as formatting, a link or a mention.</summary>
    internal static string Esc(string s)
    {
        var sb = new StringBuilder((s ?? "").Length + 8);
        foreach (var c in s ?? "")
        {
            if (@"\*_~`|>[]()#-<:".Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ buttons
    sealed class Cb { public string Tid, Cid, Sig; public JsonNode Value; public bool Ask, Confirmed; public string[] Options; }
    readonly Dictionary<string, Cb> callbacks = new();
    readonly Dictionary<string, string> cbIndex = new();
    int cbSeq;
    // A message outlives the process: an id of an earlier run must be unknown, never another task's button.
    readonly string cbRun = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    string Key(string tid, string cid, JsonNode value, bool ask, bool confirmed, string[] options)
    {
        var sig = tid + "\n" + cid + "\n" + J.Dump(value) + "\n" + ask + "\n" + confirmed + "\n" + (options == null ? "-" : J.Dump(new JsonArray(options.Select(o => (JsonNode)o).ToArray())));
        lock (callbacks)
        {
            if (cbIndex.TryGetValue(sig, out var key) && callbacks.ContainsKey(key)) return key;
            key = "k" + cbRun + "." + (++cbSeq);
            callbacks[key] = new Cb { Tid = tid, Cid = cid, Value = value, Ask = ask, Confirmed = confirmed, Options = options, Sig = sig };
            cbIndex[sig] = key;
            if (callbacks.Remove("k" + cbRun + "." + (cbSeq - 1000), out var old)) cbIndex.Remove(old.Sig);
            return key;
        }
    }

    JsonObject Button(string label, int style, string tid, string cid, JsonNode value, bool ask = false, bool confirmed = false) => new()
    {
        ["type"] = 2, ["style"] = style, ["label"] = J.Cut(label.Length > 0 ? label : cid, 80),
        ["custom_id"] = Key(tid, cid, value, ask, confirmed, null),
    };

    /// <summary>At most 5 rows of 5 buttons, a list takes a row of its own. What doesn't fit stays in the panel.</summary>
    JsonArray Components(string tid, JsonObject task)
    {
        if (tid == null || task == null) return null;
        var state = J.Str(task["state"]);
        if (!Hub.IsActive(state)) return null;
        var rows = new JsonArray();
        JsonArray cur = null;
        bool Add(JsonObject b)
        {
            if (cur == null || cur.Count >= 5)
            {
                if (rows.Count >= 5) return false;
                cur = new JsonArray();
                rows.Add(new JsonObject { ["type"] = 1, ["components"] = cur });
            }
            cur.Add(b);
            return true;
        }
        foreach (var c in (task["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (!J.Truthy(c["toast"])) continue;
            if (!(c["states"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).Contains(state)) continue;
            var cid = J.Str(c["id"]);
            var label = J.Str(c["label"]);
            int style = J.Str(c["style"]) switch { "primary" => 1, "danger" => 4, _ => 2 };
            if (c["input"] is JsonObject inp)
            {
                var typ = J.Str(inp["type"]);
                if (typ == "select")
                {
                    var opts = (inp["options"] as JsonArray ?? new JsonArray()).Select(o => J.Str(o)).Where(o => o.Length > 0).Take(25).ToArray();
                    if (opts.Length == 0 || rows.Count >= 5) continue;
                    cur = null;
                    rows.Add(new JsonObject
                    {
                        ["type"] = 1,
                        ["components"] = new JsonArray(new JsonObject
                        {
                            ["type"] = 3, ["custom_id"] = Key(tid, cid, null, false, false, opts), ["placeholder"] = J.Cut(label, 150),
                            ["options"] = new JsonArray(opts.Select((o, i) => (JsonNode)new JsonObject { ["label"] = J.Cut(o, 100), ["value"] = "o" + i }).ToArray()),
                        }),
                    });
                }
                else if (typ == "bool")
                {
                    if (Add(Button(J.Cut(label, 76) + " ✓", style, tid, cid, true))) Add(Button(J.Cut(label, 76) + " ✗", 2, tid, cid, false));
                }
                else Add(Button("✏️ " + J.Cut(label, 75) + "…", style, tid, cid, null, ask: true));
            }
            else Add(Button(label, style, tid, cid, null));
        }
        return rows.Count == 0 ? null : rows;
    }

    // ------------------------------------------------------------ live progress: one silent message per task, edited in place
    sealed class Live { public string Msg, Text, Channel; public double Last; }
    readonly Dictionary<string, Live> live = new();   // progress thread only
    readonly HashSet<string> chartsSent = new();

    void ProgressWorker()
    {
        while (true)
        {
            Thread.Sleep(2000);
            try { ProgressTick(); } catch (Exception e) { Log.W("discord", "progress: " + e.GetType().Name); }
        }
    }

    void ProgressTick()
    {
        if (!Linked || !ShowProgress || tokenBad || channelBlocked) { live.Clear(); return; }
        var now = J.Now();
        var channel = Channel;
        var tasks = hub.Brief().GroupBy(t => J.Str(t["id"])).ToDictionary(g => g.Key, g => g.First());
        foreach (var (tid, p) in live.ToList())
        {
            if (p.Channel != channel) { live.Remove(tid); continue; }
            tasks.TryGetValue(tid, out var t);
            if (t != null && Hub.IsActive(J.Str(t["state"]))) continue;
            if (t != null && p.Msg != null)
            {
                if (outbox.Count > 0 || Blocked) return;   // notifications first; the next tick finishes it
                if (!Edit(p, ProgressText(t, now), null, now)) return;
            }
            live.Remove(tid);
        }
        bool atPc = AwayOnly && TelegramBridge.IdleSeconds() < 60 * S.Int("discord_away_minutes", 5);
        foreach (var (tid, t) in tasks)
        {
            if (!Hub.IsActive(J.Str(t["state"])) || J.Num(t["progress"]) == null) continue;
            var text = ProgressText(t, now);
            var comps = Components(tid, t);
            if (live.TryGetValue(tid, out var p))
            {
                if (p.Msg != null && text + J.Dump(comps) != p.Text && now - p.Last >= 5)
                {
                    if (outbox.Count > 0 || Blocked) return;
                    if (!Edit(p, text, comps, now)) return;
                }
                continue;
            }
            if (hub.Muted || atPc || live.Count >= 5) continue;
            if (outbox.Count > 0 || Blocked) return;
            Pace();
            var r = CallSync(HttpMethod.Post, $"/channels/{channel}/messages", Message(text, comps, Silent));
            if (r.Code == 429) return;
            if (r.Code is >= 200 and < 300 && J.Str(r.Json?["id"]) is { Length: > 0 } id)
                live[tid] = new Live { Channel = channel, Msg = id, Text = text + J.Dump(comps), Last = now };
            else NoteFailure("progress", r);
        }
    }

    /// <summary>False on 429: try again later.</summary>
    bool Edit(Live p, string text, JsonArray comps, double now)
    {
        var body = Message(text, comps ?? new JsonArray(), Silent);   // an empty list removes the buttons
        Pace();
        var r = CallSync(HttpMethod.Patch, $"/channels/{p.Channel}/messages/{p.Msg}", body);
        p.Last = now;
        if (r.Code == 429) return false;
        if (r.Code is >= 200 and < 300) p.Text = text + J.Dump(comps);
        else
        {
            NoteFailure("edit", r);
            if (r.Code is 400 or 403 or 404) p.Msg = null;   // e.g. the user deleted the message: stop updating it
        }
        return true;
    }

    static string ProgressText(JsonObject t, double now)
    {
        var st = J.Str(t["state"]);
        var icon = st switch { "running" => "⏳", "paused" => "⏸", "stale" => "⚠️", "done" => "✅", "cancelled" => "⏹", _ => "❌" };
        double p = st == "done" ? 1 : Math.Clamp(J.Num(t["progress"]) ?? 0, 0, 1);
        int n = (int)Math.Round(p * 12);
        var sb = new StringBuilder();
        sb.Append(icon).Append(" **").Append(Esc(J.Cut(J.Str(t["name"]), 120))).Append("**\n");
        sb.Append('`').Append(new string('█', n)).Append(new string('░', 12 - n)).Append("` ").Append((int)Math.Floor(p * 100)).Append('%');
        if (J.Num(t["current"]) is double cur && J.Num(t["total"]) is double tot)
        {
            sb.Append("  ·  ").Append(J.Fmt(st == "done" ? tot : cur)).Append('/').Append(J.Fmt(tot));
            if (J.Str(t["unit"]) is { Length: > 0 } u) sb.Append(' ').Append(Esc(u));
        }
        var status = J.Str(t["status"]).Length > 0 ? J.Str(t["status"]) : J.Str(t["stage"]);
        if (status.Length > 0) sb.Append('\n').Append(Esc(J.Cut(status, 150)));
        double start = J.Num(t["started"]) ?? now;
        double end = Hub.IsFinal(st) ? J.Num(t["finished"]) ?? now : now;
        sb.Append("\n⏱ ").Append(Coarse(end - start));
        if (st == "running" && p < 1 && J.Num(t["eta_at"]) is double eta) sb.Append("  ·  ETA ").Append(Coarse(Math.Max(0, eta - now)));
        return sb.ToString();
    }

    static string Coarse(double s)
    {
        if (s < 60) return "\\<1m";
        long m = (long)(s / 60);
        return m < 60 ? $"{m}m" : $"{m / 60}h {m % 60:00}m";
    }

    // ------------------------------------------------------------ incoming: one interaction, off the gateway's thread
    readonly HashSet<string> seen = new();   // Discord may deliver an event more than once
    const int Ephemeral = 64;

    async Task Interaction(JsonObject d)
    {
        var id = J.Str(d["id"]);
        lock (seen)
        {
            if (seen.Count > 500) seen.Clear();
            if (id.Length == 0 || !seen.Add(id)) return;
        }
        var itoken = J.Str(d["token"]);
        var type = (int)(J.Num(d["type"]) ?? 0);
        var presser = J.Str(d["member"]?["user"]?["id"]) is { Length: > 0 } m ? m : J.Str(d["user"]?["id"]);
        var chan = J.Str(d["channel_id"]);
        var customId = J.Str(d["data"]?["custom_id"]);

        if (type == 3 && customId.StartsWith("pair:"))
        {
            await Pressed(id, itoken, presser, chan, customId[5..]);
            return;
        }
        if (presser.Length == 0 || presser != User || chan != Channel || !Linked)
        {
            await Reply(id, itoken, L.T("dc_not_linked"));
            return;
        }
        if (type == 2) await Command(id, itoken, J.Str(d["data"]?["name"]));
        else if (type == 3)
        {
            Cb cb;
            lock (callbacks) if (!callbacks.TryGetValue(customId, out cb)) cb = null;
            if (cb == null) await Reply(id, itoken, L.T("tg_expired"));
            else await Component(id, itoken, cb, d["data"] as JsonObject);
        }
        else if (type == 5)
        {
            Cb cb = null;
            if (customId.StartsWith("m")) lock (callbacks) callbacks.TryGetValue(customId[1..], out cb);
            if (cb == null) await Reply(id, itoken, L.T("tg_expired"));
            else await ModalValue(id, itoken, cb, d["data"] as JsonObject);
        }
    }

    async Task Pressed(string id, string itoken, string presser, string chan, string code)
    {
        if (presser != User || presser.Length == 0) { await Reply(id, itoken, L.T("dc_not_linked")); return; }
        if (code != pairCode || chan != pendingChannel) { await Reply(id, itoken, L.T("tg_expired")); return; }
        // the answer first: a press that can't be answered any more (an old one, delivered late) links nothing
        var ok = await Callback(id, itoken, new JsonObject
        {
            ["type"] = 7,
            ["data"] = new JsonObject
            {
                ["content"] = Limit("✅ " + Esc(L.T("dc_linked", Environment.MachineName))), ["components"] = new JsonArray(),
                ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            },
        });
        if (!ok) return;
        S.Set(("discord_channel", chan));
        pairCode = pendingChannel = phase = null;
        channelBlocked = false;
        Log.I("discord", "linked");
        await RegisterCommands();
    }

    /// <summary>The slash commands, once for an application. Discord limits how often new ones can be created.</summary>
    async Task RegisterCommands()
    {
        var app = App;
        if (app.Length == 0 || J.Str(S.Get("discord_cmds")) == app) return;
        JsonObject Cmd(string name, string key) => new()
        {
            ["name"] = name, ["type"] = 1, ["description"] = J.Cut(L.T(key), 100),
            ["contexts"] = new JsonArray(1), ["integration_types"] = new JsonArray(0),
        };
        var r = await Call(HttpMethod.Put, $"/applications/{app}/commands",
            new JsonArray(Cmd("tasks", "dc_cmd_tasks"), Cmd("mute", "dc_cmd_mute"), Cmd("unmute", "dc_cmd_unmute")), token);
        if (r.Code is >= 200 and < 300) S.Set(("discord_cmds", app));
        else NoteFailure("commands", r);
    }

    async Task Command(string id, string itoken, string name)
    {
        switch (name)
        {
            case "tasks":
            {
                var tasks = (hub.Snapshot(activeOnly: true, brief: true)["tasks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                const int Rows = 15;   // 15 x (60 + 60 + a few) characters, doubled by escaping at worst, stays under 2000 in most cases; Limit cuts the rest
                var lines = tasks.Take(Rows).Select(t => "• **" + Esc(J.Cut(J.Str(t["name"]), 60)) + "**: " + Esc(J.Str(t["state"]))
                    + (J.Num(t["progress"]) is double p ? $" {Math.Round(p * 100)}%" : "")
                    + (J.Str(t["status"]).Length > 0 ? " · " + Esc(J.Cut(J.Str(t["status"]), 60)) : "")).ToList();
                if (tasks.Count > Rows) lines.Add($"… +{tasks.Count - Rows}");
                await Reply(id, itoken, tasks.Count == 0 ? Esc(L.T("tg_no_tasks")) : string.Join("\n", lines), raw: true);
                break;
            }
            case "mute":
                if (await Reply(id, itoken, "🔕 " + L.T("muted"))) hub.SetMuted(true);
                break;
            case "unmute":
                if (await Reply(id, itoken, "🔔 OK")) hub.SetMuted(false);
                break;
            default:
                await Reply(id, itoken, L.T("tg_expired"));
                break;
        }
    }

    async Task Component(string id, string itoken, Cb cb, JsonObject data)
    {
        try
        {
            if (cb.Ask) { await AskValue(id, itoken, cb); return; }
            var value = cb.Value?.DeepClone();
            if (cb.Options != null)
            {
                var pick = J.Str((data?["values"] as JsonArray)?.FirstOrDefault());
                if (!pick.StartsWith('o') || !int.TryParse(pick[1..], out var i) || i < 0 || i >= cb.Options.Length)
                {
                    await Reply(id, itoken, L.T("tg_expired"));
                    return;
                }
                value = cb.Options[i];
            }
            if (!cb.Confirmed && await AskConfirm(id, itoken, cb, value)) return;
            await Act(id, itoken, cb, value);
        }
        catch (ApiError e) when (e.Stale) { await Reply(id, itoken, L.T("g_handled")); }
        catch (ApiError e) { await Reply(id, itoken, e.Message); }
    }

    /// <summary>The answer in Discord's 3 seconds, then the action, then its result. No answer, no action.</summary>
    async Task Act(string id, string itoken, Cb cb, JsonNode value)
    {
        if (!await Callback(id, itoken, new JsonObject { ["type"] = 5, ["data"] = new JsonObject { ["flags"] = Ephemeral } })) return;
        string answer;
        try
        {
            var res = await Task.Run(() => hub.Trigger(cb.Tid, cb.Cid, new JsonObject { ["value"] = value?.DeepClone() }, "discord"));
            answer = J.Str(res?["message"]) is { Length: > 0 } said ? said : "✓ " + cb.Cid + (value != null ? " = " + J.Cut(J.Str(value), 200) : "");
        }
        catch (ApiError e) when (e.Stale) { answer = L.T("g_handled"); }
        catch (Exception e) { answer = e.Message; }
        var app = App;
        var r = await Call(HttpMethod.Patch, $"/webhooks/{app}/{itoken}/messages/@original", new JsonObject
        {
            ["content"] = Limit(Esc(answer)), ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        }, null);
        if (r.Code is < 200 or >= 300) NoteFailure("answer", r);
    }

    static JsonObject FindControl(JsonObject task, string cid) =>
        (task["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(x => J.Str(x["id"]) == cid)
        ?? throw new ApiError(404, $"control '{cid}' not found") { Stale = true };

    /// <summary>A control with "confirm": the question and a Yes button that carries the value. False: none needed.</summary>
    async Task<bool> AskConfirm(string id, string itoken, Cb cb, JsonNode value)
    {
        var c = FindControl(hub.GetTask(cb.Tid), cb.Cid);
        var q = J.Str(c["confirm"]);
        if (q.Length == 0) return false;
        var what = J.Cut(J.Str(c["label"]), 200) + (value != null ? " = " + J.Cut(J.Str(value), 200) : "");
        await Callback(id, itoken, new JsonObject
        {
            ["type"] = 4,
            ["data"] = new JsonObject
            {
                ["flags"] = Ephemeral, ["content"] = Limit("❓ **" + Esc(what) + "**\n" + Esc(J.Cut(q, 1200))),
                ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
                ["components"] = new JsonArray(new JsonObject
                {
                    ["type"] = 1,
                    ["components"] = new JsonArray(Button("✓ " + L.T("yes"), 1, cb.Tid, cb.Cid, value?.DeepClone(), confirmed: true)),
                }),
            },
        });
        return true;
    }

    /// <summary>A text / number control: Discord's own input window. It has to be the first answer to the press.</summary>
    async Task AskValue(string id, string itoken, Cb cb)
    {
        var c = FindControl(hub.GetTask(cb.Tid), cb.Cid);
        var inp = c["input"] as JsonObject;
        string label = J.Str(c["label"]), il = J.Str(inp?["label"]);
        var cur = J.Str(c["last_value"]).Length > 0 ? J.Str(c["last_value"]) : J.Str(inp?["default"]);
        var field = new JsonObject { ["type"] = 4, ["custom_id"] = "v", ["style"] = 1, ["required"] = true, ["max_length"] = 2000 };
        if (cur.Length > 0) field["value"] = J.Cut(cur, 2000);
        if (J.Str(inp?["type"]) == "number" && (J.Num(inp["min"]) is double || J.Num(inp["max"]) is double))
            field["placeholder"] = J.Cut($"{(J.Num(inp["min"]) is double mn ? J.Fmt(mn) : "")} … {(J.Num(inp["max"]) is double mx ? J.Fmt(mx) : "")}", 100);
        var key = Key(cb.Tid, cb.Cid, null, true, false, null);
        await Callback(id, itoken, new JsonObject
        {
            ["type"] = 9,
            ["data"] = new JsonObject
            {
                ["custom_id"] = "m" + key, ["title"] = J.Cut(label.Length > 0 ? label : cb.Cid, 45),
                ["components"] = new JsonArray(new JsonObject { ["type"] = 18, ["label"] = J.Cut(il.Length > 0 ? il : label.Length > 0 ? label : cb.Cid, 45), ["component"] = field }),
            },
        });
    }

    /// <summary>The value from the input window, checked like Telegram's replies; the hub checks it once more.</summary>
    async Task ModalValue(string id, string itoken, Cb cb, JsonObject data)
    {
        var raw = "";
        foreach (var row in (data?["components"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if (J.Str(row["component"]?["custom_id"]) == "v") raw = J.Str(row["component"]?["value"]);
        try
        {
            var c = FindControl(hub.GetTask(cb.Tid), cb.Cid);
            var inp = c["input"] as JsonObject;
            JsonNode value;
            if (J.Str(inp?["type"]) == "number")
            {
                if (!double.TryParse(raw.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)
                    || (J.Num(inp["min"]) is double mn && v < mn) || (J.Num(inp["max"]) is double mx && v > mx))
                {
                    await Reply(id, itoken, L.T("tg_bad_value", J.Cut(raw, 100)));
                    return;
                }
                value = v;
            }
            else value = J.Cut(raw, 2000);
            if (!cb.Confirmed && await AskConfirm(id, itoken, cb, value)) return;
            await Act(id, itoken, cb, value);
        }
        catch (ApiError e) when (e.Stale) { await Reply(id, itoken, L.T("g_handled")); }
        catch (ApiError e) { await Reply(id, itoken, e.Message); }
    }

    /// <summary>A short answer only the presser sees.</summary>
    Task<bool> Reply(string id, string itoken, string text, bool raw = false) => Callback(id, itoken, new JsonObject
    {
        ["type"] = 4,
        ["data"] = new JsonObject { ["flags"] = Ephemeral, ["content"] = Limit(raw ? text : Esc(text)), ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() } },
    });

    /// <summary>True when Discord took the answer. These routes carry the interaction's own token, in the address: never logged.</summary>
    async Task<bool> Callback(string id, string itoken, JsonObject body)
    {
        try
        {
            var r = await Call(HttpMethod.Post, $"/interactions/{id}/{itoken}/callback", body, null);
            if (r.Code is >= 200 and < 300) return true;
            NoteFailure("press", r);
        }
        catch (Exception e) { Log.W("discord", "press not answered: " + e.GetType().Name); }
        return false;
    }
}
