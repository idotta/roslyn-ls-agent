#!/usr/bin/env bash
#
# The gate. Every server bump has to survive this before it reaches main.
#
# cases.jsonl is one flat JSON object per line with exactly four string fields
# (name, args, exit, expect) so it can be parsed with sed alone -- no jq, no python,
# so the same script runs on a GitHub runner and in Git Bash on Windows. Inside
# `expect`, ' stands for " and | separates substrings that must all appear in the
# combined stdout+stderr of the command.
set -uo pipefail

cd "$(dirname "$0")/.."
root=$(pwd)

log() { printf '\n==> %s\n' "$*"; }

log "dotnet tool restore"
dotnet tool restore || exit 1

# The language server does not restore your projects. Skip this and anything needing
# resolved references comes back empty rather than erroring -- a silent false pass.
log "dotnet restore (fixture)"
dotnet restore fixture/Fixture.sln --nologo -v q || exit 1

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

printf '\n%s passed, %s failed\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
