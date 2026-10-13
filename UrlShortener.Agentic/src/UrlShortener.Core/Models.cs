namespace UrlShortener.Core;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class UrlMapping
{
    public string Code { get; set; } = "";
    public string OriginalUrl { get; set; } = "";
    public string NormalizedUrl { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }

    public bool IsExpired(DateTime nowUtc) =>
        ExpiresAtUtc is DateTime expires && nowUtc >= UtcTime.AsUtc(expires);
}

public sealed class ClickRecord
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string UserAgent { get; set; } = "";
    public string Referrer { get; set; } = "";
}

public sealed record DayCount(string Day, int Clicks);

public sealed record ReferrerCount(string Referrer, int Clicks);

public sealed class UrlStats
{
    public string Code { get; set; } = "";
    public string OriginalUrl { get; set; } = "";
    public int TotalClicks { get; set; }
    public IReadOnlyList<DayCount> ClicksPerDay { get; set; } = [];
    public IReadOnlyList<ReferrerCount> TopReferrers { get; set; } = [];
}

public abstract record ShortenOutcome
{
    public sealed record Created(UrlMapping Mapping) : ShortenOutcome;
    public sealed record Replayed(UrlMapping Mapping) : ShortenOutcome;
    public sealed record Invalid(string Reason) : ShortenOutcome;
    public sealed record AliasConflict(string Alias) : ShortenOutcome;
}

public abstract record RedirectOutcome
{
    public sealed record Found(UrlMapping Mapping) : RedirectOutcome;
    public sealed record Missing : RedirectOutcome;
    public sealed record Expired(UrlMapping Mapping) : RedirectOutcome;
}

public readonly record struct UrlValidationResult(bool IsValid, string Reason)
{
    public static UrlValidationResult Ok() => new(true, "");
    public static UrlValidationResult Invalid(string reason) => new(false, reason);
}

public static class UtcTime
{
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
