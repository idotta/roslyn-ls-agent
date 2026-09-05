namespace Fixture.Core;

/// <summary>
/// The interface half of the `csx impl` fixture. Its implementers are deliberately split
/// across projects -- `Unit` here, `Square` in App -- so a `textDocument/implementation`
/// answer has to cross a ProjectReference to be complete. A pair declared entirely in Core
/// would resolve inside one compilation and pass even with cross-project binding broken.
/// </summary>
public interface IShape
{
    int Area();
}

public sealed class Unit : IShape
{
    public int Area() => 1;
}
