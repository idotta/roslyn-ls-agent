using System.Text.RegularExpressions;

namespace Csx;

internal static partial class Program
{
    private const string Usage = """
        csx - semantic C# queries over the official roslyn-language-server

        usage:
          csx ready [--sentinel <symbol>]
          csx refs  <symbol | file:line:col> [--max N] [--context N]

        options:
          --root <dir>      workspace root (default: current directory)
          --sentinel <sym>  readiness probe symbol (default: inferred from the workspace)
          --max N           cap results (default: 50)
          --context N       source lines either side of a hit (default: 1)
          --timeout N       seconds to wait for workspace load (default: 180)
          --log-level L     server log level (default: Warning)
          --json            machine-readable output
        """;

    private static async Task<int> Main(string[] argv)
    {
        try
        {
            return await RunAsync(argv);
        }
        catch (CsxException ex)
        {
            Console.Error.WriteLine("csx: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return argv.Length == 0 ? 2 : 0;
        }

        var opts = Options.Parse(argv);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await using var client = await LspClient.StartAsync(opts.Root, opts.LogLevel, cts.Token);

        switch (opts.Command)
        {
            case "ready":
                await client.WaitReadyAsync(
                    opts.Sentinel ?? InferSentinel(opts.Root), opts.Timeout, cts.Token);
                Console.WriteLine("ready");
                return 0;

            case "refs":
                return await RefsAsync(client, opts, cts.Token);

            default:
                throw new CsxException($"unknown command '{opts.Command}'\n\n{Usage}");
        }
    }

    private static async Task<int> RefsAsync(LspClient client, Options opts, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CsxException("refs needs a symbol or file:line:col");

        // Gate on a sentinel that must exist, never on the symbol being asked about:
        // otherwise a genuinely absent symbol is indistinguishable from a workspace that
        // has not finished loading, and the caller waits out the whole timeout for it.
        await client.WaitReadyAsync(opts.Sentinel ?? InferSentinel(opts.Root), opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, ct);
        var locations = await client.ReferencesAsync(uri, position, ct);
        await Output.WriteLocationsAsync(
            opts.Root, locations, opts.Max, opts.Context, opts.Json, u => client.LinesAsync(u, ct));
        return locations.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// Returns a URI, not a path: a source-generated declaration has no path, and converting
    /// one to a path and back silently yields a different, nonexistent document.
    /// </summary>
    private static async Task<(string Uri, Position Position)> LocateAsync(
        LspClient client, string root, string target, CancellationToken ct)
    {
        if (TryParsePosition(target, out var file, out var line, out var column))
        {
            var full = Path.GetFullPath(Path.Combine(root, file));
            if (!File.Exists(full)) throw new CsxException($"no such file: {file}");
            return (PathUri.FromPath(full), new Position(line - 1, column - 1));
        }

        // The sentinel proves the workspace loaded, not that every project did. Give the
        // target a short grace period before calling it absent.
        var grace = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        IReadOnlyList<SymbolInformation> candidates;
        List<SymbolInformation> matches;
        while (true)
        {
            candidates = await client.SymbolsAsync(LastSegment(target), ct);
            matches = candidates
                .Where(s => Matches(s, target))
                .DistinctBy(s => (s.Location.Uri, s.Location.Range.Start.Line, s.Location.Range.Start.Character))
                .ToList();

            if (matches.Count > 0 || DateTime.UtcNow >= grace) break;
            await Task.Delay(250, ct);
        }

        if (matches.Count == 0)
        {
            var seen = candidates.Count == 0
                ? string.Empty
                : "\ncandidates:\n" + string.Join('\n', candidates.Select(c => "  " + FullName(c)));
            throw new CsxException($"no symbol matched '{target}'{seen}");
        }

        if (matches.Count > 1)
        {
            var listing = string.Join('\n', matches.Select(m =>
                $"  {FullName(m)}  {PathUri.Display(root, m.Location.Uri)}:{m.Location.Range.Start.Line + 1}"));
            throw new CsxException($"'{target}' is ambiguous; qualify it further:\n{listing}");
        }

        var match = matches[0];
        return (match.Location.Uri, match.Location.Range.Start);
    }

    /// <summary>
    /// Roslyn returns containerName as a localised display string ("in Greeter (project
    /// Core (net10.0))"), not a namespace path, so a dotted target can only narrow by the
    /// identifiers that appear in it — in practice the enclosing type. A target that stays
    /// ambiguous is reported with its candidates so the caller can fall back to file:line:col.
    /// </summary>
    private static bool Matches(SymbolInformation symbol, string target)
    {
        var segments = target.Split('.');
        if (!string.Equals(symbol.Name, segments[^1], StringComparison.Ordinal)) return false;
        if (segments.Length == 1) return true;

        var tokens = Identifier().Matches(symbol.ContainerName ?? string.Empty).Select(m => m.Value);
        return tokens.Contains(segments[^2], StringComparer.Ordinal);
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();

    private static string FullName(SymbolInformation s) =>
        string.IsNullOrEmpty(s.ContainerName) ? s.Name : $"{s.Name}  {s.ContainerName}";

    private static string LastSegment(string target)
    {
        var dot = target.LastIndexOf('.');
        return dot < 0 ? target : target[(dot + 1)..];
    }

    // Anchored on the last two colons so a Windows drive letter does not parse as a line.
    [GeneratedRegex(@"^(?<file>.+):(?<line>\d+):(?<col>\d+)$")]
    private static partial Regex PositionSpec();

    private static bool TryParsePosition(string spec, out string file, out int line, out int column)
    {
        var m = PositionSpec().Match(spec);
        if (m.Success)
        {
            file = m.Groups["file"].Value;
            line = int.Parse(m.Groups["line"].Value);
            column = int.Parse(m.Groups["col"].Value);
            return true;
        }

        (file, line, column) = (string.Empty, 0, 0);
        return false;
    }

    [GeneratedRegex(@"\b(?:class|struct|record|interface|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclaration();

    /// <summary>
    /// Picks a symbol that must resolve once the workspace is loaded. Any real type in the
    /// tree will do; the point is only that an empty answer means "not loaded yet". Sorted
    /// because <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> order is
    /// filesystem-defined, and readiness must not depend on which file it happens to hit —
    /// adding a project to a workspace would otherwise silently change the sentinel.
    /// </summary>
    private static string InferSentinel(string root)
    {
        foreach (var file in Directory
                     .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var m = TypeDeclaration().Match(File.ReadAllText(file));
            if (m.Success) return m.Groups["name"].Value;
        }

        throw new CsxException($"could not infer a readiness sentinel under {root}; pass --sentinel");
    }

    private sealed record Options(
        string Command,
        string? Argument,
        string Root,
        string? Sentinel,
        int Max,
        int Context,
        TimeSpan Timeout,
        string LogLevel,
        bool Json)
    {
        public static Options Parse(string[] argv)
        {
            string command = argv[0];
            string? argument = null;
            var root = Directory.GetCurrentDirectory();
            string? sentinel = null;
            var max = Output.DefaultMax;
            var context = 1;
            var timeout = TimeSpan.FromSeconds(180);
            var logLevel = "Warning";
            var json = false;

            for (var i = 1; i < argv.Length; i++)
            {
                switch (argv[i])
                {
                    case "--root": root = Path.GetFullPath(Next(argv, ref i)); break;
                    case "--sentinel": sentinel = Next(argv, ref i); break;
                    case "--max": max = int.Parse(Next(argv, ref i)); break;
                    case "--context": context = int.Parse(Next(argv, ref i)); break;
                    case "--timeout": timeout = TimeSpan.FromSeconds(int.Parse(Next(argv, ref i))); break;
                    case "--log-level": logLevel = Next(argv, ref i); break;
                    case "--json": json = true; break;
                    default:
                        if (argv[i].StartsWith('-')) throw new CsxException($"unknown option '{argv[i]}'");
                        if (argument is not null) throw new CsxException($"unexpected argument '{argv[i]}'");
                        argument = argv[i];
                        break;
                }
            }

            if (!Directory.Exists(root)) throw new CsxException($"no such directory: {root}");
            return new Options(command, argument, root, sentinel, max, context, timeout, logLevel, json);
        }

        private static string Next(string[] argv, ref int i)
        {
            if (++i >= argv.Length) throw new CsxException($"option '{argv[i - 1]}' needs a value");
            return argv[i];
        }
    }
}
