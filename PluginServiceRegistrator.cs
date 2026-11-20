using Jellyfin.Plugin.ArrCalendar.Arr;
using Jellyfin.Plugin.ArrCalendar.Browser;
using Jellyfin.Plugin.ArrCalendar.Library;
using Jellyfin.Plugin.ArrCalendar.Posters;
using Jellyfin.Plugin.ArrCalendar.Security;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArrCalendar;

/// <summary>
/// Registers plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // One instance, so the settings page's status endpoint reads the same registration
        // state the hosted service writes.
        serviceCollection.AddSingleton<BrowserScriptRegistrar>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<BrowserScriptRegistrar>());

        serviceCollection.AddSingleton<ArrSecretSource>();
        serviceCollection.AddSingleton<ImageSigner>();
        serviceCollection.AddSingleton<ArrCalendarRateLimiter>();

        // Built here rather than registering a bare HttpClient: the handler's settings are
        // security policy, and a shared registration would hand them to unrelated consumers.
        serviceCollection.AddSingleton<IArrClient>(provider => new ArrClient(
            ArrClient.CreateHttpClient(),
            provider.GetRequiredService<ILogger<ArrClient>>()));
        serviceCollection.AddSingleton<CalendarService>();
        serviceCollection.AddSingleton<LibraryItemFinder>();
    }
}
