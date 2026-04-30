# Initialize planning files for a new plan in docs/plans/<YYYYMMDD>-<slug>/.
# Usage: .\init-session.ps1 <plan-slug>
#   plan-slug = short kebab-case description (e.g. replace-trace-emitter)
# Creates docs/plans/<YYYYMMDD>-<slug>/{task_plan.md, findings.md, progress.md}
# under the current git repo root (or PWD if not in a repo). Refuses to
# overwrite an existing plan dir.

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Slug
)

$DATE = Get-Date -Format "yyyy-MM-dd"
$DATE_PREFIX = Get-Date -Format "yyyyMMdd"

try {
    $ROOT = (& git rev-parse --show-toplevel 2>$null | Out-String).Trim()
} catch {
    $ROOT = ""
}
if (-not $ROOT) { $ROOT = (Get-Location).Path }

$FullSlug = "$DATE_PREFIX-$Slug"
$PlanDir = Join-Path $ROOT "docs/plans/$FullSlug"

if (Test-Path $PlanDir) {
    Write-Error "Refusing to overwrite existing plan: $PlanDir"
    exit 1
}

New-Item -ItemType Directory -Path $PlanDir -Force | Out-Null
Write-Host "Initializing plan: $PlanDir"

$taskPlanPath = Join-Path $PlanDir "task_plan.md"
@"
---
slug: $FullSlug
status: active
started: $DATE
updated: $DATE
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
"@ | Out-File -FilePath $taskPlanPath -Encoding utf8
Write-Host "Created task_plan.md"

$findingsPath = Join-Path $PlanDir "findings.md"
@'
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
'@ | Out-File -FilePath $findingsPath -Encoding utf8
Write-Host "Created findings.md"

$progressPath = Join-Path $PlanDir "progress.md"
@"
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
"@ | Out-File -FilePath $progressPath -Encoding utf8
Write-Host "Created progress.md"

Write-Host ""
Write-Host "Plan ready: $PlanDir"
Write-Host "Files: task_plan.md, findings.md, progress.md"
