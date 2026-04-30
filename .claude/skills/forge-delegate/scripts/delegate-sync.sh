#!/usr/bin/env bash
# delegate-sync.sh <agent-name> <input-json>
#
# Sync wrapper for `forge agent <name>`. Blocks until forge exits.
# Stdout = forge's stdout (the agent's final JSON output).
# Stderr = forge's stderr (logs).
# Exit code = forge's exit code (0 / 1 / 2 / 6 / 7 / 130).
#
# Caller decides on exit-code semantics; this script does not auto-resume.

set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $(basename "$0") <agent-name> <input-json>" >&2
  exit 64
fi

agent="$1"
input="$2"

exec forge agent "$agent" --input "$input" --callers auto-deny
