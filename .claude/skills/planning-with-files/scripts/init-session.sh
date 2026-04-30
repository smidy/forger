#!/bin/bash
# Initialize planning files for a new plan in docs/plans/<YYYYMMDD>-<slug>/.
# Usage: ./init-session.sh <plan-slug>
#   plan-slug = short kebab-case description (e.g. replace-trace-emitter)
# Creates docs/plans/<YYYYMMDD>-<slug>/{task_plan.md, findings.md, progress.md}
# under the current git repo root (or PWD if not in a repo). Refuses to
# overwrite an existing plan dir.

set -e

if [ -z "$1" ]; then
    echo "Usage: $(basename "$0") <plan-slug>" >&2
    echo "Example: $(basename "$0") replace-trace-emitter" >&2
    exit 2
fi

SLUG_RAW="$1"
DATE=$(date +%Y-%m-%d)
DATE_PREFIX=$(date +%Y%m%d)
ROOT=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
SLUG="${DATE_PREFIX}-${SLUG_RAW}"
PLAN_DIR="$ROOT/docs/plans/${SLUG}"

if [ -e "$PLAN_DIR" ]; then
    echo "Refusing to overwrite existing plan: $PLAN_DIR" >&2
    exit 1
fi

mkdir -p "$PLAN_DIR"
echo "Initializing plan: $PLAN_DIR"

cat > "$PLAN_DIR/task_plan.md" << EOF
---
slug: ${SLUG}
status: active
started: ${DATE}
updated: ${DATE}
---

# Task Plan: [Brief Description]

## Goal
[One sentence describing the end state]

## Current Phase
Phase 1

## Phases

### Phase 1: Requirements & Discovery
- [ ] Understand user intent
- [ ] Identify constraints
- [ ] Document in findings.md
- **Status:** in_progress

### Phase 2: Planning & Structure
- [ ] Define approach
- [ ] Create project structure
- **Status:** pending

### Phase 3: Implementation
- [ ] Execute the plan
- [ ] Write to files before executing
- **Status:** pending

### Phase 4: Testing & Verification
- [ ] Verify requirements met
- [ ] Document test results
- **Status:** pending

### Phase 5: Delivery
- [ ] Review outputs
- [ ] Deliver to user
- **Status:** pending

## Decisions Made
| Decision | Rationale |
|----------|-----------|

## Errors Encountered
| Error | Resolution |
|-------|------------|
EOF
echo "Created task_plan.md"

cat > "$PLAN_DIR/findings.md" << 'EOF'
# Findings & Decisions

## Requirements
-

## Research Findings
-

## Technical Decisions
| Decision | Rationale |
|----------|-----------|

## Issues Encountered
| Issue | Resolution |
|-------|------------|

## Resources
-
EOF
echo "Created findings.md"

cat > "$PLAN_DIR/progress.md" << EOF
# Progress Log

## Session: $DATE

### Current Status
- **Phase:** 1 - Requirements & Discovery
- **Started:** $DATE

### Actions Taken
-

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|

### Errors
| Error | Resolution |
|-------|------------|
EOF
echo "Created progress.md"

echo ""
echo "Plan ready: $PLAN_DIR"
echo "Files: task_plan.md, findings.md, progress.md"
