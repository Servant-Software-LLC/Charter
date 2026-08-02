# Context topic registry

Topics for `/context-save` and `/context-load`. One line per topic:
`<Path> — <description>`. Keep each section sorted.

Saves written before this registry existed live flat at `.claude/contexts/context-*.md`
(2026-07-18 → 2026-07-24) and cover the **feature-work** thread: Architecture B, the
living-document loop, the block catalog, and the v0.1.0/v0.2.0 cuts. Load those directly by
path — they are not under any topic.

## Distribution

- `Distribution/Release` — packaging and shipping Charter: the release pipeline and version conventions, Homebrew tap mechanics, NuGet dotnet-tool publishing, native-binary RIDs, macOS Gatekeeper/signing, and target-framework/SDK decisions.
