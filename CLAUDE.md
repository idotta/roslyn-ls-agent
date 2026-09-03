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
- **Roslyn will not answer for documents it does not consider open** — `didOpen` first.
- **The server does not restore your projects.** `dotnet restore` before starting it.

## Conventions

- `probes/run.sh` parses `cases.jsonl` with `sed` alone. **No `jq`, no Python** — neither exists
  in Git Bash on the dev machine. Keep `cases.jsonl` to four flat string fields.
- `probes/run.sh` must stay mode `100755` in the index. Windows Git has `core.filemode=false`, so
  `chmod +x` does not register; use `git update-index --chmod=+x` if it ever reverts.
- Adding a NuGet package for LSP types is a regression, not a cleanup. See the README.
- A bump PR opened with `GITHUB_TOKEN` gets a `probe.yml` run parked at `action_required` that
  never executes. The `probes` commit status published by `bump.yml` is the real gate.
