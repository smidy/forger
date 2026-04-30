#requires -Version 5.1

# Qualitative post-mortem for a Forge agent run. Samples the LLM's own
# `assistant` messages (reasoning_content + content + tool_calls) from the
# terminal state.json, joins against trace.jsonl for Forge-computed argsHash,
# detects stuck loops on two axes (arg stability + result stability), and
# emits bucket hints for Phase 7 findings.
#
# Complements scripts/analyse-run.ps1 (which answers "what happened"). This
# script answers "why did the agent struggle".
#
# Usage:
#   analyse-reasoning.ps1 -RunId <run-id> [-ForgeHome <path>] [-StageId <id>]
#                         [-SampleStrategy quick|full] [-SampleCap 25]
#                         [-StuckLoopThreshold 20]
#                         [-OutFormat text|jsonl|findings-scaffold]
#                         [-RedactAbsolutePaths]

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [string] $RunId,

  [Parameter(Position = 1)]
  [string] $ForgeHome,

  [string] $StageId,
  [ValidateSet('quick','full')] [string] $SampleStrategy = 'quick',
  [int] $SampleCap = 25,
  [int] $StuckLoopThreshold = 20,
  [ValidateSet('text','jsonl','findings-scaffold')] [string] $OutFormat = 'text',
  [switch] $RedactAbsolutePaths
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $ForgeHome) {
  if ($env:FORGE_HOME) { $ForgeHome = $env:FORGE_HOME }
  else { $ForgeHome = Join-Path -Path $env:USERPROFILE -ChildPath '.forge' }
}

$runRoot = Join-Path -Path $ForgeHome -ChildPath 'runs'
$runRoot = Join-Path -Path $runRoot   -ChildPath $RunId
if (-not (Test-Path $runRoot)) { throw "Run not found: $runRoot" }

$tracePath  = Join-Path -Path $runRoot -ChildPath 'trace.jsonl'
$stagesRoot = Join-Path -Path $runRoot -ChildPath 'stages'

# --- Utilities ---------------------------------------------------------------

function Redact([string] $s) {
  if (-not $RedactAbsolutePaths -or -not $s) { return $s }
  $userHome = [Environment]::GetFolderPath('UserProfile')
  if ($userHome) { $s = $s.Replace($userHome, '<HOME>') }
  # Windows-style absolute repo paths
  $s = [regex]::Replace($s, '[A-Za-z]:[\\/][^\s"'']*forge[\\/][^\s"'']*', '<REPO>/...')
  # POSIX-style absolute paths
  $s = [regex]::Replace($s, '(?<![\w])/(?:home|Users|work|tmp)/[^\s"'']*', '<PATH>')
  return $s
}

function Get-AgentToolList([string] $runRoot) {
  # Resolve the agent under test from plan.json -> agent YAML and parse its
  # `tools:` list. Returns an array of tool names (possibly empty). Falls back
  # to an empty array on any I/O or parse error so the caller can degrade
  # gracefully.
  $planPath = Join-Path -Path $runRoot -ChildPath 'plan.json'
  if (-not (Test-Path $planPath)) { return @() }
  try {
    $plan = Get-Content $planPath -Raw | ConvertFrom-Json
    if (-not $plan.agents -or $plan.agents.Count -eq 0) { return @() }
    $agentPath = $plan.agents[0].resolvedPath
    if (-not $agentPath -or -not (Test-Path $agentPath)) { return @() }
    $yamlText = Get-Content $agentPath -Raw
    if ($yamlText -match 'tools:\s*\[([^\]]*)\]') {
      return @($matches[1] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    if ($yamlText -match '(?ms)^tools:\s*$([\s\S]*?)(^\S|\Z)') {
      $block = $matches[1]
      return @([regex]::Matches($block, '-\s*([\w_]+)') | ForEach-Object { $_.Groups[1].Value })
    }
    return @()
  } catch {
    return @()
  }
}

function Hash-Prefix([string] $s) {
  # Hash a *structural* signature of the tool-result prefix so that runs of
  # varying-in-details-but-same-in-shape output (e.g. build errors with UUIDs,
  # scratch paths, line numbers) collapse to the same hash. Raw-prefix hashing
  # gave near-100% unique results in the motivating run because every bash
  # stdout carried fresh UUIDs / paths / counters, masking the MSBuild stuck
  # loop entirely.
  if (-not $s) { return '' }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    $prefix = $s.Substring(0, [Math]::Min(1024, $s.Length))
    # Normalisation order matters — broader patterns first.
    $prefix = [regex]::Replace($prefix, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', 'UUID')
    $prefix = [regex]::Replace($prefix, '[A-Za-z]:[\\/][^\s"'']*', 'PATH')
    $prefix = [regex]::Replace($prefix, '(?<![\w])/[\w./-]+', 'PATH')
    # Collapse only *long* numeric runs (timestamps, byte counts). Short indices
    # like file0/file1/file2 carry real signal — zeroing them collapses a
    # legitimate file-sweep into a bogus stuck-loop.
    $prefix = [regex]::Replace($prefix, '\d{4,}', 'N')
    # Collapse runs of whitespace and escaped whitespace chars.
    $prefix = [regex]::Replace($prefix, '(\\n|\\r|\\t|\s)+', ' ')
    $sig = $prefix.Substring(0, [Math]::Min(256, $prefix.Length))
    $bytes = [Text.Encoding]::UTF8.GetBytes($sig)
    $h = $sha.ComputeHash($bytes)
    return ([BitConverter]::ToString($h, 0, 8)).Replace('-','').ToLowerInvariant()
  } finally { $sha.Dispose() }
}

function Get-Reasoning([object] $asst) {
  # Dispatch per backend shape. Returns (text, kind).
  if ($asst.PSObject.Properties['reasoning_content'] -and $asst.reasoning_content) {
    return @([string]$asst.reasoning_content, 'reasoning_content')
  }
  if ($asst.PSObject.Properties['thinking']) {
    $chunks = @()
    foreach ($t in @($asst.thinking)) {
      if ($t.PSObject.Properties['thinking']) { $chunks += [string]$t.thinking }
      elseif ($t -is [string]) { $chunks += $t }
    }
    if ($chunks.Count -gt 0) { return @(($chunks -join "`n"), 'thinking[]') }
  }
  return @('', 'none')
}

function Truncate([string] $s, [int] $n) {
  if (-not $s) { return '' }
  if ($s.Length -le $n) { return $s }
  return $s.Substring(0, $n) + '…'
}

# --- Load trace (argsHash + promptTokens indices) ----------------------------

$traceByCallId = @{}
$llmByIter = @{}
$toolResultByCallId = @{}  # captured from state.json later; trace doesn't carry result bytes

if (Test-Path $tracePath) {
  [System.IO.File]::ReadAllLines($tracePath, [System.Text.UTF8Encoding]::new($false)) | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object {
    try { $e = $_ | ConvertFrom-Json } catch { return }
    if (-not $e.PSObject.Properties['kind']) { return }
    if ($e.kind -eq 'tool_call' -and $e.PSObject.Properties['callId']) {
      $traceByCallId[[string]$e.callId] = $e
    }
    elseif ($e.kind -eq 'llm_call') {
      # iter is not stamped on llm_call directly; order is preserved, so
      # index by arrival order and match later on assistant ordinal.
      if (-not $llmByIter.ContainsKey('_seq')) { $llmByIter['_seq'] = New-Object System.Collections.ArrayList }
      [void] $llmByIter['_seq'].Add($e)
    }
  }
}

# --- Enumerate stages --------------------------------------------------------

$stageDirs = @()
if (Test-Path $stagesRoot) {
  foreach ($d in Get-ChildItem $stagesRoot -Directory) {
    if ($StageId -and $d.Name -ne $StageId) { continue }
    $stageDirs += $d
  }
}
if ($stageDirs.Count -eq 0) {
  throw "No stages found under $stagesRoot" + $(if ($StageId) { " (filter: StageId=$StageId)" } else { '' })
}

# --- Core analysis per stage -------------------------------------------------

function Analyse-Stage {
  param([System.IO.DirectoryInfo] $StageDir)

  $iterRoot = Join-Path -Path $StageDir.FullName -ChildPath 'iterations'
  if (-not (Test-Path $iterRoot)) { return $null }

  # Walk iter dirs from highest down to first; use the first that has a
  # readable state.json. Pre-2026-04-27, the submit_final iter did not snapshot
  # (the loop returned early) — successful runs left their highest iter with
  # only reasoning.txt and the analyser silently produced no output. We still
  # walk back defensively for runs predating that fix and for write-tear
  # scenarios (Ctrl-C mid-snapshot, disk full, etc.).
  $iterDirs = @(Get-ChildItem $iterRoot -Directory | Sort-Object Name)
  if ($iterDirs.Count -eq 0) { return $null }
  $terminalIter = $null
  $statePath = $null
  for ($i = $iterDirs.Count - 1; $i -ge 0; $i--) {
    $candidate = Join-Path -Path $iterDirs[$i].FullName -ChildPath 'state.json'
    if (Test-Path $candidate) {
      $terminalIter = $iterDirs[$i]
      $statePath = $candidate
      break
    }
  }
  if ($null -eq $statePath) { return $null }
  if ($terminalIter.Name -ne $iterDirs[-1].Name) {
    Write-Warning ("note: terminal iter {0} has no state.json; falling back to iter {1}." -f $iterDirs[-1].Name, $terminalIter.Name)
  }

  # Explicit UTF-8 read so non-ASCII reasoning content (arrows, CJK) doesn't
  # mojibake on Windows (default Get-Content uses the active ANSI code page).
  $state = [System.IO.File]::ReadAllText($statePath, [System.Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
  $msgs = @()
  if ($state.PSObject.Properties['messages']) { $msgs = @($state.messages) }

  # Role histogram
  $roles = @{ system = 0; user = 0; assistant = 0; tool = 0 }
  foreach ($m in $msgs) {
    if (-not $m.PSObject.Properties['role']) { continue }
    $r = [string]$m.role
    if ($roles.ContainsKey($r)) { $roles[$r]++ } else { $roles[$r] = 1 }
  }

  # Build tool_result map by tool_call_id (from state.json — trace doesn't carry content).
  $toolResultByCallId = @{}
  foreach ($m in $msgs) {
    if ($m.role -ne 'tool') { continue }
    if (-not $m.PSObject.Properties['tool_call_id']) { continue }
    $c = if ($m.PSObject.Properties['content']) { [string]$m.content } else { '' }
    $toolResultByCallId[[string]$m.tool_call_id] = $c
  }

  # Ordered list of assistants with joined metadata.
  $asstIndex = 0
  $assistants = @()
  $reasoningPresent = 0
  $reasoningKinds = @{}
  foreach ($m in $msgs) {
    if ($m.role -ne 'assistant') { continue }
    $pair = Get-Reasoning $m
    $rText = $pair[0]; $rKind = $pair[1]
    if ($rKind -ne 'none') { $reasoningPresent++ }
    if ($reasoningKinds.ContainsKey($rKind)) { $reasoningKinds[$rKind]++ } else { $reasoningKinds[$rKind] = 1 }

    $calls = @()
    if ($m.PSObject.Properties['tool_calls']) {
      foreach ($tc in @($m.tool_calls)) {
        $callId = if ($tc.PSObject.Properties['id']) { [string]$tc.id } else { '' }
        $name = ''
        $args = ''
        if ($tc.PSObject.Properties['function']) {
          if ($tc.function.PSObject.Properties['name']) { $name = [string]$tc.function.name }
          if ($tc.function.PSObject.Properties['arguments']) { $args = [string]$tc.function.arguments }
        }
        $hash = ''
        if ($callId -and $traceByCallId.ContainsKey($callId)) {
          $hash = [string]$traceByCallId[$callId].argsHash
        }
        $resultPrefix = if ($toolResultByCallId.ContainsKey($callId)) { $toolResultByCallId[$callId] } else { '' }
        $calls += [pscustomobject]@{
          CallId = $callId
          Name = $name
          Args = $args
          ArgsHash = $hash
          ResultHash = Hash-Prefix $resultPrefix
          ResultPreview = Truncate $resultPrefix 160
        }
      }
    }

    $assistants += [pscustomobject]@{
      Iter = $asstIndex
      Content = if ($m.PSObject.Properties['content']) { [string]$m.content } else { '' }
      ReasoningText = $rText
      ReasoningKind = $rKind
      Calls = $calls
    }
    $asstIndex++
  }

  # Prompt-token growth from llm_call events (one per assistant turn, in order).
  $llmSeq = if ($llmByIter.ContainsKey('_seq')) { @($llmByIter['_seq']) } else { @() }
  $promptCurve = @()
  for ($i = 0; $i -lt $assistants.Count; $i++) {
    $pt = $null
    if ($i -lt $llmSeq.Count -and $llmSeq[$i].PSObject.Properties['promptTokens']) {
      $pt = [int]$llmSeq[$i].promptTokens
    }
    $promptCurve += [pscustomobject]@{ Iter = $i; PromptTokens = $pt }
  }

  # Adaptive windowed tool histogram
  $n = $assistants.Count
  $windowSize = if ($n -lt 80) { 10 } else { [int][Math]::Ceiling($n / 10.0) }
  $histWindows = @()
  for ($start = 0; $start -lt $n; $start += $windowSize) {
    $end = [Math]::Min($start + $windowSize - 1, $n - 1)
    $bag = @{}
    for ($i = $start; $i -le $end; $i++) {
      foreach ($c in $assistants[$i].Calls) {
        if (-not $c.Name) { continue }
        if ($bag.ContainsKey($c.Name)) { $bag[$c.Name]++ } else { $bag[$c.Name] = 1 }
      }
    }
    $histWindows += [pscustomobject]@{
      Start = $start; End = $end
      Counts = $bag
    }
  }

  # Whole-run tool histogram
  $wholeBag = @{}
  foreach ($a in $assistants) {
    foreach ($c in $a.Calls) {
      if (-not $c.Name) { continue }
      if ($wholeBag.ContainsKey($c.Name)) { $wholeBag[$c.Name]++ } else { $wholeBag[$c.Name] = 1 }
    }
  }

  # Two-axis stuck-loop detector (contiguous spans).
  $stuckSpans = @()
  if ($n -ge $StuckLoopThreshold) {
    $inSpan = $false; $spanStart = -1
    for ($i = $StuckLoopThreshold; $i -lt $n; $i++) {
      $winStart = $i - $StuckLoopThreshold
      $winEnd = $i
      $mid = $winStart + [int][Math]::Floor(($winEnd - $winStart) / 2)
      $hA = @(); $hB = @(); $res = @()
      for ($j = $winStart; $j -lt $mid; $j++) {
        foreach ($c in $assistants[$j].Calls) { if ($c.ArgsHash) { $hA += $c.ArgsHash }; if ($c.ResultHash) { $res += $c.ResultHash } }
      }
      for ($j = $mid; $j -le $winEnd; $j++) {
        foreach ($c in $assistants[$j].Calls) { if ($c.ArgsHash) { $hB += $c.ArgsHash }; if ($c.ResultHash) { $res += $c.ResultHash } }
      }
      $setA = @($hA | Select-Object -Unique); $setB = @($hB | Select-Object -Unique)
      $inter = @($setA | Where-Object { $setB -contains $_ }).Count
      $union = @(($setA + $setB) | Select-Object -Unique).Count
      $jaccard = if ($union -gt 0) { [double]$inter / $union } else { 0 }
      $resUnique = @($res | Select-Object -Unique).Count
      $stability = if ($res.Count -gt 0) { 1 - ([double]$resUnique / $res.Count) } else { 0 }
      # Flag on either pathology:
      #   (A) high arg-repeat AND high result-repeat — classic same-call-same-result loop.
      #   (B) high result-repeat alone (>= 0.7) — "different attempts, same failure" (e.g. MSBuild
      #       fight where every bash variant returns the same build error). Arg-stability stays
      #       low because the agent keeps permuting flags, but progress is zero.
      # A pure-jaccard false-positive (polling that eventually returns) is rejected by (B)'s
      # higher stability bar; a pure-stability false-positive (a legitimate sweep) is rejected
      # because distinct files produce distinct content, not duplicates.
      $flag = (($jaccard -gt 0.7 -and $stability -gt 0.5) -or ($stability -gt 0.7))
      if ($flag -and -not $inSpan) { $inSpan = $true; $spanStart = $winStart }
      if (-not $flag -and $inSpan) {
        $stuckSpans += [pscustomobject]@{ Start = $spanStart; End = $i - 1; Jaccard = $jaccard; Stability = $stability }
        $inSpan = $false
      }
    }
    if ($inSpan) { $stuckSpans += [pscustomobject]@{ Start = $spanStart; End = $n - 1; Jaccard = -1.0; Stability = -1.0 } }
  }

  # Dominant tool per span (for label).
  foreach ($sp in $stuckSpans) {
    $local = @{}
    for ($i = $sp.Start; $i -le $sp.End; $i++) {
      foreach ($c in $assistants[$i].Calls) {
        if (-not $c.Name) { continue }
        if ($local.ContainsKey($c.Name)) { $local[$c.Name]++ } else { $local[$c.Name] = 1 }
      }
    }
    $dom = ($local.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1)
    $sp | Add-Member -NotePropertyName DominantTool -NotePropertyValue ($(if ($dom) { $dom.Key } else { '(none)' }))
  }

  # Sample selection with priority: tail > stuck-loop boundaries > per-tool entry > opening.
  $sampleIdx = New-Object System.Collections.Generic.HashSet[int]
  $ordered = New-Object System.Collections.Generic.List[object]  # (index, kind)
  $add = {
    param($idx, $kind)
    if ($idx -lt 0 -or $idx -ge $n) { return }
    if ($sampleIdx.Add($idx)) { $ordered.Add([pscustomobject]@{ Idx = $idx; Kind = $kind }) | Out-Null }
  }

  # 1. Tail (last 3 assistants)
  for ($i = [Math]::Max(0, $n - 3); $i -lt $n; $i++) { & $add $i 'tail' }

  # 2. Stuck-loop boundaries
  foreach ($sp in $stuckSpans) { & $add $sp.Start 'stuck-entry'; & $add $sp.End 'stuck-exit' }

  # 3. Per-tool entry-point (first 3 assistants after each new tool's first-use)
  $toolFirstSeen = @{}
  for ($i = 0; $i -lt $n; $i++) {
    foreach ($c in $assistants[$i].Calls) {
      if ($c.Name -and -not $toolFirstSeen.ContainsKey($c.Name)) { $toolFirstSeen[$c.Name] = $i }
    }
  }
  foreach ($tool in $toolFirstSeen.Keys) {
    $first = $toolFirstSeen[$tool]
    & $add $first "first-$tool"
    & $add ($first + 1) "first-$tool+1"
    & $add ($first + 2) "first-$tool+2"
  }

  # 4. Opening (first 3 assistants)
  for ($i = 0; $i -lt [Math]::Min(3, $n); $i++) { & $add $i 'opening' }

  # Cap by SampleCap honouring priority order already established.
  if ($SampleStrategy -eq 'quick' -and $ordered.Count -gt $SampleCap) {
    $ordered = $ordered | Select-Object -First $SampleCap
  }
  # Sort the final sample by iter for display.
  $samples = $ordered | Sort-Object Idx

  # Bucket hints (heuristic — Phase 7 keeps authority).
  $hints = @()
  if ($stuckSpans.Count -gt 0) {
    foreach ($sp in $stuckSpans) {
      $len = $sp.End - $sp.Start + 1
      $hints += "stuck loop iter $($sp.Start)..$($sp.End) ($len iters, tool=$($sp.DominantTool)) -> Agent-def issue OR Forge feature gap"
    }
  }
  $bashShare = if ($wholeBag.ContainsKey('bash') -and $assistants.Count -gt 0) {
    $totalCalls = ($wholeBag.Values | Measure-Object -Sum).Sum
    if ($totalCalls -gt 0) { [double]$wholeBag['bash'] / $totalCalls } else { 0 }
  } else { 0 }
  $structuredFsTools = @('read_file', 'read_file_slice', 'glob', 'grep', 'apply_patch', 'write_repo_file', 'write_workspace_artifact', 'write_file')
  $agentTools = Get-AgentToolList $runRoot
  $isMinimalForgeProfile = $true
  foreach ($t in $agentTools) {
    if ($structuredFsTools -contains $t) { $isMinimalForgeProfile = $false; break }
  }
  if ($isMinimalForgeProfile -and $bashShare -ge 0.5) {
    $hints += "minimal-forge profile (no structured fs tools registered) -> bash-share $([int]($bashShare*100))% is by design"
  }
  elseif ($bashShare -ge 0.5 -and ($wholeBag.ContainsKey('apply_patch') -or $wholeBag.ContainsKey('write_repo_file'))) {
    # Structured writer available but underused.
  }
  elseif ($bashShare -ge 0.5 -and -not $wholeBag.ContainsKey('apply_patch') -and -not $wholeBag.ContainsKey('write_repo_file')) {
    $hints += "bash share $([int]($bashShare*100))% with zero structured writes -> Agent-def issue (tool-preference) OR Tool-description issue"
  }
  # Prompt-token growth flag
  $deltas = @()
  for ($i = 1; $i -lt $promptCurve.Count; $i++) {
    if ($promptCurve[$i].PromptTokens -and $promptCurve[$i-1].PromptTokens) {
      $deltas += [pscustomobject]@{ Iter = $i; Delta = $promptCurve[$i].PromptTokens - $promptCurve[$i-1].PromptTokens }
    }
  }
  if ($deltas.Count -gt 3) {
    $median = ($deltas | Sort-Object Delta | Select-Object -Index ([int]($deltas.Count / 2))).Delta
    $spikes = @($deltas | Where-Object { $_.Delta -gt (2 * [Math]::Max(1, $median)) })
    if ($spikes.Count -gt 0) {
      $hints += "prompt-token spikes at iter(s) $(($spikes.Iter | Select-Object -First 5) -join ',') -> Forge feature gap (context growth)"
    }
  }

  return [pscustomobject]@{
    StageId = $StageDir.Name
    Roles = $roles
    Assistants = $assistants
    ReasoningPresent = $reasoningPresent
    ReasoningKinds = $reasoningKinds
    PromptCurve = $promptCurve
    WholeBag = $wholeBag
    HistWindows = $histWindows
    WindowSize = $windowSize
    StuckSpans = $stuckSpans
    Samples = $samples
    Hints = $hints
  }
}

# --- Emit --------------------------------------------------------------------

function Emit-Text {
  param($S)
  # Use Write-Output so non-interactive PowerShell hosts (Git Bash, Claude Code's
  # PowerShell tool, anything that captures stdout) actually receive the report.
  # Write-Host writes to the host's Information stream which is silent in those
  # contexts. Loss of -ForegroundColor is acceptable for a captured report.
  Write-Output ''
  Write-Output "=== Run qualitative analysis: $RunId   (stage=$($S.StageId)) ==="
  Write-Output ''
  Write-Output ("Message roles:      system={0}, user={1}, assistant={2}, tool={3}" -f $S.Roles['system'], $S.Roles['user'], $S.Roles['assistant'], $S.Roles['tool'])
  $rk = ($S.ReasoningKinds.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
  $asstN = $S.Assistants.Count
  # Report the *dominant reasoning* kind when any assistant carried reasoning —
  # only fall back to "(none detected)" when literally zero assistants had any.
  $kindLabel = if ($S.ReasoningPresent -gt 0) {
    ($S.ReasoningKinds.GetEnumerator() | Where-Object { $_.Key -ne 'none' } | Sort-Object Value -Descending | Select-Object -First 1).Key
  } else { '(none detected)' }
  Write-Output ("Iteration count:    $asstN")
  Write-Output ("Reasoning model:    $kindLabel (present on $($S.ReasoningPresent)/$asstN assistants; kinds: $rk)")

  Write-Output ''
  Write-Output 'Tool-call histogram (whole run):'
  $total = ($S.WholeBag.Values | Measure-Object -Sum).Sum
  foreach ($kv in $S.WholeBag.GetEnumerator() | Sort-Object Value -Descending) {
    $pct = if ($total -gt 0) { [int](($kv.Value / $total) * 100) } else { 0 }
    Write-Output ("  {0,-20} {1,4}  ({2,2}%)" -f $kv.Key, $kv.Value, $pct)
  }

  Write-Output ''
  Write-Output "Tool-call histogram by $($S.WindowSize)-iter window:"
  foreach ($w in $S.HistWindows) {
    $inStuck = ($S.StuckSpans | Where-Object { $_.Start -le $w.End -and $_.End -ge $w.Start })
    $marker = if ($inStuck) { ' <- stuck loop' } else { '' }
    $cs = ($w.Counts.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
    Write-Output ("  iter {0,3}-{1,3}: {2}{3}" -f $w.Start, $w.End, $cs, $marker)
  }

  Write-Output ''
  Write-Output 'Stuck-loop detections:'
  if ($S.StuckSpans.Count -eq 0) {
    Write-Output '  (none)'
  } else {
    foreach ($sp in $S.StuckSpans) {
      Write-Output ("  iter {0}-{1} ({2} iters, jaccard={3:N2}, stability={4:N2}) - tool={5}" -f `
        $sp.Start, $sp.End, ($sp.End - $sp.Start + 1), $sp.Jaccard, $sp.Stability, $sp.DominantTool)
    }
  }

  Write-Output ''
  Write-Output 'Prompt-token growth:'
  $picks = @(0)
  if ($S.Assistants.Count -gt 4) { $picks += [int]($S.Assistants.Count / 4); $picks += [int]($S.Assistants.Count / 2); $picks += [int]($S.Assistants.Count * 3 / 4) }
  $picks += ($S.Assistants.Count - 1)
  $picks = $picks | Select-Object -Unique | Where-Object { $_ -ge 0 -and $_ -lt $S.PromptCurve.Count }
  $prev = $null
  foreach ($i in $picks) {
    $pt = $S.PromptCurve[$i].PromptTokens
    if ($null -eq $pt) { continue }
    $deltaStr = if ($null -eq $prev) { '' } else { " (+{0:N0} over {1} iters)" -f ($pt - $prev.V), ($i - $prev.I) }
    Write-Output ("  iter {0,3}: {1,7:N0}{2}" -f $i, $pt, $deltaStr)
    $prev = [pscustomobject]@{ I = $i; V = $pt }
  }

  Write-Output ''
  Write-Output '=== Sampled reasoning ==='
  foreach ($samp in $S.Samples) {
    $a = $S.Assistants[$samp.Idx]
    $r = Truncate (Redact $a.ReasoningText) 320
    $c = Truncate (Redact $a.Content) 200
    $callStr = ($a.Calls | ForEach-Object { "$($_.Name)($([Regex]::Replace((Redact $_.Args), '\s+', ' ') | ForEach-Object { Truncate $_ 80 }))" }) -join ', '
    Write-Output ''
    Write-Output ("[iter $($samp.Idx)] ($($samp.Kind))")
    if ($r)       { Write-Output "  REASONING: $r" }
    if ($c)       { Write-Output "  CONTENT:   $c" }
    if ($callStr) { Write-Output "  TOOL_CALLS: $callStr" }
  }

  Write-Output ''
  Write-Output '=== Phase 7 bucket hints ==='
  if ($S.Hints.Count -eq 0) { Write-Output '  (no pattern triggered)' }
  else { foreach ($h in $S.Hints) { Write-Output "  - $h" } }
}

function Emit-Jsonl {
  param($S)
  [pscustomobject]@{
    runId = $RunId; stageId = $S.StageId
    roles = $S.Roles; reasoningPresent = $S.ReasoningPresent; reasoningKinds = $S.ReasoningKinds
    iterations = $S.Assistants.Count
    toolHistogram = $S.WholeBag
    stuckSpans = $S.StuckSpans
    bucketHints = $S.Hints
  } | ConvertTo-Json -Depth 6 -Compress | Write-Output

  foreach ($samp in $S.Samples) {
    $a = $S.Assistants[$samp.Idx]
    [pscustomobject]@{
      runId = $RunId; stageId = $S.StageId
      iter = $samp.Idx; kind = $samp.Kind
      reasoning = (Redact $a.ReasoningText)
      content = (Redact $a.Content)
      calls = @($a.Calls | ForEach-Object { @{ name = $_.Name; args = (Redact $_.Args); argsHash = $_.ArgsHash } })
    } | ConvertTo-Json -Depth 6 -Compress | Write-Output
  }
}

function Emit-FindingsScaffold {
  param($S)
  Write-Output "## Qualitative findings (run $RunId, stage $($S.StageId))"
  Write-Output ''
  foreach ($h in $S.Hints) {
    Write-Output "### [ ] $h"
    Write-Output ''
    Write-Output '- **Evidence:** '
    Write-Output '- **Bucket:** agent-def | feature-gap | bug | docs'
    Write-Output '- **Proposal:** '
    Write-Output ''
  }
}

$results = foreach ($sd in $stageDirs) { Analyse-Stage $sd }
$results = @($results | Where-Object { $_ -ne $null })

foreach ($S in $results) {
  switch ($OutFormat) {
    'text'               { Emit-Text $S }
    'jsonl'              { Emit-Jsonl $S }
    'findings-scaffold'  { Emit-FindingsScaffold $S }
  }
}

Write-Output ''
