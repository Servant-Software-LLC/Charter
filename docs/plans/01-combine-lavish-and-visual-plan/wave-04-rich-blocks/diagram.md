<!-- guardrails:graph v1 source-sha256=6d154340064c1e799f67a52fc6083e671e8b3b12785d11aacd7df49b5657ed96 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave3-annotation-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_04_rich_blocks_01_add_block_kinds["wave-04-rich-blocks/01-add-block-kinds"]
    task_wave_04_rich_blocks_01_add_block_kinds_gr_0["01-block-kinds-declared"]:::guardrail
    task_wave_04_rich_blocks_01_add_block_kinds_gr_1["02-core-builds"]:::guardrail
  end
  style task_wave_04_rich_blocks_01_add_block_kinds fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_02_vendor_mermaid_runtime["wave-04-rich-blocks/02-vendor-mermaid-runtime"]
    task_wave_04_rich_blocks_02_vendor_mermaid_runtime_gr_0["01-mermaid-vendored-offline"]:::guardrail
    task_wave_04_rich_blocks_02_vendor_mermaid_runtime_gr_1["02-core-builds"]:::guardrail
  end
  style task_wave_04_rich_blocks_02_vendor_mermaid_runtime fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_03_author_tests_diagram_block["wave-04-rich-blocks/03-author-tests-diagram-block"]
    task_wave_04_rich_blocks_03_author_tests_diagram_block_gr_0["01-tests-build"]:::guardrail
    task_wave_04_rich_blocks_03_author_tests_diagram_block_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_wave_04_rich_blocks_03_author_tests_diagram_block_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_04_rich_blocks_03_author_tests_diagram_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_04_implement_diagram_block["wave-04-rich-blocks/04-implement-diagram-block"]
    task_wave_04_rich_blocks_04_implement_diagram_block_gr_0["01-diagram-tests-pass"]:::guardrail
    task_wave_04_rich_blocks_04_implement_diagram_block_gr_1["02-renderer-inlines-not-cdn"]:::guardrail
  end
  style task_wave_04_rich_blocks_04_implement_diagram_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_05_author_tests_comparison_block["wave-04-rich-blocks/05-author-tests-comparison-block"]
    task_wave_04_rich_blocks_05_author_tests_comparison_block_gr_0["01-tests-build"]:::guardrail
    task_wave_04_rich_blocks_05_author_tests_comparison_block_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_wave_04_rich_blocks_05_author_tests_comparison_block_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_04_rich_blocks_05_author_tests_comparison_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_06_implement_comparison_block["wave-04-rich-blocks/06-implement-comparison-block"]
    task_wave_04_rich_blocks_06_implement_comparison_block_gr_0["01-comparison-tests-pass"]:::guardrail
  end
  style task_wave_04_rich_blocks_06_implement_comparison_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_07_author_tests_diff_block["wave-04-rich-blocks/07-author-tests-diff-block"]
    task_wave_04_rich_blocks_07_author_tests_diff_block_gr_0["01-tests-build"]:::guardrail
    task_wave_04_rich_blocks_07_author_tests_diff_block_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_wave_04_rich_blocks_07_author_tests_diff_block_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_04_rich_blocks_07_author_tests_diff_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_08_implement_diff_block["wave-04-rich-blocks/08-implement-diff-block"]
    task_wave_04_rich_blocks_08_implement_diff_block_gr_0["01-diff-tests-pass"]:::guardrail
  end
  style task_wave_04_rich_blocks_08_implement_diff_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_09_author_tests_question_schema["wave-04-rich-blocks/09-author-tests-question-schema"]
    task_wave_04_rich_blocks_09_author_tests_question_schema_gr_0["01-build-passes"]:::guardrail
    task_wave_04_rich_blocks_09_author_tests_question_schema_gr_1["02-tests-fail-on-stubs"]:::guardrail
    task_wave_04_rich_blocks_09_author_tests_question_schema_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_04_rich_blocks_09_author_tests_question_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_10_implement_question_schema["wave-04-rich-blocks/10-implement-question-schema"]
    task_wave_04_rich_blocks_10_implement_question_schema_gr_0["01-question-schema-tests-pass"]:::guardrail
  end
  style task_wave_04_rich_blocks_10_implement_question_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_11_author_tests_question_form["wave-04-rich-blocks/11-author-tests-question-form"]
    task_wave_04_rich_blocks_11_author_tests_question_form_gr_0["01-tests-build"]:::guardrail
    task_wave_04_rich_blocks_11_author_tests_question_form_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_wave_04_rich_blocks_11_author_tests_question_form_gr_2["03-covers-key-behaviors"]:::guardrail
  end
  style task_wave_04_rich_blocks_11_author_tests_question_form fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_12_implement_question_form["wave-04-rich-blocks/12-implement-question-form"]
    task_wave_04_rich_blocks_12_implement_question_form_gr_0["01-question-form-tests-pass"]:::guardrail
  end
  style task_wave_04_rich_blocks_12_implement_question_form fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_13_author_tests_answer_submission["wave-04-rich-blocks/13-author-tests-answer-submission"]
    task_wave_04_rich_blocks_13_author_tests_answer_submission_gr_0["01-build-passes"]:::guardrail
    task_wave_04_rich_blocks_13_author_tests_answer_submission_gr_1["02-tests-fail-on-current-code"]:::guardrail
    task_wave_04_rich_blocks_13_author_tests_answer_submission_gr_2["03-covers-answer-round-trip"]:::guardrail
  end
  style task_wave_04_rich_blocks_13_author_tests_answer_submission fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_14_implement_answer_submission["wave-04-rich-blocks/14-implement-answer-submission"]
    task_wave_04_rich_blocks_14_implement_answer_submission_gr_0["01-answer-tests-pass"]:::guardrail
    task_wave_04_rich_blocks_14_implement_answer_submission_gr_1["02-annotation-contract-preserved"]:::guardrail
  end
  style task_wave_04_rich_blocks_14_implement_answer_submission fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_04_rich_blocks_15_extend_sdk_question_submit["wave-04-rich-blocks/15-extend-sdk-question-submit"]
    task_wave_04_rich_blocks_15_extend_sdk_question_submit_gr_0["01-sdk-answer-submit-surface"]:::guardrail
  end
  style task_wave_04_rich_blocks_15_extend_sdk_question_submit fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-rich-blocks-complete"]:::guardrail
    plan_guardrails_1["02-union-clean"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_04_rich_blocks_01_add_block_kinds
  plan_preflights --> task_wave_04_rich_blocks_02_vendor_mermaid_runtime
  plan_preflights --> task_wave_04_rich_blocks_09_author_tests_question_schema
  plan_preflights --> task_wave_04_rich_blocks_13_author_tests_answer_submission
  plan_preflights --> task_wave_04_rich_blocks_15_extend_sdk_question_submit
  task_wave_04_rich_blocks_01_add_block_kinds --> task_wave_04_rich_blocks_03_author_tests_diagram_block
  task_wave_04_rich_blocks_01_add_block_kinds --> task_wave_04_rich_blocks_05_author_tests_comparison_block
  task_wave_04_rich_blocks_01_add_block_kinds --> task_wave_04_rich_blocks_07_author_tests_diff_block
  task_wave_04_rich_blocks_01_add_block_kinds --> task_wave_04_rich_blocks_11_author_tests_question_form
  task_wave_04_rich_blocks_02_vendor_mermaid_runtime --> task_wave_04_rich_blocks_04_implement_diagram_block
  task_wave_04_rich_blocks_03_author_tests_diagram_block --> task_wave_04_rich_blocks_04_implement_diagram_block
  task_wave_04_rich_blocks_04_implement_diagram_block --> task_wave_04_rich_blocks_06_implement_comparison_block
  task_wave_04_rich_blocks_05_author_tests_comparison_block --> task_wave_04_rich_blocks_06_implement_comparison_block
  task_wave_04_rich_blocks_06_implement_comparison_block --> task_wave_04_rich_blocks_08_implement_diff_block
  task_wave_04_rich_blocks_07_author_tests_diff_block --> task_wave_04_rich_blocks_08_implement_diff_block
  task_wave_04_rich_blocks_08_implement_diff_block --> task_wave_04_rich_blocks_12_implement_question_form
  task_wave_04_rich_blocks_09_author_tests_question_schema --> task_wave_04_rich_blocks_10_implement_question_schema
  task_wave_04_rich_blocks_09_author_tests_question_schema --> task_wave_04_rich_blocks_11_author_tests_question_form
  task_wave_04_rich_blocks_10_implement_question_schema --> task_wave_04_rich_blocks_12_implement_question_form
  task_wave_04_rich_blocks_11_author_tests_question_form --> task_wave_04_rich_blocks_12_implement_question_form
  task_wave_04_rich_blocks_13_author_tests_answer_submission --> task_wave_04_rich_blocks_14_implement_answer_submission
  task_wave_04_rich_blocks_12_implement_question_form --> plan_guardrails
  task_wave_04_rich_blocks_14_implement_answer_submission --> plan_guardrails
  task_wave_04_rich_blocks_15_extend_sdk_question_submit --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
