# catches: `charter handoff` that builds but does not actually convert directives / resolve answers end
#          to end (a wired-but-broken command) - AND (post-#302-review hardening) a BROKEN/LOSSY
#          IMPLEMENTATION that ships despite passing the unit-test suite: an Emit whose switch handles
#          only Diagram/Question and silently DROPS Prose/Heading/Note/Warn/Comparison (a broken, lossy
#          handoff that still passes a thin 2-test suite). This is the ONLY check in the wave that runs
#          the ACTUAL BUILT BINARY end to end, so the sample now exercises every directive kind, not just
#          the two the earlier, narrower sample happened to cover.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("charter-handoff-smoke-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $mdx = Join-Path $tmp "sample.mdx"
    $outOpen = Join-Path $tmp "out-open.md"
    $outAnswered = Join-Path $tmp "out-answered.md"
    $answers = Join-Path $tmp "answers.json"

    @'
# Hello Handoff

A trailing prose paragraph with load-bearing text.

:::note
An important note.
:::

:::warn
Be careful here.
:::

:::comparison
- Option A
- Option B
:::

:::diagram
graph TD; A-->B;
:::

:::question
{"id":"q1","title":"Pick one","mode":"single","options":["A","B"],"target":"human"}
:::
'@ | Set-Content -Path $mdx -Encoding utf8

    dotnet run --project src/Charter.Cli -c Debug -- handoff $mdx -o $outOpen 2>&1 | Out-String | Write-Output
    if (-not (Test-Path $outOpen)) {
        Write-Output "charter handoff (no --answers) produced no output file at $outOpen"
        exit 1
    }
    $openText = Get-Content -Raw $outOpen

    if ($openText -match '(?m)^:::') {
        Write-Output "charter handoff output (no --answers) has a line beginning with ':::' - a directive fence leaked into the canonical markdown."
        exit 1
    }

    # The heading and trailing prose must survive - proves Emit does not silently drop plain block kinds.
    if ($openText -notmatch '# Hello Handoff') {
        Write-Output "charter handoff output (no --answers) lost the heading '# Hello Handoff' - the handoff is dropping plain block kinds, not just converting directives."
        exit 1
    }
    if ($openText -notmatch 'A trailing prose paragraph with load-bearing text\.') {
        Write-Output "charter handoff output (no --answers) lost the trailing prose paragraph - the handoff is dropping plain block kinds."
        exit 1
    }

    # Note/Warn/Comparison must each survive in recognizable form - the broken-lossy-implementation
    # regression guard (an Emit that only handles Diagram/Question passes a thin suite but drops these).
    if ($openText -notmatch 'An important note\.') {
        Write-Output "charter handoff output (no --answers) lost the :::note content - a lossy implementation that only handles Diagram/Question would fail here."
        exit 1
    }
    if ($openText -notmatch 'Be careful here\.') {
        Write-Output "charter handoff output (no --answers) lost the :::warn content."
        exit 1
    }
    if ($openText -notmatch 'Option A' -or $openText -notmatch 'Option B') {
        Write-Output "charter handoff output (no --answers) lost the :::comparison option list."
        exit 1
    }

    if ($openText -notmatch '```mermaid') {
        Write-Output "charter handoff output (no --answers) does not contain a ```mermaid fence for the :::diagram block."
        exit 1
    }
    if ($openText -notmatch 'Open question') {
        Write-Output "charter handoff output (no --answers) does not flag the unanswered :::question as an open question."
        exit 1
    }

    '{"q1": ["A"]}' | Set-Content -Path $answers -Encoding utf8
    dotnet run --project src/Charter.Cli -c Debug -- handoff $mdx -o $outAnswered --answers $answers 2>&1 | Out-String | Write-Output
    if (-not (Test-Path $outAnswered)) {
        Write-Output "charter handoff (with --answers) produced no output file at $outAnswered"
        exit 1
    }
    $answeredText = Get-Content -Raw $outAnswered
    if ($answeredText -match '(?m)^:::') {
        Write-Output "charter handoff output (with --answers) has a line beginning with ':::' - a directive fence leaked into the canonical markdown."
        exit 1
    }
    if ($answeredText -match 'Open question') {
        Write-Output "charter handoff output (with --answers resolving q1) still flags q1 as an open question - the answer was not resolved."
        exit 1
    }
    if ($answeredText -notmatch 'Answered:') {
        Write-Output "charter handoff output (with --answers resolving q1) does not contain the resolved-answer marker 'Answered:'."
        exit 1
    }

    exit 0
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
