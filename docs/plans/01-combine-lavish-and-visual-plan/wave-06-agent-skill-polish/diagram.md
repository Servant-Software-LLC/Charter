<!-- guardrails:graph v1 source-sha256=822739f58d157f9b9c49e39e5e97e74e0730f63ec35d75eaceb49f7c898ca223 -->

```mermaid
flowchart TD
  subgraph plan_preflights["Full Flight Checks"]
    plan_preflights_0["01-wave5-materialized"]:::preflight
  end
  style plan_preflights fill:#d4edda,stroke:#2e7d32,color:#10341a;
  subgraph task_wave_06_agent_skill_polish_01_author_charter_skill["wave-06-agent-skill-polish/01-author-charter-skill"]
    task_wave_06_agent_skill_polish_01_author_charter_skill_gr_0["01-skill-files-exist"]:::guardrail
    task_wave_06_agent_skill_polish_01_author_charter_skill_gr_1["02-skill-structure"]:::guardrail
    task_wave_06_agent_skill_polish_01_author_charter_skill_gr_2["03-skill-cites-real-verbs"]:::guardrail
    task_wave_06_agent_skill_polish_01_author_charter_skill_gr_3["04-skill-covers-feedback-drain"]:::guardrail
  end
  style task_wave_06_agent_skill_polish_01_author_charter_skill fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_06_agent_skill_polish_02_refresh_readme_truthfulness["wave-06-agent-skill-polish/02-refresh-readme-truthfulness"]
    task_wave_06_agent_skill_polish_02_refresh_readme_truthfulness_gr_0["01-readme-truthful"]:::guardrail
  end
  style task_wave_06_agent_skill_polish_02_refresh_readme_truthfulness fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph task_wave_06_agent_skill_polish_03_refresh_cli_status_banner["wave-06-agent-skill-polish/03-refresh-cli-status-banner"]
    task_wave_06_agent_skill_polish_03_refresh_cli_status_banner_gr_0["01-verb-dispatch-survives"]:::guardrail
    task_wave_06_agent_skill_polish_03_refresh_cli_status_banner_gr_1["02-banner-truthful-smoke"]:::guardrail
  end
  style task_wave_06_agent_skill_polish_03_refresh_cli_status_banner fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;
  subgraph plan_guardrails["Terminal Gate"]
    plan_guardrails_0["01-wave6-solution-builds-and-tests"]:::guardrail
    plan_guardrails_1["02-union-clean"]:::guardrail
  end
  style plan_guardrails fill:#d4edda,stroke:#2e7d32,color:#10341a;
  plan_preflights --> task_wave_06_agent_skill_polish_01_author_charter_skill
  plan_preflights --> task_wave_06_agent_skill_polish_02_refresh_readme_truthfulness
  plan_preflights --> task_wave_06_agent_skill_polish_03_refresh_cli_status_banner
  task_wave_06_agent_skill_polish_01_author_charter_skill --> plan_guardrails
  task_wave_06_agent_skill_polish_02_refresh_readme_truthfulness --> plan_guardrails
  task_wave_06_agent_skill_polish_03_refresh_cli_status_banner --> plan_guardrails
  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;
  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;
```

_Structure only — retry, feedback, and needs-human edges are omitted._

**Legend**

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two checks once for the whole plan, at the very start and very end.
- ➡️ **Edge direction** — every edge runs in execution order, from a dependency to its dependent: an edge `A → B` means B runs after A (B dependsOn A). A long edge that routes *past* an unrelated box is NOT a dependency on that box — follow the arrowhead to its real target. (In `diagram.html`, a mid-edge arrow marks each edge's direction where a crossing edge passes between boxes.)
