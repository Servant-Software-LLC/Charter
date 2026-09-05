<!-- guardrails:graph v1 source-sha256=08cb429303b1dc757e22ccc0c8d6a287ea4bc0ab88b800cffbbcf55e5bbbd99c -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-baseline-server-tests-green"]:::preflight
    plan_preflights_1["02-baseline-cli-tests-green"]:::preflight
    plan_preflights_2["03-baseline-browser-tests-green"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_unknown_read["01-author-tests-unknown-read"]
    task_01_author_tests_unknown_read_gr_0["01-tests-build"]:::guardrail
    task_01_author_tests_unknown_read_gr_1["02-tests-fail-on-stubs"]:::guardrail
  end
  style task_01_author_tests_unknown_read fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_unknown_read["02-implement-unknown-read"]
    task_02_implement_unknown_read_gr_0["01-unknown-read-tests-pass"]:::guardrail
  end
  style task_02_implement_unknown_read fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_drain_unknown["03-author-tests-drain-unknown"]
    task_03_author_tests_drain_unknown_gr_0["01-tests-build"]:::guardrail
    task_03_author_tests_drain_unknown_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_03_author_tests_drain_unknown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_drain_unknown["04-implement-drain-unknown"]
    task_04_implement_drain_unknown_gr_0["01-drain-unknown-tests-pass"]:::guardrail
  end
  style task_04_implement_drain_unknown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_05_author_tests_bridge_unknown["05-author-tests-bridge-unknown"]
    task_05_author_tests_bridge_unknown_gr_0["01-tests-build"]:::guardrail
    task_05_author_tests_bridge_unknown_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_05_author_tests_bridge_unknown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_06_implement_bridge_unknown["06-implement-bridge-unknown"]
    task_06_implement_bridge_unknown_gr_0["01-bridge-unknown-tests-pass"]:::guardrail
  end
  style task_06_implement_bridge_unknown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_07_author_tests_panel_declines_unknown["07-author-tests-panel-declines-unknown"]
    task_07_author_tests_panel_declines_unknown_gr_0["01-tests-build"]:::guardrail
    task_07_author_tests_panel_declines_unknown_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_07_author_tests_panel_declines_unknown_gr_2["03-every-test-carries-the-feature-trait"]:::guardrail
    task_07_author_tests_panel_declines_unknown_gr_3["04-no-forbidden-playwright-idioms"]:::guardrail
  end
  style task_07_author_tests_panel_declines_unknown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_08_implement_panel_declines_unknown["08-implement-panel-declines-unknown"]
    task_08_implement_panel_declines_unknown_gr_0["01-panel-unknown-tests-pass"]:::guardrail
  end
  style task_08_implement_panel_declines_unknown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_09_author_tests_log_not_loaded["09-author-tests-log-not-loaded"]
    task_09_author_tests_log_not_loaded_gr_0["01-tests-build"]:::guardrail
    task_09_author_tests_log_not_loaded_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_09_author_tests_log_not_loaded_gr_2["03-every-test-carries-the-feature-trait"]:::guardrail
    task_09_author_tests_log_not_loaded_gr_3["04-no-forbidden-playwright-idioms"]:::guardrail
  end
  style task_09_author_tests_log_not_loaded fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_10_implement_log_not_loaded["10-implement-log-not-loaded"]
    task_10_implement_log_not_loaded_gr_0["01-log-not-loaded-tests-pass"]:::guardrail
  end
  style task_10_implement_log_not_loaded fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-review-log-union-verified"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_unknown_read
  task_01_author_tests_unknown_read --> task_02_implement_unknown_read
  task_02_implement_unknown_read --> task_03_author_tests_drain_unknown
  task_02_implement_unknown_read --> task_05_author_tests_bridge_unknown
  task_03_author_tests_drain_unknown --> task_04_implement_drain_unknown
  task_05_author_tests_bridge_unknown --> task_06_implement_bridge_unknown
  task_06_implement_bridge_unknown --> task_07_author_tests_panel_declines_unknown
  task_07_author_tests_panel_declines_unknown --> task_08_implement_panel_declines_unknown
  task_08_implement_panel_declines_unknown --> task_09_author_tests_log_not_loaded
  task_09_author_tests_log_not_loaded --> task_10_implement_log_not_loaded
  task_04_implement_drain_unknown --> plan_guardrails
  task_10_implement_log_not_loaded --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
