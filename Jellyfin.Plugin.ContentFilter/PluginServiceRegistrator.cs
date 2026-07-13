using Jellyfin.Plugin.ContentFilter.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ContentFilter;

/// <summary>
/// Registers plugin services with Jellyfin's dependency injection container.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        // Named client for Ollama: no timeout — inference on CPU/slow GPU can take several minutes.
        // Cancellation is handled via the job CancellationToken passed per-request.
        serviceCollection.AddHttpClient(nameof(OllamaClient), client =>
        {
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        });
        serviceCollection.AddSingleton<FilterStore>();
        serviceCollection.AddSingleton<SubtitleFilter>();
        serviceCollection.AddSingleton<OllamaClient>();
        // Register VideoScanner as a concrete singleton so ScanController can resolve it directly,
        // then also wire it up as a hosted service using the same instance.
        serviceCollection.AddSingleton<VideoScanner>();
        serviceCollection.AddHostedService(static sp => sp.GetRequiredService<VideoScanner>());
        serviceCollection.AddHostedService<PlaybackMonitor>();
    }
}
