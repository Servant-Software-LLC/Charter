# catches: an author-tests task that writes only a trivial subset and skips the load-bearing review-
#          server behaviors, so the wave goes green having never encoded SDK injection, the capability
#          key, path-confinement, or the loopback serve proof. Lower bound: a token present != the
#          behavior fully asserted, but its ABSENCE proves the behavior is unwritten. Scoped to this
#          task's ReviewServer test files only.
$files = Get-ChildItem -Path tests/Charter.Server.Tests -Recurse -Filter *.cs -File |
    Where-Object { (Get-Content -Raw $_.FullName) -match 'ReviewServer' }
if (-not $files) {
    Write-Output "No ReviewServer-traited test file found under tests/Charter.Server.Tests - the author-tests task produced no covered tests."
    exit 1
}
$content = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"

# Each behavior the action prompt enumerates must appear (lower-bound presence check).
$required = [ordered]@{
    'serve-time SDK injection'   = 'Inject'
    'per-session capability key' = 'Capability'
    'path-confinement'           = 'Confin'
    'loopback bind'              = '(Loopback|127\.0\.0\.1)'
    'server serve (Start call)'  = '\.Start\s*\('
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += ("$k [/$($required[$k])/]") }
}
if ($missing.Count -gt 0) {
    Write-Output ("Required behavior token(s) absent from the ReviewServer tests: " + ($missing -join '; '))
    Write-Output "The action prompt enumerates these behaviors; each needs at least one test. The loopback serve integration test is the end-to-end proof."
    exit 1
}
exit 0
