<!-- guardrails:graph v1 source-sha256=8b42e53b50c2e7259672434e4e9fe0de566600c5e706b7e3dd2957f7ed99b63e -->

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
    wave_4_preflights_0["01-wave3-annotation-materialized"]:::preflight
  end
  style wave_4_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_4["Wave 4 — rich-blocks"]
    subgraph task_wave_04_rich_blocks_01_add_block_kinds["01-add-block-kinds"]
      task_wave_04_rich_blocks_01_add_block_kinds_gr_0["01-block-kinds-declared"]:::guardrail
      task_wave_04_rich_blocks_01_add_block_kinds_gr_1["02-core-builds"]:::guardrail
    end
    style task_wave_04_rich_blocks_01_add_block_kinds fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_02_vendor_mermaid_runtime["02-vendor-mermaid-runtime"]
      task_wave_04_rich_blocks_02_vendor_mermaid_runtime_gr_0["01-mermaid-vendored-offline"]:::guardrail
      task_wave_04_rich_blocks_02_vendor_mermaid_runtime_gr_1["02-core-builds"]:::guardrail
    end
    style task_wave_04_rich_blocks_02_vendor_mermaid_runtime fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_03_author_tests_diagram_block["03-author-tests-diagram-block"]
      task_wave_04_rich_blocks_03_author_tests_diagram_block_gr_0["01-tests-build"]:::guardrail
      task_wave_04_rich_blocks_03_author_tests_diagram_block_gr_1["02-tests-fail-on-current-code"]:::guardrail
      task_wave_04_rich_blocks_03_author_tests_diagram_block_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_04_rich_blocks_03_author_tests_diagram_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_04_implement_diagram_block["04-implement-diagram-block"]
      task_wave_04_rich_blocks_04_implement_diagram_block_gr_0["01-diagram-tests-pass"]:::guardrail
      task_wave_04_rich_blocks_04_implement_diagram_block_gr_1["02-renderer-inlines-not-cdn"]:::guardrail
    end
    style task_wave_04_rich_blocks_04_implement_diagram_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_05_author_tests_comparison_block["05-author-tests-comparison-block"]
      task_wave_04_rich_blocks_05_author_tests_comparison_block_gr_0["01-tests-build"]:::guardrail
      task_wave_04_rich_blocks_05_author_tests_comparison_block_gr_1["02-tests-fail-on-current-code"]:::guardrail
      task_wave_04_rich_blocks_05_author_tests_comparison_block_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_04_rich_blocks_05_author_tests_comparison_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_06_implement_comparison_block["06-implement-comparison-block"]
      task_wave_04_rich_blocks_06_implement_comparison_block_gr_0["01-comparison-tests-pass"]:::guardrail
    end
    style task_wave_04_rich_blocks_06_implement_comparison_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_07_author_tests_diff_block["07-author-tests-diff-block"]
      task_wave_04_rich_blocks_07_author_tests_diff_block_gr_0["01-tests-build"]:::guardrail
      task_wave_04_rich_blocks_07_author_tests_diff_block_gr_1["02-tests-fail-on-current-code"]:::guardrail
      task_wave_04_rich_blocks_07_author_tests_diff_block_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_04_rich_blocks_07_author_tests_diff_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_08_implement_diff_block["08-implement-diff-block"]
      task_wave_04_rich_blocks_08_implement_diff_block_gr_0["01-diff-tests-pass"]:::guardrail
    end
    style task_wave_04_rich_blocks_08_implement_diff_block fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_09_author_tests_question_schema["09-author-tests-question-schema"]
      task_wave_04_rich_blocks_09_author_tests_question_schema_gr_0["01-build-passes"]:::guardrail
      task_wave_04_rich_blocks_09_author_tests_question_schema_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_04_rich_blocks_09_author_tests_question_schema_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_04_rich_blocks_09_author_tests_question_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_10_implement_question_schema["10-implement-question-schema"]
      task_wave_04_rich_blocks_10_implement_question_schema_gr_0["01-question-schema-tests-pass"]:::guardrail
    end
    style task_wave_04_rich_blocks_10_implement_question_schema fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_11_author_tests_question_form["11-author-tests-question-form"]
      task_wave_04_rich_blocks_11_author_tests_question_form_gr_0["01-tests-build"]:::guardrail
      task_wave_04_rich_blocks_11_author_tests_question_form_gr_1["02-tests-fail-on-current-code"]:::guardrail
      task_wave_04_rich_blocks_11_author_tests_question_form_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_04_rich_blocks_11_author_tests_question_form fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_12_implement_question_form["12-implement-question-form"]
      task_wave_04_rich_blocks_12_implement_question_form_gr_0["01-question-form-tests-pass"]:::guardrail
    end
    style task_wave_04_rich_blocks_12_implement_question_form fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_13_author_tests_answer_submission["13-author-tests-answer-submission"]
      task_wave_04_rich_blocks_13_author_tests_answer_submission_gr_0["01-build-passes"]:::guardrail
      task_wave_04_rich_blocks_13_author_tests_answer_submission_gr_1["02-tests-fail-on-current-code"]:::guardrail
      task_wave_04_rich_blocks_13_author_tests_answer_submission_gr_2["03-covers-answer-round-trip"]:::guardrail
    end
    style task_wave_04_rich_blocks_13_author_tests_answer_submission fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_14_implement_answer_submission["14-implement-answer-submission"]
      task_wave_04_rich_blocks_14_implement_answer_submission_gr_0["01-answer-tests-pass"]:::guardrail
      task_wave_04_rich_blocks_14_implement_answer_submission_gr_1["02-annotation-contract-preserved"]:::guardrail
    end
    style task_wave_04_rich_blocks_14_implement_answer_submission fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_04_rich_blocks_15_extend_sdk_question_submit["15-extend-sdk-question-submit"]
      task_wave_04_rich_blocks_15_extend_sdk_question_submit_gr_0["01-sdk-answer-submit-surface"]:::guardrail
    end
    style task_wave_04_rich_blocks_15_extend_sdk_question_submit fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_4 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_4_guardrails["Wave 4 Exit Gate"]
    wave_4_guardrails_0["01-rich-blocks-complete"]:::guardrail
    wave_4_guardrails_1["02-union-clean"]:::guardrail
  end
  style wave_4_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_5_preflights["Wave 5 Entry Gate"]
    wave_5_preflights_0["01-wave4-materialized"]:::preflight
  end
  style wave_5_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_5["Wave 5 — export-handoff"]
    subgraph task_wave_05_export_handoff_01_author_tests_artifact_exporter["01-author-tests-artifact-exporter"]
      task_wave_05_export_handoff_01_author_tests_artifact_exporter_gr_0["01-tests-build"]:::guardrail
      task_wave_05_export_handoff_01_author_tests_artifact_exporter_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_05_export_handoff_01_author_tests_artifact_exporter_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_05_export_handoff_01_author_tests_artifact_exporter fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_05_export_handoff_02_implement_artifact_exporter["02-implement-artifact-exporter"]
      task_wave_05_export_handoff_02_implement_artifact_exporter_gr_0["01-exporter-tests-pass"]:::guardrail
      task_wave_05_export_handoff_02_implement_artifact_exporter_gr_1["02-core-has-no-server-dependency"]:::guardrail
    end
    style task_wave_05_export_handoff_02_implement_artifact_exporter fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_05_export_handoff_03_wire_export_cli["03-wire-export-cli"]
      task_wave_05_export_handoff_03_wire_export_cli_gr_0["01-export-command-wired"]:::guardrail
      task_wave_05_export_handoff_03_wire_export_cli_gr_1["02-export-smoke"]:::guardrail
    end
    style task_wave_05_export_handoff_03_wire_export_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_05_export_handoff_04_author_tests_handoff_markdown["04-author-tests-handoff-markdown"]
      task_wave_05_export_handoff_04_author_tests_handoff_markdown_gr_0["01-tests-build"]:::guardrail
      task_wave_05_export_handoff_04_author_tests_handoff_markdown_gr_1["02-tests-fail-on-stubs"]:::guardrail
      task_wave_05_export_handoff_04_author_tests_handoff_markdown_gr_2["03-covers-key-behaviors"]:::guardrail
    end
    style task_wave_05_export_handoff_04_author_tests_handoff_markdown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_05_export_handoff_05_implement_handoff_markdown["05-implement-handoff-markdown"]
      task_wave_05_export_handoff_05_implement_handoff_markdown_gr_0["01-handoff-tests-pass"]:::guardrail
      task_wave_05_export_handoff_05_implement_handoff_markdown_gr_1["02-real-dispatch-not-hardcoded"]:::guardrail
    end
    style task_wave_05_export_handoff_05_implement_handoff_markdown fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
    subgraph task_wave_05_export_handoff_06_wire_handoff_cli["06-wire-handoff-cli"]
      task_wave_05_export_handoff_06_wire_handoff_cli_gr_0["01-handoff-command-wired"]:::guardrail
      task_wave_05_export_handoff_06_wire_handoff_cli_gr_1["02-handoff-smoke"]:::guardrail
    end
    style task_wave_05_export_handoff_06_wire_handoff_cli fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  end
  style wave_5 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_5_guardrails["Wave 5 Exit Gate"]
    wave_5_guardrails_0["01-wave5-solution-builds-and-tests"]:::guardrail
    wave_5_guardrails_1["02-union-clean"]:::guardrail
  end
  style wave_5_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_6_preflights["Wave 6 Entry Gate"]
  end
  style wave_6_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph wave_6["Wave 6 — agent-skill-polish"]
    wave_6_stub["⏸ JIT stub — run halts here for breakdown"]
    style wave_6_stub fill:#fef9c3,stroke:#ca8a04,color:#713f12;
  end
  style wave_6 fill:#f0f4f8,stroke:#64748b,color:#0f172a;
  subgraph wave_6_guardrails["Wave 6 Exit Gate"]
  end
  style wave_6_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
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
  wave_4_preflights --> task_wave_04_rich_blocks_01_add_block_kinds
  wave_4_preflights --> task_wave_04_rich_blocks_02_vendor_mermaid_runtime
  wave_4_preflights --> task_wave_04_rich_blocks_09_author_tests_question_schema
  wave_4_preflights --> task_wave_04_rich_blocks_13_author_tests_answer_submission
  wave_4_preflights --> task_wave_04_rich_blocks_15_extend_sdk_question_submit
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
  task_wave_04_rich_blocks_12_implement_question_form --> wave_4_guardrails
  task_wave_04_rich_blocks_14_implement_answer_submission --> wave_4_guardrails
  task_wave_04_rich_blocks_15_extend_sdk_question_submit --> wave_4_guardrails
  wave_5_preflights --> task_wave_05_export_handoff_01_author_tests_artifact_exporter
  wave_5_preflights --> task_wave_05_export_handoff_04_author_tests_handoff_markdown
  task_wave_05_export_handoff_01_author_tests_artifact_exporter --> task_wave_05_export_handoff_02_implement_artifact_exporter
  task_wave_05_export_handoff_02_implement_artifact_exporter --> task_wave_05_export_handoff_03_wire_export_cli
  task_wave_05_export_handoff_03_wire_export_cli --> task_wave_05_export_handoff_06_wire_handoff_cli
  task_wave_05_export_handoff_04_author_tests_handoff_markdown --> task_wave_05_export_handoff_05_implement_handoff_markdown
  task_wave_05_export_handoff_05_implement_handoff_markdown --> task_wave_05_export_handoff_06_wire_handoff_cli
  task_wave_05_export_handoff_06_wire_handoff_cli --> wave_5_guardrails
  wave_6_preflights --> wave_6_stub
  wave_6_stub --> wave_6_guardrails
  wave_1_guardrails -.->|"🔒 wave barrier"| wave_2_preflights
  wave_2_guardrails -.->|"🔒 wave barrier"| wave_3_preflights
  wave_3_guardrails -.->|"🔒 wave barrier"| wave_4_preflights
  wave_4_guardrails -.->|"🔒 wave barrier"| wave_5_preflights
  wave_5_guardrails -.->|"🔒 wave barrier"| wave_6_preflights
  wave_6_guardrails --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
