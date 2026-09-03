using System.Text.Encodings.Web;
using System.Text.Json;

namespace Csx;

/// <summary>
/// Raw LSP hands back URIs and zero-based line/character ranges, which is close to
/// useless to a model. Everything an agent sees goes through here instead: repo-relative
/// path, one-based line, the matched source line and a line of context either side.
/// </summary>
internal static class Output
{
    public const int DefaultMax = 50;

    // Source lines are full of quotes and angle brackets; the default encoder turns them
    // into " noise that a model then has to decode.
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// <paramref name="linesOf"/> resolves a URI to its source lines. Text is not read here
    /// because a generated document has no file behind it — only the server can supply it —
    /// and it is fetched lazily so a capped result set costs no requests for hits it drops.
    /// </summary>
    public static async Task WriteLocationsAsync(
        string root,
        IReadOnlyList<Location> locations,
        int max,
        int context,
        bool json,
        Func<string, Task<string[]>> linesOf)
    {
        var hits = locations
            .Select(l => new Hit(l.Uri, PathUri.Display(root, l.Uri), l.Range))
            .OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Range.Start.Line)
            .ThenBy(h => h.Range.Start.Character)
            .ToList();

        var shown = hits.Take(max).ToList();

        if (json)
        {
            var payload = new List<object>(shown.Count);
            foreach (var hit in shown)
            {
                payload.Add(new
                {
                    path = hit.Display,
                    line = hit.Range.Start.Line + 1,
                    column = hit.Range.Start.Character + 1,
                    endLine = hit.Range.End.Line + 1,
                    endColumn = hit.Range.End.Character + 1,
                    generated = PathUri.IsGenerated(hit.Uri),
                    text = At(await linesOf(hit.Uri), hit.Range.Start.Line)?.TrimEnd(),
                });
            }

            Console.WriteLine(JsonSerializer.Serialize(
                new { count = hits.Count, truncated = hits.Count > shown.Count, results = payload },
                JsonOut));
            return;
        }

        if (shown.Count == 0)
        {
            Console.WriteLine("no results");
            return;
        }

        var first = true;
        foreach (var hit in shown)
        {
            if (!first) Console.WriteLine();
            first = false;

            var line = hit.Range.Start.Line;
            Console.WriteLine($"{hit.Display}:{line + 1}:{hit.Range.Start.Character + 1}");

            var lines = await linesOf(hit.Uri);
            var width = (line + 1 + context).ToString().Length;
            for (var i = Math.Max(0, line - context); i <= Math.Min(lines.Length - 1, line + context); i++)
            {
                var marker = i == line ? ">" : " ";
                Console.WriteLine($"{marker} {(i + 1).ToString().PadLeft(width)} | {lines[i].TrimEnd()}");
            }
        }

        if (hits.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"... {hits.Count - shown.Count} more (use --max {hits.Count} to see all)");
        }
    }

    /// <summary>
    /// Diagnostics follow the same rules as locations: repo-relative path, one-based
    /// line and column, the offending line and a line of context either side.
    /// </summary>
    public static async Task WriteDiagnosticsAsync(
        string root,
        IReadOnlyList<(string Uri, Diagnostic Diagnostic)> findings,
        int max,
        int context,
        bool json,
        Func<string, Task<string[]>> linesOf)
    {
        var hits = findings
            .Select(f => new Finding(f.Uri, PathUri.Display(root, f.Uri), f.Diagnostic))
            .OrderBy(f => f.Display, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Diagnostic.Range.Start.Line)
            .ThenBy(f => f.Diagnostic.Range.Start.Character)
            .ToList();

        var shown = hits.Take(max).ToList();

        if (json)
        {
            var payload = new List<object>(shown.Count);
            foreach (var hit in shown)
            {
                var range = hit.Diagnostic.Range;
                payload.Add(new
                {
                    path = hit.Display,
                    line = range.Start.Line + 1,
                    column = range.Start.Character + 1,
                    endLine = range.End.Line + 1,
                    endColumn = range.End.Character + 1,
                    severity = Severity(hit.Diagnostic.Severity),
                    code = Code(hit.Diagnostic.Code),
                    source = hit.Diagnostic.Source,
                    message = hit.Diagnostic.Message,
                    generated = PathUri.IsGenerated(hit.Uri),
                    text = At(await linesOf(hit.Uri), range.Start.Line)?.TrimEnd(),
                });
            }

            Console.WriteLine(JsonSerializer.Serialize(
                new { count = hits.Count, truncated = hits.Count > shown.Count, results = payload },
                JsonOut));
            return;
        }

        if (shown.Count == 0)
        {
            Console.WriteLine("no diagnostics");
            return;
        }

        var first = true;
        foreach (var hit in shown)
        {
            if (!first) Console.WriteLine();
            first = false;

            var start = hit.Diagnostic.Range.Start;
            var code = Code(hit.Diagnostic.Code);
            var label = code is null ? Severity(hit.Diagnostic.Severity) : $"{Severity(hit.Diagnostic.Severity)} {code}";
            Console.WriteLine($"{hit.Display}:{start.Line + 1}:{start.Character + 1} {label}: {hit.Diagnostic.Message}");

            var lines = await linesOf(hit.Uri);
            var width = (start.Line + 1 + context).ToString().Length;
            for (var i = Math.Max(0, start.Line - context); i <= Math.Min(lines.Length - 1, start.Line + context); i++)
            {
                var marker = i == start.Line ? ">" : " ";
                Console.WriteLine($"{marker} {(i + 1).ToString().PadLeft(width)} | {lines[i].TrimEnd()}");
            }
        }

        if (hits.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"... {hits.Count - shown.Count} more (use --max {hits.Count} to see all)");
        }
    }

    public static string Severity(int? severity) => severity switch
    {
        1 => "error",
        2 => "warning",
        3 => "info",
        4 => "hint",
        // Absent means "as the client sees fit"; treat it as the worst case rather than
        // hiding it from --errors-only.
        _ => "error",
    };

    // string-or-int per the spec, and Roslyn's own analyzers are free to use either.
    private static string? Code(JsonElement code) => code.ValueKind switch
    {
        JsonValueKind.String => code.GetString(),
        JsonValueKind.Number => code.ToString(),
        _ => null,
    };

    private static string? At(string[] lines, int zeroBased) =>
        zeroBased >= 0 && zeroBased < lines.Length ? lines[zeroBased] : null;

    private sealed record Hit(string Uri, string Display, Range Range);

    private sealed record Finding(string Uri, string Display, Diagnostic Diagnostic);
}
