# catches: the MermaidResource loader or csproj embed that does not compile (e.g. a bad LogicalName
#          reference, a syntax error in the loader). Scoped to the one project this task touches.
dotnet build src/Charter.Core/Charter.Core.csproj -c Debug --nologo -v q 2>&1 | Out-String | Write-Output
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Charter.Core does not build after vendoring Mermaid + adding MermaidResource - the loader or the csproj embed is not type-correct."
    exit 1
}
exit 0
