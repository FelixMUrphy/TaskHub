using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>
/// GitHub Actions: watches the workflow runs of the chosen repositories (nothing changes in the workflows) and shows each
/// run as a task: progress by jobs / steps, ETA from the usual duration of the same workflow, on failure the job, step and
/// the end of its log; buttons to open it, cancel, re-run (all / failed jobs).
/// Talks only to api.github.com with the token; the job logs (optional, "logs") come from GitHub's log storage
/// (*.actions.githubusercontent.com / *.blob.core.windows.net) through a one-time link, without the token.
/// Polling: ETag / 304 (free), ~interval s when idle, ~10 s while a run is active, slower close to the rate limit.
/// Config (connectors.github): enabled, repos [owner/repo], interval, notify_success, logs, auth (token | gcm), api (tests),
/// pr_reviews / pr_dependabot (pull requests, read-only: PullRequests.cs).
/// The token lives in the Windows Credential Manager (Secrets "github"); auth "gcm" borrows the Git Credential Manager's.
/// </summary>
sealed partial class GitHubConnector : ISettingsConnector
{
    public string Id => "github";
    const int ExpectedTtl = 3600, WarnDays = 7;
    static readonly Regex RepoRx = new(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9_.-]{1,100}$");
    static readonly Regex TokenRx = new(@"^(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{40,})$");
    static readonly string[] LogHosts = { ".actions.githubusercontent.com", ".blob.core.windows.net" };

    readonly Settings S;
    readonly Hub hub;
    readonly SafeHttp http;
    readonly object gate = new();
    CancellationTokenSource cts;

    sealed class Run
    {
        public long Id; public string Repo; public int Attempt; public long WorkflowId;
        public string RunEtag, JobsEtag; public JsonObject Json; public JsonArray Jobs;
        public double NextPoll; public int AwaitAttempt; public double AwaitUntil;
        public double? Usual;   // usual duration of the workflow, s
    }
    sealed class RepoState
    {
        public string Etag; public double NextPoll; public bool Baseline = true; public string Status = "";
        public readonly HashSet<long> Seen = new();   // this repository's ids in `known`
        public long Forgotten;                        // the highest id dropped from `known`: older ids aren't new runs
    }
    const int KnownPerRepo = 1000;
    readonly Dictionary<long, Run> active = new();                 // runs being followed
    readonly Dictionary<long, int> known = new();                  // run id -> attempt already shown (or skipped)
    readonly Dictionary<string, RepoState> repos = new();
    readonly Dictionary<string, (double? secs, double at)> expected = new();   // repo/workflow -> usual duration

    string login, status = "off", gcmToken;
    // the last verdict on the saved token: "ok" | "revoked" | "network" | "unavailable"; null = none / not checked
    string tokenCheck;
    int tokenCheckCode;
    double gcmAt, pausedUntil, lastExpiryWarn;
    DateTimeOffset? expires;
    int rateLeft = -1;
    double rateReset, secondaryWait;   // secondary rate limit: 60 s, doubled while it repeats

    public GitHubConnector(Settings s, Hub h)
    {
        S = s; hub = h;
        http = new SafeHttp(() =>
        {
            var api = ApiUri.Host.ToLowerInvariant();
            var hosts = new List<string> { api };
            if (Cfg.Logs) hosts.AddRange(LogHosts);
            return hosts;
        });
    }

    // ------------------------------------------------------------ settings
    sealed record Conf(bool Enabled, List<string> Repos, int Interval, bool NotifySuccess, bool Logs, string Auth, int ActiveEvery,
        bool PrReviews, bool PrDependabot);

    // seconds; a local mock API (tests) may go faster
    int MinInterval => ApiUri.IsLoopback ? 2 : 15;

    Conf Cfg
    {
        get
        {
            var c = Connectors.Config(S, Id);
            var rs = (c["repos"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x).Trim()).Where(x => RepoRx.IsMatch(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var auth = J.Str(c["auth"], "token");
            return new Conf(J.Truthy(c["enabled"]), rs, Math.Clamp((int)(J.Num(c["interval"]) ?? 60), MinInterval, 3600),
                J.Truthy(c["notify_success"]), J.Truthy(c["logs"]), auth is "gcm" ? "gcm" : "token",
                Math.Clamp((int)(J.Num(c["active_interval"]) ?? 10), Math.Min(MinInterval, 10), 60),
                J.Truthy(c["pr_reviews"]), J.Truthy(c["pr_dependabot"]));
        }
    }

    // the API base can point at a local mock server (tests); anything else than loopback must be api.github.com
    Uri ApiUri
    {
        get
        {
            var o = J.Str(Connectors.Config(S, Id)["api"]);
            return Uri.TryCreate(o, UriKind.Absolute, out var u) && u.IsLoopback ? u : new Uri("https://api.github.com");
        }
    }

    /// <summary>Changes the settings (API / settings window); unknown keys are ignored, bad values rejected.</summary>
    public JsonObject Configure(JsonObject b)
    {
        var c = Connectors.Config(S, Id);
        if (b.ContainsKey("repos"))
        {
            var list = b["repos"] is JsonArray a ? a.Select(x => J.Str(x).Trim()).Where(x => x.Length > 0).ToList()
                : J.Str(b["repos"]).Split(new[] { ',', ';', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            list = list.Select(NormRepo).ToList();
            var bad = list.FirstOrDefault(x => !RepoRx.IsMatch(x));
            if (bad != null) throw new ApiError(400, $"'{J.Cut(bad, 80)}' is not owner/repo") { Ui = L.T("gh_bad_repo", J.Cut(bad, 80)) };
            if (list.Count > 50) throw new ApiError(400, "at most 50 repositories") { Ui = L.T("gh_too_many", 50) };
            c["repos"] = J.StrArray(list.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        if (b.ContainsKey("interval"))
        {
            if (J.Num(b["interval"]) is not double d || d < MinInterval || d > 3600)
                throw new ApiError(400, $"interval must be {MinInterval}-3600 s") { Ui = L.T("gh_bad_interval", MinInterval, 3600) };
            c["interval"] = (int)d;
        }
        if (b.ContainsKey("active_interval"))   // while a run is going (default 10 s)
        {
            int lo = Math.Min(MinInterval, 10);
            if (J.Num(b["active_interval"]) is not double d || d < lo || d > 60) throw new ApiError(400, $"active_interval must be {lo}-60 s");
            c["active_interval"] = (int)d;
        }
        if (b.ContainsKey("auth"))
        {
            var a = J.Str(b["auth"]);
            if (a is not ("token" or "gcm")) throw new ApiError(400, "auth must be token or gcm");
            c["auth"] = a;
        }
        foreach (var k in new[] { "enabled", "notify_success", "logs", "pr_reviews", "pr_dependabot" })
            if (b.ContainsKey(k)) c[k] = J.Truthy(b[k]);
        if (J.IsStr(b["token"])) SetToken(J.Str(b["token"]));   // a rejected token: nothing is changed
        Connectors.SetConfig(S, Id, c);
        if (J.Truthy(b["forget_token"])) { Secrets.Delete(S, Id); login = null; expires = null; tokenCheck = null; }
        Start();
        return State();
    }

    // https://github.com/owner/repo(.git) -> owner/repo
    static string NormRepo(string s)
    {
        var m = Regex.Match(s, @"^(?:https?://github\.com/)?([^/\s]+/[^/\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : s;
    }

    /// <summary>Checks a fine-grained (or classic) token with GitHub, then keeps it in the Credential Manager. A token GitHub
    /// rejects (401) is not kept (400); when GitHub can't be asked (no network, 5xx, rate limit) it is kept and checked again
    /// by the loop (token_check "network" / "unavailable"); a working one: "ok" with the login.</summary>
    public void SetToken(string token)
    {
        token = (token ?? "").Trim();
        if (!TokenRx.IsMatch(token)) throw new ApiError(400, "this does not look like a GitHub token (github_pat_... or ghp_...)");
        Resp r = null;
        try { r = Call(HttpMethod.Get, "/user", null, token, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        { Log.W("github", "token not checked: " + e.GetBaseException().Message); }
        if (r?.Code == 401) throw new ApiError(400, "GitHub rejected the token");
        try { Secrets.Write(S, Id, token); }
        catch (Exception e) { throw new ApiError(500, "the token was not saved: " + e.Message); }
        login = r?.Code == 200 ? J.Str(r.Body?["login"]) : null;
        if (login == "") login = null;
        tokenCheck = r == null ? "network" : r.Code == 200 ? "ok" : "unavailable";
        tokenCheckCode = r?.Code ?? 0;
    }

    /// <summary>The verdict on the saved token in words ("Connected as @login" / revoked / no network / unavailable).</summary>
    public string CheckText() => tokenCheck switch
    {
        "ok" => login != null ? L.T("k_ok_login", "@" + login) : L.T("k_ok"),
        "revoked" => L.T("k_revoked", "GitHub"),
        "network" => L.T("k_network", "GitHub"),
        "unavailable" => L.T("k_unavailable", "GitHub", tokenCheckCode > 0 ? "HTTP " + tokenCheckCode : "?"),
        _ => null,
    };

    public int RepoCount => Cfg.Repos.Count;

    public JsonObject State()
    {
        var c = Cfg;
        string[] rs;
        lock (gate) rs = c.Repos.ToArray();
        var rst = new JsonObject();
        lock (gate) foreach (var r in rs) rst[r] = repos.TryGetValue(r, out var x) ? RepoStatusText(x.Status) : "";
        return new JsonObject
        {
            ["enabled"] = c.Enabled, ["repos"] = J.StrArray(c.Repos), ["interval"] = c.Interval,
            ["notify_success"] = c.NotifySuccess, ["logs"] = c.Logs, ["auth"] = c.Auth,
            ["pr_reviews"] = c.PrReviews, ["pr_dependabot"] = c.PrDependabot, ["prs"] = PrCount(), ["pr_status"] = PrStatus(c),
            ["token_set"] = TokenSet(),
            ["status"] = status, ["login"] = login,
            ["token_check"] = c.Auth == "token" ? tokenCheck : null, ["token_check_text"] = c.Auth == "token" ? CheckText() : null,
            ["token_expires"] = expires?.ToUnixTimeSeconds(),
            ["rate_remaining"] = rateLeft >= 0 ? rateLeft : null,
            ["repo_status"] = rst, ["active_runs"] = Count(),
        };
    }

    // ------------------------------------------------------------ settings page row
    public string Title => "GitHub";

    /// <summary>Everything on the settings page (it used to be a window of its own): on / off, access (own token or the Git
    /// Credential Manager's login), the token, repositories (a list with Add / Remove and each one's state), the polling
    /// interval, notify on success, logs, the two pull-request switches, state lines. Never the token. Words in the UI language.</summary>
    public JsonObject SettingsRow()
    {
        var g = State();
        bool on = J.Truthy(g["enabled"]), tokenAuth = J.Str(g["auth"]) != "gcm", keySet = J.Truthy(g["token_set"]);
        var st = J.Str(g["status"]);
        var check = J.Str(g["token_check"]);
        var repoList = (g["repos"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).ToList();
        int n = repoList.Count;
        var login = J.Str(g["login"]);
        var row = new JsonObject
        {
            ["id"] = Id, ["name"] = "GitHub Actions", ["kind"] = "key", ["enabled"] = on,
            ["uses_key"] = tokenAuth, ["key_set"] = keySet,
            ["key_check"] = check.Length > 0 ? check : null,
            ["summary"] = !on ? "" : SettingsRows.Line(tokenAuth ? J.Str(g["token_check_text"]) : (login.Length > 0 ? "@" + login : ""),
                (tokenAuth ? SettingsRows.Normal(st) : st is "ok" or "starting" or "off" or "") ? "" : st, n > 0 ? L.N("gh_repos_n", n) : ""),
            ["help_url"] = SettingsRows.KeyHelpUrl(Id),
        };
        if (!on) return row;
        // access: own token (fine-grained, read-only is enough) or the login Git already has
        row["choice"] = new JsonObject
        {
            ["key"] = "auth", ["label"] = L.T("gh_access"), ["value"] = tokenAuth ? "token" : "gcm",
            ["options"] = new JsonArray(new JsonObject { ["v"] = "token", ["t"] = L.T("gh_auth_token") },
                                        new JsonObject { ["v"] = "gcm", ["t"] = L.T("gh_auth_gcm") }),
            ["note"] = tokenAuth ? (keySet ? "" : L.T("gh_perm_hint")) : L.T("gh_gcm_warn"),
        };
        var rst = g["repo_status"] as JsonObject ?? new JsonObject();
        var pst = g["pr_status"] as JsonObject ?? new JsonObject();
        var items = new JsonArray();
        foreach (var r in repoList)
        {
            var note = SettingsRows.Line(J.Str(rst[r]) is { Length: > 0 } v && v != "ok" ? v : "", J.Str(pst[r]));
            items.Add(new JsonObject { ["v"] = r, ["note"] = note });
        }
        row["list"] = new JsonObject
        {
            ["key"] = "repos", ["label"] = L.T("gh_repos"), ["items"] = items, ["placeholder"] = "owner/repo",
            ["add"] = L.T("gh_add"), ["remove"] = L.T("gh_remove"), ["empty"] = L.T("gh_repos_empty"), ["dup"] = L.T("gh_repo_dup"),
        };
        var f = SettingsRows.Field("interval", null, MinInterval, 3600, 15, g["interval"]);
        f["text"] = L.T("gh_interval");
        row["fields"] = new JsonArray(f);
        row["switches"] = new JsonArray(
            new JsonObject { ["key"] = "notify_success", ["text"] = L.T("gh_notify_success"), ["value"] = J.Truthy(g["notify_success"]) },
            new JsonObject { ["key"] = "logs", ["text"] = L.T("gh_logs"), ["value"] = J.Truthy(g["logs"]) },
            new JsonObject
            {
                ["key"] = "pr_reviews", ["text"] = L.T("pr_reviews"), ["value"] = J.Truthy(g["pr_reviews"]),
                ["head"] = L.T("pr_section"), ["hint"] = L.T("pr_hint"),
            },
            new JsonObject { ["key"] = "pr_dependabot", ["text"] = L.T("pr_dependabot"), ["value"] = J.Truthy(g["pr_dependabot"]) });
        var notes = new JsonArray();
        if (tokenAuth && J.Num(g["token_expires"]) is double e)
        {
            var exp = DateTimeOffset.FromUnixTimeSeconds((long)e).ToLocalTime();
            notes.Add(JsonValue.Create((exp - DateTimeOffset.Now < TimeSpan.FromDays(7) ? "\u26a0 " : "") + L.T("gh_expires", exp.ToString("d MMM yyyy"))));
        }
        if (J.Num(g["rate_remaining"]) is double rl && rl < 1000) notes.Add(JsonValue.Create(L.T("gh_rate_left", J.Fmt(rl))));
        if (notes.Count > 0) row["notes"] = notes;
        return row;
    }

    public JsonObject SettingsChange(JsonObject page) =>
        SettingsRows.Change(page, "token", "forget_token", new[] { "auth", "repos", "interval", "notify_success", "logs", "pr_reviews", "pr_dependabot" });

    public (string Check, string Text) KeyVerdict()
    {
        var v = State();
        return (J.Str(v["token_check"]), J.Str(v["token_check_text"]));
    }

    int Count() { lock (gate) return active.Count; }

    bool TokenSet()
    {
        try { return Secrets.Read(S, Id) is { Length: > 0 }; }
        catch (Exception e) { Log.W("github", "token not read: " + e.Message); return false; }
    }

    // ------------------------------------------------------------ lifecycle
    public void Start()
    {
        Stop();
        var c = Cfg;
        lock (gate) { repos.Clear(); known.Clear(); active.Clear(); pausedUntil = 0; secondaryWait = 0; prs.Clear(); prNext = prPausedUntil = 0; prAdopted = false; }
        // the Dependabot lists (with their ETags) are kept over a settings change: a review request turned off that is also
        // an open Dependabot PR stays, as that
        if (!c.Enabled) { status = "off"; lock (gate) pullLists.Clear(); EndAllPrs(); return; }
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        status = "starting";
        _ = Task.Run(() => Loop(ct));
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
    }

    async Task Loop(CancellationToken ct)
    {
        Adopt();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Tick(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                status = "offline: " + e.GetBaseException().Message;
                Log.W("github", e.GetBaseException().Message);
                try { await Task.Delay(15000, ct); } catch { return; }
            }
            try { await Task.Delay(1000, ct); } catch { return; }
        }
    }

    /// <summary>After a restart: runs that were still going when TaskHub stopped are followed again (and finished if
    /// they ended meanwhile), from the tasks the hub kept.</summary>
    void Adopt()
    {
        foreach (var t in hub.Brief())
        {
            if (!Hub.IsActive(J.Str(t["state"]))) continue;
            JsonObject full;
            try { full = hub.GetTask(J.Str(t["id"])); } catch { continue; }
            if (J.Str(full["source"]) != Id || full["meta"]?["github"] is not JsonObject g) continue;
            var run = new Run { Id = (long)(J.Num(g["run_id"]) ?? 0), Repo = J.Str(g["repo"]), Attempt = (int)(J.Num(g["attempt"]) ?? 1),
                WorkflowId = (long)(J.Num(g["workflow_id"]) ?? 0) };
            if (run.Id == 0 || !RepoRx.IsMatch(run.Repo)) continue;
            lock (gate) { active[run.Id] = run; known[run.Id] = run.Attempt; }
        }
    }

    async Task Tick(CancellationToken ct)
    {
        var c = Cfg;
        var now = J.Now();
        PrsCleanup(c);   // also while paused / without a token: switched-off PR tasks end
        if (now < pausedUntil) return;
        var token = await Token(c, ct);
        if (token == null) return;
        if (login == null)
        {
            var me = await Call(HttpMethod.Get, "/user", null, token, ct);
            if (me.Code == 401) { Rejected(c); return; }
            if (me.Code != 200) { status = $"GitHub: HTTP {me.Code}"; pausedUntil = now + 60; return; }
            login = J.Str(me.Body?["login"]);
        }
        status = "ok";
        if (c.Auth == "token") tokenCheck = "ok";
        WarnExpiry();
        bool slow = rateLeft >= 0 && rateLeft < 500;   // close to the hourly limit: everything at a quarter of the pace
        foreach (var repo in c.Repos)
        {
            RepoState rs;
            lock (gate) if (!repos.TryGetValue(repo, out rs)) repos[repo] = rs = new RepoState();
            if (now < rs.NextPoll) continue;
            if (J.Now() < pausedUntil) return;   // a rate limit hit by the previous request
            bool busy;
            lock (gate) busy = active.Values.Any(r => r.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));
            rs.NextPoll = now + (busy ? c.ActiveEvery : c.Interval) * (slow ? 4 : 1);
            await PollRepo(repo, rs, token, c, ct);
        }
        await PollPrs(token, c, ct);
        List<Run> due;
        lock (gate) due = active.Values.Where(r => now >= r.NextPoll).ToList();
        foreach (var run in due)
        {
            if (J.Now() < pausedUntil) return;
            run.NextPoll = now + c.ActiveEvery * (slow ? 4 : 1);
            await PollRun(run, token, c, ct);
        }
    }

    void Rejected(Conf c)
    {
        status = "token rejected";
        if (c.Auth == "token") tokenCheck = "revoked";
        login = null;
        gcmToken = null;
        pausedUntil = J.Now() + 300;
        Log.W("github", "token rejected (" + c.Auth + ")");
    }

    // ------------------------------------------------------------ token
    async Task<string> Token(Conf c, CancellationToken ct)
    {
        if (c.Auth == "gcm")
        {
            if (gcmToken != null && J.Now() - gcmAt < 1800) return gcmToken;
            gcmToken = await Task.Run(GcmToken, ct);
            gcmAt = J.Now();
            if (gcmToken == null) { status = "no token from Git Credential Manager"; pausedUntil = J.Now() + 120; }
            return gcmToken;
        }
        // "device" (Sign in with GitHub, OAuth device flow) can come here later: its token would be kept like this one
        string t;
        try { t = Secrets.Read(S, Id); }
        catch (Exception e) { status = e.Message; pausedUntil = J.Now() + 120; return null; }
        if (string.IsNullOrEmpty(t)) { status = "no token"; pausedUntil = J.Now() + 30; return null; }
        return t;
    }

    /// <summary>The github.com credential the Git Credential Manager keeps (never asks: no window, no prompt). GCM keeps
    /// them per account (remotes like https://user@github.com/...): without a match for "no user", its accounts are tried.</summary>
    static string GcmToken()
    {
        var t = GcmFill(null);
        if (t != null) return t;
        foreach (var user in Git("credential-manager github list", null).Split('\n').Select(x => x.Trim()))
            if (Regex.IsMatch(user, @"^[A-Za-z0-9-]{1,39}$") && GcmFill(user) is { } tu) return tu;
        return null;
    }

    static string GcmFill(string user)
    {
        foreach (var line in Git("credential fill", "protocol=https\nhost=github.com\n" + (user != null ? "username=" + user + "\n" : "") + "\n").Split('\n'))
            if (line.StartsWith("password=")) return line[9..].Trim();
        return null;
    }

    static string Git(string args, string input)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_ASKPASS"] = "";   // git asks a program before core.askPass and SSH_ASKPASS; empty means no window (GIT_TERMINAL_PROMPT doesn't cover this)
            psi.Environment["GCM_INTERACTIVE"] = "never";
            using var p = Process.Start(psi);
            if (input != null) p.StandardInput.Write(input);
            p.StandardInput.Close();
            var outp = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return ""; }
            return p.ExitCode == 0 ? outp.Result : "";
        }
        catch (Exception e) { Log.W("github", "git " + args.Split(' ')[0] + ": " + e.Message); }
        return "";
    }

    void WarnExpiry()
    {
        if (expires is not { } ex) return;
        var left = ex - DateTimeOffset.UtcNow;
        if (left > TimeSpan.FromDays(WarnDays) || J.Now() - lastExpiryWarn < 86400) return;
        lastExpiryWarn = J.Now();
        hub.Notify(new JsonObject
        {
            ["title"] = "TaskHub · GitHub", ["level"] = "warn", ["click"] = "dismiss",
            ["message"] = L.T("gh_token_expires", ex.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)),
        });
    }

    // A repository's state is kept as a code and put into words when read: the language can change, and a 304 keeps the state for hours.
    // GitHub's 404 doesn't tell a wrong name from a repository the token wasn't given or a missing permission (its docs name all three),
    // so the words name all three. https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api
    const string StatusNotFound = "not_found", StatusNoActions = "no_actions", StatusMoved = "moved", StatusMovedTo = "moved:";

    static string RepoStatusText(string status) => status switch
    {
        StatusNotFound => L.T("gh_repo_not_found"),
        StatusNoActions => L.T("gh_repo_no_actions"),
        StatusMoved => L.T("gh_repo_moved"),
        _ when status.StartsWith(StatusMovedTo) => L.T("gh_repo_moved_to", status[StatusMovedTo.Length..]),
        _ => status,
    };

    // ------------------------------------------------------------ runs
    async Task PollRepo(string repo, RepoState rs, string token, Conf c, CancellationToken ct)
    {
        var r = await Call(HttpMethod.Get, $"/repos/{repo}/actions/runs?per_page=100", rs.Etag, token, ct);
        if (r.Code == 304) return;
        if (r.Limited) return;   // paused; the status says until when
        if (r.Code == 401) { Rejected(c); return; }
        if (r.Code is 403 or 404) { rs.Status = r.Code == 404 ? StatusNotFound : StatusNoActions; return; }
        if (r.Code != 200) { rs.Status = $"HTTP {r.Code}"; return; }
        rs.Etag = r.ETag;
        var page = (r.Body?["workflow_runs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        // renamed / transferred: GitHub answers 301 to the new place (followed); say where, so the list can be updated
        if (r.Redirected)
        {
            var moved = J.Str(page.FirstOrDefault()?["repository"]?["full_name"]);
            rs.Status = moved.Length > 0 && !moved.Equals(repo, StringComparison.OrdinalIgnoreCase) ? StatusMovedTo + moved : StatusMoved;
        }
        else rs.Status = "ok";
        // more new runs than one page (a burst of pushes): the next pages too, while everything on a page is new
        var all = new List<JsonObject>(page);
        var next = r.Next;
        for (int pages = 1; !rs.Baseline && next != null && pages < 5 && page.Count > 0 && AllNew(page, rs); pages++)
        {
            var rn = await Send(HttpMethod.Get, next, null, token, ct);
            if (rn.Code != 200) break;
            page = (rn.Body?["workflow_runs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            all.AddRange(page);
            next = rn.Next;
        }
        var runs = all.GroupBy(x => J.Num(x["id"]) ?? 0).Select(g => g.First()).OrderBy(x => J.Num(x["id"]) ?? 0).ToList();
        foreach (var j in runs)
        {
            long id = (long)(J.Num(j["id"]) ?? 0);
            int attempt = (int)(J.Num(j["run_attempt"]) ?? 1);
            bool done = Done(j);
            bool follow;
            lock (gate)
            {
                if (active.ContainsKey(id)) continue;
                bool seen = known.TryGetValue(id, out var ka);
                // first look after start: only what is still going; later: every new run / new attempt (re-run).
                // A run older than what this repository remembers isn't new: only a re-run still going is followed.
                follow = rs.Baseline ? !done
                    : !seen && id <= rs.Forgotten ? attempt > 1 && !done
                    : !seen || attempt > ka;
                known[id] = Math.Max(attempt, seen ? ka : 0);
                rs.Seen.Add(id);
                if (rs.Seen.Count > KnownPerRepo)
                {
                    // each repository has its own bound, and what its current page shows stays
                    var onPage = runs.Select(x => (long)(J.Num(x["id"]) ?? 0)).ToHashSet();
                    foreach (var k in rs.Seen.Where(x => !onPage.Contains(x)).OrderBy(x => x).Take(KnownPerRepo / 3).ToList())
                    {
                        rs.Seen.Remove(k);
                        known.Remove(k);
                        rs.Forgotten = Math.Max(rs.Forgotten, k);
                    }
                }
            }
            if (!follow) continue;
            var run = new Run { Id = id, Repo = repo, Attempt = attempt, WorkflowId = (long)(J.Num(j["workflow_id"]) ?? 0), Json = j };
            await Show(run, token, c, ct);
            if (done) await Finished(run, token, c, ct);
            else lock (gate) active[id] = run;
        }
        rs.Baseline = false;
    }

    bool AllNew(List<JsonObject> page, RepoState rs)
    {
        lock (gate)
            return page.All(x => (long)(J.Num(x["id"]) ?? 0) is var id && id > rs.Forgotten && !known.ContainsKey(id) && !active.ContainsKey(id));
    }

    /// <summary>Finished for good. "action_required" (a run that needs someone's approval, e.g. from a fork) is not:
    /// once approved the same run goes on.</summary>
    static bool Done(JsonObject j) => J.Str(j?["status"]) == "completed" && !NeedsApproval(j);
    static bool NeedsApproval(JsonObject j) => J.Str(j?["conclusion"]) == "action_required" || J.Str(j?["status"]) == "action_required";

    async Task PollRun(Run run, string token, Conf c, CancellationToken ct)
    {
        var r = await Call(HttpMethod.Get, $"/repos/{run.Repo}/actions/runs/{run.Id}", run.RunEtag, token, ct);
        if (r.Limited) return;
        if (r.Code == 401) { Rejected(c); return; }
        // deleted on GitHub (or access lost), or sent somewhere we don't follow: not "running" forever
        if (r.Code is 404 or 410 || r.Code is >= 300 and < 400 and not 304)
        {
            lock (gate) active.Remove(run.Id);
            Try(() => hub.Finish(Tid(run.Id), new JsonObject
            {
                ["state"] = "lost", ["message"] = r.Code is 404 or 410 ? "run not found on GitHub" : $"run moved on GitHub (HTTP {r.Code})",
            }));
            return;
        }
        if (r.Code == 200) { run.RunEtag = r.ETag; run.Json = r.Body as JsonObject; }
        if (run.Json == null) return;
        int attempt = (int)(J.Num(run.Json["run_attempt"]) ?? 1);
        bool done = Done(run.Json);
        if (NeedsApproval(run.Json)) run.NextPoll = J.Now() + c.Interval;   // may wait for days: at the idle pace
        // just re-run from here: GitHub needs a moment before the new attempt shows up - not "finished" again meanwhile
        if (run.AwaitAttempt > 0)
        {
            if (attempt < run.AwaitAttempt && J.Now() < run.AwaitUntil) return;
            run.AwaitAttempt = 0;
        }
        if (attempt > run.Attempt) { run.Attempt = attempt; run.JobsEtag = null; run.Jobs = null; lock (gate) known[run.Id] = attempt; await Show(run, token, c, ct); }
        var j = await Jobs(run, run.JobsEtag, token, ct);
        if (j.Code == 200) { run.JobsEtag = j.ETag; run.Jobs = j.Body?["jobs"] as JsonArray; }
        if (done) { await Finished(run, token, c, ct); return; }
        Try(() => hub.Update(Tid(run.Id), Progress(run)));
    }

    /// <summary>A run's jobs, every page (a big matrix has more than 100). With more than one page there is no tag to keep: a job on a
    /// later page can change while the first page stays the same.</summary>
    async Task<Resp> Jobs(Run run, string etag, string token, CancellationToken ct)
    {
        var first = await Call(HttpMethod.Get, $"/repos/{run.Repo}/actions/runs/{run.Id}/attempts/{run.Attempt}/jobs?per_page=100", etag, token, ct);
        if (first.Code != 200 || first.Next == null) return first;
        var all = (first.Body?["jobs"] as JsonArray ?? new JsonArray()).Select(x => J.C(x)).ToList();
        var next = first.Next;
        for (int pages = 1; next != null && pages < 10; pages++)
        {
            var r = await Send(HttpMethod.Get, next, null, token, ct);
            if (r.Code != 200) break;
            all.AddRange((r.Body?["jobs"] as JsonArray ?? new JsonArray()).Select(x => J.C(x)));
            next = r.Next;
        }
        first.Body = new JsonObject { ["jobs"] = new JsonArray(all.ToArray()) };
        first.ETag = null;
        return first;
    }

    static string Tid(long runId) => "gh-" + runId;

    /// <summary>Registers (or re-registers, for a new attempt) the run's task.</summary>
    async Task Show(Run run, string token, Conf c, CancellationToken ct)
    {
        var j = run.Json;
        var repo = run.Repo;
        var html = J.Str(j["html_url"]);
        var sha = J.Str(j["head_sha"]);
        var title = J.Str(j["display_title"]);
        var wf = J.Str(j["name"], "workflow");
        if (title.Length == 0 || title == wf) title = J.Str(j["head_branch"]);   // manual runs have no title of their own
        var desc = string.Join(" · ", new[]
        {
            repo, J.Str(j["head_branch"]), sha.Length >= 7 ? sha[..7] : sha,
            J.Str(j["actor"]?["login"]) is { Length: > 0 } a ? "@" + a : "", J.Str(j["event"]),
            run.Attempt > 1 ? L.T("gh_attempt", run.Attempt) : "",
        }.Where(x => x.Length > 0));
        var exp = await Expected(run, token, ct);
        run.Usual = exp;
        var states = new JsonArray("running", "paused", "stale");
        var b = new JsonObject
        {
            ["id"] = Tid(run.Id), ["name"] = J.Cut(title.Length > 0 && title != wf ? wf + " · " + title : wf, 200),
            ["source"] = Id, ["project"] = repo.Split('/')[1], ["description"] = desc,
            ["started"] = Ts(j["run_started_at"]) ?? Ts(j["created_at"]),
            ["expected_duration"] = exp,
            ["notify"] = new JsonObject { ["finish"] = c.NotifySuccess, ["fail"] = true, ["stale"] = false },
            ["links"] = new JsonArray(new JsonObject { ["label"] = "GitHub", ["url"] = html }),
            ["meta"] = new JsonObject
            {
                ["github"] = new JsonObject { ["repo"] = repo, ["run_id"] = run.Id, ["attempt"] = run.Attempt, ["workflow_id"] = run.WorkflowId },
            },
            ["controls"] = new JsonArray(
                new JsonObject { ["id"] = "open", ["label"] = L.T("gh_open"), ["kind"] = "open", ["target"] = html,
                                 ["states"] = J.StrArray(Hub.AllStates) },
                new JsonObject { ["id"] = "cancel", ["label"] = L.T("gh_cancel"), ["kind"] = "connector", ["style"] = "danger",
                                 ["confirm"] = L.T("gh_cancel_q"), ["states"] = states },
                new JsonObject { ["id"] = "rerun_failed", ["label"] = L.T("gh_rerun_failed"), ["kind"] = "connector", ["style"] = "primary",
                                 ["confirm"] = L.T("gh_rerun_q"), ["states"] = new JsonArray("failed") },
                new JsonObject { ["id"] = "rerun", ["label"] = L.T("gh_rerun"), ["kind"] = "connector",
                                 ["confirm"] = L.T("gh_rerun_q"), ["states"] = new JsonArray("failed", "cancelled", "done") }),
        };
        if (!Uri.TryCreate(html, UriKind.Absolute, out var hu) || hu.Scheme != "https" && !hu.IsLoopback)
        {
            b.Remove("links");
            ((JsonArray)b["controls"]).RemoveAt(0);
        }
        hub.Register(b, trusted: true);
        Try(() => hub.Update(Tid(run.Id), Progress(run)));
    }

    /// <summary>Stage = the job / step running now. The bar: time against the usual duration, the same source as the ETA, because steps are
    /// very uneven (one long test step held the bar at 7 of 19 for most of a run while the ETA ran out). Past the usual duration it
    /// has nothing to say: no percentage, and the status says so. Without earlier runs, or waiting for an approval, steps of all jobs
    /// (a job without steps yet counts as one).</summary>
    JsonObject Progress(Run run)
    {
        var st = J.Str(run.Json?["status"]);
        var u = new JsonObject
        {
            ["attention"] = st == "waiting" || NeedsApproval(run.Json) ? L.T("gh_waiting") : "",
        };
        var jobs = (run.Jobs ?? new JsonArray()).OfType<JsonObject>().ToList();
        if (jobs.Count == 0) { u["status"] = st is "queued" or "requested" or "pending" or "waiting" ? L.T("gh_queued") : ""; return u; }
        int total = 0, done = 0;
        string stage = "";
        foreach (var job in jobs)
        {
            var steps = (job["steps"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            if (steps.Count == 0) { total++; if (J.Str(job["status"]) == "completed") done++; continue; }
            total += steps.Count;
            done += steps.Count(s => J.Str(s["status"]) == "completed");
            if (stage.Length == 0 && J.Str(job["status"]) == "in_progress")
            {
                var cur = steps.FirstOrDefault(s => J.Str(s["status"]) == "in_progress");
                stage = J.Str(job["name"]) + (cur != null ? " › " + J.Str(cur["name"]) : "");
            }
        }
        int jd = jobs.Count(x => J.Str(x["status"]) == "completed");
        double? bar = total > 0 ? Math.Min(0.99, (double)done / total) : null;
        var status = L.T("gh_jobs", jd, jobs.Count);
        if (run.Usual is double usual && usual > 0 && J.Str(u["attention"]).Length == 0 && Ts(run.Json["run_started_at"]) is double started)
        {
            var part = Math.Max(0, J.Now() - started) / usual;
            bar = part < 1 ? Math.Min(0.99, part) : null;
            if (part >= 1) status += " · " + L.T("gh_longer");
        }
        u["progress"] = bar;
        u["stage"] = J.Cut(stage, 200);
        u["status"] = status;
        return u;
    }

    async Task Finished(Run run, string token, Conf c, CancellationToken ct)
    {
        lock (gate) active.Remove(run.Id);
        var j = run.Json;
        var concl = J.Str(j["conclusion"]);
        var state = concl switch
        {
            "success" or "neutral" or "skipped" => "done",
            "cancelled" or "stale" => "cancelled",
            _ => "failed",   // failure, timed_out, startup_failure (action_required is not finished: see Done)
        };
        if (run.Jobs == null)
        {
            var jr = await Jobs(run, null, token, ct);
            if (jr.Code == 200) run.Jobs = jr.Body?["jobs"] as JsonArray;
        }
        var jobs = (run.Jobs ?? new JsonArray()).OfType<JsonObject>().ToList();
        string message = "";
        var upd = new JsonObject { ["status"] = jobs.Count > 0 ? L.T("gh_jobs", jobs.Count(x => J.Str(x["status"]) == "completed"), jobs.Count) : "", ["stage"] = "" };
        if (state == "failed")
        {
            var job = jobs.FirstOrDefault(x => J.Str(x["conclusion"]) is "failure" or "timed_out" or "startup_failure")
                      ?? jobs.FirstOrDefault(x => J.Str(x["conclusion"]) is not ("success" or "skipped" or "neutral" or ""));
            var step = (job?["steps"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .FirstOrDefault(s => J.Str(s["conclusion"]) is "failure" or "timed_out" or "cancelled");
            message = job != null ? J.Str(job["name"]) + (step != null ? " › " + J.Str(step["name"]) : "") : "";
            if (concl != "failure") message = concl.Replace('_', ' ') + (message.Length > 0 ? " · " + message : "");
            if (job != null && c.Logs)
            {
                var tail = await LogTail((long)(J.Num(job["id"]) ?? 0), run.Repo, token, ct);
                if (tail.Count > 0) { upd["log"] = J.StrArray(tail); upd["level"] = "error"; }
            }
        }
        var links = new JsonArray(new JsonObject { ["label"] = "GitHub", ["url"] = J.Str(j["html_url"]) });
        if (state == "done") foreach (var l in await Extras(run, token, ct)) links.Add(l);
        if (Uri.TryCreate(J.Str(j["html_url"]), UriKind.Absolute, out var hu) && (hu.Scheme == "https" || hu.IsLoopback)) upd["links"] = links;
        Try(() => hub.Update(Tid(run.Id), upd));
        Try(() => hub.Finish(Tid(run.Id), new JsonObject { ["state"] = state, ["message"] = J.Cut(message, 300) }));
        if (state == "done" && run.WorkflowId != 0) lock (gate) expected.Remove(run.Repo + "/" + run.WorkflowId);   // next run: with this one
    }

    /// <summary>Links of a successful run: its artifacts (pages on github.com) and the release of its tag, if there is one.</summary>
    async Task<List<JsonObject>> Extras(Run run, string token, CancellationToken ct)
    {
        var res = new List<JsonObject>();
        var html = J.Str(run.Json["html_url"]);
        var a = await Call(HttpMethod.Get, $"/repos/{run.Repo}/actions/runs/{run.Id}/artifacts?per_page=10", null, token, ct);
        foreach (var x in (a.Body?["artifacts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(5))
            if (!J.Truthy(x["expired"]))
                res.Add(new JsonObject { ["label"] = "📦 " + J.Cut(J.Str(x["name"]), 60), ["url"] = html + "/artifacts/" + J.Str(x["id"]) });
        var tag = J.Str(run.Json["head_branch"]);
        if (J.Str(run.Json["event"]) == "release" || Regex.IsMatch(tag, @"^v?\d+(\.\d+)+"))
        {
            var r = await Call(HttpMethod.Get, $"/repos/{run.Repo}/releases/tags/{Uri.EscapeDataString(tag)}", null, token, ct);
            if (r.Code == 200 && J.Str(r.Body?["html_url"]) is { Length: > 0 } ru)
                res.Add(new JsonObject { ["label"] = "🏷 " + J.Cut(J.Str(r.Body["name"], tag), 60), ["url"] = ru });
        }
        return res;
    }

    /// <summary>The last lines of a job's log. GitHub answers with a short-lived link to its log storage: followed only to
    /// those hosts, without the token, reading at most 30 MB.</summary>
    async Task<List<string>> LogTail(long jobId, string repo, string token, CancellationToken ct)
    {
        var tail = new Queue<string>();
        try
        {
            var r = await Call(HttpMethod.Get, $"/repos/{repo}/actions/jobs/{jobId}/logs", null, token, ct, follow: false);
            if (r.Code is not (301 or 302 or 307) || r.Location == null) return new();
            if (!LogHostOk(r.Location)) { Log.W("github", "log link to an unexpected host " + r.Location.Host); return new(); }
            using var req = new HttpRequestMessage(HttpMethod.Get, r.Location);
            using var resp = await http.Send(req, ct, HttpCompletionOption.ResponseHeadersRead);
            if (resp.StatusCode != HttpStatusCode.OK) return new();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var rd = new StreamReader(stream, Encoding.UTF8);
            long read = 0;
            string line;
            while ((line = await rd.ReadLineAsync(ct)) != null && read < 30_000_000)
            {
                read += line.Length + 1;
                line = Regex.Replace(line, @"^﻿?\d{4}-\d\d-\d\dT[\d:.]+Z ?", "").TrimEnd();
                if (line.Length == 0 || line.StartsWith("##[group]") || line.StartsWith("##[endgroup]")) continue;
                tail.Enqueue(J.Cut(line.Replace("##[error]", "error: "), 500));
                if (tail.Count > 30) tail.Dequeue();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.W("github", "log: " + e.Message); }
        return tail.ToList();
    }

    bool LogHostOk(Uri u)
    {
        var h = u.Host.ToLowerInvariant();
        if (u.IsLoopback && ApiUri.IsLoopback) return true;   // the tests' mock server
        return u.Scheme == "https" && LogHosts.Any(s => h.EndsWith(s));
    }

    /// <summary>Usual duration of the workflow: median of its last successful runs (cached for an hour).</summary>
    async Task<double?> Expected(Run run, string token, CancellationToken ct)
    {
        if (run.WorkflowId == 0) return null;
        var key = run.Repo + "/" + run.WorkflowId;
        lock (gate) if (expected.TryGetValue(key, out var e) && J.Now() - e.at < ExpectedTtl) return e.secs;
        var r = await Call(HttpMethod.Get, $"/repos/{run.Repo}/actions/workflows/{run.WorkflowId}/runs?status=success&per_page=10", null, token, ct);
        var ds = (r.Body?["workflow_runs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(x => J.Num(x["id"]) != run.Id)
            .Select(x => Ts(x["updated_at"]) - Ts(x["run_started_at"])).Where(d => d is > 0 and < 86400 * 3).Select(d => d.Value)
            .OrderBy(d => d).ToList();
        double? secs = ds.Count >= 2 ? ds[ds.Count / 2] : null;
        if (r.Code == 200) lock (gate) expected[key] = (secs, J.Now());
        return secs;
    }

    // ------------------------------------------------------------ buttons
    public JsonObject OnControl(string tid, string cid, JsonNode value, string via)
    {
        var t = hub.GetTask(tid);
        if (J.Str(t["source"]) != Id || t["meta"]?["github"] is not JsonObject g) throw new ApiError(404, "not a GitHub run");
        var repo = J.Str(g["repo"]);
        long id = (long)(J.Num(g["run_id"]) ?? 0);
        if (!RepoRx.IsMatch(repo) || id == 0) throw new ApiError(400, "bad run reference");
        var c = Cfg;
        var token = Token(c, CancellationToken.None).GetAwaiter().GetResult() ?? throw new ApiError(400, "no GitHub token");
        var path = cid switch
        {
            "cancel" => $"/repos/{repo}/actions/runs/{id}/cancel",
            "rerun" => $"/repos/{repo}/actions/runs/{id}/rerun",
            "rerun_failed" => $"/repos/{repo}/actions/runs/{id}/rerun-failed-jobs",
            _ => throw new ApiError(404, $"unknown action '{cid}'"),
        };
        var r = Call(HttpMethod.Post, path, null, token, CancellationToken.None).GetAwaiter().GetResult();
        if (r.Code is not (200 or 201 or 202 or 204))
        {
            var msg = J.Str(r.Body?["message"], $"HTTP {r.Code}");
            throw new ApiError(r.Code is 403 or 404 or 409 or 422 ? 409 : 502, "GitHub: " + J.Cut(msg, 200)
                + (r.Code is 403 or 404 ? " (" + L.T("gh_need_write") + ")" : ""));
        }
        if (cid != "cancel")
        {
            int attempt = (int)(J.Num(g["attempt"]) ?? 1);
            lock (gate)
            {
                if (!active.TryGetValue(id, out var run))
                    active[id] = run = new Run { Id = id, Repo = repo, Attempt = attempt, WorkflowId = (long)(J.Num(g["workflow_id"]) ?? 0) };
                run.AwaitAttempt = attempt + 1;
                run.AwaitUntil = J.Now() + 120;
                run.NextPoll = J.Now() + 2;
            }
        }
        else lock (gate) if (active.TryGetValue(id, out var run)) run.NextPoll = J.Now() + 2;
        return new JsonObject { ["ok"] = true };
    }

    // ------------------------------------------------------------ HTTP
    sealed class Resp
    {
        public int Code; public JsonNode Body; public string ETag; public Uri Location, Next;
        public bool Limited;      // rate limited: everything paused (pausedUntil)
        public bool Redirected;   // answered from where a redirect led (301: the repository was renamed / moved)
    }

    Task<Resp> Call(HttpMethod m, string path, string etag, string token, CancellationToken ct, bool follow = true)
    {
        var baseUri = ApiUri;
        return Send(m, new Uri(baseUri, baseUri.AbsolutePath.TrimEnd('/') + path), etag, token, ct, follow);
    }

    static bool SameHost(Uri a, Uri b) =>
        a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    /// <summary>A request to the API. Redirects (301 renamed repository, 302 / 307) are followed only within the API host,
    /// so the token never goes anywhere else; a link to another host comes back as it is (Location).</summary>
    async Task<Resp> Send(HttpMethod m, Uri uri, string etag, string token, CancellationToken ct, bool follow = true)
    {
        var api = ApiUri;
        if (!SameHost(uri, api)) throw new InvalidOperationException("not the API host: " + uri.Host);
        bool redirected = false;
        for (int hop = 0; ; hop++)
        {
            var res = await SendOnce(m, uri, etag, token, ct);
            if (follow && hop < 3 && res.Code is 301 or 302 or 307 or 308 && res.Location != null && SameHost(res.Location, api))
            {
                uri = res.Location;
                redirected = true;
                continue;
            }
            res.Redirected = redirected;
            return res;
        }
    }

    async Task<Resp> SendOnce(HttpMethod m, Uri uri, string etag, string token, CancellationToken ct)
    {
        var path = uri.AbsolutePath;
        using var req = new HttpRequestMessage(m, uri);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (etag != null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts2.CancelAfter(TimeSpan.FromSeconds(30));
        using var r = await http.Send(req, cts2.Token);
        var res = new Resp { Code = (int)r.StatusCode, ETag = r.Headers.ETag?.ToString(), Location = r.Headers.Location };
        if (res.Location != null && !res.Location.IsAbsoluteUri) res.Location = new Uri(req.RequestUri, res.Location);
        // the search API has its own bucket (30 a minute): it pauses only the pull request searches
        bool search = r.Headers.TryGetValues("x-ratelimit-resource", out var rsrc) ? rsrc.First() == "search"
            : path.StartsWith(ApiUri.AbsolutePath.TrimEnd('/') + "/search/", StringComparison.Ordinal);
        int searchLeft = -1;
        double searchReset = 0;
        if (r.Headers.TryGetValues("x-ratelimit-remaining", out var rl) && int.TryParse(rl.First(), out var left)) { if (search) searchLeft = left; else rateLeft = left; }
        if (r.Headers.TryGetValues("x-ratelimit-reset", out var rr) && double.TryParse(rr.First(), CultureInfo.InvariantCulture, out var reset)) { if (search) searchReset = reset; else rateReset = reset; }
        if (r.Headers.TryGetValues("Link", out var lk))
        {
            var mn = Regex.Match(string.Join(",", lk), @"<([^>]+)>\s*;\s*rel=""next""");
            if (mn.Success && Uri.TryCreate(mn.Groups[1].Value, UriKind.Absolute, out var nu) && SameHost(nu, ApiUri)) res.Next = nu;
        }
        if (r.Headers.TryGetValues("github-authentication-token-expiration", out var ex))
        {
            var s = ex.First().Trim().Replace(" UTC", " +00:00");
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) expires = d;
        }
        else if (path.EndsWith("/user") && res.Code == 200) expires = null;
        if (res.Code is >= 200 and < 300 && m == HttpMethod.Get)
        {
            var text = await r.Content.ReadAsStringAsync(cts2.Token);
            try { res.Body = text.Length > 0 ? J.Parse(text) : null; } catch { }
        }
        else if (res.Code >= 400)
        {
            try { res.Body = J.Parse(await r.Content.ReadAsStringAsync(cts2.Token)); } catch { }
        }
        // out of requests for this hour, or a secondary limit (403 / 429 with remaining > 0 and no retry-after: only the
        // message tells it from a permission 403): everything waits - as long as GitHub says, else 60 s, doubling
        bool secondary = res.Code is 403 or 429 && J.Str(res.Body?["message"]).Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        if (search && (res.Code is 403 or 429) && (searchLeft == 0 || r.Headers.RetryAfter != null || secondary))
        {
            double until = r.Headers.RetryAfter?.Delta is TimeSpan sra ? J.Now() + Math.Max(1, sra.TotalSeconds)
                : searchLeft == 0 && searchReset > 0 ? Math.Max(searchReset, J.Now() + 10) : J.Now() + 120;
            prPausedUntil = Math.Min(until, J.Now() + 3600);
            res.Limited = true;
            Log.W("github", "search rate limit, pull requests wait until " + DateTimeOffset.FromUnixTimeSeconds((long)prPausedUntil).ToLocalTime().ToString("HH:mm:ss"));
        }
        else if ((res.Code is 403 or 429) && (rateLeft == 0 || r.Headers.RetryAfter != null || secondary))
        {
            double until;
            if (r.Headers.RetryAfter?.Delta is TimeSpan ra) until = J.Now() + Math.Max(1, ra.TotalSeconds);
            else if (rateLeft == 0 && !secondary) until = Math.Max(rateReset, J.Now() + 60);
            else
            {
                secondaryWait = secondaryWait <= 0 ? 60 : Math.Min(secondaryWait * 2, 1800);
                until = J.Now() + secondaryWait;
            }
            pausedUntil = Math.Max(pausedUntil, Math.Min(until, J.Now() + 3600));
            res.Limited = true;
            status = "rate limit, waiting until " + DateTimeOffset.FromUnixTimeSeconds((long)pausedUntil).ToLocalTime().ToString("HH:mm:ss");
            Log.W("github", "" + status + (secondary ? " (secondary limit)" : ""));
        }
        else if (res.Code is >= 200 and < 400) secondaryWait = 0;
        return res;
    }

    static double? Ts(JsonNode n) =>
        DateTimeOffset.TryParse(J.Str(n), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.ToUnixTimeMilliseconds() / 1000.0 : null;

    static void Try(Action a)
    {
        try { a(); } catch (Exception e) { Log.W("github", e.Message); }
    }
}
