# catches: an enum edit that does not compile - e.g. a stray comma, a duplicate member, or a switch made
#          non-exhaustive. With TreatWarnings settings this proves the new members are type-correct before
#          the per-block implement tasks build on them. Scoped to the one project this task touches.
dotnet build src/Charter.Core/Charter.Core.csproj -c Debug --nologo -v q 2>&1 | Out-String | Write-Output
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Charter.Core does not build after adding the four BlockKind members - the enum edit is not type-correct."
    exit 1
}
exit 0
