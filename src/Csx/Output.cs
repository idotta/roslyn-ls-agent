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

    public static void WriteLocations(
        string root, IReadOnlyList<Location> locations, int max, int context, bool json)
    {
        var hits = locations
            .Select(l => new Hit(PathUri.ToPath(l.Uri), l.Range))
            .OrderBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Range.Start.Line)
            .ThenBy(h => h.Range.Start.Character)
            .ToList();

        var shown = hits.Take(max).ToList();

        if (json)
        {
            var payload = shown.Select(h => new
            {
                path = PathUri.Relative(root, h.Path),
                line = h.Range.Start.Line + 1,
                column = h.Range.Start.Character + 1,
                endLine = h.Range.End.Line + 1,
                endColumn = h.Range.End.Character + 1,
                text = LineAt(h.Path, h.Range.Start.Line)?.TrimEnd(),
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

        var first = true;
        foreach (var hit in shown)
        {
            if (!first) Console.WriteLine();
            first = false;

            var rel = PathUri.Relative(root, hit.Path);
            var line = hit.Range.Start.Line;
            Console.WriteLine($"{rel}:{line + 1}:{hit.Range.Start.Character + 1}");

            var lines = Lines(hit.Path);
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

    private static readonly Dictionary<string, string[]> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static string[] Lines(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        Cache[path] = lines;
        return lines;
    }

    private static string? LineAt(string path, int zeroBased)
    {
        var lines = Lines(path);
        return zeroBased >= 0 && zeroBased < lines.Length ? lines[zeroBased] : null;
    }

    private sealed record Hit(string Path, Range Range);
}
