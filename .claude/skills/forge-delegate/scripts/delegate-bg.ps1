# delegate-bg.ps1 <agent-name> <input-json>
#
# Background wrapper for `forge agent <name>`. Returns immediately with a
# handle path (a temp directory under ~/.forge/delegations/).
#
# Handle directory contents:
#   input.json   - agent input (UTF-8)
#   agent.txt    - agent name (ASCII)
#   started_at   - epoch seconds at kick-off (ASCII)
#   pid          - wrapper subprocess PID (ASCII)
#   exit_code    - populated when forge exits (ASCII)
#   stdout.json  - forge's stdout
#   stderr.log   - forge's stderr
#
# Poll progress with delegate-status.ps1 <handle>.

param(
    [Parameter(Mandatory=$true, Position=0)] [string]$AgentName,
    [Parameter(Mandatory=$true, Position=1)] [string]$InputJson
)

$ErrorActionPreference = 'Stop'

$delegationsRoot = Join-Path $env:USERPROFILE ".forge\delegations"
[void][System.IO.Directory]::CreateDirectory($delegationsRoot)

$handleName = "forge-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$handle = Join-Path $delegationsRoot $handleName
[void][System.IO.Directory]::CreateDirectory($handle)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$ascii = New-Object System.Text.ASCIIEncoding

[System.IO.File]::WriteAllText((Join-Path $handle "input.json"), $InputJson, $utf8NoBom)
[System.IO.File]::WriteAllText((Join-Path $handle "agent.txt"), $AgentName, $ascii)
[System.IO.File]::WriteAllText((Join-Path $handle "started_at"),
    [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString(), $ascii)

$inputArg = "@" + (Join-Path $handle "input.json")
$stdoutPath = Join-Path $handle "stdout.json"
$stderrPath = Join-Path $handle "stderr.log"
$exitCodePath = Join-Path $handle "exit_code"
$pidPath = Join-Path $handle "pid"
$runnerPath = Join-Path $PSScriptRoot "_bg-runner.ps1"

$proc = Start-Process -FilePath "powershell.exe" `
    -ArgumentList @(
        "-NoProfile",
        "-NonInteractive",
        "-File", $runnerPath,
        $AgentName,
        $inputArg,
        $exitCodePath
    ) `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -PassThru `
    -WindowStyle Hidden

[System.IO.File]::WriteAllText($pidPath, $proc.Id.ToString(), $ascii)

Write-Output $handle
