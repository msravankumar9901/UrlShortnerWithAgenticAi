# Orchestration

`WorkflowEngine` in `src/Agentic.Orchestrator/WorkflowEngine.cs` is the scheduler.

## Graph

`GraphValidator.TopologicalSort` uses a stable Kahn sort. A node that depends on itself, or a pair that depends on each other, throws `CyclicGraphException` before a run directory is treated as successful. OC-01 covers this.

Ready nodes are those whose dependencies are all `Succeeded` and whose entry gate passes. The entry gate `always` passes. `context:Key=Value` passes only when the run context has that pair. Anything else fails closed and the node is `Blocked`.

Independent ready nodes run with `Task.WhenAll`. OC-12 checks that two such nodes overlap and that a joiner does not start until both have finished.

## Gates

- **Entry:** dependencies and `entryGate`.
- **Approval:** before execution, or after the artifact exists when `approvalTiming` is `after`.
- **Exit:** the agent returned success, policy allowed every write, and the security scan found no HIGH or CRITICAL issue in workspace source files.

## Recovery

| Situation | Behavior |
| --- | --- |
| Implementation or injected failure, retries left | `RetryScheduled`, exponential backoff from `RetryBaseDelay`, failure text fed back |
| Retries exhausted and `rollback: true` | Snapshot restored, stage `RolledBack`, descendants not run |
| Policy denial or security gate | No retry |
| Unclassified exception | `SafeStop`, stage `Failed` |
| `runs/STOP` or `runs/<id>/STOP` | `SafeStopped` between stages, `resume` continues |
| Step count or duration exceeded | Same safe-stop |

## Lineage and replan

Each accepted output stores `outputHash`. A decision stores `inputsHash` and `parentDecisionId`. `ReplanFromOutputChangeAsync` and the scenario `replan` block replace one artifact, audit `ArtifactSuperseded`, and set only downstream nodes to `Stale`. OC-08 is the unit proof. The ambiguous scenario is the end-to-end proof.

## Metrics

Written at the end of a run, including safe-stop and rollback. MTTR ignores stages that never failed. End-to-end time includes approvals and retries. Per-stage latency is the clock from the first attempt's start to the terminal timestamp.
