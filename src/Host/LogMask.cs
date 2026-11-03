using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>What must never reach a log file, whatever a message happens to contain. Shared with the CLI.</summary>
static class LogMask
{
    static readonly (Regex rx, string to)[] Masks =
    {
        (new Regex(@"bot\d+:[A-Za-z0-9_-]+", RegexOptions.Compiled), "bot***"),
        (new Regex(@"\b\d{6,12}:[A-Za-z0-9_-]{30,}", RegexOptions.Compiled), "***"),                       // a Telegram token without its prefix
        (new Regex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.Compiled), "***"),
        (new Regex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b(?::[^\s""']+)?", RegexOptions.Compiled), "***"),                // alone or as id:secret
        (new Regex(@"\b[A-Fa-f0-9]{64,}\b", RegexOptions.Compiled), "***"),                                         // a long hex key; a 40-digit commit stays
        (new Regex(@"\brpa_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "***"),
        (new Regex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled), "$1 ***"),
        (new Regex(@"(?i)\b(token|api_key|apikey|secret|secret_access_key|aws_secret_access_key|password)=[^\s&""']+", RegexOptions.Compiled), "$1=***"),
        (new Regex(@"(?i)""(token|api_key|apikey|secret|secret_access_key|password)""\s*:\s*""[^""]*""", RegexOptions.Compiled), "\"$1\":\"***\""),
    };

    public static string Apply(string s)
    {
        foreach (var (rx, to) in Masks) s = rx.Replace(s, to);
        return s;
    }
}
