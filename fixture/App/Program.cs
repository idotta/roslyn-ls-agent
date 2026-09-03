using Fixture.Core;

namespace Fixture.App;

public static class Program
{
    public static void Main()
    {
        Console.WriteLine(Greeter.Greet("world"));
        Console.WriteLine(Fixture.Core.Generated.BuildInfo.Stamp());
        Console.WriteLine($"🎉 {Party.Cheer("world")}");
    }
}
