using Microsoft.Extensions.DependencyInjection;

namespace AndroidExplorer.Search;

public static class SearchServiceCollectionExtensions
{
    /// <summary>Registers search, storage analysis and duplicate detection.</summary>
    public static IServiceCollection AddSearchServices(this IServiceCollection services)
    {
        services.AddSingleton<ISearchServiceFactory, SearchServiceFactory>();
        return services;
    }
}
