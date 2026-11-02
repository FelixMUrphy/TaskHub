using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Looks up the latest GitHub release once a day (installed copy only, config.json "update_check": false turns it off)
/// and offers newer versions with a notification that opens the release page. Nothing is downloaded or run.
/// </summary>
sealed class Updater
{
    const string LatestApiDefault = "https://api.github.com/repos/FelixMUrphy/TaskHub/releases/latest";
    string LatestApi = LatestApiDefault;   // a local mock only in --selftest-update
    static readonly TimeSpan Every = TimeSpan.FromHours(24);
    // the settings page re-checks quietly when the last check is older than this: the hub keeps running while the panel
    // is hidden, and a release found days ago must not stay on the button after a newer one is out
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(15);
    double lastTry;   // J.Now() of the last quiet re-check attempt (also failed ones: no retry storm while offline)
    bool stale;       // "Update to X" was pressed: the next settings refresh re-checks whatever the age
    bool selfTest;    // --selftest-update: a mock API stands in for the installed copy

    readonly Settings S;
    readonly Hub hub;
    readonly Notifier notifier;
    readonly HttpClient http;
    readonly SemaphoreSlim busy = new(1, 1);

    /// <summary>A release newer than this build (set by a check), or null.</summary>
    public volatile Release Available;
    public sealed record Release(Version Version, string Tag, string Url);

    public Updater(Settings s, Hub h, Notifier n)
    {
        S = s; hub = h; notifier = n;
        // default handler: the system proxy is used (unlike the loopback clients)
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Settings.AppId, Settings.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public bool Auto => S.Bool("update_check", true);

    /// <summary>Background loop: first check a few minutes after start, then once a day (also across restarts).</summary>
    public async Task Loop(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), ct);
            while (!ct.IsCancellationRequested)
            {
                var last = J.Num(S.Get("update_checked")) ?? 0;
                var due = TimeSpan.FromSeconds(last + Every.TotalSeconds - J.Now());
                if (due > TimeSpan.Zero) { await Task.Delay(due < Every ? due : Every, ct); continue; }
                if (Auto) await Check(false);
                // a failed check (offline, rate limit) leaves update_checked unchanged: retry in an hour, not at once
                await Task.Delay(TimeSpan.FromHours(1), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.W("update", "update loop: " + e.Message); }
    }

    /// <summary>Checks GitHub. manual: waits for a check already running, and the answer (latest version / a newer one /
    /// failed) comes back in words for the settings page; a newer version also gets its notification.</summary>
    public async Task<Release> Check(bool manual) => (await CheckSay(manual, false)).Release;

    /// <summary>A manual check from the settings page: the answer in words.</summary>
    public async Task<string> CheckNow() => (await CheckSay(true, false)).Text;

    /// <summary>The settings page is open (called on its every refresh): when automatic checks are on and the last check
    /// is older than <see cref="Fresh"/>, check again in the background, without a notification - the page shows the
    /// result on its next refresh. Returns the running check (tests), or null when nothing was started.</summary>
    public Task RecheckIfStale()
    {
        if (!Auto) return null;   // automatic checks off: no network on its own
        if (!Install.IsInstalledCopy && !selfTest) return null;   // dev / test / demo hubs never call GitHub on their own
        double now = J.Now(), last = Math.Max(J.Num(S.Get("update_checked")) ?? 0, lastTry);
        if (!stale && now - last < Fresh.TotalSeconds) return null;
        lastTry = now; stale = false;
        return CheckSay(false, true);
    }

    async Task<(Release Release, string Text)> CheckSay(bool manual, bool quiet)
    {
        if (manual) await busy.WaitAsync();
        else if (!await busy.WaitAsync(0)) return (Available, null);
        try
        {
            var r = await Fetch();
            S.Set(("update_checked", J.Now()));
            if (r == null || r.Version <= Version.Parse(Settings.Version))
            {
                Available = null;
                return (null, L.T("update_latest", Settings.Version));
            }
            Available = r;
            // automatic checks announce each version once; a manual check always answers; a quiet re-check from the open
            // settings page shows it there (and counts as announced)
            if (quiet) S.Set(("update_notified", r.Tag));
            else if (manual || (J.Str(S.Get("update_notified")) != r.Tag && hub?.Muted != true))
            {
                S.Set(("update_notified", r.Tag));
                notifier?.UpdateToast(L.T("update_title", r.Version), L.T("update_msg", Settings.Version));
            }
            return (r, L.T("update_title", r.Version));
        }
        catch (Exception e)
        {
            Log.W("update", "update check failed: " + e.Message);
            return (null, L.T("update_failed", e.GetBaseException().Message));
        }
        finally { busy.Release(); }
    }

    async Task<Release> Fetch()
    {
        using var resp = await http.GetAsync(LatestApi);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no release published yet
        resp.EnsureSuccessStatusCode();
        var o = J.Parse(await resp.Content.ReadAsStringAsync()) as JsonObject;
        var tag = J.Str(o?["tag_name"]);
        var url = J.Str(o?["html_url"]);
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) throw new InvalidDataException($"unexpected release tag '{tag}'");
        // only ever open GitHub pages of this repository
        if (!url.StartsWith(L.RepoUrl + "/releases/", StringComparison.OrdinalIgnoreCase)) url = L.RepoUrl + "/releases/latest";
        return new Release(v, tag, url);
    }

    /// <summary>"Update to X" / the notification: the newest release page, never a remembered older one (a newer version
    /// may have come out since the check); the button's version is refreshed by a quiet re-check.</summary>
    public void OpenPage()
    {
        Hub.OpenTarget(L.RepoUrl + "/releases/latest");
        stale = true;   // the next settings refresh re-checks (when automatic checks are on)
    }

    /// <summary>Tests only (with TASKHUB_TEST_HOME): the stale-offer scenario against a local mock of the releases API.
    /// Prints one line per step; no notifications, no hub.</summary>
    public static int SelfTest(Settings s, string api)
    {
        if (!Uri.TryCreate(api, UriKind.Absolute, out var u) || !u.IsLoopback) return 2;
        var up = new Updater(s, null, null) { LatestApi = u.ToString(), selfTest = true };
        string Av() => up.Available?.Version.ToString(3) ?? "none";
        foreach (var step in Console.In.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (step)
            {
                case "manual": Console.WriteLine("manual " + up.CheckNow().GetAwaiter().GetResult()); break;
                case "recheck":
                {
                    var t = up.RecheckIfStale();
                    t?.GetAwaiter().GetResult();
                    Console.WriteLine("recheck " + (t == null ? "skipped" : "ran"));
                    break;
                }
                case "age": s.Set(("update_checked", J.Now() - 3600)); up.lastTry = 0; Console.WriteLine("aged"); break;
                case "auto-off": s.Set(("update_check", false)); Console.WriteLine("auto off"); break;
                case "open": up.stale = true; Console.WriteLine("open"); break;   // OpenPage without the browser
            }
            Console.WriteLine("available " + Av());
        }
        return 0;
    }

}
