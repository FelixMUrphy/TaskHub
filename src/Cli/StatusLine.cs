using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskHub;

namespace TaskHubCli;

/// <summary>
/// `taskhub statusline`: Claude Code's status line command when "plan limits" is on. Claude Code hands it JSON on stdin.
/// It runs the user's own status line (kept by AgentFiles when the switch was turned on) with the same input and returns its
/// output and exit code unchanged, and tells the hub only the used percentages and reset times of the plan's limit windows:
/// nothing else of the input (folders, session, transcript) leaves this process. Without a status line of the user's it prints
/// nothing. It never starts the hub, and a hub that is down costs a blank reading, not a broken line.
/// </summary>
static class StatusLine
{
    const int WrappedCapMs = 15000;   // only against a command that hangs: Claude Code cancels a slow one with its next update
    const int DrainMs = 300;          // output after the command ended (a background child may hold the pipe for long)
    const int PostWaitMs = 400;       // the line shows only when this exits, so the hub doesn't get to hold it up
    const string Inside = "TASKHUB_IN_STATUSLINE";   // set for the user's command: if that calls us again we don't go round in circles

    public static int Run(Func<Client> client)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Inside))) return 0;
        JsonObject wrapped = null;
        try { wrapped = AgentFiles.WrappedStatusLine(); } catch { }
        bool gone = false;
        try { gone = StoreCleanup.Run(); } catch { }   // the Store edition's copy after an uninstall: this restored the user's entry

        var input = ReadInput();
        Task post = gone ? null : Post(client, input);
        var rc = 0;
        try { rc = RunWrapped(wrapped, input); } catch { }
        try { post?.Wait(PostWaitMs); } catch { }
        return rc;
    }

    static byte[] ReadInput()
    {
        try
        {
            if (!Console.IsInputRedirected) return Array.Empty<byte>();
            var ms = new MemoryStream();
            var read = Console.OpenStandardInput().CopyToAsync(ms);
            return read.Wait(3000) ? ms.ToArray() : Array.Empty<byte>();
        }
        catch { return Array.Empty<byte>(); }
    }

    static Task Post(Func<Client> client, byte[] input)
    {
        var body = Reading(input);
        return body == null ? null : Task.Run(() =>
        {
            try { client().Call("POST", "/api/limits/claude", body, 2); } catch (Exception e) { CliLog.W("status", e.Message); }
        });
    }

    /// <summary>The only part of the input the hub gets: numbers of the two limit windows. Null if there are none (no plan with
    /// limits, or before the first answer of the API).</summary>
    public static JsonObject Reading(byte[] input)
    {
        try
        {
            if (input.Length == 0 || JsonNode.Parse(input) is not JsonObject o || o["rate_limits"] is not JsonObject rl) return null;
            var r = new JsonObject();
            foreach (var w in new[] { "five_hour", "seven_day" })
                if (rl[w] is JsonObject x && Num(x["used_percentage"]) is double p)
                {
                    var one = new JsonObject { ["used_percentage"] = p };
                    if (Num(x["resets_at"]) is double at) one["resets_at"] = at;
                    r[w] = one;
                }
            return r.Count > 0 ? r : null;
        }
        catch { return null; }
    }

    static double? Num(JsonNode n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) ? d : null;

    static int RunWrapped(JsonObject wrapped, byte[] input)
    {
        var command = Program.S(wrapped?["command"]);
        if (string.IsNullOrWhiteSpace(command)) return 0;
        var (shell, args) = ShellFor(command);
        var psi = new System.Diagnostics.ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[Inside] = "1";
        using var p = System.Diagnostics.Process.Start(psi);
        if (p == null) return 0;
        var output = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(output);
        _ = Task.Run(() =>
        {
            try { p.StandardInput.BaseStream.Write(input); p.StandardInput.Close(); } catch { }   // it needn't read it
        });
        var rc = 0;
        var ended = p.WaitForExitAsync();
        if (Task.WaitAny(new Task[] { ended }, WrappedCapMs) < 0)
        {
            try { p.Kill(true); } catch { }
            try { copy.Wait(1000); } catch { }
        }
        else
        {
            rc = p.ExitCode;
            try { copy.Wait(DrainMs); } catch { }
        }
        var bytes = output.ToArray();
        using var o = Console.OpenStandardOutput();
        o.Write(bytes);
        o.Flush();
        return rc;
    }

    /// <summary>The shell the user's command was written for: the one Claude Code started us in. If we weren't started by a shell,
    /// the way Claude Code picks one on Windows: Git Bash when installed, else PowerShell.</summary>
    static (string shell, string[] args) ShellFor(string command)
    {
        var parent = ParentExe();
        var name = Path.GetFileNameWithoutExtension(parent ?? "").ToLowerInvariant();
        if (name is "bash" or "sh") return (parent, new[] { "-c", command });
        if (name is "powershell" or "pwsh") return (parent, PowerShellArgs(command));
        var bash = GitBash();
        // the full path: by its bare name Windows would look in the current folder first, which may be any repository
        return bash != null ? (bash, new[] { "-c", command }) : (Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), PowerShellArgs(command));
    }

    static string[] PowerShellArgs(string command) => new[] { "-NoProfile", "-NonInteractive", "-Command", command };

    static string GitBash()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } set && File.Exists(set)) return set;
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try
            {
                if (d.Trim().Length == 0 || !File.Exists(Path.Combine(d.Trim(), "git.exe"))) continue;
                // <Git>\cmd\git.exe or <Git>\mingw64\bin\git.exe -> <Git>\bin\bash.exe
                foreach (var up in new[] { "..", Path.Combine("..", "..") })
                {
                    var bash = Path.GetFullPath(Path.Combine(d.Trim(), up, "bin", "bash.exe"));
                    if (File.Exists(bash)) return bash;
                }
            }
            catch { }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicInfo { public IntPtr R1, Peb, R2a, R2b, Pid, ParentPid; }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int cls, ref BasicInfo info, int size, out int ret);

    static string ParentExe()
    {
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            var info = new BasicInfo();
            if (NtQueryInformationProcess(me.Handle, 0, ref info, Marshal.SizeOf<BasicInfo>(), out _) != 0) return null;
            using var parent = System.Diagnostics.Process.GetProcessById((int)info.ParentPid);
            return parent.MainModule?.FileName;
        }
        catch { return null; }
    }
}
