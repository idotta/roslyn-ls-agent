# Roadmap

Work spans multiple sessions. This file is the handoff: what is done, what is next, and which
questions are already settled. `DESIGN.md` holds the why behind the settled ones.

Last updated: 2026-09-03, after `csx def` and `csx outline` closed Milestone 2.

## Status

| Milestone | Scope | State |
|---|---|---|
| 1 | `ready` + `refs`, cross-project fixture, probe gate, both workflows | **done** |
| 2 | The hard fixture cases and the read commands | **done** |
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

- [x] A **source generator** producing a symbol that gets referenced from another project.
      `fixture/Gen` emits `Fixture.Core.Generated.BuildInfo.Stamp()` into Core via
      `RegisterSourceOutput` keyed on a syntax provider that looks for `Greeter`, so the
      generated symbol genuinely depends on the compilation — rename `Greeter` and the
      generated namespace disappears (CS0234). Deliberately *not*
      `RegisterPostInitializationOutput`: that emits before any compilation analysis and can
      never go stale, so a fixture built on it would pass without testing the path that can.
      `--sourceGeneratorExecutionPreference` was not needed at the default `Automatic`, and
      `workspace/_roslyn_refreshSourceGenerators` now has a stub handler. **Still untested:**
      staleness itself. No case edits a file mid-session, so nothing yet proves a stale
      generated symbol gets refreshed rather than served from cache — that needs either a
      long-lived daemon (Milestone 3) or a case that mutates the fixture and re-queries.
- [x] A file with **non-ASCII characters** on a line containing a symbol. Use an **astral-plane
      character (an emoji)**, not just an accented letter: positions are UTF-16 code units, which
      .NET string indices already are, so an accent passes even on a broken implementation. Only
      a surrogate pair catches code that counts runes or UTF-8 bytes.
      `fixture/Core/Party.cs` declares `Cheer` on an emoji-bearing line; `App/Program.cs:11`
      calls it with the emoji *before* the call, putting `Cheer` at UTF-16 column 39 (rune
      counting gives 38, UTF-8 bytes 41). No code in `csx` counts characters today —
      `LocateAsync` forwards the caller's column and `Output` renders the server's — so these
      cases pin the server staying UTF-16 behaviourally, `didOpen` text matching what Roslyn
      parses off disk, and insurance for `def` / `outline` later. **Known gap:** the position
      case catches a rune-counting error (column 38 lands on the `.` and resolves `Party`, so
      the case fails) but not a UTF-8 one (41 is still inside `Cheer`); the `'column': 39`
      assertion in the JSON case is what covers that direction.
- [x] One **deliberate type error** for `csx diag`. It must live in **App**, or a new
      project — never in Core. `probes/run.sh` compiles Core to arm its `CS9057` guard (the
      analyzer-vs-compiler version mismatch that otherwise degrades to a silently absent
      generated symbol), so an uncompilable Core would disarm that guard permanently.
      `fixture/App/TypeError.cs` holds a **cross-project** CS0029 (`int Wrong() =>
      Greeter.Farewell("x")`, a member Core exposes only for it, so the refs cases keep their
      pinned reference counts) rather than a self-contained one: binding it needs Core's reference
      resolved, so the misc-files state a freshly opened document is first bound against
      cannot report it. That is what makes the re-pull-after-load requirement testable —
      a first-response-only `diag` returns nothing and the case fails. Nothing builds App,
      so an uncompilable App costs the gate nothing.

Commands:

- [x] `csx def <file>:<line>:<col>` — also accepts `Namespace.Type.Member`. Renders through the
      same `Output.WriteLocationsAsync` as `refs`; empty exits 1. **Verified on the wire, since
      the plan turned on it:** `textDocument/definition` fired *at* a declaration returns that
      declaration (count 1), not empty, so the symbol form is safe and keeps its server round
      trip. The server also honours the absent `definition.linkSupport` and answers
      `Location[]`, not `LocationLink[]` — there is deliberately no two-shape reader, because a
      deserialization failure beats silently rendering half a response.
      `def-position-json` asserts `'count': 1` at a position where `refs` returns 2: without
      that one assertion every `def` case would also pass if `def` were secretly `refs`, since
      `run.sh` can only assert that a substring *appears*. `def-symbol` cannot be distinguished
      from printing `LocateAsync`'s own answer by any external assertion — the two are
      identical by construction — so it is regression coverage, not endpoint coverage.
- [x] `csx diag [path] [--errors-only]`. Calls `textDocument/diagnostic` optimistically; the
      dynamic `client/registerCapability` is still accepted and discarded. `workspace/diagnostic`
      was tried and dropped: the server answers it but returns **zero reports**, matching the
      `workspaceDiagnostics: false` in that registration, and the call is specified as a long
      poll, so attempting it only bought a timeout. With no argument, `diag` walks the `.cs`
      files under `--root` instead (`Program.SourceFiles`, shared with the sentinel inference).
      Exit code is 0 whenever the query was answered — a clean file is a successful `diag`.
- [x] `csx outline <file | symbol>` via `textDocument/documentSymbol`. Hierarchical: the client
      declares `hierarchicalDocumentSymbolSupport`, and that capability's property name has to
      serialise to `textDocument.documentSymbol` or the server quietly falls back to the flat
      `SymbolInformation[]` form. Output is the one documented exception to DESIGN.md's output
      rules — see the note there. Exits 0 for a document with no symbols (an answered query,
      like `diag`); a target that fails to resolve exits 1 from the resolver.
      Targeting: a file path, a `file:line:col` spec, or a symbol whose declaring document is
      outlined. Anything file-shaped (containing a separator or ending `.cs`) resolves as a
      file and never falls through to the symbol resolver, which would otherwise answer a
      mistyped path with "no symbol matched 'Core/Missing.cs'" and a candidate dump. Overloads
      are collapsed by document first: several matches in one file are not ambiguity for
      `outline`, only differing documents are. No fixture case covers that — `Greeter` has no
      overloads — but it is the common real-world shape.
      `outline-generated` targets `…BuildInfo.Stamp`, not the type: every case pinning
      `Program.Matches` is member-level, and what Roslyn puts in `containerName` for a *type*
      has never been verified, so a type-level target would have bet the case on an unknown.

Done: the first document opened only reports errors that need no loaded project (a missing
semicolon, say), and waiting on readiness does not help because the document is opened after it.
`LspClient.DiagnosticsAsync` re-pulls until two consecutive reports agree, with a 5 s budget;
`deliberate-error-diag` is the case that pins it, via the cross-project error above.

- [x] Expand `cases.jsonl` to cover `def` and `outline` — 26 cases now. `outline-truncates`
      pins `--max`, the only genuinely new capping logic in this milestone;
      `def-no-definition-fails` pins the empty-result exit 1, which is the one failure path
      `def` actually added (a symbol that does not exist was already pinned by
      `unknown-symbol-fails`). Note that `|` is both the `expect` separator and `outline`'s
      gutter, so no case can quote a rendered outline row — see the header in `run.sh`.
- [x] Expand `cases.jsonl` to cover the remaining fixture case (the deliberate type error).
      `deliberate-error-diag`, `-diag-json` and `-diag-workspace`; the last one covers the
      no-argument file walk, which no path-taking case would reach. The
      symbol and position forms are kept separate on purpose — `LocateAsync`'s position branch
      never touches the server, so a single case would pass with `workspace/symbol` coverage of
      generated symbols entirely broken.

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
- [x] `csx refs` on a source-generated symbol resolves
- [x] Column positions correct on the non-ASCII fixture line
- [x] `csx diag` finds the deliberate error and does *not* report it before load completes
- [x] `csx def` resolves from a use, a symbol and a source-generated symbol
- [x] `csx outline` renders a nested document outline, including a generated document
- [x] Probe suite fails loudly when the server returns empty due to premature querying
- [x] `bump.yml` opens a PR that is gated (see the `probes` commit status caveat in the README)
- [ ] Two concurrent clients work; daemon survives killing client 1's process tree
- [ ] Warm command latency measured and recorded in the README

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
- Pull diagnostics: `textDocument/diagnostic` answers unadvertised, returning
  `kind: "full"` reports. `workspace/diagnostic` also answers but returns zero reports —
  `workspaceDiagnostics: false` in its dynamic registration is honest. Roslyn leaves
  `source` null on compiler diagnostics and sends `code` as a string (`"CS0029"`).
- `textDocument/definition` answers `Location[]` when the client omits
  `definition.linkSupport`, and returns the declaration itself when fired at a declaration
  rather than falling through to implementations. `textDocument/documentSymbol` returns the
  nested `DocumentSymbol[]` form when `hierarchicalDocumentSymbolSupport` is declared, with
  the namespace as the root node and `detail` duplicating `name`.
- Source-generated documents use the `roslyn-source-generated:` scheme. `workspace/symbol`,
  `textDocument/definition`, `textDocument/references` and `textDocument/documentSymbol` all
  cover them. `workspace/textDocumentContent` (LSP 3.18) returns the text and needs no declared
  client capability; `sourceGeneratedDocument/_roslyn_getText` is gone. The URI's authority guid
  and `documentId` are regenerated on every workspace load.
- `DOTNET_CLI_UI_LANGUAGE=en` pins Roslyn's own display strings, but StreamJsonRpc's error text
  still came back localised (Portuguese on this machine). Do not assert on transport error text.
