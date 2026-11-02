using TaskHub;

namespace TaskHubCli;

/// <summary>The CLI's own failures, for diagnosis: hooks and the MCP server have nowhere else to say them. Written only in a beta build
/// or when the hub's config.json has "log_level": "debug"; called on failure paths only, and never allowed to fail.</summary>
static class CliLog
{
    const long MaxBytes = 1_000_000;

    static bool Enabled()
    {
#if BETA
        return true;
#else
        return string.Equals(Program.S(Client.HubConfig()?["log_level"]), "debug", StringComparison.OrdinalIgnoreCase);
#endif
    }

    public static void W(string area, string msg)
    {
        try
        {
            if (!Enabled()) return;
            var path = Path.Combine(Client.DataFolder(), "cli.log");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxBytes) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} WRN {area,-8} [{Environment.ProcessId}] {LogMask.Apply(msg ?? "")}{Environment.NewLine}");
        }
        catch { }
    }
}
