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

    /// <summary>
    /// A search result set is a list of places to go, not a place to read, so this is the
    /// renderer that prints no source line and no <c>&gt;</c> marker: <c>--context</c> is
    /// inert for it. Everything else in DESIGN.md's output rules still holds — root-relative
    /// path, one-based line and column, capped by <paramref name="max"/>, the same JSON
    /// envelope. No <c>|</c> appears in a row, unlike an outline's gutter, so a probe case
    /// can quote one whole. containerName is Roslyn's localised display text ("in Greeter
    /// (project Core (net10.0))"), not a namespace path; it is rendered because it is the
    /// only thing separating two symbols that share a name, and never asserted on, because
    /// DOTNET_CLI_UI_LANGUAGE pins its language but nothing pins its shape.
    /// </summary>
    public static void WriteSymbols(
        string root, IReadOnlyList<SymbolInformation> symbols, int max, bool json)
    {
        var hits = symbols
            .Select(s => new Match(PathUri.Display(root, s.Location.Uri), s))
            .OrderBy(h => h.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Display, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Symbol.Location.Range.Start.Line)
            .ToList();

        var shown = hits.Take(max).ToList();

        if (json)
        {
            var payload = shown.Select(h => new
            {
                name = h.Symbol.Name,
                kind = Kind(h.Symbol.Kind),
                container = h.Symbol.ContainerName,
                path = h.Display,
                line = h.Symbol.Location.Range.Start.Line + 1,
                column = h.Symbol.Location.Range.Start.Character + 1,
                generated = PathUri.IsGenerated(h.Symbol.Location.Uri),
            });

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

        var kindWidth = shown.Max(h => Kind(h.Symbol.Kind).Length);
        var nameWidth = shown.Max(h => h.Symbol.Name.Length);
        var containerWidth = shown.Max(h => (h.Symbol.ContainerName ?? string.Empty).Length);
        foreach (var hit in shown)
        {
            var start = hit.Symbol.Location.Range.Start;
            var row =
                $"{Kind(hit.Symbol.Kind).PadRight(kindWidth)}  " +
                $"{hit.Symbol.Name.PadRight(nameWidth)}  " +
                $"{(hit.Symbol.ContainerName ?? string.Empty).PadRight(containerWidth)}  " +
                $"{hit.Display}:{start.Line + 1}:{start.Character + 1}";
            Console.WriteLine(row);
        }

        if (hits.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"... {hits.Count - shown.Count} more (use --max {hits.Count} to see all)");
        }
    }

    /// <summary>
    /// The one command that does not print path:line plus context per row — see DESIGN.md.
    /// An outline is already the summary, so it renders as a tree: the document path once as
    /// a header, then one row per symbol carrying that declaration's own source line,
    /// indented by nesting depth. <paramref name="max"/> caps rows over the pre-order
    /// flattening, so a truncated tree is always a prefix and no node outlives its parent.
    /// </summary>
    public static async Task WriteOutlineAsync(
        string root,
        string uri,
        IReadOnlyList<DocumentSymbol> symbols,
        int max,
        bool json,
        Func<string, Task<string[]>> linesOf)
    {
        var display = PathUri.Display(root, uri);
        var total = Count(symbols);
        var kept = Math.Min(max, total);
        var lines = await linesOf(uri);

        if (json)
        {
            var budget = kept;
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    count = total,
                    truncated = total > kept,
                    path = display,
                    generated = PathUri.IsGenerated(uri),
                    results = Nodes(symbols, lines, ref budget),
                },
                JsonOut));
            return;
        }

        Console.WriteLine(display);
        if (total == 0)
        {
            Console.WriteLine("no symbols");
            return;
        }

        var rows = Flatten(symbols).Take(kept).ToList();
        var width = rows.Max(r => r.Symbol.SelectionRange.Start.Line + 1).ToString().Length;
        foreach (var row in rows)
        {
            var line = row.Symbol.SelectionRange.Start.Line;
            var text = At(lines, line)?.Trim() ?? row.Symbol.Name;
            Console.WriteLine($"  {(line + 1).ToString().PadLeft(width)} | {new string(' ', row.Depth * 2)}{text}");
        }

        if (total > kept)
        {
            Console.WriteLine();
            Console.WriteLine($"... {total - kept} more (use --max {total} to see all)");
        }
    }

    private static int Count(IReadOnlyList<DocumentSymbol> symbols) =>
        symbols.Sum(s => 1 + Count(s.Children ?? []));

    private static IEnumerable<(DocumentSymbol Symbol, int Depth)> Flatten(
        IReadOnlyList<DocumentSymbol> symbols, int depth = 0)
    {
        foreach (var symbol in symbols)
        {
            yield return (symbol, depth);
            foreach (var child in Flatten(symbol.Children ?? [], depth + 1)) yield return child;
        }
    }

    // Pruned against the same pre-order budget the text form uses, so --max means the same
    // thing in both and a JSON case and a text case stay cases about the same output.
    private static List<object> Nodes(IReadOnlyList<DocumentSymbol> symbols, string[] lines, ref int budget)
    {
        var nodes = new List<object>();
        foreach (var symbol in symbols)
        {
            if (budget <= 0) break;
            budget--;

            var start = symbol.SelectionRange.Start;
            var end = symbol.SelectionRange.End;
            nodes.Add(new
            {
                name = symbol.Name,
                kind = Kind(symbol.Kind),
                detail = symbol.Detail,
                line = start.Line + 1,
                column = start.Character + 1,
                endLine = end.Line + 1,
                endColumn = end.Character + 1,
                text = At(lines, start.Line)?.TrimEnd(),
                children = Nodes(symbol.Children ?? [], lines, ref budget),
            });
        }

        return nodes;
    }

    // LSP SymbolKind. An unmapped value renders as its integer rather than "unknown": the
    // server is free to add kinds, and dropping the one fact we have helps nobody.
    private static string Kind(int kind) => kind switch
    {
        1 => "file",
        2 => "module",
        3 => "namespace",
        4 => "package",
        5 => "class",
        6 => "method",
        7 => "property",
        8 => "field",
        9 => "constructor",
        10 => "enum",
        11 => "interface",
        12 => "function",
        13 => "variable",
        14 => "constant",
        15 => "string",
        16 => "number",
        17 => "boolean",
        18 => "array",
        19 => "object",
        20 => "key",
        21 => "null",
        22 => "enumMember",
        23 => "struct",
        24 => "event",
        25 => "operator",
        26 => "typeParameter",
        _ => kind.ToString(),
    };

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

    private sealed record Match(string Display, SymbolInformation Symbol);
}
