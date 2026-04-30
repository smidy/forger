# delegate-status.ps1 <handle>
#
# Returns JSON describing the current state of a background forge run.
#
# State machine:
#   exit_code missing + pid alive  -> state="running"
#   exit_code missing + pid gone   -> state="failed" (abnormal)
#   exit_code = 0                  -> state="completed"
#   exit_code = 1 / 2 / *          -> state="failed"
#   exit_code = 6                  -> state="rate_limited"
#   exit_code = 7                  -> state="caller_deferred"
#   exit_code = 130                -> state="cancelled"

param(
    [Parameter(Mandatory=$true, Position=0)] [string]$Handle
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -PathType Container $Handle)) {
    @{ state = "unknown"; error = "handle directory not found: $Handle" } | ConvertTo-Json -Compress
    exit 0
}

$pidFile       = Join-Path $Handle "pid"
$exitCodeFile  = Join-Path $Handle "exit_code"
$agentFile     = Join-Path $Handle "agent.txt"
$startedAtFile = Join-Path $Handle "started_at"
$stdoutFile    = Join-Path $Handle "stdout.json"
$stderrFile    = Join-Path $Handle "stderr.log"

function Read-TrimmedText([string]$path) {
    if (Test-Path $path) {
        return (Get-Content -Raw -Path $path).Trim()
    }
    return ""
}

$pidValue   = Read-TrimmedText $pidFile
$agent      = Read-TrimmedText $agentFile
$startedAtS = Read-TrimmedText $startedAtFile

$startedAt = 0
if ($startedAtS -ne "") {
    [int64]::TryParse($startedAtS, [ref]$startedAt) | Out-Null
}

$pidInt = 0
if ($pidValue -ne "") {
    [int]::TryParse($pidValue, [ref]$pidInt) | Out-Null
}

if (-not (Test-Path $exitCodeFile)) {
    $isAlive = $false
    if ($pidInt -gt 0) {
        try {
            Get-Process -Id $pidInt -ErrorAction Stop | Out-Null
            $isAlive = $true
        } catch {
            $isAlive = $false
        }
    }
    if ($isAlive) {
        @{ state = "running" } | ConvertTo-Json -Compress
        exit 0
    }
    @{
        state = "failed"
        error = "process exited without writing exit_code"
        pid   = $pidValue
    } | ConvertTo-Json -Compress
    exit 0
}

$exitCodeText = Read-TrimmedText $exitCodeFile
$exitCode = 0
if (-not [int]::TryParse($exitCodeText, [ref]$exitCode)) {
    $exitCode = -1
}

$state = switch ($exitCode) {
    0   { "completed"; break }
    1   { "failed"; break }
    2   { "failed"; break }
    6   { "rate_limited"; break }
    7   { "caller_deferred"; break }
    130 { "cancelled"; break }
    default { "failed" }
}

# Recover run-id by scanning ~/.forge/runs/<agent>-* with mtime >= startedAt.
$runsRoot = Join-Path $env:USERPROFILE ".forge\runs"
$runId = ""
if ($agent -ne "" -and (Test-Path -PathType Container $runsRoot)) {
    $startedAtDt = [DateTimeOffset]::FromUnixTimeSeconds($startedAt).LocalDateTime
    $candidate = Get-ChildItem -Path $runsRoot -Directory -Filter "$agent-*" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $startedAtDt } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($candidate) { $runId = $candidate.Name }
}

function Read-TailUtf8([string]$path, [int]$maxBytes = 4096) {
    if (-not (Test-Path $path)) { return "" }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -eq 0) { return "" }
    if ($bytes.Length -gt $maxBytes) {
        $bytes = $bytes[($bytes.Length - $maxBytes)..($bytes.Length - 1)]
    }
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

$result = $null
$stdoutRaw = ""
if ((Test-Path $stdoutFile) -and (Get-Item $stdoutFile).Length -gt 0) {
    if ($state -eq "completed") {
        try {
            $result = (Get-Content -Raw $stdoutFile) | ConvertFrom-Json
        } catch {
            $result = $null
            $stdoutRaw = Read-TailUtf8 $stdoutFile
        }
    } else {
        $stdoutRaw = Read-TailUtf8 $stdoutFile
    }
}

$stderr = Read-TailUtf8 $stderrFile

$out = [ordered]@{
    state    = $state
    exitCode = $exitCode
}
if ($runId)               { $out["runId"]  = $runId }
if ($null -ne $result)    { $out["result"] = $result }
if ($stdoutRaw -ne "")    { $out["stdout"] = $stdoutRaw }
if ($stderr -ne "")       { $out["stderr"] = $stderr }

$out | ConvertTo-Json -Compress -Depth 32
