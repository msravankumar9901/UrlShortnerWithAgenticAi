# Engineering summary

## Plan

Build the shortener first so there is a real system to test and to scan. Then put a deterministic engine around it: graph, gates, audit, recovery. Then agents that return artifacts, with a real HTTP LLM client and an offline fallback. Then the three scenarios that exercise greenfield flow, brownfield impact analysis, and an ambiguous requirement that replans.

The LLM is a tool the agents call. It is not the controller. That split is the design.

## What was produced

- A running API: create, redirect with click capture, stats, soft delete, health, rate limit, Swagger, dashboard.
- JSON repositories with atomic replace and a concurrency test that expects zero lost rows.
- `WorkflowEngine` plus policy, hash-chained audit, metrics, snapshots, and replanning.
- Agents for requirements, baseline, codebase analysis, architecture, implementation, test, security, review, docs, and release. Prompts are in `prompts/`.
- CLI commands `run`, `resume`, `audit verify`, `metrics`, `stop`.
- xUnit suites TC-01–TC-12 and OC-01–OC-12.
- This `docs/` set and the demo script in `README.md`.

## Requirement map

| Assignment need | Where it lives |
| --- | --- |
| Requirement understanding and ambiguity | `RequirementsAgent`, `prompts/requirement.md`, `scenarios/ambiguous.json` |
| Task decomposition and dependencies | Scenario DAGs and `GraphValidator` |
| Brownfield reasoning | `CodebaseAnalystAgent` reading `src/UrlShortener.*` |
| Explicit DAG, sequential and parallel, join | `WorkflowEngine.CollectReadyAsync` and `Task.WhenAll` |
| Cross-stage context and decision lineage | `RunState.Context`, `DecisionRecord` |
| Human approval | `IApprovalService`, `--auto-approve`, dashboard `POST /api/approvals/{runId}` |
| Bounded retry, fallback, rollback, safe-stop | Retry loop, `FallbackLlmClient`, `DirectorySnapshot`, `STOP` file |
| Policy: security, compliance, change control | `SecurityScanner`, `PolicyEngine.Redact`, `Workspace` allow-list |
| Audit-grade trace | `runs/<id>/audit.jsonl`, `AuditLog.Verify` |
| Success, retry, rollback, MTTR, latency | `RunMetricsCalculator`, `runs/<id>/metrics.json` |
| Dynamic replan of affected nodes only | `ApplyOutputChangeAsync`, OC-08, ambiguous scenario |
| Controlled autonomy | Agents execute; `HumanRequired` stages do not succeed without a decision |
| Tests, docs, risks, summary | `tests/`, `docs/` |

## Validation

`dotnet test`: 12 orchestrator tests and 18 product tests, 0 failed, on SDK 10.0.401. Scenario commands with `--auto-approve` complete; brownfield with `--inject-failure implementation` records retries and rollback; `audit verify` accepts an untampered log.

## Assumptions

- One API process owns the JSON files.
- Expiry is whole days, UTC, and `expiresInDays` of 1 or more.
- Custom aliases are case-sensitive and are not reused after delete.
- "Same URL" means the same trimmed string.
- Demo approvals may be automatic; production would not ship `--auto-approve`.
- The shipping feature set already includes aliases and expiry so the product tests have something to assert. The brownfield scenario shows how that change is governed, and writes its patch into the run workspace.

## Limitations

- No multi-instance store, no authenticated dashboard, no external audit signature.
- Hostname DNS rebinding is out of scope on purpose.
- The security scan is a string scan of source files in the workspace and the URL shortener projects.
- Safe-stop is checked between stages. A long `dotnet test` finishes before the `STOP` file is noticed.
- Live model output is not schema-validated beyond "non-empty." The deterministic client returns JSON. A live model that returns prose is stored as the artifact and still has to pass later gates (tests, security, human release).
- Git commit/rollback was not used. Snapshots cover the workspace. See ADR-6 in `docs/ARCHITECTURE.md`.

## Why this shape

A regulated reviewer needs to point at a gate and a hash, not at a chat transcript. The engine is boring on purpose. The interesting part is which decisions are automatic, which are human, and which nodes have to run again when the requirement changes.
