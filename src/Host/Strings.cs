using System.Globalization;

namespace TaskHub;

/// <summary>Host-side UI strings (tray menu, tooltip, toasts). Texts: i18n/*.json ("host" tables); the panel gets the "panel" tables.</summary>
static class L
{
    public static readonly string[] Langs = { "en", "uk", "es", "de", "fr", "it", "pt", "pl", "zh", "ja" };
    public static readonly Dictionary<string, string> Names = new()
    {
        ["en"] = "English", ["uk"] = "Українська", ["es"] = "Español", ["de"] = "Deutsch", ["fr"] = "Français",
        ["it"] = "Italiano", ["pt"] = "Português", ["pl"] = "Polski", ["zh"] = "中文", ["ja"] = "日本語",
    };

    public static Settings S;
    public const string RepoUrl = "https://github.com/FelixMUrphy/TaskHub";
    public const string SupportUrl = "https://github.com/sponsors/FelixMUrphy";

    /// <summary>Setting value: "auto" or a language code.</summary>
    public static string Setting
    {
        get
        {
            var v = S == null ? "auto" : J.Str(S.Get("language"), "auto").ToLowerInvariant();
            return v == "auto" || Langs.Contains(v) ? v : "auto";
        }
    }

    /// <summary>Effective language code.</summary>
    public static string Lang
    {
        get
        {
            var v = Setting;
            if (v != "auto") return v;
            var sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return Langs.Contains(sys) ? sys : "en";
        }
    }

    // key -> text per language (order of Langs); the source is i18n/*.json (embedded), see I18n.cs
    static readonly Dictionary<string, string[]> T_ = I18n.Host(Langs);

    public static string T(string key, params object[] a)
    {
        if (!T_.TryGetValue(key, out var arr)) return key;
        var i = Array.IndexOf(Langs, Lang);
        var s = arr[i < 0 ? 0 : i];
        return a.Length > 0 ? string.Format(s, a) : s;
    }

    /// <summary>A count in words: the entry holds the plural forms separated by "|" ({0} = n, {1}... = the extra args).
    /// Forms per language: en de es it pt fr: one|other; uk pl: one|few|many; zh ja: one form for everything.
    /// The same rules as plural() in ui/i18n.js.</summary>
    public static string N(string key, long n, params object[] a)
    {
        if (!T_.TryGetValue(key, out var arr)) return key;
        var i = Array.IndexOf(Langs, Lang);
        var forms = arr[i < 0 ? 0 : i].Split('|');
        var s = forms[Math.Min(PluralForm(i < 0 ? "en" : Langs[i], n), forms.Length - 1)];
        return string.Format(s, new object[] { n }.Concat(a).ToArray());
    }

    /// <summary>Which plural form a count takes (CLDR cardinal rules for integers): 0 = one, 1 = other / few, 2 = many.</summary>
    public static int PluralForm(string lang, long n)
    {
        n = Math.Abs(n);
        long d = n % 10, h = n % 100;
        switch (lang)
        {
            case "zh": case "ja": return 0;
            case "fr": return n <= 1 ? 0 : 1;   // 0 and 1 are singular in French
            case "uk": return d == 1 && h != 11 ? 0 : d is >= 2 and <= 4 && h is < 12 or > 14 ? 1 : 2;
            case "pl": return n == 1 ? 0 : d is >= 2 and <= 4 && h is < 12 or > 14 ? 1 : 2;
            default: return n == 1 ? 0 : 1;
        }
    }
}
