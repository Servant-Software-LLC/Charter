# catches: the wave-6 three-leaf fan-in union (the skill task -> skills/charter/, the README task ->
#          README.md, the banner task -> src/Charter.Cli/Program.cs) that left git conflict markers on any
#          file it produced. The three leaves write DISJOINT surfaces, so a textual conflict is unlikely - but
#          this is the GR2028 union-soundness re-run this fan-in wave requires, and it stays cheap + correct.
#          Union-safe / CONDITIONAL (#125/#165): scan each file that is PRESENT under the touched surface for
#          line-anchored <<<<<<< / >>>>>>> markers; a path not yet materialized at a given union is simply
#          skipped, so this never false-REDs a correct partial merge. scope:"integration" (the LOCAL
#          whole-build/whole-suite terminal postcondition is 01, kept LOCAL so it does not re-run at the union
#          - the #125 trap).
$targets = @()
foreach ($item in @('README.md', 'skills/charter', 'src/Charter.Cli')) {
    if (Test-Path $item) {
        if ((Get-Item $item).PSIsContainer) {
            $targets += Get-ChildItem -Path $item -Recurse -File -Include *.md, *.cs, *.csproj -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        }
        else {
            $targets += Get-Item $item
        }
    }
}
foreach ($f in $targets) {
    $content = Get-Content -Raw -Path $f.FullName
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        Write-Output "$($f.FullName) contains git conflict markers - the wave-6 union did not cleanly integrate."
        exit 1
    }
}
exit 0
