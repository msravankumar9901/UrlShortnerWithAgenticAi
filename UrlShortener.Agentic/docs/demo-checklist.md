# Demo checklist

## Before

- [ ] `dotnet --version` shows a 10.0 SDK (this tree pins `10.0.401` with feature roll-forward).
- [ ] `dotnet test` is green.
- [ ] No `OPENAI_API_KEY` unless you intend to show a live model.
- [ ] Port 5080 is free.
- [ ] You know the last good `runs/<runId>` in case you need a backup artifact.

## Commands

See `docs/demo.md`. Keep a second terminal for the API.

## Checkpoints to say out loud

- [ ] Agents do not pick the next stage.
- [ ] Parallel stages overlap; release waits.
- [ ] `--auto-approve` still writes approval audit events.
- [ ] Injected failure retries and restores the snapshot.
- [ ] Brownfield impact JSON cites this repository.
- [ ] Ambiguous run produces architecture v1 and v2; baseline does not re-run.
- [ ] `audit verify` prints `AUDIT OK`.
- [ ] Metrics define MTTR in the file itself.

## If something breaks

| Problem | Recovery |
| --- | --- |
| API port in use | Stop the other `dotnet` process. The profile is `http://localhost:5080`. |
| Scenario test stage fails | Run `dotnet test tests/UrlShortener.Tests` and fix the product. Do not delete tests. |
| Live LLM error | Fallback should complete the stage. If you disabled fallback, unset the key and rerun. |
| Need a safe-stop | `dotnet run --project src/Agentic.Cli -- stop`, then `resume <runId> --auto-approve`. |
| Time is short | Skip Swagger. Do greenfield, the inject run, and the ambiguous v1/v2 files. |

## Do not claim

- A multi-node or multi-region deployment.
- That the dashboard is an authenticated control.
- That a failed live model call succeeded.
- PostgreSQL, Redis, or OpenTelemetry. Those were in the generic prompt pack and were deliberately not built. See `docs/DEVELOPMENT_STRATEGY.md`.
