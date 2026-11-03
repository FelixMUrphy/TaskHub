using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>AWS Signature Version 4 for the header form (own small signer instead of the AWS SDK: read-only calls to a few
/// hosts, all traffic through SafeHttp; see .ai/decisions.md). Checked against AWS's own test suite (tests/sigv4_vectors.json)
/// and, for what HttpClient really sends, against an independent signer in the mock (tests/aws_mock.py).
/// https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_sigv-create-signed-request.html</summary>
static class SigV4
{
    public sealed record Creds(string AccessKey, string Secret, string SessionToken = null);

    public sealed record Request(string Method, string Path, IReadOnlyList<(string Name, string Value)> Query,
        IReadOnlyList<(string Name, string Value)> Headers, byte[] Body);

    public sealed record Result(string Canonical, string StringToSign, string Signature, string SignedHeaders, string Authorization,
        string AmzDate);

    const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>Signs a request. x-amz-date and, with a session token, x-amz-security-token are signed too: the caller sends them.
    /// <paramref name="normalize"/>: remove dot segments and repeated slashes (every service but S3).</summary>
    public static Result Sign(Request r, Creds c, string region, string service, DateTime utc, bool normalize = true)
    {
        string amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture), date = amzDate[..8];
        var all = r.Headers.Select(h => (Name: h.Name.ToLowerInvariant(), Value: CleanValue(h.Value))).ToList();
        all.Add(("x-amz-date", amzDate));
        if (!string.IsNullOrEmpty(c.SessionToken)) all.Add(("x-amz-security-token", c.SessionToken));
        var headers = all.GroupBy(h => h.Name).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Value: string.Join(",", g.Select(x => x.Value)))).ToList();
        var signed = string.Join(";", headers.Select(h => h.Name));
        var query = string.Join("&", r.Query.Select(q => (Name: Encode(q.Name), Value: Encode(q.Value)))
            .OrderBy(q => q.Name, StringComparer.Ordinal).ThenBy(q => q.Value, StringComparer.Ordinal).Select(q => q.Name + "=" + q.Value));
        var canonical = string.Join("\n", r.Method.ToUpperInvariant(), CanonicalPath(r.Path, normalize), query,
            string.Concat(headers.Select(h => h.Name + ":" + h.Value + "\n")), signed, Hex(SHA256.HashData(r.Body ?? Array.Empty<byte>())));
        var scope = $"{date}/{region}/{service}/aws4_request";
        var toSign = string.Join("\n", Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + c.Secret), date);
        foreach (var part in new[] { region, service, "aws4_request" }) key = Hmac(key, part);
        var signature = Hex(Hmac(key, toSign));
        return new Result(canonical, toSign, signature, signed,
            $"{Algorithm} Credential={c.AccessKey}/{scope}, SignedHeaders={signed}, Signature={signature}", amzDate);
    }

    /// <summary>Signs a message before it is sent: host, content-type and x-amz-* are signed. <paramref name="body"/> must be
    /// the exact bytes of its content, and the content's type must be set by the caller (no charset added afterwards).</summary>
    public static void Apply(HttpRequestMessage m, byte[] body, Creds c, string region, string service, DateTime utc)
    {
        var u = m.RequestUri;
        var headers = new List<(string, string)> { ("host", u.Authority.ToLowerInvariant()) };
        if (m.Content?.Headers.ContentType is MediaTypeHeaderValue ct) headers.Add(("content-type", ct.ToString()));
        foreach (var h in m.Headers)
            if (h.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
                foreach (var v in h.Value) headers.Add((h.Key, v));
        var r = Sign(new Request(m.Method.Method, u.AbsolutePath, ParseQuery(u.Query), headers, body), c, region, service, utc);
        m.Headers.TryAddWithoutValidation("X-Amz-Date", r.AmzDate);
        if (!string.IsNullOrEmpty(c.SessionToken)) m.Headers.TryAddWithoutValidation("X-Amz-Security-Token", c.SessionToken);
        m.Headers.TryAddWithoutValidation("Authorization", r.Authorization);
    }

    static List<(string, string)> ParseQuery(string raw)
    {
        var res = new List<(string, string)>();
        foreach (var p in (raw ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = p.IndexOf('=');
            res.Add(i < 0 ? (Uri.UnescapeDataString(p), "") : (Uri.UnescapeDataString(p[..i]), Uri.UnescapeDataString(p[(i + 1)..])));
        }
        return res;
    }

    static string CleanValue(string v) => string.Join(" ", (v ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The path as it goes on the wire, encoded once more (every service but S3 signs it double-encoded).</summary>
    static string CanonicalPath(string path, bool normalize)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        if (normalize)
        {
            bool endSlash = path.EndsWith('/') || path.EndsWith("/.") || path.EndsWith("/..");
            var parts = new List<string>();
            foreach (var s in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (s == ".") continue;
                if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(s);
            }
            path = "/" + string.Join("/", parts) + (endSlash && parts.Count > 0 ? "/" : "");
        }
        return string.Join("/", path.Split('/').Select(Encode));
    }

    /// <summary>Everything but A-Za-z0-9-._~ as %XX of its UTF-8 bytes, upper-case hex.</summary>
    static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s ?? ""))
        {
            char ch = (char)b;
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.' or '_' or '~') sb.Append(ch);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    /// <summary>Hidden test entry: `TaskHubHost --selftest-sigv4 IN.json OUT.json`, honoured only with TASKHUB_TEST_HOME set
    /// (Program.Main). "vectors": the signer alone; "send": real requests through SafeHttp to a loopback mock.</summary>
    public static int SelfTest(string inPath, string outPath)
    {
        try
        {
            var inp = (JsonObject)JsonNode.Parse(File.ReadAllText(inPath));
            var vectors = new JsonArray();
            foreach (var v in inp["vectors"] as JsonArray ?? new JsonArray())
            {
                var c = new Creds(J.Str(v["access_key"]), J.Str(v["secret"]), v["token"] == null ? null : J.Str(v["token"]));
                var hs = (v["headers"] as JsonArray ?? new JsonArray()).Select(h => (J.Str(h[0]), J.Str(h[1]))).ToList();
                var r = Sign(new Request(J.Str(v["method"]), J.Str(v["path"]), ParseQuery(J.Str(v["query"])), hs, Encoding.UTF8.GetBytes(J.Str(v["body"]))),
                    c, J.Str(v["region"]), J.Str(v["service"]), DateTime.Parse(J.Str(v["timestamp"]), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                    J.Truthy(v["normalize"]));
                vectors.Add((JsonNode)new JsonObject { ["name"] = J.Str(v["name"]), ["canonical"] = r.Canonical, ["string_to_sign"] = r.StringToSign, ["signature"] = r.Signature, ["authorization"] = r.Authorization });
            }
            var sent = new JsonArray();
            var http = new SafeHttp(() => new[] { "127.0.0.1" });
            foreach (var q in inp["send"] as JsonArray ?? new JsonArray())
            {
                var body = q["body"] == null ? null : Encoding.UTF8.GetBytes(J.Str(q["body"]));
                using var m = new HttpRequestMessage(new HttpMethod(J.Str(q["method"])), J.Str(q["url"]));
                if (body != null)
                {
                    m.Content = new ByteArrayContent(body);
                    m.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(J.Str(q["content_type"]));
                }
                if (q["target"] != null) m.Headers.TryAddWithoutValidation("X-Amz-Target", J.Str(q["target"]));
                Apply(m, body, new Creds(J.Str(q["access_key"]), J.Str(q["secret"]), q["token"] == null ? null : J.Str(q["token"])),
                    J.Str(q["region"]), J.Str(q["service"]), DateTime.UtcNow);
                using var resp = http.Send(m, CancellationToken.None).GetAwaiter().GetResult();
                sent.Add((JsonNode)new JsonObject { ["status"] = (int)resp.StatusCode, ["body"] = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() });
            }
            File.WriteAllText(outPath, new JsonObject { ["vectors"] = vectors, ["send"] = sent }.ToJsonString());
            return 0;
        }
        catch (Exception e) { File.WriteAllText(outPath, "{\"error\":" + JsonValue.Create(e.ToString()).ToJsonString() + "}"); return 1; }
    }
}
