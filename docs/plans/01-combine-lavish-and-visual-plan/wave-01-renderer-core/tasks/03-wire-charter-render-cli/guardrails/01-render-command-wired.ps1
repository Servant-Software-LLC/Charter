# catches: a Charter.Cli that builds but never references the renderer (the `render` command is a
#          stub / not wired to CharterRenderer), so the feature is inert. Grep scoped to Program.cs.
$prog = Get-Content -Raw src/Charter.Cli/Program.cs
if ($prog -notmatch 'CharterRenderer') {
    Write-Output "src/Charter.Cli/Program.cs does not reference CharterRenderer - the render command is not wired to the Charter.Core renderer."
    exit 1
}
if ($prog -notmatch 'render') {
    Write-Output "src/Charter.Cli/Program.cs has no 'render' command."
    exit 1
}
exit 0
