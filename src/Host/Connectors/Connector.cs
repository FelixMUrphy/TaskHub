using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// A built-in connector: watches an outside service and shows its jobs as TaskHub tasks (created through the hub like
/// any other task, with the connector's own source). Buttons of kind "connector" on those tasks come back to it.
/// Settings live in config.json under connectors.&lt;id&gt; (never secrets); secrets go to Secrets (Credential Manager).
/// </summary>
interface IConnector
{
    string Id { get; }                       // "github": config key, task source, credential name
    void Start();                            // (re)reads its settings; also called after they change
    void Stop();
    /// <summary>A "connector" control pressed on one of its tasks (panel, toast, Telegram, API). Runs off the UI thread.</summary>
    JsonObject OnControl(string tid, string cid, JsonNode value);
    JsonObject State();                      // for the settings window / GET /api/connectors
}

/// <summary>The connectors of this hub: started with it, controls routed back by task.</summary>
sealed class Connectors
{
    readonly Dictionary<string, IConnector> all = new();
    readonly Settings S;

    public Connectors(Settings s, Hub hub)
    {
        S = s;
        hub.ConnectorControl = Route;
    }

    public T Add<T>(T c) where T : IConnector { all[c.Id] = c; return c; }
    public IConnector Get(string id) => all.TryGetValue(id, out var c) ? c : null;
    public IEnumerable<IConnector> All => all.Values;
    public void StartAll() { foreach (var c in all.Values) try { c.Start(); } catch (Exception e) { Log.W($"connector {c.Id}: " + e.Message); } }
    public void StopAll() { foreach (var c in all.Values) try { c.Stop(); } catch { } }

    JsonObject Route(string source, string tid, string cid, JsonNode value) =>
        Get(source) is { } c ? c.OnControl(tid, cid, value) : throw new ApiError(404, $"no connector '{source}'");

    // ------------------------------------------------------------ settings: connectors.<id> in config.json
    public static JsonObject Config(Settings s, string id) =>
        (s.Get("connectors") as JsonObject)?[id] as JsonObject ?? new JsonObject();

    public static void SetConfig(Settings s, string id, JsonObject cfg)
    {
        var all = s.Get("connectors") as JsonObject ?? new JsonObject();
        all[id] = cfg.DeepClone();
        s.Set(("connectors", all));
    }
}

/// <summary>Secrets in the Windows Credential Manager (generic credentials of the current user), never in config.json.
/// A hub with its own data folder (tests, demos) gets its own names, so it never sees or changes the real ones.</summary>
static class Secrets
{
    /// <summary>Every name TaskHub saves a secret under (GitHub connector, Telegram bot): deleted by uninstall --purge.</summary>
    public static readonly string[] Names = { "github", "telegram" };

    static string Target(Settings s, string name)
    {
        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Settings.AppId);
        var dir = Path.GetFullPath(s.DataDir).TrimEnd('\\');
        var tag = string.Equals(dir, Path.GetFullPath(def).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? ""
            : "@" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..10];
        return "TaskHub/" + name + tag;
    }

    public static string Read(Settings s, string name)
    {
        if (!CredRead(Target(s, name), 1 /* GENERIC */, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            if (c.CredentialBlobSize == 0) return "";
            var b = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, b, 0, b.Length);
            return Encoding.Unicode.GetString(b);
        }
        finally { CredFree(p); }
    }

    public static void Write(Settings s, string name, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var mem = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, mem, blob.Length);
            var c = new CREDENTIAL
            {
                Type = 1, TargetName = Target(s, name), UserName = Environment.UserName, Persist = 2 /* LOCAL_MACHINE */,
                CredentialBlob = mem, CredentialBlobSize = (uint)blob.Length, Comment = "TaskHub",
            };
            if (!CredWrite(ref c, 0)) throw new Exception("Credential Manager: error " + Marshal.GetLastWin32Error());
        }
        finally
        {
            for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(mem, i, 0);
            Marshal.FreeHGlobal(mem);
            Array.Clear(blob);
        }
    }

    public static void Delete(Settings s, string name) => CredDelete(Target(s, name), 1, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    static extern bool CredWrite(ref CREDENTIAL cred, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr p);
}

/// <summary>HTTP for connectors: only to the hosts given (exact names or ".suffix"), https only (plain http only to
/// loopback - the mock servers of the tests), no automatic redirects (each hop is checked by the caller).</summary>
sealed class SafeHttp
{
    readonly HttpClient http;
    readonly Func<IEnumerable<string>> hosts;

    public SafeHttp(Func<IEnumerable<string>> allowedHosts, int timeoutSec = 30)
    {
        hosts = allowedHosts;
        http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        }) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TaskHub/" + Settings.Version);
    }

    public bool Allowed(Uri u)
    {
        if (u == null || !u.IsAbsoluteUri) return false;
        bool loop = u.IsLoopback;
        if (u.Scheme != "https" && !(u.Scheme == "http" && loop)) return false;
        var h = u.Host.ToLowerInvariant();
        return hosts().Any(a => a.StartsWith('.') ? h.EndsWith(a) && h.Length > a.Length : h == a);
    }

    public async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct, HttpCompletionOption opt = HttpCompletionOption.ResponseContentRead)
    {
        if (!Allowed(req.RequestUri)) throw new InvalidOperationException("host not allowed: " + req.RequestUri?.Host);
        return await http.SendAsync(req, opt, ct);
    }
}
