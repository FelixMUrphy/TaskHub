using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>The gateway: a WebSocket that brings the button presses. No public address needed; nothing is polled.
/// Every address goes through <see cref="AllowedSocket"/> before the connection, the one the server names too, because the
/// first message on it carries the token. Frames are never logged.</summary>
sealed partial class DiscordBridge
{
    const int Intents = 1;              // GUILDS: not privileged; presses arrive under no intent at all
    const int MaxFrame = 8 << 20;
    const int IdentifyFloor = 100;      // at 1000 new sessions in a day Discord resets the token: stop well before
    double IdentifyGap => J.Num(S.Get("discord_identify_gap")) is double d && ApiBase.IsLoopback ? d : 300;

    string sessionId, resumeUrl;        // gateway thread only, and Start before it runs
    long? seq;
    double lastIdentify;

    void ResetSession() { sessionId = resumeUrl = null; seq = null; }

    /// <summary>wss to Discord's own gateway hosts; plain ws only on loopback (the mock of the tests).</summary>
    internal static bool AllowedSocket(Uri u) => u != null && u.IsAbsoluteUri
        && (u.IsLoopback ? u.Scheme is "ws" or "wss"
            : u.Scheme == "wss" && (u.Host.Equals("gateway.discord.gg", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".discord.gg", StringComparison.OrdinalIgnoreCase)));

    enum Next { Resume, NewSession, Stop }

    async Task GatewayLoop(CancellationToken ct)
    {
        int failures = 0;
        await ReadEndpointSetting(ct);
        while (!ct.IsCancellationRequested && !tokenBad)
        {
            try
            {
                Uri url = null;
                bool resume = sessionId != null && seq != null && Uri.TryCreate(resumeUrl, UriKind.Absolute, out url) && AllowedSocket(url);
                if (!resume)
                {
                    ResetSession();
                    url = await PrepareIdentify(ct);
                    if (url == null) return;   // refused address or a bad token: the page says so
                }
                var next = await Session(url, resume, ct, () => failures = 0);
                if (next == Next.Stop) return;
                if (next == Next.NewSession) ResetSession();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Log.D("discord", "gateway: " + e.GetType().Name);
                if (!tokenBad) Status = "offline";
            }
            if (ct.IsCancellationRequested || tokenBad) return;
            // a dropped connection is usually gone at once: back off only if it keeps failing
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(failures++, 6))), ct); } catch { return; }
        }
    }

    /// <summary>Whether a public address is set for this application: then Discord delivers presses there, not to us.</summary>
    async Task ReadEndpointSetting(CancellationToken ct)
    {
        try
        {
            var a = await Call(HttpMethod.Get, "/applications/@me", null, token, ct);
            if (a.Code == 200) endpointSet = J.Str(a.Json?["interactions_endpoint_url"]).Length > 0;
        }
        catch (Exception e) { Log.D("discord", "application: " + e.GetType().Name); }
    }

    /// <summary>Waits until a new session is safe (gap, Discord's daily count) and returns the address. Null: don't connect.</summary>
    async Task<Uri> PrepareIdentify(CancellationToken ct)
    {
        var wait = lastIdentify + IdentifyGap - J.Now();
        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
        var r = await Call(HttpMethod.Get, "/gateway/bot", null, token, ct);
        if (r.Code == 401) return null;
        if (r.Code != 200) throw new IOException("gateway address: HTTP " + r.Code);
        var limit = r.Json?["session_start_limit"];
        if (J.Num(limit?["remaining"]) is double left && left < IdentifyFloor)
        {
            var ms = J.Num(limit["reset_after"]) ?? 3600000;
            Status = "on hold";
            Log.W("discord", $"{left:0} new sessions left today: waiting {ms / 60000:0} min");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(ms, 1000, 86400000)), ct);
            throw new IOException("waited for the daily count");
        }
        if (!Uri.TryCreate(J.Str(r.Json?["url"]), UriKind.Absolute, out var url) || !AllowedSocket(url))
        {
            Status = "offline";
            Log.W("discord", "gateway address not allowed");
            return null;
        }
        return url;
    }

    static Uri WithQuery(Uri u) => new(u.GetLeftPart(UriPartial.Path) + "?v=10&encoding=json");

    /// <summary>One connection, until it ends. The caller decides from the answer.</summary>
    async Task<Next> Session(Uri url, bool resume, CancellationToken ct, Action connected)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", UserAgent);
        using var noRedirects = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false });
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(30));
            await ws.ConnectAsync(WithQuery(url), noRedirects, connectCts.Token);
        }
        using var sendLock = new SemaphoreSlim(1, 1);
        using var beatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool acked = true;

        async Task Send(JsonObject o)
        {
            var bytes = Encoding.UTF8.GetBytes(o.ToJsonString());
            await sendLock.WaitAsync(ct);
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
            finally { sendLock.Release(); }
        }
        Task Beat() => Send(new JsonObject { ["op"] = 1, ["d"] = seq });

        async Task Heartbeat(double ms)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(ms * Random.Shared.NextDouble()), beatCts.Token);
            while (!beatCts.IsCancellationRequested)
            {
                if (!acked) { ws.Abort(); return; }   // a dead connection: end it without a close, so the session can be resumed
                acked = false;
                await Beat();
                await Task.Delay(TimeSpan.FromMilliseconds(ms), beatCts.Token);
            }
        }

        var buffer = new byte[64 * 1024];
        var frame = new MemoryStream();
        try
        {
            while (true)
            {
                frame.SetLength(0);
                WebSocketReceiveResult got;
                do
                {
                    got = await ws.ReceiveAsync(buffer, ct);
                    if (got.MessageType == WebSocketMessageType.Close) return AfterClose((int?)ws.CloseStatus);
                    frame.Write(buffer, 0, got.Count);
                    if (frame.Length > MaxFrame) { ws.Abort(); return Next.Resume; }
                } while (!got.EndOfMessage);
                if (got.MessageType != WebSocketMessageType.Text) continue;
                JsonObject m;
                try { m = J.Parse(Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length)) as JsonObject; }
                catch { continue; }
                if (m == null) continue;
                switch ((int)(J.Num(m["op"]) ?? -1))
                {
                    case 10:   // Hello
                        _ = Heartbeat(Math.Max(1000, J.Num(m["d"]?["heartbeat_interval"]) ?? 41250)).ContinueWith(_ => { }, TaskScheduler.Default);
                        if (resume)
                            await Send(new JsonObject { ["op"] = 6, ["d"] = new JsonObject { ["token"] = token, ["session_id"] = sessionId, ["seq"] = seq } });
                        else
                        {
                            lastIdentify = J.Now();
                            await Send(new JsonObject
                            {
                                ["op"] = 2,
                                ["d"] = new JsonObject
                                {
                                    ["token"] = token, ["intents"] = Intents,
                                    ["properties"] = new JsonObject { ["os"] = "windows", ["browser"] = "taskhub", ["device"] = "taskhub" },
                                },
                            });
                        }
                        break;
                    case 11: acked = true; break;
                    case 1: await Beat(); break;
                    case 7:   // come back: a close other than 1000 / 1001 keeps the session
                        await CloseQuietly(ws);
                        return Next.Resume;
                    case 9:
                        await CloseQuietly(ws);
                        if (J.Truthy(m["d"])) return Next.Resume;
                        await Task.Delay(TimeSpan.FromSeconds(1 + Random.Shared.NextDouble() * 4), ct);
                        return Next.NewSession;
                    case 0:
                        if (J.Num(m["s"]) is double s) seq = (long)s;
                        var t = J.Str(m["t"]);
                        var d = m["d"] as JsonObject;
                        if (t == "READY")
                        {
                            sessionId = J.Str(d?["session_id"]);
                            resumeUrl = J.Str(d?["resume_gateway_url"]);
                            if (J.Str(d?["user"]?["username"]) is { Length: > 0 } n) botName = n;
                            Status = "connected";
                            connected();
                            Log.I("discord", "connected");
                        }
                        else if (t == "RESUMED") { Status = "connected"; connected(); }
                        else if (t == "INTERACTION_CREATE" && d != null)
                            _ = Task.Run(async () =>
                            {
                                try { await Interaction(d); }
                                catch (Exception e) { Log.W("discord", "press: " + e.GetType().Name); }
                            });
                        break;
                }
            }
        }
        catch (WebSocketException) when (!ct.IsCancellationRequested)
        {
            // the line dropped, or the heartbeat ended it
            Status = "offline";
            return Next.Resume;
        }
        finally
        {
            beatCts.Cancel();
            if (ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, bye.Token); } catch { }
            }
        }
    }

    static async Task CloseQuietly(ClientWebSocket ws)
    {
        using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await ws.CloseOutputAsync((WebSocketCloseStatus)4000, "reconnecting", bye.Token); } catch { }
    }

    /// <summary>What Discord's close code asks for. Some mean that trying again can't help.</summary>
    Next AfterClose(int? code)
    {
        switch (code)
        {
            case 4004:
                tokenBad = true;
                Status = "invalid token";
                Log.W("discord", "token rejected");
                return Next.Stop;
            case 4010 or 4011 or 4012 or 4013 or 4014:
                Status = "closed " + code;
                Log.W("discord", $"connection closed by Discord, code {code}");
                return Next.Stop;
            case 4007 or 4009:
                Status = "offline";
                return Next.NewSession;
            default:
                Status = "offline";
                return Next.Resume;
        }
    }
}
