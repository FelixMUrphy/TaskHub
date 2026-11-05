using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TaskHub;

/// <summary>HTTP API (Kestrel on loopback) - same routes and guards as the Python version.</summary>
sealed partial class Api
{
    readonly Settings S;
    readonly Hub hub;
    public Func<string, bool> UiOpen;    // task -> ok
    public Func<bool> UiHide;            // -> hidden
    public TelegramBridge Telegram;
    public DiscordBridge Discord;
    public Connectors Conns;
    public readonly Worktime Work;
    WebApplication app;

    delegate Task<JsonNode> Handler(HttpContext ctx, Dictionary<string, string> q, string[] g);
    readonly List<(string m, Regex rx, Handler h)> routes = new();

    public Api(Settings s, Hub h)
    {
        S = s;
        hub = h;
        Work = new Worktime(s, h.Poke) { Toast = (t, m) => h.Notify(new JsonObject { ["title"] = t, ["message"] = m, ["level"] = "warn" }) };
        h.WorktimeMark = () => Work.Mark;
        Routes();
        WorktimeRoutes();
    }

    void R(string method, string pattern, Handler h) => routes.Add((method, new Regex("^" + pattern + "$"), h));
    static Task<JsonNode> V(JsonNode n) => Task.FromResult(n);
    const string Id = "([A-Za-z0-9_.-]+)";

    void Routes()
    {
        R("GET", "/", (c, q, g) => { c.Response.Redirect("/ui"); return V(null); });
        R("GET", "/ui", async (c, q, g) =>
        {
            var html = UiPage.Html();
            html = html.Replace("__VERSION__", Settings.Version)
                .Replace("__AUTOHIDE__", S.Bool("panel_autohide", true) ? "true" : "false");
            await Raw(c, 200, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
            return null;
        });
        R("GET", "/icon.png", async (c, q, g) =>
        {
            var p = S.File_("icon.png");
            if (!File.Exists(p)) throw new ApiError(404, "no icon");
            await Raw(c, 200, await File.ReadAllBytesAsync(p), "image/png");
            return null;
        });
        R("GET", "/api/health", (c, q, g) =>
        {
            var (total, active) = hub.Counts();
            return V(new JsonObject
            {
                ["ok"] = true, ["app"] = Settings.AppId, ["version"] = Settings.Version, ["impl"] = "dotnet", ["edition"] = Install.Edition,
                ["pid"] = Environment.ProcessId, ["tasks"] = total, ["active"] = active,
                ["notifications"] = hub.Notifier?.SystemSetting,   // Windows' switch for TaskHub's notifications
                ["toast_queue"] = hub.Notifier?.QueueLength ?? 0,  // notifications waiting for the rate limit
            });
        });
        R("GET", "/api/hub-proof", (c, q, g) =>
        {
            // taskhub.exe asks this before it sends anything; the port is signed too, so a proof can't be passed on from another port
            var nonce = q.TryGetValue("nonce", out var nv) ? nv : "";
            if (nonce.Length is < 16 or > 128 || !nonce.All(Uri.IsHexDigit)) throw new ApiError(400, "nonce: 16-128 hex characters");
            var mac = System.Security.Cryptography.HMACSHA256.HashData(S.HubKey, System.Text.Encoding.UTF8.GetBytes($"taskhub-hub-proof|{S.Port}|{nonce}"));
            return V(new JsonObject { ["proof"] = Convert.ToHexString(mac).ToLowerInvariant() });
        });
        R("GET", "/api/help", async (c, q, g) =>
        {
            await Raw(c, 200, Res.Read("API.md") ?? Array.Empty<byte>(), "text/markdown; charset=utf-8");
            return null;
        });
        R("GET", "/api/tasks", async (c, q, g) =>
        {
            long since = q.TryGetValue("since", out var sv) && long.TryParse(sv, out var l) ? l : 0;
            if (since > 0 && q.TryGetValue("wait", out var wv) && double.TryParse(wv, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var w))
                await hub.WaitChange(since, Math.Min(w, 55), c.RequestAborted);
            return hub.Snapshot(Flag(q, "active"), Flag(q, "brief"));
        });
        R("POST", "/api/tasks", async (c, q, g) => hub.RegisterApi(NoConnectorReference(await Body(c))));
        R("GET", $"/api/tasks/{Id}", (c, q, g) => V(hub.GetTask(g[0])));
        R("DELETE", $"/api/tasks/{Id}", (c, q, g) => V(hub.Delete(g[0], Flag(q, "force"))));
        R("POST", $"/api/tasks/{Id}/update", async (c, q, g) => hub.UpdateApi(g[0], NoConnectorReference(await Body(c))));
        R("POST", $"/api/tasks/{Id}/log", async (c, q, g) => hub.AddLogApi(g[0], await Body(c)));
        R("POST", $"/api/tasks/{Id}/heartbeat", async (c, q, g) => { await Body(c); return hub.Heartbeat(g[0]); });
        R("POST", $"/api/tasks/{Id}/finish", async (c, q, g) => hub.FinishApi(g[0], await Body(c)));
        R("POST", $"/api/tasks/{Id}/controls/([A-Za-z0-9_.:-]+)", async (c, q, g) =>
        {
            var b = await Body(c);
            return await Task.Run(() => (JsonNode)hub.Trigger(g[0], g[1], b));
        });
        R("GET", $"/api/tasks/{Id}/commands", async (c, q, g) =>
        {
            double w = q.TryGetValue("wait", out var wv) && double.TryParse(wv, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
            return await hub.PollCommands(g[0], w, c.RequestAborted);
        });
        R("POST", $"/api/tasks/{Id}/commands/([A-Za-z0-9]+)/ack", async (c, q, g) => hub.Ack(g[0], g[1], await Body(c)));
        R("POST", "/api/clear", async (c, q, g) => { await Body(c); return hub.ClearFinished(); });
        R("POST", "/api/notify", async (c, q, g) => { var b = await Body(c); Hub.CheckNotify(b); var r = hub.Notify(b); AgentHooks.AgentSpoke(b); return r; });
        R("POST", "/api/hooks/(claude|codex)", async (c, q, g) => AgentHooks.Handle(hub, g[0], await Body(c)));
        R("POST", "/api/limits/(claude)", async (c, q, g) => hub.ReportLimits(g[0], await Body(c)));
        R("GET", "/api/limits", (c, q, g) => V(hub.LimitsView()));
        R("GET", "/api/telegram", (c, q, g) =>
        {
            var st = Telegram?.State() ?? new JsonObject { ["status"] = "off" };
            if (Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is not { Length: > 0 }) st.Remove("link");   // the pairing code stays on the settings page
            return V(st);
        });
        // setting it up is only on the settings page (WebView2 messages): a pairing code gives a chat the user's notifications and buttons.
        // This is for the isolated hubs of the tests
        if (Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 })
        R("POST", "/api/telegram", async (c, q, g) =>
        {
            var b = await Body(c);
            if (Telegram == null) throw new ApiError(503, "telegram bridge not available");
            if (J.Truthy(b["disconnect"])) Telegram.Disconnect();
            if (b.ContainsKey("away_only")) Telegram.SetAwayOnly(J.Truthy(b["away_only"]));
            if (b.ContainsKey("progress")) Telegram.SetProgress(J.Truthy(b["progress"]));
            if (J.IsStr(b["token"])) return await Task.Run(() => Telegram.Pair(J.Str(b["token"])));
            if (J.Truthy(b["relink"])) return await Task.Run(() => Telegram.Relink());
            if (J.Truthy(b["test"])) Telegram.Test();
            return Telegram.State();
        });
        R("GET", "/api/discord", (c, q, g) => V(Discord?.State() ?? new JsonObject { ["status"] = "off" }));
        // a toast's button as Windows would press it, for the isolated hubs of the tests
        if (Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 })
            R("POST", "/api/test/toast", async (c, q, g) =>
            {
                var b = await Body(c);
                ((Notifier)hub.Notifier).Activate(J.Str(b["argument"]), new Dictionary<string, object>());
                return new JsonObject { ["ok"] = true };
            });
        // setting it up is only on the settings page (WebView2 messages): this is for the isolated hubs of the tests
        if (Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 })
            R("POST", "/api/discord", async (c, q, g) =>
            {
                var b = await Body(c);
                if (J.Truthy(b["disconnect"])) Discord.Disconnect();
                if (b.ContainsKey("away_only")) Discord.SetAwayOnly(J.Truthy(b["away_only"]));
                if (b.ContainsKey("progress")) Discord.SetProgress(J.Truthy(b["progress"]));
                if (J.IsStr(b["token"])) return await Task.Run(() => Discord.Pair(J.Str(b["token"])));
                if (J.Truthy(b["retry"])) return await Task.Run(() => Discord.Retry());
                if (J.Truthy(b["test"])) await Task.Run(Discord.Test);
                return Discord.State();
            });
        R("GET", "/api/connectors", (c, q, g) =>
        {
            // the rows of the settings page's Connectors section (read-only; never a key): {connectors: [...]}
            if (q.TryGetValue("view", out var view) && view == "settings")
            {
                var all = Conns?.All.ToList() ?? new List<IConnector>();
                return V(new JsonObject
                {
                    ["connectors"] = SettingsBridge.ConnectorRows(all.OfType<ISettingsConnector>()),
                    ["lang"] = L.Lang,
                });
            }
            var o = new JsonObject();
            foreach (var k in Conns?.All ?? Enumerable.Empty<IConnector>()) o[k.Id] = k.State();
            return V(o);
        });
        R("GET", "/api/connectors/([a-z0-9]{1,20})", (c, q, g) => V(Conn(g[0]).State()));
        R("GET", "/api/guard/log", (c, q, g) =>
        {
            // what the balance guard stopped, newest first: ?provider=runpod|vastai&limit=1-200 (default 50)
            var prov = q.TryGetValue("provider", out var pv) && pv.Length > 0 ? pv : null;
            int lim = q.TryGetValue("limit", out var lv) && int.TryParse(lv, out var li) ? li : 50;
            var a = new JsonArray();
            foreach (var e in GuardLog.Read(S, prov, lim)) a.Add((JsonNode)e);
            return V(new JsonObject { ["entries"] = a });
        });
        R("POST", "/api/connectors/([a-z0-9]{1,20})", async (c, q, g) =>
        {
            var b = await Body(c);
            var conn = Conn(g[0]);
            return await Task.Run(() => (JsonNode)(conn switch
            {
                GitHubConnector gh => gh.Configure(b),
                MachineConnector mc => mc.ConfigureHttp(b),
                AwsConnector aw => aw.ConfigureHttp(b),
                SshConnector sc => sc.ConfigureHttp(b),
                _ => throw new ApiError(404, "not configurable"),
            }));
        });
        R("POST", "/api/settings", async (c, q, g) =>
        {
            var b = await Body(c);
            if (b.ContainsKey("muted")) hub.SetMuted(J.Truthy(b["muted"]));
            if (b["seen"] is JsonArray seenIds) hub.MarkSeen(seenIds.Select(x => J.Str(x)));   // shown in the open panel
            else if (J.Truthy(b["seen"])) hub.MarkSeen();
            if (b.ContainsKey("language")) hub.SetLanguage(J.Str(b["language"], "auto"));
            hub.SetPanelSettings(b);
            return new JsonObject { ["ok"] = true, ["muted"] = hub.Muted, ["lang"] = L.Lang, ["language"] = L.Setting, ["panel"] = hub.PanelSettings() };
        });
        R("POST", "/api/ui/hide", async (c, q, g) =>
        {
            await Body(c);
            return new JsonObject { ["ok"] = true, ["hidden"] = UiHide?.Invoke() ?? false };
        });
        R("POST", "/api/ui/open", async (c, q, g) =>
        {
            var b = await Body(c);
            var tid = J.Truthy(b["task"]) ? J.Str(b["task"]) : null;
            return new JsonObject { ["ok"] = UiOpen?.Invoke(tid) ?? false };
        });
    }

    IConnector Conn(string id) => Conns == null ? throw new ApiError(503, "connectors not available")
        : Conns.Get(id) ?? throw new ApiError(404, $"no connector '{id}'");

    static bool Flag(Dictionary<string, string> q, string k) => q.TryGetValue(k, out var v) && v is "1" or "true";

    static async Task<JsonObject> Body(HttpContext c)
    {
        using var ms = new MemoryStream();
        await c.Request.Body.CopyToAsync(ms, c.RequestAborted);
        if (ms.Length > 2_000_000) throw new ApiError(413, "body too large");
        if (ms.Length == 0) return new JsonObject();
        var text = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿');
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        JsonNode n;
        try { n = J.Parse(text); }
        catch { throw new ApiError(400, "body must be JSON"); }
        if (n is not JsonObject o) throw new ApiError(400, "body must be a JSON object");
        try { _ = o.Count; }   // a key sent twice shows only when the object is first read
        catch (ArgumentException) { throw new ApiError(400, "body has a key twice"); }
        return o;
    }

    static async Task Raw(HttpContext c, int code, byte[] body, string ctype)
    {
        c.Response.StatusCode = code;
        c.Response.ContentType = ctype;
        c.Response.Headers["Cache-Control"] = "no-store";
        c.Response.ContentLength = body.Length;
        if (!HttpMethods.IsHead(c.Request.Method)) await c.Response.Body.WriteAsync(body, c.RequestAborted);
    }

    static Task Json(HttpContext c, int code, JsonNode n) =>
        Raw(c, code, Encoding.UTF8.GetBytes(J.Dump(n)), "application/json; charset=utf-8");

    /// <summary>meta.&lt;connector&gt; says what a connector's buttons act on (a run, a pull request, a machine), with the user's key:
    /// only the connector sets it.</summary>
    JsonObject NoConnectorReference(JsonObject b)
    {
        if (b["meta"] is not JsonObject m) return b;
        m.Remove("github_pr");
        foreach (var k in Conns?.All ?? Enumerable.Empty<IConnector>()) m.Remove(k.Id);
        return b;
    }

    void Check(HttpContext c, string method, string path)
    {
        var port = S.Port;
        var host = (c.Request.Headers.Host.ToString() ?? "").ToLowerInvariant();
        string[] okHosts = { $"127.0.0.1:{port}", $"localhost:{port}", $"[::1]:{port}", "127.0.0.1", "localhost", "[::1]" };
        if (!okHosts.Contains(host)) throw new ApiError(403, "bad Host header");   // DNS-rebinding guard
        var origin = c.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            var o = origin.ToLowerInvariant();
            if (o != $"http://127.0.0.1:{port}" && o != $"http://localhost:{port}") throw new ApiError(403, "cross-origin requests are not allowed");
        }
        if (!path.StartsWith("/api/")) return;
        if (path is not ("/api/health" or "/api/hub-proof") && !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(c.Request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes("Bearer " + S.Token)))
            throw new ApiError(401, "token required (an agent started before an update: restart it)");
        if (method is "POST" or "DELETE" or "PUT" or "PATCH" && string.IsNullOrEmpty(c.Request.Headers["X-TaskHub"].ToString()))
            throw new ApiError(403, "header 'X-TaskHub: 1' required on write requests");   // CSRF guard
    }

    async Task Dispatch(HttpContext c)
    {
        var method = c.Request.Method.ToUpperInvariant();
        if (method == "PATCH") method = "POST";
        if (method == "HEAD") method = "GET";
        var path = c.Request.Path.Value ?? "/";
        var q = new Dictionary<string, string>();
        foreach (var kv in c.Request.Query) q[kv.Key] = kv.Value.LastOrDefault() ?? "";
        try
        {
            Check(c, method, path);
            foreach (var (m, rx, h) in routes)
            {
                if (m != method) continue;
                var mm = rx.Match(path);
                if (!mm.Success) continue;
                var groups = mm.Groups.Cast<Group>().Skip(1).Select(x => x.Value).ToArray();
                var res = await h(c, q, groups);
                if (method != "GET") Log.D("api", $"{method} {path}");   // the panel's polling would drown everything else
                if (res != null) await Json(c, 200, res);
                return;
            }
            throw new ApiError(404, $"no route {method} {path}");
        }
        catch (ApiError e)
        {
            Log.D("api", $"{method} {path}: {e.Code}");
            if (!c.Response.HasStarted) await Json(c, e.Code, new JsonObject { ["error"] = e.Message });
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception e)
        {
            Log.E("api", "handler error " + e);
            try { if (!c.Response.HasStarted) await Json(c, 500, new JsonObject { ["error"] = e.Message }); } catch { }
        }
    }

    WebApplication Build(bool ipv6)
    {
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = S.ExeDir, Args = Array.Empty<string>() });
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = 4_000_000;
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(3);
            o.AddServerHeader = false;
            o.Listen(IPAddress.Loopback, S.Port);
            if (ipv6) o.Listen(IPAddress.IPv6Loopback, S.Port);
        });
        var a = b.Build();
        _ = S.Token;   // the file is there before the first client looks for it
        a.Run(Dispatch);
        return a;
    }

    public async Task Start()
    {
        try
        {
            app = Build(System.Net.Sockets.Socket.OSSupportsIPv6);
            await app.StartAsync();
        }
        catch (Exception e) when (System.Net.Sockets.Socket.OSSupportsIPv6)
        {
            Log.W("api", "dual-stack listen failed (" + e.GetBaseException().Message + "), retrying IPv4 only");
            try { await app.DisposeAsync(); } catch { }
            app = Build(false);
            await app.StartAsync();
        }
        Log.I("api", $"listening on {S.Url}");
    }

    public async Task Stop()
    {
        try { if (app != null) await app.StopAsync(new CancellationTokenSource(2000).Token); } catch { }
    }
}
