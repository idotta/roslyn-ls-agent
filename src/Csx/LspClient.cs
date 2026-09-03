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

    public string Root { get; }

    private LspClient(string root, Process proc, JsonRpc rpc, Endpoints endpoints, StringBuilder stderr)
        => (Root, _proc, _rpc, _endpoints, _stderr) = (root, proc, rpc, endpoints, stderr);

    public static async Task<LspClient> StartAsync(string root, string logLevel, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ServerArgs.Command)
        {
            WorkingDirectory = ServerArgs.ToolManifestRoot(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in ServerArgs.Stdio(logLevel)) psi.ArgumentList.Add(a);

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
        await client.InitializeAsync(ct);
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
                    new DiagnosticCapabilities(true, true)),
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
    /// Project load is async, and a query fired too early returns empty results rather than
    /// an error. Waits for the load notification, then keeps polling the sentinel until it
    /// actually resolves: a warm daemon fired the notification before we connected, and the
    /// notification on its own does not mean indexing has finished.
    /// </summary>
    public async Task WaitReadyAsync(string sentinel, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;

        var remaining = deadline - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.WhenAny(_endpoints.ProjectInitialized, Task.Delay(remaining, ct));
        }

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
        var result = await _rpc.InvokeWithParameterObjectAsync<Location[]?>(
            "textDocument/references",
            new ReferenceParams(new TextDocumentIdentifier(uri), position, new ReferenceContext(true)),
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
