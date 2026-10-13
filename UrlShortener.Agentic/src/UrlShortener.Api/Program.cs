using System.Text.Json;
using System.Threading.RateLimiting;
using UrlShortener.Api;
using UrlShortener.Core;
using UrlShortener.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var repoRoot = RepoPaths.FindRepoRoot();
var dataDirectory = builder.Configuration["Data:Directory"];
if (string.IsNullOrWhiteSpace(dataDirectory))
    dataDirectory = Path.Combine(repoRoot, "data");

var runsDirectory = builder.Configuration["Runs:Directory"];
if (string.IsNullOrWhiteSpace(runsDirectory))
    runsDirectory = Path.Combine(repoRoot, "runs");

builder.Services.AddUrlShortener(dataDirectory);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(new RunCatalog(runsDirectory));

var permitLimit = builder.Configuration.GetValue("RateLimit:PermitLimit", 60);
var windowSeconds = builder.Configuration.GetValue("RateLimit:WindowSeconds", 60);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = windowSeconds.ToString();
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "Too many requests.",
            code = "rate-limited",
            status = StatusCodes.Status429TooManyRequests
        }, token);
    };
    options.AddPolicy("per-ip", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(Math.Max(1, windowSeconds)),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

app.UseExceptionHandler(handler =>
{
    handler.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        if (feature?.Error is Exception exception)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UrlShortener.Api");
            logger.LogError(exception, "Unhandled exception");
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "An unexpected error occurred.",
            code = "unexpected-error",
            status = StatusCodes.Status500InternalServerError
        });
    });
});

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
    await next();
});

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseRateLimiter();

var links = app.Services.GetRequiredService<UrlShortenerService>();
var runs = app.Services.GetRequiredService<RunCatalog>();

app.MapGet("/", () => Results.Redirect("/dashboard"));
app.MapGet("/favicon.ico", () => Results.NoContent());

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }))
    .WithName("Health")
    .WithTags("Ops");

app.MapGet("/health/ready", () =>
{
    try
    {
        Directory.CreateDirectory(dataDirectory);
        var probe = Path.Combine(dataDirectory, ".ready");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        return Results.Ok(new { status = "Ready" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "NotReady", error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).WithName("Ready").WithTags("Ops");

app.MapPost("/api/shorten", async (ShortenRequest request, CancellationToken cancellationToken) =>
{
    var outcome = await links.ShortenAsync(request.Url, request.CustomAlias, request.ExpiresInDays, cancellationToken);
    return outcome switch
    {
        ShortenOutcome.Created created => Results.Created(
            "/" + created.Mapping.Code,
            ToShortenBody(created.Mapping, created: true)),
        ShortenOutcome.Replayed replayed => Results.Ok(ToShortenBody(replayed.Mapping, created: false)),
        ShortenOutcome.Invalid invalid => ApiError(StatusCodes.Status400BadRequest, invalid.Reason, "invalid-request"),
        ShortenOutcome.AliasConflict => ApiError(StatusCodes.Status409Conflict, "Custom alias is already in use.", "alias-conflict"),
        _ => ApiError(StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "unexpected-error")
    };
}).RequireRateLimiting("per-ip").WithName("Shorten").WithTags("Links");

app.MapGet("/api/stats/{code}", async (string code, CancellationToken cancellationToken) =>
{
    if (IsBlankOrMalformedCode(code))
        return MissingShortUrl();

    var stats = await links.GetStatsAsync(code, cancellationToken);
    return stats is null ? MissingShortUrl() : Results.Ok(stats);
}).WithName("Stats").WithTags("Links");

app.MapDelete("/api/urls/{code}", async (string code, CancellationToken cancellationToken) =>
{
    if (IsBlankOrMalformedCode(code))
        return MissingShortUrl();

    var deleted = await links.SoftDeleteAsync(code, cancellationToken);
    return deleted ? Results.NoContent() : MissingShortUrl();
}).WithName("SoftDelete").WithTags("Links");

app.MapGet("/dashboard", () => Results.Content(DashboardPage.Html, "text/html"));

app.MapGet("/api/runs", () => Results.Ok(runs.List()));
app.MapGet("/api/runs/{runId}", (string runId) =>
{
    var detail = runs.Get(runId);
    return detail is null ? Results.NotFound(new { error = "Run not found." }) : Results.Ok(detail);
});
app.MapGet("/api/runs/{runId}/audit", (string runId) =>
{
    var audit = runs.ReadAudit(runId);
    return audit is null ? Results.NotFound(new { error = "Audit log not found." }) : Results.Ok(audit);
});
app.MapGet("/api/runs/{runId}/metrics", (string runId) =>
{
    var metrics = runs.ReadMetrics(runId);
    return metrics is null ? Results.NotFound(new { error = "Metrics not found." }) : Results.Ok(metrics);
});
app.MapGet("/api/approvals", () => Results.Ok(runs.PendingApprovals()));
app.MapPost("/api/approvals/{runId}", async (string runId, ApprovalBody body, CancellationToken cancellationToken) =>
{
    var decision = body.Decision?.Trim().ToLowerInvariant();
    if (decision is not ("approve" or "reject"))
        return Results.BadRequest(new { error = "Decision must be approve or reject." });

    var wrote = await runs.WriteDecisionAsync(runId, decision, body.Comment ?? "", cancellationToken);
    return wrote ? Results.Ok(new { runId, decision }) : Results.NotFound(new { error = "No pending approval for that run." });
});

app.MapGet("/{code}", async (string code, HttpContext http, CancellationToken cancellationToken) =>
{
    if (IsBlankOrMalformedCode(code))
        return MissingShortUrl();

    var outcome = await links.ResolveAsync(code, cancellationToken);
    if (IsFetchPreview(http.Request))
        return ToFetchPreview(outcome);

    if (outcome is RedirectOutcome.Found found)
    {
        if (!IsAbsoluteHttpUrl(found.Mapping.OriginalUrl))
            return InvalidDestination();

        await links.RecordClickAsync(
            found.Mapping,
            http.Request.Headers.UserAgent.ToString(),
            http.Request.Headers.Referer.ToString(),
            cancellationToken);
        return Results.Redirect(found.Mapping.OriginalUrl, permanent: false);
    }

    return ToLinkStatus(outcome);
}).RequireRateLimiting("per-ip").WithName("Redirect").WithTags("Links");

app.Run();

static IResult ApiError(int status, string error, string code) =>
    Results.Json(new { error, code, status }, statusCode: status);

static IResult MissingShortUrl() =>
    ApiError(StatusCodes.Status404NotFound, "Short URL was not found.", "missing-code");

static IResult InvalidDestination() =>
    ApiError(StatusCodes.Status400BadRequest, "Short URL destination is invalid.", "invalid-destination");

static bool IsFetchPreview(HttpRequest request)
{
    var mode = request.Headers["Sec-Fetch-Mode"].ToString();
    var dest = request.Headers["Sec-Fetch-Dest"].ToString();
    return mode.Equals("cors", StringComparison.OrdinalIgnoreCase)
        || dest.Equals("empty", StringComparison.OrdinalIgnoreCase);
}

static IResult ToFetchPreview(RedirectOutcome outcome) => outcome switch
{
    RedirectOutcome.Found found when !IsAbsoluteHttpUrl(found.Mapping.OriginalUrl) => InvalidDestination(),
    RedirectOutcome.Found found => Results.Ok(new
    {
        code = found.Mapping.Code,
        originalUrl = found.Mapping.OriginalUrl,
        shortUrl = "/" + found.Mapping.Code,
        status = "Active",
        expiresAtUtc = found.Mapping.ExpiresAtUtc
    }),
    _ => ToLinkStatus(outcome)
};

static IResult ToLinkStatus(RedirectOutcome outcome) => outcome switch
{
    RedirectOutcome.Expired => ApiError(StatusCodes.Status410Gone, "Short URL has expired.", "expired"),
    _ => MissingShortUrl()
};

static bool IsAbsoluteHttpUrl(string? url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
    (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
     uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

static bool IsBlankOrMalformedCode(string? code) =>
    string.IsNullOrWhiteSpace(code) || code.Contains('.') || code.Length is < 3 or > 32;

static object ToShortenBody(UrlMapping mapping, bool created) => new
{
    code = mapping.Code,
    shortUrl = "/" + mapping.Code,
    originalUrl = mapping.OriginalUrl,
    expiresAtUtc = mapping.ExpiresAtUtc,
    created
};

public sealed class ShortenRequest
{
    public string? Url { get; set; }
    public string? CustomAlias { get; set; }
    public int? ExpiresInDays { get; set; }
}

public sealed class ApprovalBody
{
    public string? Decision { get; set; }
    public string? Comment { get; set; }
}

public partial class Program;

namespace UrlShortener.Api
{
    public static class RepoPaths
    {
        public static string FindRepoRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir is not null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "UrlShortener.Agentic.sln")) ||
                        File.Exists(Path.Combine(dir.FullName, "UrlShortener.Agentic.slnx")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }

            return Directory.GetCurrentDirectory();
        }
    }

    public sealed class RunCatalog
    {
        private readonly string _runsDirectory;

        public RunCatalog(string runsDirectory) => _runsDirectory = runsDirectory;

        public IReadOnlyList<object> List()
        {
            if (!Directory.Exists(_runsDirectory))
                return [];

            var list = new List<object>();
            foreach (var dir in Directory.GetDirectories(_runsDirectory).OrderByDescending(d => d, StringComparer.Ordinal))
            {
                var state = ReadState(dir);
                if (state is null)
                    continue;
                list.Add(Summarize(state.Value, Path.GetFileName(dir)));
            }

            return list;
        }

        public object? Get(string runId)
        {
            var dir = RunDir(runId);
            if (dir is null)
                return null;
            var state = ReadState(dir);
            return state is null ? null : JsonSerializer.Deserialize<object>(state.Value.GetRawText());
        }

        public object? ReadAudit(string runId)
        {
            var dir = RunDir(runId);
            if (dir is null)
                return null;
            var path = Path.Combine(dir, "audit.jsonl");
            if (!File.Exists(path))
                return null;

            var events = new List<JsonElement>();
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    events.Add(doc.RootElement.Clone());
                }
                catch
                {
                    events.Add(JsonSerializer.SerializeToElement(new { raw = line, parseError = true }));
                }
            }

            return events;
        }

        public object? ReadMetrics(string runId)
        {
            var dir = RunDir(runId);
            if (dir is null)
                return null;
            var path = Path.Combine(dir, "metrics.json");
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }

        public IReadOnlyList<object> PendingApprovals()
        {
            if (!Directory.Exists(_runsDirectory))
                return [];

            var pending = new List<object>();
            foreach (var dir in Directory.GetDirectories(_runsDirectory))
            {
                var requestPath = Path.Combine(dir, "approval-request.json");
                if (!File.Exists(requestPath))
                    continue;
                try
                {
                    using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
                    var requestId = request.RootElement.TryGetProperty("requestId", out var id) ? id.GetString() : "";
                    if (HasMatchingDecision(dir, requestId))
                        continue;
                    pending.Add(new
                    {
                        runId = Path.GetFileName(dir),
                        request = JsonSerializer.Deserialize<object>(request.RootElement.GetRawText())
                    });
                }
                catch
                {
                    // Ignore a partially written request file; the next refresh will read it.
                }
            }

            return pending;
        }

        public async Task<bool> WriteDecisionAsync(string runId, string decision, string comment, CancellationToken cancellationToken)
        {
            var dir = RunDir(runId);
            if (dir is null)
                return false;
            var requestPath = Path.Combine(dir, "approval-request.json");
            if (!File.Exists(requestPath))
                return false;

            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath, cancellationToken));
            var requestId = request.RootElement.TryGetProperty("requestId", out var id) ? id.GetString() : "";
            var payload = JsonSerializer.Serialize(new
            {
                requestId,
                decision,
                comment,
                decidedAtUtc = DateTime.UtcNow.ToString("O")
            });
            await File.WriteAllTextAsync(Path.Combine(dir, "approval-decision.json"), payload, cancellationToken);
            return true;
        }

        private string? RunDir(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId) || runId.IndexOfAny(['/', '\\', '.']) >= 0)
                return null;
            var dir = Path.Combine(_runsDirectory, runId);
            return Directory.Exists(dir) ? dir : null;
        }

        private static JsonElement? ReadState(string dir)
        {
            var path = Path.Combine(dir, "state.json");
            if (!File.Exists(path))
                return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.Clone();
            }
            catch
            {
                return null;
            }
        }

        private static object Summarize(JsonElement state, string folder)
        {
            var stages = new List<object>();
            if (state.TryGetProperty("stages", out var stageMap))
            {
                foreach (var stage in stageMap.EnumerateObject())
                {
                    stages.Add(new
                    {
                        id = stage.Name,
                        status = stage.Value.TryGetProperty("status", out var status)
                            ? status.ValueKind == JsonValueKind.String ? status.GetString() : status.ToString()
                            : ""
                    });
                }
            }

            return new
            {
                runId = Text(state, "runId", folder),
                name = Text(state, "workflowName", folder),
                status = Text(state, "status", ""),
                scenarioType = Text(state, "scenarioType", ""),
                stages
            };
        }

        private static string Text(JsonElement element, string name, string fallback)
        {
            if (!element.TryGetProperty(name, out var value))
                return fallback;
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
        }

        private static bool HasMatchingDecision(string dir, string? requestId)
        {
            var decisionPath = Path.Combine(dir, "approval-decision.json");
            if (!File.Exists(decisionPath))
                return false;
            try
            {
                using var decision = JsonDocument.Parse(File.ReadAllText(decisionPath));
                var id = decision.RootElement.TryGetProperty("requestId", out var value) ? value.GetString() : "";
                return string.Equals(id, requestId, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }
}
