# catches: a Charter.Cli that names the review server only in a comment/using but never wires the
#          `review` command to it. Structural: require Charter.Cli to reference Charter.Server (project
#          ref) AND Program.cs to INVOKE the server start (a dotted .Start( call) behind a `review` verb -
#          a bare name grep passes on a comment/using/local stub; a call + a project ref do not.
$csproj = Get-Content -Raw "src/Charter.Cli/Charter.Cli.csproj"
if ($csproj -notmatch 'Charter\.Server\.csproj') {
    Write-Output "Charter.Cli.csproj does not ProjectReference Charter.Server - the review command cannot start the review server without it."
    exit 1
}
$prog = Get-Content -Raw "src/Charter.Cli/Program.cs"
if ($prog -notmatch 'ReviewServer') {
    Write-Output "src/Charter.Cli/Program.cs does not reference the ReviewServer type - the review command is not wired to Charter.Server."
    exit 1
}
if ($prog -notmatch '\.Start\s*\(') {
    Write-Output "src/Charter.Cli/Program.cs never invokes ReviewServer.Start(...) - the wiring is a mention, not a call (a comment or using satisfies a bare name grep, but not this)."
    exit 1
}
if ($prog -notmatch '"review"') {
    Write-Output "src/Charter.Cli/Program.cs does not define a `review` command/verb - the review server has no CLI entry point."
    exit 1
}
exit 0
