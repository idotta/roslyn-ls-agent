namespace Csx;

internal static class PathUri
{
    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string ToPath(string uri) => new Uri(uri).LocalPath;

    /// <summary>Agents want repo-relative forward-slash paths, not absolute paths or URIs.</summary>
    public static string Relative(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel.StartsWith("..", StringComparison.Ordinal) ? path.Replace('\\', '/') : rel.Replace('\\', '/');
    }
}
