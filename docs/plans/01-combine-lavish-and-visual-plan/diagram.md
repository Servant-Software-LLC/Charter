<!-- guardrails:graph v1 source-sha256=58bf4a859e3ece816d25fddf4bf2e2de5ee382d159845345db92ad93d219e5e6 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1_preflights["Wave 1 Entry Gate"]
  end
  style wave_1_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_1["Wave 1 — renderer-core"]
    subgraph task_wave_01_renderer_core_01_author_tests_core_renderer["01-author-tests-core-renderer"]
      task_wave_01_renderer_core_01_author_tests_core_renderer_gr_0["01-tests-build"]:::guardrail
      task_wave_01_renderer_core_01_author_tests_core_renderer_gr_1["02-tests-fail-on-stubs"]:::guardrail
    end
    style task_wave_01_renderer_core_01_author_tests_core_renderer fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_renderer_core_02_implement_core_renderer["02-implement-core-renderer"]
      task_wave_01_renderer_core_02_implement_core_renderer_gr_0["01-core-tests-pass"]:::guardrail
    end
    style task_wave_01_renderer_core_02_implement_core_renderer fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_01_renderer_core_03_wire_charter_render_cli["03-wire-charter-render-cli"]
      task_wave_01_renderer_core_03_wire_charter_render_cli_gr_0["01-render-command-wired"]:::guardrail
      task_wave_01_renderer_core_03_wire_charter_render_cli_gr_1["02-render-smoke"]:::guardrail
    end
    style task_wave_01_renderer_core_03_wire_charter_render_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_1 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_1_guardrails["Wave 1 Exit Gate"]
    wave_1_guardrails_0["01-core-builds"]:::guardrail
  end
  style wave_1_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2_preflights["Wave 2 Entry Gate"]
  end
  style wave_2_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2["Wave 2 — review-server"]
    wave_2_stub["⏸ JIT stub — run halts here for breakdown"]
    style wave_2_stub fill:#fef9c3,stroke:#ca8a04,color:#713f12;
  end
  style wave_2 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_2_guardrails["Wave 2 Exit Gate"]
  end
  style wave_2_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph plan_guardrails["Terminal Gate"]
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> wave_1_preflights
  wave_1_preflights --> task_wave_01_renderer_core_01_author_tests_core_renderer
  task_wave_01_renderer_core_01_author_tests_core_renderer --> task_wave_01_renderer_core_02_implement_core_renderer
  task_wave_01_renderer_core_02_implement_core_renderer --> task_wave_01_renderer_core_03_wire_charter_render_cli
  task_wave_01_renderer_core_03_wire_charter_render_cli --> wave_1_guardrails
  wave_2_preflights --> wave_2_stub
  wave_2_stub --> wave_2_guardrails
  wave_1_guardrails -.->|"🔒 wave barrier"| wave_2_preflights
  wave_2_guardrails --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
