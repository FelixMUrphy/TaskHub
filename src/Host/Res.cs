using System.Reflection;

namespace TaskHub;

/// <summary>
/// Files shipped inside the single-file exe (ui.html, API.md, taskhub_client.py, taskhub.exe CLI).
/// A file with the same name next to the exe wins (handy while developing ui.html).
/// </summary>
static class Res
{
    public static byte[] Read(string name, bool allowOverride = true)
    {
        if (allowOverride)
        {
            var p = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(p)) return File.ReadAllBytes(p);
        }
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static bool Has(string name) => Assembly.GetExecutingAssembly().GetManifestResourceInfo(name) != null;

    /// <summary>Write the payload (CLI, Python client, API doc) into a directory.</summary>
    public static void Extract(string dir)
    {
        foreach (var name in new[] { "taskhub.exe", "taskhub_client.py", "API.md" })
        {
            var data = Read(name, allowOverride: false);
            if (data == null) continue;
            var dst = Path.Combine(dir, name);
            for (int i = 0; ; i++)
            {
                try { File.WriteAllBytes(dst, data); break; }
                catch (IOException) when (i < 10) { Thread.Sleep(500); }   // CLI may be running
            }
        }
    }
}
