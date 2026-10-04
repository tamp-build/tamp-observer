using System.Text.RegularExpressions;

namespace Tamp.Observer.Api;

// Stack-trace parsing for the issue-detail panel (TOBS-27). Turns a raw exception.stacktrace string (as captured
// on the occurrence: a .NET ToString() or a browser Error.stack) into structured frames for display. True
// symbolication (source maps / portable PDB -> source context) is a follow-up; frames here are as-captured, so
// Symbolicated is reported false and the UI links to the Symbols settings.

public sealed record StackTraceView(
    string? ErrorType, string? Message, bool Symbolicated, string? Source,
    IReadOnlyList<StackFrameView> Frames, string? Raw);

public sealed record StackFrameView(string Function, string? File, int? Line, bool InApp);

public static partial class StackTraceParser
{
    // .NET:  "   at Ns.Type.Method(ArgType a) in C:\path\File.cs:line 42"
    [GeneratedRegex(@"^\s*at\s+(?<fn>.+?)(?:\s+in\s+(?<file>.+):line\s+(?<line>\d+))?\s*$")]
    private static partial Regex DotNet();

    // JS:  "    at fn (https://host/app.js:12:34)"  or  "    at https://host/app.js:12:34"
    [GeneratedRegex(@"^\s*at\s+(?:(?<fn>.+?)\s+\()?(?<file>[^\s()]+?):(?<line>\d+):\d+\)?\s*$")]
    private static partial Regex Js();

    private static readonly string[] Framework =
        ["System.", "Microsoft.", "Npgsql.", "node:", "node_modules", "internal/", "anonymous"];

    public static IReadOnlyList<StackFrameView> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        var frames = new List<StackFrameView>();
        foreach (var line in raw.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith("at ", StringComparison.Ordinal))
                continue;

            var js = Js().Match(text);
            var net = DotNet().Match(text);
            string fn;
            string? file;
            int? ln = null;

            if (js.Success && js.Groups["file"].Value.Contains(':'))
            {
                fn = js.Groups["fn"].Success ? js.Groups["fn"].Value : "(anonymous)";
                file = Shorten(js.Groups["file"].Value);
                if (int.TryParse(js.Groups["line"].Value, out var l)) ln = l;
            }
            else if (net.Success)
            {
                fn = net.Groups["fn"].Value.Trim();
                file = net.Groups["file"].Success ? Shorten(net.Groups["file"].Value) : null;
                if (net.Groups["line"].Success && int.TryParse(net.Groups["line"].Value, out var l)) ln = l;
            }
            else
            {
                continue;
            }

            var inApp = !Framework.Any(p => fn.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                                            || (file?.Contains(p, StringComparison.OrdinalIgnoreCase) ?? false));
            frames.Add(new StackFrameView(fn, file, ln, inApp));
        }
        return frames;
    }

    private static string Shorten(string file)
    {
        // Keep the last path/URL segment for readability.
        var slash = file.LastIndexOfAny(['/', '\\']);
        return slash >= 0 && slash < file.Length - 1 ? file[(slash + 1)..] : file;
    }
}
