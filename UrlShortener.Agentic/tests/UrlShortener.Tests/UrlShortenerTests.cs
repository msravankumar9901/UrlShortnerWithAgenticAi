using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Core;
using UrlShortener.Infrastructure;

namespace UrlShortener.Tests;

public sealed class MutableClock : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
}

public sealed class ShortenerAppFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "urlshortener-tests", Guid.NewGuid().ToString("N"));
    public int PermitLimit { get; init; } = 10000;
    public MutableClock? Clock { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataDirectory);
        builder.UseSetting("Data:Directory", DataDirectory);
        builder.UseSetting("RateLimit:PermitLimit", PermitLimit.ToString());
        builder.UseSetting("RateLimit:WindowSeconds", "60");
        if (Clock is not null)
        {
            var clock = Clock;
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(DataDirectory))
                Directory.Delete(DataDirectory, recursive: true);
        }
        catch
        {
            // The temp directory is best-effort cleanup.
        }
    }
}

public class ProductTests
{
    [Fact]
    public async Task TC01_ValidHttpsShorten_Returns201AndCode()
    {
        await using var factory = new ShortenerAppFactory();
        var client = Client(factory);
        var response = await Shorten(client, "https://example.com/welcome");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var code = await Code(response);
        Assert.False(string.IsNullOrWhiteSpace(code));
    }

    [Fact]
    public async Task TC02_JavascriptUrl_IsRejected()
    {
        await using var factory = new ShortenerAppFactory();
        var response = await Shorten(Client(factory), "javascript:alert(1)");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TC03_LoopbackUrl_IsRejected()
    {
        await using var factory = new ShortenerAppFactory();
        var response = await Shorten(Client(factory), "http://127.0.0.1/admin");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TC04_RedirectExisting_Returns302Location()
    {
        await using var factory = new ShortenerAppFactory();
        var client = Client(factory);
        var created = await Shorten(client, "https://example.com/docs");
        var code = await Code(created);
        var redirect = await client.GetAsync("/" + code);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("https://example.com/docs", redirect.Headers.Location?.ToString());
    }

    [Fact]
    public async Task SwaggerFetch_ActiveLink_Returns200JsonWithoutRedirect()
    {
        await using var factory = new ShortenerAppFactory();
        var client = Client(factory);
        var code = await Code(await Shorten(client, "https://example.com/docs"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/" + code);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal("https://example.com/docs", json.GetProperty("originalUrl").GetString());
        Assert.Equal("/" + code, json.GetProperty("shortUrl").GetString());
        Assert.Equal("Active", json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("expiresAtUtc").ValueKind);

        var stats = await client.GetFromJsonAsync<JsonElement>("/api/stats/" + code);
        Assert.Equal(0, stats.GetProperty("totalClicks").GetInt32());
    }

    [Fact]
    public async Task TC05_UnknownCode_Returns404()
    {
        await using var factory = new ShortenerAppFactory();
        var response = await Client(factory).GetAsync("/missingcode");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertMissingShortUrl(response);
    }

    [Fact]
    public async Task UnknownStatsCode_Returns404Json()
    {
        await using var factory = new ShortenerAppFactory();
        var response = await Client(factory).GetAsync("/api/stats/missingcode");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertMissingShortUrl(response);
    }

    [Fact]
    public async Task UnknownDelete_Returns404Json()
    {
        await using var factory = new ShortenerAppFactory();
        var response = await Client(factory).DeleteAsync("/api/urls/missingcode");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertMissingShortUrl(response);
    }

    [Fact]
    public async Task UnknownRedirectPreview_Returns404Json()
    {
        await using var factory = new ShortenerAppFactory();
        var client = Client(factory);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var response = await client.GetAsync("/missingcode");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertMissingShortUrl(response);
    }

    [Fact]
    public async Task TC06_ExpiredLink_Returns410()
    {
        var clock = new MutableClock { UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        await using var factory = new ShortenerAppFactory { Clock = clock };
        var client = Client(factory);
        var created = await Shorten(client, "https://example.com/expires", days: 1);
        var code = await Code(created);
        clock.UtcNow = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var redirect = await client.GetAsync("/" + code);
        Assert.Equal(HttpStatusCode.Gone, redirect.StatusCode);
    }

    [Fact]
    public async Task TC07_AliasTaken_Returns409()
    {
        await using var factory = new ShortenerAppFactory();
        var client = Client(factory);
        var first = await Shorten(client, "https://example.com/one", alias: "promo");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await Shorten(client, "https://example.com/two", alias: "promo");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task TC08_StatsAfterThreeClicks_EqualsThree()
    {
        await using var factory = new ShortenerAppFactory();
        var client = Client(factory);
        var code = await Code(await Shorten(client, "https://example.com/stats"));
        for (var i = 0; i < 3; i++)
        {
            var redirect = await client.GetAsync("/" + code);
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        }

        var stats = await client.GetFromJsonAsync<JsonElement>("/api/stats/" + code);
        Assert.Equal(3, stats.GetProperty("totalClicks").GetInt32());
    }

    [Fact]
    public async Task TC09_ConcurrentShortens_DoNotLoseWrites()
    {
        await using var factory = new ShortenerAppFactory();
        var client = Client(factory);
        var tasks = Enumerable.Range(0, 100).Select(i => Shorten(client, "https://example.com/item/" + i)).ToArray();
        var responses = await Task.WhenAll(tasks);
        var failures = new List<string>();
        foreach (var response in responses.Where(response => response.StatusCode != HttpStatusCode.Created))
            failures.Add((int)response.StatusCode + " " + await response.Content.ReadAsStringAsync());
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        var codes = new List<string>();
        foreach (var response in responses)
            codes.Add(await Code(response));
        Assert.Equal(100, codes.Distinct().Count());

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(factory.DataDirectory, "urls.json")));
        Assert.Equal(100, document.RootElement.GetProperty("urls").GetArrayLength());
        Assert.Empty(Directory.GetFiles(factory.DataDirectory, "*.tmp"));
    }

    [Fact]
    public async Task TC10_AtomicWrite_LeavesPreviousFileIntactOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "urlshortener-atomic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "urls.json");
            var store = new AtomicFileStore();
            await store.WriteAsync(path, "{\"ok\":1}");
            var failing = new AtomicFileStore(new ThrowBeforeCommit());
            await Assert.ThrowsAsync<IOException>(() => failing.WriteAsync(path, "{\"ok\":2}"));
            Assert.Equal("{\"ok\":1}", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TC11_RateLimit_Returns429()
    {
        await using var factory = new ShortenerAppFactory { PermitLimit = 2 };
        var client = Client(factory);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await Shorten(client, "https://example.com/rate/" + i)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, statuses[0]);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task TC12_SameUrlTwice_ReturnsSameCode()
    {
        await using var factory = new ShortenerAppFactory();
        var client = Client(factory);
        var first = await Shorten(client, "https://example.com/same");
        var second = await Shorten(client, "https://example.com/same");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await Code(first), await Code(second));
    }

    [Fact]
    public async Task SoftDelete_ThenRedirect_Returns404()
    {
        await using var factory = new ShortenerAppFactory();
        var client = Client(factory);
        var code = await Code(await Shorten(client, "https://example.com/delete-me"));
        var deleted = await client.DeleteAsync("/api/urls/" + code);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var redirect = await client.GetAsync("/" + code);
        Assert.Equal(HttpStatusCode.NotFound, redirect.StatusCode);
    }

    [Theory]
    [InlineData("http://10.1.2.3/a")]
    [InlineData("http://192.168.0.5/a")]
    [InlineData("http://localhost/a")]
    [InlineData("file:///c:/temp")]
    public void Validator_BlocksUnsafeDestinations(string url)
    {
        Assert.False(UrlValidator.Validate(url).IsValid);
    }

    [Fact]
    public void Validator_AllowsPublicHttps()
    {
        Assert.True(UrlValidator.Validate("https://example.com/path").IsValid);
    }

    private static HttpClient Client(ShortenerAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Task<HttpResponseMessage> Shorten(HttpClient client, string url, string? alias = null, int? days = null)
    {
        var payload = new Dictionary<string, object?> { ["url"] = url };
        if (alias is not null)
            payload["customAlias"] = alias;
        if (days is not null)
            payload["expiresInDays"] = days;
        return client.PostAsJsonAsync("/api/shorten", payload);
    }

    private static async Task<string> Code(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("code").GetString() ?? "";
    }

    private static async Task AssertMissingShortUrl(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Short URL was not found.", json.GetProperty("error").GetString());
        Assert.Equal("missing-code", json.GetProperty("code").GetString());
        Assert.Equal(404, json.GetProperty("status").GetInt32());
    }

    private sealed class ThrowBeforeCommit : ICommitGuard
    {
        public void OnBeforeCommit(string tempPath, string destinationPath) =>
            throw new IOException("Simulated failure before commit");
    }
}
