using Fixture.Core;

namespace Fixture.App;

/// <summary>
/// The deliberate type error for `csx diag`. Three things about it are load-bearing.
/// It is a *cross-project* error: binding it needs Core's reference resolved, so the
/// misc-files state a freshly opened document is first bound against cannot report it,
/// which is what makes the re-pull after load testable. It calls `Farewell` rather than
/// `Greet` so the refs cases keep their pinned reference counts. And the file is named to
/// sort after `Program.cs`, because `Program.InferSentinel` walks the tree in path order
/// and the readiness sentinel should not be a type declared in the deliberately broken file.
/// Never move this into Core: probes/run.sh compiles Core to arm its CS9057 guard, and an
/// uncompilable Core would disarm that permanently. Nothing builds App.
/// </summary>
internal static class TypeError
{
    internal static int Wrong() => Greeter.Farewell("x");
}
