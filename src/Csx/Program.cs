using System.Text.RegularExpressions;

namespace Csx;

internal static partial class Program
{
    private const string Usage = """
        csx - semantic C# queries over the official roslyn-language-server

        usage:
          csx ready   [--sentinel <symbol>]
          csx refs    <symbol | file:line:col> [--max N] [--context N]
          csx def     <symbol | file:line:col> [--max N] [--context N]
          csx outline <file | symbol> [--max N]
          csx diag    [path] [--errors-only] [--max N] [--context N]

        options:
          --root <dir>      workspace root (default: current directory)
          --sentinel <sym>  readiness probe symbol (default: inferred from the workspace)
          --max N           cap results (default: 50)
          --context N       source lines either side of a hit (default: 1; unused by outline)
          --timeout N       seconds to wait for workspace load (default: 180)
          --log-level L     server log level (default: Warning)
          --errors-only     diag: drop warnings and below
          --json            machine-readable output
          --daemon          use the shared multi-client server daemon
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

        await using var client = await LspClient.StartAsync(opts.Root, opts.LogLevel, opts.Daemon, cts.Token);

        switch (opts.Command)
        {
            case "ready":
                await client.WaitReadyAsync(
                    opts.Sentinel ?? InferSentinel(opts.Root), opts.Timeout, cts.Token);
                Console.WriteLine("ready");
                return 0;

            case "refs":
                return await RefsAsync(client, opts, cts.Token);

            case "def":
                return await DefAsync(client, opts, cts.Token);

            case "outline":
                return await OutlineAsync(client, opts, cts.Token);

            case "diag":
                return await DiagAsync(client, opts, cts.Token);

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

    private static async Task<int> DefAsync(LspClient client, Options opts, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CsxException("def needs a symbol or file:line:col");

        await client.WaitReadyAsync(opts.Sentinel ?? InferSentinel(opts.Root), opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, ct);
        var locations = await client.DefinitionAsync(uri, position, ct);
        await Output.WriteLocationsAsync(
            opts.Root, locations, opts.Max, opts.Context, opts.Json, u => client.LinesAsync(u, ct));
        return locations.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// Exits 0 for a document with no symbols, like <c>diag</c> and unlike <c>refs</c>: an
    /// empty file is a query that was answered. A target that fails to resolve still exits 1,
    /// by throwing out of the resolver.
    /// </summary>
    private static async Task<int> OutlineAsync(LspClient client, Options opts, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CsxException("outline needs a file or symbol");

        await client.WaitReadyAsync(opts.Sentinel ?? InferSentinel(opts.Root), opts.Timeout, ct);

        var uri = await OutlineTargetAsync(client, opts.Root, target, ct);
        var symbols = await client.DocumentSymbolsAsync(uri, ct);
        await Output.WriteOutlineAsync(
            opts.Root, uri, symbols, opts.Max, opts.Json, u => client.LinesAsync(u, ct));
        return 0;
    }

    /// <summary>
    /// A file path, a <c>file:line:col</c> spec (whose document is outlined, so a position
    /// copied from a <c>def</c> result works), or a symbol whose declaring document is
    /// outlined — the last being the only route to a source-generated document, which has no
    /// path. Anything file-shaped is resolved as a file and nothing else: letting a mistyped
    /// path fall through to the symbol resolver produces "no symbol matched 'Core/Missing.cs'"
    /// and a candidate dump, when the answer is that the file is not there.
    /// </summary>
    private static async Task<string> OutlineTargetAsync(
        LspClient client, string root, string target, CancellationToken ct)
    {
        var path = TryParsePosition(target, out var file, out _, out _) ? file : target;
        var full = Path.GetFullPath(Path.Combine(root, path));

        if (File.Exists(full)) return PathUri.FromPath(full);
        if (Directory.Exists(full)) throw new CsxException($"outline needs a file, not a directory: {path}");
        if (LooksLikePath(path)) throw new CsxException($"no such file: {path}");

        // Overloads are not ambiguity here: several matches that share a document all outline
        // to the same thing, so collapse by document and only complain if they really differ.
        var matches = await MatchSymbolsAsync(client, target, ct);
        var uris = matches.Select(m => m.Location.Uri).Distinct(StringComparer.Ordinal).ToList();
        if (uris.Count > 1)
        {
            var listing = string.Join('\n', uris.Select(u => "  " + PathUri.Display(root, u)));
            throw new CsxException($"'{target}' is declared in several documents; pick one:\n{listing}");
        }

        return uris[0];
    }

    private static bool LooksLikePath(string target) =>
        target.Contains('/') || target.Contains('\\') ||
        target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Exit code reports whether the query was answered, not whether the workspace is clean:
    /// a repo with no diagnostics is a successful `diag`, unlike an empty `refs`, which means
    /// the lookup failed.
    /// </summary>
    private static async Task<int> DiagAsync(LspClient client, Options opts, CancellationToken ct)
    {
        await client.WaitReadyAsync(opts.Sentinel ?? InferSentinel(opts.Root), opts.Timeout, ct);

        var findings = new List<(string Uri, Diagnostic Diagnostic)>();
        if (opts.Argument is { } target)
        {
            var full = Path.GetFullPath(Path.Combine(opts.Root, target));
            if (Directory.Exists(full))
            {
                foreach (var uri in SourceFiles(full).Select(PathUri.FromPath))
                {
                    findings.AddRange((await client.DiagnosticsAsync(uri, ct)).Select(d => (uri, d)));
                }
            }
            else if (File.Exists(full))
            {
                var uri = PathUri.FromPath(full);
                findings.AddRange((await client.DiagnosticsAsync(uri, ct)).Select(d => (uri, d)));
            }
            else
            {
                throw new CsxException($"no such file or directory: {target}");
            }
        }
        else
        {
            // Per file, not workspace/diagnostic: that endpoint answers but returns zero
            // reports, which is what workspaceDiagnostics: false in its dynamic registration
            // means. Verified against 5.12.0-1.26426.8.
            foreach (var uri in SourceFiles(opts.Root).Select(PathUri.FromPath))
            {
                findings.AddRange((await client.DiagnosticsAsync(uri, ct)).Select(d => (uri, d)));
            }
        }

        if (opts.ErrorsOnly)
        {
            findings.RemoveAll(f => Output.Severity(f.Diagnostic.Severity) != "error");
        }

        await Output.WriteDiagnosticsAsync(
            opts.Root, findings, opts.Max, opts.Context, opts.Json, u => client.LinesAsync(u, ct));
        return 0;
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

        var matches = await MatchSymbolsAsync(client, target, ct);
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
    /// Every symbol matching <paramref name="target"/>, deduplicated by location. Callers
    /// decide what more than one means: for <c>refs</c> and <c>def</c> it is ambiguity, for
    /// <c>outline</c> it is only ambiguity when the documents differ.
    /// </summary>
    private static async Task<List<SymbolInformation>> MatchSymbolsAsync(
        LspClient client, string target, CancellationToken ct)
    {
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

        return matches;
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
        foreach (var file in SourceFiles(root))
        {
            var m = TypeDeclaration().Match(File.ReadAllText(file));
            if (m.Success) return m.Groups["name"].Value;
        }

        throw new CsxException($"could not infer a readiness sentinel under {root}; pass --sentinel");
    }

    /// <summary>
    /// The workspace's own C# files. Sorted because
    /// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> order is
    /// filesystem-defined, and neither the inferred sentinel nor the order diagnostics are
    /// pulled in should depend on which file it happens to hit first.
    /// </summary>
    private static IEnumerable<string> SourceFiles(string root) => Directory
        .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Order(StringComparer.OrdinalIgnoreCase);

    private sealed record Options(
        string Command,
        string? Argument,
        string Root,
        string? Sentinel,
        int Max,
        int Context,
        TimeSpan Timeout,
        string LogLevel,
        bool ErrorsOnly,
        bool Json,
        bool Daemon)
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
            var errorsOnly = false;
            var json = false;
            var daemon = false;

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
                    case "--errors-only": errorsOnly = true; break;
                    case "--json": json = true; break;
                    case "--daemon": daemon = true; break;
                    default:
                        if (argv[i].StartsWith('-')) throw new CsxException($"unknown option '{argv[i]}'");
                        if (argument is not null) throw new CsxException($"unexpected argument '{argv[i]}'");
                        argument = argv[i];
                        break;
                }
            }

            if (!Directory.Exists(root)) throw new CsxException($"no such directory: {root}");
            return new Options(
                command, argument, root, sentinel, max, context, timeout, logLevel, errorsOnly, json,
                daemon);
        }

        private static string Next(string[] argv, ref int i)
        {
            if (++i >= argv.Length) throw new CsxException($"option '{argv[i - 1]}' needs a value");
            return argv[i];
        }
    }
}
