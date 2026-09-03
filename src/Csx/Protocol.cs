using System.Text.Json;
using System.Text.Json.Serialization;

namespace Csx;

// Minimal LSP 3.17 subset, hand-defined because no maintained Microsoft package
// supplies these types:
//   * Microsoft.CodeAnalysis.LanguageServer.Protocol is unlisted on nuget.org.
//   * Microsoft.VisualStudio.LanguageServer.Protocol last shipped 17.2.8 (May 2022),
//     predates LSP 3.17, has no PositionEncoding, and is Newtonsoft-based.
// dotnet/roslyn#68696 tracks making the real ones public; it is still open.
// Framing and request correlation still come from StreamJsonRpc — only the
// payload shapes are ours.

internal static class Lsp
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record Position(int Line, int Character);

internal sealed record Range(Position Start, Position End);

internal sealed record Location(string Uri, Range Range);

internal sealed record TextDocumentIdentifier(string Uri);

internal sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

internal sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

internal sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

internal sealed record ReferenceContext(bool IncludeDeclaration);

internal sealed record ReferenceParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    ReferenceContext Context);

internal sealed record WorkspaceSymbolParams(string Query);

// LSP 3.18. The server implements this but never advertises a textDocumentContentProvider
// in its initialize result, and answers regardless of whether the client declares the
// matching capability — verified both ways against 5.12.0-1.26426.8.
internal sealed record TextDocumentContentParams(string Uri);

internal sealed record TextDocumentContentResult(string Text);

internal sealed record SymbolInformation(
    string Name,
    int Kind,
    Location Location,
    string? ContainerName);

internal sealed record WorkspaceFolder(string Uri, string Name);

internal sealed record ClientInfo(string Name, string Version);

internal sealed record GeneralCapabilities(string[] PositionEncodings);

internal sealed record SynchronizationCapabilities(bool DynamicRegistration);

internal sealed record DiagnosticCapabilities(bool DynamicRegistration, bool RelatedDocumentSupport);

internal sealed record TextDocumentCapabilities(
    SynchronizationCapabilities Synchronization,
    DiagnosticCapabilities Diagnostic);

internal sealed record SymbolCapabilities(bool DynamicRegistration);

internal sealed record WorkspaceCapabilities(
    bool Configuration,
    bool WorkspaceFolders,
    SymbolCapabilities Symbol);

internal sealed record WindowCapabilities(bool WorkDoneProgress);

internal sealed record ClientCapabilities(
    GeneralCapabilities General,
    TextDocumentCapabilities TextDocument,
    WorkspaceCapabilities Workspace,
    WindowCapabilities Window);

internal sealed record InitializeParams(
    int ProcessId,
    ClientInfo ClientInfo,
    string Locale,
    string RootUri,
    ClientCapabilities Capabilities,
    WorkspaceFolder[] WorkspaceFolders);

internal sealed record ServerCapabilities(string? PositionEncoding);

internal sealed record InitializeResult(ServerCapabilities Capabilities);

internal sealed record ConfigurationItem(string? ScopeUri, string? Section);

internal sealed record ConfigurationParams(ConfigurationItem[] Items);
