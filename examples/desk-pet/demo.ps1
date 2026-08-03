<#
  Desk Pet — the Charter demo, driven step by step.

  Runs the author -> review -> handoff loop against a WORKING COPY of the chart, so the committed
  desk-pet.charter.md stays pristine and the demo is re-runnable as many times as you like. That
  matters: `resolve` writes answers back INTO the chart, so a demo run against the committed file
  would leave the second rehearsal starting from an already-answered plan.

  Usage:
    ./demo.ps1              # use the `charter` on PATH (the honest demo)
    ./demo.ps1 -Local       # use a locally-built CLI (the reliable demo)
#>
[CmdletBinding()]
param([switch]$Local)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here '..\..')
$work = Join-Path $here '.work'

# Deliberately a SIMPLE function with no param block. Declaring
# [Parameter(ValueFromRemainingArguments)] would make this an ADVANCED function, which gains the
# common parameters — and PowerShell then binds `-o` against -OutVariable/-OutBuffer and dies with
# "parameter name 'o' is ambiguous" before charter ever runs. With no param block, $args collects
# everything verbatim, `-o` included.
function Invoke-Charter {
    if ($Local) { & dotnet run --project (Join-Path $repo 'src\Charter.Cli') -c Release -- @args }
    else        { & charter @args }
}

function Step  { param($m) Write-Host "`n== $m" -ForegroundColor Cyan }
function Pause { Write-Host "`n-- press Enter to continue --" -ForegroundColor DarkGray; [void][Console]::ReadLine() }

if (-not $Local -and -not (Get-Command charter -ErrorAction SilentlyContinue)) {
    Write-Error @"
charter is not on PATH. Install it, or re-run with -Local to use a locally-built CLI:
  brew install servant-software-llc/tap/charter
  dotnet tool install -g ServantSoftware.Charter
"@
}

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null
Copy-Item (Join-Path $here 'desk-pet.charter.md') $work
$plan = Join-Path $work 'desk-pet.charter.md'

Step '0. What we start with'
$blocks = (Select-String -Path $plan -Pattern '^:::' ).Count
Write-Host "A single prompt (PROMPT.md) produced this chart. Nothing here was hand-written:"
Write-Host "  $blocks directive block delimiters"
Write-Host "  4 open questions, 0 answered"
Pause

Step '1. RENDER — one portable HTML artifact'
Invoke-Charter render $plan -o (Join-Path $work 'desk-pet.html')
Write-Host 'Opens standalone in any browser. No server, no runtime.'
Pause

Step '2. REVIEW — serve it and annotate IN PLACE'
Write-Host @'
The browser will open. Things worth doing on camera, in this order:

  a. Comment on a single ROW of the "what counts as feeding it" comparison
     (hover the "Lines changed" row) — the comment anchors to that row, not the block.
  b. Comment on one NODE of the mood diagram (click the "Feral" state).
  c. Comment on one LINE of the diff (the `|| true` line).
  d. Answer all four questions in the panel — note they are real form controls:
     two single-selects, a yes/no, and a free-text.
  e. Click "Send to agent" to end the round.

Then come back here and press Ctrl-C to stop the server.
'@
Pause
try { Invoke-Charter review $plan } catch { }

Step '3. RESOLVE — fold the answers back INTO the chart'
try { Invoke-Charter resolve $plan } catch { }
Write-Host "`nWhat changed in the source — the answers are now part of the document:"
$before = Select-String -Path (Join-Path $here 'desk-pet.charter.md') -Pattern '"id":' | ForEach-Object { $_.Line.Trim() }
$after  = Select-String -Path $plan -Pattern '"id":' | ForEach-Object { $_.Line.Trim() }
Compare-Object $before $after | Format-Table -AutoSize
Pause

Step '4. HANDOFF — plain CommonMark for Guardrails'
Invoke-Charter handoff $plan -o (Join-Path $work 'desk-pet.md')
Write-Host 'Every ::: directive is now plain CommonMark. This is what Guardrails breaks into a task DAG:'
Get-Content (Join-Path $work 'desk-pet.md') -TotalCount 30
Pause

Step 'Done'
Write-Host "Artifacts left in: $work"
Write-Host 'The committed chart is untouched — run this again any time.'
