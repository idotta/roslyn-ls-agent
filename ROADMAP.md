# Roadmap

Work spans multiple sessions. This file is the handoff: what is done, what is next, and which
questions are already settled so they don't get re-litigated.

Last updated: 2026-09-03, after Milestone 1.

## Status

| Milestone | Scope | State |
|---|---|---|
| 1 | `ready` + `refs`, cross-project fixture, probe gate, both workflows | **done** |
| 2 | The hard fixture cases and the read commands | not started |
| 3 | Daemon mode, then `skill/SKILL.md` | not started |
| 4 | Remaining commands and output tuning | not started |

## Milestone 1 — the loop works (done)

`csx ready` and `csx refs`, a fixture with a cross-project reference, six probe cases, and
`bump.yml` / `probe.yml` both green. The full pin → bump → probe → PR loop was exercised against
a deliberately stale pin and opens a PR carrying a passing `probes` status.

## Milestone 2 — the hard cases

The point of this milestone is that a probe suite which only checks go-to-definition inside one
file passes while everything real is broken. Add the fixture cases first, then the commands.

Fixture:

- [ ] A **source generator** producing a symbol that gets referenced from another project.
      `--sourceGeneratorExecutionPreference <Automatic|Balanced>` exists on the server and
      defaults to `Automatic`; `workspace/_roslyn_refreshSourceGenerators` also exists if
      generated symbols turn out to go stale.
- [ ] A file with **non-ASCII characters** on a line containing a symbol. Use an **astral-plane
      character (an emoji)**, not just an accented letter: positions are UTF-16 code units, which
      .NET string indices already are, so an accent passes even on a broken implementation. Only
      a surrogate pair catches code that counts runes or UTF-8 bytes.
- [ ] One **deliberate type error** for `csx diag`.

Commands:

- [ ] `csx def <file>:<line>:<col>` — also accepts `Namespace.Type.Member`. Symbol resolution and
      the position parser already exist in `Program.LocateAsync`.
- [ ] `csx diag [path] [--errors-only]`. The server implements `textDocument/diagnostic` and
      `workspace/diagnostic` but does **not** advertise `diagnosticProvider` in its initialize
      result — it registers dynamically via `client/registerCapability`, which
      `LspClient.Endpoints` currently accepts and discards. Either record the registration or
      call the endpoint optimistically.
- [ ] `csx outline <file>` via `textDocument/documentSymbol`.

Also: the first document opened only reports errors that need no loaded project (a missing
semicolon, say). Re-pull after load settles rather than trusting the first response, and add a
probe case pinning that.

- [ ] Expand `cases.jsonl` to cover all of the above.

## Milestone 3 — daemon and skill

- [ ] Switch `ServerArgs` to `--daemon-mode`. Do **not** pass `--clientProcessId`; it makes the
      server exit when the client does, which defeats the point.
- [ ] Verify a second concurrent client gets its own isolated server instance.
- [ ] Verify the daemon survives killing the first client's whole process tree.
- [ ] Consider `--daemonKeepAlive` / `ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE`; default is 900 s,
      `-1` keeps it alive indefinitely.
- [ ] Re-measure latency warm and update the README table.
- [ ] Write `skill/SKILL.md`.

`SKILL.md` conventions: YAML frontmatter with `name` and `description`, where the description is
the entire triggering mechanism and should lean pushy, since skills under-trigger. Body under
~500 lines, command reference inline. Include an explicit "task → use this command → do NOT use
grep/read for this" table, which is the part that actually changes agent behaviour. Tell the
agent to run `csx ready` once at session start.

## Milestone 4 — the rest

- [ ] `csx impl <symbol>` via `textDocument/implementation`.
- [ ] `csx sym <query>` — `workspace/symbol` is already wired up in `LspClient.SymbolsAsync`.
- [ ] Output tuning.

## Acceptance criteria

- [x] `.config/dotnet-tools.json` pins `roslyn-language-server`; `dotnet tool restore` reproduces it
- [x] Zero non-Microsoft C#-specific dependencies in the query path
- [x] `csx refs` on a cross-project symbol returns correct `file:line` plus context
- [ ] `csx refs` on a source-generated symbol resolves
- [ ] Column positions correct on the non-ASCII fixture line
- [ ] `csx diag` finds the deliberate error and does *not* report it before load completes
- [x] Probe suite fails loudly when the server returns empty due to premature querying
- [x] `bump.yml` opens a PR that is gated (see the `probes` commit status caveat in the README)
- [ ] Two concurrent clients work; daemon survives killing client 1's process tree
- [ ] Warm command latency measured and recorded in the README

## Settled — do not re-litigate

Decisions, with the reason, so a later session doesn't spend the day rediscovering them.

| Decision | Why |
|---|---|
| Server is `roslyn-language-server` | Microsoft-published, MIT, same engine as the VS Code C# extension |
| We write our own thin LSP client | A client is needed for the probes regardless; two clients would disagree about readiness |
| Agent surface is a CLI, not an MCP server | MCP tool schemas cost context on every request; a CLI costs one paragraph in `SKILL.md`, and we control the output shape |
| Updates via cron GitHub Action | Renovate's `ignoreUnstable` default would silently never bump this train; Dependabot has a history of mangling `dotnet-tools.json`. Beatable, but not worth the fight for one dependency |
| Version pinned in `.config/dotnet-tools.json` | Reproducible, in source control, bumpable by CI |
| LSP payload types are hand-defined | No maintained Microsoft package supplies them — see the README's Dependencies section. Adding a package back is a regression, not a cleanup |
| No off-the-shelf MCP↔LSP bridge | Third-party code in the hot path, and language-agnostic bridges know nothing about Roslyn's specific failure modes |

If `--autoLoadProjects` turns out to be insufficient for some repo shape, investigate the
non-standard `solution/open` notification (it exists in the server) rather than pulling in a
third-party wrapper.

## Verified facts, and when

Re-verify before relying on these; the server is a fast-moving prerelease train. All confirmed
against nuget.org and the shipped binary on 2026-09-02/03.

- No stable release exists. Every RID package lists a bare `5.11.0`, but it is **unlisted**, and
  the non-RID tool ID never had one. `--prerelease` is load-bearing. Never pick a version by
  scraping the flat-container index — it includes unlisted versions.
- The non-RID tool ID resolves per-platform through `RuntimeIdentifierPackages`, so CI needs no
  RID selection. Payload is ~300 MB.
- Server flags come from the bundled `Microsoft.CodeAnalysis.LanguageServer.exe --help`; the thin
  client itself has no `--help`. `--autoLoadProjects` takes an optional integer. `--daemon-mode`
  is the thin client's flag; the server's internal equivalent is `--daemon`.
- The server does **not** advertise `positionEncoding`, which per LSP 3.17 means utf-16.
- `workspace/projectInitializationComplete` is a real server→client notification and is the
  readiness signal.
- Roslyn's `containerName` is localised display text, not a namespace path.
