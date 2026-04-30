#!/bin/bash
# Find the most recently updated active plan directory.
# Globs docs/plans/*/task_plan.md (excluding _archive/), reads YAML frontmatter,
# filters status: active, sorts by `updated:` desc, prints the dir of the head
# match to stdout. Exits 1 if no active plan exists.
#
# Usage: find-active-plan.sh [project-root]
#   project-root defaults to `git rev-parse --show-toplevel` or PWD.

set -e

ROOT="${1:-}"
if [ -z "$ROOT" ]; then
    ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
fi

PLANS_DIR="$ROOT/docs/plans"
[ -d "$PLANS_DIR" ] || exit 1

best_dir=""
best_updated=""

for f in "$PLANS_DIR"/*/task_plan.md; do
    [ -f "$f" ] || continue
    case "$f" in
        "$PLANS_DIR/_archive/"*) continue ;;
        "$PLANS_DIR/_template/"*) continue ;;
    esac

    status=$(awk '/^---$/{f=!f;next} f && /^status:[[:space:]]/{sub(/^status:[[:space:]]*/,"");gsub(/[[:space:]]+$/,"");print;exit}' "$f")
    [ "$status" = "active" ] || continue

    updated=$(awk '/^---$/{f=!f;next} f && /^updated:[[:space:]]/{sub(/^updated:[[:space:]]*/,"");gsub(/[[:space:]]+$/,"");print;exit}' "$f")

    if [ -z "$best_updated" ] || [ "$updated" \> "$best_updated" ]; then
        best_updated="$updated"
        best_dir="$(dirname "$f")"
    fi
done

[ -n "$best_dir" ] || exit 1
echo "$best_dir"
