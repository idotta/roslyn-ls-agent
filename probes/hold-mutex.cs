// Holds the thin client's daemon-startup mutex so that the next client times out waiting for
// it and takes its silent non-daemon fallback. That fallback is the whole point: a run that
// takes it still answers correctly, just cold, and nothing but one stderr line distinguishes
// it -- so `csx` watches for it, and this is how the watching gets tested.
//
// A file-based app rather than a project. It needs to be a .NET process because the name and
// both options belong to the server, not to us: it creates Global\<pipeName>.client with
// CurrentUserOnly, and a mutex of that name created any other way -- from PowerShell, or
// without CurrentSessionOnly = false -- throws instead of contending, which looks exactly
// like the mechanism not working.
//
// Takes the pipe name and builds the mutex name here on purpose. Passing the whole name from
// run.sh means writing a backslash inside a double-quoted shell string next to a variable,
// where `"Global\${pipe}.client"` silently yields a literal `${pipe}` -- a mutex nothing
// contends for, so the case fails with no hint of why.
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run probes/hold-mutex.cs -- <pipe name> <seconds>");
    return 2;
}

var name = $@"Global\{args[0]}.client";
using var mutex = new Mutex(
    false, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });

if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
{
    Console.Error.WriteLine($"could not acquire '{name}' within 10s");
    return 1;
}

// The caller waits for this line before starting the client it wants to see fall back.
Console.WriteLine($"held {name}");
Console.Out.Flush();

Thread.Sleep(TimeSpan.FromSeconds(int.Parse(args[1])));
mutex.ReleaseMutex();
return 0;
