using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Core;

namespace UrlShortener.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddUrlShortener(this IServiceCollection services, string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ICodeGenerator, Base62CodeGenerator>();
        services.AddSingleton(new AtomicFileStore());
        services.AddSingleton<IUrlRepository>(sp => new JsonUrlRepository(
            Path.Combine(dataDirectory, "urls.json"),
            sp.GetRequiredService<AtomicFileStore>(),
            sp.GetRequiredService<ICodeGenerator>()));
        services.AddSingleton<IClickRepository>(sp => new JsonClickRepository(
            Path.Combine(dataDirectory, "clicks.json"),
            sp.GetRequiredService<AtomicFileStore>()));
        services.AddSingleton<UrlShortenerService>();
        return services;
    }
}
