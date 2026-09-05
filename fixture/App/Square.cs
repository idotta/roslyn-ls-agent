using Fixture.Core;

namespace Fixture.App;

/// <summary>
/// The cross-project half of the `csx impl` fixture -- see `Fixture.Core.IShape`. Named to
/// sort after `Program.cs` so `Program.InferSentinel`, which walks the tree in path order,
/// keeps picking `Program`.
/// </summary>
internal sealed class Square(int side) : IShape
{
    public int Area() => side * side;
}
