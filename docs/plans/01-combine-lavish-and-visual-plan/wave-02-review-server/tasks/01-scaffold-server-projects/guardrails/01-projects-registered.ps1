# catches: the two new wave-2 projects not created/registered - Charter.Server + Charter.Server.Tests
#          must exist, be registered in Charter.sln, wire the right references, and the solution must
#          build. A file-exists alone misses an unregistered project (a descriptor build ignores it).
$sln = Get-Content -Raw "Charter.sln"
if ($sln -notmatch '"Charter\.Server",') {
    Write-Output "Charter.sln does not register the Charter.Server project - add it (dotnet sln Charter.sln add src/Charter.Server/Charter.Server.csproj)."
    exit 1
}
if ($sln -notmatch '"Charter\.Server\.Tests",') {
    Write-Output "Charter.sln does not register the Charter.Server.Tests project - add it (dotnet sln Charter.sln add tests/Charter.Server.Tests/Charter.Server.Tests.csproj)."
    exit 1
}
if (-not (Test-Path "src/Charter.Server/Charter.Server.csproj")) {
    Write-Output "src/Charter.Server/Charter.Server.csproj is missing."
    exit 1
}
if (-not (Test-Path "tests/Charter.Server.Tests/Charter.Server.Tests.csproj")) {
    Write-Output "tests/Charter.Server.Tests/Charter.Server.Tests.csproj is missing."
    exit 1
}
$serverProj = Get-Content -Raw "src/Charter.Server/Charter.Server.csproj"
if ($serverProj -notmatch 'Charter\.Core\.csproj') {
    Write-Output "Charter.Server.csproj must ProjectReference Charter.Core (it renders via Charter.Core.CharterRenderer)."
    exit 1
}
$testProj = Get-Content -Raw "tests/Charter.Server.Tests/Charter.Server.Tests.csproj"
if ($testProj -notmatch 'Charter\.Server\.csproj') {
    Write-Output "Charter.Server.Tests.csproj must ProjectReference Charter.Server (its tests exercise the server)."
    exit 1
}
$build = dotnet build Charter.sln -c Debug 2>&1 | Out-String
Write-Output $build
if ($LASTEXITCODE -ne 0) {
    Write-Output "The solution does not build after scaffolding the new projects."
    exit 1
}
exit 0
