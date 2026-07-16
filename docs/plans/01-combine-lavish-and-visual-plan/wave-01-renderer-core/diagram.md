<!-- guardrails:graph v1 source-sha256=32e83de61944b71250e3de773990327f8684b1b9500dfd1288f0ffb37c1fbaa3 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_01_renderer_core_01_author_tests_core_renderer["wave-01-renderer-core/01-author-tests-core-renderer"]
    task_wave_01_renderer_core_01_author_tests_core_renderer_gr_0["01-tests-build"]:::guardrail
    task_wave_01_renderer_core_01_author_tests_core_renderer_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_wave_01_renderer_core_01_author_tests_core_renderer fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_renderer_core_02_implement_core_renderer["wave-01-renderer-core/02-implement-core-renderer"]
    task_wave_01_renderer_core_02_implement_core_renderer_gr_0["01-core-tests-pass"]:::guardrail
  end
  style task_wave_01_renderer_core_02_implement_core_renderer fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_01_renderer_core_03_wire_charter_render_cli["wave-01-renderer-core/03-wire-charter-render-cli"]
    task_wave_01_renderer_core_03_wire_charter_render_cli_gr_0["01-render-command-wired"]:::guardrail
    task_wave_01_renderer_core_03_wire_charter_render_cli_gr_1["02-render-smoke"]:::guardrail
  end
  style task_wave_01_renderer_core_03_wire_charter_render_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-core-builds"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_01_renderer_core_01_author_tests_core_renderer
  task_wave_01_renderer_core_01_author_tests_core_renderer --> task_wave_01_renderer_core_02_implement_core_renderer
  task_wave_01_renderer_core_02_implement_core_renderer --> task_wave_01_renderer_core_03_wire_charter_render_cli
  task_wave_01_renderer_core_03_wire_charter_render_cli --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
