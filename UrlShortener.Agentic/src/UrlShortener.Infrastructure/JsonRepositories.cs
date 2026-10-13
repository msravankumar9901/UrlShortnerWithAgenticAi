using System.Text.Json;
using System.Text.Json.Serialization;
using UrlShortener.Core;

namespace UrlShortener.Infrastructure;

public static class JsonFileOptions
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class JsonUrlRepository : IUrlRepository
{
    public const int MaxCodeAttempts = 8;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly AtomicFileStore _store;
    private readonly ICodeGenerator _codes;

    public JsonUrlRepository(string path, AtomicFileStore store, ICodeGenerator codes)
    {
        _path = path;
        _store = store;
        _codes = codes;
    }

    public async Task<ShortenOutcome> CreateAsync(string originalUrl, string? customAlias, DateTime? expiresAtUtc, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var db = await ReadAsync(cancellationToken);
            var normalized = UrlValidator.Normalize(originalUrl);
            var existing = db.Urls.FirstOrDefault(u =>
                !u.IsDeleted &&
                !u.IsExpired(nowUtc) &&
                string.Equals(u.NormalizedUrl, normalized, StringComparison.Ordinal));
            if (existing is not null)
                return new ShortenOutcome.Replayed(existing);

            string code;
            if (!string.IsNullOrWhiteSpace(customAlias))
            {
                if (db.Urls.Any(u => string.Equals(u.Code, customAlias, StringComparison.Ordinal)))
                    return new ShortenOutcome.AliasConflict(customAlias);
                code = customAlias;
            }
            else
            {
                string? allocated = null;
                for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
                {
                    var candidate = _codes.Generate();
                    if (db.Urls.All(u => !string.Equals(u.Code, candidate, StringComparison.Ordinal)))
                    {
                        allocated = candidate;
                        break;
                    }
                }

                if (allocated is null)
                    throw new InvalidOperationException("Could not allocate a unique short code within the retry bound.");
                code = allocated;
            }

            var mapping = new UrlMapping
            {
                Code = code,
                OriginalUrl = originalUrl,
                NormalizedUrl = normalized,
                CreatedAtUtc = nowUtc,
                ExpiresAtUtc = expiresAtUtc
            };
            db.Urls.Add(mapping);
            await WriteAsync(db, cancellationToken);
            return new ShortenOutcome.Created(mapping);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UrlMapping?> FindByCodeAsync(string code, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var db = await ReadAsync(cancellationToken);
            return db.Urls.FirstOrDefault(u => string.Equals(u.Code, code, StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SoftDeleteAsync(string code, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var db = await ReadAsync(cancellationToken);
            var mapping = db.Urls.FirstOrDefault(u => string.Equals(u.Code, code, StringComparison.Ordinal) && !u.IsDeleted);
            if (mapping is null)
                return false;

            mapping.IsDeleted = true;
            mapping.DeletedAtUtc = nowUtc;
            await WriteAsync(db, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UrlFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new UrlFile();

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var db = await JsonSerializer.DeserializeAsync<UrlFile>(stream, JsonFileOptions.Indented, cancellationToken);
        return db ?? new UrlFile();
    }

    private Task WriteAsync(UrlFile db, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(db, JsonFileOptions.Indented);
        return _store.WriteAsync(_path, json, cancellationToken);
    }

    private sealed class UrlFile
    {
        public List<UrlMapping> Urls { get; set; } = [];
    }
}

public sealed class JsonClickRepository : IClickRepository
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly AtomicFileStore _store;

    public JsonClickRepository(string path, AtomicFileStore store)
    {
        _path = path;
        _store = store;
    }

    public async Task AddAsync(ClickRecord click, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var db = await ReadAsync(cancellationToken);
            db.Clicks.Add(click);
            await _store.WriteAsync(_path, JsonSerializer.Serialize(db, JsonFileOptions.Indented), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ClickRecord>> ListByCodeAsync(string code, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var db = await ReadAsync(cancellationToken);
            return db.Clicks.Where(c => string.Equals(c.Code, code, StringComparison.Ordinal)).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ClickFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new ClickFile();

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var db = await JsonSerializer.DeserializeAsync<ClickFile>(stream, JsonFileOptions.Indented, cancellationToken);
        return db ?? new ClickFile();
    }

    private sealed class ClickFile
    {
        public List<ClickRecord> Clicks { get; set; } = [];
    }
}
