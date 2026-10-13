# URL Shortener + Governed Agentic SDLC

A URL shortener and a deterministic orchestration engine that turns a requirement into a reviewable engineering outcome. Agents do the work. The orchestrator owns the workflow. Humans approve high-impact steps.

Use it for marketing, SMS, and QR links, including healthcare communications where a link must be valid, short-lived, and safe to redirect. The same pipeline is the part to show in a regulated environment: every stage has a gate, an audit event, and a recorded decision.

## What is in the box

- ASP.NET Core minimal API for shorten, redirect, analytics, soft delete, health, Swagger, and a local run dashboard.
- JSON file repositories behind interfaces, with atomic replace and a process-wide lock.
- A DAG workflow engine: entry/exit gates, parallel branches, a join, approvals, bounded retries, fallback, rollback, safe-stop, policy checks, a hash-chained audit log, metrics, and re-planning of only the affected downstream stages.
- An OpenAI-compatible client and a deterministic fallback so the demo runs with no API key.
- Three scenarios: greenfield, brownfield, and ambiguous.

## Prerequisites

- .NET SDK 10 (this repo was built with SDK `10.0.401`; `global.json` rolls forward on the 10.0 feature band).
- Windows PowerShell or any shell that can run `dotnet`.
- No database, Docker, or API key.

## Setup

```powershell
cd "C:\Users\SM014163\OneDrive - Zelis Healthcare\Desktop\AgenticAi\UrlShortener.Agentic"
dotnet build
dotnet test
```

Run every command below from that folder.

## Run the API

```powershell
dotnet run --project src/UrlShortener.Api
```

Base URL: `http://localhost:5080`

| Action | Request |
| --- | --- |
| Health | `GET /health` |
| Create | `POST /api/shorten` with `{ "url": "https://example.com/a", "customAlias": "promo", "expiresInDays": 7 }` |
| Redirect | `GET /{code}` → 302, or 404 / 410 |
| Stats | `GET /api/stats/{code}` |
| Soft delete | `DELETE /api/urls/{code}` |
| Swagger | `http://localhost:5080/swagger` |
| Dashboard | `http://localhost:5080/dashboard` |

Data files: `data/urls.json`, `data/clicks.json`.

## Run the orchestrator

```powershell
dotnet run --project src/Agentic.Cli -- run scenarios/greenfield.json --auto-approve
dotnet run --project src/Agentic.Cli -- run scenarios/brownfield.json --auto-approve
dotnet run --project src/Agentic.Cli -- run scenarios/ambiguous.json --auto-approve
dotnet run --project src/Agentic.Cli -- run scenarios/brownfield.json --auto-approve --inject-failure implementation
dotnet run --project src/Agentic.Cli -- audit verify <runId>
dotnet run --project src/Agentic.Cli -- metrics <runId>
dotnet run --project src/Agentic.Cli -- stop
dotnet run --project src/Agentic.Cli -- resume <runId> --auto-approve
```

`--auto-approve` records the human gates and grants them. Omit it to pause for `approve` / `reject` on the console, or use the dashboard buttons. A `STOP` file under `runs/` ends the run between stages and leaves `state.json` so `resume` can continue.

## LLM configuration

The demo does not need a key. If `OPENAI_API_KEY` is unset, every agent call uses `RuleBasedLlmClient` and the audit event `LlmFallback` is recorded when the fallback wrapper handles an unconfigured or failed primary.

| Variable | Purpose |
| --- | --- |
| `OPENAI_API_KEY` | Bearer token. Never commit this. |
| `OPENAI_BASE_URL` | Default `https://api.openai.com/v1` |
| `OPENAI_MODEL` | Default `gpt-4o-mini` |
| `AZURE_OPENAI_ENDPOINT` | If set with a key, Azure is preferred |
| `AZURE_OPENAI_API_KEY` | Azure key |
| `AZURE_OPENAI_DEPLOYMENT` | Deployment name |
| `AZURE_OPENAI_API_VERSION` | Default `2024-10-21` |

Prompts live in `prompts/`. The model is not allowed to change workflow state. Invalid or empty provider output falls through to the deterministic client.

## Demo script (about 12 minutes)

1. **Product (2 min).** Start the API. `GET /health`. Shorten `https://example.com/demo`. Open the `Location` from `GET /{code}`. `GET /api/stats/{code}` after a couple of redirects. Show Swagger. Mention SSRF rejection (`javascript:`, `http://127.0.0.1`) and 429 on the limiter.

2. **Why the orchestrator exists (1 min).** Open `docs/ARCHITECTURE.md`. The LLM does not decide retries, approvals, or the next stage. `WorkflowEngine` does.

3. **Greenfield (3 min).** Run the greenfield command above. Point at:
   - approval lines even with `--auto-approve` (the gate still exists),
   - `testing`, `security`, `review`, and `docs` starting together,
   - `release` starting only after that join,
   - `runs/<runId>/audit.jsonl` and `metrics.json`.

4. **Failure, retry, rollback (2 min).** Run brownfield with `--inject-failure implementation`. The implementation stage is snapshotted, fails, retries, then `RollbackCompleted` restores the workspace. Downstream stages are not executed. `audit verify <runId>` still succeeds because the log was appended, not edited.

5. **Brownfield (2 min).** Run brownfield without the inject flag. Open `runs/<runId>/workspace/analysis/impact.json`. It lists real files and endpoints from `src/UrlShortener.*`, plus a risk score. The test stage runs `dotnet test`.

6. **Ambiguous + replan (2 min).** Run the ambiguous scenario. Requirements flags "secure" and "fast" and waits for approval. After architecture v1 (assumptions), the approved interpretation is applied. Architecture is invalidated and runs again (`artifacts/architecture.v1.txt` vs `v2.txt`). `baseline` does not re-run. Say the sentence: agents execute, humans approve.

7. **Close.** `audit verify` and `metrics`. Mention limitations: single-node JSON, no DNS resolution of hostnames, dashboard approval is local and unauthenticated.

## Tests

```powershell
dotnet test
```

`docs/TESTING.md` has the case table. Product tests use `WebApplicationFactory`. Orchestrator tests use temp directories and do not call a live model.

## Layout

```
UrlShortener.Agentic.sln
UrlShortener.Agentic.slnx
src/UrlShortener.Core
src/UrlShortener.Infrastructure
src/UrlShortener.Api
src/Agentic.Orchestrator
src/Agentic.Cli
tests/UrlShortener.Tests
tests/Agentic.Orchestrator.Tests
scenarios/   prompts/   docs/   runs/   data/
```

## Further reading

- `docs/ARCHITECTURE.md` — components, control flow, decisions
- `docs/SCENARIOS.md` — the three walks
- `docs/TESTING.md` — cases and how to run them
- `docs/RISKS_AND_TRADEOFFS.md`
- `docs/ENGINEERING_SUMMARY.md`
- `docs/DEVELOPMENT_STRATEGY.md`
- `docs/engineering-principles.md`
- `docs/demo.md` and `docs/demo-checklist.md`
