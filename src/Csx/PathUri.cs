namespace Csx;

internal static class PathUri
{
    /// <summary>
    /// Roslyn reports source-generated documents under its own scheme. Nothing is on disk,
    /// and <c>new Uri(u).LocalPath</c> does not throw for one — it returns a path-shaped
    /// string ("/BuildInfo.g.cs") that then renders as a confident wrong answer, so every
    /// URI-to-path conversion has to check this first.
    /// </summary>
    public const string GeneratedScheme = "roslyn-source-generated";

    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string ToPath(string uri) => new Uri(uri).LocalPath;

    public static bool IsGenerated(string uri) =>
        uri.StartsWith(GeneratedScheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>Agents want repo-relative forward-slash paths, not absolute paths or URIs.</summary>
    public static string Relative(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel.StartsWith("..", StringComparison.Ordinal) ? path.Replace('\\', '/') : rel.Replace('\\', '/');
    }

    /// <summary>
    /// The display form for any location. A generated URI carries an authority guid and a
    /// documentId that are both regenerated on every workspace load, plus a machine-absolute
    /// assemblyPath, so the label is built only from the fields that are stable across runs
    /// and machines. The angle brackets keep it from being mistaken for a readable file.
    /// </summary>
    public static string Display(string root, string uri)
    {
        if (!IsGenerated(uri)) return Relative(root, ToPath(uri));

        var query = Query(uri);
        var assembly = query.GetValueOrDefault("assemblyName", "?");
        var hint = query.GetValueOrDefault("hintName") ?? ToPath(uri).TrimStart('/');
        return $"<generated>/{assembly}/{hint}";
    }

    private static Dictionary<string, string> Query(string uri)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in new Uri(uri).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) pairs[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return pairs;
    }
}
