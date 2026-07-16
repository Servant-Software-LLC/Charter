# Charter — Combining Lavish's review loop with visual-plan's block authoring

**Status:** draft-of-record · decisions from the format-research verdict and the devil's-advocate
critique are folded in · **Consumes into:** Guardrails `plan-breakdown`

## Goal

Charter is the front door to an agentic delivery pipeline (`Charter → Guardrails → firstmate/gnhf`):
an AI authors a rich, **block-structured** plan; a human reviews it in the browser and **comments in
place** (notes anchored to the exact block they point at); the reviewed plan feeds Guardrails, which
breaks it into a verified task DAG. It combines **[Lavish](https://github.com/kunchenguid/lavish-axi)**'s
comment-in-place review loop with **[Builder.io visual-plan](https://github.com/BuilderIO/skills/tree/main/skills/visual-plan)**'s
block authoring, implemented C#-native.

## Two settled decisions (reviewed, not assumed)

### D1 — Format: markdown + directives (Markdig), as a deliberate hybrid

Chosen over MDX, Adaptive Cards, JSON Forms, raw HTML, notebooks, AsciiDoc/RST, and slides after a
head-to-head study.

- **The essence of "MDX blocks" is a validated block *schema*, not JSX.** Builder.io's pipeline is
  MDX → typed components → **normalized to JSON, validated with Zod** → renderer; the load-bearing
  part is the strict schema ("type-safety for natural language"). Real MDX **cannot run in C#** (needs
  a JS runtime), so **markdown + `:::` directives via [Markdig](https://github.com/xoofx/markdig), each validated against a C# record, is
  the correct C# reproduction of Builder's actual architecture** — not a weaker substitute.
- **Hybrid split by concern:** expressive markdown/directives for narrative + visuals (strict format
  degrades LLM *reasoning*), plus a **schema-validated `:::question` block** rendered to native HTML
  `<form>` inputs for elicitation. This `:::question` block **reproduces visual-plan's `question-form`**
  — the input gap it fills is in *base markdown* (CommonMark has no input primitive), **not** in
  visual-plan, which already elicits via `question-form` and its `visual-intake` mode. It borrows
  [Adaptive Cards](https://adaptivecards.io/)' `Input`/`Action.Submit` *shape* as a template, not a dependency. Plus a
  **`:::custom-html` escape hatch** for raw-HTML ceiling cases.
- **No more-expressive *viable* standard exists.** Raw HTML is absolutely more expressive but least
  constrainable/anchorable — against Charter's "reviewable, validated" differentiator.

### D2 — Review loop: reimplement **lean** in C# (not a full Lavish port, not a subprocess)

Charter stays an **independent, single node-free binary** in its own stack — the reason it exists.
- **Lean, purpose-built surface:** serve artifact + inject SDK + annotate (element / text-range /
  diagram-node) + long-poll feedback + live reload. We own a **small** server contract, not Lavish's
  ~18 routes.
- **Explicitly OUT for v1:** hosted export/share, telemetry (none in v1), the layout-audit gate,
  publish, multi-artifact sessions, review-round diffing (see *Out of scope*).
- **Honest cost:** a few thousand LOC of C# + a **lean embedded JS SDK adapted from Lavish (MIT,
  attributed)** — *not* the ~7k-LOC full clone (which couples us to an actively-developed upstream and
  forces perpetual re-porting). Keeping the SDK minimal and purpose-built is what makes the re-port
  drift manageable.

## Architecture

```
plan.mdx ─▶ Charter.Core (Markdig + block catalog + stable IDs + source-map) ─▶ artifact.html (portable)
                                                                                   │ served + SDK injected at serve time
                                                                                   ▼
   agent ◀─ charter poll (long-poll) ◀─ Charter.Server (IReviewServer, 127.0.0.1) ─▶ browser (annotate in place)
              │ annotation carries anchor → markdown line range (source-map)
              ▼
        emit canonical reviewed markdown ─▶ Guardrails plan-breakdown ─▶ task DAG
```

Projects: `Charter.Core` (renderer, block catalog, **anchor source-map**, session model),
`Charter.Cli` (commands), `Charter.Server` (behind an `IReviewServer` seam), and a lean `sdk/` (JS,
adapted from Lavish). The saved artifact stays byte-identical apart from the serve-time SDK injection.

## Format & block catalog

| Block | Charter directive | Annotatable | Interactive |
|---|---|---|---|
| prose / heading / list | plain markdown | ✅ text-range | — |
| callout | `:::note` / `:::warn` | ✅ | — |
| table / comparison | pipe tables · `:::comparison` | ✅ per-row/option | — |
| code / diff | fenced ` ```lang ` · `:::diff` | ✅ per-line | — |
| annotated-code | `:::annotated-code {#id}` | ✅ per-line | — |
| file-tree | `:::file-tree` | ✅ | — |
| diagram | `:::diagram` (Mermaid body) | ✅ per-node | pan/zoom |
| wireframe / escape hatch | `:::custom-html` (sanitized inline HTML) | ✅ | (author's HTML) |
| **question (elicitation)** | **`:::question`** — body = YAML/JSON validated to a C# record (`id`, `title`, `mode` ∈ single/multi/free-text/bool/number, `options`, `target` ∈ human/agent) → native HTML `<form>` | ✅ | ✅ submits structured answers |

Every block gets a **content-derived stable ID**; the renderer carries a **source-map (block/anchor
ID → markdown line range)** so a human's annotation on the *rendered HTML* round-trips to the
*markdown source* the agent edits.

## Load-bearing invariants

1. **Portable artifact** — opens standalone; SDK injected only at serve time.
2. **Comment-in-place with round-trip** — annotations anchor to stable block IDs and map back to
   markdown source lines; they survive a re-render of unrelated blocks.
3. **Format single-sourced** — the block schema lives in one place; renderer, SDK, skill cite it.
4. **Loopback + capability** — server binds `127.0.0.1`; each session carries a capability key; file
   serving is path-confined. Exposure beyond loopback is explicit and documented.
5. **Feeds Guardrails via plain markdown** — the handoff is canonical reviewed markdown, no MDX.
6. **Narrow C#↔JS boundary** — browser logic isolated in `sdk/`, over a defined postMessage/HTTP
   contract.
7. **Telemetry off / opt-in** — a deliberate departure from Lavish's default-on model.

## Milestones = Guardrails waves (real unknowns front-loaded)

These milestones are **ordered stages** — each builds on the prior stage's *materialized* output — so
they map to Guardrails **waves** (see *Guardrails execution mapping* below). There is **no separate M0
throwaway spike**: the end-to-end walkthrough it would have given is already provided by the DAG plus
wave-by-wave review, and the risk it would have proven is folded into each wave's acceptance guardrails,
front-loaded as far as it deterministically can be.

- **Wave 1 (M1) — renderer core + source-map.** Markdig + prose/heading/list/callout/table/code;
  content-derived stable IDs; anchor→markdown source-map; `charter render plan.mdx -o plan.html`.
  *Accept:* golden HTML per block; an **anchor-survival test** (annotate a block, edit an *unrelated*
  block, re-render, the anchor still resolves to the right markdown line); and a **handoff fixture** — a
  sample emitted markdown that passes `guardrails validate` (pins the handoff shape now). The deepest
  *deterministic* risk — the source↔render anchor map — is proven here, first.
- **Wave 2 (M2) — lean review server.** `Charter.Server` behind `IReviewServer`; **spike Kestrel vs
  HttpListener for single-file/AOT size**; serve artifact + inject SDK + live reload (watch the *file*);
  loopback-only + per-session capability key + path-confinement, all tested. Read-only preview.
- **Wave 3 (M3) — annotation + feedback loop.** Lean embedded JS SDK (element + text-range + node
  annotation, postMessage) + a minimal server contract (`/api/sessions`, `/api/:key/prompts`,
  `/api/poll` long-poll, `/events` SSE); session store. **Choose the browser-test harness here
  (Playwright).** *Accept:* a headless test queues an annotation and `poll` returns it with the correct
  markdown source anchor — proving the *browser* half of the round-trip (the deterministic half landed
  in wave 1).
- **Wave 4 (M4) — rich + interactive blocks.** Mermaid (theme-aware, node-anchored annotation via node
  identity), `:::comparison`, `:::question` controls that submit structured answers, `:::diff`.
- **Wave 5 (M5) — export + Guardrails handoff.** Minimal self-contained HTML export (inline local
  assets, `file://` redaction, size caps) + emit canonical reviewed markdown; the **wave-1 handoff
  fixture** is the acceptance gate.
- **Wave 6 (M6) — agent skill + polish.** Bundled `charter` `SKILL.md` + playbooks; distribution polish.

## Guardrails execution mapping (waves + JIT)

Charter's milestones are ordered stages whose later stages build on earlier ones' *materialized*
artifacts, so `plan-breakdown` emits them as a **WAVED** plan (native, #254): a nested
`<plan>/<wave-NN>/…` layout, one wave per milestone, each with its own entry gate → task DAG → exit gate.

- **One `/plan-breakdown` over the whole plan** produces the wave folders. Waves that are fully
  designable up front are authored now; a wave that references artifacts an upstream wave hasn't produced
  yet is left as a **stub** (declared dir, empty `tasks/`).
- **JIT staged-breakdown per stub wave.** `guardrails run` executes the ready waves, then **honest-halts**
  at the first stub wave and prints the materialized *integration worktree*. Re-invoke `/plan-breakdown`
  in JIT mode to author that wave **against the real upstream code** (no guessing at signatures/paths),
  then resume.
- **A review gate per wave.** Each wave is a mini-plan with its own review marker; the honest-halt at a
  stub wave is the pause where the human reviews that wave's draft before it runs
  (`/guardrails-review <plan>/<wave>` → `guardrails mark-reviewed <plan>/<wave>`). *(As of Guardrails
  preview.41 the whole-plan diagram is **wave-aware** — labeled wave subgraphs, a barrier edge, and a
  visible JIT-stub node — and a per-wave `graph <plan>/<wave>` view exists (#355/#356).)*

One breakdown pass, JIT within later waves as their upstream materializes, a review gate per wave — which
is exactly why a separate M0 vertical slice is unnecessary here.

## Out of scope for v1 (each tracked as an issue)

Deferred features — every one has a tracking issue so it outlives this plan:
- Hosted share/publish — [#4](https://github.com/Servant-Software-LLC/Charter/issues/4)
- Layout-audit gate (overflow/clipping/overlap) — [#5](https://github.com/Servant-Software-LLC/Charter/issues/5)
- Multi-artifact sessions — [#2](https://github.com/Servant-Software-LLC/Charter/issues/2)
- Review-round versioning + diff — [#3](https://github.com/Servant-Software-LLC/Charter/issues/3)
- Recap mode (build-from-diff) — [#1](https://github.com/Servant-Software-LLC/Charter/issues/1) (see *Natural extension* above)

**Telemetry:** v1 ships **none** (a decision — zero analytics dependency). Any *future* telemetry must be
vendor-neutral (no SDK lock-in) and is tracked in [#6](https://github.com/Servant-Software-LLC/Charter/issues/6);
Charter will never adopt Lavish's default-on model. See *Trust, security & telemetry*.

## Natural extension — recap mode (v2, not v1)

Charter's renderer and review loop are **direction-agnostic**: the same block catalog and
comment-in-place surface work whether the blocks describe a change *to be made* (a plan) or a change
*already made* (a recap of a diff). This mirrors Builder.io's sibling **`/visual-recap`**, whose whole
premise is *"the same plan data model serves both directions."*

So a `charter recap <PR | branch | diff>` mode is a cheap, high-value future addition — the only
genuinely new piece is a **diff → blocks** input adapter (map schema/API/file/architecture changes to
`:::diagram` / `:::file-tree` / `:::diff` / table blocks); the renderer, server, annotation loop, and
export are all reused unchanged.

**Open question (for the architect):** in this ecosystem a post-execution recap overlaps with
Guardrails' own completion reporting (and its `uber-report`). De-conflict before building — Charter
should own the *render + review surface* (blocks + annotation over a diff), not duplicate Guardrails'
execution reporting. Deferred to v2, noted here so the v1 architecture stays **recap-ready**: keep
plan-only assumptions out of the block model and the source-map. **Tracked in
[#1](https://github.com/Servant-Software-LLC/Charter/issues/1)** so it outlives this plan.

## Trust, security & telemetry

Loopback-only default; per-session capability key; path-confinement + CSRF on state-changing routes;
export redaction of absolute `file://` paths. These are reimplemented and tested in C#, not silently
inherited.

**Telemetry: none in v1** — zero analytics dependency, zero data egress. If ever added it must be
**vendor-neutral**: a best-effort `HttpClient` POST of a tiny self-defined event to a *configurable*
endpoint (BCL only), or OpenTelemetry (OTLP) — **never a vendor SDK** (Application Insights / Segment /
Mixpanel / …). A default-*off* flag does **not** avoid lock-in: the dependency is compiled into the
binary regardless of the runtime default, so the safeguard is *not adding a vendor SDK*, not the off
switch. Tracked in [#6](https://github.com/Servant-Software-LLC/Charter/issues/6).

## Open items to pin early

- The exact **reviewed-markdown handoff shape** — defined + fixtured in wave 1 (M1).
- **Store concurrency** — single-writer or locking for the session JSON (Lavish does whole-file
  read-modify-write; a concurrent `poll` + `prompts` can race).
- The **JS vendor → bundle → test pipeline** (a small node toolchain in CI) + the vendored-SDK
  attribution/version-pin contract.
- **Review rounds / versioning / diff** — v2 ([#3](https://github.com/Servant-Software-LLC/Charter/issues/3)); the model needs a round concept before round 2.

## Risks

Anchor source-map correctness is the deepest (the source↔render split Lavish never had); server
AOT/size under self-contained single-file; browser-test determinism (mitigated by a chosen harness in
M3); JS re-port drift (mitigated by keeping the SDK lean and purpose-built, not a Lavish clone).

## References

**Prior art — the two ideas Charter combines**

- **[Lavish (lavish-axi)](https://github.com/kunchenguid/lavish-axi)** by Kun Chen (MIT) — the
  comment-in-place review loop (local loopback server + in-browser annotation of elements / text ranges /
  diagram nodes + long-poll feedback + live reload + self-contained export) that Charter reimplements
  lean in C#; its SDK JS is adapted with attribution.
- **[Builder.io visual-plan / Agent-Native Plans](https://github.com/BuilderIO/skills/tree/main/skills/visual-plan)**
  ([rationale](https://www.builder.io/blog/claude-code-plan)) — the block-authoring model (a validated
  block *schema* + a `question-form` / `visual-intake` elicitation surface) that Charter reproduces with
  markdown+directives. Its sibling [`/visual-recap`](https://github.com/BuilderIO/skills/tree/main/skills/visual-recap)
  motivates the recap-mode extension ([#1](https://github.com/Servant-Software-LLC/Charter/issues/1)).

**Libraries**

- **[Markdig](https://github.com/xoofx/markdig)** ([NuGet](https://www.nuget.org/packages/Markdig)) — the
  C# CommonMark processor. Its [custom containers](https://github.com/xoofx/markdig/blob/master/src/Markdig.Tests/Specs/CustomContainerSpecs.md)
  (`:::name`) plus generic attributes (`{#id key=val}`) back Charter's block directives; each block
  payload is validated against a C# record.
- **[Adaptive Cards](https://adaptivecards.io/)** (Microsoft) — its typed `Input.*` + `Action.Submit`
  shape is the *design template* for the `:::question` schema (see the
  [schema explorer](https://adaptivecards.io/explorer/)). **Not a dependency:** the
  [.NET HTML renderer](https://learn.microsoft.com/en-us/adaptive-cards/sdk/rendering-cards/net-html/render-a-card)
  supports only schema 1.0 and isn't a document format — so Charter borrows the shape, not the library.

## Acknowledgements

Charter combines **Lavish**'s in-place review loop and **visual-plan**'s block authoring; it replaces
neither. Adapted Lavish JS is MIT, attributed in `sdk/`.
