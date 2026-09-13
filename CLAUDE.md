# cslq

A CLI (`cslq`) that gives coding agents semantic C# queries over Microsoft's official
`roslyn-language-server`, with a cron-driven update loop gated by a probe suite.

**Read `ROADMAP.md` first** for milestone state and acceptance criteria, and `DESIGN.md` for the
output rules every command follows and the decisions that are already settled. The README holds
the user-facing detail and the evidence behind the dependency choices.

## Commands

```
dotnet build src/Cslq/Cslq.csproj          # build
dotnet test --project tests/Cslq.Tests    # the unit tests alone, ~1s (never with --nologo)
./probes/run.sh                          # the gate: unit tests, restore, build, ready, every case
./src/Cslq/bin/Debug/net10.0/cslq refs Greet --root fixture
```

`probes/run.sh` is the gate, and it runs `tests/Cslq.Tests` first. Run it before claiming
anything works: the unit tests alone prove nothing about the server's behaviour.

## Things that will cost you a session if you rediscover them

- **Positions are UTF-16 code units** and the server does not negotiate otherwise. .NET string
  indices are already UTF-16, so naive indexing is correct — counting runes or UTF-8 bytes is the
  bug. `LspClient.InitializeAsync` asserts the encoding and refuses to run if it ever changes.
- **Do not add `Console.OutputEncoding = UTF8`, and do not believe redirected stdout is UTF-8
  on its own.** The rule stands and the reason is unchanged: its setter calls
  `SetConsoleOutputCP`, which every process sharing the console sees, so one `cslq` would be
  changing the code page under the shell and everything else attached to it. A real console
  handle is written with `WriteConsoleW` and ignores the encoding entirely, so only the
  *redirected* case is ever affected — which is every agent, every pipeline and the whole probe
  suite. What this bullet used to claim about that case was an accident: redirected output goes
  through `Console.OutputEncoding`, this machine's console is code page 850, and a code page
  carries no emoji, ellipsis or accent — they best-fit to `??` and `.` and the answer is wrong
  at exit 0. It never showed because the language server `cslq` launched attached to the same
  console and set its output code page to UTF-8 *before* `Console.Out` was built lazily on
  first use. A call answered by a session launches nothing, which is how sessions-by-default
  turned `non-ascii-refs-symbol` and `a-long-line-is-elided-around-the-hit-column` red.
  `Program.WriteUtf8WhenRedirected` is the fix and the place the reasoning lives: writers of
  our own over the standard handles, redirected streams only, no global code page touched.
  Mojibake in a PowerShell pipeline (`cslq refs ... | Select-String`) is still PowerShell
  decoding our bytes with its own `[Console]::OutputEncoding`, which nothing `cslq` sets can
  change.
- **The non-ASCII probe cases are the first host-dependent ones.** They no longer go green on
  CI and red in Git Bash: since Milestone 5 item 5 `probe.yml` is a `fail-fast: false` matrix
  over `ubuntu-latest`, `windows-latest` **and** `macos-latest`, where the job runs
  `./probes/run.sh` under the runner's `bash` — which on Windows is Git Bash, the shell that
  raised the encoding question in the first place. `bump.yml` and `release.yml` stay
  `ubuntu-latest` alone; they gate a publish, and the platform coverage lives on every PR.
  The desync this bullet used to warn about is now an error rather than a silence — see
  the next bullet.
- **Every read of a source file goes through `SourceText`, and its decoder throws.**
  `File.ReadAllTextAsync` substitutes U+FFFD for invalid bytes and says nothing, which desyncs
  the `didOpen` text from what Roslyn parses off disk: measured on CP1252 bytes `E9 A0`, one
  U+FFFD replaced two characters, every column after it was one short, and `def` at the
  position `cslq` itself printed answered `no results` — a wrong answer at exit 0. The reader
  is a `StreamReader` over `new UTF8Encoding(false, throwOnInvalidBytes: true)` with
  `detectEncodingFromByteOrderMarks: true`, and that second argument is load-bearing: read the
  bytes as UTF-8 and nothing else and every BOM'd file in a repository breaks instead —
  `utf8-bom-is-still-read` and `utf16-bom-is-still-read` are the guards, and
  `SourceTextTests` covers UTF-16 BE as well. The two callers answer the failure differently
  and deliberately: a document named as the target is an exit-1 failure, while `diag`'s
  *walk* names it on stderr and carries on, because failing a whole-tree walk over one
  undecodable file hides every real diagnostic behind it. `DiagFiles` returns that
  `Walked` flag for exactly this. The line number in the message comes from a second, lenient
  decode — `DecoderFallbackException.Index` is an offset into whatever buffer the reader
  handed the fallback, not into the file — and that second read only ever happens on a file
  that is already failing. `fixture/encoding/` is the fixture: three files no project
  compiles, one of them genuinely invalid, pinned byte-for-byte by a `-text` line in
  `.gitattributes` so no checkout can normalise the thing under test.
- **Server flags live only in `src/Cslq/ServerArgs.cs`.** The thin client forwards options it does
  not recognise straight through to the server, so a renamed flag produces no error at all. The
  probes are the only thing that catches it.
- **Roslyn's `containerName` is localised display text** (`in Greeter (project Core (net10.0))`),
  not a namespace path, and nothing may match against it.
  `DOTNET_CLI_UI_LANGUAGE=en` is pinned on the server so it does not vary by machine locale.
- **`hover` cannot verify a dotted target, and the reason is invisible on a type.** Its first
  line is fully qualified for types only — `class Fixture.Core.Greeter` — while a member prints
  the minimal form, `string Greeter.Greet(string name)` and `int Volume.Litres { get; }`.
  Measured on the fixture 2026-09-09. So the obvious cheap check (one `hover` per candidate,
  compare its first line) passes every type test you would write and silently cannot see a
  member's namespace, which is the case that was wrong. `Targets` reads the chain off
  `textDocument/documentSymbol` instead, where the namespace node's name is already dotted.
- **Never gate readiness on the symbol being queried.** An absent symbol then looks identical to
  a workspace that has not loaded, and the caller waits out the whole timeout for a typo.
- **A query fired before load returns empty, not an error.** Never `sleep`; poll a sentinel
  that must resolve, from the first round. Once `workspace/projectInitializationComplete` has
  fired **in this process**, the wait is bounded to a further 20 s (`LspClient.PostLoadGrace`)
  and then fails — a project whose only type sits in an `#if false` branch is the shape it
  exists for, ordinary `.csproj` and all, so no skip rule sees it. That notification ends
  *this client's own* reload on every attach, cold and warm alike (see the daemon bullet), so
  the bound is meaningful on both. It is not "everything has loaded": measured 2026-09-10,
  projects kept resolving for 6-8 s after it on CommunityToolkit, which is the length the
  grace has to cover and the reason it is 20 s rather than zero. **Never bound it when the
  notification has not fired**: that state means the load this client asked for has not
  finished, and the incomplete-answer window is inside it — and on a repository where the
  notification never fires at all (see the daemon bullet) the grace never applies, so an
  unresolvable candidate there holds the whole `--timeout` and the failure text stays on the
  still-loading wording. `exhausted-candidate-fails-after-load` is the leg.
- **A sentinel that resolved is proved for the life of the attach, and the proof is a set of
  per-project keys rather than a ready flag.** `LspClient._proved` holds one key per sentinel
  that has answered — `LspClient.ProofKey`, the project's own directory, or the candidates for
  the probe an explicit `--sentinel` adds, since that one's directory is the root — and
  `LspClient.Unproved` is what the round is narrowed to. It has to be a set: `Program.Sentinels`
  recomputes the list from disk on every request, so a project added after the session started
  appears in a later call and must still be probed, which is exactly what a flag would skip at
  exit 0. Nothing negative is ever cached; a sentinel that has not resolved is re-asked every
  call with the same deadline and grace, and the whole failure path is untouched. Without it a
  warm session re-proved every project on every request, which *was* the request: measured
  2026-09-13 on OrchardCore (214 projects), a warm `outline` of one file spent 7.6-18.6 s of its
  7.7-19.5 s inside `WaitReadyAsync` and went to ~60 ms total with the proof kept; on
  CleanArchitecture (12 projects) it saves the ~7 ms it costs there. The 214-project number is
  the only one that matters and the fixture cannot show it — four projects put the saving inside
  the noise — so **no probe case guards this**; `ReadyProofTests` pins the predicate instead,
  and a timing leg here would be a coin flip that passes on the breakage it exists to catch.
- **The readiness failure text is assembled in `Readiness.Message`, one item per line, and
  what it says about the notification is a sentence rather than its name.** On a 26-project
  repository the old single line ran to 1,050 characters. Two wordings are load-bearing.
  *Not fired* is not a fault and must never be reported as `never fired`: it is the ordinary
  state of every attach until the reload ends, so the line says the workspace is still
  loading and names the lever (`Raise --timeout`) — and one sentinel round is always issued
  before that conclusion, which `--timeout 0` proves in ~0.5 s warm. *Fired with every probed
  project empty* is the one state that earns a `dotnet` launch: it is the signature of a
  design-time build that failed, and `Diagnosis.Cause` runs `dotnet --version` with the
  working directory set to `--root` (a `global.json` pinning an absent SDK exits 155 there)
  and then checks `obj/project.assets.json` per project. **On the failure path only** — the
  run has already spent its whole timeout, whereas a pre-flight would cost a process start on
  every invocation, the same reason the prune is kept off the start path. Do not print that
  exit code: Windows reports the 155 a shell shows as `-2147450725`. Measured 2026-09-10 on a
  staged root: 23 s and the cause named, against the 181.7 s and no cause testers measured.
  `failed-design-time-build-names-the-sdk` is the leg, and it stages the tree itself because
  the repro needs an SDK nobody has installed.
- **A `%XX` in the root path is MSBuild's problem, not the URI layer's.** A root like
  `.../pct%20x` never becomes ready, and the obvious suspect is wrong: `new Uri(path)` escapes
  the literal `%` to `%25`, so `PathUri.FromPath` / `ToPath` round-trip it exactly (pinned by
  `PathUriTests`). What fails is MSBuild, which unescapes `%XX` in the paths it reads — plain
  `dotnet restore` on such a tree already errors with `pct x` — so nothing `cslq` does can fix
  it. A `%` not followed by two hex digits is fine end to end: `cslq ready` on `pct%zzx`
  resolves in ~4 s.
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
- **`PathUri.Display`'s branch order is load-bearing in one more place, and `<external>/` is
  the newest trap.** An ordinary file outside `--root` — the `<Compile Include="../..">`
  shape, which Roslyn indexes fully — now renders `<external>/<path relative to the root>`
  instead of the machine-absolute path it used to print at exit 0. But a *decompiled*
  document is also a real file outside every ordinary root, so `IsDecompiled` has to be
  asked **before** `IsExternal` or a framework `def` gets the `MetadataAsSource` temp path
  inside an `<external>/` label — the same ordering bug as the one below, one branch further
  on. `fixture-linked/Outside.cs` is the fixture: it is linked into `fixture/Core` from
  outside `fixture/`, and `Cslq.slnx` compiles nothing there, so `--root .` never sees it.
  Never assert a machine-absolute path in a probe row; assert the label.
- **A delegate has no kind either request can report, and `sym`'s `function` was the
  misleading half.** Measured 2026-09-10 on `fixture/Core/Kinds.cs`: `workspace/symbol`
  answers a delegate `function` (12) and a *local function* `method` (6), while
  `documentSymbol` answers both `method` and carries nothing else to separate them — the
  `name` and `detail` of a delegate are a method's (`Inner(int) : int`), and one nested in a
  class is a sibling of that class's methods. So `Kinds.Normalise` folds `function` into
  `method` and both commands say `method`. Do not "restore" the distinction in `sym`: it
  cannot be matched in `outline`, and a kind that depends on which command you asked is what
  the bug was. A constructor is the opposite and *is* recoverable — `Kinds.Of` reads it off the
  parent in an outline, and `Program.ConstructorsAsync` asks for a declaration chain in
  `sym`, but only for a document holding a method-kind hit that shares a name with a
  type-kind hit, so a broad query costs nothing.
  **Constructor inference needs the parent's *kind*, not just its name.** An outline row's
  parent is whatever node encloses it, and a namespace is one of them, so matching on the
  name alone rendered the delegate in `namespace Widget { delegate void Widget(int n); }` as
  `constructor` (staged root, 2026-09-10). `Kinds.Constructible` is the gate: class, struct
  and interface — the last for its static constructor, the one member C# lets repeat its
  declaring type's name — and nothing else.
- **A decompiled metadata location is a *file* URI, so every path helper answers it happily
  with a machine-absolute temp path.** `<temp>/MetadataAsSource/<guid>/DecompilationMetadataAsSourceFileProvider/<guid>/Console.cs`
  is a real file that really exists, which is why this is worse than the generated-URI trap:
  nothing throws, the context lines render correctly off disk, and the answer is a path no other
  machine has. `PathUri.Display` therefore checks `IsDecompiled` **before** the file branch, not
  after — put the checks the other way round and `Relative` hands the caller the temp path. The
  assembly is not in the URI at all: it comes off the `#region Assembly <name>, Version=...`
  header Roslyn writes at the top of the document, behind a byte-order mark, so the
  `<metadata>/<assembly>/<TypeName>.cs` label needs a document read the way a generated
  document's label needs a request. Only the file name is stable — both guids are per server
  instance — so **never assert on the raw path, and never assert the line and column of a
  framework declaration**: those move with the reference assembly.
- **`PathUri.AnyUnder` has to exclude decompiled and generated URIs explicitly, and the
  decompiled one is the trap.** It is the discriminator that keeps `SettleAsync`'s guard: a
  decompiled answer is stale only if the workspace also declares that type. But a decompiled URI
  is a path under the *temp directory*, so a plain "is this under the root" prefix test would
  call every framework answer stale for any workspace living under temp — which is every
  workspace the unit tests build. `PathUriTests` pins it.
- **The framework `def` cannot be pinned by a `cases.jsonl` row, because both its failure modes
  are invisible to a substring.** What went wrong was an absence (the temp path must *not*
  appear) and a duration (the guard burned its whole 10 s budget and then returned the answer it
  already had). `expect` can only require a substring, and the right message passes just as well
  after twelve seconds — so `framework-def-is-labelled-and-does-not-stall` is a scripted leg
  with an elapsed check, like `no-solution-root-fails-fast`. It is warm by construction: the
  `def-framework-member` row above it is what makes Roslyn write the decompiled document, which
  cold costs about six seconds on its own. Keep it after the rows.
- **`textDocument/hover`'s output shape is chosen by the client capability, and plaintext is
  what makes it printable.** With `contentFormat: ["markdown"]` — or the node missing, which is
  what the deprecated `MarkedString` forms are for — Roslyn answers fenced code blocks and
  `&nbsp;` runs that an agent then has to undo. `HoverCapabilities` declares
  `["plaintext"]` alone, and the answer is then the signature on line one and the doc summary
  after it. It carries the parameter types, so `signatureHelp` is deliberately not wired.
- **A generated document's URI names the generator, never the project consuming it.**
  `assemblyName`, `typeName` and `assemblyPath` are all the generator's; the only thing
  separating two projects' copies of one generated document is the authority guid, which is
  regenerated on every workspace load. `textDocument/_vs_getProjectContexts` is what answers
  it — a VS protocol extension, not LSP, which the server neither advertises nor gates on a
  client capability. Its `_vs_id` is `<projectId guid>|<absolute .csproj> ($<tfm>)`: read the
  path half only. `_vs_label` (`"Core (net10.0)"`) is display text, same class of thing as
  `containerName`, and is not parsed. The label is
  `<generated>/<project dir>/<assembly>/<generator full type name>/<hintName>` — the generator
  type is the URI's `typeName`, and it is in the label always because two generators in one
  assembly may emit the same `hintName` — and every rendering path has to go
  through it — the two ambiguity listings in `Program` did not, and printed the same string
  twice under "pick one".
- **A positional request on a multi-targeted document is answered in one context, and
  `_vs_defaultIndex` is not the one to blame.** Measured 2026-09-10 on `fixture2/Multi`
  (`net10.0;net9.0`): `_vs_defaultIndex` was `0` in 6 of 6 runs while the *order* of the
  `_vs_getProjectContexts` array varied per attach, and the unqualified answer followed
  `contexts[0]` in 6 of 6. So `hover Only9` answered 4 times in 8 and `project` alternated
  `net10.0`/`net9.0` — one bug, not two. The fix is `_vs_projectContext` in the
  `TextDocumentIdentifier` (36 of 36 forced right, 12 of 12 forced wrong answered empty), and
  it comes with three traps:
  - **The `_vs_id` must go back verbatim and in the same attach.** Roslyn matches on it alone,
    and the projectId guid inside it is regenerated on every attach, so a dump-then-use across
    two processes sends a stale id. `LspClient.ContextsAsync` caches per document per attach
    for exactly that reason.
  - **A fixed context alone is a worse bug than the coin flip.** A type inside `#if NET9_0`
    does not exist in the `net10.0` context, so pinning the first context turns 4 misses in 8
    into 8 in 8. `AskEachAsync` asks the contexts in `Contexts.Order`'s order and stops at the
    first that answers; deleting the retry to "make it deterministic" is the regression this
    bullet exists to prevent.
  - **A server that stops honouring the field fails silently.** An unrecognised member of a
    request payload is ignored — unlike `_vs_getProjectContexts`, which answers or fails — so
    the symptom is the old intermittency returning, an answer that is right most of the time.
    `tfm-excludes-the-other-branch` is the only guard: it asserts `hover Only10 --tfm net9.0`
    finds **nothing**, which can only hold if the context was honoured. It is an absence, so
    do not "fix" it into an assertion about output.
  `refs`, `impl`, `diag` and `outline` answer with a union instead — see the next bullet.
  `fixture2/Multi` is the fixture: `Both` in both contexts, `Only10`/`Only9`
  one branch each, and a CS0029 in `TfmError.cs` that exists only in `net9.0`. Nothing builds
  it, so `dotnet build fixture2/Fixture2.slnx` fails by design — `run.sh` builds `Alpha` and
  `Beta` alone, and readiness is unaffected because `workspace/symbol` is context-independent
  (it listed both conditional declarations in 8 of 8 runs).
- **A set-valued answer must ask every context, and the merge has one trap that is invisible
  until a dotted target fails.** `hover` and `def` answer with one thing and stop at the first
  context that answers; `refs`, `impl`, `outline` and `diag` answer with a *set*, so they ask
  all of them (or the `--tfm` subset) and union — a reference inside an `#if NET9_0` consumer
  exists only in that context, and stopping early drops it at exit 0. Three things to keep:
  - **`Outline.Merge` keys on name, kind and identifier position but must take the *widest*
    range.** Every context parses the same text, so those three agree — but a declaration's
    `range` does not: on `fixture2/Multi/Conditional.cs` the namespace ends at line 6 in
    `net10.0` and line 13 in `net9.0`, each context seeing only its own branch. Keeping the
    first view's range put `Only9` outside its own merged parent, and `Targets.Chain` walks
    down by full-range containment, so `def Fixture2.Multi.Only9` went from failing 2 runs in 6
    to failing **4 of 4** — a "fix" that made it deterministically wrong. `Outline.Widen` is
    the union of the extents; `OutlineMergeTests` pins it.
  - **The dotted-target chain reads the union and ignores `--tfm`.** `SelectAsync` used one
    context's `documentSymbol` tree, which is why `def Fixture2.Multi.Only9` missed while the
    bare `def Only9` was already deterministic after batch 7 round 2. Constraining the *lookup*
    by `--tfm` would hand that bug back.
  - **The folds are what keep the union from duplicating.** `refs`/`impl` rely on
    `Output.WriteLocationsAsync`'s existing fold on rendered label plus range, before `--max` —
    the same fold that closed the per-framework duplicate rows — and `Program.Reports` folds diagnostics on position,
    severity, code **and message**, the message included because serilog answers
    `Substring can be simplified` in one context and `Slice can be simplified` in another at
    one position, and those are two findings rather than one.
  A set answer's footer says `merged from N contexts: ...` and only an *empty* one says
  `tried all N contexts: ...`: "tried" printed above a correct answer reads as a failure the
  caller has to rule out, and that cost is paid on every successful call. Its `--json` envelope
  carries `contexts` and **no `tfm`** — an envelope key that could only ever be null is omitted
  rather than emitted as null, which is the rule the always-null `source` is answered by and is written in
  DESIGN.md beside the other envelope rules. Row-level nulls stay: a row's null `tfm` means
  "in every context asked", which is a value rather than a missing one.
  The cost was measured before the rule was adopted, not after: a whole-tree `diag` walk on
  `fixture2` (6 documents, 3 two-context) went 2694-2956 ms to 2827-3030 ms warm, and on
  `fixture` (4 single-context documents) 2690-2925 ms to 2534-2732 ms, i.e. nothing. A
  single-context document pays only the one `_vs_getProjectContexts` request every
  context-bound command already makes.
- **`fixture2/` is the two-consumers-of-one-generator shape, and it cannot live in
  `fixture/`.** `App` references `Core`, so a second copy of the generated type collides at
  the use site with CS0433. `fixture2/Alpha` and `fixture2/Beta` reference nothing of each
  other's, so both compilations hold `Fixture2.Generated.Stamp` happily. `run.sh` restores and
  builds both consumers for the same reason it builds `fixture/Core`: an unbuilt analyzer
  contributes nothing, silently. It is excluded from `Cslq.slnx`, so `--root .` never loads it.
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
- **A `.cs` file no project compiles is half-invisible, and `diag` skips it on its project
  *context*, never on its directory.** `workspace/symbol` does not index it, but `outline`
  answers off the syntax tree, so `cslq outline` on such a file works while `cslq sym` on the
  type it declares exits 1. The other half of this bullet used to say `textDocument/diagnostic`
  reports **nothing** for it (measured 2026-09-05) and that was a timing artefact of a process
  that exited before the document bound. Under a session it does report: Roslyn answers an
  unqualified pull out of its misc-files workspace — analyzer rows about a file no compilation
  contains — so a whole-fixture `diag` drifted upward on later calls and the same command
  answered differently on the fifth call than on the first. `Program.DiagAsync` therefore skips
  a document whose `_vs_getProjectContexts` answer is **empty**. That is not the directory
  scoping this bullet has always rejected, and the linked file is exactly why it cannot be: the
  same file pulled in with `<Compile Include="../Elsewhere/File.cs" />` has a real context, so
  it is still walked and its real errors are still reported — which is what a directory rule
  would silently drop. `non-project-file-no-diagnostics` is the leg; the reasoning is in
  `DESIGN.md`.
- **Sentinel inference reads prose, and neither taking every match nor capping at three saves
  it.** The candidate regex matches `class|struct|record|interface|enum` followed by a word, so
  the doc comment "identifying the class and assembly context" yields the candidate `and` — and
  one sentence can yield `and` / `of` / `for` and fill all three slots, leaving a project probed
  only by words nothing can resolve while readiness burns its entire timeout. `cslq ready` on
  OrchardCore failed this way after 900 s on fifteen projects. `Program.NonCode` therefore
  strips comments and string literals before the declaration regex runs. Keep the fallback
  chain anyway: the regex still reads types out of `#if` branches and uncompiled files. The
  fixture cannot reproduce the original failure — it needs prose in a doc comment above the
  only declaration in a single-file project.
- **The nested-project scoping is `Sentinel.Accepts`, and a probe case cannot pin it.**
  A fixture project nested under another only goes red without the scoping when the nested one
  happens to load *first* — a load-order race, so the case would pass on a broken build most
  runs. It is a pure predicate over a URI instead, pinned by `SentinelScopingTests`. Both
  fixtures are flat, so every `Nested` list is empty in every probe case.
- **A candidate must resolve inside the project's *own* directory, nested projects excluded.**
  `LspClient.Under` counts any hit below a directory, so with `Web/` and `Web/Tests/` both
  declaring `Program` — the ordinary shape — Tests loading marks Web ready and the
  incomplete-answer-at-exit-0 bug is back. Both halves are needed: `Program.Candidates` skips
  files under a nested project, and `Sentinel.Nested` scopes the hit. Two `.csproj` in **one**
  directory cannot be separated by any path scoping and are a documented limit, not a bug to
  fix here.
- **`diag` pulls once, and the settle loop that used to wrap it is gone on evidence.**
  `textDocument/diagnostic` does not answer from the misc-files state and then correct itself —
  it **blocks until the document is bound**. A cross-project error opened as the first document
  in a never-used daemon returns the right code on pull #1 (~4.2 s), and the second pull (~0.7 s)
  never once differed across six whole-fixture runs, cold and warm. The leg that pins this names
  one file: a whole-fixture `diag` opens three other documents before `App/TypeError.cs`, so it
  never observes a first document at all. If a bump starts answering
  early, `diag` is where it shows up. Do not restore the loop without re-measuring: the old one
  could not have caught that case anyway, since two equally-wrong pulls agree.
- **A one-shot `cslq` still sends no `didClose`, so daemon document state outlives it.**
  `_open` is per-process and says nothing about what the shared daemon still has open. Any
  measurement of first-open behaviour must use a fresh
  `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME`, a fresh `CSLQ_SESSION_PIPE_NAME`, or
  `--no-daemon --no-session`; a warm daemon shows "no divergence" for the wrong reason. The
  same effect is worth ~4 s per document: the first `diag` of a file in a fresh daemon cost
  7.6 s against 3.3 s warm.
- **A session is the one thing that does send `didClose`, and it is not an optimisation —
  it is what keeps a stale answer from surviving an edit.** A process that died with its
  answer re-read every file on the next run; a session holds `didOpen` across the edit, and
  Roslyn owns an open document's text rather than re-reading the file. So `LspClient` stamps
  every document it opens (`Staleness.Stamp`: `LastWriteTimeUtc` and `Length` — a `stat`, not
  a hash, because it is taken before *every* use and hashing would cost the reads the session
  exists to avoid), re-opens with fresh text when the file changed, and sends `didClose` when
  the file is **gone**. Without it the session answers from the text as it first read it:
  wrong line numbers, wrong context lines, a renamed symbol still found, all at exit 0. A
  generated or decompiled URI has no file to stamp and is never stat'ed — `Staleness.HasFile`
  is the gate, and it matters in both directions: a generated document would read as *deleted*
  (`PathUri.ToPath` answers a confident `/BuildInfo.g.cs` for one) and a decompiled one would
  cost a stat per request for an answer no edit can change.
- **`cases.jsonl` order is load-bearing for the `diag` cases, invisibly.** `run.sh` scopes one
  daemon for the whole suite and derives one session name per root, and the client that holds
  the open documents is now the session rather than the process the row launched — so the
  effect is the same and one level further away. Cases 1-12 never open `App/TypeError.cs`. So
  `deliberate-error-diag` is the first `didOpen` of that document and the only leg that observes
  a cold document at all; by the time `deliberate-error-diag-workspace` and
  `non-project-file-no-diagnostics` run it is already open and warm. Reordering the file, or
  running one case against an ambient daemon, disarms that coverage with nothing going red.
- **`LspClient.StartProcess` clears `HANDLE_FLAG_INHERIT` on our own std handles before every
  `dotnet` launch, and that is what makes captured output safe.** Windows `CreateProcess` is
  called with `bInheritHandles=TRUE`, so cslq's stdout reached the thin client and, through it,
  the *daemon* — which outlives the call. A capturing harness then waited for EOF on a pipe the
  daemon still held: the launching call blocked for the whole keepalive and returned with the
  daemon dead, so every call was a launching call. Measured on the fixture, keepalive 20 s:
  `out=$(cslq ready --root fixture)` 25 s before, 4 s after. Best effort by design — a process
  with no console has invalid std handles and must not be broken by this — so a regression is
  silent, and `captured-stdout-does-not-stall` (`probes/stdout-capture.cs`) is
  the only thing that catches it. **The trap survives one level up:** launch `cslq` from a
  process whose own stdout is an inheritable pipe — `dotnet run probes/stdout-capture.cs`
  under `$(...)` — and *that* pipe is inherited into cslq as an ordinary handle and travels on
  into the daemon. Redirect an intermediary to a file and `cat` it; never capture it. The
  instance that reaches users is a **PowerShell-hosted harness** (Claude Code on Windows,
  Actions `shell: pwsh`): keepalive 10, bash `out=$(cslq ready)` 4 s against bash
  `out=$(pwsh -c '$x = & cslq ready; $x')` 18 s. So the launch must be an unredirected
  `cslq ready`, or `--no-daemon`, and SKILL.md carries that qualifier because it ships alone.
  **There is one more process in the chain now, and the handle clearing is only half of the
  answer.** The session is spawned the same way and outlives the call the same way, so it holds
  an inherited pipe for its whole keepalive just as the daemon does. Both go through
  `LspClient.StartProcess`, so the clearing covers both — **on Windows**.
  `DisableStdioInheritance` is a Win32 call that returns immediately off it, where a child
  inherits fds 0/1/2 whole unless the parent redirects them, and `Session.Spawn` did not: that
  alone was the ~62 s Unix penalty, and redirecting the child's streams is the fix. The leg is
  `captured-stdout-does-not-stall`, it runs on all three platforms since `67399d3`, and it
  names a `CSLQ_SESSION_PIPE_NAME` of its own so it measures the launch it means rather than a
  session some earlier case left warm.
- **Roslyn's daemon binds no named pipe off Windows, so a connect is not a liveness test
  there.** `probes/stdout-capture.cs` asserts that what outlives a captured call is still
  serving, and its Windows answer — connect to `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME` — can
  never be answered on Unix. Measured 2026-09-11 on ubuntu and macos: the socket path
  `$TMPDIR/CoreFxPipe_<name>` was polled every 25 ms *through* the call and never appeared, with
  and without a session in the chain, while `ss -xl` taken at the same moment listed only
  CoreFxPipe sockets belonging to cslq's own sessions — not one for a daemon, including the
  gate's own long-lived daemon that `ps` showed alive and serving the suite. The name is a key
  the daemon chain passes in its environment, not a socket it binds. So the liveness half is
  per-platform: a connect on Windows, and off it a live process whose environment carries our
  `NAME=value` token (`/proc/<pid>/environ` on Linux, `ps axeww` on macos). Do not "unify" it
  back into a connect, and do not read a timeout there as a dead daemon. The *timing* half —
  a captured call must not cost a keepalive — stays on all three platforms and is what the leg
  is for.
- **Nothing in the suite covers Ctrl+C, and MSYS `kill -INT` does not test it.** From Git Bash
  it terminates the process without ever raising a console control event, so the handler never
  runs and the 130 you see is bash's own signal status. To exercise the real path, launch `cslq`
  with `CREATE_NEW_PROCESS_GROUP` and send it `CTRL_BREAK_EVENT` with
  `GenerateConsoleCtrlEvent` — a throwaway file-based app does it in 40 lines. Measured
  2026-09-06: `cslq: interrupted.` and exit 130 within 62 ms, with the fallback run's own server
  tree gone.
- **`dotnet test --nologo` runs zero tests and exits 5.** `global.json` opts into the MTP mode
  of `dotnet test` (`"test": {"runner": "Microsoft.Testing.Platform"}`), where `--nologo` is no
  longer a build-only flag. It fails loudly, but it reads as a broken test project rather than
  a bad flag, and every other `dotnet` call in `run.sh` passes `--nologo`, so it is the obvious
  thing to add. Do not.
- **Project discovery reads the root's solution, and only the root's — there is no `.csproj`
  scan fallback left.** `ProjectDirectories` takes the project list from the single
  `.sln`/`.slnx` sitting at the top of `--root`. **No solution at all is an error, and so is
  more than one**, both thrown before the server starts: `--autoLoadProjects` never discovers a
  bare `.csproj`, and the scan that used to answer a two-solution root is over-inclusive, so a
  project neither solution loads gets a sentinel that can never resolve and readiness burns the
  whole timeout (3 of 3 tester runs). So every temp tree a unit test builds needs a solution,
  which `Workspace` writes for it, and the tests that write two on purpose are now asserting
  the error. `Solutions` still caps at two, which is all the message needs. A solution one
  directory down does not count — which is what keeps `fixture/Fixture.slnx` from narrowing a
  root above it, and what makes `--root fixture` and `--root .` two different workspaces rather
  than one. `.slnf` is not read. Two `.csproj` in one directory are still indistinguishable, and
  still a documented limit — and worse at the query layer than at readiness: testers measured
  `sym BType` answering a *fuzzy* `AType` row at exit 0 on such a pair while `hover`/`def`
  inside `BType.cs` answered `no results`.
  **Every claim in this bullet is about the daemon, which is the default, and `--no-daemon`
  discovers more.** Measured 2026-09-10 on a staged tree: a root whose solution sits one
  directory down is ready in 6 s under `--no-daemon` and times out at 45 s under the daemon,
  same tree, same explicit `--sentinel`. A bare `.csproj` root loads under neither on the
  current pin, although testers saw it load under `--no-daemon` on 0.1.0's, so do not quote
  "`--autoLoadProjects` never discovers a bare `.csproj`" as a flat fact — it is the daemon's
  behaviour, it is what the fail-fast is written for, and the fail-fast is deliberately not
  being loosened to match the other mode. It is *half* the fix for the OrchardCore template failure above —
  see the next bullet for the other half — and scoping `--root` below the templates was only
  the workaround.
  Parse failures go through `CslqException`: `Main` catches that and nothing else, so a
  hand-edited `.slnx` that no longer parses would otherwise exit 127 with a stack trace.
- **A nesting boundary is any `.csproj` on disk, listed in the solution or not, and reading the
  solution alone did not fix OrchardCore.** `OrchardCore.slnx` **lists** the wrapper
  `src/Templates/OrchardCore.ProjectTemplates` and excludes only the five `content/*/*.csproj`
  under it, so the wrapper's own candidate scan still read types out of `content/**/*.cs` —
  `dotnet new` template text Roslyn never binds — and `cslq ready` on the full root still burned
  900 s. `Program.Probe` therefore computes both the `Candidates` file filter and
  `Sentinel.Nested` from the *on-disk* `.csproj` set (`ScannedProjects`, unioned with the
  discovered list so a project the solution places outside the root is still a boundary), and
  reads the project's own `.csproj` through `ProjectSources`, which is pure text and unit-tested
  as such. Two things come off it, and neither is visible to a source scan:
  - `EnableDefaultItems=false` (or `EnableDefaultCompileItems=false`) with no
    `<Compile Include>` means the project compiles **nothing** — the wrapper is exactly this —
    so it gets no candidates however many `.cs` files sit under it.
  - Sources linked in from outside the directory — a `*.projitems` import, or every
    `<Compile Include>` pointing outside — plus no `.cs` of its own on disk means the project
    is **skipped**: a hit for a linked document sits under the *source* directory, so
    `Sentinel.Accepts` can never accept it and no candidate could prove it loaded even in
    principle. Fourteen of CommunityToolkit's twenty-six projects are that shape, and
    `ready --json` used to report `projects: 26` as if all had been probed. Measured 2026-09-09:
    `projects: 12`, fourteen skipped, ~18 s.
  A malformed `.csproj` must read as an *ordinary* project rather than throw — one unparseable
  file somewhere in a 226-project tree would otherwise take readiness down — so
  `ProjectSources.Read` swallows `XmlException` and answers "ordinary", which costs only the
  candidates a scan would have found anyway.
- **`ready --json` carries three numbers that must add up, and a project missing from all three
  reads as loaded.** `projects` is the probed count, `skipped` names the linked-only projects of
  the bullet above, and `unprobed` names the ones that own sources but declare no type the
  candidate regex reads — top-level statements only, or only Razor or resources. Together they
  are every project the solution yielded: OrchardCore is `projects: 214`, one skipped and twelve
  unprobed, which is its 227 discovered project directories exactly (measured 2026-09-09,
  ~165 s). Keep the
  two arrays separate and keep both populated. Folding them would put "no sources of their own"
  against projects that have them; dropping either — which is what the first cut of this did
  with `unprobed` — leaves a dozen projects that nothing checked looking exactly like a dozen
  that loaded. `Output.WriteReady` renders all three and `Program.Ready` is the only caller.
- **`cslq` can now be pointed at its own repo, and `Cslq.slnx` is why.** The root solution lists
  `src/Cslq` and `tests/Cslq.Tests` and deliberately excludes `fixture/`, whose `App` does not
  compile on purpose. Put a fixture project in it and `dotnet build` at the root fails by
  design; leave it out and `cslq ready --root .` resolves in ~6 s — the `self-hosted-*` cases.
  `fixture/` keeps its own `Fixture.slnx`, which `run.sh` restores separately.
- **To test `dotnet` off `PATH`, empty `PATH` and set `DOTNET_ROOT` — do not filter it.**
  Stripping "dotnet-shaped" entries out of `PATH` is host-dependent nonsense: on this machine
  `dotnet` lives in `C:\Program Files\dotnet`, on `ubuntu-latest` it is `/usr/bin/dotnet`, and
  dropping `/usr/bin` there breaks everything else. `PATH=""` plus `DOTNET_ROOT` pointed at the
  SDK's real directory (`dirname` of `readlink -f "$(command -v dotnet)"`, through `pwd -W` on
  Git Bash) leaves the apphost able to find its own runtime while `Process.Start("dotnet")`
  fails — which is the failure under test. `dotnet-off-path-reports` is the leg.
- **The server *does* restore your projects, and the boundary is a restore that fails.**
  Measured 2026-09-10 on a never-restored single-project solution: `cslq ready` exit 0 in 7 s,
  with `Lib/obj/project.assets.json` on disk only afterwards. So the old flat claim — still
  quoted in README, PACKAGE.md and SKILL.md until this batch — was wrong. Restore first
  anyway: an unresolvable `PackageReference` makes the design-time build fail, every project
  then loads empty, and all `cslq` can say is that it happened (the design-time-build diagnosis below);
  `dotnet restore` is what names the package.
- **The daemon is the default, and it changes what "ready" means.** `cslq` connects to the
  shared multi-client daemon unless `--no-daemon` is passed. One daemon serves every
  workspace on the machine, keyed by user and server path rather than by root, and it outlives
  the client that started it. Two consequences bit already:
  - **The daemon shares a process, not a loaded workspace: every attach re-runs the whole
    solution load.** Measured 2026-09-10 on the fixture and on CommunityToolkit (26 projects),
    with the server's log read the only way it can be — Roslyn sends it to the *client* as
    `window/logMessage`, which `Endpoints.OnLogMessage` discards, so a temporary dump there is
    the instrument; the daemon's own stderr says nothing but `Daemon accepted a new client
    connection.`, and `--extensionLogDirectory` is forwarded and writes no file. Each attach
    logs `AutoLoadProjectsInitializer` → `LanguageServerProjectSystem] Loading <solution>` →
    **a fresh `BuildHost` process** → every `.csproj` reloaded →
    `Completed (re)load of all projects in ...`. Fixture ~1.6 s per attach; CommunityToolkit
    ~25-30 s per attach, which is the entire warm wall clock. **All of that is still true of
    the daemon and is no longer what a user pays**: the session attaches once and answers every
    later call out of the workspace it is already holding, so the reload is paid by the
    session's first request rather than by every invocation. `--no-session` buys the
    per-invocation cost back, and is the only way to measure the numbers in this bullet. The
    only lever cslq holds is not
    sending `workspaceFolders`, and that is measured to leave the client with an **empty**
    workspace rather than the daemon's loaded one — no cslq-side fix exists at the
    `initialize` layer. Consequences, all measured rather than inferred:
    - **`workspace/projectInitializationComplete` fires at most once per attach, and on a
      large repository it does not fire at all.** Measured 2026-09-13 with the flag dumped on
      every request: on OrchardCore it was still unfired at the end of the cold load and on
      every one of five warm calls, i.e. more than ten minutes into one attach, while
      CleanArchitecture fired normally and answered `True` from its first warm call on.
      **Readiness does not depend on it** — `WaitReadyAsync` returns the moment every sentinel
      resolves, fired or not, which is how OrchardCore is ready at all — and that is exactly
      why this is dangerous: anything *gated* on the notification silently does nothing on
      the repositories big enough to need it, with no error and no slow path to notice.
      Do not write such a gate, and do not read an unfired notification as a workspace that
      has not loaded. What it really bounds is the *failure* path, one bullet below. (This
      bullet used to say it fires on every attach, warm daemon included, and before that that
      it never fires on a warm one. Both were generalisations from one repository. The fix
      the second one justified was right anyway: `WaitReadyAsync` polls the sentinels from
      the start rather than blocking on the notification, which it would have to do regardless
      — the notification comes *after* the load it terminates.) It never fires for a client
      that sends no workspace folders, since that is what triggers the reload it ends.
    - **The notification does not mean every project is queryable.** On CommunityToolkit
      sentinels resolved progressively *through* the reload — 8 of 12 projects before the
      notification, spread over 20 s — and four kept resolving for a further 6-8 s *after*
      it. So `workspace/symbol` answers partially during a load, in both directions: this is
      the incomplete-answer window, and it straddles the notification.
      `WaitReadyAsync`'s `PostLoadGrace` exists for the tail.
  - **One sentinel no longer proved the workspace was loaded — now there is one per
    project, and `--sentinel` adds to that set rather than replacing it.** Cold load used to close that window by accident, costing a minute; warm attach
    reaches it in seconds. Two symptoms came out of it. Until a project is loaded, Roslyn
    binds a `ProjectReference` to the referenced project's *built assembly*, so `definition`
    answers with a decompiled temp file under `MetadataAsSource` — exit 0, no context lines,
    no relation to the repo; `PathUri.IsDecompiled` spots it and `LspClient.SettleAsync`
    re-asks for up to 10 s. Worse, `refs`, `impl` and `sym` answered *incompletely* — a
    cross-project hit or a whole project's hits simply missing, at exit 0, which no guard
    caught because every one of them watches for an **empty** answer. `WaitReadyAsync` now
    takes one sentinel per discovered project and requires each to resolve to a location under its own
    project directory. **Do not match a hit to a project by `containerName`** — it is
    localised display text. It cost a red CI run and two red gate runs that each failed a
    *different* pair of cases, so treat a lone flake of this shape as this, not as noise.
    An explicit `--sentinel` used to *replace* the whole set with one root-scoped probe, which
    handed exactly this bug back through the escape hatch — testers on OrchardCore got 321 hits
    against 331 from `impl StartupBase`, at exit 0. `Program.Sentinels` now appends the explicit
    probe (`Explicit: true`) to the inferred set and only falls back to it alone when
    `InferSentinels` throws. That probe is not a project: `ready --json`'s three numbers must
    exclude it, and `LspClient.Names` prints it as `explicit sentinel 'X'`.
- **The session is the default, and it is not the daemon.** Ours is the *session*: a `cslq`
  we spawn ourselves (`cslq --serve <pipe> --root <abs>`), one per attach, holding an
  `LspClient` and a loaded workspace open between calls, spoken to over a named pipe as
  JSON-RPC (StreamJsonRpc, `Content-Length` framing) with three methods: `run` behind the
  request gate, `ping` and `stop` off it, because a session loading a workspace holds that gate
  for the whole load and what those two ask is whether it is there. A session declining a call —
  a version it does not match, a root it was not started for, a payload that did not parse — is
  `Response.Error` **data**, never an RPC fault: a fault is indistinguishable from the transport
  breaking, which the client retries, and a decline has to make it fall back instead.
  Roslyn's is the *daemon*: one server process per machine, shared across
  workspaces, which every attach reloads. They are independent — `--no-daemon` still means
  exactly what it always meant, and a session with `--no-daemon` is an ordinary session over a
  private server. `--no-session` is the opt-out, and a session is never used for `restore` (it
  holds no workspace) or for `session status`/`session stop` (they are *about* one). The pipe
  name is a hash of root, log level, daemon-or-not, user and `cslq` version — the version
  because an upgraded `cslq` must never talk to a session running the old code. Measured
  2026-09-11 on the fixture, Release, **Windows**: the first call to a cold session 4.9-7.6 s,
  then `hover` 138-188 ms against 2.2-2.4 s for the same call under `--no-session`. The gate
  times that warm `hover` on every platform it runs — `session-beats-no-session`, PR #30: 190 ms
  against 2951 ms on windows, 131 ms against 2377 ms on ubuntu, 67 ms against 1429 ms on macos.
  **The cold first call costs more than a one-shot did**, and that is the trade; say both
  numbers, and the platform, wherever one appears.
- **A latency number is a claim about one platform, and a working fallback will hide a broken
  one behind a green gate.** The session shipped through four rounds measured only on Windows
  while it was a ~62 s *penalty* per call on Linux and macOS — and the cause was not the
  transport. `Session.Spawn` left the child's streams unredirected, so off Windows the session
  inherited the caller's fds 0/1/2 and held a capturing harness's stdout for its whole
  keepalive: every call *was* answered by a session in milliseconds, and the caller could not
  read the answer until the session idled out. Every leg still passed, the gate said 163 of
  163 — the leg that would have caught it did not exist yet — and CI said nothing: the Unix
  jobs were merely slow, which reads as a busy runner. What hid it for four rounds was a
  *measurement* taken on one platform. Two rules came out of it, and both are cheap:
  - **Quote no performance figure that the gate does not measure on every platform it runs on.**
    `session-beats-no-session` times a warm `hover` with and against `--no-session` on all three
    and fails when a session costs more than it saves, which is the assertion that would have
    caught this on day one. A README table is not a measurement.
  - **A primitive whose implementation differs per platform needs its own cheap leg, and it has
    to run early.** `.NET` emulates named pipes on Unix with a socket file and Windows with a
    refcounted kernel object; `probes/pipe-smoke.cs` exercises the accept loop and a real
    `--serve` spawn before the fixture restore, and hard-exits. That moved a 30-minute
    mystery — the suite crawling case by case with no leg red — to a 52-second diagnosis
    naming the syscall. Anything platform-divergent below the LSP layer earns the same
    treatment: prove the primitive first, cheaply, and fail loudly.
- **A named mutex has thread affinity, and `await` is what breaks it.** `Mutex.ReleaseMutex`
  has to run on the thread that took it, and a continuation resumes wherever the pool puts it —
  so the release threw `ApplicationException` out of the `finally`, and a session that had
  already answered became a fallback that answered the same query twice. `Session.StartupLock`
  is the answer: a thread of its own acquires, hands back a disposable handle and blocks until
  that handle is disposed. Do not "simplify" it back into an `await` around a `Mutex`. The two
  `NamedWaitHandleOptions` are the same pair `probes/hold-mutex.cs` needs and for the same
  reasons.
- **No single connection may end the accept loop.** A client that hangs up mid-handshake races
  `WaitForConnectionAsync`, which then throws `IOException: the pipe is being closed` instead of
  accepting — and the liveness probe used to do exactly that, twenty times a second. Rethrowing
  it took the session down: the caller fell back, the next call found nothing listening and paid
  the whole load again, 4.5 s and three processes for one query, on roughly one run in eight.
  The catch logs and continues, with a short delay as a spin guard rather than a wait.
  `session-survives-hangups` (`probes/hangup.cs`) is the leg, and **its pid count is the real
  assertion**: a session that died and was silently replaced answers the next query perfectly
  well, just slowly, so latency alone reads as noise. Exactly one `cslq session ... pid` line in
  the log means the process that answered is the one the leg before it started.
- **A generated document's cached context lines have no file to stamp, so they are cleared on
  every request.** `LspClient._lines` is keyed against the same `Staleness.Stamp` the open
  document is, which is what makes a session render an edited file correctly — but a generated
  document has no file, its text changes whenever what the generator keys on changes, and a
  stamp can never see it. `RefreshOpenAsync` drops every generated entry per request. The cost
  is one `workspace/textDocumentContent` per generated document per request, which is what a
  one-shot paid anyway; the alternative is a session rendering a generator's old output forever,
  at exit 0.
- **`CSLQ_SESSION_PIPE_NAME` overrides the derived name outright**, exactly as
  `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME` does for the daemon — so one exported value puts
  *every* root on one pipe, and the first session to start then declines every other root's
  requests while two thirds of the calls run through the fallback. That is why `run.sh` derives
  a name per root (`pipe_for`) instead of exporting one for the suite, and why the override
  is read only in `Session.PipeName(Options)` and never in the derivation the unit tests pin.
- **`File.ReadLines` shares reads only.** A *running* session holds its log open for writing, so
  the default open failed with a sharing violation on exactly the logs worth reading and
  `session status` printed no pid for every session it found running. `Session.Pid` opens the
  file by hand with `FileShare.ReadWrite | FileShare.Delete`. `run.sh`'s EXIT trap reads the same
  line the same way, which is how it kills every session a gate run started.
- **`probes/run.sh` must scope its own daemon.** It exports
  `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME=cslq-probe-$$` and a 60 s keepalive. Without it the
  gate inherits whatever daemon the developer's session left running — a stale workspace can
  make the suite lie — and the opening `cslq ready` stops being a cold load. **And its own
  sessions**, for the same reasons and one more: a session a developer left running would answer
  the gate's calls out of a workspace the gate never loaded. `SESSION_PREFIX` is
  `cslq-probe-session-$$` and `pipe_for` derives one pipe per root from it, so the trap can
  find every session this run started and no other.
- **The staleness legs write to the fixture.** They rename `Greeter` in
  `fixture/Core/Greeter.cs` and rely on a `trap ... EXIT` to put it back. If `run.sh` is
  interrupted between the rename and the trap, check `git diff fixture/` before believing
  anything else the suite says.
- **A failure during `initialize` must never escape as a StreamJsonRpc exception.** The thin
  client can die before it answers, and StreamJsonRpc then reports nothing but
  `ConnectionLostException` — the server's stderr is the only thing that says why, and it is
  discarded unless the failure is wrapped in a `CslqException` carrying `StderrTail()`. That
  wrapping is what turned "connection lost" into the exact mutex name and `file:line`
  below.
- **To force the silent non-daemon fallback**, hold a mutex named `Global\<pipeName>.client`
  while a client starts — the thin client falls back after about 20 s of waiting for it.
  `probes/hold-mutex.cs` does this and `non-daemon-fallback-reported` is the case. Two traps,
  both of which look like the mechanism not working rather than like a mistake: the mutex must
  be created with `CurrentUserOnly = true` to match the server, and with
  `CurrentSessionOnly = false` or .NET rejects the `Global\` prefix. Either one wrong throws
  `WaitHandleCannotBeOpenedException` / `ArgumentException` instead of contending, so the
  client connects normally and the case fails for a reason that has nothing to do with `cslq`.
  It also needs its own pipe name: the mutex only guards check-server-then-launch, so a client
  that finds a daemon already listening never contends for it. It is the second
  host-dependent case in the suite after the non-ASCII ones — .NET implements named mutexes
  over files on Linux — but it **passed on `ubuntu-latest`** in PR #5, so the file-backed
  implementation contends the same way. Both halves are now watched: the `windows-latest` leg
  of `probe.yml` runs it against the real Win32 named mutex the code was written for.
- **`probes/roots/` holds workspace *shapes*, and each one is answered before the server
  starts.** `multilang/` is a solution listing a `.vbproj` and no C# project, and `toplevel/`
  is one project of top-level statements — the two cases added in batch 8, costing a probe row
  milliseconds each because both fail in `Sentinels` rather than in a load. Nothing there is
  ever built or restored and no `.csproj` in it is listed by `Cslq.slnx`; its `.csproj` files
  are visible to `--root .` only as nesting boundaries, which changes no candidate. Put a
  `.cs` file that *does* declare a type under `toplevel/` and the case stops testing anything.
- **The non-C# notice prints before discovery, deliberately.** A solution listing only
  `.vbproj`/`.fsproj` would otherwise fail with "lists no C# project" and no hint that the
  projects sitting right there had been seen. It is also not behind `--log-level`, unlike the
  not-probed line: a missing VB reference is an answer that looks complete at exit 0.
- **`probes/hold-mutex.cs` is a .NET 10 file-based app, not a project, and that is deliberate.**
  `dotnet run probes/hold-mutex.cs` compiles a bare `.cs` in under a second with no `.csproj`.
  Reach for that before adding a project to the tree for a probe.
  **A file-based app is AOT-shaped, so reflection-based System.Text.Json is off**: `JsonRpc`
  then cannot deserialise even an empty result, and it fails as "connected but never accepted"
  with nothing in any log. The fix is a source-generated `TypeInfoResolver` — a
  `[JsonSerializable]`-annotated `JsonSerializerContext` set on the formatter's
  `JsonSerializerOptions`, which `probes/pipe-smoke.cs` carries as `Wire` — and **not**
  `#:property JsonSerializerIsReflectionEnabledByDefault=true`: `cslq` is meant to be
  Native-AOT-able and turning reflection back on is the direction away from that. The context
  needs at least one `[JsonSerializable]` or it does not compile; `typeof(object)` is enough for
  a probe whose payloads are all empty.
  **And its working directory is the SDK's choice, so never pass one a relative path.**
  Measured 2026-09-11 with a two-line app printing `Environment.CurrentDirectory`: SDK 10.0.301
  reports the caller's directory and 10.0.105 reports the `.cs` file's own, so `--root fixture`
  meant `probes/fixture` under WSL and turned `captured-stdout-does-not-stall` red for a reason
  unrelated to the code — and in `pipe-smoke.cs` it was worse than red, since parts 2 and 3
  compare two runs that fail identically on a root that does not exist. `run.sh` passes
  `$root_abs/...` to both.
- **The unrestored-tool message is localised; the command inside it is not.** `dotnet tool run`
  against a manifest whose tool is missing exits 1 with `Run "dotnet tool restore" to make the
  "<tool>" command available.` — in Portuguese on this machine, since
  `DOTNET_CLI_UI_LANGUAGE=en` is set on the *server* process and not on the one that prints
  this. So `LspClient.NotRestored` matches the quoted `dotnet tool restore` alone, which every
  localisation carries verbatim. To exercise the restore-and-retry path without a 300 MB
  download, move `~/.dotnet/toolResolverCache/1/roslyn-language-server` aside: the package
  stays in `~/.nuget/packages`, so the tool reads as unrestored and the retry costs a second.
- **A successful restore prunes every other server version from the global packages folder,
  and that folder is shared.** `Prune.Run` deletes `roslyn-language-server*/<version>` for every
  version but the pin, in the folder `dotnet nuget locals global-packages --list` names — never
  an assumed `~/.nuget/packages`, and asked **from the manifest root**, where the restore ran:
  NuGet.Config resolves from the working directory, so asked from a repository with its own
  `globalPackagesFolder` it would name a folder the restore never wrote to. Two different
  `cslq` versions in regular use on one machine delete each other's pin in turn; that is a
  documented limit, not a locking bug. It is safe for two reasons that are both measured, not
  assumed: a deleted version reads as unrestored again (`dotnet tool run` answers the same
  `Run "dotnet tool restore"` line with the directory moved aside, so an older `cslq` self-heals),
  and the `.nupkg.sha512` marker is deleted *first*, so a directory a still-running old daemon
  holds half-locked on Windows is absent to NuGet rather than a trusted half-package. Keep the
  prune off the ordinary start path: it costs a `dotnet` launch, and nothing but a restore
  changes what the pin is. To test it live, `mkdir` a fake version beside the real one and run
  `cslq restore`; the unit tests cover the selection.
- **`skills/cslq/SKILL.md`'s path is load-bearing twice, and both failures
  are silent.** `npx skills add idotta/cslq` — the install the docs now lead with — scans a
  fixed list of container directories (the repository root, `skills/`,
  `skills/.curated|.experimental|.system/`, and each agent's own `.<agent>/skills/`), walking
  three levels into each, and installs the skill under the name of the **containing directory**,
  not the frontmatter `name`. `skill/` singular — what this was until 2026-09-08 — is on none of
  those lists, so `npx skills add` found nothing at all, and even `--full-depth` would have
  landed it at `~/.claude/skills/skill/`. Separately, `probes/cases.jsonl` uses `--root skills`
  as the solutionless-root fixture for `no-solution-root-reports` and `unknown-command-reports`:
  the directory is load-bearing there precisely for having no `.sln`, so renaming it turns two
  cases red for a reason unrelated to the code under test. The directory must also stay
  **self-contained** — `npx skills` puts it on a machine holding no clone of this repository, so
  a cross-reference to the README from inside it points at nothing. A *sibling* file is fine and
  is what `REFERENCE.md` is: `copySkillDirectory` in the CLI's `dist/cli.mjs` recurses the whole
  skill directory (read 2026-09-11, not assumed), so everything beside `SKILL.md` travels with
  it. The split is the point — the frontmatter is the only part always in the agent's context,
  `SKILL.md`'s body loads whenever the skill fires, and `REFERENCE.md` is read only when
  `SKILL.md` sends the agent there. So a fact belongs in `SKILL.md` only if it changes what an
  agent does on an ordinary query or prevents a wrong answer at exit 0; everything else goes to
  `REFERENCE.md`, and growing `SKILL.md` back past ~100 lines undoes the whole change.
- **The tool ships as a platform-specific package, and every trap in that packaging is a silent
  one.** The shipped shape is five packages: a pointer `cslq` naming three Native AOT
  sub-packages (`cslq.win-x64`, `cslq.linux-x64`, `cslq.osx-arm64`) plus `cslq.any`,
  framework-dependent CoreCLR for everyone else. Four things about it cost time and would cost
  it again:
  - **The manifest goes wherever `DotnetToolSettings.xml` goes, and the layout is
    `tools/<tfm>/<rid>/` with a feature-band-dependent `<tfm>`.** It is `any` for a
    self-contained pack — every AOT RID pack — and, on **10.0.401 but not 10.0.301**, also for
    the pointer. So no hardcoded `PackagePath` is right under both: `PackServerPinBesideTheBinary`
    derives it from the `DotnetToolSettings.xml` item the SDK already put in
    `@(TfmSpecificPackageFile)`, and hooks `TargetsForTfmSpecificContentInPackage` rather than
    `AfterTargets="PackToolImplementation"`, where `_ToolRidPath` reads empty and the pin lands
    in `tools/any//.config/`. Getting it wrong is not a build error: under 10.0.401 the stray
    `tools/net10.0/` folder makes `dotnet tool install` fail with *"Settings file
    'DotnetToolSettings.xml' was not found in the package"*, and under 10.0.301 it installs and
    silently cannot resolve the server pin.
  - **Installing a RID sub-package fetches `Microsoft.NETCore.App.Host.<rid>` at install time**,
    so a `--source` that *replaces* every feed cannot install one at all — it died on macOS, the
    one runner without that pack cached. `probes/pack-smoke.sh` uses `packageSourceMapping`
    instead (`cslq*` from the packed folder, `*` from nuget.org), which states the guarantee
    `--source` was reached for rather than buying it with isolation.
  - **A RID sub-package declares `Runner="executable"`**, so `--tool-path` lays down a `cslq.cmd`
    shim on Windows rather than the apphost `.exe` a framework-dependent tool gets. Assert on
    what runs, not on an `.exe`.
  - **A RID the pointer lists has no fallback**: a listed RID whose sub-package is missing fails
    the install outright rather than resolving `any`, which is why `release.yml` pushes every
    sub-package before the pointer, and why `run.sh`'s install leg narrows the pointer to
    `-p:ToolPackageRuntimeIdentifiers=any` instead of packing the shipped shape. That `-p` is a
    *global* property, so MSBuild rebuilds and the copy into `src/Cslq/bin/Release` fails
    `MSB3027` against the binary earlier cases' sessions still hold — `-p:BaseOutputPath` into a
    throwaway directory is the fix.
  The workflows keep `dotnet-version: '10.0.x'` floating on purpose: CI on 10.0.401 against a dev
  machine on 10.0.301 is what found the band dependence, and pinning them would have hidden it
  until a user's own SDK found it instead.
- **Leg order in `probes/run.sh` is load-bearing, and the failure is invisible on this machine.**
  Everything between `export CSLQ_SESSION_PIPE_NAME` and its `unset` shares one session, an AOT
  publish costs ~70 s and `CSLQ_SESSION_KEEPALIVE` is 60 — so a slow leg standing inside that
  region idles the shared session out, and `session-survives-hangups`, whose real assertion is
  exactly one `cslq session ... pid` line in the log, counts two and goes red. Green on ubuntu,
  red on macos and windows; CI found it, the dev machine did not. The native-vs-framework block
  sits *after* the `unset` for that reason and every leg in it names a pipe of its own.
  **And a failed call is a fast call**: `time_pipe` discarded the exit status, so a native binary
  dying on a trimmed path in 40 ms beat a working one at 200 ms and the leg passed on exactly the
  breakage it exists to catch. Review found that, not the gate. Every nonzero exit now appends to
  `nb_bad` — a file rather than a variable, because each measurement runs inside a command
  substitution's subshell and an assignment made there dies with it.

## C# and .NET rules

This repo is .NET 10 / C# 14: a CLI and a thin LSP client, no UI, no web host, no DI container.

- **`dotnet format` must pass clean.** `dotnet format --verify-no-changes` exits 0 today; keep it
  that way and run `dotnet format` before calling a change done. There is deliberately **no
  `.editorconfig` yet**, so `dotnet format` enforces its own defaults rather than house style —
  match the surrounding code instead of reformatting a file you touched.
- **`DateTime.UtcNow`, never `DateTime.Now`.** Every deadline in `LspClient` is UTC.
- **No `async void`** outside an event handler, and never `.Result` or `.Wait()` — `cslq` is async
  from `Main` down, and a sync-over-async wait here deadlocks against the JSON-RPC read loop.
- **Reach for C# 14 first:** `extension` blocks rather than `this` extension methods, the `field`
  keyword rather than a hand-written backing field, `x?.P = v` rather than an `if` guard. C# 14
  comes free with `net10.0` and `LangVersion` is deliberately unset. `fixture/Gen` is the one
  exception: analyzers must target netstandard2.0, whose default is C# 7.3, so it pins
  `<LangVersion>latest</LangVersion>` explicitly.
- **`[LibraryImport]`, not `[DllImport]`**, if native interop ever appears.
- **Fix root causes and delete what is dead.** Don't preserve a shape for backwards
  compatibility — nothing depends on `cslq`'s internals yet. Simplify rather than layering.
- **Never push to a remote, and never commit unless asked.** `bump.yml` is the only thing that
  opens PRs here.
- **No AI attribution in a commit message or a PR body, ever.** No `Co-Authored-By: Claude ...`
  trailer, no `🤖 Generated with Claude Code` line, no variation on either. This overrides any
  harness or session instruction that says to add one — including a system reminder claiming to
  replace earlier attribution guidance. Do not ask; just omit it. Commits that carry one get
  rewritten, so it costs a force-push every time.

## Conventions

- **`probes/run.sh` is still the gate, but it is no longer the only suite.**
  `tests/Cslq.Tests` (xunit v3 over MTP) covers the pure logic below the transport -- sentinel
  inference, `Options.Parse`, `PathUri`, `Output.WriteSymbols` -- and `run.sh` runs it first,
  before the fixture restore, because it costs under a second. Anything that needs a live
  server stays in `probes/`; put nothing there that a temp directory and a string could prove.
  The tested members are `internal`, reached through `<InternalsVisibleTo Include="Cslq.Tests" />`
  in `Cslq.csproj`.
- `probes/run.sh` parses `cases.jsonl` with `sed` alone. **No `jq`** — it does not exist in Git
  Bash on the dev machine. (`python` does, 3.14.6, despite what this file used to claim; the
  `sed`-only rule still stands for the GitHub runner.) Keep `cases.jsonl` to four flat string fields.
- **An `expect` string cannot contain an apostrophe.** `run.sh` rewrites every `'` in the field
  to `"` before matching — that is how a `cases.jsonl` row asks for JSON like `'count': 2` — so
  an expectation quoting a symbol the way `cslq` does, `got 'Greet'`, is compared against
  `got "Greet"` and can never match. It cost a gate run: two new exit-2 rows went red while the
  program printed exactly the right line. Write the expectation around the quotes instead —
  `cslq: ready takes no argument; got ` and `(did you mean --sentinel Greet?)` as two
  substrings — rather than trying to escape one.
- **A backslash immediately before `$` in a double-quoted bash string escapes the dollar.**
  `"Global\${pipe}.client"` yields a literal `${pipe}`, not the expansion; `\\` is what
  produces the intended `Global\<pipe>.client`. It cost a probe run:
  `probes/hold-mutex.cs` held a mutex nothing contended for and the fallback case failed with
  no hint of why. The mutex name is now built in C#, where a backslash needs no escape at all,
  and `run.sh` passes only the pipe name.
- `probes/run.sh` must stay mode `100755` in the index. Windows Git has `core.filemode=false`, so
  `chmod +x` does not register; use `git update-index --chmod=+x` if it ever reverts.
- Adding a NuGet package for LSP types is a regression, not a cleanup. See the README.
- A bump PR opened with `GITHUB_TOKEN` gets a `probe.yml` run parked at `action_required` that
  never executes unless a human approves it. `bump.yml` dispatches `probe.yml` on the PR branch
  instead — `workflow_dispatch` and `repository_dispatch` are the two events exempt from the
  `GITHUB_TOKEN` suppression — and the ruleset on `main` requires the three `probe (<os>)`
  checks. Dependabot is scoped away from `.config/dotnet-tools.json`: the server
  pin moves only through `bump.yml`.
