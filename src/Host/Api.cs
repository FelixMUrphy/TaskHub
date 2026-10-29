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
sealed class Api
{
    readonly Settings S;
    readonly Hub hub;
    public Func<string, bool> UiOpen;    // task -> ok
    public Func<bool> UiHide;            // -> hidden
    public TelegramBridge Telegram;
    WebApplication app;

    delegate Task<JsonNode> Handler(HttpContext ctx, Dictionary<string, string> q, string[] g);
    readonly List<(string m, Regex rx, Handler h)> routes = new();

    public Api(Settings s, Hub h)
    {
        S = s;
        hub = h;
        Routes();
    }

    void R(string method, string pattern, Handler h) => routes.Add((method, new Regex("^" + pattern + "$"), h));
    static Task<JsonNode> V(JsonNode n) => Task.FromResult(n);
    const string Id = "([A-Za-z0-9_.-]+)";

    void Routes()
    {
        R("GET", "/", (c, q, g) => { c.Response.Redirect("/ui"); return V(null); });
        R("GET", "/ui", async (c, q, g) =>
        {
            var html = Encoding.UTF8.GetString(Res.Read("ui.html") ?? Array.Empty<byte>());
            html = html.Replace("__TOKEN__", J.Dump(JsonValue.Create(S.Token)))
                .Replace("__VERSION__", Settings.Version)
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
                ["ok"] = true, ["app"] = Settings.AppId, ["version"] = Settings.Version, ["impl"] = "dotnet",
                ["pid"] = Environment.ProcessId, ["tasks"] = total, ["active"] = active,
            });
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
        R("POST", "/api/tasks", async (c, q, g) => hub.Register(await Body(c)));
        R("GET", $"/api/tasks/{Id}", (c, q, g) => V(hub.GetTask(g[0])));
        R("DELETE", $"/api/tasks/{Id}", (c, q, g) => V(hub.Delete(g[0], Flag(q, "force"))));
        R("POST", $"/api/tasks/{Id}/update", async (c, q, g) => hub.Update(g[0], await Body(c)));
        R("POST", $"/api/tasks/{Id}/log", async (c, q, g) => hub.AddLogApi(g[0], await Body(c)));
        R("POST", $"/api/tasks/{Id}/heartbeat", async (c, q, g) => { await Body(c); return hub.Heartbeat(g[0]); });
        R("POST", $"/api/tasks/{Id}/finish", async (c, q, g) => hub.Finish(g[0], await Body(c)));
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
        R("POST", "/api/notify", async (c, q, g) => hub.Notify(await Body(c)));
        R("POST", "/api/hooks/(claude|codex)", async (c, q, g) => AgentHooks.Handle(hub, g[0], await Body(c)));
        R("GET", "/api/telegram", (c, q, g) => V(Telegram?.State() ?? new JsonObject { ["status"] = "off" }));
        R("POST", "/api/telegram", async (c, q, g) =>
        {
            var b = await Body(c);
            if (Telegram == null) throw new ApiError(503, "telegram bridge not available");
            if (J.Truthy(b["disconnect"])) Telegram.Disconnect();
            if (b.ContainsKey("away_only")) Telegram.SetAwayOnly(J.Truthy(b["away_only"]));
            if (J.IsStr(b["token"])) return await Task.Run(() => Telegram.Pair(J.Str(b["token"])));
            if (J.Truthy(b["test"])) Telegram.Test();
            return Telegram.State();
        });
        R("POST", "/api/settings", async (c, q, g) =>
        {
            var b = await Body(c);
            if (b.ContainsKey("muted")) hub.SetMuted(J.Truthy(b["muted"]));
            if (J.Truthy(b["seen"])) hub.MarkSeen();
            if (b.ContainsKey("language")) hub.SetLanguage(J.Str(b["language"], "auto"));
            return new JsonObject { ["ok"] = true, ["muted"] = hub.Muted, ["lang"] = L.Lang, ["language"] = L.Setting };
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
        return n as JsonObject ?? throw new ApiError(400, "body must be a JSON object");
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
        var tok = S.Token;
        if (!string.IsNullOrEmpty(tok) && c.Request.Headers.Authorization.ToString() != "Bearer " + tok && path != "/api/health")
            throw new ApiError(401, "token required");
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
                if (res != null) await Json(c, 200, res);
                return;
            }
            throw new ApiError(404, $"no route {method} {path}");
        }
        catch (ApiError e)
        {
            if (!c.Response.HasStarted) await Json(c, e.Code, new JsonObject { ["error"] = e.Message });
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception e)
        {
            Log.W("handler error " + e);
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
            Log.W("dual-stack listen failed (" + e.GetBaseException().Message + "), retrying IPv4 only");
            try { await app.DisposeAsync(); } catch { }
            app = Build(false);
            await app.StartAsync();
        }
        Log.W($"listening on {S.Url}");
    }

    public async Task Stop()
    {
        try { if (app != null) await app.StopAsync(new CancellationTokenSource(2000).Token); } catch { }
    }
}
