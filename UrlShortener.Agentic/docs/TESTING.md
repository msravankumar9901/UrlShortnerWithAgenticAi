# Testing

## Strategy

Product behavior is tested through the HTTP pipeline (`WebApplicationFactory`) so routing, validation, status codes, the rate limiter, and the JSON repositories are all in the path. Each host gets its own temp data directory. Expiry uses a mutable `IClock` so the test does not sleep. Concurrency uses 100 parallel creates against one singleton repository lock.

Orchestrator behavior is tested against `WorkflowEngine` with scripted stage executors and temp `runs/` folders. Those tests do not call the network and do not start the URL shortener. The security gate and the policy engine are exercised by real writes, not by stubs of the gate itself.

The scenario test stage shells out to `dotnet test` on `tests/UrlShortener.Tests` only. It does not rebuild the running CLI, and it does not recurse into scenario runs.

## How to run

```powershell
dotnet test
dotnet test tests/UrlShortener.Tests --filter TC09
dotnet test tests/Agentic.Orchestrator.Tests --filter OC08
```

No Docker, database, or API key. Coverage of the happy path is the xUnit suite plus the three scenario runs. There is no coverage-percentage gate.

## Product cases

| Id | Expectation |
| --- | --- |
| TC-01 | `POST /api/shorten` with https returns 201 and a code |
| TC-02 | `javascript:` returns 400 |
| TC-03 | `http://127.0.0.1/...` returns 400 |
| TC-04 | Existing code redirects 302 with `Location` |
| TC-05 | Unknown code returns 404 |
| TC-06 | Expired link returns 410 |
| TC-07 | Second URL with the same alias returns 409 |
| TC-08 | Three redirects then stats `totalClicks == 3` |
| TC-09 | 100 concurrent creates, 100 distinct codes, valid `urls.json`, no temp files left |
| TC-10 | Failure before commit leaves the previous file bytes unchanged |
| TC-11 | Fixed window of 2 then a later create returns 429 |
| TC-12 | The same URL twice returns the same code (201 then 200) |

Extra checks: soft delete then redirect is 404; validator blocks `10/8`, `192.168/16`, `localhost`, and `file:`; public https is allowed.

## Orchestrator cases

| Id | Expectation |
| --- | --- |
| OC-01 | A cycle throws `CyclicGraphException` |
| OC-02 | Unmet `context:` entry gate → `Blocked`, executor not called |
| OC-03 | Fail twice then succeed → `RetryCount == 2` and MTTR is not null |
| OC-04 | Retry exhaustion restores the snapshotted file |
| OC-05 | Human reject at design → `Rejected`, downstream executor not called |
| OC-06 | Write outside the allow-list → `Blocked` by policy, file absent |
| OC-07 | Generated `Process.Start` fails the security gate once (no retry) |
| OC-08 | Changing stage A re-runs only B, C, and D; E stays at one execution |
| OC-09 | `STOP` file → `SafeStopped` with `state.json`; resume completes |
| OC-10 | Editing an audit line fails `AuditLog.Verify` |
| OC-11 | Throwing LLM client; fallback completes the workflow |
| OC-12 | Two stages overlap in time; the join starts at or after both ends |

## Latest local run

`dotnet test` on SDK 10.0.401: Agentic.Orchestrator.Tests 12 passed; UrlShortener.Tests 18 passed (the 12 numbered product cases, soft delete, four validator inputs, and the public-https validator case). Failed: 0.

## Gaps

- No property-based fuzz of URL parsers beyond the cases above.
- No load test beyond 100 concurrent creates.
- The live OpenAI path is not part of CI; OC-11 covers the fallback. A provider outage during a demo should be described as fallback, never as a successful live model call.
- Scenario runs are not xUnit tests. They are exercised by the CLI commands in the README.
