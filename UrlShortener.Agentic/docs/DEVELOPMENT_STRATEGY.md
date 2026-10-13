# Development strategy

The build order is the one in the master prompt, with the user's stack choices winning where they differ: .NET, and JSON files instead of PostgreSQL, Redis, and Testcontainers.

| Phase | Outcome |
| --- | --- |
| 1. URL shortener | Core models, validator, service, JSON repositories, minimal API, product tests TC-01–TC-12 |
| 2. Orchestration engine | DAG validation, topological execution, parallel `WhenAll`, join, persisted `state.json` |
| 3. Governance | Approvals, policy allow-list, security gate, bounded retry, snapshot rollback, `STOP` safe-stop |
| 4. LLM runtime | `ILlmClient`, OpenAI-compatible and Azure settings from the environment, `RuleBasedLlmClient`, `FallbackLlmClient` |
| 5. Agents | Requirements, baseline, codebase analyst, architect, developer, test, security, review, docs, release. Prompts under `prompts/` |
| 6. Replan and audit | Hash-chained JSONL, `audit verify`, metrics including MTTR, invalidate-downstream replan |
| 7. Scenarios | `scenarios/greenfield.json`, `brownfield.json`, `ambiguous.json`, failure injection |
| 8. Tests | OC-01–OC-12 plus the product suite |
| 9. Demo | CLI output, dashboard, this `docs/` set, README script |

## What we did not copy from the prompt pack

The pack's project list (`Domain`, `Application`, `AgenticSdlc.Contracts`, `Tools`, separate integration-test projects, Docker Compose, EF Core, Redis, OpenTelemetry, git tools) conflicts with a single-process JSON prototype. Those were collapsed:

- Domain + application → `UrlShortener.Core`
- Persistence → `UrlShortener.Infrastructure`
- Contracts, engine, agents, and tools → `Agentic.Orchestrator` (tools are `Workspace`, the test process, and the file approval drop)
- One xUnit project per side instead of four

The non-negotiable rules were kept: the model is not the controller, agents return structured results, security and review do not get write paths, high-impact actions have an approval record, and tests are not deleted to go green.

## Definition of done used here

- `dotnet build` has no errors.
- `dotnet test` passes, including the numbered cases.
- All three scenarios complete with `--auto-approve`.
- Brownfield injection shows retry and rollback, and the audit chain still verifies.
- The API answers health, create, redirect, and stats.
- Docs state the limitations instead of implying a platform we did not build.
