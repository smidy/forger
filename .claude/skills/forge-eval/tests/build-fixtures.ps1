#requires -Version 5.1

# Builds the canned fixtures used to smoke-test analyse-reasoning.ps1.
# Each fixture is a minimal forge-home layout:
#
#   <fixture>/runs/test-run/trace.jsonl
#   <fixture>/runs/test-run/stages/agent/iterations/NNN/state.json
#
# Run once to populate .claude/skills/forge-eval/tests/fixtures/*.

[CmdletBinding()]
param(
  [string] $Root = (Join-Path $PSScriptRoot 'fixtures')
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function New-Fixture {
  param(
    [string] $Name,
    [object[]] $Assistants,   # each: @{ reasoning=..; thinking=..; content=..; tool_calls=@(@{ id; name; args; result }) }
    [string] $Role = 'system',
    [string] $SystemPrompt = 'You are a test agent.',
    [string] $UserPrompt = 'Do the thing.'
  )

  $fxHome = Join-Path -Path $Root -ChildPath $Name
  $runRoot = Join-Path -Path $fxHome -ChildPath 'runs'
  $runRoot = Join-Path -Path $runRoot -ChildPath 'test-run'
  $stageRoot = Join-Path -Path $runRoot -ChildPath 'stages'
  $stageRoot = Join-Path -Path $stageRoot -ChildPath 'agent'
  $iterRoot = Join-Path -Path $stageRoot -ChildPath 'iterations'
  New-Item -ItemType Directory -Path $iterRoot -Force | Out-Null

  # Build cumulative messages and trace events.
  $messages = @(
    [ordered]@{ role = 'system'; content = $SystemPrompt },
    [ordered]@{ role = 'user'; content = $UserPrompt }
  )
  $traceEvents = @()
  $promptTok = 1000

  for ($i = 0; $i -lt $Assistants.Count; $i++) {
    $a = $Assistants[$i]

    # llm_call event preceding the assistant turn.
    $promptTok += 200 + $i * 30
    $traceEvents += [ordered]@{
      kind = 'llm_call'; timestamp = (Get-Date -Format o)
      promptTokens = $promptTok; completionTokens = 100; durationMs = 500
    }

    # Assistant message.
    $asst = [ordered]@{ role = 'assistant' }
    if ($a.ContainsKey('content'))   { $asst['content']   = [string]$a.content }
    if ($a.ContainsKey('reasoning')) { $asst['reasoning_content'] = [string]$a.reasoning }
    if ($a.ContainsKey('thinking'))  { $asst['thinking']  = $a.thinking }
    $toolCalls = @()
    if ($a.ContainsKey('tool_calls')) {
      foreach ($tc in $a.tool_calls) {
        $callId = [string]$tc.id
        $toolCalls += [ordered]@{
          id = $callId
          type = 'function'
          function = [ordered]@{ name = [string]$tc.name; arguments = [string]$tc.args }
        }
        $hash = if ($tc.ContainsKey('hash')) { [string]$tc.hash } else {
          [BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($tc.args))).Replace('-','').Substring(0,16).ToLowerInvariant()
        }
        $traceEvents += [ordered]@{
          kind = 'tool_call'; iteration = $i; callId = $callId
          toolName = [string]$tc.name; argsHash = $hash
          durationMs = 10; error = $null; timestamp = (Get-Date -Format o)
        }
      }
    }
    if ($toolCalls.Count -gt 0) { $asst['tool_calls'] = $toolCalls }
    $messages += $asst

    # Tool results following the assistant.
    if ($a.ContainsKey('tool_calls')) {
      foreach ($tc in $a.tool_calls) {
        $messages += [ordered]@{
          role = 'tool'; tool_call_id = [string]$tc.id
          content = [string]$tc.result
        }
      }
    }

    # Write the per-iter snapshot (cumulative messages to this point).
    $iterDir = Join-Path $iterRoot ('{0:D3}' -f $i)
    New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
    $snapshot = [ordered]@{
      iter = $i
      nudged = $false
      messages = $messages
      ledger = @()
    }
    $json = $snapshot | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText((Join-Path $iterDir 'state.json'), $json, (New-Object Text.UTF8Encoding $false))
  }

  # trace.jsonl — one event per line.
  $tracePath = Join-Path $runRoot 'trace.jsonl'
  $lines = foreach ($e in $traceEvents) { $e | ConvertTo-Json -Compress -Depth 6 }
  [System.IO.File]::WriteAllLines($tracePath, $lines, (New-Object Text.UTF8Encoding $false))

  Write-Host "Built fixture: $Name at $fxHome" -ForegroundColor Green
}

# --- happy-path: 5 assistants, 2 with reasoning, 2 tool calls each ---

$happy = @(
  @{ reasoning = 'Open the plan to understand the task.'; tool_calls = @(
      @{ id = 'hp-000'; name = 'read_file'; args = '{"path":"docs/plans/x.md"}'; result = '# Plan X'  },
      @{ id = 'hp-001'; name = 'glob';      args = '{"patterns":["src/**/*.cs"]}'; result = '["src/A.cs","src/B.cs"]' }
  ) },
  @{ content = 'Reading source'; tool_calls = @(
      @{ id = 'hp-010'; name = 'read_file'; args = '{"path":"src/A.cs"}'; result = 'namespace A { class A {} }' },
      @{ id = 'hp-011'; name = 'read_file'; args = '{"path":"src/B.cs"}'; result = 'namespace B { class B {} }' }
  ) },
  @{ reasoning = 'Apply the patch.'; tool_calls = @(
      @{ id = 'hp-020'; name = 'apply_patch'; args = '{"diff":"--- a/src/A.cs\n+++ b/src/A.cs"}'; result = 'patched' },
      @{ id = 'hp-021'; name = 'read_file';   args = '{"path":"src/A.cs"}'; result = 'patched contents' }
  ) },
  @{ content = 'Building'; tool_calls = @(
      @{ id = 'hp-030'; name = 'read_file'; args = '{"path":"Forge.sln"}'; result = 'sln contents' },
      @{ id = 'hp-031'; name = 'grep';      args = '{"pattern":"TODO"}';   result = 'no matches' }
  ) },
  @{ tool_calls = @(
      @{ id = 'hp-040'; name = 'submit_final'; args = '{"summary":"done"}'; result = 'accepted' },
      @{ id = 'hp-041'; name = 'submit_final'; args = '{"summary":"done"}'; result = 'accepted' }
  ) }
)
New-Fixture -Name 'happy-path' -Assistants $happy

# --- stuck-loop: 25 iterations, identical argsHash + identical result content ---

$stuck = @()
for ($k = 0; $k -lt 25; $k++) {
  $stuck += @{ reasoning = "Attempt $k. The build keeps failing."; tool_calls = @(
    @{ id = "sl-$k"; name = 'bash'; args = '{"command":"dotnet build"}'; hash = 'stuckhash00000000'; result = 'error NU1301 package not found' }
  ) }
}
New-Fixture -Name 'stuck-loop' -Assistants $stuck

# --- false-positive-sweep: 30 iters of bash, different args + different results ---

$sweep = @()
for ($k = 0; $k -lt 30; $k++) {
  $sweep += @{ reasoning = "Inspecting file $k."; tool_calls = @(
    @{ id = "sw-$k"; name = 'bash'; args = "{`"command`":`"cat file$k.txt`"}";
       result = "unique content for file $k with specific data abc$k def$k ghi" }
  ) }
}
New-Fixture -Name 'false-positive-sweep' -Assistants $sweep

# --- non-reasoning: 5 assistants, no reasoning_content or thinking ---

$nonR = @()
for ($k = 0; $k -lt 5; $k++) {
  $nonR += @{ content = "Plain assistant turn $k."; tool_calls = @(
    @{ id = "nr-$k"; name = 'read_file'; args = "{`"path`":`"src/Turn$k.cs`"}"; result = "class Turn$k {}" }
  ) }
}
New-Fixture -Name 'non-reasoning' -Assistants $nonR

# --- utf8: non-ASCII reasoning content (arrows, box drawing, CJK, emoji) ---
# Source stays pure ASCII; PowerShell composes the UTF-8 runtime strings
# from codepoint escapes so the build script parses identically under any
# default encoding (including Windows-1252).

$arrowRight  = [char]0x2192
$boxH        = [char]0x2500
$accentedA   = [char]0x00E1
$cjkSample   = "$([char]0x6D4B)$([char]0x8BD5)"   # "test" in Chinese
$emojiCheck  = [char]0x2713
$realSet     = [char]0x211D

$utf8 = @(
  @{ reasoning = "Arrow symbol present: $arrowRight, box: $boxH, accent: $accentedA"; tool_calls = @(
    @{ id = 'u8-000'; name = 'grep'; args = "{`"pattern`":`"$accentedA`"}"; result = "matched $accentedA $arrowRight $realSet" }
  ) },
  @{ reasoning = "CJK sample $cjkSample emoji $emojiCheck"; tool_calls = @(
    @{ id = 'u8-010'; name = 'bash'; args = "{`"command`":`"echo $emojiCheck`"}"; result = $emojiCheck }
  ) }
)
New-Fixture -Name 'utf8' -Assistants $utf8

Write-Host ''
Write-Host "All fixtures built under $Root" -ForegroundColor Cyan
