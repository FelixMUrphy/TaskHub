using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Codex's plan limits: Codex has no status line, so TaskHub asks its own `codex app-server` (documented as experimental): start, read, stop,
/// every 10 minutes while the switch is on and Codex is running. Measured on 0.166.0: about a second, no model request, MCP servers aren't
/// started (only a thread does that), nothing left after stdin closes. Codex uses its own login; TaskHub never touches its tokens, and
/// keeps only the percentages and reset times, through ReportLimits like Claude's. If any of this stops working, the row just goes stale.
/// </summary>
sealed partial class Hub
{
    const string CodexLimitsKey = "limits_codex";
    const int CodexEverySec = 600, CodexProbeSec = 60, CodexDeadlineSec = 20;
    double codexNext, codexProbe;
    int codexBusy;
    string codexProblem;   // why the last read gave no numbers, for the panel: not_found, login, failed

    /// <summary>For GET /api/tasks: {agent: why there are no numbers}. Only while the switch is on.</summary>
    JsonObject LimitsProblem() =>
        codexProblem != null && S.Bool(CodexLimitsKey) ? new JsonObject { ["codex"] = codexProblem } : new JsonObject();

    void SetCodexProblem(string problem)
    {
        lock (Lock)
        {
            if (codexProblem == problem) return;
            codexProblem = problem;
            Bump();
        }
    }

    static string TestEnv(string name) =>
        Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 } ? Environment.GetEnvironmentVariable(name) : null;

    /// <summary>From the watchdog. Reading while Codex is closed would only repeat the last numbers, so the 10 minutes count from a real read
    /// and a Codex opened after the hub is seen within a minute.</summary>
    void TickCodexLimits()
    {
        if (!S.Bool(CodexLimitsKey)) return;
        var now = J.Now();
        if (now < codexNext || now < codexProbe) return;
        codexProbe = now + (double.TryParse(TestEnv("TASKHUB_TEST_CODEX_PROBE"), out var probe) ? probe : CodexProbeSec);
        _ = Task.Run(() =>
        {
            if (!CodexRunning()) return;
            codexNext = J.Now() + (double.TryParse(TestEnv("TASKHUB_TEST_CODEX_EVERY"), out var every) ? every : CodexEverySec);
            ReadCodexLimits();
        });
    }

    /// <summary>The switch was just turned on: the user is looking, so read now, whether or not Codex is running.</summary>
    public void ReadCodexLimitsNow()
    {
        codexNext = J.Now() + CodexEverySec;
        _ = Task.Run(ReadCodexLimits);
    }

    static bool CodexRunning()
    {
        if (TestEnv("TASKHUB_TEST_CODEX_RUNNING") is { } flag) return File.Exists(flag);
        var ps = Process.GetProcessesByName("codex");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>codex.exe on PATH, or the one the npm package holds: its codex.cmd goes through a shell and node, which isn't what was measured.</summary>
    public static string LocateCodex()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            try
            {
                var exe = Path.Combine(d, "codex.exe");
                if (File.Exists(exe)) return exe;
                var pkg = Path.Combine(d, "node_modules", "@openai", "codex", "node_modules", "@openai");
                if (File.Exists(Path.Combine(d, "codex.cmd")) && Directory.Exists(pkg))
                    foreach (var found in Directory.EnumerateFiles(pkg, "codex.exe", SearchOption.AllDirectories)) return found;
            }
            catch (Exception) { }   // a bad PATH entry
        return null;
    }

    void ReadCodexLimits()
    {
        if (Interlocked.Exchange(ref codexBusy, 1) == 1) return;
        try
        {
            var start = new ProcessStartInfo
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = S.DataDir,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            };
            if (TestEnv("TASKHUB_TEST_CODEX") is { Length: > 0 } test)
            {
                var parts = test.Split('|');
                start.FileName = parts[0];
                start.ArgumentList.Add(parts[1]);
            }
            else if (LocateCodex() is { } exe) start.FileName = exe;
            else
            {
                if (codexProblem != "not_found") Log.I("limits", "codex: not found on PATH");
                SetCodexProblem("not_found");
                return;
            }
            start.ArgumentList.Add("app-server");
            using var p = Process.Start(start);
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            try
            {
                var result = ReadRateLimits(p);
                if (result == null) return;
                var body = new JsonObject();
                foreach (var k in new[] { "primary", "secondary" })
                    if (result["rateLimits"]?[k] is JsonObject w && NumOnly(w["usedPercent"]) is double pct)
                    {
                        // by length, not place: a plan with no 5-hour window has the week first
                        var name = NumOnly(w["windowDurationMins"]) switch { 300 => "five_hour", 10080 => "seven_day", _ => null };
                        if (name != null) body[name] = new JsonObject { ["used_percentage"] = pct, ["resets_at"] = NumOnly(w["resetsAt"]) };
                    }
                if (body.Count == 0)
                {
                    Log.W("limits", "codex: no known window in the reading");
                    SetCodexProblem("failed");
                    return;
                }
                ReportLimits("codex", body);
                SetCodexProblem(null);
            }
            finally
            {
                try { p.StandardInput.Close(); } catch (Exception) { }
                if (!p.WaitForExit(3000)) { Log.W("limits", "codex: still running after stdin closed, ended"); p.Kill(true); }
            }
        }
        catch (Exception e)
        {
            Log.W("limits", "codex: " + e.GetType().Name + ": " + e.Message);
            SetCodexProblem("failed");
        }
        finally { Volatile.Write(ref codexBusy, 0); }
    }

    /// <summary>The handshake and one read, within the deadline. Null on any failure, said by code only: its message may name the account.</summary>
    JsonNode ReadRateLimits(Process p)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(double.TryParse(TestEnv("TASKHUB_TEST_CODEX_DEADLINE"), out var d) ? d : CodexDeadlineSec));
        void Send(JsonObject o)
        {
            p.StandardInput.WriteLine(o.ToJsonString());
            p.StandardInput.Flush();
        }
        Send(new JsonObject
        {
            ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "taskhub", ["title"] = "TaskHub", ["version"] = Settings.Version } },
        });
        try
        {
            while (true)
            {
                var line = p.StandardOutput.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
                if (line == null)
                {
                    Log.W("limits", "codex: ended without an answer");
                    SetCodexProblem("failed");
                    return null;
                }
                JsonNode m;
                try { m = JsonNode.Parse(line); } catch (JsonException) { continue; }
                if (m is not JsonObject o || o["method"] != null || NumOnly(o["id"]) is not double id) continue;   // a notification, or a request to us
                if (o["error"] != null)
                {
                    Log.W("limits", $"codex: refused, code {NumOnly(o["error"]?["code"])}");
                    // the words as Codex 0.166.0 has them; another wording only makes the line less precise
                    SetCodexProblem(J.Str(o["error"]?["message"])?.Contains("authentication required", StringComparison.OrdinalIgnoreCase) == true ? "login" : "failed");
                    return null;
                }
                if (id == 1)
                {
                    Send(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() });
                    Send(new JsonObject { ["id"] = 2, ["method"] = "account/rateLimits/read", ["params"] = new JsonObject() });
                }
                else if (id == 2) return o["result"];
            }
        }
        catch (OperationCanceledException)
        {
            Log.W("limits", "codex: no answer in time");
            SetCodexProblem("failed");
            return null;
        }
    }
}
