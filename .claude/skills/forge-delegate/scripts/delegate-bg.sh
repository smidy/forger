#!/usr/bin/env bash
# delegate-bg.sh <agent-name> <input-json>
#
# Background wrapper for `forge agent <name>`. Returns immediately with a
# handle path (a temp directory under ~/.forge/delegations/) containing:
#
#   input.json   — agent input (UTF-8)
#   agent.txt    — agent name
#   started_at   — epoch seconds at kick-off (used for run-id recovery)
#   pid          — wrapper subprocess PID (for liveness check)
#   exit_code    — populated when forge exits (sentinel for completion)
#   stdout.json  — forge's stdout
#   stderr.log   — forge's stderr
#
# Poll progress with delegate-status.sh <handle>.

set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $(basename "$0") <agent-name> <input-json>" >&2
  exit 64
fi

agent="$1"
input="$2"
script_dir="$(cd "$(dirname "$0")" && pwd)"

delegations_root="$HOME/.forge/delegations"
mkdir -p "$delegations_root"
handle=$(mktemp -d "$delegations_root/forge-XXXXXX")

printf '%s' "$input"  > "$handle/input.json"
printf '%s' "$agent"  > "$handle/agent.txt"
date -u +%s           > "$handle/started_at"

nohup bash "$script_dir/_bg-runner.sh" "$agent" "$handle" > /dev/null 2>&1 &
echo "$!" > "$handle/pid"
disown 2>/dev/null || true

echo "$handle"
