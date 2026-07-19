# catches: a wave-3 fan-in union (04 + 05 -> 06) that left git conflict markers on a shared file - the
#          annotation server, the embedded SDK, its .csproj, or the tests. Union-safe / CONDITIONAL
#          (#125/#165): scan each file that is PRESENT for line-anchored <<<<<<< / >>>>>>> markers; a path
#          not yet materialized at a given union is simply skipped, so this never false-REDs a correct
#          partial merge. This is the GR2028 union-soundness re-run for this multi-leaf/fan-in wave;
#          scope:"integration" (the LOCAL whole-build/whole-suite terminal postcondition is 01, kept LOCAL
#          so it does not re-run at every union - the #125 trap).
$targets = @()
foreach ($dir in @('src/Charter.Server', 'tests/Charter.Server.Tests', 'sdk')) {
    if (Test-Path $dir) {
        $targets += Get-ChildItem -Path $dir -Recurse -File -Include *.cs, *.js, *.csproj -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    }
}
foreach ($f in $targets) {
    $content = Get-Content -Raw -Path $f.FullName
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        Write-Output "$($f.FullName) contains git conflict markers - the wave-3 union did not cleanly integrate."
        exit 1
    }
}
exit 0
