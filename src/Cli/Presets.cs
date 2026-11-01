using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHubCli;

/// <summary>
/// "taskhub run" presets: read a known tool's own output - progress, status, and errors the tool doesn't report through
/// its exit code. Chosen by --preset NAME, or by the command's file name when no --progress-regex / --status-regex /
/// --auto-progress is given; --preset none turns that off.
/// </summary>
interface IOutputPreset
{
    /// <summary>Widgets the task gets at registration (their values come from <see cref="Line"/>).</summary>
    IEnumerable<JsonObject> Widgets { get; }

    /// <summary>A line of the command's output (stdout or stderr, trimmed, under the runner's lock). Sets task fields
    /// (progress, status...) and widgets; returns the log entry to keep instead of the plain line, or null.</summary>
    JsonNode Line(string line, JsonObject fields, JsonObject widgets);

    /// <summary>At the end: (state, message) instead of the ones from the exit code, or null.</summary>
    (string State, string Message)? Verdict(int exitCode);
}

static class Presets
{
    public static readonly string[] Names = { "blender", "none" };

    /// <summary>The preset for a command; name null = by the command's file name. Throws ArgumentException for an
    /// unknown name.</summary>
    public static IOutputPreset For(string name, IReadOnlyList<string> cmd)
    {
        name = name?.Trim().ToLowerInvariant();
        if (name == "none") return null;
        if (name == "blender" || name == null && IsBlender(cmd[0])) return new BlenderPreset(cmd.Skip(1).ToList());
        if (name != null) throw new ArgumentException($"unknown preset '{name}' (known: {string.Join(", ", Names)})");
        return null;
    }

    static bool IsBlender(string exe)
    {
        try { return string.Equals(Path.GetFileNameWithoutExtension(exe), "blender", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>
/// Blender in background mode (blender -b file.blend [-s N] [-e N] [-j N] -a | -f N[,M..K]): the frame, the samples of
/// the frame, overall progress when the number of frames is known (from -s / -e / -f, or Blender 5's "Rendering animation
/// (frames a..b)"), the saved files; "Error: ..." lines (5.x: "| ERROR ...") fail the task - Blender itself exits with
/// 0 when a render fails. Formats: Blender 3.6-5.0 source (render/intern/pipeline.cc, cycles session, eevee instance).
/// </summary>
sealed class BlenderPreset : IOutputPreset
{
    static readonly Regex FrameRx = new(@"\bFra:\s?(-?\d+)\b");
    static readonly Regex SamplesRx = new(@"\bSample (\d+)/(\d+)|\bRendering (\d+) / (\d+) samples|\bRendered (\d+)/(\d+) Tiles");
    static readonly Regex SavedRx = new(@"\bSaved: '(.+)'");
    static readonly Regex SkipRx = new(@"^skipping existing frame ""(.+)""");
    static readonly Regex AnimRx = new(@"\bRendering animation \(frames (-?\d+)\.\.(-?\d+)\)");
    static readonly Regex SingleRx = new(@"\bRendering single frame\b");
    static readonly Regex TimeRx = new(@"(?:^|\|\s*)Time: (\d+(?::\d+){1,2}(?:\.\d+)?)");
    static readonly Regex ErrorRx = new(@"^Error: (.+)$|\|\s*ERROR\s+(.+)$");

    readonly int jump = 1;
    int total;            // frames to render, 0 = unknown
    int done;             // frames saved or skipped
    int frame = int.MinValue;
    double frac;          // samples of the current frame
    string samples = "", lastSaved = "", firstError, lastStatus;

    public BlenderPreset(IReadOnlyList<string> args)
    {
        int? start = null, end = null;
        bool anim = false;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i], next = i + 1 < args.Count ? args[i + 1] : null;
            switch (a)
            {
                case "-s": case "--frame-start": start = Int(next) ?? start; break;
                case "-e": case "--frame-end": end = Int(next) ?? end; break;
                case "-j": case "--frame-jump": if (Int(next) is > 0 and var j) jump = j; break;
                case "-a": case "--render-anim": anim = true; break;
                case "-f": case "--render-frame": total = FrameCount(next); break;
            }
        }
        if (anim && start is int s && end is int e && e >= s) total = (e - s) / jump + 1;
    }

    static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>-f 7 / -f 1,3,5 / -f 1..10 -> the number of frames; 0 when relative (+N / -N) or not readable.</summary>
    static int FrameCount(string spec)
    {
        if (string.IsNullOrEmpty(spec) || spec.StartsWith('+') || spec.StartsWith('-')) return 0;
        int n = 0;
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var r = part.Split("..");
            if (r.Length == 1 && Int(r[0]) != null) n++;
            else if (r.Length == 2 && Int(r[0]) is int a && Int(r[1]) is int b && b >= a) n += b - a + 1;
            else return 0;
        }
        return n;
    }

    public IEnumerable<JsonObject> Widgets => new[]
    {
        new JsonObject { ["id"] = "blender_spf", ["type"] = "chart", ["label"] = "s / frame" },
        new JsonObject { ["id"] = "blender_saved", ["type"] = "text", ["label"] = "Last saved", ["mono"] = true, ["span"] = 2 },
    };

    /// <summary>"04.12" / "01:04.12" / "1:01:04.12" -> seconds.</summary>
    static double Seconds(string t) =>
        t.Split(':').Aggregate(0.0, (acc, p) => acc * 60 + double.Parse(p, CultureInfo.InvariantCulture));

    public JsonNode Line(string line, JsonObject fields, JsonObject widgets)
    {
        JsonNode entry = null;
        Match m;
        if ((m = AnimRx.Match(line)).Success && total == 0 && Int(m.Groups[1].Value) is int s && Int(m.Groups[2].Value) is int e && e >= s)
            total = (e - s) / jump + 1;
        else if (SingleRx.IsMatch(line) && total == 0) total = 1;
        if ((m = FrameRx.Match(line)).Success && Int(m.Groups[1].Value) is int f)
        {
            if (f != frame) { frame = f; frac = 0; samples = ""; }
            if ((m = SamplesRx.Match(line)).Success)
            {
                var g = m.Groups[1].Success ? 1 : m.Groups[3].Success ? 3 : 5;
                double cur = double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture), tot = double.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture);
                if (tot > 0) { frac = Math.Clamp(cur / tot, 0, 1); samples = m.Value; }
            }
        }
        else if ((m = SavedRx.Match(line)).Success)
        {
            done++; frac = 0; samples = ""; lastSaved = m.Groups[1].Value;
            widgets["blender_saved"] = lastSaved;
        }
        else if ((m = TimeRx.Match(line)).Success) widgets["blender_spf"] = Math.Round(Seconds(m.Groups[1].Value), 2);
        else if (SkipRx.IsMatch(line)) { done++; frac = 0; samples = ""; }
        else if ((m = ErrorRx.Match(line)).Success)
        {
            var msg = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            firstError ??= msg;
            entry = new JsonObject { ["level"] = "error", ["msg"] = line };
        }

        if (frame != int.MinValue)
        {
            if (total > 0) fields["progress"] = Math.Round(Math.Min(1, (Math.Min(done, total) + frac) / total), 4);
            var parts = new List<string> { $"Frame {frame}" };
            if (total > 1) parts.Add($"{Math.Min(done + 1, total)}/{total}");
            if (samples.Length > 0) parts.Add(samples);
            var status = string.Join(" · ", parts);
            if (status != lastStatus) { fields["status"] = status; lastStatus = status; }
        }
        return entry;
    }

    public (string State, string Message)? Verdict(int exitCode)
    {
        if (exitCode != 0) return null;   // the usual "exit code N"
        if (firstError != null) return ("failed", "Blender: " + Cut(firstError, 200));
        if (done > 0) return ("done", done == 1 ? "1 frame · " + Cut(lastSaved, 200) : $"{done} frames · last: {Cut(lastSaved, 200)}");
        return null;
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
}
