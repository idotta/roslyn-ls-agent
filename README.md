# roslyn-ls-agent

Semantic C# queries for coding agents, over Microsoft's official
[`roslyn-language-server`](https://www.nuget.org/packages/roslyn-language-server) — the same
engine behind the VS Code C# extension. `csx` is a thin LSP client that turns LSP's URIs and
zero-based ranges into `path:line` plus source context, so an agent can find every caller of a
method instead of grepping for its name.

Two constraints drive the design:

1. **Official tooling only.** The C#-specific component in the query path is Microsoft-published.
2. **Always current.** A weekly cron bumps the pin and a probe suite gates the bump.

Status: **Milestone 1** — `csx ready` and `csx refs`, cross-project fixture, probe gate, both
workflows. `def` / `diag` / `outline` / `impl` / `sym`, the source-generator and non-ASCII
fixture cases, and daemon mode are Milestones 2–4.

## Use

```
csx ready                                    # block until the workspace has loaded
csx refs <symbol | file:line:col> [--max N]  # every reference, with context
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

Options: `--root <dir>` (default: cwd), `--sentinel <symbol>`, `--max N` (default 50),
`--context N` (default 1), `--timeout N` seconds (default 180), `--log-level L`, `--json`.

Paths are relative to `--root`; lines and columns are one-based.

`refs` exits 1 with `no results` when a symbol resolves but has no references, and 1 with a
diagnostic when the symbol does not resolve or the workspace never loaded.

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

`bump.yml` runs Mondays at 05:00 UTC, updates the pin, runs the probes, and opens a PR that
`probe.yml` re-gates. It needs *Settings → Actions → Allow GitHub Actions to create and approve
pull requests* enabled on the repo.

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

## Failure modes this handles

| Failure mode | How |
|---|---|
| Async project load returning empty instead of erroring | `WaitReadyAsync` waits for `workspace/projectInitializationComplete`, then polls a sentinel symbol until it resolves, then fails loudly on timeout. Never `sleep`. |
| A sentinel that is itself the thing being queried | The sentinel is inferred from a type declaration in the tree, so "symbol absent" and "workspace not loaded" stay distinguishable. The target gets a 10 s grace poll after readiness. |
| UTF-16 position encoding | The server does not advertise `positionEncoding`, which per LSP 3.17 means utf-16 — the same unit as a .NET string index. `csx` asserts this at `initialize` and refuses to run if a future build negotiates utf-8. |
| Roslyn ignoring unopened documents | Every query opens its document via `textDocument/didOpen` first. |
| No auto-restore | `probes/run.sh` runs `dotnet restore` on the fixture before starting the server. |
| Server-to-client requests faulting the connection | `LspClient.Endpoints` answers `workspace/configuration`, `client/registerCapability`, `window/workDoneProgress/create` and friends. |
| A renamed server flag failing silently | The thin client forwards unrecognised options straight through to the server, so a rename produces no error. Flags live only in `src/Csx/ServerArgs.cs`, and the probes are the only guard. |

## Probes

```
./probes/run.sh
```

Restores the tool and the fixture, builds `csx`, asserts readiness, then runs every case in
`probes/cases.jsonl`. Exits non-zero on any mismatch. Six cases today, including a negative one
that pins a query fired before load to a loud failure rather than an empty result.

`cases.jsonl` is one flat JSON object per line with four string fields so `run.sh` can parse it
with `sed` alone — no `jq`, no Python, so it runs unchanged on a GitHub runner and in Git Bash.
Inside `expect`, `'` stands for `"` and `|` separates substrings that must all appear.

## Layout

```
.config/dotnet-tools.json   the version pin
.github/workflows/          bump.yml (weekly cron), probe.yml (every PR)
src/Csx/                    the thin LSP client and CLI
  ServerArgs.cs             the only place server flags live
  Protocol.cs               hand-defined LSP payload types
  LspClient.cs              transport, initialize, readiness, didOpen
  Output.cs                 path:line + context formatting
fixture/                    deliberately tricky solution (cross-project reference)
probes/                     cases.jsonl + run.sh
```
