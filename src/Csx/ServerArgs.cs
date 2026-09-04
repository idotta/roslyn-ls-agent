namespace Csx;

/// <summary>
/// The one place server invocation lives. The thin client forwards options it does
/// not recognise straight through to the underlying server, so a renamed flag fails
/// silently rather than erroring — probes/ is the only thing that catches that.
/// </summary>
internal static class ServerArgs
{
    public const string Command = "dotnet";

    /// <summary>Milestone 1: a dedicated child server over stdio.</summary>
    public static string[] Stdio(string logLevel) =>
    [
        "tool", "run", "roslyn-language-server",
        "--stdio",
        "--autoLoadProjects",
        "--logLevel", logLevel,
    ];

    /// <summary>
    /// Milestone 3: connect to (or start) the shared multi-client daemon. The thin client
    /// takes <c>--daemon-mode</c>; the server's own equivalent is <c>--daemon</c>, which the
    /// thin client passes to a detached double launch of its own. Deliberately no
    /// <c>--clientProcessId</c>: the server exits when that process does, which is the whole
    /// point of a daemon. Note the daemon is scoped by user and server path only, so the
    /// first client's <c>--autoLoadProjects</c> / <c>--logLevel</c> configure it for every
    /// later client, whatever they ask for.
    /// </summary>
    public static string[] Daemon(string logLevel) =>
    [
        "tool", "run", "roslyn-language-server",
        "--daemon-mode",
        "--stdio",
        "--autoLoadProjects",
        "--logLevel", logLevel,
    ];

    /// <summary>
    /// LSP 3.17 position encoding. The server does not advertise
    /// <c>positionEncoding</c> in its initialize result, which per spec means
    /// utf-16 — the same unit as a .NET string index. Anything else would break
    /// column math on non-ASCII lines, so we assert rather than adapt.
    /// </summary>
    public const string ExpectedPositionEncoding = "utf-16";

    /// <summary>Walks up from the running binary to the directory holding the tool manifest.</summary>
    public static string ToolManifestRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".config", "dotnet-tools.json")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new CsxException("Could not locate .config/dotnet-tools.json above " + AppContext.BaseDirectory);
    }
}

internal sealed class CsxException(string message) : Exception(message);
