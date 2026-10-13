# Risks and trade-offs

## Product

| Risk | What we did | Residual |
| --- | --- | --- |
| Open redirect / SSRF | http/https only; block loopback, private, link-local, and decimal IP literals; redirect never fetches the target | Hostnames that point at private IPs are not resolved. Doing that lookup would itself be SSRF. |
| Code collision | 7-character base62 with rejection sampling, 8 attempts inside the write lock | Birthday collision is negligible at prototype volume. The bound fails closed with 500 if it is ever hit. |
| Lost updates | One `SemaphoreSlim` around read/modify/write; temp file then `File.Replace` | One process, one machine. Two API processes would corrupt the files. |
| Alias reuse after delete | Deleted codes stay reserved | Storage grows with deleted rows. Acceptable here; a database would archive them. |
| Rate limit bypass | Fixed window per remote IP on create and redirect | No proxy-header trust. A shared NAT shares a budget. The window is in-memory and resets on restart. |
| PII in clicks | User agent and referrer are stored, truncated. Raw IP is not stored or audited. | User agent can still be identifying. Retention is "until the file is deleted." |
| Idempotency | Same trimmed URL, not deleted and not expired, returns the existing code | A different alias on the second call does not create a second code. |

## Orchestrator

| Risk | What we did | Residual |
| --- | --- | --- |
| LLM takes over the workflow | Status changes only in `WorkflowEngine`. Release `ready` is computed from stage status. | A live model can still write a poor artifact. The human gate and the test exit code are the backstop. |
| Runaway retries | `MaxRetries` per stage. Security and governance do not retry. Unknown failures safe-stop. | A stage that returns `Fail` is classified as implementation and will use its whole budget. |
| Unsafe generated code | Allow-listed paths, then a scan for process execution and hardcoded secrets | The scanner is textual. It will miss obfuscation. It is a gate, not a substitute for review. |
| Audit tampering | Hash chain and `audit verify` | The file is not signed by an external system. Someone with disk access can rewrite the whole chain. The verify command catches a partial edit. |
| Replan too wide or too narrow | Only transitive dependents of the changed node are marked stale | A node that read the requirement only through prose, with no graph edge, will not re-run. The scenario graph has to be honest. |
| Approval fatigue | `--auto-approve` for the unattended demo; interactive and dashboard for the live one | Auto-approve must not be the production default. The dashboard has no authentication. |
| Rollback scope | Workspace snapshot for the stage | It does not roll back the shipping `src/` tree, because agents do not write there. It also does not roll back a successful later stage if you inject failure only on implementation (later stages have not run). |

## Trade-offs we would defend

- **JSON instead of PostgreSQL.** Chosen because the brief for this build said file storage. We kept the repository interface so the choice is reversible. We gave up multi-writer durability and ad-hoc analytics queries.
- **No Redis.** A cache would add a failure mode the JSON file does not have. Redirect already does not leave the process. The p95 target in the ambiguous scenario is an in-process read, not a cache tier.
- **Custom engine instead of a workflow product.** More code to maintain, much easier to show gates, hashes, and the join on one screen.
- **Workspace copies instead of editing the API during the scenario.** The agent output is real files, and the regression suite keeps testing the API you can curl. The cost is that the brownfield "change" is evidenced by the impact report and the workspace sketch, while the feature itself already lives in `src/` so the product tests can pass.
- **Rule-based fallback.** Offline demos work. The text is template-quality, not a substitute for a review of a live model. When a key is set, the same prompts and the same gates apply.
