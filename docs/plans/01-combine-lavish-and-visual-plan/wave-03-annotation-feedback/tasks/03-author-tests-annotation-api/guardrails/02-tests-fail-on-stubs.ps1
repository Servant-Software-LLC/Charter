# catches: authored AnnotationApi tests that PASS against the un-routed wave-2 server (a tautological,
#          not-real TDD red - e.g. a test that only asserts the server starts). With the build green
#          (guardrail 01), a non-zero test exit here means the tests RAN and FAILED at runtime because the
#          /api/* + /events endpoints are not routed yet = a real red. A zero exit means the tests assert
#          nothing that the wave-2 server does not already satisfy.
$test = dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --filter "Category=AnnotationApi" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -eq 0) {
    Write-Output "The Category=AnnotationApi tests PASSED (or matched none) against the un-routed server - they are not a real TDD red. The round-trip (POST /api/{key}/prompts -> GET /api/poll with the resolved source line), /api/sessions, /events, and CSRF must FAIL until task 04 routes them."
    exit 1
}
exit 0
