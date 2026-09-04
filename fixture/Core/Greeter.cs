namespace Fixture.Core;

public static class Greeter
{
    public static string Greet(string name) => $"Hello, {name}!";

    // Referenced only by App/TypeError.cs, whose deliberate CS0029 has to be a cross-project
    // error. Keeping it off Greet is what leaves the refs cases' reference counts pinned.
    public static string Farewell(string name) => $"Bye, {name}!";
}
