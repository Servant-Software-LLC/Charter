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
  a JS runtime), so **markdown + `:::` directives via Markdig, each validated against a C# record, is
  the correct C# reproduction of Builder's actual architecture** — not a weaker substitute.
- **Hybrid split by concern:** expressive markdown/directives for narrative + visuals (strict format
  degrades LLM *reasoning*), plus a **schema-validated `:::question` block** rendered to native HTML
  `<form>` inputs for elicitation (borrow Adaptive Cards' `Input`/`Action.Submit` *shape* as a
  template, not a dependency), plus a **`:::custom-html` escape hatch** for raw-HTML ceiling cases.
- **No more-expressive *viable* standard exists.** Raw HTML is absolutely more expressive but least
  constrainable/anchorable — against Charter's "reviewable, validated" differentiator.

### D2 — Review loop: reimplement **lean** in C# (not a full Lavish port, not a subprocess)

Charter stays an **independent, single node-free binary** in its own stack — the reason it exists.
- **Lean, purpose-built surface:** serve artifact + inject SDK + annotate (element / text-range /
  diagram-node) + long-poll feedback + live reload. We own a **small** server contract, not Lavish's
  ~18 routes.
- **Explicitly OUT for v1:** hosted export/share, default-on telemetry, the layout-audit gate,
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

## Milestones (M0 spike first; real unknowns front-loaded)

- **M0 — end-to-end spike (throwaway).** One block → render with a stable ID → serve → annotate →
  long-poll → **map the anchor back to a markdown line** → emit markdown that passes
  `guardrails validate`. Proves the whole loop, the anchor source-map, and the handoff *before* any
  polish. Pins the reviewed-markdown handoff shape as a fixture.
- **M1 — renderer core + source-map.** Markdig + prose/heading/list/callout/table/code; content-derived
  stable IDs; anchor→markdown source-map; `charter render plan.mdx -o plan.html`. *Accept:* golden HTML
  per block **and** an anchor-survival test (annotate a block, edit an *unrelated* block, re-render,
  anchor still resolves).
- **M2 — lean review server.** `Charter.Server` behind `IReviewServer`; **spike Kestrel vs
  HttpListener for single-file/AOT size**; serve artifact + inject SDK + live reload (watch the *file*);
  loopback-only + per-session capability key + path-confinement, all tested. Read-only preview.
- **M3 — annotation + feedback loop.** Lean embedded JS SDK (element + text-range + node annotation,
  postMessage) + a minimal server contract (`/api/sessions`, `/api/:key/prompts`, `/api/poll`
  long-poll, `/events` SSE); session store; anchor→markdown round-trip. **Choose the browser-test
  harness here (Playwright).** *Accept:* a headless test queues an annotation and `poll` returns it with
  the correct source anchor.
- **M4 — rich + interactive blocks.** Mermaid (theme-aware, node-anchored annotation via node
  identity), `:::comparison`, `:::question` controls that submit structured answers, `:::diff`.
- **M5 — export + Guardrails handoff.** Minimal self-contained HTML export (inline local assets,
  `file://` redaction, size caps) + emit canonical reviewed markdown; the M0 `guardrails validate`
  fixture is the acceptance gate.
- **M6 — agent skill + polish.** Bundled `charter` `SKILL.md` + playbooks; distribution polish.

## Out of scope for v1 (named deliberately)

Hosted share/publish; default-on telemetry; the layout-audit gate; multi-artifact sessions; review-round
diffing. Each is a real Lavish feature we are *choosing* not to clone yet — noted so the estimate is honest.

## Trust, security & telemetry

Loopback-only default; per-session capability key; path-confinement + CSRF on state-changing routes;
export redaction of absolute `file://` paths; **telemetry OFF / opt-in**. These are reimplemented and
tested in C#, not silently inherited.

## Open items to pin early

- The exact **reviewed-markdown handoff shape** — defined + fixtured in M0.
- **Store concurrency** — single-writer or locking for the session JSON (Lavish does whole-file
  read-modify-write; a concurrent `poll` + `prompts` can race).
- The **JS vendor → bundle → test pipeline** (a small node toolchain in CI) + the vendored-SDK
  attribution/version-pin contract.
- **Review rounds / versioning / diff** — v2; note the model needs a round concept before round 2.

## Risks

Anchor source-map correctness is the deepest (the source↔render split Lavish never had); server
AOT/size under self-contained single-file; browser-test determinism (mitigated by a chosen harness in
M3); JS re-port drift (mitigated by keeping the SDK lean and purpose-built, not a Lavish clone).

## Acknowledgements

Charter combines **Lavish**'s in-place review loop and **visual-plan**'s block authoring; it replaces
neither. Adapted Lavish JS is MIT, attributed in `sdk/`.
