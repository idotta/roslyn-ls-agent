namespace Fixture.Core;

/// <summary>
/// One type declared across two documents. This is the shape `csx outline` has to refuse
/// rather than pick from, and it is not the same thing as an overload: overloads share a
/// document and collapse to one outline, so `OutlineTargetAsync` only complains when the
/// documents differ. Nothing else in the fixture produces more than one declaration of a
/// name, so without this the ambiguity branch is unreachable.
/// </summary>
public static partial class Split
{
    public static string Left() => "left";

    // Two declarations of one name in ONE document. The opposite case to the type above:
    // `outline` must collapse these and outline the document, not call them ambiguous.
    public static string Left(string tag) => $"left:{tag}";
}
