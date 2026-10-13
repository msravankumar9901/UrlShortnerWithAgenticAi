using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Agentic.Orchestrator;

public interface ILlmClient
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}

public sealed class LlmRequest
{
    public string Role { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string UserPrompt { get; set; } = "";
    public string? CorrelationId { get; set; }
}

public sealed class LlmResponse
{
    public string Content { get; set; } = "";
    public bool UsedFallback { get; set; }
    public string Provider { get; set; } = "";
    public int LatencyMs { get; set; }
}

public sealed class LlmSettings
{
    public bool UseAzure { get; init; }
    public string? ApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public string Model { get; init; } = "gpt-4o-mini";
    public string? AzureEndpoint { get; init; }
    public string? AzureDeployment { get; init; }
    public string AzureApiVersion { get; init; } = "2024-10-21";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public static LlmSettings FromEnvironment()
    {
        var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(azureEndpoint) && !string.IsNullOrWhiteSpace(azureKey))
        {
            return new LlmSettings
            {
                UseAzure = true,
                ApiKey = azureKey,
                AzureEndpoint = azureEndpoint.TrimEnd('/'),
                AzureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini",
                AzureApiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-10-21"
            };
        }

        return new LlmSettings
        {
            ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            BaseUrl = (Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com/v1").TrimEnd('/'),
            Model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini"
        };
    }
}

public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly LlmSettings _settings;
    private readonly HttpClient _http;

    public OpenAiCompatibleLlmClient(LlmSettings settings, HttpClient? httpClient = null)
    {
        _settings = settings;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public string Name => _settings.UseAzure ? "azure-openai" : "openai";
    public bool IsConfigured => _settings.IsConfigured;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new LlmUnavailableException("No OpenAI or Azure OpenAI API key is configured.");

        var url = _settings.UseAzure
            ? $"{_settings.AzureEndpoint}/openai/deployments/{_settings.AzureDeployment}/chat/completions?api-version={_settings.AzureApiVersion}"
            : $"{_settings.BaseUrl}/chat/completions";

        var body = new Dictionary<string, object?>
        {
            ["messages"] = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            },
            ["temperature"] = 0.2
        };
        if (!_settings.UseAzure)
            body["model"] = _settings.Model;

        var started = DateTime.UtcNow;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            if (_settings.UseAzure)
                httpRequest.Headers.Add("api-key", _settings.ApiKey);
            else
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            httpRequest.Headers.TryAddWithoutValidation("User-Agent", "UrlShortener-Agentic/1.0");
            if (!string.IsNullOrWhiteSpace(request.CorrelationId))
                httpRequest.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
            httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(httpRequest, cancellationToken);
            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                if (attempt == 3)
                    throw new LlmUnavailableException($"LLM provider returned HTTP {(int)response.StatusCode}.");
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new LlmUnavailableException($"LLM provider returned HTTP {(int)response.StatusCode}.");

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
            if (string.IsNullOrWhiteSpace(content))
                throw new LlmUnavailableException("LLM provider returned an empty response.");

            return new LlmResponse
            {
                Content = content,
                Provider = Name,
                LatencyMs = (int)(DateTime.UtcNow - started).TotalMilliseconds
            };
        }

        throw new LlmUnavailableException("LLM provider retries exhausted.");
    }
}

public sealed class RuleBasedLlmClient : ILlmClient
{
    public string Name => "rule-based";
    public bool IsConfigured => true;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var content = request.Role switch
        {
            "RequirementsAgent" => Requirements(request.UserPrompt),
            "ArchitectAgent" => Architecture(request.UserPrompt),
            "DeveloperAgent" => Developer(request.UserPrompt),
            "ReleaseAgent" => Release(request.UserPrompt),
            _ => JsonSerializer.Serialize(new
            {
                summary = "Deterministic offline response.",
                role = request.Role,
                note = Truncate(request.UserPrompt, 400)
            }, OrchestratorJson.Options)
        };

        return Task.FromResult(new LlmResponse
        {
            Content = content,
            Provider = Name,
            UsedFallback = false
        });
    }

    private static string Requirements(string text)
    {
        if (Contains(text, "secure") && Contains(text, "fast") && !text.Contains("APPROVED INTERPRETATION", StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(new
            {
                problemStatement = "The phrase \"make links secure and fast\" names qualities but not a testable requirement.",
                functionalRequirements = new object[]
                {
                    new { id = "FR-1", text = "Keep shorten, redirect, analytics, and soft delete working for already-accepted behavior." }
                },
                nonFunctionalRequirements = Array.Empty<object>(),
                ambiguities = new object[]
                {
                    new { id = "AMB-1", topic = "secure", question = "Does secure mean SSRF controls, authentication, encryption at rest, or something else?" },
                    new { id = "AMB-2", topic = "fast", question = "Which percentile and budget define fast?" },
                    new { id = "AMB-3", topic = "privacy", question = "Are unique visitors, retention, or raw IP logs in scope?" }
                },
                assumptions = new[] { "Nothing about security or latency is treated as approved until a human chooses an interpretation." },
                acceptanceCriteria = new[] { "A human approves a concrete interpretation before downstream design is final." },
                requiresHumanApproval = true,
                proposedInterpretations = new[]
                {
                    "Candidate, not approved: accept only http/https, reject private and loopback addresses, rate limit per IP, and set a p95 redirect budget during approval."
                }
            }, OrchestratorJson.Options);
        }

        if (Contains(text, "alias") || Contains(text, "collision") || Contains(text, "expir"))
        {
            return JsonSerializer.Serialize(new
            {
                problemStatement = "Extend the existing shortener with custom aliases and link expiry, and fix duplicate-code allocation under concurrency.",
                functionalRequirements = new object[]
                {
                    new { id = "FR-1", text = "Accept an optional custom alias and return 409 when it is already taken." },
                    new { id = "FR-2", text = "Accept expiresInDays and return 410 for expired links without recording a click." },
                    new { id = "FR-3", text = "Allocate base62 codes with a bounded collision retry so concurrent creates cannot persist duplicate codes." },
                    new { id = "FR-4", text = "Shortening the same active URL again returns the original code." }
                },
                nonFunctionalRequirements = new object[]
                {
                    new { id = "NFR-1", text = "JSON persistence stays behind IUrlRepository so a later database does not rewrite the API." },
                    new { id = "NFR-2", text = "Writes are atomic and serialized so a crash cannot leave a partial document." }
                },
                ambiguities = Array.Empty<object>(),
                assumptions = new[] { "Deleted codes stay reserved so an old alias cannot be silently revived.", "Expiry is whole days, evaluated in UTC." },
                acceptanceCriteria = new[] { "TC-06 expired is 410.", "TC-07 alias conflict is 409.", "TC-09 concurrent creates lose no rows.", "TC-12 same URL returns the same code." },
                requiresHumanApproval = false
            }, OrchestratorJson.Options);
        }

        return JsonSerializer.Serialize(new
        {
            problemStatement = "Build a URL shortener that creates short links, redirects, and reports click analytics, with validation and a durable local store.",
            functionalRequirements = new object[]
            {
                new { id = "FR-1", text = "POST /api/shorten accepts an http(s) URL and returns a code." },
                new { id = "FR-2", text = "GET /{code} redirects with 302 and records timestamp, user agent, and referrer." },
                new { id = "FR-3", text = "Unknown codes return 404. The service exposes GET /health." },
                new { id = "FR-4", text = "GET /api/stats/{code} returns total clicks, clicks per day, and top referrers." }
            },
            nonFunctionalRequirements = new object[]
            {
                new { id = "NFR-1", text = "Reject non-http(s) schemes and localhost or private IP literals." },
                new { id = "NFR-2", text = "Per-IP rate limiting returns 429." },
                new { id = "NFR-3", text = "The same active URL shortens to the same code." }
            },
            ambiguities = Array.Empty<object>(),
            assumptions = new[] { "No authentication in this increment.", "JSON files are the system of record for the prototype." },
            acceptanceCriteria = new[] { "Valid https returns 201 and a code.", "javascript: is rejected.", "Three clicks yield a total of 3." },
            requiresHumanApproval = false
        }, OrchestratorJson.Options);
    }

    private static string Architecture(string text)
    {
        if (text.Contains("APPROVED INTERPRETATION", StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(new
            {
                mode = "approved",
                summary = "Architecture updated from the approved interpretation of secure and fast.",
                components = new[] { "UrlShortener.Api", "UrlShortener.Core", "UrlShortener.Infrastructure", "Agentic.Orchestrator" },
                api = new { shorten = "POST /api/shorten", redirect = "GET /{code}", stats = "GET /api/stats/{code}" },
                nonFunctional = new
                {
                    secure = "http/https only, SSRF block for localhost and private IP literals, per-IP 429, no raw IP in the click log",
                    fast = "p95 redirect under 50ms via in-process JSON lookup; redirect does not fetch the destination"
                },
                decisions = new[]
                {
                    "ADR-1: Do not DNS-resolve destination hosts.",
                    "ADR-2: Rate limit creation and redirect with the built-in ASP.NET limiter.",
                    "ADR-3: Human approval remains on the release gate."
                },
                approvalRequired = new[] { "Release" }
            }, OrchestratorJson.Options);
        }

        if (Contains(text, "secure") && Contains(text, "fast"))
        {
            return JsonSerializer.Serialize(new
            {
                mode = "assumption",
                summary = "Draft architecture only. Security and latency are still ambiguous, so this design is not final.",
                openQuestions = new[] { "Security control set is unapproved.", "Latency budget is unapproved." },
                components = new[] { "Existing modular monolith" },
                decisions = new[] { "Do not invent authentication or a multi-region design until the human approves an interpretation." }
            }, OrchestratorJson.Options);
        }

        if (Contains(text, "alias") || Contains(text, "collision"))
        {
            return JsonSerializer.Serialize(new
            {
                mode = "brownfield",
                summary = "Alias and expiry stay inside the existing repository and service. No public route is removed.",
                impacted = new[] { "UrlShortenerService", "JsonUrlRepository", "UrlMapping", "POST /api/shorten", "GET /{code}" },
                dataFlow = "Request -> validation -> locked read/modify/write of urls.json -> atomic replace. Redirect reads the mapping, then appends clicks.json.",
                decisions = new[] { "ADR: Keep IUrlRepository so JSON can be replaced later without changing endpoints.", "Collision retries are bounded to 8 attempts inside the write lock." },
                compatibility = "Existing codes keep working. New fields are optional."
            }, OrchestratorJson.Options);
        }

        return JsonSerializer.Serialize(new
        {
            mode = "greenfield",
            summary = "Modular monolith: HTTP adapter, domain service, JSON repositories, and a separate governed orchestrator.",
            components = new[] { "UrlShortener.Api", "UrlShortener.Core", "UrlShortener.Infrastructure" },
            api = new { shorten = "POST /api/shorten", redirect = "GET /{code}", stats = "GET /api/stats/{code}", delete = "DELETE /api/urls/{code}", health = "GET /health" },
            schema = new { urls = "data/urls.json", clicks = "data/clicks.json" },
            decisions = new[]
            {
                "ADR-1: The orchestrator, not the LLM, owns state transitions.",
                "ADR-2: JSON files behind repository interfaces.",
                "ADR-3: Deterministic LLM fallback so the demo runs offline."
            }
        }, OrchestratorJson.Options);
    }

    private static string Developer(string text) => JsonSerializer.Serialize(new
    {
        status = "implemented-in-workspace",
        summary = "Workspace artifacts describe the change. Production code already lives in src/UrlShortener.* and is not mutated by the agent.",
        failureFeedback = string.IsNullOrWhiteSpace(text) ? null : Truncate(text, 300)
    }, OrchestratorJson.Options);

    private static string Release(string text) => JsonSerializer.Serialize(new
    {
        narrative = "Release readiness is computed by the release agent from stage evidence. This note is commentary only.",
        evidence = Truncate(text, 500)
    }, OrchestratorJson.Options);

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];
}

public sealed class FallbackLlmClient : ILlmClient
{
    private readonly ILlmClient _primary;
    private readonly ILlmClient _fallback;

    public FallbackLlmClient(ILlmClient primary, ILlmClient fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public string Name => _primary.IsConfigured ? _primary.Name + "+fallback" : _fallback.Name;
    public bool IsConfigured => true;
    public bool PrimaryConfigured => _primary.IsConfigured;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        if (_primary.IsConfigured)
        {
            try
            {
                var primary = await _primary.CompleteAsync(request, cancellationToken);
                if (!string.IsNullOrWhiteSpace(primary.Content))
                {
                    primary.UsedFallback = false;
                    return primary;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The deterministic client below is the reliability path.
            }
        }

        var fallback = await _fallback.CompleteAsync(request, cancellationToken);
        fallback.UsedFallback = true;
        fallback.Provider = _fallback.Name;
        return fallback;
    }
}

public static class LlmClientFactory
{
    public static FallbackLlmClient CreateFromEnvironment() =>
        new(new OpenAiCompatibleLlmClient(LlmSettings.FromEnvironment()), new RuleBasedLlmClient());
}
