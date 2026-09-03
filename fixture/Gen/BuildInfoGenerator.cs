using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace Fixture.Gen;

[Generator]
public sealed class BuildInfoGenerator : IIncrementalGenerator
{
    private const string Source = """
        namespace Fixture.Core.Generated
        {
            public static class BuildInfo
            {
                public static string Stamp() => "fixture-stamp";
            }
        }
        """;

    /// <summary>
    /// Keyed on a declaration in Core rather than emitted from
    /// <c>RegisterPostInitializationOutput</c>. Post-initialization output is produced before
    /// any compilation analysis and so can never go stale, which would make this fixture pass
    /// without ever exercising the path that can: a generated symbol whose existence depends
    /// on the compilation. Delete <c>Greeter</c> and <c>BuildInfo</c> has to disappear with it.
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var anchor = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => ((ClassDeclarationSyntax)ctx.Node).Identifier.ValueText)
            .Where(static name => name == "Greeter")
            .Collect();

        context.RegisterSourceOutput(anchor, static (ctx, names) =>
        {
            if (names.IsDefaultOrEmpty) return;
            ctx.AddSource("BuildInfo.g.cs", SourceText.From(Source, Encoding.UTF8));
        });
    }
}
