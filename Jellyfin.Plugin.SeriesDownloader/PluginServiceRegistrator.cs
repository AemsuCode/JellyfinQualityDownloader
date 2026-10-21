using Jellyfin.Plugin.SeriesDownloader.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SeriesDownloader;

/// <summary>
/// Registers plugin services with the Jellyfin dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<DownloadJobManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<DownloadJobManager>());

        serviceCollection.AddSingleton<WebUiInjectorService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WebUiInjectorService>());

        // In-memory middleware injection for read-only Docker containers (/jellyfin/jellyfin-web/index.html)
        serviceCollection.AddTransient<IStartupFilter, WebUiInjectionStartupFilter>();
    }
}
