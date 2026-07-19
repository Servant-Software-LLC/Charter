<!-- guardrails:graph v1 source-sha256=51018e62f3a123f9c4547d26afd4d898cfb58582ff2c47334e09ffb605fe03bd -->

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
      task_wave_01_renderer_core_01_author_tests_core_renderer_gr_2["03-covers-key-behaviors"]:::guardrail
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
    wave_2_preflights_0["01-wave1-renderer-materialized"]:::preflight
  end
  style wave_2_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_2["Wave 2 — review-server"]
    subgraph task_wave_02_review_server_01_scaffold_server_projects["01-scaffold-server-projects"]
      task_wave_02_review_server_01_scaffold_server_projects_gr_0["01-projects-registered"]:::guardrail
    end
    style task_wave_02_review_server_01_scaffold_server_projects fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_review_server_02_author_tests_review_server["02-author-tests-review-server"]
      task_wave_02_review_server_02_author_tests_review_server_gr_0["01-tests-build"]:::guardrail
      task_wave_02_review_server_02_author_tests_review_server_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_02_review_server_02_author_tests_review_server_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_02_review_server_02_author_tests_review_server fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_review_server_03_implement_review_server["03-implement-review-server"]
      task_wave_02_review_server_03_implement_review_server_gr_0["01-server-tests-pass"]:::guardrail
    end
    style task_wave_02_review_server_03_implement_review_server fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_02_review_server_04_wire_review_cli["04-wire-review-cli"]
      task_wave_02_review_server_04_wire_review_cli_gr_0["01-review-command-wired"]:::guardrail
      task_wave_02_review_server_04_wire_review_cli_gr_1["02-review-serve-smoke"]:::guardrail
    end
    style task_wave_02_review_server_04_wire_review_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_2 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_2_guardrails["Wave 2 Exit Gate"]
    wave_2_guardrails_0["01-server-builds-and-tests"]:::guardrail
  end
  style wave_2_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3_preflights["Wave 3 Entry Gate"]
    wave_3_preflights_0["01-wave2-review-server-materialized"]:::preflight
  end
  style wave_3_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_3["Wave 3 — annotation-feedback"]
    subgraph task_wave_03_annotation_feedback_01_author_tests_session_store["01-author-tests-session-store"]
      task_wave_03_annotation_feedback_01_author_tests_session_store_gr_0["01-build-passes"]:::guardrail
      task_wave_03_annotation_feedback_01_author_tests_session_store_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_03_annotation_feedback_01_author_tests_session_store_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_03_annotation_feedback_01_author_tests_session_store fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_annotation_feedback_02_implement_session_store["02-implement-session-store"]
      task_wave_03_annotation_feedback_02_implement_session_store_gr_0["01-store-tests-pass"]:::guardrail
    end
    style task_wave_03_annotation_feedback_02_implement_session_store fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_annotation_feedback_03_author_tests_annotation_api["03-author-tests-annotation-api"]
      task_wave_03_annotation_feedback_03_author_tests_annotation_api_gr_0["01-build-passes"]:::guardrail
      task_wave_03_annotation_feedback_03_author_tests_annotation_api_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_03_annotation_feedback_03_author_tests_annotation_api_gr_2["03-covers-round-trip"]:::guardrail
    end
    style task_wave_03_annotation_feedback_03_author_tests_annotation_api fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_annotation_feedback_04_implement_annotation_api["04-implement-annotation-api"]
      task_wave_03_annotation_feedback_04_implement_annotation_api_gr_0["01-api-tests-pass"]:::guardrail
    end
    style task_wave_03_annotation_feedback_04_implement_annotation_api fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_annotation_feedback_05_build_annotation_sdk["05-build-annotation-sdk"]
      task_wave_03_annotation_feedback_05_build_annotation_sdk_gr_0["01-sdk-file-exists"]:::guardrail
      task_wave_03_annotation_feedback_05_build_annotation_sdk_gr_1["02-sdk-structure"]:::guardrail
    end
    style task_wave_03_annotation_feedback_05_build_annotation_sdk fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_03_annotation_feedback_06_wire_sdk_into_server["06-wire-sdk-into-server"]
      task_wave_03_annotation_feedback_06_wire_sdk_into_server_gr_0["01-sdk-embedded"]:::guardrail
      task_wave_03_annotation_feedback_06_wire_sdk_into_server_gr_1["02-served-sdk-real"]:::guardrail
    end
    style task_wave_03_annotation_feedback_06_wire_sdk_into_server fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_3 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_3_guardrails["Wave 3 Exit Gate"]
    wave_3_guardrails_0["01-annotation-feedback-complete"]:::guardrail
    wave_3_guardrails_1["02-union-clean"]:::guardrail
  end
  style wave_3_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4_preflights["Wave 4 Entry Gate"]
  end
  style wave_4_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4["Wave 4 — rich-blocks"]
    wave_4_stub["⏸ JIT stub — run halts here for breakdown"]
    style wave_4_stub fill:#fef9c3,stroke:#ca8a04,color:#713f12;
  end
  style wave_4 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_4_guardrails["Wave 4 Exit Gate"]
  end
  style wave_4_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph plan_guardrails["Terminal Gate"]
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> wave_1_preflights
  wave_1_preflights --> task_wave_01_renderer_core_01_author_tests_core_renderer
  task_wave_01_renderer_core_01_author_tests_core_renderer --> task_wave_01_renderer_core_02_implement_core_renderer
  task_wave_01_renderer_core_02_implement_core_renderer --> task_wave_01_renderer_core_03_wire_charter_render_cli
  task_wave_01_renderer_core_03_wire_charter_render_cli --> wave_1_guardrails
  wave_2_preflights --> task_wave_02_review_server_01_scaffold_server_projects
  task_wave_02_review_server_01_scaffold_server_projects --> task_wave_02_review_server_02_author_tests_review_server
  task_wave_02_review_server_02_author_tests_review_server --> task_wave_02_review_server_03_implement_review_server
  task_wave_02_review_server_03_implement_review_server --> task_wave_02_review_server_04_wire_review_cli
  task_wave_02_review_server_04_wire_review_cli --> wave_2_guardrails
  wave_3_preflights --> task_wave_03_annotation_feedback_01_author_tests_session_store
  wave_3_preflights --> task_wave_03_annotation_feedback_05_build_annotation_sdk
  task_wave_03_annotation_feedback_01_author_tests_session_store --> task_wave_03_annotation_feedback_02_implement_session_store
  task_wave_03_annotation_feedback_02_implement_session_store --> task_wave_03_annotation_feedback_03_author_tests_annotation_api
  task_wave_03_annotation_feedback_03_author_tests_annotation_api --> task_wave_03_annotation_feedback_04_implement_annotation_api
  task_wave_03_annotation_feedback_04_implement_annotation_api --> task_wave_03_annotation_feedback_06_wire_sdk_into_server
  task_wave_03_annotation_feedback_05_build_annotation_sdk --> task_wave_03_annotation_feedback_06_wire_sdk_into_server
  task_wave_03_annotation_feedback_06_wire_sdk_into_server --> wave_3_guardrails
  wave_4_preflights --> wave_4_stub
  wave_4_stub --> wave_4_guardrails
  wave_1_guardrails -.->|"🔒 wave barrier"| wave_2_preflights
  wave_2_guardrails -.->|"🔒 wave barrier"| wave_3_preflights
  wave_3_guardrails -.->|"🔒 wave barrier"| wave_4_preflights
  wave_4_guardrails --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
