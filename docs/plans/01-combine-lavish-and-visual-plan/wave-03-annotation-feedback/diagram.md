<!-- guardrails:graph v1 source-sha256=7d224d7e080e337020af87df2585b2f7a741baf4d94a203e21d7e4c4d232dd51 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave2-review-server-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_03_annotation_feedback_01_author_tests_session_store["wave-03-annotation-feedback/01-author-tests-session-store"]
    task_wave_03_annotation_feedback_01_author_tests_session_store_gr_0["01-build-passes"]:::guardrail
    task_wave_03_annotation_feedback_01_author_tests_session_store_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_annotation_feedback_01_author_tests_session_store_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_03_annotation_feedback_01_author_tests_session_store fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_annotation_feedback_02_implement_session_store["wave-03-annotation-feedback/02-implement-session-store"]
    task_wave_03_annotation_feedback_02_implement_session_store_gr_0["01-store-tests-pass"]:::guardrail
  end
  style task_wave_03_annotation_feedback_02_implement_session_store fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_annotation_feedback_03_author_tests_annotation_api["wave-03-annotation-feedback/03-author-tests-annotation-api"]
    task_wave_03_annotation_feedback_03_author_tests_annotation_api_gr_0["01-build-passes"]:::guardrail
    task_wave_03_annotation_feedback_03_author_tests_annotation_api_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_03_annotation_feedback_03_author_tests_annotation_api_gr_2["03-covers-round-trip"]:::guardrail
  end
  style task_wave_03_annotation_feedback_03_author_tests_annotation_api fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_annotation_feedback_04_implement_annotation_api["wave-03-annotation-feedback/04-implement-annotation-api"]
    task_wave_03_annotation_feedback_04_implement_annotation_api_gr_0["01-api-tests-pass"]:::guardrail
  end
  style task_wave_03_annotation_feedback_04_implement_annotation_api fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_annotation_feedback_05_build_annotation_sdk["wave-03-annotation-feedback/05-build-annotation-sdk"]
    task_wave_03_annotation_feedback_05_build_annotation_sdk_gr_0["01-sdk-file-exists"]:::guardrail
    task_wave_03_annotation_feedback_05_build_annotation_sdk_gr_1["02-sdk-structure"]:::guardrail
  end
  style task_wave_03_annotation_feedback_05_build_annotation_sdk fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_03_annotation_feedback_06_wire_sdk_into_server["wave-03-annotation-feedback/06-wire-sdk-into-server"]
    task_wave_03_annotation_feedback_06_wire_sdk_into_server_gr_0["01-sdk-embedded"]:::guardrail
    task_wave_03_annotation_feedback_06_wire_sdk_into_server_gr_1["02-served-sdk-real"]:::guardrail
  end
  style task_wave_03_annotation_feedback_06_wire_sdk_into_server fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-annotation-feedback-complete"]:::guardrail
    plan_guardrails_1["02-union-clean"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_03_annotation_feedback_01_author_tests_session_store
  plan_preflights --> task_wave_03_annotation_feedback_05_build_annotation_sdk
  task_wave_03_annotation_feedback_01_author_tests_session_store --> task_wave_03_annotation_feedback_02_implement_session_store
  task_wave_03_annotation_feedback_02_implement_session_store --> task_wave_03_annotation_feedback_03_author_tests_annotation_api
  task_wave_03_annotation_feedback_03_author_tests_annotation_api --> task_wave_03_annotation_feedback_04_implement_annotation_api
  task_wave_03_annotation_feedback_04_implement_annotation_api --> task_wave_03_annotation_feedback_06_wire_sdk_into_server
  task_wave_03_annotation_feedback_05_build_annotation_sdk --> task_wave_03_annotation_feedback_06_wire_sdk_into_server
  task_wave_03_annotation_feedback_06_wire_sdk_into_server --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
