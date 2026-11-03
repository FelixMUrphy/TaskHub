using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>Controls and commands: trigger (signal / shell / open / kill / connector), value checks, command queue.</summary>
sealed partial class Hub
{
    // ------------------------------------------------------------ controls & commands
    /// <summary>A "connector" control pressed: (source, task, control, value, via) -> the connector that made the task.
    /// via: null from the HTTP API (also the panel's ordinary buttons), "toast", "telegram", or "panel" (the panel's WebView2
    /// bridge - the only way to panel_only controls).</summary>
    public Func<string, string, string, JsonNode, string, JsonObject> ConnectorControl;

    public JsonObject Trigger(string tid, string cid, JsonObject b, string via = null)
    {
        var value = J.C(b["value"]);
        JsonObject c;
        int? pid;
        long? ct;
        string source;
        lock (Lock)
        {
            var t = Get(tid);
            c = (t["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(x => J.Str(x["id"]) == cid);
            if (c == null) throw new ApiError(404, $"control '{cid}' not found") { Stale = true };
            if (J.Str(c["kind"]) == "kill" && J.Num(t["pid"]) is double kp && kp > 0 && In(tid).Ct == null)
                throw new ApiError(409, "kill refused: the process's creation time wasn't known at registration, so the pid can't be told from a reused one");
            if (J.Truthy(c["panel_only"]) && via != "panel")
                throw new ApiError(403, $"control '{cid}' works only in the TaskHub panel, not through the HTTP API, toasts or Telegram");
            var states = (c["states"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).ToList();
            if (!states.Contains(St(t))) throw new ApiError(409, $"control '{cid}' not available in state '{St(t)}'") { Stale = true };
            value = CheckValue(c["input"] as JsonObject, value);
            var label = J.Str(c["label"]) + (value != null ? " = " + J.Dump(value) : "") + (via != null ? $" ({via})" : "");
            if (value != null)
            {
                c["last_value"] = J.C(value);   // next toast / panel form starts from the current value
                Notifier?.Refresh(tid);         // and toasts already on screen / in the notification center follow it
            }
            if (J.Str(c["kind"]) == "signal")
            {
                var cmd = new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString("N")[..10], ["control"] = cid, ["value"] = J.C(value),
                    ["t"] = J.Now(), ["status"] = "pending", ["reply"] = null,
                };
                In(tid).CmdQ.Add((JsonObject)cmd.DeepClone());
                var hist = (t["commands"] as JsonArray ?? new JsonArray()).Select(x => x.DeepClone()).ToList();
                hist = hist.Skip(Math.Max(0, hist.Count - 19)).ToList();
                hist.Add(cmd.DeepClone());
                t["commands"] = new JsonArray(hist.ToArray());
                AddLog(t, "⇢ " + label, "cmd");
                Bump();
                return new JsonObject { ["ok"] = true, ["command"] = cmd };
            }
            AddLog(t, "⇢ " + label, "cmd");
            Bump();
            pid = J.Num(t["pid"]) is double pd ? (int)pd : null;
            ct = In(tid).Ct;
            c = (JsonObject)c.DeepClone();
            source = J.Str(t["source"]);
        }
        switch (J.Str(c["kind"]))
        {
            case "connector":
                if (ConnectorControl == null) throw new ApiError(503, "connectors not available");
                return ConnectorControl(source, tid, cid, value, via);
            case "kill":
                if (pid is not > 0) throw new ApiError(400, "task has no pid");
                var kr = KillTree(pid.Value, ct, out var why);
                if (kr == KillResult.Failed)
                {
                    // still running: the task stays as it is (an elevated process can't be stopped from here)
                    lock (Lock) { if (tasks.TryGetValue(tid, out var kt)) { AddLog(kt, $"kill failed (pid {pid}): {why}", "error"); Bump(); } }
                    throw new ApiError(500, $"kill failed, pid {pid} is still running: {why}"
                        + (why.Contains("denied", StringComparison.OrdinalIgnoreCase) ? " (does it run elevated?)" : ""));
                }
                Finish(tid, new JsonObject
                {
                    ["state"] = "cancelled",
                    ["message"] = kr == KillResult.Killed ? $"killed from panel (pid {pid})" : $"process had already ended (pid {pid})",
                });
                return new JsonObject { ["ok"] = true };
            case "open":
                try { OpenTarget(J.Str(c["target"])); }
                catch (Exception e) { throw new ApiError(500, "open failed: " + e.Message); }
                return new JsonObject { ["ok"] = true };
            case "shell":
                var v = value;
                _ = Task.Run(() => RunShell(tid, c, v, pid));
                return new JsonObject { ["ok"] = true, ["started"] = true };
        }
        throw new ApiError(400, "unknown kind");
    }

    /// <summary>A control value checked against the control's input (same rules for the panel, toasts, Telegram and the
    /// API): numbers finite and within min/max, bools real bools, select values one of the options, text a string.</summary>
    static JsonNode CheckValue(JsonObject inp, JsonNode v)
    {
        if (inp == null || v == null) return v;
        switch (J.Str(inp["type"]))
        {
            case "number":
                if (J.Num(v) is not double d) throw new ApiError(400, "value must be a number");
                if ((J.Num(inp["min"]) is double mn && d < mn) || (J.Num(inp["max"]) is double mx && d > mx))
                    throw new ApiError(400, $"value {J.Fmt(d)} out of range");
                return d;
            case "bool":
                if (!J.IsBool(v)) throw new ApiError(400, "value must be true or false");
                return v;
            case "select":
                var s = J.Str(v);
                if (!(inp["options"] as JsonArray ?? new JsonArray()).Any(o => J.Str(o) == s))
                    throw new ApiError(400, $"'{J.Cut(s, 60)}' is not one of the options");
                return s;
            default:
                return J.Cut(J.Str(v), 2000);
        }
    }

    public enum KillResult { Killed, Gone, Failed }

    /// <summary>Kills pid and its children - only if it is still the process that registered (creation time ct, when
    /// known): a pid reused by an unrelated process after the task's own one exited is left alone (Gone). Failed only
    /// when the process is still alive afterwards (e.g. access denied: it runs elevated); a child that couldn't be
    /// killed while the process itself is gone counts as killed.</summary>
    public static KillResult KillTree(int pid, long? ct, out string why)
    {
        why = "";
        try
        {
            using var p = Process.GetProcessById(pid);
            // open and keep one handle: the identity check and the kill go through it, so the pid can't be reused in between
            _ = p.SafeHandle;
            if (ct is long want && Math.Abs(p.StartTime.ToFileTimeUtc() - want) > 10_000_000)
            {
                Log.W("hub", $"kill {pid}: pid now belongs to another process, not killed");
                return KillResult.Gone;
            }
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            if (p.HasExited) return KillResult.Killed;
            why = "the process did not exit";
        }
        catch (ArgumentException) { return KillResult.Gone; }   // no such process
        catch (Exception e)
        {
            why = (e is AggregateException ae ? ae.InnerExceptions.FirstOrDefault()?.Message ?? e.Message : e.Message).Trim();
            Log.W("hub", $"kill {pid} failed: {e.Message}; trying taskkill");
        }
        if (!Native.PidAlive(pid, ct)) return KillResult.Killed;   // the process itself is gone (only some child resisted)
        try
        {
            var psi = new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F") { CreateNoWindow = true, UseShellExecute = false };
            using var tk = Process.Start(psi);
            tk.WaitForExit(10000);
        }
        catch (Exception e) { Log.W("hub", "taskkill: " + e.Message); }
        for (int i = 0; i < 10 && Native.PidAlive(pid, ct); i++) Thread.Sleep(100);
        return Native.PidAlive(pid, ct) ? KillResult.Failed : KillResult.Killed;
    }

    /// <summary>A line of a shell control's output: UTF-8 if it is valid UTF-8, else the OEM code page console tools write.</summary>
    static string ShellLine(string latin1)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(latin1);
        try { return strictUtf8.GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { return oem.Value.GetString(bytes); }
    }
    static readonly System.Text.Encoding strictUtf8 = new System.Text.UTF8Encoding(false, true);
    static readonly Lazy<System.Text.Encoding> oem = new(() =>
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return System.Text.Encoding.Latin1; }
    });
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetOEMCP();

    public static void OpenTarget(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Shell control as a list: the value may not choose the program, and .bat / .cmd targets (run through
    /// cmd.exe, where the argument quoting doesn't hold) can't take {value}. 400 otherwise.</summary>
    static void ShellListCheck(List<string> parts)
    {
        if (parts.Count == 0) return;
        if (parts[0].Contains("{value}")) throw new ApiError(400, "shell control: {value} can't be in the program (first item)");
        var ext = Path.GetExtension(parts[0].TrimEnd(' ', '.')).ToLowerInvariant();
        if (ext is ".bat" or ".cmd" && parts.Skip(1).Any(a => a.Contains("{value}")))
            throw new ApiError(400, "shell control: a .bat / .cmd program can't take {value} in a list command (cmd.exe would parse it); use the string form");
    }

    void RunShell(string tid, JsonObject c, JsonNode value, int? pid)
    {
        var val = value == null ? "" : J.Str(value);
        // {pid} and {task_id} are digits / [A-Za-z0-9_.-]; only {value} comes from the user (panel, toast, Telegram)
        string Sub(string s, string v) => s.Replace("{pid}", pid?.ToString() ?? "").Replace("{task_id}", tid).Replace("{value}", v);
        var cid = J.Str(c["id"]);
        List<string> lines;
        string level;
        try
        {
            ProcessStartInfo psi;
            var cmd = c["command"];
            if (cmd is JsonArray ca)
            {
                // each item is one argument (ArgumentList quotes it); see ShellListCheck for .bat / .cmd
                var tpl = ca.Select(x => J.Str(x)).ToList();
                ShellListCheck(tpl);
                var parts = tpl.Select(x => Sub(x, val)).ToList();
                psi = new ProcessStartInfo(parts[0]);
                foreach (var a in parts.Skip(1)) psi.ArgumentList.Add(a);
            }
            else
            {
                // the value never enters the command line cmd.exe parses: it goes in TASKHUB_VALUE and {value} becomes
                // !TASKHUB_VALUE! with delayed expansion (/v:on), which cmd expands only after it has split the line into
                // commands, pipes and redirections - so & | < > ^ % " ! in the value stay plain text (%VAR% would be
                // expanded before parsing and is not safe). An empty value is substituted as nothing (an empty variable
                // counts as undefined and would stay as the literal !TASKHUB_VALUE!).
                var line = J.Str(cmd);
                bool hasValue = line.Contains("{value}");
                psi = new ProcessStartInfo("cmd.exe");
                psi.Arguments = (hasValue ? "/v:on " : "") + "/d /s /c \"" + Sub(line, val.Length > 0 ? "!TASKHUB_VALUE!" : "") + "\"";
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            // bytes as they are: each line is decoded below, as a command can print either
            psi.StandardOutputEncoding = psi.StandardErrorEncoding = System.Text.Encoding.Latin1;
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["TASKHUB_TASK_ID"] = tid;
            psi.Environment["TASKHUB_URL"] = S.Url;
            // both forms; the string form reads it via !TASKHUB_VALUE!
            if (val.Length > 0) psi.Environment["TASKHUB_VALUE"] = val; else psi.Environment.Remove("TASKHUB_VALUE");
            if (J.Truthy(c["cwd"])) psi.WorkingDirectory = J.Str(c["cwd"]);
            using var p = Process.Start(psi);
            // only the last 25 lines go to the log: keep just those, however much the command prints
            var tail = new Queue<string>();
            async Task Drain(StreamReader r)
            {
                string l;
                while ((l = await r.ReadLineAsync()) != null)
                    if (l.Length > 0) lock (tail) { tail.Enqueue(J.Cut(ShellLine(l), 2000)); if (tail.Count > 25) tail.Dequeue(); }
            }
            var drained = Task.WhenAll(Drain(p.StandardOutput), Drain(p.StandardError));
            var secs = Math.Clamp(J.Num(c["timeout"]) ?? 300, 1, 86400);
            var deadline = DateTime.UtcNow.AddSeconds(secs);
            if (!p.WaitForExit(TimeSpan.FromSeconds(secs)))
            {
                try { p.Kill(true); } catch { }
                throw new Exception($"timeout after {J.Fmt(secs)}s");
            }
            // a child the command left running may keep the pipes open: wait for the rest of the output only up to the deadline
            var left = deadline - DateTime.UtcNow;
            drained.Wait(left > TimeSpan.FromSeconds(2) ? left : TimeSpan.FromSeconds(2));
            lock (tail) lines = tail.Select(x => $"[{cid}] {x}").ToList();
            lines.Add($"[{cid}] exit {p.ExitCode}");
            level = p.ExitCode == 0 ? "info" : "error";
        }
        catch (Exception e)
        {
            lines = new() { $"[{cid}] failed: {e.Message}" };
            level = "error";
        }
        lock (Lock)
        {
            if (tasks.TryGetValue(tid, out var t))
            {
                AddLog(t, J.StrArray(lines), level);
                Bump();
            }
        }
    }

    public async Task<JsonObject> PollCommands(string tid, double wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, Math.Min(wait, 60)));
        while (true)
        {
            Task w;
            lock (Lock)
            {
                Get(tid);
                var q = In(tid).CmdQ;
                // the client is gone: leave the queue alone, the commands go out with its next poll
                if (ct.IsCancellationRequested)
                    return new JsonObject { ["commands"] = new JsonArray(), ["state"] = J.Str(tasks[tid]["state"]) };
                if (q.Count > 0 || DateTime.UtcNow >= deadline)
                {
                    var outp = new JsonArray(q.Select(x => (JsonNode)x.DeepClone()).ToArray());
                    var t = tasks[tid];
                    if (q.Count > 0)
                    {
                        var ids = q.Select(x => J.Str(x["id"])).ToHashSet();
                        foreach (var h in (t["commands"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            if (ids.Contains(J.Str(h["id"]))) h["status"] = "delivered";
                        q.Clear();
                        Bump();
                    }
                    return new JsonObject { ["commands"] = outp, ["state"] = J.Str(t["state"]) };
                }
                w = changed.Task;
            }
            var rem = deadline - DateTime.UtcNow;
            if (rem < TimeSpan.FromMilliseconds(50)) rem = TimeSpan.FromMilliseconds(50);
            try { await w.WaitAsync(rem, ct); } catch (TimeoutException) { } catch (OperationCanceledException) { }
        }
    }

    public JsonObject Ack(string tid, string cmdId, JsonObject b)
    {
        lock (Lock)
        {
            var t = Get(tid);
            bool ok = !b.ContainsKey("ok") || J.Truthy(b["ok"]);
            var msg = J.Cut(J.Truthy(b["message"]) ? J.Str(b["message"]) : (ok ? "ok" : "failed"), 500);
            foreach (var h in (t["commands"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (J.Str(h["id"]) != cmdId) continue;
                h["status"] = ok ? "ok" : "error";
                h["reply"] = msg;
                AddLog(t, $"⇠ {J.Str(h["control"])}: {msg}", ok ? "cmd" : "error");
                break;
            }
            Bump();
        }
        return new JsonObject { ["ok"] = true };
    }
}
