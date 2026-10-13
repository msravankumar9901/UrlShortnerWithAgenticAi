# Demo script

Ten to fifteen minutes. Run from the repository root. Details and the same commands are in `README.md`.

## 0. Setup

```powershell
dotnet build
dotnet test
```

Leave the API stopped until the product section. No API key is required. Say so out loud: the fallback is intentional.

## 1. Objective

We are not chatting with one agent that edits the repo. A deterministic engine runs a DAG. Agents fill in artifacts. Humans approve high-impact nodes. The subject is a URL shortener because the behavior is easy to see and the safety issues (open redirect, SSRF, lost updates) are real.

## 2. Architecture

Show the component diagram in `docs/ARCHITECTURE.md`. Name ADR-1 (custom engine), ADR-2 (JSON behind an interface), ADR-3 (fallback).

## 3. Product

```powershell
dotnet run --project src/UrlShortener.Api
```

`GET http://localhost:5080/health`, `POST /api/shorten`, `GET /{code}`, `GET /api/stats/{code}`. Optional: a `javascript:` body returns 400. Open `/dashboard` and `/swagger`. Stop the process before the long scenarios if you want the machine quiet; the scenarios do not need the API.

## 4. Greenfield

```powershell
dotnet run --project src/Agentic.Cli -- run scenarios/greenfield.json --auto-approve
```

Point at the approval lines, the four stages that start together, and release after the join. Open `runs/<runId>/audit.jsonl`.

## 5. Retry and rollback

```powershell
dotnet run --project src/Agentic.Cli -- run scenarios/brownfield.json --auto-approve --inject-failure implementation
dotnet run --project src/Agentic.Cli -- audit verify <runId>
```

Expect two retries, `RollbackCompleted`, workflow `RolledBack`, and a valid audit chain. The injected file is gone.

## 6. Brownfield

```powershell
dotnet run --project src/Agentic.Cli -- run scenarios/brownfield.json --auto-approve
```

Open `workspace/analysis/impact.json` in that run. The paths are from this repo, not from a prompt.

## 7. Ambiguous requirement

```powershell
dotnet run --project src/Agentic.Cli -- run scenarios/ambiguous.json --auto-approve
```

Show the ambiguity list in `artifacts/requirements.txt`, then `architecture.v1.txt` (`mode: assumption`) and `architecture.v2.txt` (`mode: approved`, p95 under 50ms, SSRF, 429). `baseline` runs once.

## 8. Metrics and close

```powershell
dotnet run --project src/Agentic.Cli -- metrics <runId>
```

Read success rate, retries, rollbacks, MTTR, and the MTTR definition printed under the table. Close with the limitations in `docs/ENGINEERING_SUMMARY.md`: single-node JSON, no dashboard auth, no DNS-based SSRF check, textual security scan.

## If the live model fails

Do not claim the live call worked. The engine already records `LlmFallback`. You can also show OC-11 and a previous run's `audit.jsonl`. Unset `OPENAI_API_KEY` to force the offline path.
