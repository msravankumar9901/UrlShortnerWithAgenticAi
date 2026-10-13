# Ambiguous

"Make links secure and fast." The requirements agent lists ambiguities and does not treat a latency budget or a security control as fact. After architecture v1, the approved interpretation (SSRF checks, HTTP 429, p95 redirect under 50 ms, no destination fetch) replaces the requirements artifact and only downstream stages re-run. `baseline` stays at one execution.

See `docs/SCENARIOS.md`.
