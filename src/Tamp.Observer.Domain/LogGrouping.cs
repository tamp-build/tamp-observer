using System.Text.RegularExpressions;

namespace Tamp.Observer.Domain;

/// <summary>
/// Builds the Issue grouping key for an error-severity LOG that has no <c>exception.type</c> (ADR 0015, TOBS-42).
/// Unlike an exception, a log line carries volatile literals: entry ids, record ids, row counts, quoted table or
/// column names that differ on every occurrence of the SAME error class (e.g. Sample's db.query validation
/// output). Grouping on the raw text would mint one Issue per line. We parameterize those literals so occurrences
/// collapse into one Issue, and fold in the logger category so distinct categories never share an Issue. The
/// prose that names the error class is preserved, so different classes stay separate.
/// </summary>
public static partial class LogGrouping
{
    /// <summary>The grouping key fed to <see cref="IssueFingerprint"/>: logger category + the normalized message.</summary>
    public static string Key(string? body, string? category)
    {
        var norm = Normalize(body);
        return string.IsNullOrEmpty(category) ? norm : $"{category}|{norm}";
    }

    /// <summary>A readable, representative Issue title from a raw log body (single line, length-bounded).</summary>
    public static string Title(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "log error";
        var oneLine = WhitespaceRegex().Replace(body.Trim(), " ");
        return oneLine.Length <= 140 ? oneLine : oneLine[..140];
    }

    /// <summary>
    /// Replace volatile NUMERIC literals (GUIDs, hex, bare numbers) with placeholders so every occurrence of one
    /// error class maps to one stable key. Quoted spans are deliberately NOT stripped: a quoted table/column/script
    /// name (e.g. `handler_beta` vs `handler_alpha`) is the class DISCRIMINATOR, not a volatile
    /// value — stripping it over-collapses distinct error classes into one Issue (the bug the live Sample restart
    /// corpus exposed, TOBS-42). Numbers inside quotes (ids like '45204') are still parameterized by the number
    /// pass, so a quoted literal value with digits still collapses while a pure-identifier name is preserved.
    /// </summary>
    public static string Normalize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "log-error";
        var s = body.Trim();
        s = GuidRegex().Replace(s, "#");
        s = HexRegex().Replace(s, "#");
        s = NumberRegex().Replace(s, "#");
        s = WhitespaceRegex().Replace(s, " ").Trim();
        return s.Length <= 200 ? s : s[..200];
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();

    [GeneratedRegex("0x[0-9a-fA-F]+")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
