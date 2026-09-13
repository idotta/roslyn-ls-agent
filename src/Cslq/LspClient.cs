using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using PolyType;
using StreamJsonRpc;

namespace Cslq;

internal sealed partial class LspClient : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly JsonRpc _rpc;
    private readonly Endpoints _endpoints;
    private readonly StringBuilder _stderr;
    private readonly Dictionary<string, Staleness.Stamp?> _open = new(PathUri.PathComparer);
    private readonly Dictionary<string, (Staleness.Stamp? Stamp, string[] Lines)> _lines =
        new(PathUri.PathComparer);
    private readonly Dictionary<string, IReadOnlyList<DocumentContext>> _contexts = new(PathUri.PathComparer);
    /// <summary>
    /// The sentinels already proved against the workspace this attach holds. A sentinel enters
    /// it by having resolved — a <c>workspace/symbol</c> hit this client's own
    /// <see cref="Sentinel.Accepts"/> scoped to that project's directory — and nothing can make
    /// that observation false again while the client lives, so the round is not paid twice.
    /// An instance field rather than a static: it has to die with the attach, so a session that
    /// re-attaches after the project graph changed leaves the old client's proofs behind.
    /// </summary>
    private readonly HashSet<string> _proved = new(StringComparer.Ordinal);

    private readonly bool _daemon;
    private readonly CancellationToken _ct;

    private static readonly TimeSpan BindBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long <see cref="WaitReadyAsync"/> keeps asking after this process saw
    /// <c>projectInitializationComplete</c>. Generous against the one thing that could still
    /// be in flight — the indexer catching up on a large solution — and short against the
    /// case it exists for, a candidate that can never resolve holding the full timeout.
    /// </summary>
    private static readonly TimeSpan PostLoadGrace = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the <c>dotnet --version</c> of <see cref="SdkAsync"/> gets. It is a diagnostic
    /// on a path that has already failed, so it is bounded well below anything a caller would
    /// notice and the answer to overrunning it is to say nothing.
    /// </summary>
    private static readonly TimeSpan SdkBudget = TimeSpan.FromSeconds(10);

    private const int HandleFlagInherit = 0x1;

    public string Root { get; }

    private LspClient(
        string root, Process proc, JsonRpc rpc, Endpoints endpoints, StringBuilder stderr,
        bool daemon, CancellationToken ct)
        => (Root, _proc, _rpc, _endpoints, _stderr, _daemon, _ct)
            = (root, proc, rpc, endpoints, stderr, daemon, ct);

    /// <summary>
    /// Starts the server, and on the one failure a first-time user always hits — the pinned
    /// tool never restored — restores it once and retries. Deliberately not a preflight
    /// check: `dotnet tool restore` costs a second even when everything is already there,
    /// and every run would pay it to save the first one.
    /// </summary>
    public static async Task<LspClient> StartAsync(string root, string logLevel, bool daemon, CancellationToken ct)
    {
        var manifestRoot = ServerArgs.ToolManifestRoot();
        try
        {
            return await StartCoreAsync(root, manifestRoot, logLevel, daemon, ct);
        }
        catch (CslqException ex) when (NotRestored(ex.Message))
        {
            Console.Error.WriteLine(
                $"cslq: the pinned language server is not restored; restoring it in {manifestRoot}. " +
                "This is a one-time ~300 MB download.");
            var pruned = await RestoreAsync(manifestRoot, ct);
            foreach (var line in Output.PruneLines(pruned)) Console.Error.WriteLine("cslq: " + line);
            return await StartCoreAsync(root, manifestRoot, logLevel, daemon, ct);
        }
    }

    /// <summary>
    /// The `dotnet tool run` message naming the fix. The prose around it is localised — this
    /// machine answers in Portuguese without <c>DOTNET_CLI_UI_LANGUAGE</c> — but the quoted
    /// command inside it is not, so match on that alone.
    /// </summary>
    internal static bool NotRestored(string stderr) =>
        stderr.Contains("dotnet tool restore", StringComparison.Ordinal);

    /// <summary>
    /// Also the whole of <c>cslq restore</c>, which is why this is not private: the pre-warm
    /// a Dockerfile or a CI job runs is exactly this restore, asked for rather than recovered
    /// from. A restore that succeeds is followed by <see cref="Prune.Run"/>: the new pin is
    /// now the only version this binary can run, so the others stop costing 300 MB each. The
    /// prune is reported, never fatal — null when the packages folder could not be found.
    /// </summary>
    internal static async Task<Prune.Result?> RestoreAsync(string manifestRoot, CancellationToken ct)
    {
        await RestoreCoreAsync(manifestRoot, ct);

        var pin = Prune.PinnedVersion(
            await File.ReadAllTextAsync(Path.Combine(manifestRoot, ".config", "dotnet-tools.json"), ct));
        var packages = await GlobalPackagesAsync(manifestRoot, ct);
        return pin is null || packages is null ? null : Prune.Run(packages, pin);
    }

    /// <summary>
    /// Where NuGet extracts packages, asked of the CLI rather than assumed: <c>NUGET_PACKAGES</c>
    /// and a <c>globalPackagesFolder</c> in any NuGet.Config both move it. The line is
    /// <c>global-packages: &lt;path&gt;</c>; the label is not localised with the UI language
    /// pinned, and the path is everything after the first <c>: </c> because on Windows it
    /// carries a colon of its own. Null when the CLI would not say. Run from the manifest
    /// root, as the restore was: NuGet.Config is resolved from the working directory, so a
    /// repository with its own <c>globalPackagesFolder</c> would otherwise name a folder the
    /// restore never wrote to.
    /// </summary>
    private static async Task<string?> GlobalPackagesAsync(string manifestRoot, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ServerArgs.Command)
        {
            WorkingDirectory = manifestRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in ServerArgs.GlobalPackages()) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using var proc = StartProcess(psi, "locate the NuGet global packages folder");
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        _ = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0) return null;

        foreach (var line in (await stdout).Split('\n'))
        {
            var sep = line.IndexOf(": ", StringComparison.Ordinal);
            if (sep < 0 || !line.StartsWith("global-packages", StringComparison.Ordinal)) continue;
            var path = line[(sep + 2)..].Trim();
            return path.Length == 0 ? null : Path.TrimEndingDirectorySeparator(path);
        }

        return null;
    }

    private static async Task RestoreCoreAsync(string manifestRoot, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ServerArgs.Command)
        {
            WorkingDirectory = manifestRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in ServerArgs.Restore()) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using (var proc = StartProcess(psi, $"restore the pinned language server in {manifestRoot}"))
        {
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C mid-restore: the payload behind the pin is ~300 MB, so leaving it
                // running orphans a download nothing will ever wait on. Tree, as
                // DisposeAsync does for a dedicated server — the child is ours alone.
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already exited.
                }

                throw;
            }

            if (proc.ExitCode != 0)
            {
                var why = (await stderr).Trim();
                if (why.Length == 0) why = (await stdout).Trim();
                throw new CslqException(
                    $"'dotnet tool restore' failed in {manifestRoot} (exit {proc.ExitCode}): {Firstline(why)} "
                    + $"Run 'dotnet tool restore' in {manifestRoot} once that is fixed.");
            }
        }
    }

    /// <summary>
    /// Windows <c>CreateProcess</c> is called with <c>bInheritHandles=TRUE</c>, so cslq's own
    /// std handles reach the thin client and, through it, the daemon — which outlives us. A
    /// harness that captures our output then waits for EOF on a pipe the daemon still holds,
    /// so a launching call blocks for the whole keepalive and the daemon is dead by the time
    /// it returns. Clearing the inherit flag before every launch is what stops the leak.
    /// Unix is unaffected: .NET opens its own descriptors <c>O_CLOEXEC</c> and dup2s only the
    /// redirected ends.
    /// </summary>
    internal static void DisableStdioInheritance()
    {
        if (!OperatingSystem.IsWindows()) return;

        // STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE. Best effort: a process with
        // no console has invalid std handles, and it must not be broken by this.
        foreach (var id in (ReadOnlySpan<int>)[-10, -11, -12])
        {
            var handle = Native.GetStdHandle(id);
            if (handle == nint.Zero || handle == -1) continue;
            Native.SetHandleInformation(handle, HandleFlagInherit, 0);
        }
    }

    /// <summary>
    /// Every <c>dotnet</c> launch goes through here. <see cref="Process.Start(ProcessStartInfo)"/>
    /// throws <see cref="System.ComponentModel.Win32Exception"/> when <c>dotnet</c> is off
    /// <c>PATH</c> — the first-time-user case exactly — and that escaped as a stack trace and
    /// exit 127 rather than as a <c>cslq:</c> line naming the fix.
    /// </summary>
    private static Process StartProcess(ProcessStartInfo psi, string what)
    {
        DisableStdioInheritance();
        try
        {
            return Process.Start(psi) ?? throw new CslqException($"could not {what}: no process started.");
        }
        catch (Exception ex) when (ex is not CslqException and not OperationCanceledException)
        {
            throw new CslqException(
                $"could not {what}: {Firstline(ex.Message)} "
                + $"cslq runs the language server with '{ServerArgs.Command}', so the .NET 10 SDK "
                + "must be installed and on PATH.");
        }
    }

    private static string Firstline(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "no output";
        return line.Length > 200 ? line[..200] : line;
    }

    /// <summary>
    /// This end of the language server's wire: JSON-RPC over the server process's stdio,
    /// framed by <c>Content-Length</c> headers.
    /// <para>
    /// Both halves are the trim-safe shape rather than the obvious one. The formatter is
    /// annotated at the type level whatever it is handed, so the suppression says why it is
    /// nonetheless true here: <see cref="Lsp.Options"/> resolves through
    /// <see cref="LspWire"/> alone, so nothing crossing it is serialized reflectively. The
    /// target goes in as metadata read off a compile-time shape, because the untyped
    /// overload leaves a published binary with <em>no methods at all</em> — every
    /// server-to-client call then comes back <c>RemoteMethodNotFoundException</c>, which
    /// reads as a protocol bug rather than as a trimmed target.
    /// </para>
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "SystemTextJsonFormatter is annotated at the type level, so the whole " +
            "of it is flagged whatever it is handed. Lsp.Options has one resolver, the " +
            "source-generated LspWire, and no reflection-based fallback behind it: a type " +
            "that is not declared there throws instead of being reflected over, trimmed or " +
            "not. LspWireTests pins that.")]
    [UnconditionalSuppressMessage(
        "AOT", "IL3050:RequiresDynamicCode",
        Justification = "Same site, same reason: no reflective serialization reaches this " +
            "formatter, so it makes no dynamic code.")]
    private static JsonRpc Rpc(Process proc, out Endpoints endpoints)
    {
        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = Lsp.Options };
        var handler = new HeaderDelimitedMessageHandler(
            proc.StandardInput.BaseStream, proc.StandardOutput.BaseStream, formatter);

        endpoints = new Endpoints();
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<Endpoints>(), endpoints, null);
        return rpc;
    }

    private static async Task<LspClient> StartCoreAsync(
        string root, string manifestRoot, string logLevel, bool daemon, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ServerArgs.Command)
        {
            WorkingDirectory = manifestRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var args = daemon ? ServerArgs.Daemon(logLevel) : ServerArgs.Stdio(logLevel);
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Roslyn localises the display strings it puts in LSP responses. Pin English so
        // output is the same for an agent regardless of the developer's machine locale.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        var proc = StartProcess(psi, "start the language server");

        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) stderr.AppendLine(e.Data); } };
        proc.BeginErrorReadLine();

        var rpc = Rpc(proc, out var endpoints);
        rpc.StartListening();

        var client = new LspClient(root, proc, rpc, endpoints, stderr, daemon, ct);
        try
        {
            await client.InitializeAsync(ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or CslqException)
        {
            // Escapes unchanged, but not uncleaned: without this the client built above is
            // dropped with its process and RPC connection still live. A CslqException means
            // initialize was answered and we rejected the answer — the encoding assertion —
            // so the connection-lost wrapping below would be a lie about a live server.
            await client.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            // The thin client can die before it answers initialize — a daemon that never came
            // up, for one — and StreamJsonRpc then reports nothing but a lost connection. Give
            // the process a moment to finish exiting so its stderr, the only thing that says
            // why, is flushed before we quote it. Quote it before disposing: StderrTail is the
            // only thing that turns "connection lost" into a diagnosis.
            await Task.WhenAny(proc.WaitForExitAsync(ct), Task.Delay(1000, ct));
            var tail = client.StderrTail();
            await client.DisposeAsync();
            throw new CslqException(
                $"the language server closed the connection during initialize: {ex.Message}{tail}");
        }

        return client;
    }

    [UnconditionalSuppressMessage(
        "Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "The parameter-object overloads are flagged because an untyped " +
            "argument may be reflected over. Nothing here is: Lsp.Options resolves through " +
            "the source-generated LspWire and has no fallback resolver, so every payload and " +
            "result crossing this connection is serialized by generated code or not at all. " +
            "The NamedArgs and dictionary overloads the warning suggests are not an option: " +
            "measured 2026-09-11, NamedArgs writes the CLR member names verbatim, so the " +
            "params object goes out with a PascalCase key per member, and LSP wants the " +
            "object itself rather than named arguments.")]
    private async Task InitializeAsync(CancellationToken ct)
    {
        var uri = PathUri.FromPath(Root);
        var init = new InitializeParams(
            Environment.ProcessId,
            new ClientInfo("cslq", Build.Version),
            "en",
            uri,
            new ClientCapabilities(
                new GeneralCapabilities([ServerArgs.ExpectedPositionEncoding]),
                new TextDocumentCapabilities(
                    new SynchronizationCapabilities(true),
                    new DiagnosticCapabilities(true, true),
                    new DocumentSymbolCapabilities(true, true),
                    new HoverCapabilities(true, ["plaintext"])),
                new WorkspaceCapabilities(true, true, new SymbolCapabilities(true)),
                new WindowCapabilities(true)),
            [new WorkspaceFolder(uri, Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar)))]);

        var result = await _rpc.InvokeWithParameterObjectAsync<InitializeResult>("initialize", init, ct);

        // Absent means utf-16 per LSP 3.17. A server that started answering utf-8 would
        // silently shift every column on a non-ASCII line, so refuse rather than adapt.
        var encoding = result.Capabilities.PositionEncoding ?? ServerArgs.ExpectedPositionEncoding;
        if (encoding != ServerArgs.ExpectedPositionEncoding)
        {
            throw new CslqException(
                $"Server negotiated positionEncoding '{encoding}'; cslq assumes '{ServerArgs.ExpectedPositionEncoding}'.");
        }

        await _rpc.NotifyWithParameterObjectAsync("initialized", new InitializedParams());
    }

    /// <summary>
    /// A query fired before the workspace loads returns an empty result, not an error, so
    /// readiness has to be established rather than assumed. Polls from the first round rather
    /// than blocking on <c>projectInitializationComplete</c>: that notification arrives at the
    /// <em>end</em> of the solution load this client's own <c>initialized</c> triggered, and
    /// the daemon re-runs that load per attach (measured 2026-09-10 — see CLAUDE.md), so
    /// waiting on it before asking anything would cost the whole load even where the answer
    /// was already there.
    /// <para>
    /// One sentinel per project, and every project that has one has to resolve it — see the
    /// last paragraph for the ones that have none. A single sentinel only
    /// ever proved that <em>some</em> project loaded, which is the race behind every
    /// incomplete answer this client has produced: a cross-project <c>refs</c> or <c>impl</c>
    /// missing the half that had not loaded, and a <c>sym</c> search missing a whole project's
    /// hits — all of them exit 0, because a short answer is not an error. Measured on the wire
    /// 2026-09-10 on CommunityToolkit: <c>workspace/symbol</c> answers <em>partially</em>
    /// throughout a load — eight of twelve projects resolved their sentinel before
    /// <c>projectInitializationComplete</c>, spread over twenty seconds, and four more kept
    /// resolving for six to eight seconds after it. So the partial window is every load, and
    /// it straddles the notification in both directions.
    /// </para>
    /// <para>
    /// A hit only counts for the project that asked for it: its location has to sit under that
    /// project's own directory. Two projects declaring <c>Program</c> is the ordinary case in a
    /// real repo, and without the check one project's symbol would satisfy another's sentinel
    /// and readiness would lie again. Matching on <c>containerName</c> would be the obvious
    /// alternative and is wrong — it is localised display text.
    /// </para>
    /// <para>
    /// A project that contributed no candidate is not waited on — there is nothing to ask for
    /// — but it is named on the failure path so its absence from readiness is visible rather
    /// than silent. <c>Program.InferSentinels</c> guarantees at least one project does
    /// contribute, so this never degrades to waiting for nothing at all.
    /// </para>
    /// <para>
    /// The wait is bounded to <see cref="PostLoadGrace"/> past
    /// <c>projectInitializationComplete</c>, but only when that notification arrived <em>in
    /// this process</em> — which is what <c>ProjectInitialized.IsCompleted</c> means, since
    /// nothing but our own notification handler ever completes that task. It fires on every
    /// attach, warm daemon included, because it ends the load this client asked for, so the
    /// bound applies to cold and warm alike. Past it, a candidate still unresolved is very
    /// likely never going to resolve — a project whose only type the regex read out of an
    /// <c>#if false</c> branch is the shape that does this, and it should fail in seconds
    /// rather than hold the whole <c>--timeout</c>. The grace is not zero because the tail
    /// above is real: projects were still resolving six to eight seconds after the
    /// notification on a 26-project solution. Before the notification there is nothing to
    /// bound from and nothing that should be bounded — that is the load itself, and the full
    /// timeout is what is wanted. A <c>--timeout</c> shorter than the grace still wins.
    /// </para>
    /// <para>
    /// A sentinel that has resolved is remembered in <see cref="_proved"/> and not asked
    /// again for the life of this attach — see <see cref="Unproved"/> for why that is a set
    /// rather than a flag. Nothing else is cached: a sentinel that has never resolved is
    /// re-probed on every call, with this same deadline and grace, and the whole failure path
    /// below is untouched.
    /// </para>
    /// </summary>
    public async Task WaitReadyAsync(
        IReadOnlyList<Sentinel> sentinels, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var pending = Unproved(sentinels, _proved);
        var unprobed = sentinels.Where(s => s.Candidates.Count == 0 && !s.Skipped).ToList();
        var linked = sentinels.Where(s => s.Skipped).ToList();
        DateTime? loaded = null;

        while (true)
        {
            // One round for every project at once, so a warm workspace costs a single
            // round trip's wall-clock rather than one per project.
            var resolved = await Task.WhenAll(pending.Select(s => ResolvesAsync(s, ct)));
            for (var i = 0; i < pending.Count; i++)
            {
                if (resolved[i]) _proved.Add(ProofKey(pending[i]));
            }

            pending = [.. pending.Where((_, i) => !resolved[i])];
            if (pending.Count == 0) return;
            if (_endpoints.ProjectInitialized.IsCompleted) loaded ??= DateTime.UtcNow;
            var bound = loaded is null || deadline < loaded.Value + PostLoadGrace
                ? deadline
                : loaded.Value + PostLoadGrace;
            if (DateTime.UtcNow >= bound) break;
            await Task.Delay(250, ct);
        }

        var fired = _endpoints.ProjectInitialized.IsCompleted;
        var probed = sentinels.Count(s => s.Candidates.Count > 0);
        // The whole load finished and not one project answered: that is what a failed
        // design-time build looks like from here, and it is the only state worth a `dotnet`
        // launch to explain. Anything less is an ordinary unresolvable candidate.
        var cause = fired && Readiness.EveryProjectEmpty(pending.Count, probed)
            ? Diagnosis.Cause(
                await SdkAsync(ct),
                Diagnosis.Unrestored(Root, sentinels.Where(s => !s.Explicit).Select(s => s.Directory)))
            : null;

        throw new CslqException(Readiness.Message(new Readiness.Failure(
            timeout,
            fired,
            [.. pending.Select(s => new Readiness.Unresolved(Subject(s), s.Candidates))],
            // Projects nothing probed are named too: readiness says nothing about them either
            // way, and leaving them out is the same quiet degradation the per-project set
            // exists to end.
            [.. unprobed.Select(Name)],
            [.. linked.Select(Name)],
            cause,
            StderrTail())));
    }

    /// <summary>
    /// <c>dotnet --version</c> with the working directory set to the root, which is where a
    /// <c>global.json</c> pinning an absent SDK bites: it exits 155 with "A compatible .NET SDK
    /// was not found", and nothing else cslq can see says so. Null when the launch itself could
    /// not happen — <c>dotnet</c> off <c>PATH</c> has its own message and must not be reported
    /// here as an SDK mismatch.
    /// <para>
    /// Bounded by <see cref="SdkBudget"/> on top of the caller's token, and the bound is the
    /// point: this runs on the failure path, <em>after</em> the readiness loop has already
    /// spent the whole <c>--timeout</c>, so a <c>dotnet</c> that never exits would hold the
    /// process open past every deadline the caller thought it had. Disposing the process does
    /// not end it, so the tree is killed on the way out. A diagnostic that times out is simply
    /// no diagnosis — null, and the readiness message prints without a cause — while the
    /// caller's own cancellation still propagates, because that is a different answer.
    /// </para>
    /// </summary>
    private async Task<Diagnosis.Sdk?> SdkAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(SdkBudget);
        try
        {
            var psi = new ProcessStartInfo(ServerArgs.Command)
            {
                WorkingDirectory = Root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in ServerArgs.Version()) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

            using var proc = StartProcess(psi, "ask the SDK for its version");
            try
            {
                proc.StandardInput.Close();
                var stdout = proc.StandardOutput.ReadToEndAsync(budget.Token);
                var stderr = proc.StandardError.ReadToEndAsync(budget.Token);
                await proc.WaitForExitAsync(budget.Token);

                var said = (await stderr).Trim();
                if (said.Length == 0) said = (await stdout).Trim();
                return new Diagnosis.Sdk(proc.ExitCode, Diagnosis.Summary(said));
            }
            finally
            {
                // Best effort, and on every path out: a child still running holds the pipes
                // this method is reading, so leaving it is what turns a stalled `dotnet` into
                // a stalled cslq.
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                    or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        // The diagnostic ran out of its own budget: no cause, rather than no answer at all.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>One project, or the explicit probe, named for a message.</summary>
    private static string Name(Sentinel sentinel) => Names([sentinel]);

    /// <summary>The same, in the grammatical position the failure text puts it in.</summary>
    private static string Subject(Sentinel sentinel) =>
        sentinel.Explicit ? Name(sentinel) : $"project {Name(sentinel)}";

    /// <summary>
    /// Which sentinels still have to be asked: the ones that contribute a candidate and have
    /// not already proved themselves against this attach. A <em>set</em> of proofs rather than
    /// a single ready flag, because <c>Program.Sentinels</c> recomputes the list from disk on
    /// every request — a project added after the session started is therefore absent from the
    /// set and probed, where a flag would skip it silently and hand back the incomplete answer
    /// at exit 0 that the per-project set was introduced to end.
    /// </summary>
    internal static List<Sentinel> Unproved(
        IEnumerable<Sentinel> sentinels, IReadOnlySet<string> proved) =>
        [.. sentinels.Where(s => s.Candidates.Count > 0 && !proved.Contains(ProofKey(s)))];

    /// <summary>
    /// A sentinel's own identity, never its position in the list. A project is its directory;
    /// the probe an explicit <c>--sentinel</c> adds is not a project — its directory is the
    /// root — so it is keyed by the candidates that are the whole of what it asks, and the two
    /// kinds are prefixed apart so a project directory can never read as an explicit probe.
    /// </summary>
    internal static string ProofKey(Sentinel sentinel) => sentinel.Explicit
        ? "explicit " + string.Join(' ', sentinel.Candidates)
        : "project " + Path.GetFullPath(sentinel.Directory).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Whether any of a project's candidate sentinels resolves to a hit the project accepts —
    /// see <see cref="Sentinel.Accepts"/> for what that scoping is worth. Several candidates
    /// because <c>Program.InferSentinels</c>'s type-declaration match is a regex, not a
    /// parser, and one bad guess would otherwise block readiness for the run.
    /// </summary>
    private async Task<bool> ResolvesAsync(Sentinel sentinel, CancellationToken ct)
    {
        foreach (var candidate in sentinel.Candidates)
        {
            var hits = await SymbolsAsync(candidate, ct);
            if (hits.Any(h => sentinel.Accepts(h.Location.Uri))) return true;
        }

        return false;
    }

    /// <summary>
    /// A project is named by its directory; the probe an explicit <c>--sentinel</c> adds is not
    /// a project and is named as what it is, since its directory is the root and would read as
    /// a project called after the repository.
    /// </summary>
    private static string Names(IEnumerable<Sentinel> sentinels) => string.Join(
        ", ",
        sentinels.Select(s => s.Explicit
            ? $"explicit sentinel '{string.Join("' / '", s.Candidates)}'"
            : Path.GetFileName(s.Directory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));

    public async Task<IReadOnlyList<SymbolInformation>> SymbolsAsync(string query, CancellationToken ct)
    {
        var result = await RequestAsync<SymbolInformation[]?>(
            "workspace/symbol", new WorkspaceSymbolParams(query), ct);
        return result ?? [];
    }

    /// <summary>
    /// Every reference, from every context asked, concatenated. Not the first context that
    /// answers, unlike <see cref="DefinitionAsync"/>: a reference inside an
    /// <c>#if NET9_0</c> block exists only in that context, so stopping early omits it
    /// silently. The fold that collapses a hit two contexts both report is
    /// <c>Output.WriteLocationsAsync</c>'s, on the rendered label plus range, and it is the
    /// same fold that already collapsed a generated document's per-framework twins.
    /// </summary>
    public async Task<IReadOnlyList<Location>> ReferencesAsync(
        string uri, Position position, IReadOnlyList<DocumentContext> contexts, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await AskAllAsync(
            uri,
            contexts,
            doc => SettleAsync(async () =>
                await RequestAsync<Location[]?>(
                    "textDocument/references",
                    new ReferenceParams(doc, position, new ReferenceContext(true)),
                    ct) ?? [], ct));
    }

    /// <summary>
    /// Location[], not LocationLink[]: the client does not declare
    /// textDocument.definition.linkSupport, so per LSP 3.17 the server owes us the plain form.
    /// Deliberately no two-shape reader — if that ever stops holding, a deserialization
    /// failure is a better outcome than silently rendering half a response.
    /// </summary>
    public async Task<Answer<IReadOnlyList<Location>>> DefinitionAsync(
        string uri, Position position, IReadOnlyList<DocumentContext> contexts, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        // The decompilation guard wraps each context's ask rather than the loop: a stale
        // binding is a property of one context's answer, and an empty answer from the wrong
        // context is what the loop is for.
        return await AskEachAsync<IReadOnlyList<Location>>(
            uri,
            contexts,
            doc => SettleAsync(async () =>
                await RequestAsync<Location[]?>(
                    "textDocument/definition", new TextDocumentPositionParams(doc, position), ct) ?? [], ct),
            l => l.Count > 0);
    }

    /// <summary>
    /// One request per context, in the order <see cref="Contexts.Order"/> fixed, stopping at
    /// the first context that answers.
    /// <para>
    /// Both halves are load-bearing. Sending <c>_vs_projectContext</c> is what makes the
    /// answer repeatable — without it Roslyn binds the document to whichever context sorted
    /// first on this attach, which on <c>fixture2/Multi</c> made <c>hover</c> on a type inside
    /// an <c>#if</c> answer 4 times in 8 (measured 2026-09-10). The retry is what makes it
    /// <em>right</em>: a symbol only exists in one branch, so a fixed context alone would turn
    /// a coin flip into a guaranteed miss for every symbol living in the other one.
    /// </para>
    /// <para>
    /// A single-context document pays one ask, the identifier carrying the one context it has.
    /// An empty answer from every context returns the <em>first</em> context's, so the caller
    /// renders one determinate "nothing" rather than whichever context was tried last.
    /// </para>
    /// </summary>
    private static async Task<Answer<T>> AskEachAsync<T>(
        string uri,
        IReadOnlyList<DocumentContext> contexts,
        Func<TextDocumentIdentifier, Task<T>> ask,
        Func<T, bool> answered)
    {
        if (contexts.Count == 0) return new Answer<T>(await ask(new TextDocumentIdentifier(uri)), null);

        Answer<T>? empty = null;
        foreach (var context in contexts)
        {
            var value = await ask(new TextDocumentIdentifier(uri) { ProjectContext = context.Wire });
            if (answered(value)) return new Answer<T>(value, context);
            empty ??= new Answer<T>(value, context);
        }

        return empty!;
    }

    /// <summary>
    /// Same <c>Location[]</c> reasoning as <see cref="DefinitionAsync"/>, and for a stronger
    /// reason: the client declares no <c>textDocument.implementation</c> capability node at
    /// all, so <c>linkSupport</c> is absent by construction. Roslyn does not answer empty for
    /// a member that simply has no implementations — it falls through to the declaration, so
    /// this degenerates to <c>definition</c> on an ordinary method. Empty means the position
    /// resolved to no symbol.
    /// </summary>
    public async Task<IReadOnlyList<Location>> ImplementationsAsync(
        string uri, Position position, IReadOnlyList<DocumentContext> contexts, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await AskAllAsync(
            uri,
            contexts,
            doc => SettleAsync(async () =>
                await RequestAsync<Location[]?>(
                    "textDocument/implementation", new TextDocumentPositionParams(doc, position), ct) ?? [], ct));
    }

    /// <summary>
    /// One request per context, every answer concatenated — the set-valued counterpart to
    /// <see cref="AskEachAsync{T}"/>. A document with no context at all is asked once with no
    /// context, which is what every request looked like before any of this existed.
    /// </summary>
    private static async Task<IReadOnlyList<T>> AskAllAsync<T>(
        string uri,
        IReadOnlyList<DocumentContext> contexts,
        Func<TextDocumentIdentifier, Task<IReadOnlyList<T>>> ask)
    {
        if (contexts.Count == 0) return await ask(new TextDocumentIdentifier(uri));

        var all = new List<T>();
        foreach (var context in contexts)
        {
            all.AddRange(await ask(new TextDocumentIdentifier(uri) { ProjectContext = context.Wire }));
        }

        return all;
    }

    /// <summary>
    /// The same fan-out, keeping each context's answer separate: <c>outline</c> and
    /// <c>diag</c> both have to say which contexts a row came from, which a concatenation
    /// throws away. The name is the context's label, or the empty string for a document with
    /// no context, so a caller can render a mark without knowing about contexts at all.
    /// </summary>
    private static async Task<IReadOnlyList<(string Name, IReadOnlyList<T> Values)>> AskEveryAsync<T>(
        string uri,
        IReadOnlyList<DocumentContext> contexts,
        IReadOnlyList<string> names,
        Func<TextDocumentIdentifier, Task<IReadOnlyList<T>>> ask)
    {
        if (contexts.Count == 0)
        {
            return [(string.Empty, await ask(new TextDocumentIdentifier(uri)))];
        }

        var views = new List<(string, IReadOnlyList<T>)>(contexts.Count);
        for (var i = 0; i < contexts.Count; i++)
        {
            views.Add((
                names[i],
                await ask(new TextDocumentIdentifier(uri) { ProjectContext = contexts[i].Wire })));
        }

        return views;
    }

    /// <summary>
    /// Re-asks while the answer is decompiled metadata <em>and</em> the workspace declares the
    /// type itself. Roslyn binds a <c>ProjectReference</c> to the referenced project's built
    /// assembly until that project is loaded, so a query fired in the window between the
    /// sentinel resolving and the last project loading comes back pointing at a temp file under
    /// <c>MetadataAsSource</c> — a confident wrong answer, exit 0, no relation to the repo.
    /// <para>
    /// The second condition is what this used to lack, and the framework case paid for it.
    /// Measured 2026-09-07 against 5.12.0-1.26426.8: <c>def</c> at <c>Console.WriteLine</c>
    /// burned the whole 10 s budget over 40 identical queries and then returned the same
    /// decompiled answer it had on the first — that document <em>is</em> the definition, so
    /// there was never anything to wait for. The stale-binding case meanwhile did not occur
    /// once in four cold runs against the fixture, readiness-per-project having closed the
    /// window it needs; the guard is kept rather than deleted because that is an absence of
    /// evidence over three projects, and it now costs one <c>workspace/symbol</c> query on the
    /// metadata path instead of ten seconds.
    /// </para>
    /// <para>
    /// The discriminator is the workspace itself: a decompiled document is named after the type
    /// it stands for, so asking whether any source file under the root declares that name
    /// separates "bound to an assembly whose source is right here" from "bound to an assembly
    /// because that is all there is". A workspace that declares its own <c>Console</c> would
    /// pay the budget on a framework <c>def</c>, which is the accepted cost of keeping the
    /// guard.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<Location>> SettleAsync(
        Func<Task<IReadOnlyList<Location>>> query, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + BindBudget;
        while (true)
        {
            var locations = await query();
            var decompiled = locations.Where(l => PathUri.IsDecompiled(l.Uri)).ToList();
            if (decompiled.Count == 0) return locations;
            if (!await StaleBindingAsync(decompiled, ct)) return locations;
            if (DateTime.UtcNow >= deadline) return locations;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// Whether a decompiled answer is the not-yet-loaded fingerprint rather than the real
    /// definition — see <see cref="SettleAsync"/>. Asked again on every round rather than
    /// cached, because the whole point is that the workspace is still changing underneath.
    /// <para>
    /// The hits are filtered to an exact ordinal name match first, and that filter is the
    /// whole guard. <c>workspace/symbol</c> answers prefix and substring matches too, so a
    /// workspace that merely declares a <c>ConsoleBanner</c> would otherwise make every
    /// framework <c>def</c> at <c>Console.WriteLine</c> look like a stale binding and burn
    /// the entire budget — the exact failure the second condition was added to remove.
    /// </para>
    /// </summary>
    private async Task<bool> StaleBindingAsync(IEnumerable<Location> decompiled, CancellationToken ct)
    {
        foreach (var name in decompiled
            .Select(l => PathUri.MetadataTypeName(l.Uri))
            .Distinct(StringComparer.Ordinal))
        {
            var hits = await SymbolsAsync(name, ct);
            if (PathUri.AnyUnder(
                Root,
                hits.Where(h => string.Equals(h.Name, name, StringComparison.Ordinal))
                    .Select(h => h.Location.Uri))) return true;
        }

        return false;
    }

    /// <summary>
    /// The answer to "what is this". Roslyn's hover carries the full signature including
    /// parameter types and the doc-comment summary, so <c>textDocument/signatureHelp</c> is
    /// not wired: it would add a second request for information already in this one, and it
    /// only answers inside an argument list rather than at a symbol. Verified on the wire
    /// against 5.12.0-1.26426.8, 2026-09-07 — <c>void Console.WriteLine(string? value)
    /// (+ 19 overloads)</c> plus the summary and the <c>Exceptions:</c> list.
    /// <para>
    /// Null for a position that resolves to no symbol, which is how the empty answer arrives:
    /// the server sends JSON <c>null</c> rather than an empty <c>contents</c>. No
    /// <see cref="SettleAsync"/> around it — a hover names a type, it does not point at a
    /// document, so a metadata binding is not a wrong answer here.
    /// </para>
    /// </summary>
    public async Task<Answer<Hover?>> HoverAsync(
        string uri, Position position, IReadOnlyList<DocumentContext> contexts, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await AskEachAsync<Hover?>(
            uri,
            contexts,
            doc => RequestAsync<Hover?>(
                "textDocument/hover", new TextDocumentPositionParams(doc, position), ct),
            h => h?.Contents?.Value is not null);
    }

    /// <summary>
    /// Each context's view of the document's declarations, kept apart for
    /// <see cref="Outline.Merge"/> to union. Every caller wants the union — the renderer to
    /// print a mark, the dotted-target chain so that a declaration in either branch resolves.
    /// </summary>
    public async Task<IReadOnlyList<OutlineView>> DocumentSymbolsAsync(
        string uri, IReadOnlyList<DocumentContext> contexts, IReadOnlyList<string> names,
        CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        var views = await AskEveryAsync<DocumentSymbol>(
            uri,
            contexts,
            names,
            async doc => await RequestAsync<DocumentSymbol[]?>(
                "textDocument/documentSymbol", new DocumentSymbolParams(doc), ct) ?? []);
        return [.. views.Select(v => new OutlineView(v.Name, v.Values))];
    }

    /// <summary>
    /// One pull. This used to re-pull until two consecutive reports agreed, on the premise that
    /// a freshly opened document is bound against the misc-files state and under-reports until
    /// its project references resolve. <b>Measured false on 2026-09-06</b> against
    /// 5.12.0-1.26426.8: the endpoint does not answer early, it <em>blocks</em> until the
    /// document is bound. A cross-project error opened as the first document in a never-used
    /// daemon returns the correct CS0029 on the first pull — that pull costs ~4.2 s and the
    /// redundant second one ~0.7 s. Across six whole-fixture runs, cold daemon and warm, the
    /// second pull never once differed from the first, so the loop bought a mandatory 250 ms
    /// delay plus a duplicate round trip per file and nothing else: removing it halved the
    /// per-file cost, 570 ms to 294 ms warm. <c>cold-server-diag-reports-cross-project-error</c>
    /// in <c>probes/run.sh</c> is the guard — it is the only leg that pulls a document the
    /// daemon has never opened, which is the one state where answering before binding shows up.
    /// Note the old loop could not have caught that case anyway: two equally-wrong pulls agree.
    /// </summary>
    /// <remarks>
    /// One pull <em>per context</em>, though. A diagnostic can exist in one framework and not
    /// another, and an unqualified pull reported one such error in 1 run of 4 — so a single pull reports one context's view of the file
    /// and silently drops the rest. <c>fixture2/Multi/TfmError.cs</c> holds a CS0029 that only
    /// <c>net9.0</c> has, and an unqualified pull reported it in 1 run of 4.
    /// </remarks>
    public async Task<IReadOnlyList<(string Name, IReadOnlyList<Diagnostic> Items)>> DiagnosticsAsync(
        string uri, IReadOnlyList<DocumentContext> contexts, IReadOnlyList<string> names,
        CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await AskEveryAsync<Diagnostic>(
            uri,
            contexts,
            names,
            async doc => (await RequestAsync<DocumentDiagnosticReport?>(
                "textDocument/diagnostic", new DocumentDiagnosticParams(doc), ct))?.Items ?? []);
    }

    /// <summary>
    /// Roslyn will not answer requests for a document it does not consider open. Generated
    /// documents are the exception: they are the server's own, it answers for them without a
    /// didOpen, and there is no file to read the text from anyway.
    /// <para>
    /// A document already open is re-sent when the file has changed under it. That costs one
    /// <c>stat</c> per use in a one-shot run, where the set is always empty and nothing is
    /// ever re-sent, and it is the only thing keeping a session — which holds this set across
    /// an edit — from answering forever from the text it first read. <c>didClose</c> and a
    /// fresh <c>didOpen</c> rather than <c>didChange</c>: the pair needs nothing from the
    /// server beyond the synchronization capability this client already declares, while a
    /// whole-document <c>didChange</c> would be correct only for the sync kind the server
    /// advertises — which <see cref="ServerCapabilities"/> does not even read — and would
    /// save one notification, not the file read, since the new text has to be read either
    /// way. The re-send is a file read and two notifications against a query costing
    /// milliseconds at best, and it only happens when the file actually changed.
    /// </para>
    /// </summary>
    public async Task OpenAsync(string uri, CancellationToken ct)
    {
        if (PathUri.IsGenerated(uri)) return;
        if (_open.TryGetValue(uri, out var opened))
        {
            switch (Staleness.Check(uri, opened))
            {
                case DocumentState.Unchanged:
                    return;
                case DocumentState.Deleted:
                    // The server would keep answering for a document that is gone. Drop it
                    // and fail the way a file that was never there fails.
                    await CloseAsync(uri);
                    throw new CslqException($"no such file: {PathUri.Relative(Root, PathUri.ToPath(uri))}");
                default:
                    await CloseAsync(uri);
                    break;
            }
        }

        // Stamped and read before the set is marked, so a document refused for its encoding is
        // not recorded as open: nothing was sent for it, and the next attempt should fail the
        // same way rather than silently proceed as if the server had the text. The stamp is
        // taken before the read, so an edit landing during it reads as changed next time
        // rather than being missed.
        var path = PathUri.ToPath(uri);
        var stamp = Staleness.OfPath(path);
        var text = await SourceText.ReadAsync(Root, path, ct);
        _open[uri] = stamp;
        await NotifyAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams(new TextDocumentItem(uri, "csharp", 1, text)));
    }

    /// <summary>
    /// Re-sends every open document the file behind it has changed under, before the request
    /// that is about to be answered. <see cref="OpenAsync"/>'s own check is not enough on its
    /// own: a symbol query reads <c>workspace/symbol</c>, whose index is built from the text
    /// of the documents this client has open, and it runs <em>before</em> any position is
    /// known — so a session that only refreshed the document it was about to position in
    /// answered <c>hover Greeted</c> with <c>no symbol matched</c> after the rename and went
    /// on doing so, having never re-sent the file that would have told it otherwise
    /// (measured on the fixture, 2026-09-11).
    /// <para>
    /// One <c>stat</c> per open document, which is what a session holds and nothing more. A
    /// document that has become undecodable, or has been deleted, is left closed rather than
    /// failed here: this sweep belongs to no command in particular, and the command that does
    /// ask for that document raises it exactly as it does today.
    /// </para>
    /// </summary>
    public async Task RefreshOpenAsync(CancellationToken ct)
    {
        // A generated document has no file behind it, so no stamp can see its text change --
        // and it does change, whenever what the generator keys on does. Its context lines are
        // therefore cached for one request rather than for the session: dropping them here
        // costs one workspace/textDocumentContent per generated document per request, which is
        // what a one-shot run pays, instead of one per rendered row.
        foreach (var generated in _lines.Keys.Where(PathUri.IsGenerated).ToList())
        {
            _lines.Remove(generated);
        }

        foreach (var (uri, stamp) in _open.ToList())
        {
            var state = Staleness.Check(uri, stamp);
            if (state == DocumentState.Unchanged) continue;

            await CloseAsync(uri);
            if (state == DocumentState.Deleted) continue;

            try
            {
                await OpenAsync(uri, ct);
            }
            catch (CslqException)
            {
                // Undecodable, or deleted between the stat above and the read. Either way it
                // is left closed for the command that asks for it to answer.
            }
        }
    }

    /// <summary>
    /// Gives the document back to the server and forgets everything read off it. cslq sent no
    /// <c>didClose</c> at all before sessions existed, because a process that dies closes
    /// every document it opened.
    /// </summary>
    public async Task CloseAsync(string uri)
    {
        _open.Remove(uri);
        _lines.Remove(uri);
        await NotifyAsync(
            "textDocument/didClose",
            new DidCloseTextDocumentParams(new TextDocumentIdentifier(uri)));
    }

    /// <summary>
    /// Document text for rendering context lines: off disk for a real file, from the server
    /// for a generated one. An unreadable document yields no lines rather than failing —
    /// a hit with a correct position is still worth printing.
    /// </summary>
    public async Task<string[]> LinesAsync(string uri, CancellationToken ct)
    {
        // Cached against the same stamp the open document is: a session that rendered a file's
        // context rows before it was edited would otherwise keep printing the old rows under
        // fresh positions, which is the same wrong answer one layer out.
        if (_lines.TryGetValue(uri, out var cached) &&
            Staleness.Check(uri, cached.Stamp) == DocumentState.Unchanged)
        {
            return cached.Lines;
        }

        var stamp = Staleness.Of(uri);
        string[] lines;
        if (PathUri.IsGenerated(uri))
        {
            var content = await RequestAsync<TextDocumentContentResult?>(
                "workspace/textDocumentContent", new TextDocumentContentParams(uri), ct);
            // Trailing newline dropped so a generated document splits the way
            // File.ReadAllLines would, instead of printing a phantom blank context row.
            var text = content?.Text.ReplaceLineEndings("\n");
            if (text is not null && text.EndsWith('\n')) text = text[..^1];
            lines = text is null ? [] : text.Split('\n');
        }
        else
        {
            var path = PathUri.ToPath(uri);
            lines = File.Exists(path) ? await TextLinesAsync(path, ct) : [];
        }

        _lines[uri] = (stamp, lines);
        return lines;
    }

    /// <summary>
    /// Context lines off disk, with the encoding failure answered the way this method answers
    /// every other unreadable document: no lines, so the hit still prints with its position.
    /// The difference is that this one is said out loud — a file decoded with substitutions
    /// renders context rows whose columns no longer agree with the header above them, and a
    /// silent wrong rendering is the whole of the defect.
    /// </summary>
    private async Task<string[]> TextLinesAsync(string path, CancellationToken ct)
    {
        try
        {
            return await SourceText.ReadLinesAsync(Root, path, ct);
        }
        catch (InvalidTextException ex)
        {
            Console.Error.WriteLine($"cslq: no context lines — {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// The assembly a decompiled document was produced from, for its <c>&lt;metadata&gt;</c>
    /// label. Roslyn writes it into a <c>#region Assembly</c> header at the top of the file,
    /// which is the only place it exists — the URI is a temp path made of two run-specific
    /// guids and the type's name. Read through <see cref="LinesAsync"/>, so it costs the same
    /// single file read the context lines already pay for.
    /// </summary>
    public async Task<string?> AssemblyOfAsync(string uri, CancellationToken ct)
    {
        if (!PathUri.IsDecompiled(uri)) return null;
        var lines = await LinesAsync(uri, ct);
        return PathUri.MetadataAssembly(lines.FirstOrDefault());
    }

    /// <summary>
    /// The <c>.csproj</c> a document belongs to, or null if the server will not say. Only a
    /// source-generated document needs asking: a file URI already carries its own path, and
    /// the generated URI's query names the <em>generator</em> assembly, never the project
    /// consuming it — so one generator applied to several projects yields several distinct
    /// documents whose labels are otherwise identical.
    /// <para>
    /// One name, not a list: which of a multi-targeted project's contexts supplied it makes no
    /// difference to the project it names, so this takes the first in
    /// <see cref="Contexts.Order"/>'s order. Everything that asks a <em>question</em> of a
    /// context goes through <see cref="ContextsAsync"/> and chooses deliberately.
    /// </para>
    /// </summary>
    public async Task<string?> ProjectOfAsync(string uri, CancellationToken ct) =>
        (await ContextsAsync(uri, ct)) is [var first, ..] ? first.File : null;

    /// <summary>
    /// Whether this server does not implement <c>_vs_getProjectContexts</c> at all — a
    /// <c>RemoteMethodNotFoundException</c> and nothing else, because every other failure is
    /// transient and a session would carry it forever. It is an optional VS extension, so an
    /// empty answer means two different things — this document is compiled by no project, or
    /// this server cannot say — and only a caller that can tell them apart may act on the
    /// first. <c>diag</c> is that caller: it skips a document with no context, and a server
    /// that dropped the extension would otherwise turn a whole-tree walk into
    /// <c>no diagnostics</c>, silently.
    /// </summary>
    public bool ProjectContextsUnsupported { get; private set; }

    /// <summary>
    /// Whether a failed <c>_vs_getProjectContexts</c> means the server does not implement it,
    /// which is the only failure <see cref="ProjectContextsUnsupported"/> may remember.
    /// <c>RemoteMethodNotFoundException</c> alone: it is a sibling of
    /// <c>RemoteInvocationException</c> rather than a subclass, and the base of both,
    /// <c>RemoteRpcException</c>, also covers <c>ConnectionLostException</c> — which is a
    /// blip, not a capability, and catching it here is what made one dropped connection
    /// permanent for a whole session.
    /// </summary>
    internal static bool ContextsUnsupported(Exception ex) => ex is RemoteMethodNotFoundException;

    /// <summary>
    /// Every project context the document is compiled in, in <see cref="Contexts.Order"/>'s
    /// order. Empty when no project compiles it, and empty rather than fatal when the server
    /// does not implement the request at all — but a failed one is a failed command, not an
    /// empty list; see <see cref="ContextsUnsupported"/>.
    /// <para>
    /// <c>textDocument/_vs_getProjectContexts</c> is a VS protocol extension rather than LSP,
    /// and the server neither advertises it nor requires a matching client capability
    /// (verified against 5.12.0-1.26426.8, 2026-09-06). <c>_vs_id</c> is
    /// <c>&lt;projectId guid&gt;|&lt;absolute .csproj&gt; ($&lt;tfm&gt;)</c>. Only the path
    /// half is read — the guid is regenerated on every attach — but the id travels back whole
    /// in a <c>_vs_projectContext</c>, so the fetch and the use have to happen in one process.
    /// That is what this cache is: per document, per attach, never across runs.
    /// </para>
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "The parameter-object overloads are flagged because an untyped " +
            "argument may be reflected over. Nothing here is: Lsp.Options resolves through " +
            "the source-generated LspWire and has no fallback resolver, so every payload and " +
            "result crossing this connection is serialized by generated code or not at all. " +
            "The NamedArgs and dictionary overloads the warning suggests are not an option: " +
            "measured 2026-09-11, NamedArgs writes the CLR member names verbatim, so the " +
            "params object goes out with a PascalCase key per member, and LSP wants the " +
            "object itself rather than named arguments.")]
    public async Task<IReadOnlyList<DocumentContext>> ContextsAsync(string uri, CancellationToken ct)
    {
        if (_contexts.TryGetValue(uri, out var cached)) return cached;

        IReadOnlyList<DocumentContext> contexts;
        try
        {
            var list = await _rpc.InvokeWithParameterObjectAsync<ProjectContextList?>(
                "textDocument/_vs_getProjectContexts",
                new ProjectContextParams(new TextDocumentIdentifier(uri)),
                ct);

            contexts = Contexts.Read(list?.Contexts ?? []);
        }
        catch (Exception ex) when (ContextsUnsupported(ex))
        {
            // The one failure that is a property of the server rather than of this moment:
            // it does not implement the extension, it never will within this attach, and a
            // generator-only label is a correct if coarser answer. Cached and remembered.
            //
            // This is also the one call that deliberately bypasses RequestAsync, for the same
            // reason: an optional VS extension the server need not implement is a label to
            // soften rather than a command to fail.
            ProjectContextsUnsupported = true;
            contexts = [];
        }
        catch (Exception ex) when (Describe("textDocument/_vs_getProjectContexts", ex, PipeName) is { } message)
        {
            // Everything else is transient — a connection lost mid-request most of all — and
            // must be neither remembered nor cached. A one-shot process retried on its next
            // run; a session has no next run, so a single blip used to set the flag for the
            // life of the session (which stops `diag` skipping misc-file documents, the very
            // drift this guard exists to prevent) and cache an empty context list for the URI
            // (which asks every later positional request with no _vs_projectContext, the
            // multi-TFM coin flip). It fails the request instead: a walk that names the file
            // it could not read is strictly better than one that drops it at exit 0.
            throw new CslqException(message + (ex is ConnectionLostException ? StderrTail() : string.Empty));
        }

        _contexts[uri] = contexts;
        return contexts;
    }

    /// <summary>
    /// Whether the thin client gave up on the daemon and started its own server. It does that
    /// silently — a fallback run answers correctly, just cold, and these two lines on stderr
    /// are the only difference — so an agent would otherwise blame the latency on us. Read
    /// after the command has run, not right after connecting: the marker is written while the
    /// pipe is being established, which races the initialize response.
    /// </summary>
    public bool DaemonFallback
    {
        get
        {
            string text;
            lock (_stderr) { text = _stderr.ToString(); }
            return DaemonFallbackMarkers.Any(m => text.Contains(m, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Whether this run owns its server outright. A daemon run that fell back has a private
    /// server nothing else will ever attach to, so it is as dedicated as <c>--no-daemon</c>.
    /// </summary>
    private bool Dedicated => !_daemon || DaemonFallback;

    private static readonly string[] DaemonFallbackMarkers =
    [
        "Falling back to non-daemon mode",
        "non-daemon fallback mode",
    ];

    /// <summary>
    /// Every post-initialize request goes through here. <c>Main</c> catches
    /// <see cref="CslqException"/> and <see cref="OperationCanceledException"/> and nothing
    /// else, so a bare <c>_rpc.Invoke</c> turns a server-side rejection or a daemon that died
    /// mid-request into a stack trace and exit 127. <c>initialize</c> keeps its own wrapping
    /// in <see cref="StartCoreAsync"/> — it has a different message and its own disposal — and
    /// <see cref="ContextsAsync"/> is the one deliberate bypass; see the catch there.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Same overload, same reason - and here the reason has to hold for a " +
            "caller that does not exist yet, not just for today's. It does, structurally: " +
            "Lsp.Options names LspWire as its only TypeInfoResolver, so a T or a @params " +
            "type that context does not declare throws on the first call rather than being " +
            "reflected over, in a trimmed build and an untrimmed one alike. A request added " +
            "without registering its shapes cannot reach the server at all, which is the " +
            "right outcome in this file: a malformed payload takes the server's whole queue " +
            "down. LspWireTests.An_unregistered_type_is_refused_rather_than_reflected_over " +
            "pins it.")]
    private async Task<T?> RequestAsync<T>(string method, object? @params, CancellationToken ct)
    {
        try
        {
            return await _rpc.InvokeWithParameterObjectAsync<T?>(method, @params, ct);
        }
        catch (Exception ex) when (Describe(method, ex, PipeName) is { } message)
        {
            throw new CslqException(message + (ex is ConnectionLostException ? StderrTail() : string.Empty));
        }
    }

    /// <summary>
    /// The notification twin. A notification is never answered, so it cannot be rejected —
    /// but it is still a write to the connection, and a daemon that has gone away fails it
    /// with the same <see cref="ConnectionLostException"/> a request would.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Same overload, same reason - and here the reason has to hold for a " +
            "caller that does not exist yet, not just for today's. It does, structurally: " +
            "Lsp.Options names LspWire as its only TypeInfoResolver, so a T or a @params " +
            "type that context does not declare throws on the first call rather than being " +
            "reflected over, in a trimmed build and an untrimmed one alike. A request added " +
            "without registering its shapes cannot reach the server at all, which is the " +
            "right outcome in this file: a malformed payload takes the server's whole queue " +
            "down. LspWireTests.An_unregistered_type_is_refused_rather_than_reflected_over " +
            "pins it.")]
    private async Task NotifyAsync(string method, object? @params)
    {
        try
        {
            await _rpc.NotifyWithParameterObjectAsync(method, @params);
        }
        catch (Exception ex) when (Describe(method, ex, PipeName) is { } message)
        {
            throw new CslqException(message + (ex is ConnectionLostException ? StderrTail() : string.Empty));
        }
    }

    /// <summary>
    /// The message for a failed request, or null for an exception that must escape untouched
    /// — a cancellation above all, since Ctrl+C is exit 130 and wrapping it as a
    /// <see cref="CslqException"/> would report it as exit 1. Pure, so the wording is
    /// testable without a live connection.
    /// </summary>
    internal static string? Describe(string method, Exception ex, string? pipe) => ex switch
    {
        ConnectionLostException => $"the language server connection was lost during {method}" +
            (pipe is null ? string.Empty : $" (daemon pipe '{pipe}')") +
            "; rerun to relaunch it.",
        RemoteRpcException => $"{method} failed: {ex.Message}",
        _ => null,
    };

    /// <summary>
    /// The daemon pipe this run is talking over, for the connection-lost message, or null
    /// when there is no daemon to name — <c>--no-daemon</c>, a run that fell back to its own
    /// server, or a default pipe name only the thin client knows.
    /// </summary>
    private string? PipeName => Dedicated
        ? null
        : Environment.GetEnvironmentVariable("ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME");

    public string StderrTail(int lines = 12)
    {
        string text;
        lock (_stderr) { text = _stderr.ToString(); }
        if (text.Length == 0) return string.Empty;
        var tail = text.TrimEnd().Split('\n');
        return "\n--- server stderr ---\n" + string.Join('\n', tail[Math.Max(0, tail.Length - lines)..]);
    }

    /// <summary>
    /// Ctrl+C has to be felt at the prompt, so a cancelled teardown skips the polite shutdown
    /// and does not wait out the exit: the two budgets together cost ~8s, and the token was
    /// honoured everywhere except here. Only a server this run owns is then killed. The
    /// shared daemon is a child of the thin client this run started but serves every other
    /// client on the machine, so killing that process tree would take their workspace down
    /// with it; it is left to its own keepalive instead.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var cancelled = _ct.IsCancellationRequested;
        if (!cancelled)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _rpc.InvokeWithParameterObjectAsync<object?>("shutdown", null, cts.Token);
                await _rpc.NotifyWithParameterObjectAsync("exit");
            }
            catch
            {
                // A server that is already gone needs no polite shutdown.
            }
        }

        _rpc.Dispose();
        try
        {
            if (!_proc.WaitForExit(cancelled ? 250 : 3000) && (Dedicated || !cancelled))
            {
                // Tree only when the server is ours. The shared daemon is a child of this
                // thin client, so a tree kill on the timeout path would take it down too.
                _proc.Kill(entireProcessTree: Dedicated);
            }
        }
        catch
        {
            // Already exited.
        }

        _proc.Dispose();
    }

    /// <summary>
    /// Server-to-client calls. An unhandled request would fault the connection.
    /// <para>
    /// The target is registered from a compile-time shape, and <c>IncludeMethods</c> is
    /// load-bearing there: a
    /// shape carries properties alone by default, so the metadata would describe a target
    /// with no methods and every notification — every <c>window/logMessage</c>, and
    /// <c>projectInitializationComplete</c>, which readiness waits on — would reach a target
    /// that cannot answer it.
    /// </para>
    /// </summary>
    [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    internal sealed partial class Endpoints
    {
        private readonly TaskCompletionSource _projectInitialized =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProjectInitialized => _projectInitialized.Task;

        [JsonRpcMethod("workspace/projectInitializationComplete")]
        public void OnProjectInitializationComplete() => _projectInitialized.TrySetResult();

        [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
        public object?[] OnConfiguration(ConfigurationParams p) => new object?[p.Items.Length];

        [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
        public object? OnRegisterCapability(JsonElement _) => null;

        [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
        public object? OnUnregisterCapability(JsonElement _) => null;

        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public object? OnWorkDoneProgressCreate(JsonElement _) => null;

        [JsonRpcMethod("workspace/_roslyn_restorableProjects", UseSingleObjectParameterDeserialization = true)]
        public string[] OnRestorableProjects(JsonElement _) => [];

        // Refresh requests for source-generated documents. cslq is one-shot, so there is
        // nothing to invalidate — but answering beats the alternative: an error response on
        // an unexpected server-to-client call, and a bad payload is already known to take the
        // server's whole request queue down with it.
        [JsonRpcMethod("workspace/_roslyn_refreshSourceGenerators", UseSingleObjectParameterDeserialization = true)]
        public object? OnRefreshSourceGenerators(JsonElement _) => null;

        [JsonRpcMethod("workspace/textDocumentContent/refresh", UseSingleObjectParameterDeserialization = true)]
        public object? OnTextDocumentContentRefresh(JsonElement _) => null;

        [JsonRpcMethod("workspace/diagnostic/refresh", UseSingleObjectParameterDeserialization = true)]
        public object? OnDiagnosticRefresh(JsonElement _) => null;

        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void OnLogMessage(JsonElement _) { }

        [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
        public void OnShowMessage(JsonElement _) { }

        [JsonRpcMethod("telemetry/event", UseSingleObjectParameterDeserialization = true)]
        public void OnTelemetry(JsonElement _) { }

        [JsonRpcMethod("$/progress", UseSingleObjectParameterDeserialization = true)]
        public void OnProgress(JsonElement _) { }
    }
}

/// <summary>
/// One project's readiness probe: the directory a resolving hit has to sit under, the
/// candidate type names to look for, in the order they were found, and the directories of
/// projects nested inside this one, which a hit must <em>not</em> sit under.
/// </summary>
/// <param name="Skipped">
/// Whether this project cannot be probed at all, as against merely having contributed no
/// candidate. A project whose sources are linked in from outside its directory — a
/// <c>*.projitems</c> import, a <c>&lt;Compile Include="../Shared/**"&gt;</c> — owns no
/// document whose location <see cref="Accepts"/> could ever accept, so no candidate could
/// prove it loaded even in principle. Fourteen of CommunityToolkit's twenty-six projects are
/// that shape, and before this they were counted in <c>ready --json</c>'s <c>projects</c> as
/// if they had been probed.
/// </param>
internal sealed record Sentinel(
    string Directory,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> Nested,
    bool Skipped = false,
    bool Explicit = false)
{
    /// <summary>
    /// Whether a sentinel hit proves <em>this</em> project loaded. Inside the directory and
    /// not inside a project nested within it: without that second half, <c>Web/</c> and
    /// <c>Web/Tests/</c> both declaring <c>Program</c> — the ordinary shape — lets Tests
    /// loading mark Web ready, which is the every-project-loaded guarantee failing quietly.
    /// A generated document is never accepted: it has no on-disk path to scope, and
    /// <c>ToPath</c> would answer a path-shaped lie for it.
    /// </summary>
    public bool Accepts(string uri) =>
        Under(uri, Directory) && !Nested.Any(n => Under(uri, n));

    private static bool Under(string uri, string directory)
    {
        if (PathUri.IsGenerated(uri)) return false;

        var path = Path.GetFullPath(PathUri.ToPath(uri));
        var dir = Path.GetFullPath(directory).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(dir + Path.DirectorySeparatorChar, PathUri.PathComparison);
    }
}
