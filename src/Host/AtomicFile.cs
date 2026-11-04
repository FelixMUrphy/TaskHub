using System.Text;

namespace TaskHub;

/// <summary>TaskHub's own files that must survive a power cut and a damaged read. A write reaches the disk before it replaces the
/// file, and the previous version stays as ".bak"; a file that can't be read is kept as ".bad-(time)" before anything else touches it.</summary>
static class AtomicFile
{
    public static string Backup(string path) => path + ".bak";

    public static void Write(string path, string text)
    {
        var tmp = path + ".tmp";
        var bytes = new UTF8Encoding(false).GetBytes(text);
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(bytes);
            f.Flush(true);
        }
        if (File.Exists(path))
        {
            // not every file system takes it (network folders): then the plain way, the backup first
            try { File.Replace(tmp, path, Backup(path), true); return; }
            catch (Exception e) when (e is IOException or PlatformNotSupportedException or UnauthorizedAccessException) { }
            File.Copy(path, Backup(path), true);
        }
        File.Move(tmp, path, true);
    }

    /// <summary>Keeps a damaged file beside itself; the same damage found again at the next start adds no copy.</summary>
    public static void Quarantine(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            foreach (var old in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".bad-*"))
                if (File.ReadAllBytes(old).AsSpan().SequenceEqual(bytes)) return;
            File.WriteAllBytes($"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", bytes);
        }
        catch (Exception e) { Log.E("config", $"keeping a copy of {Path.GetFileName(path)}: {e.Message}"); }
    }
}
