using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Looks up the latest GitHub release once a day (installed copy only, config.json "update_check": false turns it off)
/// and offers newer versions with a notification that opens the release page. Nothing is downloaded or run.
/// </summary>
sealed class Updater
{
    const string LatestApi = "https://api.github.com/repos/FelixMUrphy/TaskHub/releases/latest";
    static readonly TimeSpan Every = TimeSpan.FromHours(24);

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
        catch (Exception e) { Log.W("update loop: " + e.Message); }
    }

    /// <summary>Checks GitHub. manual: also report "up to date" / errors. Returns the newer release or null.</summary>
    public async Task<Release> Check(bool manual)
    {
        if (!await busy.WaitAsync(0)) return Available;
        try
        {
            var r = await Fetch();
            S.Set(("update_checked", J.Now()));
            if (r == null || r.Version <= Version.Parse(Settings.Version))
            {
                Available = null;
                if (manual) Say(L.T("update_latest", Settings.Version));
                return null;
            }
            Available = r;
            // automatic checks announce each version once; a manual check always answers
            if (manual || (J.Str(S.Get("update_notified")) != r.Tag && !hub.Muted))
            {
                S.Set(("update_notified", r.Tag));
                notifier.UpdateToast(L.T("update_title", r.Version), L.T("update_msg", Settings.Version));
            }
            return r;
        }
        catch (Exception e)
        {
            Log.W("update check failed: " + e.Message);
            if (manual) Say(L.T("update_failed", e.GetBaseException().Message));
            return null;
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

    public void OpenPage() => Hub.OpenTarget(Available?.Url ?? L.RepoUrl + "/releases/latest");

    void Say(string msg) => hub.Notify(new JsonObject { ["title"] = "TaskHub", ["message"] = msg, ["force"] = true, ["click"] = "dismiss" });
}
