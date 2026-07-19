# catches: an author-tests task whose test file or stubs do not COMPILE - garbage exits dotnet test
#          non-zero identically to a real red, so without a build gate the red signal is gameable (#155).
#          With the minimal NotImplementedException stubs, the test project MUST build.
dotnet build tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --nologo -v q 2>&1 | Out-String | Write-Output
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Charter.Server.Tests does not build with the authored store tests + stubs present - the test file or its AnnotationStore/Annotation stubs are not type-correct."
    exit 1
}
exit 0
