using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskHub;

sealed partial class DiscordBridge
{
    const int MaxContent = 2000, NoEmbeds = 4, Silent = 4096;
    readonly SafeHttp http;
    readonly BlockingCollection<(JsonObject body, byte[] png)> outbox = new(500);
    volatile bool tokenBad;        // 401: nothing is sent until the token changes (Discord bans an address for many of these)
    volatile bool channelBlocked;  // 403 on the chat: nothing is sent until the user acts

    static string UserAgent => $"DiscordBot (https://github.com/FelixMUrphy/TaskHub, {Settings.Version})";

    Uri ApiBase
    {
        get
        {
            var o = J.Str(S.Get("discord_api"));
            return Uri.TryCreate(o, UriKind.Absolute, out var u) && u.IsLoopback ? u : new Uri("https://discord.com/api/v10");
        }
    }

    readonly record struct Resp(int Code, JsonNode Json);

    Resp CallSync(HttpMethod m, string path, JsonNode body, string withToken = "") =>
        Call(m, path, body, withToken == "" ? token : withToken).GetAwaiter().GetResult();

    /// <summary>One call. auth null: an interaction's route, which has its token in the address. No redirects are followed.</summary>
    async Task<Resp> Call(HttpMethod m, string path, JsonNode body, string auth, CancellationToken ct = default, byte[] png = null)
    {
        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts2.CancelAfter(TimeSpan.FromSeconds(png != null ? 60 : 20));
        using var req = new HttpRequestMessage(m, new Uri(ApiBase.ToString().TrimEnd('/') + path));
        if (auth != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bot", auth);
        if (png != null)
        {
            var form = new MultipartFormDataContent();
            var clone = (JsonObject)body.DeepClone();
            clone["attachments"] = new JsonArray(new JsonObject { ["id"] = 0, ["filename"] = "chart.png" });
            form.Add(new StringContent(clone.ToJsonString(), Encoding.UTF8, "application/json"), "payload_json");
            var img = new ByteArrayContent(png);
            img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(img, "files[0]", "chart.png");
            req.Content = form;
        }
        else if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await http.Send(req, cts2.Token);
        var text = await r.Content.ReadAsStringAsync(cts2.Token);
        JsonNode json = null;
        try { json = J.Parse(text); } catch { }   // a block in front of Discord answers without JSON
        var code = (int)r.StatusCode;
        if (auth != null)
        {
            if (code == 429)
            {
                double ra = r.Headers.RetryAfter?.Delta?.TotalSeconds ?? J.Num(json?["retry_after"]) ?? 5;
                BlockFor(ra);
                Log.W("discord", $"too many requests, waiting {Math.Clamp(ra, 1, 3600):0} s");
            }
            else if (Header(r, "X-RateLimit-Remaining") == "0" && double.TryParse(Header(r, "X-RateLimit-Reset-After"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var after))
                BlockFor(after);
            if (code == 401 && auth == token && auth.Length > 0)
            {
                tokenBad = true;
                Status = "invalid token";
                Log.W("discord", "token rejected");
            }
        }
        return new Resp(code, json);
    }

    static string Header(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    // ------------------------------------------------------------ pacing: Discord's headers decide; about a message a second otherwise
    readonly object paceLock = new();
    double blockedUntil, tokens = 3, tokensAt;
    const double Burst = 3, PerSecond = 1;

    bool Blocked { get { lock (paceLock) return J.Now() < blockedUntil; } }

    void BlockFor(double seconds)
    {
        lock (paceLock) blockedUntil = Math.Max(blockedUntil, J.Now() + Math.Clamp(seconds, 0, 3600));
    }

    void Pace()
    {
        while (true)
        {
            double wait;
            lock (paceLock)
            {
                var now = J.Now();
                if (now < blockedUntil) wait = blockedUntil - now;
                else
                {
                    tokens = Math.Min(Burst, tokens + (now - tokensAt) * PerSecond);
                    tokensAt = now;
                    if (tokens >= 1) { tokens -= 1; return; }
                    wait = (1 - tokens) / PerSecond;
                }
            }
            Thread.Sleep(TimeSpan.FromSeconds(Math.Clamp(wait, 0.05, 60)));
        }
    }

    void SendWorker()
    {
        foreach (var (body, png) in outbox.GetConsumingEnumerable())
        {
            int errors = 0, limited = 0;
            while (Linked && !tokenBad && !channelBlocked)
            {
                try
                {
                    Pace();
                    var r = Call(HttpMethod.Post, $"/channels/{Channel}/messages", body, token, default, png).GetAwaiter().GetResult();
                    if (r.Code == 429) { if (++limited < 20) continue; Log.W("discord", "still limited, message dropped"); break; }
                    if (r.Code >= 500) { if (++errors >= 3) { NoteFailure("message", r); break; } Thread.Sleep(2000); continue; }
                    if (r.Code is < 200 or >= 300) NoteFailure("message", r);
                    break;
                }
                catch (Exception e)
                {
                    Log.W("discord", "message: " + e.GetType().Name);
                    if (++errors >= 3) break;
                    Thread.Sleep(2000);
                }
            }
        }
    }

    string lastFailure;

    /// <summary>Status and code only: the text of an error may hold what was sent. A chat that refuses stops being written to.</summary>
    void NoteFailure(string what, Resp r)
    {
        var code = J.Str(r.Json?["code"]);
        if (r.Code == 403 && what is "message" or "progress" or "edit")
        {
            channelBlocked = true;
            Log.W("discord", $"the chat refuses messages (code {code}): nothing more is sent until you act");
        }
        var e = $"{what}: HTTP {r.Code} code {code}";
        if (e != lastFailure) Log.W("discord", lastFailure = e);
    }
}
