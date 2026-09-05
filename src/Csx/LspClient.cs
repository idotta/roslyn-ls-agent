using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

namespace Csx;

internal sealed class LspClient : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly JsonRpc _rpc;
    private readonly Endpoints _endpoints;
    private readonly StringBuilder _stderr;
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _lines = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BindBudget = TimeSpan.FromSeconds(10);

    public string Root { get; }

    private LspClient(string root, Process proc, JsonRpc rpc, Endpoints endpoints, StringBuilder stderr)
        => (Root, _proc, _rpc, _endpoints, _stderr) = (root, proc, rpc, endpoints, stderr);

    public static async Task<LspClient> StartAsync(string root, string logLevel, bool daemon, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ServerArgs.Command)
        {
            WorkingDirectory = ServerArgs.ToolManifestRoot(),
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

        var proc = Process.Start(psi) ?? throw new CsxException("Failed to start the language server.");

        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) stderr.AppendLine(e.Data); } };
        proc.BeginErrorReadLine();

        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = Lsp.Options };
        var handler = new HeaderDelimitedMessageHandler(
            proc.StandardInput.BaseStream, proc.StandardOutput.BaseStream, formatter);

        var endpoints = new Endpoints();
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(endpoints);
        rpc.StartListening();

        var client = new LspClient(root, proc, rpc, endpoints, stderr);
        try
        {
            await client.InitializeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The thin client can die before it answers initialize — a daemon that never came
            // up, for one — and StreamJsonRpc then reports nothing but a lost connection. Give
            // the process a moment to finish exiting so its stderr, the only thing that says
            // why, is flushed before we quote it.
            await Task.WhenAny(proc.WaitForExitAsync(ct), Task.Delay(1000, ct));
            throw new CsxException(
                $"the language server closed the connection during initialize: {ex.Message}{client.StderrTail()}");
        }

        return client;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var uri = PathUri.FromPath(Root);
        var init = new InitializeParams(
            Environment.ProcessId,
            new ClientInfo("csx", "0.1.0"),
            "en",
            uri,
            new ClientCapabilities(
                new GeneralCapabilities([ServerArgs.ExpectedPositionEncoding]),
                new TextDocumentCapabilities(
                    new SynchronizationCapabilities(true),
                    new DiagnosticCapabilities(true, true),
                    new DocumentSymbolCapabilities(true, true)),
                new WorkspaceCapabilities(true, true, new SymbolCapabilities(true)),
                new WindowCapabilities(true)),
            [new WorkspaceFolder(uri, Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar)))]);

        var result = await _rpc.InvokeWithParameterObjectAsync<InitializeResult>("initialize", init, ct);

        // Absent means utf-16 per LSP 3.17. A server that started answering utf-8 would
        // silently shift every column on a non-ASCII line, so refuse rather than adapt.
        var encoding = result.Capabilities.PositionEncoding ?? ServerArgs.ExpectedPositionEncoding;
        if (encoding != ServerArgs.ExpectedPositionEncoding)
        {
            throw new CsxException(
                $"Server negotiated positionEncoding '{encoding}'; csx assumes '{ServerArgs.ExpectedPositionEncoding}'.");
        }

        await _rpc.NotifyWithParameterObjectAsync("initialized", new { });
    }

    /// <summary>
    /// A query fired before the workspace loads returns an empty result, not an error, so
    /// readiness has to be established rather than assumed. Polls the sentinel from the
    /// start: a client attaching to an already-loaded daemon never sees
    /// <c>projectInitializationComplete</c> — it fired before this process existed — so
    /// waiting on the notification first burned the entire timeout on a workspace that was
    /// ready before we connected. The notification is kept only as diagnostic detail on the
    /// failure path.
    /// </summary>
    public async Task WaitReadyAsync(string sentinel, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if ((await SymbolsAsync(sentinel, ct)).Count > 0) return;
            await Task.Delay(250, ct);
        }

        var fired = _endpoints.ProjectInitialized.IsCompleted ? "fired" : "never fired";
        throw new CsxException(
            $"Workspace did not become ready within {timeout.TotalSeconds:0}s: sentinel query '{sentinel}' " +
            $"returned no symbols (projectInitializationComplete {fired}).{StderrTail()}");
    }

    public async Task<IReadOnlyList<SymbolInformation>> SymbolsAsync(string query, CancellationToken ct)
    {
        var result = await _rpc.InvokeWithParameterObjectAsync<SymbolInformation[]?>(
            "workspace/symbol", new WorkspaceSymbolParams(query), ct);
        return result ?? [];
    }

    public async Task<IReadOnlyList<Location>> ReferencesAsync(string uri, Position position, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await SettleAsync(async () =>
            await _rpc.InvokeWithParameterObjectAsync<Location[]?>(
                "textDocument/references",
                new ReferenceParams(new TextDocumentIdentifier(uri), position, new ReferenceContext(true)),
                ct) ?? [], ct);
    }

    /// <summary>
    /// Location[], not LocationLink[]: the client does not declare
    /// textDocument.definition.linkSupport, so per LSP 3.17 the server owes us the plain form.
    /// Deliberately no two-shape reader — if that ever stops holding, a deserialization
    /// failure is a better outcome than silently rendering half a response.
    /// </summary>
    public async Task<IReadOnlyList<Location>> DefinitionAsync(string uri, Position position, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await SettleAsync(async () =>
            await _rpc.InvokeWithParameterObjectAsync<Location[]?>(
                "textDocument/definition",
                new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position),
                ct) ?? [], ct);
    }

    /// <summary>
    /// Same <c>Location[]</c> reasoning as <see cref="DefinitionAsync"/>, and for a stronger
    /// reason: the client declares no <c>textDocument.implementation</c> capability node at
    /// all, so <c>linkSupport</c> is absent by construction. Roslyn does not answer empty for
    /// a member that simply has no implementations — it falls through to the declaration, so
    /// this degenerates to <c>definition</c> on an ordinary method. Empty means the position
    /// resolved to no symbol.
    /// </summary>
    public async Task<IReadOnlyList<Location>> ImplementationsAsync(string uri, Position position, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await SettleAsync(async () =>
            await _rpc.InvokeWithParameterObjectAsync<Location[]?>(
                "textDocument/implementation",
                new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position),
                ct) ?? [], ct);
    }

    /// <summary>
    /// Re-asks while the answer is decompiled metadata. Roslyn binds a <c>ProjectReference</c>
    /// to the referenced project's built assembly until that project is loaded into the
    /// workspace, so a query fired in the window between the sentinel resolving and the last
    /// project loading comes back pointing at a temp file under <c>MetadataAsSource</c> —
    /// a confident wrong answer, exit 0, no relation to the repo. The daemon is what made that
    /// window reachable: readiness used to cost a full cold load, which closed it by accident.
    /// A symbol that really does come from an assembly costs this budget once and then answers.
    /// </summary>
    private async Task<IReadOnlyList<Location>> SettleAsync(
        Func<Task<IReadOnlyList<Location>>> query, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + BindBudget;
        while (true)
        {
            var locations = await query();
            if (locations.Count == 0 || !locations.Any(l => PathUri.IsDecompiled(l.Uri))) return locations;
            if (DateTime.UtcNow >= deadline) return locations;
            await Task.Delay(250, ct);
        }
    }


    public async Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolsAsync(string uri, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        var result = await _rpc.InvokeWithParameterObjectAsync<DocumentSymbol[]?>(
            "textDocument/documentSymbol",
            new DocumentSymbolParams(new TextDocumentIdentifier(uri)),
            ct);
        return result ?? [];
    }

    /// <summary>
    /// A freshly opened document is bound against whatever the server has at that instant,
    /// which for the first one is the misc-files state: it reports only what needs no project
    /// references. Waiting on readiness is not enough either, since the document was opened
    /// after it. So re-pull until two consecutive reports agree, or the budget runs out.
    /// </summary>
    public async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(string uri, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + SettleBudget;
        var previous = await PullDiagnosticsAsync(uri, ct);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct);
            var next = await PullDiagnosticsAsync(uri, ct);
            if (Same(previous, next)) return next;
            previous = next;
        }

        return previous;
    }

    private async Task<IReadOnlyList<Diagnostic>> PullDiagnosticsAsync(string uri, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        var report = await _rpc.InvokeWithParameterObjectAsync<DocumentDiagnosticReport?>(
            "textDocument/diagnostic",
            new DocumentDiagnosticParams(new TextDocumentIdentifier(uri)),
            ct);
        return report?.Items ?? [];
    }

    private static bool Same(IReadOnlyList<Diagnostic> a, IReadOnlyList<Diagnostic> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            p.First.Range == p.Second.Range &&
            p.First.Severity == p.Second.Severity &&
            p.First.Message == p.Second.Message);

    /// <summary>
    /// Roslyn will not answer requests for a document it does not consider open. Generated
    /// documents are the exception: they are the server's own, it answers for them without a
    /// didOpen, and there is no file to read the text from anyway.
    /// </summary>
    public async Task OpenAsync(string uri, CancellationToken ct)
    {
        if (PathUri.IsGenerated(uri) || !_open.Add(uri)) return;
        var text = await File.ReadAllTextAsync(PathUri.ToPath(uri), ct);
        await _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams(new TextDocumentItem(uri, "csharp", 1, text)));
    }

    /// <summary>
    /// Document text for rendering context lines: off disk for a real file, from the server
    /// for a generated one. An unreadable document yields no lines rather than failing —
    /// a hit with a correct position is still worth printing.
    /// </summary>
    public async Task<string[]> LinesAsync(string uri, CancellationToken ct)
    {
        if (_lines.TryGetValue(uri, out var cached)) return cached;

        string[] lines;
        if (PathUri.IsGenerated(uri))
        {
            var content = await _rpc.InvokeWithParameterObjectAsync<TextDocumentContentResult?>(
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
            lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, ct) : [];
        }

        _lines[uri] = lines;
        return lines;
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

    private static readonly string[] DaemonFallbackMarkers =
    [
        "Falling back to non-daemon mode",
        "non-daemon fallback mode",
    ];

    public string StderrTail(int lines = 12)
    {
        string text;
        lock (_stderr) { text = _stderr.ToString(); }
        if (text.Length == 0) return string.Empty;
        var tail = text.TrimEnd().Split('\n');
        return "\n--- server stderr ---\n" + string.Join('\n', tail[Math.Max(0, tail.Length - lines)..]);
    }

    public async ValueTask DisposeAsync()
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

        _rpc.Dispose();
        try
        {
            if (!_proc.WaitForExit(3000)) _proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited.
        }

        _proc.Dispose();
    }

    /// <summary>Server-to-client calls. An unhandled request would fault the connection.</summary>
    private sealed class Endpoints
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

        // Refresh requests for source-generated documents. csx is one-shot, so there is
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
