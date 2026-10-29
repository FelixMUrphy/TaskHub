using Microsoft.Win32;

namespace TaskHub;

/// <summary>HKCU-only registration (no admin): autostart Run key and the legacy taskhub:// protocol.</summary>
static class Install
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ProtoKey = @"Software\Classes\taskhub";
    static string Exe => Environment.ProcessPath;

    /// <summary>"TaskHub" for the default port (replaces the Python entry), "TaskHub-PORT" otherwise.</summary>
    public static string RunValueName(Settings s) => s.Port == Settings.DefaultPort ? Settings.AppId : $"{Settings.AppId}-{s.Port}";

    public static bool AutostartEnabled(Settings s)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        var v = k?.GetValue(RunValueName(s)) as string;
        return v != null && v.Contains(Path.GetFileName(Exe), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutostart(bool on, Settings s)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunValueName(s), $"\"{Exe}\"");
        else if (AutostartEnabled(s)) k.DeleteValue(RunValueName(s), false);
    }

    public static void SetProtocol(bool on)
    {
        if (on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(ProtoKey);
            k.SetValue("", "URL:TaskHub");
            k.SetValue("URL Protocol", "");
            using var c = Registry.CurrentUser.CreateSubKey(ProtoKey + @"\shell\open\command");
            c.SetValue("", $"\"{Exe}\" --open-url \"%1\"");
        }
        else
        {
            using var c = Registry.CurrentUser.OpenSubKey(ProtoKey + @"\shell\open\command");
            var v = c?.GetValue("") as string;
            c?.Dispose();
            if (v != null && v.Contains(Path.GetFileName(Exe), StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(ProtoKey, false);
        }
    }

    public static string Describe(Settings s)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        using var p = Registry.CurrentUser.OpenSubKey(ProtoKey + @"\shell\open\command");
        return $"Run[{RunValueName(s)}] = {k?.GetValue(RunValueName(s)) ?? "(none)"}\n" +
               $"taskhub:// = {p?.GetValue("") ?? "(none)"}";
    }
}
