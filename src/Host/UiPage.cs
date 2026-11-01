using System.Text;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>
/// The panel page (/ui): ui/index.html with each line "@@include part@@" replaced by the file ui/part (CSS / JS parts,
/// embedded as resources "ui/&lt;name&gt;"). A whole ui.html next to the exe still wins (development), and so does a single
/// part placed in a "ui" folder next to the exe (Res.Read override). Translations: i18n/*.json.
/// </summary>
static class UiPage
{
    static readonly Regex Include = new(@"^@@include ([A-Za-z0-9_.-]+)@@\r?\n", RegexOptions.Multiline);

    public static string Html()
    {
        if (Res.Read("ui.html") is { } whole) return FillI18n(Encoding.UTF8.GetString(whole));
        var index = Res.Read("ui/index.html");
        if (index == null) return "";
        var html = Include.Replace(Encoding.UTF8.GetString(index), m =>
            Res.Read("ui/" + m.Groups[1].Value) is { } part ? Encoding.UTF8.GetString(part)
                : throw new InvalidOperationException("ui part missing: " + m.Groups[1].Value));
        return FillI18n(html);
    }

    /// <summary>"const I18N = __I18N__;" (ui/i18n.js) gets the panel's translations from i18n/*.json.</summary>
    static string FillI18n(string html)
    {
        const string slot = "const I18N = __I18N__;";
        var i = html.IndexOf(slot, StringComparison.Ordinal);
        return i < 0 ? html : html[..i] + "const I18N = " + I18n.PanelJson(L.Langs) + ";" + html[(i + slot.Length)..];
    }
}
