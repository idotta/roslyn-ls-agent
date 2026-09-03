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

    public async Task<IReadOnlyList<Location>> ReferencesAsync(string path, Position position, CancellationToken ct)
    {
        await OpenAsync(path, ct);
        var result = await _rpc.InvokeWithParameterObjectAsync<Location[]?>(
            "textDocument/references",
            new ReferenceParams(
                new TextDocumentIdentifier(PathUri.FromPath(path)), position, new ReferenceContext(true)),
            ct);
        return result ?? [];
    }

    /// <summary>Roslyn will not answer requests for a document it does not consider open.</summary>
    public async Task OpenAsync(string path, CancellationToken ct)
    {
        if (!_open.Add(path)) return;
        var text = await File.ReadAllTextAsync(path, ct);
        await _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams(new TextDocumentItem(PathUri.FromPath(path), "csharp", 1, text)));
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
