# catches: a CLI whose bare-invocation banner still lies - calling the tool a 'scaffold' or saying the review
#          server 'lands next' (both false once render/review/export/handoff shipped) - or an edit that fails
#          to surface BOTH new verbs (F4: the prompt requires export AND handoff, not either). This is the ONE
#          wave-6 check that runs the ACTUAL BUILT BINARY end to end: it drives bare `charter` (the banner
#          path) and `charter --version`, so it also proves the banner edit did not break the build or the
#          version path. It FAILS on the current (pre-edit) code - the stale banner still says 'scaffold' -
#          so it has genuine anti-tautology teeth, and goes green only once the banner is truthful.
#          Idempotent: reads stdout, writes no files.
$banner = dotnet run --project src/Charter.Cli -c Debug -- 2>&1 | Out-String
Write-Output $banner
if ($LASTEXITCODE -ne 0) {
    Write-Output "the banner smoke could not run the built binary (bare charter exited $LASTEXITCODE) - the CLI must still build and run after the banner edit."
    exit 1
}
if ($banner -match '(?i)scaffold') {
    Write-Output "bare charter banner still calls the tool a 'scaffold' - render/review/export/handoff have all shipped; drop the scaffold status."
    exit 1
}
if ($banner -match '(?i)lands next') {
    Write-Output "bare charter banner still says the review server 'lands next' - it has landed; the banner must not describe it as future work."
    exit 1
}
if ($banner -notmatch '(?i)export' -or $banner -notmatch '(?i)handoff') {
    Write-Output "bare charter banner does not surface BOTH the 'export' and 'handoff' verbs - the prompt requires both to appear so the working command surface is fully shown."
    exit 1
}
$version = dotnet run --project src/Charter.Cli -c Debug -- --version 2>&1 | Out-String
Write-Output $version
if ($version -notmatch 'charter\s+\d+\.\d+\.\d+') {
    Write-Output "charter --version did not print 'charter <version>' - the version path must stay intact through the banner edit."
    exit 1
}
exit 0
