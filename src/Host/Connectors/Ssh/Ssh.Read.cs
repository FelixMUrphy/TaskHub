using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>The connection (ssh.exe), the fixed script and what its output means.</summary>
sealed partial class SshConnector
{
    /// <summary>The script of one tick: POSIX sh, LF only, a section marker with this tick's nonce before each part. A login shell's greeting or a
    /// .bashrc that prints can't pass for data. Nothing in it comes from the user or the host except the watched PIDs, which are numbers.</summary>
    internal static string Script(string nonce, IEnumerable<long> pids)
    {
        var m = "TH-" + nonce + "-";
        var s = new StringBuilder("LC_ALL=C; export LC_ALL\n{\n");
        void Part(string name, string cmd) => s.Append("echo ").Append(m).Append(name).Append('\n').Append(cmd).Append('\n');
        Part("uname", "uname -s");
        Part("boot", "cat /proc/sys/kernel/random/boot_id");
        Part("stat", "head -n 1 /proc/stat");
        Part("mem", "cat /proc/meminfo");
        Part("mounts", "cat /proc/mounts");
        // local file systems when df can tell: a hung network mount would hang it
        Part("df", "df -kPl || df -kP");
        Part("gpu", "command -v nvidia-smi >/dev/null && nvidia-smi --query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits");
        // ":" can't be in a session's name
        Part("tmux", "command -v tmux >/dev/null && tmux list-panes -a -F '#{pane_id}:#{session_name}:#{pane_dead}:#{pane_current_command}'");
        foreach (var p in pids) Part("proc-" + p, $"cat /proc/{p}/stat");
        s.Append("} </dev/null 2>/dev/null\necho ").Append(m).Append("end\n");
        return s.ToString();
    }

    static readonly Regex EscRx = new(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)?|\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);
    static readonly Regex DfRx = new(@"^(\S+)\s.*?(\d+)%\s+(/.*)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);   // lines from a host: linear time
    const int MaxDfLine = 1024;

    /// <summary>Control characters and escape sequences out, cut: remote text ends up in the panel, notifications and the mirrors.</summary>
    internal static string Clean(string s, int max = 120)
    {
        s ??= "";
        if (s.Length > max * 8) s = s[..(max * 8)];
        s = EscRx.Replace(s, "");
        var b = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s)
        {
            // format characters: direction overrides, zero widths, a byte order mark; line separators
            if (char.IsControl(ch) || char.GetUnicodeCategory(ch) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            b.Append(ch);
            if (b.Length >= max) break;
        }
        return b.ToString().Trim();
    }

    static long Long(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>A tick's answer. False: this host can't be read (the state says why), the connection isn't kept.</summary>
    bool Apply(Watch w, Dictionary<string, List<string>> r, List<long> pids)
    {
        var now = J.Now();
        var uname = r.TryGetValue("uname", out var un) && un.Count > 0 ? Clean(un[0]) : "";
        if (uname.Length == 0 && w.LastOk > 0)
        {
            Lost(w, "unreachable");   // it has answered before: a bad moment, not a host that can't be read
            return false;
        }
        if (uname != "Linux")
        {
            SetState(w, uname.Length == 0 ? "restricted" : "not_linux");   // no answer to uname: not a shell that can be read
            return false;
        }
        if (TryTask(Tid(w.Host)) == null)   // deleted in the panel, while the host is still on the list
        {
            w.SigControls = null;
            Register(w);
        }
        bool gap = w.LastOk > 0 && (w.LostSaid || now - w.LastOk > Math.Max(3 * Interval, 180));
        var parts = new List<string>();

        // CPU: the part of the time since the last tick that wasn't idle
        if (r.TryGetValue("stat", out var st) && st.Count > 0)
        {
            var f = st[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(Long).ToArray();
            if (f.Length >= 4)
            {
                long all = f.Take(8).Sum(), idle = f[3] + (f.Length > 4 ? f[4] : 0), busy = all - idle;
                if (w.CpuAll > 0 && all > w.CpuAll) parts.Add($"CPU {Math.Clamp(100.0 * (busy - w.CpuBusy) / (all - w.CpuAll), 0, 100):0}%");
                w.CpuBusy = busy;
                w.CpuAll = all;
            }
        }
        // memory: MemAvailable, on kernels since 3.14
        if (r.TryGetValue("mem", out var mem))
        {
            long Val(string key)
            {
                var l = mem.FirstOrDefault(x => x.StartsWith(key + ":", StringComparison.Ordinal));
                return l == null ? 0 : Long(l.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1));
            }
            long total = Val("MemTotal"), avail = Val("MemAvailable");
            if (total > 0 && avail > 0) parts.Add($"RAM {100.0 * (total - avail) / total:0}%");
        }
        // disks: the fullest shown; a word once per disk until it has room again
        if (r.TryGetValue("df", out var df))
        {
            int top = -1;
            string topMount = null;
            var warn = DiskWarn;
            var seen = new HashSet<string>();
            var readOnly = ReadOnlyMounts(r);
            foreach (var line in df)
            {
                if (line.Length > MaxDfLine) continue;
                var x = DfRx.Match(line);
                if (!x.Success || !int.TryParse(x.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pct)) continue;
                var (fs, mount) = (x.Groups[1].Value, Clean(x.Groups[3].Value));
                if (readOnly.Contains(mount)) continue;   // full by design and can't fill up: an image, a CD, WSL's own mounts
                // docker's overlays and shm are per container; an overlay at / is the disk of a host that is a container
                if (fs is "tmpfs" or "devtmpfs" or "udev" or "none" or "squashfs" or "efivarfs" or "shm" || (fs == "overlay" && mount != "/") || Regex.IsMatch(mount, "^/(snap|run|dev|sys|proc)(/|$)")) continue;
                seen.Add(mount);
                if (pct > top) { top = pct; topMount = mount; }
                if (warn <= 0) continue;
                if (pct >= warn && w.FullDisks.Add(mount)) Say(w, L.T("ssh_disk", w.Host, mount, pct), "warn");
                else if (pct < warn - 2) w.FullDisks.Remove(mount);
            }
            w.FullDisks.IntersectWith(seen);
            if (warn <= 0) w.FullDisks.Clear();
            if (top >= 0) parts.Add(top >= 90 ? $"{L.T("ssh_disk_short")} {topMount} {top}%" : $"{L.T("ssh_disk_short")} {top}%");
        }
        // GPUs: the mean load, the memory of all
        if (r.TryGetValue("gpu", out var gpu) && gpu.Count > 0)
        {
            var rows = gpu.Select(l => l.Split(',').Select(p => p.Trim()).ToArray()).Where(a => a.Length >= 3 && long.TryParse(a[0], out _)).ToList();
            if (rows.Count > 0)
            {
                double used = rows.Sum(a => (double)Long(a[1])) / 1024, total = rows.Sum(a => (double)Long(a[2])) / 1024;
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"GPU {rows.Average(a => (double)Long(a[0])):0}%, {used:0.#}/{total:0.#} GB"));
            }
        }
        // a restart: every process is gone, and PIDs mean something else now
        var boot = r.TryGetValue("boot", out var bo) && bo.Count > 0 ? Clean(bo[0], 40) : "";
        bool rebooted = w.Boot != null && boot.Length > 0 && boot != w.Boot;
        if (rebooted) Say(w, L.T("ssh_reboot", w.Host), "warn");
        if (boot.Length > 0) w.Boot = boot;

        Tmux(w, r, gap, parts);
        Processes(w, r, pids, gap || w.LastOk <= 0, boot, parts);   // a first read here: it ended while TaskHub itself was closed

        w.Line = string.Join(" · ", parts);
        w.LastOk = now;
        if (w.LostSaid)
        {
            w.LostSaid = false;
            Log1(w, L.T("ssh_back", w.Host));
        }
        w.Fails = 0;
        w.SaidBlocked = null;
        w.State = "ok";
        var disks = w.FullDisks.Count > 0 ? L.T("ssh_disk_full", string.Join(", ", w.FullDisks)) : "";
        Try(() => hub.Update(Tid(w.Host), new JsonObject
        {
            ["status"] = J.Cut(w.Line, 1000), ["attention"] = J.Cut(disks, 300),
        }, trusted: true));
        SyncControls(w);
        return true;
    }

    /// <summary>From /proc/mounts: device, mount point with \040 for a space, type, options. The last line for a mount point is the one in force.</summary>
    static HashSet<string> ReadOnlyMounts(Dictionary<string, List<string>> r)
    {
        var ro = new HashSet<string>();
        if (!r.TryGetValue("mounts", out var lines)) return ro;
        foreach (var line in lines)
        {
            var a = line.Split(' ');
            if (a.Length < 4) continue;
            var mount = Clean(Regex.Replace(a[1], @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString()));
            if (a[3].Split(',').Contains("ro")) ro.Add(mount);
            else ro.Remove(mount);
        }
        return ro;
    }

    static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "bash", "sh", "zsh", "fish", "dash", "ksh", "ash", "-bash", "-sh", "-zsh" };

    /// <summary>tmux: a session gone, or a pane back at its shell after a command: "ended". How it ended can't be told from here.</summary>
    void Tmux(Watch w, Dictionary<string, List<string>> r, bool gap, List<string> parts)
    {
        var now = new Dictionary<string, (string Session, string Cmd)>();
        foreach (var l in r.TryGetValue("tmux", out var t) ? t : new List<string>())
        {
            var a = l.Split(':', 4);
            if (a.Length < 4 || !a[0].StartsWith('%')) continue;
            now[Clean(a[0], 12)] = (Clean(a[1], 60), a[2].Trim() == "1" ? "" : Clean(a[3], 60));
        }
        if (w.Panes != null)
        {
            var had = w.Panes.Values.Select(p => p.Session).ToHashSet();
            var has = now.Values.Select(p => p.Session).ToHashSet();
            void Word(string text) { if (TmuxNotify) Say(w, text, "info"); else Log1(w, text); }
            foreach (var s in had.Except(has)) Word(L.T(gap ? "ssh_session_gap" : "ssh_session_ended", w.Host, s));
            foreach (var (id, was) in w.Panes)
                if (now.TryGetValue(id, out var cur) && was.Cmd.Length > 0 && !Shells.Contains(was.Cmd) && (cur.Cmd.Length == 0 || Shells.Contains(cur.Cmd)))
                    Word(L.T(gap ? "ssh_cmd_gap" : "ssh_cmd_ended", w.Host, was.Cmd, cur.Session));
        }
        w.Panes = now;
        var names = now.Values.Select(p => p.Session).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (names.Count > 0) parts.Add("tmux: " + string.Join(", ", names.Take(5)) + (names.Count > 5 ? "…" : ""));
    }

    /// <summary>The processes the user asked about: told apart from a later one with the same number by their start time (field 22 of /proc/PID/stat)
    /// and the boot. "Ended", never "succeeded": the exit code isn't known.</summary>
    void Processes(Watch w, Dictionary<string, List<string>> r, List<long> pids, bool gap, string boot, List<string> parts)
    {
        if (pids.Count == 0) return;
        var said = new List<string>();
        var ended = new List<long>();
        var fresh = new List<(long Pid, string Start, string Comm)>();
        var known = Watched(w.Host).OfType<JsonObject>().ToDictionary(x => (long)(J.Num(x["pid"]) ?? 0));
        foreach (var pid in pids)
        {
            if (!known.TryGetValue(pid, out var rec)) continue;
            var (start, comm, alive) = (J.Str(rec["start"]), J.Str(rec["comm"]), false);
            string nowStart = null, nowComm = null;
            if (r.TryGetValue("proc-" + pid, out var st) && st.Count > 0)
            {
                var line = st[0];
                int a = line.IndexOf('('), b = line.LastIndexOf(')');
                if (a > 0 && b > a)
                {
                    nowComm = Clean(line[(a + 1)..b], 40);
                    var f = line[(b + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length > 19 && f[0] != "Z") { nowStart = f[19]; alive = true; }
                }
            }
            bool isNew = start.Length == 0;
            if (isNew)
            {
                if (alive) { fresh.Add((pid, nowStart, nowComm)); said.Add($"{nowComm} ({pid})"); Log1(w, L.T("ssh_watching", $"{nowComm} ({pid})")); }
                else { ended.Add(pid); Say(w, L.T("ssh_no_pid", pid), "warn"); }
                continue;
            }
            if (alive && nowStart == start && J.Str(rec["boot"]) == boot) { said.Add($"{comm} ({pid})"); continue; }
            ended.Add(pid);
            Say(w, L.T(gap ? "ssh_proc_gap" : "ssh_proc_ended", w.Host, comm, pid), "info");
        }
        if (ended.Count > 0 || fresh.Count > 0)
            SaveWatched(w.Host, list =>
            {
                var a = new JsonArray();
                foreach (var x in list.OfType<JsonObject>())
                {
                    var p = (long)(J.Num(x["pid"]) ?? 0);
                    if (ended.Contains(p)) continue;
                    var c = (JsonObject)x.DeepClone();
                    foreach (var f in fresh.Where(f => f.Pid == p)) { c["start"] = f.Start; c["comm"] = f.Comm; c["boot"] = boot; }
                    a.Add(c);
                }
                return a;
            });
        if (said.Count > 0) parts.Add(L.T("ssh_watching", string.Join(", ", said.Take(5))));
    }

    // ------------------------------------------------------------ ssh.exe
    /// <summary>A Job object nobody else holds: when the hub ends in any way the last handle closes and Windows ends what is in it, a ProxyCommand ssh
    /// started too. A closed stdin isn't enough: ssh keeps waiting for a remote shell that hangs while its sshd answers keepalives. Best effort: without
    /// a job, ssh still works.</summary>
    static class DiesWithHub
    {
        const uint KillOnJobClose = 0x2000;
        const int JobObjectExtendedLimitInformation = 9;
        static readonly IntPtr Job = Create();

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
        [DllImport("kernel32", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);
        [DllImport("kernel32", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static IntPtr Create()
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            var limits = new ExtendedLimits { LimitFlags = KillOnJobClose };
            if (job == IntPtr.Zero || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()))
            {
                Log.W("ssh", "job: " + Marshal.GetLastWin32Error());
                return IntPtr.Zero;
            }
            return job;   // held for the hub's whole life, on purpose
        }

        public static void Add(Process p)
        {
            try
            {
                if (Job != IntPtr.Zero && !AssignProcessToJobObject(Job, p.Handle)) Log.W("ssh", "job: " + Marshal.GetLastWin32Error());
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }   // it has already exited
        }
    }

    /// <summary>One ssh.exe with a shell at the other end, which waits for the next tick's script. The closed stdin ends it if the hub dies.</summary>
    sealed class Conn : IDisposable
    {
        const int MaxLine = 4096, MaxLines = 5000;
        readonly Process p;
        readonly System.Collections.Concurrent.BlockingCollection<string> lines = new(MaxLines + 16);
        readonly StringBuilder err = new();
        public int? ExitCode;
        public bool TimedOut;
        public readonly ManualResetEvent Gone = new(false);   // never disposed: the reading thread sets it whenever it ends

        public Conn(string exe, string host)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var a in Args(host)) psi.ArgumentList.Add(a);
            psi.Environment["SSH_ASKPASS_REQUIRE"] = "never";
            psi.Environment["LC_ALL"] = "C";
            p = Process.Start(psi) ?? throw new IOException("ssh didn't start");
            DiesWithHub.Add(p);
            new Thread(ReadOut) { IsBackground = true }.Start();
            new Thread(ReadErr) { IsBackground = true }.Start();
        }

        /// <summary>Everything on the command line, so it beats the user's config: never a prompt, never a new trust, no forwarding. The -- keeps the host
        /// from being read as an option. known_hosts is the user's and only read.</summary>
        internal static string[] Args(string host) => new[]
        {
            "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "UpdateHostKeys=no", "-o", "VerifyHostKeyDNS=no",
            "-o", "ConnectTimeout=10", "-o", "ConnectionAttempts=1", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2",
            "-o", "ControlMaster=no", "-o", "ControlPath=none", "-o", "ForwardAgent=no", "-o", "ClearAllForwardings=yes", "-o", "PermitLocalCommand=no",
            "-o", "CheckHostIP=no", "-o", "ForwardX11=no", "-o", "Tunnel=no", "-o", "AddKeysToAgent=no",
            "-o", "PreferredAuthentications=publickey", "--", host, "sh -s",
        };

        void ReadOut()
        {
            try
            {
                var buf = new char[4096];
                var line = new StringBuilder();
                int n;
                var r = p.StandardOutput;
                while ((n = r.Read(buf, 0, buf.Length)) > 0)
                    for (int i = 0; i < n; i++)
                    {
                        var ch = buf[i];
                        if (ch == '\n')
                        {
                            lines.Add(line.ToString());
                            line.Clear();
                        }
                        else if (ch != '\r' && line.Length < MaxLine) line.Append(ch);
                    }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            try { lines.CompleteAdding(); } catch (ObjectDisposedException) { }
            Gone.Set();
        }

        void ReadErr()
        {
            try
            {
                var buf = new char[1024];
                int n;
                while ((n = p.StandardError.Read(buf, 0, buf.Length)) > 0)
                    lock (err)
                    {
                        err.Append(buf, 0, n);
                        if (err.Length > 8192) err.Remove(0, err.Length - 8192);
                    }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        /// <summary>Writes the script, reads the sections up to its end marker. Null: no answer in time, or the connection is gone.</summary>
        public Dictionary<string, List<string>> Tick(string script, string nonce, TimeSpan timeout, CancellationToken ct)
        {
            var m = "TH-" + nonce + "-";
            try
            {
                p.StandardInput.Write(script);
                p.StandardInput.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { return null; }
            var r = new Dictionary<string, List<string>>();
            List<string> cur = null;
            var until = DateTime.UtcNow + timeout;
            int count = 0;
            while (true)
            {
                var left = until - DateTime.UtcNow;
                string l;
                try
                {
                    if (left <= TimeSpan.Zero || !lines.TryTake(out l, (int)left.TotalMilliseconds, ct))
                    {
                        if (!lines.IsCompleted) TimedOut = true;
                        return null;
                    }
                }
                catch (InvalidOperationException) { return null; }
                if (++count > MaxLines) { TimedOut = true; return null; }
                if (l.StartsWith(m, StringComparison.Ordinal))
                {
                    var name = l[m.Length..].Trim();
                    if (name == "end") return r;
                    r[name] = cur = new List<string>();
                }
                else cur?.Add(l);   // what comes before the first marker is a greeting
            }
        }

        /// <summary>Ends it; ssh's own words.</summary>
        public string Stop()
        {
            try
            {
                if (!p.WaitForExit(TimedOut ? 0 : 1500)) p.Kill(true);
                p.WaitForExit(2000);
                ExitCode = p.ExitCode;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            lock (err) return err.ToString();
        }

        public void Dispose()
        {
            try { if (!p.HasExited) p.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
    }
}
