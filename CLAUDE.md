# roslyn-ls-agent

A CLI (`csx`) that gives coding agents semantic C# queries over Microsoft's official
`roslyn-language-server`, with a cron-driven update loop gated by a probe suite.

**Read `ROADMAP.md` first** for milestone state and acceptance criteria, and `DESIGN.md` for the
output rules every command follows and the decisions that are already settled. The README holds
the user-facing detail and the evidence behind the dependency choices.

## Commands

```
dotnet build src/Csx/Csx.csproj          # build
./probes/run.sh                          # the gate: restore, build, ready, run every case
./src/Csx/bin/Debug/net10.0/csx refs Greet --root fixture
```

`probes/run.sh` is the only real test suite. Run it before claiming anything works.

## Things that will cost you a session if you rediscover them

- **Positions are UTF-16 code units** and the server does not negotiate otherwise. .NET string
  indices are already UTF-16, so naive indexing is correct — counting runes or UTF-8 bytes is the
  bug. `LspClient.InitializeAsync` asserts the encoding and refuses to run if it ever changes.
- **Do not add `Console.OutputEncoding = UTF8`.** It looks required — this machine's console is
  code page 850 — but it fixes nothing and was tried and reverted. .NET writes a real console
  handle with `WriteConsoleW`, so the code page never applies, and redirected stdout is already
  UTF-8. Verified both ways against the emoji fixture line. Mojibake in a PowerShell pipeline
  (`csx refs ... | Select-String`) is PowerShell decoding our bytes with its own
  `[Console]::OutputEncoding`, which nothing `csx` sets can change.
- **The non-ASCII probe cases are the first host-dependent ones.** Both workflows run
  `ubuntu-latest`, where the encoding question does not arise; a regression here would be green
  on CI and red in Git Bash. Also note `File.ReadAllTextAsync` substitutes U+FFFD for invalid
  bytes rather than throwing, so a fixture file corrupted to a non-UTF-8 encoding would desync
  the `didOpen` text from what Roslyn parses off disk — silently, except that
  `non-ascii-refs-position` then fails.
- **Server flags live only in `src/Csx/ServerArgs.cs`.** The thin client forwards options it does
  not recognise straight through to the server, so a renamed flag produces no error at all. The
  probes are the only thing that catches it.
- **Roslyn's `containerName` is localised display text** (`in Greeter (project Core (net10.0))`),
  not a namespace path. A dotted symbol target can only narrow by enclosing type.
  `DOTNET_CLI_UI_LANGUAGE=en` is pinned on the server so it does not vary by machine locale.
- **Never gate readiness on the symbol being queried.** An absent symbol then looks identical to
  a workspace that has not loaded, and the caller waits out the whole timeout for a typo.
- **A query fired before load returns empty, not an error.** Never `sleep`; wait for
  `workspace/projectInitializationComplete` and then poll a sentinel that must resolve.
- **Roslyn will not answer for documents it does not consider open** — `didOpen` first. The
  exception is a source-generated document: the server owns it, answers without a `didOpen`,
  and there is no file to read the text from. `OpenAsync` skips them.
- **Source-generated locations arrive under `roslyn-source-generated:`, and every path
  helper lies about them.** `new Uri(u).LocalPath` does not throw — it returns
  `/BuildInfo.g.cs`, which then renders as a confident wrong answer with no context lines and
  exit 0. Route every location through `PathUri.Display`. The authority guid, the
  `documentId` and `assemblyPath` in that URI all change between runs and machines, so
  **never assert on the raw URI** — only `hintName`, `assemblyName`, `assemblyVersion` and
  `typeName` are stable. Text comes from `workspace/textDocumentContent`, which the server
  implements without advertising a `textDocumentContentProvider` and answers whether or not
  the client declares the matching capability (verified both ways). The older
  `sourceGeneratedDocument/_roslyn_getText` no longer exists.
- **An unbuilt source generator produces nothing, silently.** With `fixture/Gen/bin` absent
  the workspace still loads, the sentinel still resolves, and only the generated symbol is
  missing — no error, no diagnostic, no CS9057 on the wire. `run.sh` builds `fixture/Gen`
  with `-c Debug` pinned, because the design-time build resolves the analyzer from
  `Gen/bin/Debug`; building it Release leaves that path stale.
- **A malformed request payload takes the server's whole queue down.** Sending
  `workspace/textDocumentContent` with `{textDocument:{uri}}` instead of `{uri}` returned
  `TaskCanceled`, and every later request then failed with `-32000: Server was requested to
  shut down`. Payloads are hand-rolled in `Protocol.cs`, so a shape mistake is silent and
  then fatal rather than a clean error.
- **The server does not restore your projects.** `dotnet restore` before starting it.

## C# and .NET rules

This repo is .NET 10 / C# 14: a CLI and a thin LSP client, no UI, no web host, no DI container.

- **`dotnet format` must pass clean.** `dotnet format --verify-no-changes` exits 0 today; keep it
  that way and run `dotnet format` before calling a change done. There is deliberately **no
  `.editorconfig` yet**, so `dotnet format` enforces its own defaults rather than house style —
  match the surrounding code instead of reformatting a file you touched.
- **`DateTime.UtcNow`, never `DateTime.Now`.** Every deadline in `LspClient` is UTC.
- **No `async void`** outside an event handler, and never `.Result` or `.Wait()` — `csx` is async
  from `Main` down, and a sync-over-async wait here deadlocks against the JSON-RPC read loop.
- **Reach for C# 14 first:** `extension` blocks rather than `this` extension methods, the `field`
  keyword rather than a hand-written backing field, `x?.P = v` rather than an `if` guard. C# 14
  comes free with `net10.0` and `LangVersion` is deliberately unset. `fixture/Gen` is the one
  exception: analyzers must target netstandard2.0, whose default is C# 7.3, so it pins
  `<LangVersion>latest</LangVersion>` explicitly.
- **`[LibraryImport]`, not `[DllImport]`**, if native interop ever appears.
- **Fix root causes and delete what is dead.** Don't preserve a shape for backwards
  compatibility — nothing depends on `csx`'s internals yet. Simplify rather than layering.
- **Never push to a remote, and never commit unless asked.** `bump.yml` is the only thing that
  opens PRs here.

## Conventions

- `probes/run.sh` parses `cases.jsonl` with `sed` alone. **No `jq`** — it does not exist in Git
  Bash on the dev machine. (`python` does, 3.14.6, despite what this file used to claim; the
  `sed`-only rule still stands for the GitHub runner.) Keep `cases.jsonl` to four flat string fields.
- `probes/run.sh` must stay mode `100755` in the index. Windows Git has `core.filemode=false`, so
  `chmod +x` does not register; use `git update-index --chmod=+x` if it ever reverts.
- Adding a NuGet package for LSP types is a regression, not a cleanup. See the README.
- A bump PR opened with `GITHUB_TOKEN` gets a `probe.yml` run parked at `action_required` that
  never executes. The `probes` commit status published by `bump.yml` is the real gate.
