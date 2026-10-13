# Scenarios

Each scenario is a JSON DAG in `scenarios/`. The CLI loads it and `WorkflowEngine` executes it. `--auto-approve` grants human gates and still writes `ApprovalRequested` / `ApprovalGranted`.

## Greenfield

**Requirement.** Build a shortener with creation, redirect, and analytics, plus validation, collision-safe codes, and health.

**Decomposition.**

| Stage | Agent | Depends on | Autonomy |
| --- | --- | --- | --- |
| baseline | BaselineAgent | — | Auto |
| requirements | RequirementsAgent | — | AutoWithReview, before |
| architecture | ArchitectAgent | requirements | HumanRequired, before |
| implementation | DeveloperAgent | architecture | AutoWithReview, before, rollback |
| testing, security, review, docs | those agents | implementation | Auto, parallel |
| release | ReleaseAgent | the four above | HumanRequired, after |

**Orchestration.** `baseline` and `requirements` can run together. Architecture waits for requirements. The four validation stages start together and release waits for all of them. Testing runs `dotnet test` on `tests/UrlShortener.Tests`.

**Validation.** Workflow status `Completed`. Every stage `Succeeded` with execution count 1. Audit verifies. Metrics success rate is 1.

```powershell
dotnet run --project src/Agentic.Cli -- run scenarios/greenfield.json --auto-approve
```

## Brownfield

**Requirement.** Add custom aliases and link expiry, and fix duplicate-code allocation, without breaking redirect or analytics.

**Decomposition.** Same as greenfield, plus `analysis` (`CodebaseAnalystAgent`) between requirements and architecture.

**Orchestration.** The analyst enumerates `src/UrlShortener.*.cs`, records `MapGet` / `MapPost` / `MapDelete` routes, and cites `UrlShortenerService`, `JsonUrlRepository`, `AtomicFileStore`, and `UrlValidator` with file and line. Risk score rises because the change touches allocation, the create contract, and the document on disk. Architecture is not allowed to run until that artifact exists.

**Validation.** `workspace/analysis/impact.json` contains real project paths. The test stage is the regression gate (`dotnet test`). Implementation writes `workspace/src/CollisionSafeCodes.cs` and does not modify `src/`.

**Injected failure.** `--inject-failure implementation` lets the agent write, then throws `InjectedFailureException`. The stage retries until `maxRetries` (2) is exhausted (3 attempts, 2 retries), restores the workspace snapshot, and marks descendants `RolledBack`. The CLI exits 0 for this demonstration and prints that rollback completed. Confirm `RetryScheduled` and `RollbackCompleted` in the audit log, and that `workspace/src/injected-failure.txt` is gone.

```powershell
dotnet run --project src/Agentic.Cli -- run scenarios/brownfield.json --auto-approve
dotnet run --project src/Agentic.Cli -- run scenarios/brownfield.json --auto-approve --inject-failure implementation
```

## Ambiguous

**Requirement.** "Make links secure and fast."

**Decomposition.** Same shape as greenfield. Requirements use `HumanRequired` with approval *after* the agent, so the ambiguity list exists before the gate. Architecture is `HumanRequired` before it runs.

**What the agent flags.** Security is undefined (SSRF, auth, encryption, rate limit). Fast has no percentile or budget. Privacy (unique users, retention, raw IPs) is not specified. `requiresHumanApproval` is true. A candidate interpretation is proposed and labeled as not approved.

**Orchestration and replan.** The run proceeds with `--auto-approve`, so architecture v1 is an assumption draft (`"mode": "assumption"`). The scenario then applies this approved text as a new requirements artifact:

> APPROVED INTERPRETATION … SSRF blocking, per-IP 429, no raw IP logs, no accounts, p95 redirect under 50ms, no fetch of the destination.

`ReplanStarted` / `NodeInvalidated` / `ReplanCompleted` follow. Architecture, implementation, and everything downstream are `Stale` and run again. Architecture v2 has `"mode": "approved"` and the 50ms / SSRF decisions. `baseline` has no edge from requirements, so its execution count stays 1. High-impact approval gates run again for the new architecture and for release.

**Validation.** `artifacts/architecture.v1.txt` and `artifacts/architecture.v2.txt` differ. `state.json` `invalidatedStages` does not include `baseline`. `replanCount` is 1.

```powershell
dotnet run --project src/Agentic.Cli -- run scenarios/ambiguous.json --auto-approve
```
