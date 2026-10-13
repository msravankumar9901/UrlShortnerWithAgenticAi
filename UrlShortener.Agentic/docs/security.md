# Security

## URL shortener

- Schemes other than http and https are rejected before `Uri` parsing, so `javascript:` and `data:` cannot sneak through.
- Userinfo (`https://user:pass@host`) is rejected.
- Loopback, unspecified, RFC1918, link-local, CGNAT (`100.64/10`), and IPv6 unique-local addresses are rejected, including IPv4-mapped IPv6.
- `localhost`, `*.localhost`, and a few well-known metadata names are rejected. Pure-digit hosts (decimal IPs) and `0x` hosts are rejected.
- Redirect does not perform an outbound request. The 302 is the client's problem.
- Clicks store a truncated user agent and referrer. They do not store the client IP. Audit messages pass through `PolicyEngine.Redact` for emails and long digit strings.
- Create and redirect use the ASP.NET Core fixed-window limiter, keyed by remote IP, and return 429.
- Unhandled exceptions return a generic 500. Development adds a one-line detail for debugging; the response has no stack trace.

## Agent controls

- Allow-listed relative paths only. `..` and rooted paths are denied.
- An empty allow-list means the stage cannot write. Security and review are configured that way.
- Workspace `*.cs` (and script extensions) are scanned for process execution and hardcoded secret assignments. A hit fails the stage and does not retry.
- The test stage may start `dotnet test` as a fixed tool. That process call lives in the orchestrator, which the product security scan does not treat as generated code.
- Provider keys are read from the environment inside `LlmSettings`. They are not written to audit details.

## What this is not

The dashboard approval endpoint is intentionally open on localhost. Do not expose it beyond the machine. There is no authentication on the shortener either; anyone who can call the API can create links. That matches the prototype scope and would be the first production gap to close.
