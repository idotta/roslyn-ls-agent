#!/usr/bin/env bash
#
# The gate. Every server bump has to survive this before it reaches main.
#
# cases.jsonl is one flat JSON object per line with exactly four string fields
# (name, args, exit, expect) so it can be parsed with sed alone -- no jq, no python,
# so the same script runs on a GitHub runner and in Git Bash on Windows. Inside
# `expect`, ' stands for " and | separates substrings that must all appear in the
# combined stdout+stderr of the command. That separator means an expectation can never
# quote a rendered `csx outline` row, whose gutter is also | -- pasting one in silently
# becomes two weaker substring matches. Assert bare declarations instead.
set -uo pipefail

cd "$(dirname "$0")/.."
root=$(pwd)
# csx talks to the shared daemon by default, so scope this run to a daemon of its own.
# Without the pipe name the suite would inherit whatever daemon the developer's session
# left running -- a stale workspace could make the gate lie, and the `csx ready` below
# would stop being a cold load. Keepalive is short because this daemon is disposable:
# the countdown only starts once the last case disconnects.
export ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME="csx-probe-$$"
export ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE=60


log() { printf '\n==> %s\n' "$*"; }

log "dotnet tool restore"
dotnet tool restore || exit 1

# The language server does not restore your projects. Skip this and anything needing
# resolved references comes back empty rather than erroring -- a silent false pass.
log "dotnet restore (fixture)"
dotnet restore fixture/Fixture.slnx --nologo -v q || exit 1

# The analyzer has to exist as a built assembly before the server loads Core, or the
# generator contributes nothing -- no error, no diagnostic, the generated symbol simply
# is not there. Debug is pinned because the design-time build resolves the analyzer from
# Gen/bin/Debug; building it Release would leave that path stale or empty.
#
# Core, not Gen: the analyzer ProjectReference makes this build Gen first anyway, and
# compiling Core is what arms its <WarningsAsErrors>CS9057</WarningsAsErrors> -- the guard
# against the analyzer being built against a newer compiler than the one loading it, which
# otherwise degrades to the same silent nothing. Never the solution: the deliberate type
# error for `csx diag` is deliberately kept out of Core so this step stays green.
log "build fixture generator + Core"
dotnet build fixture/Core/Core.csproj -c Debug --nologo -v q || exit 1

log "build csx"
dotnet build src/Csx/Csx.csproj -c Release --nologo -v q || exit 1

CSX="$root/src/Csx/bin/Release/net10.0/csx"
[ -x "$CSX" ] || CSX="$CSX.exe"
[ -x "$CSX" ] || { echo "csx not found at $CSX" >&2; exit 1; }

# Readiness is asserted before any case runs: project load is async and a query fired
# too early returns empty results, not an error, so a naive probe reports a false pass.
log "csx ready"
start=$(date +%s)
"$CSX" ready --root fixture --timeout 300 || exit 1
printf 'cold ready: %ss\n' "$(( $(date +%s) - start ))"

log "cases"
pass=0
fail=0

while IFS= read -r line || [ -n "$line" ]; do
  [ -z "${line// /}" ] && continue

  name=$(printf '%s' "$line" | sed -n 's@.*"name":"\([^"]*\)".*@\1@p')
  args=$(printf '%s' "$line" | sed -n 's@.*"args":"\([^"]*\)".*@\1@p')
  want_exit=$(printf '%s' "$line" | sed -n 's@.*"exit":"\([^"]*\)".*@\1@p')
  expect=$(printf '%s' "$line" | sed -n 's@.*"expect":"\([^"]*\)".*@\1@p')

  if [ -z "$name" ] || [ -z "$args" ]; then
    printf 'MALFORMED %s\n' "$line"
    fail=$((fail + 1))
    continue
  fi

  # shellcheck disable=SC2086 -- args is a deliberately word-split argument list.
  out=$("$CSX" $args 2>&1)
  got_exit=$?

  ok=1
  reason=""
  if [ "$got_exit" != "$want_exit" ]; then
    ok=0
    reason="exit $got_exit, wanted $want_exit"
  fi

  old_ifs=$IFS
  IFS='|'
  for want in $expect; do
    want=${want//\'/\"}
    case "$out" in
      *"$want"*) ;;
      *) ok=0; reason="${reason:+$reason; }missing: $want" ;;
    esac
  done
  IFS=$old_ifs

  if [ "$ok" = 1 ]; then
    printf 'PASS  %s\n' "$name"
    pass=$((pass + 1))
  else
    printf 'FAIL  %s (%s)\n' "$name" "$reason"
    printf '%s\n' "$out" | sed 's/^/      | /'
    fail=$((fail + 1))
  fi
done < probes/cases.jsonl

# The only case that mutates the fixture, and the only one that needs a server to outlive
# an invocation: rename what the generator keys on, then ask a *fresh* client whether the
# generated symbol went away. It is a shell block rather than a cases.jsonl row for both
# reasons -- a row is one invocation and cannot restore what it changed.
#
# Absence is also what an unloaded workspace looks like, so every leg gates on
# `--sentinel Cheer`, which the rename does not touch: the workspace is provably loaded
# before absence is concluded. Run the legs in order -- only restore-and-present proves
# the daemon is still live and answering rather than quietly stuck.
log "source-generator staleness"
greeter=fixture/Core/Greeter.cs
greeter_saved=$(mktemp)
cp "$greeter" "$greeter_saved"
trap 'cp "$greeter_saved" "$greeter"; rm -f "$greeter_saved"' EXIT

# The daemon refreshes off its own file watcher, so both directions are eventually
# consistent -- poll rather than trust the first answer. Each absent attempt already
# costs the ~10s grace MatchSymbolsAsync gives a symbol before calling it missing.
await_generated() {
  deadline=$(( $(date +%s) + 90 ))
  while :; do
    out=$("$CSX" def Fixture.Core.Generated.BuildInfo.Stamp --root fixture --sentinel Cheer 2>&1)
    rc=$?
    if [ "$1" = present ] && [ "$rc" = 0 ]; then
      case "$out" in *"BuildInfo.g.cs"*) return 0 ;; esac
    fi
    if [ "$1" = absent ] && [ "$rc" = 1 ]; then
      case "$out" in *"no symbol matched"*) return 0 ;; esac
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
      printf '%s\n' "$out" | sed 's/^/      | /'
      return 1
    fi
  done
}

leg() {
  if await_generated "$2"; then
    printf 'PASS  %s\n' "$1"
    pass=$((pass + 1))
  else
    printf 'FAIL  %s (generated symbol never became %s)\n' "$1" "$2"
    fail=$((fail + 1))
  fi
}

leg staleness-baseline-present present
sed -i 's/\bGreeter\b/GreeterRenamed/g' "$greeter"
leg staleness-after-rename-absent absent
cp "$greeter_saved" "$greeter"
leg staleness-after-restore-present present

# The silent non-daemon fallback, forced. The thin client falls back when it times out
# waiting for its startup mutex (about 20 s), so holding that mutex is the entire trigger
# -- see probes/hold-mutex.cs for why that needs a .NET process. A distinct pipe name is
# required: the mutex only guards check-server-then-launch, so a client that finds a daemon
# already listening never contends for it.
#
# Both halves of the assertion matter. Exit 0 pins that a fallback run still answers, which
# is what makes it silent; the warning pins that csx noticed, which is the only thing between
# an agent and blaming the latency on us.
log "non-daemon fallback"
fb_pipe="csx-probe-fallback-$$"
fb_log=$(mktemp)
dotnet run probes/hold-mutex.cs -- "$fb_pipe" 90 > "$fb_log" 2>&1 &
fb_holder=$!

fb_deadline=$(( $(date +%s) + 60 ))
while ! grep -q held "$fb_log" 2>/dev/null; do
  if ! kill -0 "$fb_holder" 2>/dev/null || [ "$(date +%s)" -ge "$fb_deadline" ]; then
    break
  fi
  sleep 1
done

if ! grep -q held "$fb_log" 2>/dev/null; then
  printf 'FAIL  %s (could not hold the daemon startup mutex)\n' "non-daemon-fallback-reported"
  sed 's/^/      | /' "$fb_log"
  fail=$((fail + 1))
else
  out=$(ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME="$fb_pipe" "$CSX" ready --root fixture --timeout 300 2>&1)
  rc=$?
  case "$out" in
    *"daemon unreachable"*) ok=$([ "$rc" = 0 ] && echo 1 || echo 0) ;;
    *) ok=0 ;;
  esac
  if [ "$ok" = 1 ]; then
    printf 'PASS  %s\n' "non-daemon-fallback-reported"
    pass=$((pass + 1))
  else
    printf 'FAIL  %s (exit %s, wanted 0 and the fallback warning)\n' "non-daemon-fallback-reported" "$rc"
    printf '%s\n' "$out" | sed 's/^/      | /'
    fail=$((fail + 1))
  fi
fi

kill "$fb_holder" 2>/dev/null
wait "$fb_holder" 2>/dev/null
rm -f "$fb_log"

printf '\n%s passed, %s failed\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
