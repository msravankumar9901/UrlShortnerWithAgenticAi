namespace UrlShortener.Core;

public interface ICodeGenerator
{
    string Generate(int length = 7);
}

public interface IUrlRepository
{
    Task<ShortenOutcome> CreateAsync(string originalUrl, string? customAlias, DateTime? expiresAtUtc, DateTime nowUtc, CancellationToken cancellationToken);
    Task<UrlMapping?> FindByCodeAsync(string code, CancellationToken cancellationToken);
    Task<bool> SoftDeleteAsync(string code, DateTime nowUtc, CancellationToken cancellationToken);
}

public interface IClickRepository
{
    Task AddAsync(ClickRecord click, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClickRecord>> ListByCodeAsync(string code, CancellationToken cancellationToken);
}
