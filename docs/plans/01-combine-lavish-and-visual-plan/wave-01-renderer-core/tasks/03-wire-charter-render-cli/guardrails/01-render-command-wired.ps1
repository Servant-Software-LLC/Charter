# catches: a Charter.Cli that names the renderer only in a comment/using but never INVOKES it (the
#          render command inert). Structural (#76): require the CharterRenderer type reference AND a
#          dotted .Render( call - a bare name grep passes on a comment / using / local stub, a call
#          does not. Grep scoped to the one file this task owns (Program.cs).
$prog = Get-Content -Raw src/Charter.Cli/Program.cs
if ($prog -notmatch 'CharterRenderer') {
    Write-Output "src/Charter.Cli/Program.cs does not reference the CharterRenderer type - the render command is not wired to the Charter.Core renderer."
    exit 1
}
if ($prog -notmatch '\.Render\s*\(') {
    Write-Output "src/Charter.Cli/Program.cs never invokes CharterRenderer.Render(...) - the wiring is a mention, not a call (a comment or using satisfies a bare name grep, but not this)."
    exit 1
}
exit 0
