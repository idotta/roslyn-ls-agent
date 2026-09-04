# Design

Distilled from the original brief. `ROADMAP.md` tracks state; this file is the why.

## The problem

Agents in C# repos fall back on grep and reading whole files. They can't reliably find all
callers of a method, resolve types across project boundaries, or see source-generated symbols.
A language server fixes that; the existing ways to wire one to an agent are third-party wrappers
that go stale.

Two hard constraints:

1. **Official tooling only.** The C#-specific component in the hot path is Microsoft-published.
2. **Always current.** The pin updates automatically and safely, not by hand.

## Output rules

The highest-leverage part of the project. Raw LSP returns URIs and zero-based ranges, which is
near-useless to a model.

- Print `path:line` plus the matched line and a line of surrounding context.
- Paths relative to the workspace root. Lines and columns one-based.
- Cap results by default so one call can't blow the context window.
- `--json` for the probe harness to assert against.
- Every new command follows these. They are the reason this is a CLI and not a wrapper.

**`outline` is the one deliberate exception.** It prints the document path once as a header
and then one row per declaration — that declaration's own source line, indented by nesting —
with no per-row `path:line:col`, no `>` marker and no surrounding context, and `--context` is
inert for it. An outline *is* the summary the other rules exist to produce; repeating the path
on every row and padding each with context lines would make a whole file unreadable and cost
the context window the rules are meant to protect. Everything else still holds: one-based
lines, root-relative paths, `--max` (over the pre-order flattening, so a truncated tree is
always a prefix and no node outlives its parent) and the same `{ count, truncated, results }`
JSON envelope, with `path` and `generated` on the envelope because the whole document is one
URI.

Source-generated locations are labelled `<generated>/<assemblyName>/<hintName>`, built only
from the URI fields that are stable across runs and machines. **Known limitation:** none of
those fields identifies the *consuming* project, so one generator applied to several projects
— an analyzer in `Directory.Build.props`, the common real-world shape — produces several
distinct documents that all render identically, and `Output` sorts and renders by that label.
A references response carries only a URI and a range, so there is nothing in it to
disambiguate with. The available disambiguator is `containerName` (`"in BuildInfo (project
Core (net10.0))"`), which arrives on `workspace/symbol` results and is already parsed by
`Program.Matches` — but it is absent from the reference locations themselves, so wiring it
through means carrying the resolved symbol's project alongside the URI. Deferred until a
fixture has two projects consuming one generator; the fixture today has one.

## Settled — do not re-litigate

| Decision | Why |
|---|---|
| Server is `roslyn-language-server` | Microsoft-published, MIT, same engine as the VS Code C# extension |
| We write our own thin LSP client | A client is needed for the probes regardless; two clients would disagree about readiness |
| CLI, not an MCP server | MCP tool schemas cost context on every request; a CLI costs one paragraph in `SKILL.md`, and we control the output shape |
| Updates via cron GitHub Action | Renovate's `ignoreUnstable` default would silently never bump a train whose minor moves every release; Dependabot has a history of mangling `dotnet-tools.json`. Both beatable, neither worth the fight for one dependency |
| Pin in `.config/dotnet-tools.json` | Reproducible, in source control, bumpable by CI |
| LSP payload types are hand-defined | No maintained Microsoft package supplies them — see the README. Adding a package back is a regression, not a cleanup |
| No off-the-shelf MCP↔LSP bridge | Third-party code in the hot path, and language-agnostic bridges know nothing about Roslyn's specific failure modes |

If `--autoLoadProjects` proves insufficient for some repo shape, investigate the non-standard
`solution/open` notification (it exists in the server) rather than pulling in a wrapper.
