---
name: csharp-semantic-queries
description: >-
  Use for ANY question about C# or .NET code in this repository that is about meaning rather
  than text: who calls a method, where a type or member is declared, what is in a file, what
  the compiler thinks is wrong. Run `csx` instead of grep, ripgrep, Select-String or reading
  files to answer them. Trigger on "find all callers", "who uses", "where is X defined",
  "go to definition", "what's in this file", "does this compile", "any errors", "what does
  this class expose", "rename impact", "is this method still used", "dead code", and on any
  C# identifier the user names without a file path. Also use before editing an unfamiliar C#
  file, to see the declarations and the callers of what you are about to change.
---

# Semantic C# queries with `csx`

`csx` is a CLI over Microsoft's `roslyn-language-server` — the same engine as the VS Code C#
extension. It answers about the compiled semantic model, so it sees cross-project references,
generic instantiations, source-generated code and `partial` halves. Grep sees none of that.

## Start every session with this

```
csx ready
```

Blocks until the workspace has loaded and exits 0. Everything else waits for readiness on its
own, so this is not required — but running it once gets the load out of the way and turns a
slow first query into a fast one.

## Task → command

| Task | Use this | Do NOT |
|---|---|---|
| Every caller / user of a method, type, property | `csx refs <symbol>` | grep the name — misses aliases, hits comments and strings |
| Where something is declared | `csx def <symbol>` | grep `class X` — misses `partial`, generated and cross-project |
| What a file declares, and its nesting | `csx outline <file>` | read the whole file into context |
| Compiler / analyzer errors in a file or the tree | `csx diag [path]` | `dotnet build` and parse the log |
| Confirm a symbol still exists at all | `csx def <symbol>` | assume from a grep hit |

Never answer "who calls this?" or "where is this defined?" from a text search in a C# repo.
A text search cannot tell a call from a comment, and it cannot see a caller in another project.

## Targets

`refs`, `def` and `outline` take either form:

- **A symbol:** `Greet`, `Greeter.Greet`, `Fixture.Core.Greeter.Greet`. Only the last two
  segments are matched — the enclosing type and the member — because Roslyn returns the
  container as localised display text, not a namespace path. An ambiguous target exits 1 and
  lists the candidates with their locations.
- **A position:** `App/Program.cs:9:35`, relative to the workspace root, **one-based** line and
  column, and columns are UTF-16 code units. Paste a position straight out of any `csx` result.

`outline` also takes a bare file path. Anything containing a separator or ending `.cs` is
treated as a file, never as a symbol.

`diag` is the exception: it takes a **file or directory path, or nothing at all** — never a
symbol or a position. With no argument it walks every `.cs` file under `--root`.

## Output

`path:line:col` relative to the workspace root, then the matched line marked `>` with a line of
context either side. `--max N` caps results (default 50) and `--context N` widens the window.
`--json` gives `{ count, truncated, results }` for scripting.

`outline` is the exception: the path once as a header, then one row per declaration indented by
nesting, no per-row position and no context.

Source-generated locations print as `<generated>/<assembly>/<hintName>` and have no file on
disk. That is a real answer, not an error — read the source with `csx outline` on the symbol,
not with a file read.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | The query was answered. For `diag` this includes a clean file — no diagnostics is a successful query |
| 1 | The lookup failed: no such symbol, an ambiguous symbol, no references, no definition, no such file, or the workspace never loaded |
| 2 | No arguments |

`refs` and `def` exit 1 on an empty result, because an empty answer means the target was not
what you thought. `diag` and `outline` exit 0 on an empty result, because nothing to report is
an answer.

## Options worth knowing

```
--root <dir>        workspace root (default: the current directory)
--max N             cap results (default 50)
--context N         source lines either side of a hit (default 1)
--timeout N         seconds to wait for the workspace to load (default 180)
--json              machine-readable output
--sentinel <sym>    the symbol used to prove the workspace loaded
--no-daemon         start a private server instead of sharing the daemon
```

`csx` shares one background server (the daemon) across invocations, so a warm query costs a
couple of seconds instead of a full solution load. You do not need to manage it. If a run
prints `csx: daemon unreachable`, the answer is still correct — it was just slow.

## When a query comes back empty

1. **A root with only a `.csproj` and no solution never loads.** `csx` waits out its whole
   timeout and every query returns nothing. Add a `.sln`/`.slnx`, or point `--root` at a
   directory that has one. This is the most common cause by far.
2. **Run `dotnet restore` first.** The language server does not restore for you, and anything
   needing resolved references comes back empty rather than erroring.
3. **A source generator has to be built** before its output exists. If a generated symbol is
   missing, build the analyzer project.
4. **Check the symbol with `csx def`** before concluding anything about `refs`.
