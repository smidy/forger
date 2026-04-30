#!/usr/bin/env bash
# delegate-status.sh <handle>
#
# Returns JSON describing the current state of a background forge run.
#
# State machine:
#   exit_code missing  + pid alive  →  state="running"
#   exit_code missing  + pid gone   →  state="failed" (abnormal)
#   exit_code = 0                   →  state="completed"
#   exit_code = 1 / 2 / *           →  state="failed"
#   exit_code = 6                   →  state="rate_limited"
#   exit_code = 7                   →  state="caller_deferred"
#   exit_code = 130                 →  state="cancelled"

set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: $(basename "$0") <handle>" >&2
  exit 64
fi

handle="$1"

if [ ! -d "$handle" ]; then
  printf '{"state":"unknown","error":"handle directory not found: %s"}\n' "$handle"
  exit 0
fi

pid=$(cat "$handle/pid" 2>/dev/null || echo "")
agent=$(cat "$handle/agent.txt" 2>/dev/null || echo "")
started_at=$(cat "$handle/started_at" 2>/dev/null || echo "0")

if [ ! -f "$handle/exit_code" ]; then
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
    echo '{"state":"running"}'
    exit 0
  fi
  printf '{"state":"failed","error":"process exited without writing exit_code","pid":"%s"}\n' "$pid"
  exit 0
fi

exit_code=$(cat "$handle/exit_code" | tr -d '[:space:]')

case "$exit_code" in
  0)   state="completed" ;;
  1)   state="failed" ;;
  2)   state="failed" ;;
  6)   state="rate_limited" ;;
  7)   state="caller_deferred" ;;
  130) state="cancelled" ;;
  *)   state="failed" ;;
esac

runs_root="$HOME/.forge/runs"
run_id=""
if [ -n "$agent" ] && [ -d "$runs_root" ]; then
  run_id=$(
    find "$runs_root" -maxdepth 1 -mindepth 1 -type d -name "${agent}-*" -newermt "@$started_at" 2>/dev/null \
      | sort | tail -n 1 | xargs -I{} basename {} 2>/dev/null
  )
fi

result_json="null"
stdout_raw=""
if [ -s "$handle/stdout.json" ]; then
  if [ "$state" = "completed" ] && jq -e . "$handle/stdout.json" > /dev/null 2>&1; then
    result_json=$(cat "$handle/stdout.json")
  else
    stdout_raw=$(tail -c 4096 "$handle/stdout.json" 2>/dev/null || true)
  fi
fi

stderr_text=""
if [ -f "$handle/stderr.log" ]; then
  stderr_text=$(tail -c 4096 "$handle/stderr.log" 2>/dev/null || true)
fi

jq -n \
  --arg state "$state" \
  --argjson exit_code "$exit_code" \
  --arg run_id "$run_id" \
  --argjson result "$result_json" \
  --arg stdout "$stdout_raw" \
  --arg stderr "$stderr_text" \
  '{
    state: $state,
    exitCode: $exit_code,
    runId: (if $run_id == "" then null else $run_id end),
    result: $result,
    stdout: (if $stdout == "" then null else $stdout end),
    stderr: (if $stderr == "" then null else $stderr end)
  } | with_entries(select(.value != null))'
