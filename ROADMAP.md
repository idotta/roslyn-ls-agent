# Roadmap

Work spans multiple sessions. This file is the handoff: what is done, what is next, and which
questions are already settled. `DESIGN.md` holds the why behind the settled ones.

Last updated: 2026-09-04, after the daemon became the default, the source-generator
staleness legs and the forced non-daemon fallback landed in `probes/run.sh`, and
`skill/SKILL.md` was written. Milestone 3 is done.

## Status

| Milestone | Scope | State |
|---|---|---|
| 1 | `ready` + `refs`, cross-project fixture, probe gate, both workflows | **done** |
| 2 | The hard fixture cases and the read commands | **done** |
| 3 | Daemon mode, then `skill/SKILL.md` | **done** |
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
      staleness itself, and it is *blocked*, not merely undone — see the Milestone 3 item.
      `csx` is one-shot: every invocation starts its own server and loads the workspace cold,
      so a probe case that mutates the fixture between two invocations exercises a cold load
      of changed sources and says nothing about whether a cached generated symbol is
      refreshed. Nothing can reach that path until a single server outlives an edit.
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
      `outline`, only differing documents are. `fixture/Core/Split.cs` carries both halves of
      that distinction — an overloaded `Left` in one document, and a `partial class Split`
      whose second half lives in `Split.More.cs` — so `outline-overloads-collapse` and
      `outline-ambiguous-fails` pin each direction. `Core/Empty.cs` declares nothing and pins
      the exit-0-on-no-symbols path.
      `outline-generated` targets `…BuildInfo.Stamp`, not the type: every case pinning
      `Program.Matches` is member-level, and what Roslyn puts in `containerName` for a *type*
      has never been verified, so a type-level target would have bet the case on an unknown.

Done: the first document opened only reports errors that need no loaded project (a missing
semicolon, say), and waiting on readiness does not help because the document is opened after it.
`LspClient.DiagnosticsAsync` re-pulls until two consecutive reports agree, with a 5 s budget;
`deliberate-error-diag` is the case that pins it, via the cross-project error above.

- [x] Expand `cases.jsonl` to cover `def` and `outline` — 29 cases now. `outline-truncates`
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

Wire behaviour was probed on 2026-09-04 before anything was designed around it; the daemon
entries under "Verified facts" are the output. Three findings moved this milestone: the daemon
is **shared**, not per-client; `LspClient` needs no protocol change at all; and source-generator
staleness is already reachable without any client work.

- [x] Switch `ServerArgs` to `--daemon-mode`. Do **not** pass `--clientProcessId`; it makes the
      server exit when the client does, which defeats the point.
- [x] **Decided: daemon on by default, `--no-daemon` to opt out.** ~3.2x on `refs` is the whole
      value for an agent, and the cold path stays reachable for anyone who needs it.
      `probes/run.sh` keeps its cold-load coverage by scoping itself with
      `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME=csx-probe-$$` plus a 60 s keepalive, so the suite
      gets a daemon of its own rather than inheriting whatever the developer's session left
      running — a stale daemon would otherwise let the gate lie. Accepted costs: `--log-level`
      against an already-running daemon is silently a no-op (the daemon takes its configuration
      from whoever launched it), and the silent non-daemon fallback is now the *default* path's
      failure mode, which is why `csx` reports it. `no-daemon-refs` is the one case that
      still runs a dedicated server, since the whole suite would otherwise stop covering that
      path — which is also the path a fallback takes.
- [x] Verify what a second concurrent client gets. It **shares** the first client's daemon; the
      earlier "own isolated server instance" wording here was a wrong guess, and sharing is the
      documented design. One daemon served two different `--root`s with no symbol leakage in
      either direction, order-independent; three simultaneous clients all exited 0.
- [x] Verify the daemon survives killing the first client's whole process tree. It does —
      `taskkill /T /F` on the `csx` chain left the daemon up and the next client reconnected to
      the same pid. Our own `shutdown` + `exit` does not kill it either.
- [x] `--daemonKeepAlive` / `ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE` confirmed: default 900 s
      after the last client disconnects, and the env var propagates by plain inheritance through
      the whole launch chain.
- [x] Re-measure latency warm and update the README table.
- [x] **Source-generator staleness**, carried over from Milestone 2. Needed neither `didChange`
      nor a `didChangeWatchedFiles` capability — the daemon runs its own file watcher. Three
      legs at the end of `probes/run.sh`, not `cases.jsonl` rows: a row is one invocation and
      cannot restore what it changed. Baseline present, rename `Greeter` on disk (the syntax
      provider the generator keys on) and assert the generated symbol goes away, restore and
      assert it comes back. Both directions were immediate and deterministic — the poll loop
      is insurance, not a measured need. Every leg gates on `--sentinel Cheer`, which the
      rename does not touch, so absence is never concluded from an unloaded workspace; the
      legs run in order because only restore-and-present proves the daemon is still live. A
      `trap ... EXIT` restores `fixture/Core/Greeter.cs` even on failure.
- [x] Assert on the silent non-daemon fallback. `csx` **detects** it — `LspClient` scans the
      thin client's stderr for `Falling back to non-daemon mode` /
      `Running language server in non-daemon fallback mode` and prints
      `csx: daemon unreachable; this run used its own cold server`, read after the command
      rather than right after connecting because the marker races the initialize response.
      `non-daemon-fallback-reported` at the end of `run.sh` **forces** one: the thin client
      falls back when it times out waiting for a mutex named `Global\<pipeName>.client`
      (~20 s), so `probes/hold-mutex.cs` holds it while one client starts. The case needs its
      own pipe name — the mutex only guards check-server-then-launch, so a client that finds a
      daemon already listening never contends for it — and asserts both exit 0 (a fallback run
      still answers, which is what makes it silent) and the warning (that `csx` noticed).
      **No new project was needed**, which was the reason this was briefly deferred: .NET 10
      runs a bare `.cs` file, and `dotnet run probes/hold-mutex.cs` compiles in under a second.
      Two traps in that holder, both of which look like the mechanism not working rather than
      like a mistake: the mutex must be created with `CurrentUserOnly = true` to match the
      server, and with `CurrentSessionOnly = false` or the `Global\` prefix is rejected. Either
      one wrong throws instead of contending, and so does passing the whole mutex name from
      the shell: `"Global\\${pipe}.client"` in a double-quoted bash string yields a literal
      `${pipe}`, so the holder guards a name nothing contends for and the case fails silently.
      `run.sh` passes only the pipe name and the holder builds the rest. **Unverified on Linux** — .NET implements named
      mutexes over files there, and both workflows run `ubuntu-latest`, so this is the second
      host-dependent case in the suite after the non-ASCII ones.
- [x] Write `skill/SKILL.md`.

Three client bugs surfaced while measuring, all fixed here:

- **`WaitReadyAsync` waited on `projectInitializationComplete` before polling the sentinel.**
  A client attaching to an already-loaded daemon never sees that notification — it fired
  before the process existed — so every warm run burned its entire timeout (300 s under
  `run.sh`) on a workspace that was ready before it connected. It now polls the sentinel from
  the start and keeps the notification only as diagnostic detail on the failure path. The
  docstring had already noticed the warm case; the code had not acted on it. `--timeout 0`
  still issues no query at all, so `premature-query-fails-loudly` still pins the timeout guard.
- **An initialize-time connection loss escaped as an unhandled `ConnectionLostException`** and
  discarded the server's stderr — the only thing that says why. It is now a `CsxException`
  carrying `StderrTail()`, after a bounded wait for the process to finish exiting so stderr is
  flushed. This is what turned the fallback experiment above from "connection lost" into the
  exact mutex name and file:line.
- **The sentinel resolving stopped implying every project was loaded.** Cold load closed that
  window by accident; warm attach reaches it in seconds. Roslyn binds a `ProjectReference` to
  the referenced project's built assembly until that project loads, so `def App/Program.cs:11:39`
  came back as a decompiled temp file under `MetadataAsSource` — exit 0, no context lines, no
  relation to the repo. It showed up as `def-non-ascii-json` failing once in a run where all 30
  other cases passed. `PathUri.IsDecompiled` recognises the shape and `LspClient.SettleAsync`
  re-asks for up to 10 s, on both `references` and `definition`. Residual risk: a `refs` answer
  that is merely *incomplete* in that window carries no decompiled URI to detect, so nothing
  catches it — the position form of `refs` and `def` never round-trips the symbol resolver,
  which is where `MatchSymbolsAsync` already has its own grace period.

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
- [x] Two concurrent clients work (sharing one daemon); daemon survives killing client 1's
      process tree
- [x] Warm command latency measured and recorded in the README
- [x] The daemon is the default path, with `--no-daemon` as the opt-out, and the probe suite
      still exercises a cold load
- [x] A source-generated symbol disappears when what the generator keys on is renamed on
      disk, and comes back when it is restored, against a daemon that outlives both queries
- [x] A run that silently fell back to a non-daemon server says so, and a probe forces one
- [x] `skill/SKILL.md` exists and tells an agent not to grep for what `csx` answers

## Verified facts, and when

Re-verify before relying on these; the server is a fast-moving prerelease train. All confirmed
against nuget.org and the shipped binary on 2026-09-02/03, and the daemon entries on 2026-09-04
against 5.12.0-1.26426.8 / win-x64.

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

### The daemon

Two sources beyond experiment, both shipped in the package and both worth re-reading before
trusting any of this: `roslyn-language-server.xml` next to the thin client documents the whole
daemon design (`DaemonBootstrap`, `DaemonPipeName`, `DaemonServerMutex`, `ChildServerHost`,
`ExitCodes`) including *why* the bootstrap exists, and the dll's UTF-16 string table carries the
flag and env-var names that appear in no `--help`.

- **The launch chain is four processes deep**, and the middle one is deliberate:

  ```
  csx
   └─ dotnet tool run
       └─ roslyn-language-server.exe --daemon-mode --stdio --autoLoadProjects --logLevel L
           └─ roslyn-language-server.exe --daemon-launch   (bootstrap, exits immediately)
               └─ Microsoft.CodeAnalysis.LanguageServer.exe --daemon --pipe <name> ...
  ```

  The thin client relaunches *itself* as a short-lived bootstrap purely so the daemon is
  orphaned rather than a descendant — the XML doc says process-tree teardowns walk parent/child
  links, which "neither Windows job-object breakaway nor Unix `setsid` change". The bootstrap
  waits on the server mutex, then exits. The thin client then relays our stdio to the daemon's
  named pipe.
- **`LspClient` needs no protocol change**: same `initialize`, same stdio
  `HeaderDelimitedMessageHandler`. Daemon mode is an argv change and nothing else.
- **One daemon is shared across workspaces, not one per client.** The pipe name is a hash of
  user identity plus the server exe's versioned path — *not* the workspace. `--daemon` on the
  server is documented as "run as a multi-client daemon".
- **The daemon takes its configuration from whoever launched it.** It inherits the *first*
  client's `--autoLoadProjects` and `--logLevel`, both visible in its cmdline; later clients only
  connect. So `csx --log-level Debug` is silently a no-op against an already-running daemon.
  Inferred from the observed cmdline, not separately tested.
- **`ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME=<literal>`** yields a fully isolated daemon under
  that exact name; a client without it starts a separate daemon alongside. This is the per-run
  scoping a probe suite wants. Keepalive defaults to 900 s after the last client disconnects
  (`-1` for indefinite) and `ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE` propagates by plain env
  inheritance down the whole chain.
- **There is a silent non-daemon fallback.** The dll carries "Falling back to non-daemon mode"
  and "Running language server in non-daemon fallback mode" (a daemon startup-mutex timeout, for
  one). A fallback run still answers correctly, just cold — nothing but latency or that stderr
  line distinguishes it, which is why `csx` watches for it. Forced deliberately by
  `non-daemon-fallback-reported`: the client mutex is named `Global\<pipeName>.client`, created
  with .NET 10's `NamedWaitHandleOptions { CurrentUserOnly = true }` — a same-named mutex
  without that option makes the thin client throw `WaitHandleCannotBeOpenedException` rather
  than contend, which is how the name was pinned down.
- **A root with only a `.csproj` and no solution never becomes ready**, daemon or not.
  `projectInitializationComplete` never fires and `workspace/symbol` stays empty for the full
  timeout; `--autoLoadProjects` does not discover a bare project. Adding a `.slnx` fixes it
  immediately. This belongs in `SKILL.md`.
- `premature-query-fails-loudly` still exits 1 against a warm daemon, because `--timeout 0` means
  `WaitReadyAsync` never issues a query at all. That case pins the timeout guard, not cold load.
