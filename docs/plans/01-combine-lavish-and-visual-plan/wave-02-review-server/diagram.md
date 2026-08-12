<!-- guardrails:graph v1 source-sha256=a9c25d2eba156fea8c954759b029c532da4490b6a16e438c3ccad280b0eaac04 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave1-renderer-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_02_review_server_01_scaffold_server_projects["wave-02-review-server/01-scaffold-server-projects"]
    task_wave_02_review_server_01_scaffold_server_projects_gr_0["01-projects-registered"]:::guardrail
  end
  style task_wave_02_review_server_01_scaffold_server_projects fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_review_server_02_author_tests_review_server["wave-02-review-server/02-author-tests-review-server"]
    task_wave_02_review_server_02_author_tests_review_server_gr_0["01-tests-build"]:::guardrail
    task_wave_02_review_server_02_author_tests_review_server_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_02_review_server_02_author_tests_review_server_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_02_review_server_02_author_tests_review_server fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_review_server_03_implement_review_server["wave-02-review-server/03-implement-review-server"]
    task_wave_02_review_server_03_implement_review_server_gr_0["01-server-tests-pass"]:::guardrail
  end
  style task_wave_02_review_server_03_implement_review_server fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_02_review_server_04_wire_review_cli["wave-02-review-server/04-wire-review-cli"]
    task_wave_02_review_server_04_wire_review_cli_gr_0["01-review-command-wired"]:::guardrail
    task_wave_02_review_server_04_wire_review_cli_gr_1["02-review-serve-smoke"]:::guardrail
  end
  style task_wave_02_review_server_04_wire_review_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-server-builds-and-tests"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_02_review_server_01_scaffold_server_projects
  task_wave_02_review_server_01_scaffold_server_projects --> task_wave_02_review_server_02_author_tests_review_server
  task_wave_02_review_server_02_author_tests_review_server --> task_wave_02_review_server_03_implement_review_server
  task_wave_02_review_server_03_implement_review_server --> task_wave_02_review_server_04_wire_review_cli
  task_wave_02_review_server_04_wire_review_cli --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
