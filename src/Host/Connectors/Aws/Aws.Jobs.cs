using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>
/// AWS long jobs, each behind its own switch (off by default; they need extra permissions): SageMaker training jobs and AWS
/// Batch jobs become tasks. SageMaker reports no percent for ordinary training, so none is shown; the phase and the elapsed
/// time are. An array job's real progress comes from its children's counts. Jobs finished before the switch was turned on
/// never appear. Job names and failure reasons are the service's text: cut, shown as text. A Region or service that refuses
/// or fails declares nothing finished.
/// </summary>
sealed partial class AwsConnector
{
    const double DefaultJobInterval = 300, MinJobInterval = 120, NotOfferedRetry = 3600;
    const int MaxJobPages = 20, MaxQueues = 50, MaxDescribes = 10;
    const string SmTarget = "SageMaker.", Json11 = "application/x-amz-json-1.1";
    // forward order: a job only moves forward, so one moving during the scan is seen again later, never missed
    static readonly string[] BatchUnfinished = { "SUBMITTED", "PENDING", "RUNNABLE", "STARTING", "RUNNING" };

    sealed class Job
    {
        public string Kind, Id, Name, Region, Queue = "", Raw = "", Phase = "", Reason = "";
        public double? Created, Started, Ended, MaxRuntime, Progress;
        public int? ExitCode;
        public bool Cancelled, Described;
        public bool Active => Kind == "sm" ? Raw is "InProgress" or "Stopping" : Raw is not ("SUCCEEDED" or "FAILED");
        public bool HitLimit => Kind == "sm" && Phase is "MaxRuntimeExceeded" or "MaxWaitTimeExceeded";
        // a job being deleted keeps what it ended as: the secondary status still says
        public string Final => Kind != "sm"
            ? Raw == "FAILED" ? (Cancelled ? "cancelled" : "failed") : "done"
            : HitLimit || Raw == "Failed" || Phase == "Failed" ? "failed"
            : Raw == "Stopped" || Phase == "Stopped" ? "cancelled" : "done";
    }

    sealed class JobTrack { public Job J; public string LastSig; public int Missed; public double Asked; }

    sealed class JobPoll : RegionState { public string Service; public double LastRead; public readonly HashSet<string> Gone = new(); }

    sealed record JsonReply(Verdict V, string Code, int Http, JsonObject Body);

    readonly Dictionary<string, JobTrack> jobs = new();                                  // task id -> job
    readonly Dictionary<(string Region, string Service), JobPoll> jobPolls = new();

    bool JobsOn(string service) => J.Truthy(Cfg[service]);
    double JobInterval => Math.Clamp(J.Num(Cfg["job_interval"]) ?? DefaultJobInterval, Mock ? 0.2 : MinJobInterval, 3600);
    double JobMargin => Mock ? 0.5 : 300;   // our clock against theirs
    double JobsSince(string service) => J.Num(Cfg[service + "_since"]) ?? J.Now();

    /// <summary>A switch and interval from the page or API. Turning a switch on starts its "since"; off lets its jobs go.</summary>
    void ConfigureJobs(JsonObject b, JsonObject c)
    {
        if (b.ContainsKey("job_interval"))
        {
            double lo = Mock ? 0.2 : MinJobInterval;
            if (J.Num(b["job_interval"]) is not double d || d < lo || d > 3600) throw new ApiError(400, $"job_interval must be {J.Fmt(lo)}-3600");
            c["job_interval"] = d;
        }
        foreach (var svc in new[] { "sagemaker", "batch" })
        {
            if (!b.ContainsKey(svc)) continue;
            bool on = J.Truthy(b[svc]);
            if (on && !J.Truthy(c[svc])) c[svc + "_since"] = J.Now();
            c[svc] = on;
        }
    }

    /// <summary>A poll per watched Region and switched-on service; the ones switched off go.</summary>
    void SyncJobPolls()
    {
        var now = J.Now();
        var services = new[] { "sagemaker", "batch" }.Where(JobsOn).ToList();
        lock (gate)
        {
            foreach (var k in jobPolls.Keys.Where(k => !services.Contains(k.Service) || !regions.ContainsKey(k.Region)).ToList()) jobPolls.Remove(k);
            int i = 0;
            foreach (var r in regions.Keys)
                foreach (var svc in services)
                {
                    if (!jobPolls.ContainsKey((r, svc))) jobPolls[(r, svc)] = new JobPoll { Service = svc, NextAt = now + (Mock ? 0 : 3 * i + 5) };
                    i++;
                }
        }
        Release(_ => false, j => !services.Contains(j.Kind == "sm" ? "sagemaker" : "batch"));
    }

    async Task PollJobs(string region, JobPoll p, SigV4.Creds creds, CancellationToken ct)
    {
        var found = new List<Job>();
        JsonReply bad = null;
        p.Gone.Clear();
        try
        {
            bad = p.Service == "sagemaker"
                ? await ReadTraining(region, p, creds, found, ct)
                : await ReadBatch(region, p, creds, found, ct);
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound })
        {
            // no such host: this service isn't offered in this Region, if that Region's EC2 answers. Otherwise it's the network
            lock (gate)
            {
                if (regions.GetValueOrDefault(region)?.State == "ok")
                {
                    if (jobPolls.GetValueOrDefault((region, p.Service)) == p) { p.State = "not_offered"; p.Code = ""; p.NextAt = J.Now() + NotOfferedRetry; }
                    return;
                }
            }
            bad = new JsonReply(Verdict.Server, "network", 0, null);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            bad = new JsonReply(Verdict.Server, "network", 0, null);
        }
        var now = J.Now();
        lock (gate)
        {
            if (jobPolls.GetValueOrDefault((region, p.Service)) != p) return;   // no longer watched
            if (bad != null)
            {
                p.Code = bad.Code;
                switch (bad.V)
                {
                    case Verdict.BadKey or Verdict.Denied: p.State = "no_access"; p.Fails = 0; p.NextAt = now + (Mock ? 2 : NoAccessRetry); break;
                    case Verdict.Clock: p.State = "clock"; p.Fails = 0; p.NextAt = now + (Mock ? 2 : 300); break;
                    case Verdict.Throttled: p.State = "throttled"; p.NextAt = now + Backoff(++p.Fails); break;
                    default: p.State = "error"; p.NextAt = now + Backoff(++p.Fails); break;
                }
            }
            else
            {
                p.State = "ok"; p.Code = ""; p.Fails = 0; p.Count = found.Count(j => j.Active); p.ReadAt = now; p.NextAt = now + JobInterval;
            }
        }
        if (bad != null) { Log.W($"aws: {region} {p.Service}: {bad.Code}"); return; }
        lock (gate) if (jobPolls.GetValueOrDefault((region, p.Service)) != p) return;   // switched off meanwhile
        ReconcileJobs(region, p, found);
        lock (gate) p.LastRead = now;
        Forget(p.Service);
        AdvanceSince(p.Service);
    }

    /// <summary>Moves "since" up once every Region that can be read has been, at most every 10 minutes: a finished job the user
    /// cleared doesn't come back after a restart, and one that finished while TaskHub was off still shows. A Region refusing
    /// or not offering the service doesn't hold it back: nothing can be seen there anyway.</summary>
    void AdvanceSince(string service)
    {
        double oldest;
        lock (gate)
        {
            var polls = jobPolls.Where(k => k.Key.Service == service && k.Value.State is not ("not_offered" or "no_access")).Select(k => k.Value).ToList();
            if (polls.Count == 0 || polls.Any(x => x.LastRead <= 0)) return;
            oldest = polls.Min(x => x.LastRead);
        }
        var since = oldest - JobMargin;
        if (since - JobsSince(service) < (Mock ? 1 : 600)) return;
        lock (configLock)
        {
            if (!JobsOn(service)) return;
            var c = Cfg;
            c[service + "_since"] = since;
            Connectors.SetConfig(S, Id, c);
        }
    }

    /// <summary>A finished job past "since" can't come back from the lists: nothing to remember it by.</summary>
    void Forget(string service)
    {
        var since = JobsSince(service);
        var kind = service == "sagemaker" ? "sm" : "batch";
        lock (gate)
            foreach (var (tid, t) in jobs.Where(kv => kv.Value.J.Kind == kind && !kv.Value.J.Active && (kv.Value.J.Ended ?? kv.Value.J.Created) < since).ToList())
                jobs.Remove(tid);
    }

    // ------------------------------------------------------------ SageMaker
    /// <summary>Everything in progress, plus what changed since the last read (a job may start and end between two reads).
    /// MaxResults applies before the status filter: pages can be empty, and a long history uses up the page limit. What we
    /// follow and didn't see is asked by name, so it can't be stuck; only "no such job" lets it go.</summary>
    async Task<JsonReply> ReadTraining(string region, JobPoll p, SigV4.Creds c, List<Job> found, CancellationToken ct)
    {
        var since = JobsSince("sagemaker");
        var after = p.LastRead > 0 ? Math.Max(since, p.LastRead - JobMargin) : since;
        foreach (var query in new[]
        {
            new JsonObject { ["StatusEquals"] = "InProgress" },
            new JsonObject { ["LastModifiedTimeAfter"] = Math.Floor(after) },
        })
        {
            string token = null;
            for (int page = 0; page < MaxJobPages; page++)
            {
                var body = (JsonObject)query.DeepClone();
                body["SortBy"] = "CreationTime"; body["SortOrder"] = "Descending"; body["MaxResults"] = 100;
                if (token != null) body["NextToken"] = token;
                var r = await SageMaker(c, region, "ListTrainingJobs", body, ct);
                if (r.V != Verdict.Ok) return r;
                foreach (var s in (r.Body["TrainingJobSummaries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var j = ParseTraining(s, region);
                    if (j == null || !Wanted(j, since)) continue;
                    found.RemoveAll(x => x.Id == j.Id);   // the later list has the newer state
                    found.Add(j);
                }
                token = J.Str(r.Body["NextToken"]) is { Length: > 0 } nt ? nt : null;
                if (token == null) break;
            }
        }
        // the followed ones not seen first, the longest not asked first; then new jobs (the time limit), failed ones (the reason)
        var unseen = NotSeen("sm", region, found);
        var ask = unseen.Concat(found.Where(NeedsDescribe)).Take(MaxDescribes).ToList();
        foreach (var j in ask)
        {
            MarkAsked(j);
            var r = await SageMaker(c, region, "DescribeTrainingJob", new JsonObject { ["TrainingJobName"] = j.Id }, ct);
            if (r.V == Verdict.Ok && ParseTraining(r.Body, region) is { } d)
            {
                d.Described = true;
                d.MaxRuntime = J.Num(r.Body["StoppingCondition"]?["MaxRuntimeInSeconds"]);
                d.Started = J.Num(r.Body["TrainingStartTime"]) ?? d.Created;
                d.Ended = J.Num(r.Body["TrainingEndTime"]) ?? d.Ended;
                d.Reason = CleanText(J.Str(r.Body["FailureReason"]));
                found.RemoveAll(x => x.Id == d.Id);
                found.Add(d);
            }
            else if (r.Code == "ResourceNotFound") p.Gone.Add(j.Id);
            else if (r.V is Verdict.Throttled or Verdict.Server) break;   // the rest at the next read
            else j.Described = true;                                     // refused: the lists still tell its state; not asked again
        }
        return null;
    }

    /// <summary>Running always; a finished one only if it ended after "since", or is one we follow.</summary>
    bool Wanted(Job j, double since) => j.Active || (j.Ended ?? j.Created) >= since || Followed(j);

    bool NeedsDescribe(Job j)
    {
        lock (gate)
            return !jobs.TryGetValue(JobTid(j), out var t) || !t.J.Described || (j.Final == "failed" && !j.Active && t.J.Active);   // once, for the reason
    }

    /// <summary>Followed and still running by our last word, but in no list now; the longest not asked first.</summary>
    List<Job> NotSeen(string kind, string region, List<Job> found)
    {
        var seen = found.Select(j => j.Id).ToHashSet();
        lock (gate)
            return jobs.Values.Where(t => t.J.Kind == kind && t.J.Region == region && t.J.Active && !seen.Contains(t.J.Id))
                .OrderBy(t => t.Asked).Select(t => t.J).ToList();
    }

    void MarkAsked(Job j)
    {
        lock (gate) if (jobs.TryGetValue(JobTid(j), out var t)) t.Asked = J.Now();
    }

    static Job ParseTraining(JsonObject s, string region)
    {
        var name = J.Str(s["TrainingJobName"]);
        if (!Regex.IsMatch(name, @"^[a-zA-Z0-9-]{1,63}\z")) return null;
        return new Job
        {
            Kind = "sm", Id = name, Name = name, Region = region, Raw = J.Cut(J.Str(s["TrainingJobStatus"]), 20), Phase = J.Cut(J.Str(s["SecondaryStatus"]), 40),
            Created = J.Num(s["CreationTime"]), Started = J.Num(s["CreationTime"]), Ended = J.Num(s["TrainingEndTime"]),
        };
    }

    Task<JsonReply> SageMaker(SigV4.Creds c, string region, string action, JsonObject body, CancellationToken ct) =>
        JsonCall(c, region, "sagemaker", "/", SmTarget + action, Json11, body, ct);

    // ------------------------------------------------------------ Batch
    /// <summary>The queues, then per queue: each unfinished status, and what was created lately in any status (a filtered
    /// list, not the whole SUCCEEDED history, which can be long). Followed jobs not seen are asked by id: an id no longer known
    /// lets the job go. A queue refusing (a SageMaker-type queue has its own API) doesn't lose the others.</summary>
    async Task<JsonReply> ReadBatch(string region, JobPoll p, SigV4.Creds c, List<Job> found, CancellationToken ct)
    {
        var queues = new List<string>();
        string token = null;
        do
        {
            var r = await JsonCall(c, region, "batch", "/v1/describejobqueues", null, "application/json", token == null ? new JsonObject() : new JsonObject { ["nextToken"] = token }, ct);
            if (r.V != Verdict.Ok) return r;
            foreach (var q in (r.Body["jobQueues"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (J.Str(q["jobQueueName"]) is { Length: > 0 and <= 128 } n && J.Str(q["status"]) is not ("DELETED" or "DELETING") && J.Str(q["jobQueueType"]) != "SAGEMAKER_TRAINING") queues.Add(n);
            token = J.Str(r.Body["nextToken"]) is { Length: > 0 } nt ? nt : null;
        } while (token != null && queues.Count < MaxQueues);
        var since = JobsSince("batch");
        var recent = new JsonArray(new JsonObject { ["name"] = "AFTER_CREATED_AT", ["values"] = new JsonArray(((long)((since - JobMargin) * 1000)).ToString()) });
        var byId = new Dictionary<string, Job>();
        foreach (var q in queues.Take(MaxQueues))
        {
            bool refused = false;
            foreach (var status in BatchUnfinished.Append(null))   // null: the recent ones, last, so the newest state wins
            {
                token = null;
                for (int page = 0; page < MaxJobPages && !refused; page++)
                {
                    var body = new JsonObject { ["jobQueue"] = q, ["maxResults"] = 100 };
                    if (status != null) body["jobStatus"] = status; else body["filters"] = recent.DeepClone();
                    if (token != null) body["nextToken"] = token;
                    var r = await JsonCall(c, region, "batch", "/v1/listjobs", null, "application/json", body, ct);
                    if (r.V == Verdict.Other) { Log.W($"aws: {region} batch: a queue refused: {r.Code}"); refused = true; break; }
                    if (r.V != Verdict.Ok) return r;
                    foreach (var s in (r.Body["jobSummaryList"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        if (ParseBatch(s, region, q) is { } j && Wanted(j, since)) byId[j.Id] = j;
                    token = J.Str(r.Body["nextToken"]) is { Length: > 0 } nt ? nt : null;
                    if (token == null) break;
                }
                if (refused) break;
            }
        }
        found.AddRange(byId.Values);
        var unseen = NotSeen("batch", region, found).Take(100).ToList();
        if (unseen.Count > 0)
        {
            foreach (var j in unseen) MarkAsked(j);
            var r = await JsonCall(c, region, "batch", "/v1/describejobs", null, "application/json", new JsonObject { ["jobs"] = J.StrArray(unseen.Select(j => j.Id)) }, ct);
            if (r.V == Verdict.Ok)
            {
                var known = new HashSet<string>();
                foreach (var s in (r.Body["jobs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (J.Str(s["jobId"]) is { Length: > 0 } id && unseen.FirstOrDefault(x => x.Id == id) is { } was && ParseBatch(s, region, was.Queue) is { } j) { known.Add(id); found.Add(j); }
                foreach (var j in unseen) if (!known.Contains(j.Id)) p.Gone.Add(j.Id);
            }
            // refused or failing: nothing is declared gone
        }
        return null;
    }

    bool Followed(Job j)
    {
        lock (gate) return jobs.ContainsKey(JobTid(j));
    }

    static Job ParseBatch(JsonObject s, string region, string queue)
    {
        var status = J.Str(s["status"]);
        var id = J.Str(s["jobId"]);
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9:-]{1,64}\z")) return null;
        double? Ms(string k) => J.Num(s[k]) is double d ? d / 1000 : null;
        var j = new Job
        {
            Kind = "batch", Id = id, Name = J.Cut(J.Str(s["jobName"], id), 128), Region = region, Queue = J.Cut(queue, 128), Raw = status, Phase = status,
            Created = Ms("createdAt"), Started = Ms("startedAt") ?? Ms("createdAt"), Ended = Ms("stoppedAt"),
            Cancelled = J.Truthy(s["isCancelled"]) || J.Truthy(s["isTerminated"]),
            ExitCode = J.Num(s["container"]?["exitCode"]) is double ec ? (int)ec : null,
            Reason = CleanText(new[] { J.Str(s["statusReason"]), J.Str(s["container"]?["reason"]) }.FirstOrDefault(x => x.Length > 0) ?? ""),
        };
        // an array job: finished children of all children is real progress
        // (a parent stays PENDING while its children run: the children's counts say what is going on)
        if (s["arrayProperties"] is JsonObject ap && J.Num(ap["size"]) is double size && size > 0 && ap["statusSummary"] is JsonObject sum)
        {
            j.Progress = Math.Clamp(((J.Num(sum["SUCCEEDED"]) ?? 0) + (J.Num(sum["FAILED"]) ?? 0)) / size, 0, 1);
            if (j.Active && (J.Num(sum["RUNNING"]) ?? 0) > 0) j.Phase = "RUNNING";
        }
        return j;
    }

    /// <summary>The service's text for a task: one line, cut, without what identifies the account (it also reaches the log and,
    /// if turned on, Telegram).</summary>
    static string CleanText(string s)
    {
        s = Regex.Replace(s ?? "", @"\barn:aws[a-z-]*:\S*", "[ARN]", RegexOptions.CultureInvariant);
        s = Regex.Replace(s, @"\b\d{12}\b", "[account]", RegexOptions.CultureInvariant);
        return J.Cut(Regex.Replace(s, @"\s+", " ").Trim(), 500);
    }

    // ------------------------------------------------------------ the wire
    Uri ServiceUri(string region, string service, string path)
    {
        if (!RegionRx.IsMatch(region)) throw new InvalidOperationException("not a Region name");
        var host = service == "sagemaker" ? $"api.sagemaker.{region}.amazonaws.com" : $"{service}.{region}.amazonaws.com";
        return new Uri((Mock ? Api.GetLeftPart(UriPartial.Authority) : "https://" + host) + path);
    }

    async Task<JsonReply> JsonCall(SigV4.Creds c, string region, string service, string path, string target, string contentType, JsonObject body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        using var m = new HttpRequestMessage(HttpMethod.Post, ServiceUri(region, service, path)) { Content = new ByteArrayContent(bytes) };
        m.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (target != null) m.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        SigV4.Apply(m, bytes, c, region, service, DateTime.UtcNow);
        using var r = await http.Send(m, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        JsonObject o = null;
        try { o = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { }
        if (r.IsSuccessStatusCode) return o != null ? new JsonReply(Verdict.Ok, "", (int)r.StatusCode, o) : new JsonReply(Verdict.Other, "unreadable answer", (int)r.StatusCode, null);
        var code = "";
        if (r.Headers.TryGetValues("X-Amzn-Errortype", out var hv)) code = NormCode(hv.FirstOrDefault());
        if (code.Length == 0) code = ErrorCode(null, text);
        var v = Classify(code, (int)r.StatusCode, r.Headers.Date);
        // Batch's general client error also covers a missing permission: its text is read for that, and never shown
        if (code == "ClientException" && (J.Str(o?["message"] ?? o?["Message"]).Contains("not authorized", StringComparison.OrdinalIgnoreCase))) v = Verdict.Denied;
        return new JsonReply(v, code.Length > 0 ? code : "HTTP " + (int)r.StatusCode, (int)r.StatusCode, null);
    }

    // ------------------------------------------------------------ jobs -> tasks
    /// <summary>A task id: a SageMaker name is unique only per Region and can be longer than an id may be.</summary>
    static string JobTid(Job j)
    {
        if (j.Kind == "batch") return "aws-b-" + Regex.Replace(j.Id, @"[^A-Za-z0-9_.-]", "_");
        var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(j.Region + "/" + j.Id)))[..8].ToLowerInvariant();
        return "aws-sm-" + J.Cut(j.Id, 40) + "-" + h;
    }

    void ReconcileJobs(string region, JobPoll p, List<Job> list)
    {
        bool firstRead = p.LastRead == 0;
        var seen = new HashSet<string>();
        foreach (var j in list)
        {
            var tid = JobTid(j);
            seen.Add(tid);
            JobTrack t;
            string prev;
            lock (gate)
            {
                if (!jobs.TryGetValue(tid, out t)) jobs[tid] = t = new JobTrack { J = j };
                prev = t.J == j ? null : t.J.Raw;
                if (t.J != j)
                {
                    // the list carries less than the job's own answer: keep what that told
                    j.Described |= t.J.Described; j.MaxRuntime ??= t.J.MaxRuntime;
                    if (j.Reason.Length == 0) j.Reason = t.J.Reason;
                    t.J = j;
                }
                t.Missed = 0;
            }
            JobTransition(t, prev, firstRead);
        }
        // only the job's own answer says it's gone, twice in a row
        var service = p.Service == "sagemaker" ? "sm" : "batch";
        List<Job> gone;
        lock (gate)
            gone = jobs.Values.Where(t => t.J.Kind == service && t.J.Region == region && t.J.Active && p.Gone.Contains(t.J.Id) && ++t.Missed >= 2).Select(t => t.J).ToList();
        if (gone.Count > 0) Release(_ => false, gone.Contains);
    }

    void JobTransition(JobTrack t, string prev, bool firstRead)
    {
        var j = t.J;
        var tid = JobTid(j);
        var task = TryTask(tid);
        bool active = task != null && Hub.IsActive(J.Str(task["state"]));
        if (j.Active)
        {
            if (!active) RegisterJob(t, quiet: false);
            ShowJob(t, prev != j.Raw);
            return;
        }
        if (task == null)
        {
            // finished before we looked. After a restart that's history, not news: no toast
            if (prev != null) return;
            RegisterJob(t, quiet: firstRead);
            active = true;
        }
        if (!active) return;
        ShowJob(t, true);
        var message = j.Reason.Length > 0 ? j.Reason : j.HitLimit ? L.T("aws_stopped_limit") : j.ExitCode is int ec && ec != 0 ? L.T("aws_exit_code", ec) : j.Final == "done" ? L.T("done") : "";
        Try(() => hub.Finish(tid, new JsonObject { ["state"] = j.Final, ["message"] = message }));
        if (firstRead && prev == null) hub.MarkSeen(new[] { tid });
    }

    void RegisterJob(JobTrack t, bool quiet)
    {
        var j = t.J;
        var b = new JsonObject
        {
            ["id"] = JobTid(j), ["name"] = J.Cut(j.Name, 200), ["source"] = Id, ["project"] = Title, ["started"] = j.Started,
            ["notify"] = new JsonObject { ["finish"] = !quiet, ["fail"] = !quiet, ["stale"] = false },
            ["meta"] = JobMeta(j),
            ["widgets"] = new JsonArray(new JsonObject { ["id"] = "info", ["type"] = "kv" }),
        };
        try { hub.Register(b, trusted: true); }
        catch (Exception e) { Log.W($"aws: register {JobTid(j)}: " + e.Message); return; }
        t.LastSig = null;
    }

    JsonObject JobMeta(Job j) => new()
    {
        [Id] = new JsonObject
        {
            ["kind"] = j.Kind, ["id"] = j.Id, ["name"] = j.Name, ["region"] = j.Region, ["queue"] = j.Queue, ["state"] = j.Raw, ["created"] = j.Created,
        },
    };

    void ShowJob(JobTrack t, bool force)
    {
        var j = t.J;
        bool sm = j.Kind == "sm";
        var info = new JsonObject();
        void Kv(string k, string v) { if (!string.IsNullOrEmpty(v)) info[k] = v; }
        Kv(L.T("m_region"), j.Region);
        Kv(L.T("aws_queue"), j.Queue);
        Kv(L.T("aws_state"), j.Phase);
        if (j.MaxRuntime is double mr && mr > 0) Kv(L.T("aws_time_limit"), Hub.FmtDur(mr));
        var links = new JsonArray(new JsonObject
        {
            ["label"] = L.T("aws_console"),
            ["url"] = $"https://{j.Region}.console.aws.amazon.com/{(sm ? "sagemaker" : "batch")}/home?region={j.Region}",
        });
        var u = new JsonObject
        {
            ["name"] = J.Cut(j.Name, 200),
            ["description"] = SettingsRows.Line(sm ? "SageMaker" : "AWS Batch", j.Queue, j.Region),
            ["status"] = J.Cut(j.Phase, 60),
            ["links"] = links,
            ["meta"] = JobMeta(j),
        };
        if (j.Progress is double pr) u["progress"] = pr;
        var sig = J.Dump(u) + J.Dump(info);
        if (!force && sig == t.LastSig) return;
        t.LastSig = sig;
        u["widgets"] = new JsonObject { ["info"] = info };
        Try(() => hub.Update(JobTid(j), u, trusted: true));
    }

    /// <summary>The line for a service no Region can be read for: only then is it the user's to fix.</summary>
    List<string> JobNotes()
    {
        lock (gate)
            return jobPolls.Where(k => k.Value.State != "not_offered").GroupBy(k => k.Key.Service)
                .Where(g => g.All(x => x.Value.State == "no_access"))
                .Select(g => L.T("aws_jobs_denied", g.Key == "sagemaker" ? "SageMaker" : "AWS Batch")).ToList();
    }
}
