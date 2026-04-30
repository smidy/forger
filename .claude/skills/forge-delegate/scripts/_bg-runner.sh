#!/usr/bin/env bash
# Internal helper. Called by delegate-bg.sh under nohup.
# Runs `forge agent <name>` and writes the exit code to <handle>/exit_code.
#
# usage: _bg-runner.sh <agent-name> <handle-dir>

set -u

agent="$1"
handle="$2"

forge agent "$agent" --input "@$handle/input.json" --callers auto-deny \
  > "$handle/stdout.json" 2> "$handle/stderr.log"
echo $? > "$handle/exit_code"
