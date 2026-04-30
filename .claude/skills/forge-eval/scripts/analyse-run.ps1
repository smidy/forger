#requires -Version 5.1

# Post-mortem analyser for a forge run. Reports what the current trace schema
# can actually answer, and explicitly names what it cannot.
#
# Usage:
#   analyse-run.ps1 -RunId <run-id> [-ForgeHome <path>]
#
# ForgeHome defaults to $env:FORGE_HOME then $env:USERPROFILE\.forge.

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [string] $RunId,

  [Parameter(Position = 1)]
  [string] $ForgeHome
)

$ErrorActionPreference = 'Stop'

if (-not $ForgeHome) {
  if ($env:FORGE_HOME) { $ForgeHome = $env:FORGE_HOME }
  else { $ForgeHome = Join-Path -Path $env:USERPROFILE -ChildPath '.forge' }
}

# PowerShell 5.1's Join-Path takes exactly two positional args (-Path, -ChildPath).
# Chain two calls rather than passing 3+ args; a triple-arg call produces a
# misleading "positional parameter cannot be found" error that looks like a
# param-binding failure on the outer script.
$runRoot = Join-Path -Path $ForgeHome -ChildPath 'runs'
$runRoot = Join-Path -Path $runRoot -ChildPath $RunId
if (-not (Test-Path $runRoot)) {
  throw "Run not found: $runRoot"
}

$tracePath  = Join-Path -Path $runRoot -ChildPath 'trace.jsonl'
$statusPath = Join-Path -Path $runRoot -ChildPath 'status.json'
$resultPath = Join-Path -Path $runRoot -ChildPath 'result.json'
$inputPath  = Join-Path -Path $runRoot -ChildPath 'input.json'

function Write-Section([string] $title) {
  Write-Host ''
  Write-Host "=== $title ===" -ForegroundColor Cyan
}

Write-Host ''
Write-Host "Forge run analysis: $RunId" -ForegroundColor Cyan
Write-Host "Root: $runRoot"

# --- Files present ---
Write-Section 'Files'
foreach ($pair in @(
  @('input.json',  $inputPath),
  @('status.json', $statusPath),
  @('result.json', $resultPath),
  @('trace.jsonl', $tracePath)
)) {
  $name = $pair[0]; $path = $pair[1]
  if (Test-Path $path) {
    $size = (Get-Item $path).Length
    Write-Host ("  {0,-14} {1,10:N0} bytes" -f $name, $size)
  }
  else {
    Write-Host ("  {0,-14} MISSING" -f $name) -ForegroundColor DarkYellow
  }
}

# --- Status ---
Write-Section 'Status'
if (Test-Path $statusPath) {
  $status = Get-Content $statusPath -Raw | ConvertFrom-Json
  if ($status.PSObject.Properties['status']) { Write-Host "  pipeline: $($status.status)" }
  if ($status.PSObject.Properties['stages']) {
    foreach ($stage in $status.stages) {
      Write-Host ("    stage {0,-24} {1}" -f $stage.id, $stage.status)
    }
  }
}
else {
  Write-Host '  no status.json' -ForegroundColor DarkYellow
}

# --- Result ---
Write-Section 'Result'
if (Test-Path $resultPath) {
  Write-Host '  result.json written (submit_final called OR pipeline completed)'
}
else {
  Write-Host '  result.json NOT written — agent did not call submit_final or pipeline failed' -ForegroundColor Yellow
}

# --- Diff verification (post-loop reconciliation) ---
# Parsed eagerly from the trace so Status can surface the verdict even when
# the later trace-event section never runs (e.g. missing trace.jsonl). One
# event is emitted per run at submit_final; rejecting drift is the point
# (see docs/Application/AgentRunner.md).
Write-Section 'Diff verification'
if (Test-Path $tracePath) {
  $diffEvents = Get-Content $tracePath |
    Where-Object { $_.Trim().Length -gt 0 } |
    ForEach-Object {
      try { $_ | ConvertFrom-Json } catch { $null }
    } |
    Where-Object { $_ -ne $null -and $_.kind -eq 'agent_diff_verification' }

  if (-not $diffEvents) {
    Write-Host '  no agent_diff_verification event — agent did not reach submit_final' -ForegroundColor DarkYellow
  }
  else {
    foreach ($dv in $diffEvents) {
      switch ($dv.verdict) {
        'pass'    { $color = 'Green' }
        'skipped' { $color = 'DarkYellow' }
        default   { $color = 'Yellow' }
      }
      Write-Host ("  verdict: {0}" -f $dv.verdict) -ForegroundColor $color
      $declaredCount = if ($dv.declared) { @($dv.declared).Count } else { 0 }
      $writtenCount = if ($dv.actuallyWritten) { @($dv.actuallyWritten).Count } else { 0 }
      Write-Host ("  declared: {0}   actually-written: {1}" -f $declaredCount, $writtenCount)
      if ($dv.missing -and @($dv.missing).Count -gt 0) {
        Write-Host '  missing (declared but never written):' -ForegroundColor Yellow
        foreach ($m in $dv.missing) { Write-Host ("    - {0}" -f $m) -ForegroundColor Yellow }
      }
      if ($dv.extra -and @($dv.extra).Count -gt 0) {
        Write-Host '  extra (written but not declared):' -ForegroundColor Yellow
        foreach ($x in $dv.extra) { Write-Host ("    - {0}" -f $x) -ForegroundColor Yellow }
      }
    }
  }
}
else {
  Write-Host '  no trace.jsonl — cannot report verdict' -ForegroundColor DarkYellow
}

# --- Trace ---
Write-Section 'Trace events'
if (-not (Test-Path $tracePath)) {
  Write-Host '  no trace.jsonl'
}
else {
  $rawLines = Get-Content $tracePath | Where-Object { $_.Trim().Length -gt 0 }
  $events = foreach ($line in $rawLines) {
    try { $line | ConvertFrom-Json } catch { $null }
  }
  $events = $events | Where-Object { $_ -ne $null }

  if ($events.Count -eq 0) {
    Write-Host '  trace empty'
  }
  else {
    # Wall time
    $ts = foreach ($e in $events) {
      if ($e.PSObject.Properties['timestamp']) {
        try { [DateTimeOffset]::Parse($e.timestamp) } catch { $null }
      }
    }
    $ts = $ts | Where-Object { $_ -ne $null } | Sort-Object
    if ($ts.Count -ge 2) {
      $wall = $ts[-1] - $ts[0]
      Write-Host ("  wall time    : {0:hh\:mm\:ss\.fff}" -f $wall)
    }
    Write-Host ("  total events : {0}" -f $events.Count)

    # Kinds
    $byKind = $events | Group-Object -Property kind | Sort-Object Count -Descending
    Write-Host '  by kind:'
    foreach ($g in $byKind) {
      Write-Host ("    {0,-26} {1,5}" -f $g.Name, $g.Count)
    }

    # Truncations
    $truncs = $events | Where-Object { $_.kind -eq 'tool_result_truncated' }
    if ($truncs) {
      $total = ($truncs | Measure-Object -Property bytes -Sum).Sum
      Write-Section 'Truncations (tool output exceeded cap)'
      Write-Host ("  count: {0}  total: {1:N0} bytes" -f $truncs.Count, $total)
      foreach ($t in $truncs) {
        Write-Host ("    - {0,10:N0} bytes → {1}" -f $t.bytes, $t.artifactPath)
      }
    }

    # Fs denials
    $denials = $events | Where-Object { $_.kind -eq 'fs_denied' }
    if ($denials) {
      Write-Section 'Fs denials (scope violations — always investigate)'
      foreach ($d in $denials) {
        $resolved = if ($d.PSObject.Properties['resolvedPath']) { $d.resolvedPath } else { '<unresolved>' }
        Write-Host ("  - {0} {1}  reason={2}  resolved={3}" -f $d.mode, $d.requestedPath, $d.reason, $resolved) -ForegroundColor Yellow
      }
    }

    # Plan drift
    $drift = $events | Where-Object { $_.kind -eq 'plan_drift' }
    if ($drift) {
      Write-Section 'Plan drift'
      foreach ($d in $drift) {
        $agents = $d.changedAgents -join ', '
        Write-Host ("  - changed agents: {0}" -f $agents)
      }
    }
  }
}

# --- Stage directory shape ---
Write-Section 'Stages on disk'
$stagesRoot = Join-Path $runRoot 'stages'
if (Test-Path $stagesRoot) {
  foreach ($stage in Get-ChildItem $stagesRoot -Directory) {
    $iterRoot = Join-Path $stage.FullName 'iterations'
    $iterCount = if (Test-Path $iterRoot) { (Get-ChildItem $iterRoot -Directory).Count } else { 0 }
    $artRoot = Join-Path $stage.FullName 'tool-outputs'
    $artCount = if (Test-Path $artRoot) { (Get-ChildItem $artRoot -File).Count } else { 0 }
    Write-Host ("  {0,-28} fan-out iter: {1,3}  truncated artifacts: {2,3}" -f $stage.Name, $iterCount, $artCount)
  }
}
else {
  Write-Host '  no stages/ directory (agent-only run, not a pipeline)'
}

# --- Derived metrics from the new trace events (post-F5) ---
if (Test-Path $tracePath) {
  $toolCalls = $events | Where-Object { $_.kind -eq 'tool_call' }
  if ($toolCalls) {
    Write-Section 'Tool calls'
    # Per-tool redundancy: a call is "redundant" when its (toolName, argsHash)
    # pair has already appeared in this run — i.e. unique-argsHash < total-calls.
    # F4 of eval-p6-fixes.md surfaced this: 60% of read_file calls were exact
    # duplicates. Highlight the ratio so the signal is unmissable at a glance.
    $byTool = $toolCalls | Group-Object -Property toolName | Sort-Object Count -Descending
    foreach ($g in $byTool) {
      $totalMs = ($g.Group | Measure-Object -Property durationMs -Sum).Sum
      $uniqueArgs = ($g.Group | Where-Object { $_.argsHash } | Group-Object -Property argsHash).Count
      $hashedCalls = @($g.Group | Where-Object { $_.argsHash }).Count
      $redundantCount = if ($hashedCalls -gt 0) { $hashedCalls - $uniqueArgs } else { 0 }
      if ($redundantCount -gt 0 -and $hashedCalls -gt 0) {
        $pct = [int](($redundantCount / $hashedCalls) * 100)
        Write-Host ("  {0,-18} calls={1,3}  total={2,6} ms  redundant={3,2} ({4,2}%)" -f $g.Name, $g.Count, $totalMs, $redundantCount, $pct) -ForegroundColor Yellow
      }
      else {
        Write-Host ("  {0,-18} calls={1,3}  total={2,6} ms" -f $g.Name, $g.Count, $totalMs)
      }
    }

    $failedCount = @($toolCalls | Where-Object { $_.error }).Count
    if ($failedCount -gt 0) {
      Write-Host ("  (errors: {0})" -f $failedCount) -ForegroundColor Yellow
    }

    # Redundancy detail — same tool + same argsHash called ≥2 times.
    # The per-tool summary above reports the count/percentage; this block
    # lists the specific arg hashes so a reviewer can trace which files
    # the agent kept re-reading.
    $redundant = $toolCalls |
      Group-Object -Property { "$($_.toolName)|$($_.argsHash)" } |
      Where-Object { $_.Count -ge 2 }
    if ($redundant) {
      Write-Host ''
      Write-Host '  Redundant calls (same tool + same args):' -ForegroundColor Yellow
      foreach ($r in $redundant) {
        $parts = $r.Name -split '\|', 2
        Write-Host ("    - {0} x{1} (args {2})" -f $parts[0], $r.Count, $parts[1])
      }
    }
  }

  $llmCalls = $events | Where-Object { $_.kind -eq 'llm_call' }
  if ($llmCalls) {
    Write-Section 'LLM calls'
    $totalLlmMs = ($llmCalls | Measure-Object -Property durationMs -Sum).Sum
    $promptSum = ($llmCalls | Where-Object { $_.promptTokens } | Measure-Object -Property promptTokens -Sum).Sum
    $complSum = ($llmCalls | Where-Object { $_.completionTokens } | Measure-Object -Property completionTokens -Sum).Sum
    Write-Host ("  calls={0}  total={1} ms  prompt={2:N0} tokens  completion={3:N0} tokens" -f $llmCalls.Count, $totalLlmMs, $promptSum, $complSum)
  }

  $iterations = ($events | Where-Object { $_.kind -eq 'agent_iteration' }).Count
  if ($iterations -gt 0) {
    Write-Host ''
    Write-Host ("Iterations: {0}" -f $iterations)
  }
}

# --- State snapshots (from agent-resume-from-max-iterations landing) ---
# Per-iter state.json carries full messages + ledger. Use it to classify
# the behaviour pattern at the tail of a failed run before forming a
# hypothesis. See references/failure-fingerprints.md for bucket mapping.
Write-Section 'State snapshots'

function Get-IterSlice {
  # Each state.json is CUMULATIVE — messages up to end-of-iter.
  # The tail of the messages array for iter K is:
  #   [..., iter-K-assistant (with tool_calls), iter-K-tool-result-1, iter-K-tool-result-2, ...]
  # So "what did THIS iter do" = last assistant's tool_calls + tool messages after it.
  param($state)
  $msgs = @()
  if ($state.PSObject.Properties['messages']) { $msgs = @($state.messages) }

  $lastAsstIdx = -1
  for ($i = $msgs.Count - 1; $i -ge 0; $i--) {
    if ($msgs[$i].PSObject.Properties['role'] -and $msgs[$i].role -eq 'assistant') {
      $lastAsstIdx = $i
      break
    }
  }

  $calls = @()
  if ($lastAsstIdx -ge 0 -and $msgs[$lastAsstIdx].PSObject.Properties['tool_calls']) {
    foreach ($tc in @($msgs[$lastAsstIdx].tool_calls)) {
      if ($tc.PSObject.Properties['function'] -and $tc.function.PSObject.Properties['name']) {
        $calls += [string] $tc.function.name
      }
    }
  }

  $errors = 0
  $ctxMismatch = 0
  $submitReject = 0
  for ($i = $lastAsstIdx + 1; $i -lt $msgs.Count; $i++) {
    if (-not $msgs[$i].PSObject.Properties['role']) { continue }
    if ($msgs[$i].role -ne 'tool') { continue }
    if (-not $msgs[$i].PSObject.Properties['content']) { continue }
    $c = [string] $msgs[$i].content
    $isErr = ($c -match '"error"' -or $c -match 'context did not match')
    if ($isErr) {
      $errors++
      if ($c -match 'context did not match') { $ctxMismatch++ }
      # tool_call_id pattern lets us correlate; simpler heuristic is the content mentions submit_final.
      if ($c -match 'submit_final' -or $c -match 'output_schema') { $submitReject++ }
    }
  }

  return [pscustomobject]@{
    Calls = $calls
    Errors = $errors
    CtxMismatch = $ctxMismatch
    SubmitReject = $submitReject
  }
}

function Get-FailureFingerprint {
  param([object[]] $Window)
  if ($Window.Count -lt 2) { return 'stalled - too few iterations to classify' }

  $readOnly = @('read_file','read_file_slice','grep','glob','fetch_url','web_search')
  $writeLike = @('apply_patch','write_repo_file','write_workspace_artifact','write_file')

  $calls = @()
  $toolErrorsPerIter = @()
  $patchErrorCount = 0
  $submitRejectCount = 0

  foreach ($sn in $Window) {
    $slice = Get-IterSlice -state $sn.State
    $calls += $slice.Calls
    $toolErrorsPerIter += $slice.Errors
    $patchErrorCount += $slice.CtxMismatch
    $submitRejectCount += $slice.SubmitReject
  }

  if ($calls.Count -eq 0) { return 'stalled - no tool calls in window' }

  $readCount = @($calls | Where-Object { $readOnly -contains $_ }).Count
  $writeCount = @($calls | Where-Object { $writeLike -contains $_ }).Count
  $submitCount = @($calls | Where-Object { $_ -eq 'submit_final' }).Count
  $patchCount = @($calls | Where-Object { $_ -eq 'apply_patch' }).Count

  if ($writeCount -eq 0 -and ($readCount / $calls.Count) -ge 0.8) {
    return "read-loop - $readCount/$($calls.Count) calls are read-only, 0 writes in window"
  }
  if ($patchCount -ge 3 -and $patchErrorCount -ge 2) {
    return "patch-reject-loop - $patchCount apply_patch / $patchErrorCount context-mismatch in window"
  }
  if ($submitCount -ge 2 -or $submitRejectCount -ge 2) {
    return "schema-fight - $submitCount submit_final attempt(s), $submitRejectCount rejection(s) in window"
  }
  if ($toolErrorsPerIter.Count -ge 3) {
    $firstErr = $toolErrorsPerIter[0]
    $lastErr = $toolErrorsPerIter[-1]
    if ($lastErr -gt $firstErr -and $lastErr -ge 2) {
      return "tool-error-spiral - errors $firstErr -> $lastErr across window"
    }
  }
  return 'stalled - ran to cap without a classifiable pattern'
}

$stagesRootSnap = Join-Path -Path $runRoot -ChildPath 'stages'
$allSnaps = @()
if (Test-Path $stagesRootSnap) {
  foreach ($stage in Get-ChildItem $stagesRootSnap -Directory) {
    $iterRoot = Join-Path -Path $stage.FullName -ChildPath 'iterations'
    if (-not (Test-Path $iterRoot)) { continue }
    foreach ($id in Get-ChildItem $iterRoot -Directory | Sort-Object Name) {
      $sp = Join-Path -Path $id.FullName -ChildPath 'state.json'
      if (-not (Test-Path $sp)) { continue }
      try {
        $st = Get-Content $sp -Raw | ConvertFrom-Json
        $ledgerCount = if ($st.PSObject.Properties['ledger']) { @($st.ledger).Count } else { 0 }
        $msgCount = if ($st.PSObject.Properties['messages']) { @($st.messages).Count } else { 0 }
        $iterVal = if ($st.PSObject.Properties['iter']) { [int] $st.iter } else { [int] $id.Name }
        $allSnaps += [pscustomobject]@{
          StageId = $stage.Name
          Iter = $iterVal
          Path = $sp
          MsgCount = $msgCount
          LedgerCount = $ledgerCount
          SizeBytes = (Get-Item $sp).Length
          State = $st
        }
      } catch {
        # Skip torn / unreadable snapshot; keep scanning.
      }
    }
  }
}

if ($allSnaps.Count -eq 0) {
  Write-Host '  no state.json snapshots - pre-snapshot build, or stage never iterated'
} else {
  $byStage = @($allSnaps | Group-Object StageId)
  Write-Host ("  snapshots: {0} (across {1} stage(s))" -f $allSnaps.Count, $byStage.Count)
  foreach ($sg in $byStage) {
    $ordered = @($sg.Group | Sort-Object Iter)
    $terminal = $ordered[-1]
    Write-Host ''
    Write-Host ("  stage {0} - iters {1}..{2}, terminal snapshot {3:N0} bytes, {4} messages, {5} ledger entries" -f `
      $sg.Name, $ordered[0].Iter, $terminal.Iter, $terminal.SizeBytes, $terminal.MsgCount, $terminal.LedgerCount)

    $prevLedger = 0
    foreach ($sn in $ordered) {
      $msgs = @()
      if ($sn.State.PSObject.Properties['messages']) { $msgs = @($sn.State.messages) }
      $lastAsst = $msgs | Where-Object { $_.PSObject.Properties['role'] -and $_.role -eq 'assistant' } | Select-Object -Last 1
      $seq = '<no calls>'
      if ($lastAsst -and $lastAsst.PSObject.Properties['tool_calls']) {
        $names = @()
        foreach ($tc in @($lastAsst.tool_calls)) {
          if ($tc.PSObject.Properties['function'] -and $tc.function.PSObject.Properties['name']) {
            $names += [string] $tc.function.name
          }
        }
        if ($names.Count -gt 0) { $seq = $names -join ',' }
      }
      $delta = $sn.LedgerCount - $prevLedger
      $prevLedger = $sn.LedgerCount
      $deltaStr = if ($delta -gt 0) { "  (+$delta write)" } else { '' }
      Write-Host ("    iter {0,3}: {1}{2}" -f $sn.Iter, $seq, $deltaStr)
    }

    $runCompleted = ($status -and $status.status -eq 'completed' -and (Test-Path $resultPath))
    if ($runCompleted) {
      Write-Host ''
      Write-Host '  fingerprint: completed - submit_final ok' -ForegroundColor Green
    }
    else {
      $window = @($ordered | Select-Object -Last 5)
      $fp = Get-FailureFingerprint -Window $window
      if ($fp) {
        Write-Host ''
        Write-Host ("  fingerprint: {0}" -f $fp) -ForegroundColor Yellow
        Write-Host '  (see .claude/skills/forge-eval/references/failure-fingerprints.md for bucket mapping)'
      }
    }
  }
}

Write-Host ''
