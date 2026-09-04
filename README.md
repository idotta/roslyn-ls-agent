# roslyn-ls-agent

Semantic C# queries for coding agents, over Microsoft's official
[`roslyn-language-server`](https://www.nuget.org/packages/roslyn-language-server) — the same
engine behind the VS Code C# extension. `csx` is a thin LSP client that turns LSP's URIs and
zero-based ranges into `path:line` plus source context, so an agent can find every caller of a
method instead of grepping for its name.

Two constraints drive the design:

1. **Official tooling only.** The C#-specific component in the query path is Microsoft-published.
2. **Always current.** A weekly cron bumps the pin and a probe suite gates the bump.

Status: **Milestone 2 done** — `csx ready`, `csx refs`, `csx def`, `csx outline` and
`csx diag`, cross-project fixture, probe gate, both workflows, plus the source-generator,
non-ASCII and deliberate-error fixture cases. `impl` / `sym` and daemon mode are
Milestones 3–4. See [ROADMAP.md](ROADMAP.md).

## Use

```
csx ready                                    # block until the workspace has loaded
csx refs <symbol | file:line:col> [--max N]  # every reference, with context
csx def <symbol | file:line:col>             # where it is declared
csx outline <file | symbol> [--max N]        # the declarations in one document
csx diag [path] [--errors-only]              # compiler and analyzer diagnostics
```

```
$ csx refs Fixture.Core.Greeter.Greet --root fixture
App/Program.cs:9:35
   8 |     {
>  9 |         Console.WriteLine(Greeter.Greet("world"));
  10 |     }

Core/Greeter.cs:5:26
  4 | {
> 5 |     public static string Greet(string name) => $"Hello, {name}!";
  6 | }
```

```
$ csx def App/Program.cs:9:35 --root fixture
Core/Greeter.cs:5:26
  4 | {
> 5 |     public static string Greet(string name) => $"Hello, {name}!";
  6 | }
```

```
$ csx outline Core/Greeter.cs --root fixture
Core/Greeter.cs
  1 | namespace Fixture.Core;
  3 |   public static class Greeter
  5 |     public static string Greet(string name) => $"Hello, {name}!";
  9 |     public static string Farewell(string name) => $"Bye, {name}!";
```

`outline` is the one command that does not print `path:line` and context per row — an outline
is already the summary, so the document path is a header and each row carries that
declaration's own source line, indented by nesting. `--context` does not apply to it. Its
target is a file path, a `file:line:col` spec (the document it names is outlined, so a position
copied out of a `def` result works), or a symbol whose declaring document is outlined — the
last being the only way to reach a source-generated document, which has no path on disk.

```
$ csx diag App/TypeError.cs --root fixture
App/TypeError.cs:18:36 error CS0029: Cannot implicitly convert type 'string' to 'int'
  17 | {
> 18 |     internal static int Wrong() => Greeter.Farewell("x");
  19 | }
```

`diag` takes a file, a directory, or nothing at all — with no argument it walks every `.cs` file
under `--root`, skipping `bin` and `obj`. It does *not* use `workspace/diagnostic`: the server
answers that endpoint but returns zero reports, which is what the `workspaceDiagnostics: false`
in its dynamic registration means.

Options: `--root <dir>` (default: cwd), `--sentinel <symbol>`, `--max N` (default 50),
`--context N` (default 1), `--timeout N` seconds (default 180), `--log-level L`,
`--errors-only` (`diag` only), `--json`.

Paths are relative to `--root`; lines and columns are one-based.

`refs` exits 1 with `no results` when a symbol resolves but has no references, and 1 with a
diagnostic when the symbol does not resolve or the workspace never loaded.

`diag` exits 0 whenever the query was answered, findings or not — a clean file is a successful
`diag`, unlike an empty `refs`, which means the lookup failed. It exits 1 only when the workspace
never loaded or the path does not exist.

### Symbol names

A dotted target narrows by **enclosing type**, not by namespace: `Greeter.Greet` and
`Fixture.Core.Greeter.Greet` both work, but the namespace part is not actually checked. Roslyn
returns `containerName` as a localised display string (`in Greeter (project Core (net10.0))`),
not a namespace path, so there is nothing to match a namespace against. When a target stays
ambiguous, `csx` lists the candidates with their locations so you can switch to `file:line:col`.

`csx` pins `DOTNET_CLI_UI_LANGUAGE=en` on the server so those display strings do not change with
the developer's machine locale.

## Latency

Measured on the fixture, Windows 11 / .NET 10.0.301, debug build:

| command | cold (per invocation) |
|---|---|
| `csx ready` | ~3.9–4.1 s |
| `csx refs` | ~5.9–14.7 s |
| `csx def` | ~6.4–7.0 s |
| `csx outline` | ~5.4–6.2 s |
| `csx diag <file>` | ~11–12 s |
| `csx diag` (whole fixture, 6 files) | ~16 s |

The same suite on `ubuntu-latest` reaches ready in ~12 s and runs six cases in ~39 s.

Milestone 1 starts a dedicated server per invocation, so every command pays a full solution
load. Milestone 3 switches to `--daemon-mode`, where the cost should be a pipe round-trip
against an already-warm server; warm numbers get recorded here then.

## The pin

`.config/dotnet-tools.json` pins the exact version; `dotnet tool restore` reproduces it.

Verified against nuget.org on 2026-09-02:

- **No stable release exists.** The RID-specific packages each show a bare `5.11.0` on the
  flat-container index, but it is *unlisted* and the non-RID `roslyn-language-server` never got
  a `5.11.0` at all — `dotnet tool install roslyn-language-server` without `--prerelease` fails
  outright. `--prerelease` is load-bearing, and a bump script must never scrape the
  flat-container index or it will pin something uninstallable. `dotnet tool update --prerelease`
  reads the registration API and skips unlisted versions.
- **The non-RID tool ID resolves per-platform.** It is a 130 KB shim whose
  `DotnetToolSettings.xml` maps all eight RIDs, so CI needs no RID selection. The payload it
  pulls is ~300 MB, hence the NuGet cache in `probe.yml`.
- Latest at time of writing: `5.12.0-1.26426.8` (2026-08-27). Minor moves between releases,
  roughly every 2–4 weeks — which is why the bump is a cron job rather than Renovate, whose
  `ignoreUnstable` default would silently never fire on this train.

`bump.yml` runs Mondays at 05:00 UTC, updates the pin, runs the probes, and opens a PR. It needs
*Settings → Actions → Allow GitHub Actions to create and approve pull requests* enabled on the
repo. When the pin is already current the update is a no-op and no PR is opened.

A PR opened with `GITHUB_TOKEN` does not get a working `probe.yml` run: GitHub creates the run
with `github-actions[bot]` as the actor and parks it at `action_required`, waiting for a human
to approve it, so it never executes. `bump.yml` therefore runs the probes itself before opening
the PR and publishes the result as a `probes` commit status — that is the check to require in
branch protection. Setting a `BUMP_TOKEN` secret (a PAT with `repo` scope) makes the PR run
`probe.yml` for real as well.

## Dependencies

`csx` depends on **StreamJsonRpc** (Microsoft, MIT) for `Content-Length` framing, request
correlation and notifications. Nothing C#-specific is third-party.

The LSP payload types in `src/Csx/Protocol.cs` are ours, which is a deliberate departure — no
maintained Microsoft package supplies them:

- `Microsoft.CodeAnalysis.LanguageServer.Protocol` is **unlisted** on nuget.org and absent from
  search. Roslyn maintainers describe these packages as "glorified .zips".
- `Microsoft.VisualStudio.LanguageServer.Protocol` last shipped **17.2.8 (May 2022)**. It
  targets netstandard2.0, depends on Newtonsoft.Json, ships under a VS SDK EULA rather than MIT,
  and predates LSP 3.17 — the assembly has no `PositionEncoding` type at all, so it cannot
  express the one capability the column-correctness story rests on.
- [dotnet/roslyn#68696](https://github.com/dotnet/roslyn/issues/68696), the tracking issue for
  making the real protocol APIs public and stable, is still open with no timeline.

Third-party alternatives are worse: `LspTypes` is LSP 3.16 and last shipped January 2021;
`OmniSharp.Extensions.*` last shipped 0.19.9 in September 2023 and drags in MediatR.

Only the payload shapes are hand-defined. Framing and correlation still come from StreamJsonRpc.

`fixture/Gen` references **Microsoft.CodeAnalysis.CSharp** (Microsoft, MIT) because a source
generator cannot be written without it. It is fixture-only and never loaded by `csx`, so the
query path stays free of C#-specific third-party code. The version is pinned **low** (4.3.0,
well past `IIncrementalGenerator`'s introduction) and deliberately never tracks the server:
the analyzer is loaded by two independently-moving compilers — the SDK's `csc` during
`dotnet build` and the language server's hosted Roslyn — and Roslyn only ever moves forward,
so a low pin is permanently compatible while a tracking pin would need re-verifying on every
weekly bump.

## Failure modes this handles

| Failure mode | How |
|---|---|
| Async project load returning empty instead of erroring | `WaitReadyAsync` waits for `workspace/projectInitializationComplete`, then polls a sentinel symbol until it resolves, then fails loudly on timeout. Never `sleep`. |
| A sentinel that is itself the thing being queried | The sentinel is inferred from a type declaration in the tree, so "symbol absent" and "workspace not loaded" stay distinguishable. The target gets a 10 s grace poll after readiness. |
| UTF-16 position encoding | The server does not advertise `positionEncoding`, which per LSP 3.17 means utf-16 — the same unit as a .NET string index. `csx` asserts this at `initialize` and refuses to run if a future build negotiates utf-8. A fixture line carrying an astral-plane character (a surrogate pair, so utf-16 and rune counts differ) pins the reported column at 39 in three cases; an accented letter would pass even on a broken implementation. |
| A first diagnostic pull answered from the misc-files state | A freshly opened document is bound against whatever the server has at that instant, and for the first one that is the misc-files state, which reports only errors needing no project references. `DiagnosticsAsync` re-pulls until two consecutive reports agree (5 s budget). The fixture's error is deliberately *cross-project* — binding it needs Core's reference resolved — so a first-response-only implementation reports nothing and the case fails. |
| Roslyn ignoring unopened documents | Every query opens its document via `textDocument/didOpen` first — except source-generated ones, which the server owns and answers for without it. |
| No auto-restore | `probes/run.sh` runs `dotnet restore` on the fixture before starting the server. |
| Source-generated symbols rendering as a nonexistent path | Generated documents come back under a `roslyn-source-generated:` URI. `new Uri(u).LocalPath` does not throw for one, it returns `/BuildInfo.g.cs`, so `PathUri.Display` branches on the scheme and labels them `<generated>/<assembly>/<hintName>`. Text comes from `workspace/textDocumentContent`. |
| An unbuilt source generator contributing nothing, silently | With the analyzer assembly absent the workspace still loads and the sentinel still resolves; only the generated symbol is missing, with no error or diagnostic anywhere. `probes/run.sh` builds `fixture/Gen` before starting the server, and three cases assert the generated symbol resolves. |
| Server-to-client requests faulting the connection | `LspClient.Endpoints` answers `workspace/configuration`, `client/registerCapability`, `window/workDoneProgress/create` and friends. |
| A renamed server flag failing silently | The thin client forwards unrecognised options straight through to the server, so a rename produces no error. Flags live only in `src/Csx/ServerArgs.cs`, and the probes are the only guard. |

## Probes

```
./probes/run.sh
```

Restores the tool and the fixture, builds `csx`, asserts readiness, then runs every case in
`probes/cases.jsonl`. Exits non-zero on any mismatch. Twenty-nine cases today, including a
negative one that pins a query fired before load to a loud failure rather than an empty result.

`cases.jsonl` is one flat JSON object per line with four string fields so `run.sh` can parse it
with `sed` alone — no `jq`, which is absent from Git Bash on the dev machine. That keeps it
running unchanged on a GitHub runner and in Git Bash.
Inside `expect`, `'` stands for `"` and `|` separates substrings that must all appear.

## Layout

```
.config/dotnet-tools.json   the version pin
.github/workflows/          bump.yml (weekly cron), probe.yml (every PR)
src/Csx/                    the thin LSP client and CLI
  ServerArgs.cs             the only place server flags live
  Protocol.cs               hand-defined LSP payload types
  LspClient.cs              transport, initialize, readiness, didOpen
  Output.cs                 path:line + context formatting, and the outline tree
fixture/                    deliberately tricky solution
  Gen/                      incremental source generator; its output is referenced from App
  App/TypeError.cs          the deliberate cross-project type error for `csx diag`
  Core/Party.cs             an astral-plane character on a line carrying a symbol
  Core/Split*.cs            one type in two documents, plus an overload in one of them
  Core/Empty.cs             a compilable document that declares nothing
probes/                     cases.jsonl + run.sh
```
