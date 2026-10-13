using System.Text.RegularExpressions;

namespace UrlShortener.Core;

public sealed class UrlShortenerService
{
    private static readonly Regex AliasPattern = new("^[A-Za-z0-9_-]{3,32}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "health", "dashboard", "swagger", "openapi", "metrics", "audit", "runs", "favicon"
    };

    private readonly IUrlRepository _urls;
    private readonly IClickRepository _clicks;
    private readonly IClock _clock;

    public UrlShortenerService(IUrlRepository urls, IClickRepository clicks, IClock clock)
    {
        _urls = urls;
        _clicks = clicks;
        _clock = clock;
    }

    public Task<ShortenOutcome> ShortenAsync(string? url, string? customAlias, int? expiresInDays, CancellationToken cancellationToken)
    {
        var validation = UrlValidator.Validate(url);
        if (!validation.IsValid)
            return Task.FromResult<ShortenOutcome>(new ShortenOutcome.Invalid(validation.Reason));

        if (expiresInDays is < 1 or > 3650)
            return Task.FromResult<ShortenOutcome>(new ShortenOutcome.Invalid("expiresInDays must be between 1 and 3650."));

        string? alias = string.IsNullOrWhiteSpace(customAlias) ? null : customAlias.Trim();
        if (alias is not null)
        {
            if (!AliasPattern.IsMatch(alias) || ReservedAliases.Contains(alias))
                return Task.FromResult<ShortenOutcome>(new ShortenOutcome.Invalid("Custom alias must be 3-32 characters (letters, digits, '_' or '-') and must not be a reserved path."));
        }

        var now = UtcTime.AsUtc(_clock.UtcNow);
        DateTime? expires = expiresInDays is int days ? now.AddDays(days) : null;
        return _urls.CreateAsync(url!.Trim(), alias, expires, now, cancellationToken);
    }

    public async Task<RedirectOutcome> ResolveAsync(string code, CancellationToken cancellationToken)
    {
        var mapping = await _urls.FindByCodeAsync(code, cancellationToken);
        if (mapping is null || mapping.IsDeleted)
            return new RedirectOutcome.Missing();

        var now = UtcTime.AsUtc(_clock.UtcNow);
        if (mapping.IsExpired(now))
            return new RedirectOutcome.Expired(mapping);

        return new RedirectOutcome.Found(mapping);
    }

    public Task RecordClickAsync(UrlMapping mapping, string? userAgent, string? referrer, CancellationToken cancellationToken)
    {
        var now = UtcTime.AsUtc(_clock.UtcNow);
        return _clicks.AddAsync(new ClickRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Code = mapping.Code,
            TimestampUtc = now,
            UserAgent = TrimHeader(userAgent, 512),
            Referrer = TrimHeader(referrer, 2048)
        }, cancellationToken);
    }

    public async Task<RedirectOutcome> RedirectAsync(string code, string? userAgent, string? referrer, CancellationToken cancellationToken)
    {
        var outcome = await ResolveAsync(code, cancellationToken);
        if (outcome is RedirectOutcome.Found found)
            await RecordClickAsync(found.Mapping, userAgent, referrer, cancellationToken);
        return outcome;
    }

    public async Task<UrlStats?> GetStatsAsync(string code, CancellationToken cancellationToken)
    {
        var mapping = await _urls.FindByCodeAsync(code, cancellationToken);
        if (mapping is null || mapping.IsDeleted)
            return null;

        var clicks = await _clicks.ListByCodeAsync(code, cancellationToken);
        var perDay = clicks
            .GroupBy(c => UtcTime.AsUtc(c.TimestampUtc).ToString("yyyy-MM-dd"))
            .OrderBy(g => g.Key)
            .Select(g => new DayCount(g.Key, g.Count()))
            .ToList();

        var referrers = clicks
            .GroupBy(c => string.IsNullOrWhiteSpace(c.Referrer) ? "(direct)" : c.Referrer)
            .Select(g => new ReferrerCount(g.Key, g.Count()))
            .OrderByDescending(r => r.Clicks)
            .ThenBy(r => r.Referrer, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        return new UrlStats
        {
            Code = mapping.Code,
            OriginalUrl = mapping.OriginalUrl,
            TotalClicks = clicks.Count,
            ClicksPerDay = perDay,
            TopReferrers = referrers
        };
    }

    public Task<bool> SoftDeleteAsync(string code, CancellationToken cancellationToken) =>
        _urls.SoftDeleteAsync(code, UtcTime.AsUtc(_clock.UtcNow), cancellationToken);

    private static string TrimHeader(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
