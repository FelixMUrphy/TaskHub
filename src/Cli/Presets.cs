using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHubCli;

/// <summary>
/// "taskhub run" presets: read a known tool's own output - progress, status, and errors the tool doesn't report through
/// its exit code - and add its own buttons. Chosen by --preset NAME, or by the command's file name when no
/// --progress-regex / --status-regex / --auto-progress is given; --preset none turns that off.
/// The runner calls every member under its lock (output threads and the control thread).
/// </summary>
interface IOutputPreset
{
    /// <summary>Widgets the task gets at registration (their values come from <see cref="Line"/>).</summary>
    IEnumerable<JsonObject> Widgets { get; }

    /// <summary>Buttons the task gets at registration (kind "signal"; pressed ones come to <see cref="OnControl"/>).</summary>
    IEnumerable<JsonObject> Controls { get; }

    /// <summary>A line of the command's output (stdout or stderr, trimmed). Sets task fields (progress, status...) and
    /// widgets; returns the log entry to keep instead of the plain line, or null.</summary>
    JsonNode Line(string line, JsonObject fields, JsonObject widgets);

    /// <summary>One of its buttons pressed: (ok, reply), or null when the id isn't its own.</summary>
    (bool Ok, string Reply)? OnControl(string id, JsonNode value);

    /// <summary>True once the command should be ended (e.g. "stop after this frame" and the frame is saved).</summary>
    bool WantsStop { get; }

    /// <summary>At the end: (state, message) instead of the ones from the exit code, or null.</summary>
    (string State, string Message)? Verdict(int exitCode);

    /// <summary>At the end: a command line that continues the work (printed to stderr), or null.</summary>
    string ResumeCommand { get; }
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
        if (name == "blender" || name == null && IsBlender(cmd[0])) return new BlenderPreset(cmd);
        if (name != null) throw new ArgumentException($"unknown preset '{name}' (known: {string.Join(", ", Names)})");
        return null;
    }

    static bool IsBlender(string exe)
    {
        try { return string.Equals(Path.GetFileNameWithoutExtension(exe), "blender", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>One argument for a Windows command line, read back the same by CommandLineToArgvW / the C runtime:
    /// backslashes before a quote and at the end of a quoted argument are doubled.</summary>
    public static string Quote(string s)
    {
        if (s.Length > 0 && !s.Any(ch => char.IsWhiteSpace(ch) || ch == '"')) return s;
        var sb = new System.Text.StringBuilder("\"");
        int bs = 0;
        foreach (var ch in s)
        {
            if (ch == '\\') { bs++; continue; }
            sb.Append('\\', ch == '"' ? bs * 2 + 1 : bs).Append(ch);
            bs = 0;
        }
        return sb.Append('\\', bs * 2).Append('"').ToString();
    }
}

/// <summary>
/// Blender in background mode (blender -b file.blend [-s N] [-e N] [-j N] -a | -f N[,M..K]): the frame, the samples of
/// the frame, overall progress when the number of frames is known (from -s / -e / -f, or Blender 5's "Rendering animation
/// (frames a..b)"), seconds per frame and render memory charts, the saved files; "Error: ..." lines (5.x: "| ERROR ...")
/// fail the task - Blender itself exits with 0 when a render fails. "Stop after this frame" ends Blender right after the
/// next saved frame; after a stop or a crash in an animation the task tells (and the runner prints) how to resume:
/// the same command with -s set to the next frame. No add-on inside Blender: only its output is read.
/// Formats: Blender 3.6-5.0 source (render/intern/pipeline.cc, cycles session, eevee instance, image_save.cc).
/// </summary>
sealed class BlenderPreset : IOutputPreset
{
    static readonly Regex FrameRx = new(@"\bFra:\s?(-?\d+)\b");
    static readonly Regex SamplesRx = new(@"\bSample (\d+)/(\d+)|\bRendering (\d+) / (\d+) samples|\bRendered (\d+)/(\d+) Tiles");
    static readonly Regex SavedRx = new(@"\bSaved: '(.+)'");
    // a frame written to a video (FFmpeg): "Append frame N" (3.6-4.x), "render | Video append frame N" (5.0)
    static readonly Regex AppendRx = new(@"\b(?:Video a|A)ppend frame (-?\d+)\b");
    static readonly Regex SkipRx = new(@"^skipping existing frame ""(.+)""");
    static readonly Regex AnimRx = new(@"\bRendering animation \(frames (-?\d+)\.\.(-?\d+)\)");
    static readonly Regex SingleRx = new(@"\bRendering single frame\b");
    static readonly Regex TimeRx = new(@"(?:^|\|\s*)Time: (\d+(?::\d+){1,2}(?:\.\d+)?)");
    static readonly Regex ErrorRx = new(@"^Error: (.+)$|\|\s*ERROR\s+(.+)$");
    // printed at exit by Blender's leak detector, release builds too (intern/guardedalloc/intern/leak_detector.cc): no failure
    const string LeakPrefix = "Not freed memory blocks";
    // render memory: Cycles' own "| Mem:120.40M, Peak:..." (4.x) / "| Mem: 120M |" (5.x), else Blender's "Fra:1 Mem:210.33M"
    static readonly Regex EngineMemRx = new(@"\|\s*Mem:\s?(\d+(?:\.\d+)?)M");
    static readonly Regex FraMemRx = new(@"\bFra:\s?-?\d+ Mem:(\d+(?:\.\d+)?)M");

    const string StopId = "blender_stop";
    readonly IReadOnlyList<string> cmd;
    readonly int jump = 1;
    readonly bool anim;
    int total;            // frames to render, 0 = unknown
    int done;             // frames saved or skipped
    int frame = int.MinValue, savedFrame = int.MinValue;
    double frac;          // samples of the current frame
    bool stopAfterFrame, stopArmed, stopNow;   // armed: a frame was saved after the press; stop when the next one begins
    string samples = "", lastSaved = "", firstError, lastStatus;

    public BlenderPreset(IReadOnlyList<string> command)
    {
        cmd = command;
        int? start = null, end = null;
        for (int i = 1; i < cmd.Count; i++)
        {
            string a = cmd[i], next = i + 1 < cmd.Count ? cmd[i + 1] : null;
            if (a == "--") break;   // the rest goes to Python scripts, not to Blender
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
        new JsonObject { ["id"] = "blender_mem", ["type"] = "chart", ["label"] = "Memory", ["unit"] = "MB" },
        new JsonObject { ["id"] = "blender_saved", ["type"] = "text", ["label"] = "Last saved", ["mono"] = true, ["span"] = 2 },
    };

    // only where there is more than one frame (or the number isn't known yet)
    public IEnumerable<JsonObject> Controls => total == 1 ? Array.Empty<JsonObject>() : new[]
    {
        new JsonObject { ["id"] = StopId, ["label"] = "Stop after this frame", ["kind"] = "signal", ["toast"] = true },
    };

    public (bool Ok, string Reply)? OnControl(string id, JsonNode value)
    {
        if (id != StopId) return null;
        if (stopAfterFrame) return (true, "already stopping after this frame");
        stopAfterFrame = true;
        return (true, frame == int.MinValue ? "will stop after the first frame" : $"will stop after frame {frame}");
    }

    public bool WantsStop => stopNow;

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
            // the next frame begins: a pending "stop after this frame" ends Blender now (every file / view of the saved
            // frame is written by then; a last frame just ends normally)
            if (stopArmed && f != savedFrame) stopNow = true;
            if (f != frame) { frame = f; frac = 0; samples = ""; }
            if ((m = SamplesRx.Match(line)).Success)
            {
                var g = m.Groups[1].Success ? 1 : m.Groups[3].Success ? 3 : 5;
                double cur = double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture), tot = double.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture);
                if (tot > 0) { frac = Math.Clamp(cur / tot, 0, 1); samples = m.Value; }
            }
            var mem = EngineMemRx.Match(line) is { Success: true } em ? em : FraMemRx.Match(line);
            if (mem.Success) widgets["blender_mem"] = Math.Round(double.Parse(mem.Groups[1].Value, CultureInfo.InvariantCulture), 1);
        }
        else if ((m = SavedRx.Match(line)).Success) FrameSaved(frame, m.Groups[1].Value, widgets);
        else if ((m = AppendRx.Match(line)).Success && Int(m.Groups[1].Value) is int af)
            FrameSaved(af, $"frame {af} → video", widgets);
        else if ((m = TimeRx.Match(line)).Success) widgets["blender_spf"] = Math.Round(Seconds(m.Groups[1].Value), 2);
        else if (SkipRx.IsMatch(line)) { done++; frac = 0; samples = ""; }
        else if ((m = ErrorRx.Match(line)).Success)
        {
            var msg = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            bool leak = msg.StartsWith(LeakPrefix, StringComparison.Ordinal);
            if (!leak) firstError ??= msg;
            entry = new JsonObject { ["level"] = leak ? "warn" : "error", ["msg"] = line };
        }

        if (frame != int.MinValue)
        {
            if (total > 0) fields["progress"] = Math.Round(Math.Min(1, (Math.Min(done, total) + frac) / total), 4);
            var parts = new List<string> { $"Frame {frame}" };
            if (total > 1) parts.Add($"{Math.Min(done + 1, total)}/{total}");
            if (samples.Length > 0) parts.Add(samples);
            if (stopAfterFrame) parts.Add("stopping after this frame");
            var status = string.Join(" · ", parts);
            if (status != lastStatus) { fields["status"] = status; lastStatus = status; }
        }
        return entry;
    }

    /// <summary>A file of frame <paramref name="f"/> written (an image, one view of a stereo pair, or a video frame).
    /// Another file of the same frame (stereo "Individual": one per view) doesn't count as another frame.</summary>
    void FrameSaved(int f, string what, JsonObject widgets)
    {
        if (f == int.MinValue || f != savedFrame) done++;
        if (f != int.MinValue) savedFrame = f;
        frac = 0; samples = ""; lastSaved = what;
        widgets["blender_saved"] = lastSaved;
        if (stopAfterFrame) stopArmed = true;
    }

    /// <summary>The frame to continue from: an animation with at least one frame saved, not all of them.</summary>
    int? NextFrame => anim && savedFrame != int.MinValue && (total == 0 || done < total) ? savedFrame + jump : null;

    string Saved => total > 0 ? $"{Math.Min(done, total)}/{total} saved" : $"{done} saved";

    bool resumable;

    public (string State, string Message)? Verdict(int exitCode)
    {
        resumable = NextFrame != null && (stopNow || exitCode != 0 || firstError != null);
        if (stopNow)   // ended by "stop after this frame"
            return ("cancelled", $"Stopped after frame {savedFrame} · {Saved}" + (NextFrame is int nx ? $" · resume: -s {nx}" : ""));
        if (exitCode != 0)   // crashed / killed: what was saved, where to go on
            return NextFrame is int nf ? ("failed", $"exit code {exitCode} · {Saved} · resume: -s {nf}") : null;
        if (firstError != null)
            return ("failed", "Blender: " + Cut(firstError, 200) + (NextFrame is int ne ? $" · {Saved} · resume: -s {ne}" : ""));
        if (done > 0) return ("done", done == 1 ? "1 frame · " + Cut(lastSaved, 200) : $"{done} frames · last: {Cut(lastSaved, 200)}");
        return null;
    }

    /// <summary>After a stop, a crash or an error in an animation: the same command with -s set to the next frame
    /// (inserted before -a when it had none). Valid after <see cref="Verdict"/>.</summary>
    public string ResumeCommand
    {
        get
        {
            if (!resumable || NextFrame is not int nf) return null;
            var a = cmd.ToList();
            int end = a.IndexOf("--");   // Blender's own arguments only: after "--" they belong to Python scripts
            if (end < 0) end = a.Count;
            int si = a.FindIndex(0, end, x => x is "-s" or "--frame-start");
            if (si > 0 && si + 1 < end) a[si + 1] = nf.ToString(CultureInfo.InvariantCulture);
            else
            {
                int ai = a.FindIndex(0, end, x => x is "-a" or "--render-anim");
                a.InsertRange(ai, new[] { "-s", nf.ToString(CultureInfo.InvariantCulture) });
            }
            return string.Join(" ", a.Select(Presets.Quote));
        }
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
}
