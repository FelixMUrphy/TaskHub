using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHubCli;

/// <summary>"taskhub run -- cmd": wraps any command and mirrors it into a TaskHub task.</summary>
static class Runner
{
    [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern uint GetOEMCP();
    [DllImport("kernel32.dll")] static extern uint GetACP();

    sealed class Pending
    {
        public readonly object L = new();
        public List<JsonNode> Log = new();      // string = info line, object {level,msg} = warn/error
        public JsonObject Fields = new();
        public JsonObject Widgets = new();
        public JsonObject Metrics = new();
        public string Toast;
        public readonly HashSet<string> Declared = new() { "last" };   // the hub refuses a value for a widget it doesn't have, and a batch with it
        public JsonArray NewWidgets = new();                           // ::widget with a new id: declared with its first value
    }

    public static int Run(Client c, List<string> a)
    {
        string name = Program.TakeOpt(a, "--name");
        string id = Program.TakeOpt(a, "--id");
        string source = Program.TakeOpt(a, "--source") ?? Environment.GetEnvironmentVariable("TASKHUB_SOURCE") ?? "cli";
        string desc = Program.TakeOpt(a, "--description");
        string cwd = Program.TakeOpt(a, "--cwd");
        string progRx = Program.TakeOpt(a, "--progress-regex");
        string statRx = Program.TakeOpt(a, "--status-regex");
        string hb = Program.TakeOpt(a, "--heartbeat");
        string ms = Program.TakeOpt(a, "--milestones");
        string enc = Program.TakeOpt(a, "--encoding") ?? "utf8";
        string presetName = Program.TakeOpt(a, "--preset");
        bool auto = Program.TakeFlag(a, "--auto-progress");
        bool quiet = Program.TakeFlag(a, "--quiet");
        bool ptoast = Program.TakeFlag(a, "--progress-toast");
        bool toastCtl = Program.TakeFlag(a, "--toast-controls");
        int dd = a.IndexOf("--");
        var cmd = dd >= 0 ? a.Skip(dd + 1).ToList() : a.ToList();
        if (cmd.Count == 0) { Console.Error.WriteLine("taskhub run: no command given (use: run --name X -- cmd args)"); return 2; }
        // a known tool's own output (Blender...): by --preset, or by its file name unless the caller parses it already
        IOutputPreset preset;
        try { preset = presetName != null || (progRx == null && statRx == null && !auto) ? Presets.For(presetName, cmd) : null; }
        catch (ArgumentException e) { Console.Error.WriteLine("taskhub run: " + e.Message); return 2; }

        Encoding encoding = enc.ToLowerInvariant() switch
        {
            "oem" => CodePage((int)GetOEMCP()),
            "ansi" => CodePage((int)GetACP()),
            _ => new UTF8Encoding(false),
        };

        Regex prog, stat;
        try { prog = progRx != null ? UserRegex(progRx) : auto ? new Regex(@"(?<pct>\d{1,3}(?:\.\d+)?)\s?%|(?<cur>\d+)\s?/\s?(?<tot>\d+)") : null; }
        catch (ArgumentException e) { Console.Error.WriteLine("taskhub run: bad --progress-regex: " + e.Message); return 2; }
        try { stat = statRx != null ? UserRegex(statRx) : null; }
        catch (ArgumentException e) { Console.Error.WriteLine("taskhub run: bad --status-regex: " + e.Message); return 2; }

        // ---- start the process
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        bool useHub = c.Available();
        string tid = id ?? Guid.NewGuid().ToString("N")[..8];
        psi.Environment["TASKHUB_URL"] = c.Url;
        psi.Environment["TASKHUB_TASK_ID"] = tid;
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        if (encoding is UTF8Encoding) psi.Environment["PYTHONIOENCODING"] = "utf-8";
        Process p;
        try
        {
            psi.FileName = cmd[0];
            foreach (var x in cmd.Skip(1)) psi.ArgumentList.Add(x);
            p = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            // not an executable on PATH (shell builtin, script association...) -> run through cmd.exe
            psi.ArgumentList.Clear();
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = "/d /s /c \"" + string.Join(" ", cmd.Select(Quote)) + "\"";
            p = Process.Start(psi);
        }
        var cmdline = string.Join(" ", cmd.Select(Quote));

        // ---- register
        var pending = new Pending();
        foreach (var pw in preset?.Widgets ?? Enumerable.Empty<JsonObject>()) pending.Declared.Add(Program.S(pw["id"]));
        if (useHub)
        {
            var controls = new JsonArray
            {
                (JsonNode)new JsonObject { ["id"] = "interrupt", ["label"] = "Ctrl+C", ["kind"] = "signal", ["confirm"] = "Send Ctrl+C to the process?", ["toast"] = toastCtl },
                (JsonNode)new JsonObject { ["id"] = "stdin", ["label"] = "Send input", ["kind"] = "signal", ["input"] = new JsonObject { ["type"] = "text", ["label"] = "stdin" } },
            };
            foreach (var pc in preset?.Controls ?? Enumerable.Empty<JsonObject>()) controls.Add((JsonNode)pc);
            var reg = new JsonObject
            {
                ["id"] = tid, ["name"] = name ?? Cut(cmdline, 80), ["source"] = source,
                ["description"] = desc ?? Cut(cmdline, 300), ["pid"] = p.Id, ["cwd"] = psi.WorkingDirectory,
                ["heartbeat_timeout"] = hb != null ? double.Parse(hb, CultureInfo.InvariantCulture) : 0,
                ["controls"] = controls,
                ["widgets"] = new JsonArray(new[] { (JsonNode)new JsonObject { ["id"] = "last", ["type"] = "text", ["label"] = "Last line", ["mono"] = true, ["span"] = 2 } }
                    .Concat(preset?.Widgets ?? Enumerable.Empty<JsonObject>()).ToArray()),
                ["progress_toast"] = ptoast,
            };
            if (ms != null)
                reg["notify"] = new JsonObject
                {
                    ["milestones"] = new JsonArray(ms.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => (JsonNode)double.Parse(x, CultureInfo.InvariantCulture)).ToArray()),
                };
            try { c.Call("POST", "/api/tasks", reg); }
            catch (Exception e) { Console.Error.WriteLine("taskhub: register failed: " + e.Message); useHub = false; }
        }

        bool userCancelled = false, forwarding = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;                                  // the child (same console) gets Ctrl+C itself; wait for it
            if (!forwarding) userCancelled = true;
        };

        var stop = new CancellationTokenSource();
        Thread sender = null;
        if (useHub)
        {
            sender = new Thread(() => SenderLoop(c, tid, pending, stop.Token)) { IsBackground = true };
            sender.Start();
            new Thread(() => PollLoop(c, tid, p, stop.Token, v => forwarding = v, () => userCancelled = true, preset, pending.L)) { IsBackground = true }.Start();
        }

        string lastLine = "";
        bool ending = false;   // the preset asked to end the command (e.g. "stop after this frame")
        void EndIfAsked()
        {
            if (ending || preset?.WantsStop != true) return;
            ending = true;
            new Thread(() => { try { p.Kill(entireProcessTree: true); } catch { } }) { IsBackground = true }.Start();
        }
        var outLock = new object();
        Stream stdout = quiet ? null : Console.OpenStandardOutput();
        void OnLine(string raw)
        {
            var line = raw.Trim();
            if (line.Length == 0) return;
            lock (pending.L)
            {
                // without a hub nobody sends the pending lines: keep only the last one (fail-soft, no growing buffer)
                if (!useHub)
                {
                    if (!line.StartsWith("::")) { lastLine = line; preset?.Line(line, pending.Fields, pending.Widgets); EndIfAsked(); }   // for the verdict
                    return;
                }
                if (line.StartsWith("::") && Directive(line, pending)) { TrimLog(pending); return; }
                lastLine = line;
                var entry = preset?.Line(Cut(line, 2000), pending.Fields, pending.Widgets);
                EndIfAsked();
                pending.Log.Add(entry ?? JsonValue.Create(Cut(line, 2000)));
                TrimLog(pending);
                pending.Widgets["last"] = Cut(line, 300);
                if (prog != null)
                {
                    var m = MatchLine(prog, line);
                    if (m.Success)
                    {
                        if (m.Groups["pct"].Success && Num(m.Groups["pct"].Value) is double pct) pending.Fields["percent"] = pct;
                        else if (m.Groups["cur"].Success && m.Groups["tot"].Success && Num(m.Groups["cur"].Value) is double cur && Num(m.Groups["tot"].Value) is double tot)
                        { pending.Fields["current"] = cur; pending.Fields["total"] = tot; }
                        else if (m.Groups.Count > 1 && Num(m.Groups[1].Value) is double g1) pending.Fields["percent"] = g1;
                    }
                }
                if (stat != null)
                {
                    var m = MatchLine(stat, line);
                    if (m.Success) pending.Fields["status"] = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                }
            }
        }
        void Pump(StreamReader r)
        {
            var buf = new char[4096];
            var line = new StringBuilder();
            int n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                if (stdout != null)
                {
                    var bytes = Console.OutputEncoding.GetBytes(buf, 0, n);
                    lock (outLock) { stdout.Write(bytes); stdout.Flush(); }
                }
                for (int i = 0; i < n; i++)
                {
                    char ch = buf[i];
                    if (ch == '\r' || ch == '\n')
                    {
                        if (line.Length > 0) { var l = line.ToString(); line.Clear(); OnLine(l); }
                    }
                    else line.Append(ch);
                }
            }
            if (line.Length > 0) OnLine(line.ToString());
        }

        var t1 = new Thread(() => Pump(p.StandardOutput)) { IsBackground = true };
        var t2 = new Thread(() => Pump(p.StandardError)) { IsBackground = true };
        t1.Start(); t2.Start();
        p.WaitForExit();
        t1.Join(3000); t2.Join(3000);
        int rc = p.ExitCode;
        stop.Cancel();
        (string State, string Message)? verdict;
        string resume;
        lock (pending.L) { verdict = preset?.Verdict(rc); resume = preset?.ResumeCommand; }
        if (resume != null) Console.Error.WriteLine("taskhub: resume with: " + resume);
        if (useHub)
        {
            sender?.Join(2000);
            Flush(c, tid, pending);
            // Ctrl+C stays "cancelled" also when a preset calls the non-zero exit a failure (its message, e.g. the resume hint, stays)
            var state = userCancelled && rc != 0 && verdict?.State == "failed" ? "cancelled"
                : verdict?.State ?? (rc == 0 ? "done" : (userCancelled ? "cancelled" : "failed"));
            var msg = verdict?.Message ?? $"exit code {rc}" + (lastLine.Length > 0 ? " · " + Cut(lastLine, 200) : "");
            try { c.Call("POST", $"/api/tasks/{tid}/finish", new JsonObject { ["state"] = state, ["message"] = msg }); }
            catch (Exception e) { Console.Error.WriteLine("taskhub: finish failed: " + e.Message); }
        }
        // a tool that failed but exited with 0 (Blender after a render error): scripts see the failure too
        return rc == 0 && verdict?.State == "failed" ? 1 : rc;
    }

    static Encoding CodePage(int cp)
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(cp); }
        catch { return new UTF8Encoding(false); }
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
    static double? Num(string s) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    /// <summary>For the cmd.exe fallback: an argument stays one argument. An operator on its own (`&`, `|`, `>`, `2>&1`) or a redirection (`>file`) stays an operator.</summary>
    static string Quote(string s)
    {
        bool plain = s.Length > 0 && !s.Any(ch => char.IsWhiteSpace(ch) || ch == '"');
        if (plain && (!s.Any(ch => ch is '&' or '|' or '<' or '>' or '^') || Regex.IsMatch(s, @"^(&&?|\|\|?|[0-9]?>>?(&[0-9])?|<|[0-9]?(>>?|<)[^&|<>^]+)$"))) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>::directive handling; returns true if the line was a directive.</summary>
    /// <summary>A regex given by the caller; Python's named groups, as in the docs of the Python client, work too.</summary>
    static Regex UserRegex(string pattern) =>
        new(Regex.Replace(pattern.Replace("(?P<", "(?<"), @"\(\?P=(\w+)\)", @"\k<$1>"), RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>A line of the command's output, against a regex that may be slow: it must not hold up the command.</summary>
    static Match MatchLine(Regex r, string line)
    {
        try { return r.Match(Cut(line, 2000)); }
        catch (RegexMatchTimeoutException) { return Match.Empty; }
    }

    const int MaxWidgets = 40;   // the hub's limit for a task

    static bool Directive(string line, Pending pd)
    {
        var m = Regex.Match(line, @"^::(\w+)\s*(.*)$");
        if (!m.Success) return false;
        var key = m.Groups[1].Value.ToLowerInvariant();
        var arg = m.Groups[2].Value.Trim();
        switch (key)
        {
            case "progress":
            {
                var pm = Regex.Match(arg, @"^([\d.,]+)\s*(?:/\s*([\d.,]+))?");
                if (!pm.Success) return false;
                var cur = Num(pm.Groups[1].Value);
                if (pm.Groups[2].Success) { pd.Fields["current"] = cur; pd.Fields["total"] = Num(pm.Groups[2].Value); }
                else if (cur is double f && f <= 1 && arg.Contains('.')) pd.Fields["progress"] = f;
                else pd.Fields["current"] = cur;
                var rest = arg[pm.Length..].Trim();
                if (rest.Length > 0) pd.Fields["status"] = rest;
                return true;
            }
            case "percent": if (Num(arg.TrimEnd('%')) is double pc) pd.Fields["percent"] = pc; return true;
            case "total": if (Num(arg) is double tt) pd.Fields["total"] = tt; return true;
            case "status": pd.Fields["status"] = arg; return true;
            case "stage": pd.Fields["stage"] = arg; return true;
            case "log": pd.Log.Add(JsonValue.Create(arg)); return true;
            case "warn":
            case "warning":
            case "error":
                pd.Log.Add(new JsonObject { ["level"] = key == "error" ? "error" : "warn", ["msg"] = arg });
                return true;
            case "toast": pd.Toast = arg; return true;
            case "metric":
            {
                var kv = arg.Split('=', 2);
                if (kv.Length != 2) return false;
                pd.Metrics[kv[0].Trim()] = Num(kv[1].Trim()) is double dv ? dv : kv[1].Trim();
                return true;
            }
            case "widget":
            {
                var kv = arg.Split('=', 2);
                if (kv.Length != 2) return false;
                var wid = kv[0].Trim();
                if (!Regex.IsMatch(wid, @"^[A-Za-z0-9_.:-]{1,48}$")) return false;   // the hub's rule: an ordinary line then
                if (!pd.Declared.Contains(wid))
                {
                    if (pd.Declared.Count >= MaxWidgets) return true;
                    pd.Declared.Add(wid);
                    pd.NewWidgets.Add((JsonNode)new JsonObject { ["id"] = wid, ["type"] = "text", ["label"] = wid });
                }
                pd.Widgets[wid] = Program.ParseValue(kv[1].Trim());
                return true;
            }
        }
        return false;
    }

    /// <summary>Bounds the unsent lines (the hub keeps only its last few hundred anyway) while it is slow or unreachable.</summary>
    static void TrimLog(Pending pd)
    {
        if (pd.Log.Count > 1000) pd.Log.RemoveRange(0, pd.Log.Count - 1000);
    }

    static void SenderLoop(Client c, string tid, Pending pd, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Task.Delay(350, ct).Wait(); } catch { break; }
            Flush(c, tid, pd);
        }
    }

    /// <summary>Sends accumulated lines/fields in as few update calls as possible (one per log level run).</summary>
    static void Flush(Client c, string tid, Pending pd)
    {
        var bodies = new List<JsonObject>();
        lock (pd.L)
        {
            if (pd.Log.Count == 0 && pd.Fields.Count == 0 && pd.Widgets.Count == 0 && pd.Metrics.Count == 0 && pd.NewWidgets.Count == 0 && pd.Toast == null) return;
            var info = new JsonArray();
            void FlushInfo()
            {
                if (info.Count == 0) return;
                bodies.Add(new JsonObject { ["log"] = info, ["level"] = "info" });
                info = new JsonArray();
            }
            foreach (var l in pd.Log)
            {
                if (l is JsonObject lo)
                {
                    FlushInfo();
                    bodies.Add(new JsonObject { ["log"] = lo["msg"]?.DeepClone(), ["level"] = lo["level"]?.DeepClone() });
                }
                else info.Add(l.DeepClone());
            }
            FlushInfo();
            pd.Log.Clear();
            if (bodies.Count == 0) bodies.Add(new JsonObject());
            var main = bodies[^1];
            foreach (var (k, v) in pd.Fields) main[k] = v?.DeepClone();
            if (pd.NewWidgets.Count > 0) { main["add_widgets"] = pd.NewWidgets; pd.NewWidgets = new JsonArray(); }
            if (pd.Widgets.Count > 0) main["widgets"] = pd.Widgets.DeepClone();
            if (pd.Metrics.Count > 0) main["metrics"] = pd.Metrics.DeepClone();
            if (pd.Toast != null) main["toast"] = pd.Toast;
            pd.Fields = new JsonObject(); pd.Widgets = new JsonObject(); pd.Metrics = new JsonObject(); pd.Toast = null;
        }
        foreach (var b in bodies)
        {
            try { c.Call("POST", $"/api/tasks/{tid}/update", b, 5); }
            catch
            {
                // fail-soft: the command keeps running even if the hub is gone. A new widget is asked for again, or its later values would fail too
                if (b["add_widgets"] is JsonArray again)
                    lock (pd.L) foreach (var w in again) pd.NewWidgets.Add(w.DeepClone());
            }
        }
    }

    static void PollLoop(Client c, string tid, Process p, CancellationToken ct, Action<bool> setForwarding, Action interrupted, IOutputPreset preset, object presetLock)
    {
        while (!ct.IsCancellationRequested && !p.HasExited)
        {
            JsonArray cmds;
            try { cmds = (c.Call("GET", $"/api/tasks/{tid}/commands?wait=25", null, 40) as JsonObject)?["commands"] as JsonArray; }
            catch { try { Task.Delay(3000, ct).Wait(); } catch { } continue; }
            foreach (var cmd in (cmds ?? new JsonArray()).OfType<JsonObject>())
            {
                var cid = Program.S(cmd["control"]);
                var cmdId = Program.S(cmd["id"]);
                bool ok = true;
                string reply;
                try
                {
                    switch (cid)
                    {
                        case "interrupt":
                            if (GetConsoleWindow() == IntPtr.Zero) { ok = false; reply = "no console - use Kill"; break; }
                            setForwarding(true);
                            ok = GenerateConsoleCtrlEvent(0, 0);   // CTRL_C to every process on this console (we ignore it)
                            if (ok) interrupted();   // at once, the child can end before the pause does; a non-zero exit after it is "cancelled", as with Ctrl+C in the console
                            Thread.Sleep(300);
                            setForwarding(false);
                            reply = ok ? "Ctrl+C sent" : "GenerateConsoleCtrlEvent failed";
                            break;
                        case "stdin":
                            p.StandardInput.WriteLine(Program.S(cmd["value"]));
                            p.StandardInput.Flush();
                            reply = "written";
                            break;
                        default:
                            (bool Ok, string Reply)? pr;
                            lock (presetLock) pr = preset?.OnControl(cid, cmd["value"]);
                            (ok, reply) = pr ?? (false, "unknown control");
                            break;
                    }
                }
                catch (Exception e) { ok = false; reply = e.Message; }
                try { c.Call("POST", $"/api/tasks/{tid}/commands/{cmdId}/ack", new JsonObject { ["ok"] = ok, ["message"] = reply }); } catch { }
            }
        }
    }
}
