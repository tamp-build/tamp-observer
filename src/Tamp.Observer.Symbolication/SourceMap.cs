using System.Text.Json;

namespace Tamp.Observer.Symbolication;

/// <summary>
/// A parsed Source Map v3 (ADR 0017), decoded in-house from the base64-VLQ mappings. Resolves a generated
/// (line, column) to the original source position. Zero-based throughout, matching the spec.
/// </summary>
internal sealed class SourceMap
{
    private const string Base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    private readonly string[] _sources;
    private readonly string[] _names;
    private readonly List<Segment>[] _lines;

    private readonly record struct Segment(int GenCol, int SrcIdx, int OrigLine, int OrigCol, int NameIdx, bool HasSource, bool HasName);

    private SourceMap(string[] sources, string[] names, List<Segment>[] lines)
    {
        _sources = sources;
        _names = names;
        _lines = lines;
    }

    public static SourceMap Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sources = root.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
        var names = root.TryGetProperty("names", out var n) && n.ValueKind == JsonValueKind.Array
            ? n.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
        var mappings = root.TryGetProperty("mappings", out var m) ? m.GetString() ?? "" : "";

        var lineStrings = mappings.Split(';');
        var lines = new List<Segment>[lineStrings.Length];

        // srcIdx / origLine / origCol / nameIdx are deltas across the whole mappings; genCol resets per line.
        int srcIdx = 0, origLine = 0, origCol = 0, nameIdx = 0;
        for (var l = 0; l < lineStrings.Length; l++)
        {
            var segments = new List<Segment>();
            var genCol = 0;
            foreach (var seg in lineStrings[l].Split(','))
            {
                if (seg.Length == 0)
                    continue;
                var f = DecodeVlq(seg);
                genCol += f[0];
                if (f.Count >= 4)
                {
                    srcIdx += f[1];
                    origLine += f[2];
                    origCol += f[3];
                    if (f.Count >= 5)
                    {
                        nameIdx += f[4];
                        segments.Add(new Segment(genCol, srcIdx, origLine, origCol, nameIdx, true, true));
                    }
                    else
                    {
                        segments.Add(new Segment(genCol, srcIdx, origLine, origCol, 0, true, false));
                    }
                }
                else
                {
                    segments.Add(new Segment(genCol, 0, 0, 0, 0, false, false));
                }
            }
            lines[l] = segments;
        }

        return new SourceMap(sources, names, lines);
    }

    public bool TryResolve(int generatedLine, int generatedColumn, out (string Source, int Line, int Column, string? Name) result)
    {
        result = default;
        if (generatedLine < 0 || generatedLine >= _lines.Length)
            return false;

        // Segments are in ascending generated-column order: take the last one at or before the target.
        Segment? best = null;
        foreach (var seg in _lines[generatedLine])
        {
            if (!seg.HasSource || seg.GenCol > generatedColumn)
                continue;
            best = seg;
        }

        if (best is not { } match || match.SrcIdx < 0 || match.SrcIdx >= _sources.Length)
            return false;

        string? name = match.HasName && match.NameIdx >= 0 && match.NameIdx < _names.Length ? _names[match.NameIdx] : null;
        result = (_sources[match.SrcIdx], match.OrigLine, match.OrigCol, name);
        return true;
    }

    private static List<int> DecodeVlq(string segment)
    {
        var values = new List<int>();
        var i = 0;
        while (i < segment.Length)
        {
            int value = 0, shift = 0;
            bool continuation;
            do
            {
                var digit = Base64.IndexOf(segment[i++]);
                if (digit < 0)
                    throw new FormatException($"invalid base64-VLQ character '{segment[i - 1]}'");
                continuation = (digit & 32) != 0;
                value += (digit & 31) << shift;
                shift += 5;
            }
            while (continuation);

            // Lowest bit is the sign.
            values.Add((value & 1) != 0 ? -(value >> 1) : value >> 1);
        }
        return values;
    }
}
