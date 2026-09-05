<!-- guardrails:graph v1 source-sha256=7b5b04aa72a2326ea8cc469d146b6b160c6f3061370fe77dd5ee375544c57546 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_01_author_tests_expand_affordance["01-author-tests-expand-affordance"]
    task_01_author_tests_expand_affordance_gr_0["01-tests-build"]:::guardrail
    task_01_author_tests_expand_affordance_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_01_author_tests_expand_affordance_gr_2["03-every-test-carries-the-feature-trait"]:::guardrail
  end
  style task_01_author_tests_expand_affordance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_02_implement_expand_affordance["02-implement-expand-affordance"]
    task_02_implement_expand_affordance_gr_0["01-expand-affordance-tests-pass"]:::guardrail
    task_02_implement_expand_affordance_gr_1["02-exported-artifact-unchanged"]:::guardrail
  end
  style task_02_implement_expand_affordance fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_03_author_tests_expand_invariants["03-author-tests-expand-invariants"]
    task_03_author_tests_expand_invariants_gr_0["01-tests-build"]:::guardrail
    task_03_author_tests_expand_invariants_gr_1["02-tests-fail-on-current-code"]:::guardrail
  end
  style task_03_author_tests_expand_invariants fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_04_implement_expand_invariants["04-implement-expand-invariants"]
    task_04_implement_expand_invariants_gr_0["01-expand-invariant-tests-pass"]:::guardrail
    task_04_implement_expand_invariants_gr_1["02-exported-artifact-unchanged"]:::guardrail
    task_04_implement_expand_invariants_gr_2["03-both-engines-pass"]:::guardrail
  end
  style task_04_implement_expand_invariants fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-solution-builds"]:::guardrail
    plan_guardrails_1["02-all-tests-pass"]:::guardrail
    plan_guardrails_2["03-sdk-union-verified"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_01_author_tests_expand_affordance
  task_01_author_tests_expand_affordance --> task_02_implement_expand_affordance
  task_01_author_tests_expand_affordance --> task_03_author_tests_expand_invariants
  task_02_implement_expand_affordance --> task_04_implement_expand_invariants
  task_03_author_tests_expand_invariants --> task_04_implement_expand_invariants
  task_04_implement_expand_invariants --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
