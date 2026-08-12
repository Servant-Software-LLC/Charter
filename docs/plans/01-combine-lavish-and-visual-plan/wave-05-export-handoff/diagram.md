<!-- guardrails:graph v1 source-sha256=4fdae6a5085abe98b93ad3f76161a9c7f70ae1b5ecfe18c19fd859e2c2f235d6 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave4-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_05_export_handoff_01_author_tests_artifact_exporter["wave-05-export-handoff/01-author-tests-artifact-exporter"]
    task_wave_05_export_handoff_01_author_tests_artifact_exporter_gr_0["01-tests-build"]:::guardrail
    task_wave_05_export_handoff_01_author_tests_artifact_exporter_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_05_export_handoff_01_author_tests_artifact_exporter_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_05_export_handoff_01_author_tests_artifact_exporter fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_05_export_handoff_02_implement_artifact_exporter["wave-05-export-handoff/02-implement-artifact-exporter"]
    task_wave_05_export_handoff_02_implement_artifact_exporter_gr_0["01-exporter-tests-pass"]:::guardrail
    task_wave_05_export_handoff_02_implement_artifact_exporter_gr_1["02-core-has-no-server-dependency"]:::guardrail
  end
  style task_wave_05_export_handoff_02_implement_artifact_exporter fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_05_export_handoff_03_wire_export_cli["wave-05-export-handoff/03-wire-export-cli"]
    task_wave_05_export_handoff_03_wire_export_cli_gr_0["01-export-command-wired"]:::guardrail
    task_wave_05_export_handoff_03_wire_export_cli_gr_1["02-export-smoke"]:::guardrail
  end
  style task_wave_05_export_handoff_03_wire_export_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_05_export_handoff_04_author_tests_handoff_markdown["wave-05-export-handoff/04-author-tests-handoff-markdown"]
    task_wave_05_export_handoff_04_author_tests_handoff_markdown_gr_0["01-tests-build"]:::guardrail
    task_wave_05_export_handoff_04_author_tests_handoff_markdown_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_05_export_handoff_04_author_tests_handoff_markdown_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_05_export_handoff_04_author_tests_handoff_markdown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_05_export_handoff_05_implement_handoff_markdown["wave-05-export-handoff/05-implement-handoff-markdown"]
    task_wave_05_export_handoff_05_implement_handoff_markdown_gr_0["01-handoff-tests-pass"]:::guardrail
    task_wave_05_export_handoff_05_implement_handoff_markdown_gr_1["02-real-dispatch-not-hardcoded"]:::guardrail
  end
  style task_wave_05_export_handoff_05_implement_handoff_markdown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_05_export_handoff_06_wire_handoff_cli["wave-05-export-handoff/06-wire-handoff-cli"]
    task_wave_05_export_handoff_06_wire_handoff_cli_gr_0["01-handoff-command-wired"]:::guardrail
    task_wave_05_export_handoff_06_wire_handoff_cli_gr_1["02-handoff-smoke"]:::guardrail
  end
  style task_wave_05_export_handoff_06_wire_handoff_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-wave5-solution-builds-and-tests"]:::guardrail
    plan_guardrails_1["02-union-clean"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_05_export_handoff_01_author_tests_artifact_exporter
  plan_preflights --> task_wave_05_export_handoff_04_author_tests_handoff_markdown
  task_wave_05_export_handoff_01_author_tests_artifact_exporter --> task_wave_05_export_handoff_02_implement_artifact_exporter
  task_wave_05_export_handoff_02_implement_artifact_exporter --> task_wave_05_export_handoff_03_wire_export_cli
  task_wave_05_export_handoff_03_wire_export_cli --> task_wave_05_export_handoff_06_wire_handoff_cli
  task_wave_05_export_handoff_04_author_tests_handoff_markdown --> task_wave_05_export_handoff_05_implement_handoff_markdown
  task_wave_05_export_handoff_05_implement_handoff_markdown --> task_wave_05_export_handoff_06_wire_handoff_cli
  task_wave_05_export_handoff_06_wire_handoff_cli --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
