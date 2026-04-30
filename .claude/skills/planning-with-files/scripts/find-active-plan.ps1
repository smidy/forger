# Find the most recently updated active plan directory.
# Reads YAML frontmatter from docs/plans/*/task_plan.md (excluding _archive/),
# filters status: active, sorts by `updated:` desc, prints the dir of the head
# match. Exits 1 if no active plan exists.
#
# Usage: find-active-plan.ps1 [-Root <project-root>]
#   Root defaults to `git rev-parse --show-toplevel` or PWD.

param(
    [string]$Root = ""
)

if (-not $Root) {
    try {
        $Root = (& git rev-parse --show-toplevel 2>$null | Out-String).Trim()
    } catch {
        $Root = ""
    }
    if (-not $Root) { $Root = (Get-Location).Path }
}

$plansDir = Join-Path $Root "docs/plans"
if (-not (Test-Path $plansDir)) { exit 1 }

$candidates = Get-ChildItem -Path $plansDir -Directory -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -ne "_archive" -and $_.Name -ne "_template" -and (Test-Path (Join-Path $_.FullName "task_plan.md"))
}

$best = $null
foreach ($d in $candidates) {
    $planFile = Join-Path $d.FullName "task_plan.md"
    $content = Get-Content $planFile -Raw -ErrorAction SilentlyContinue
    if (-not $content) { continue }

    if ($content -match "(?s)^---\r?\n(.*?)\r?\n---") {
        $frontmatter = $Matches[1]
        $status = if ($frontmatter -match "(?m)^status:\s*(.+)$") { $Matches[1].Trim() } else { "" }
        $updated = if ($frontmatter -match "(?m)^updated:\s*(.+)$") { $Matches[1].Trim() } else { "" }

        if ($status -eq "active") {
            if ($null -eq $best -or $updated -gt $best.Updated) {
                $best = [PSCustomObject]@{ Dir = $d.FullName; Updated = $updated }
            }
        }
    }
}

if ($null -eq $best) { exit 1 }
Write-Output $best.Dir
