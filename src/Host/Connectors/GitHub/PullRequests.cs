using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Pull requests, read-only (opt-in): "review requested from you" (any repository the token can see) and open Dependabot
/// PRs in the watched repositories, each shown as a task with a link - nothing is merged, approved or commented on.
/// A new one gets a notification; when it leaves the list (reviewed, merged, closed, the request removed) its task ends
/// quietly with what happened.
/// Review requests: the search API, `review-requested:&lt;login&gt;` (team requests included; a reviewer drops out after
/// reviewing) - its own limit of 30 requests a minute, so at most once a minute. Dependabot: the open PR list of each
/// watched repository (ETag: unchanged lists cost nothing), every 5 minutes. Both documented in docs.github.com
/// (search syntax / REST pulls); `@me` and several `repo:` in one REST search are not, so not used.
/// Config (connectors.github): pr_reviews, pr_dependabot (both off by default).
/// </summary>
sealed partial class GitHubConnector
{
    const string DependabotLogin = "dependabot[bot]";

    sealed class Pr
    {
        public string Kind = "", Repo = "", Title = "", Url = "", Author = ""; public int Number; public bool Draft;
        public string Key => Repo.ToLowerInvariant() + "#" + Number;
    }
    sealed class PullList { public string Etag; public List<Pr> Prs = new(); public double NextPoll; }
    readonly Dictionary<string, Pr> prs = new();                // PR tasks being shown: "owner/repo#n" -> PR
    readonly Dictionary<string, PullList> pullLists = new();    // repo -> its open PRs (Dependabot)
    double prNext, prPausedUntil;
    bool prAdopted;

    // review requests: two searches at most once a minute (30 a minute allowed); Dependabot lists: every 5 min
    int PrEvery(Conf c) => ApiUri.IsLoopback ? MinInterval : Math.Max(60, c.Interval);
    int DependabotEvery(Conf c) => ApiUri.IsLoopback ? MinInterval : Math.Max(300, c.Interval);

    static string PrTid(Pr p) =>
        "ghpr-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Key)))[..12].ToLowerInvariant();

    int PrCount() { lock (gate) return prs.Count; }

    /// <summary>After a restart: PR tasks still open are known again (no second notification; ended if they're gone).</summary>
    void AdoptPrs()
    {
        prAdopted = true;
        foreach (var t in hub.Brief())
        {
            if (!Hub.IsActive(J.Str(t["state"]))) continue;
            JsonObject full;
            try { full = hub.GetTask(J.Str(t["id"])); } catch { continue; }
            if (J.Str(full["source"]) != Id || full["meta"]?["github_pr"] is not JsonObject g) continue;
            var pr = new Pr { Kind = J.Str(g["kind"]), Repo = J.Str(g["repo"]), Number = (int)(J.Num(g["number"]) ?? 0), Title = J.Str(g["title"]) };
            if (pr.Number <= 0 || !RepoRx.IsMatch(pr.Repo) || pr.Kind is not ("review" or "dependabot")) continue;
            lock (gate) prs[pr.Key] = pr;
        }
    }

    /// <summary>The connector turned off: open PR tasks (also ones left from before a restart) end.</summary>
    void EndAllPrs()
    {
        AdoptPrs();
        List<Pr> all;
        lock (gate) { all = prs.Values.ToList(); prAdopted = false; }
        foreach (var p in all) EndPr(p, L.T("pr_end_off"));
    }

    async Task PollPrs(string token, Conf c, CancellationToken ct)
    {
        var now = J.Now();
        if (!prAdopted) AdoptPrs();
        // a kind turned off: its tasks end now - a review request that is also an open Dependabot PR stays, as that
        List<Pr> off, toDependabot = new();
        lock (gate)
        {
            off = prs.Values.Where(p => p.Kind == "review" ? !c.PrReviews : !c.PrDependabot).ToList();
            if (c.PrDependabot)
                foreach (var p in off.ToList())
                    if (p.Kind == "review" && pullLists.TryGetValue(p.Repo, out var pl) && pl.Prs.Any(x => x.Key == p.Key))
                    { off.Remove(p); toDependabot.Add(p); }
        }
        foreach (var p in off) EndPr(p, L.T("pr_end_off"));
        foreach (var p in toDependabot) { p.Kind = "dependabot"; RegisterPr(p); }
        if (!c.PrReviews && !c.PrDependabot) return;
        if (now < prNext || now < prPausedUntil || now < pausedUntil || string.IsNullOrEmpty(login)) return;
        prNext = now + PrEvery(c);

        var found = new Dictionary<string, Pr>();
        var asked = new HashSet<string>();   // the kinds whose lists fully answered: only those can end tasks
        if (c.PrDependabot)
        {
            bool all = true;
            foreach (var repo in c.Repos)
            {
                var list = await DependabotPrs(repo, token, c, ct);
                if (list == null) { all = false; continue; }
                foreach (var p in list) found[p.Key] = p;
            }
            lock (gate) foreach (var k in pullLists.Keys.Where(k => !c.Repos.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) pullLists.Remove(k);
            if (all) asked.Add("dependabot");
        }
        if (c.PrReviews)
        {
            var items = await SearchReviews(token, c, ct);
            if (items != null)
            {
                asked.Add("review");
                foreach (var p in items) found[p.Key] = p;   // a Dependabot PR that asks for your review: a review request
            }
            else if (found.Count > 0)   // the search didn't answer: review tasks stay as they are (not shown as Dependabot)
                lock (gate) foreach (var k in found.Keys.Where(k => prs.TryGetValue(k, out var o) && o.Kind == "review").ToList()) found.Remove(k);
        }

        List<Pr> fresh, gone;
        lock (gate)
        {
            fresh = found.Values.Where(p => !prs.ContainsKey(p.Key)).ToList();
            gone = prs.Values.Where(p => !found.ContainsKey(p.Key) && asked.Contains(p.Kind)).ToList();
        }
        foreach (var p in found.Values)
        {
            Pr old;
            lock (gate) prs.TryGetValue(p.Key, out old);
            if (old == null || (old.Title == p.Title && old.Kind == p.Kind && old.Draft == p.Draft)) continue;
            // renamed, became a review request (or no longer is one, still open as Dependabot's), out of draft
            old.Title = p.Title; old.Kind = p.Kind; old.Draft = p.Draft; old.Url = p.Url.Length > 0 ? p.Url : old.Url; old.Author = p.Author;
            RegisterPr(old);
        }
        foreach (var p in fresh) ShowPr(p);
        foreach (var p in gone) await GonePr(p, token, ct);
    }

    /// <summary>The open PRs Dependabot made in one repository (null: not answered). ETag: an unchanged list is free.</summary>
    async Task<List<Pr>> DependabotPrs(string repo, string token, Conf c, CancellationToken ct)
    {
        PullList pl;
        lock (gate) if (!pullLists.TryGetValue(repo, out pl)) pullLists[repo] = pl = new PullList();
        if (J.Now() < pl.NextPoll) return pl.Prs;
        var r = await Call(HttpMethod.Get, $"/repos/{repo}/pulls?state=open&per_page=100", pl.Etag, token, ct);
        if (r.Code == 304) { pl.NextPoll = J.Now() + DependabotEvery(c); return pl.Prs; }
        if (r.Limited) return null;
        if (r.Code == 401) { Rejected(c); return null; }
        if (r.Code is 403 or 404) { pl.NextPoll = J.Now() + DependabotEvery(c); pl.Etag = null; pl.Prs = new(); return pl.Prs; }   // no access: nothing to show
        if (r.Code != 200) return null;
        pl.Etag = r.ETag;
        pl.NextPoll = J.Now() + DependabotEvery(c);
        pl.Prs = (r.Body as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(j => J.Str(j["user"]?["login"]) == DependabotLogin)
            .Select(j => FromJson(j, J.Str(j["base"]?["repo"]?["full_name"]) is { Length: > 0 } fn ? fn : repo, "dependabot"))
            .Where(p => p != null).ToList();
        return pl.Prs;
    }

    /// <summary>PRs waiting for your review (up to 100, newest first); null when the search didn't answer.</summary>
    async Task<List<Pr>> SearchReviews(string token, Conf c, CancellationToken ct)
    {
        var q = "is:pr is:open archived:false review-requested:" + login;
        var r = await Call(HttpMethod.Get, "/search/issues?q=" + Uri.EscapeDataString(q) + "&sort=created&order=desc&per_page=100", null, token, ct);
        if (r.Limited) return null;
        if (r.Code == 401) { Rejected(c); return null; }
        if (r.Code != 200) { Log.W($"github: review search HTTP {r.Code}"); return null; }
        if (J.Truthy(r.Body?["incomplete_results"])) return null;   // timed out on GitHub's side: not a reason to end tasks
        var list = new List<Pr>();
        foreach (var it in (r.Body?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (it["pull_request"] is null) continue;
            var repoUrl = J.Str(it["repository_url"]);
            var i = repoUrl.IndexOf("/repos/", StringComparison.Ordinal);
            if (FromJson(it, i >= 0 ? repoUrl[(i + 7)..] : "", "review") is Pr p) list.Add(p);
        }
        return list;
    }

    static Pr FromJson(JsonObject j, string repo, string kind)
    {
        int n = (int)(J.Num(j["number"]) ?? 0);
        if (n <= 0 || !RepoRx.IsMatch(repo)) return null;
        return new Pr
        {
            Kind = kind, Repo = repo, Number = n, Title = J.Cut(J.Str(j["title"]), 300), Url = J.Str(j["html_url"]),
            Author = J.Str(j["user"]?["login"]), Draft = J.Truthy(j["draft"]),
        };
    }

    JsonObject PrTask(Pr p)
    {
        bool review = p.Kind == "review";
        var b = new JsonObject
        {
            ["id"] = PrTid(p), ["name"] = J.Cut((review ? L.T("pr_review_name") : "Dependabot") + " · " + p.Title, 200),
            ["source"] = Id, ["project"] = p.Repo.Split('/')[1],
            ["description"] = string.Join(" · ", new[] { p.Repo + " #" + p.Number, p.Author.Length > 0 ? "@" + p.Author : "", p.Draft ? L.T("pr_draft") : "" }
                .Where(x => x.Length > 0)),
            ["attention"] = review ? L.T("pr_review_attention") : "",
            ["notify"] = new JsonObject { ["finish"] = false, ["fail"] = false, ["stale"] = false },
            ["meta"] = new JsonObject
            {
                ["github_pr"] = new JsonObject { ["kind"] = p.Kind, ["repo"] = p.Repo, ["number"] = p.Number, ["title"] = p.Title },
            },
        };
        if (Uri.TryCreate(p.Url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.IsLoopback))
        {
            b["links"] = new JsonArray(new JsonObject { ["label"] = "GitHub", ["url"] = p.Url });
            b["controls"] = new JsonArray(new JsonObject
            {
                ["id"] = "open", ["label"] = L.T("pr_open_btn"), ["kind"] = "open", ["target"] = p.Url, ["states"] = J.StrArray(Hub.AllStates),
            });
        }
        return b;
    }

    /// <summary>Registers (or re-registers: renamed, another kind) the PR's task with its status; a new one also gets a
    /// log line and a notification.</summary>
    void RegisterPr(Pr p, string news = null)
    {
        Try(() =>
        {
            hub.Register(PrTask(p), trusted: true);
            var u = new JsonObject { ["status"] = p.Kind == "review" ? L.T("pr_waiting_you") : L.T("pr_open") };
            if (news != null) { u["log"] = news; u["toast"] = news; }
            hub.Update(PrTid(p), u, trusted: true);
        });
    }

    void ShowPr(Pr p)
    {
        lock (gate) prs[p.Key] = p;
        var where = p.Repo + " #" + p.Number;
        RegisterPr(p, p.Kind == "review" ? L.T("pr_new_review", where) : L.T("pr_new_dependabot", where));
    }

    /// <summary>Left the list: merged / closed / (still open) no longer waiting for you - read from the PR itself.</summary>
    async Task GonePr(Pr p, string token, CancellationToken ct)
    {
        string why = p.Kind == "review" ? L.T("pr_end_reviewed") : L.T("pr_end_gone");
        try
        {
            var r = await Call(HttpMethod.Get, $"/repos/{p.Repo}/pulls/{p.Number}", null, token, ct);
            if (r.Code == 200 && r.Body is JsonObject j)
            {
                if (J.Str(j["merged_at"]).Length > 0) why = L.T("pr_end_merged");
                else if (J.Str(j["state"]) == "closed") why = L.T("pr_end_closed");
            }
        }
        catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested)
        { Log.W("github: PR state not read: " + e.GetBaseException().Message); }
        EndPr(p, why);
    }

    void EndPr(Pr p, string why)
    {
        lock (gate) prs.Remove(p.Key);
        Try(() => hub.Finish(PrTid(p), new JsonObject { ["state"] = "done", ["message"] = why }));
    }
}
