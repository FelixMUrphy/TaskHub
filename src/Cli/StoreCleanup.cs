using System.Text.Json.Nodes;
using Microsoft.Win32;
using TaskHub;

namespace TaskHubCli;

/// <summary>
/// The Store edition points the AI agents at a copy of this exe outside its package (%LOCALAPPDATA%\TaskHub\bin, with
/// store.json naming the package next to it; Install.EnsureStoreCli in the hub). Windows runs nothing of a package when
/// it is removed, so when an agent later starts this copy (`taskhub mcp` / `taskhub hook`) and the package is gone,
/// the copy removes TaskHub's entries from the agents' settings. The copy itself stays (no self-deletion tricks: antivirus
/// heuristics flag them); with the entries gone nothing starts it again, and the setup-exe edition removes it.
/// </summary>
static class StoreCleanup
{
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Package family name if this exe is the Store edition's copy, else null.</summary>
    public static string PackageFamily()
    {
        try
        {
            var marker = Path.Combine(AppContext.BaseDirectory, "store.json");
            if (!File.Exists(marker)) return null;
            return (JsonNode.Parse(File.ReadAllText(marker)) as JsonObject)?["package_family"] is JsonValue v
                   && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    /// <summary>True if TaskHub is gone and the agents' entries were removed (the caller then does nothing else).</summary>
    public static bool Run()
    {
        var pfn = PackageFamily();
        if (pfn == null || PackageInstalled(pfn) || SetupEditionInstalled()) return false;
        foreach (var e in AgentFiles.RemoveAll()) Console.Error.WriteLine("taskhub cleanup: " + e);
        return true;
    }

    // the package's data folder and its execution alias folder both go with the package
    static bool PackageInstalled(string pfn) =>
        Directory.Exists(Path.Combine(Local, "Packages", pfn)) || Directory.Exists(Path.Combine(Local, "Microsoft", "WindowsApps", pfn));

    /// <summary>The setup-exe edition took over: its hub points the agents at itself on its next start and removes this copy.</summary>
    static bool SetupEditionInstalled()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME"))) return false;   // tests on a PC with TaskHub
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskHub");
            return k?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, "TaskHubHost.exe"));
        }
        catch { return false; }
    }
}
