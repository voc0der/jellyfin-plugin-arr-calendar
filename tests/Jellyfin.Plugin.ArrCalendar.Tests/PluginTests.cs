using Jellyfin.Plugin.ArrCalendar.Arr;
using Jellyfin.Plugin.ArrCalendar.Browser;
using Jellyfin.Plugin.ArrCalendar.Configuration;
using Jellyfin.Plugin.ArrCalendar.Library;
using Jellyfin.Plugin.ArrCalendar.Posters;
using Jellyfin.Plugin.ArrCalendar.Security;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Plugin.ArrCalendar.Tests;

[Collection("PluginInstance")]
public class PluginTests
{
    [Fact]
    public void PluginId_IsThisPluginsOwnGuid()
    {
        // scripts/verify-plugin-guid.sh enforces this across the repository; this pins the
        // value the assembly itself reports.
        Assert.Equal(Guid.Parse("c3d840e2-b80d-4f3f-93c7-8b246d01ce0f"), Plugin.PluginId);
        Assert.Equal(Guid.Parse("c937e280-0ac7-4250-a620-8c5892e628fe"), SeerrIntegrationPlugin.Id);
    }

    [Fact]
    public void Plugin_ExposesItsIdentity()
    {
        var plugin = PluginTestHost.WithConfiguration(new PluginConfiguration());

        // The name decides the install directory, so it may never change.
        Assert.Equal("Arr Calendar", plugin.Name);
        Assert.Equal(Plugin.PluginId, plugin.Id);
        Assert.False(string.IsNullOrWhiteSpace(plugin.Description));
        Assert.Same(plugin, Plugin.Instance);
    }

    [Fact]
    public void Plugin_ServesTheEmbeddedConfigurationPage()
    {
        var plugin = PluginTestHost.WithConfiguration(new PluginConfiguration());

        var page = Assert.Single(plugin.GetPages());

        Assert.Equal("Arr Calendar", page.Name);
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Contains(Plugin.PluginId.ToString("D"), reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_DefaultsToAnEnabledCalendarWithNothingConnected()
    {
        var config = new PluginConfiguration();

        Assert.True(config.Enabled);
        Assert.False(config.IncludeUnmonitored);
        Assert.False(config.DontSpecifyMethod);
        Assert.Equal(string.Empty, config.SonarrUrl);
        Assert.Equal(string.Empty, config.RadarrApiKey);
        Assert.Equal(TimeSpan.FromSeconds(15), config.GetRequestTimeout());
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(-4, 15)]
    [InlineData(30, 30)]
    [InlineData(500, 120)]
    public void Configuration_ClampsTheTimeout(int configured, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), new PluginConfiguration { RequestTimeoutSeconds = configured }.GetRequestTimeout());
    }

    [Fact]
    public void Configuration_ReadsEachServicesOwnFields()
    {
        var config = new PluginConfiguration { SonarrUrl = "http://s", SonarrApiKey = "sk", RadarrUrl = "http://r", RadarrApiKey = "rk" };

        Assert.Equal("http://s", config.GetUrl(ArrKind.Sonarr));
        Assert.Equal("http://r", config.GetUrl(ArrKind.Radarr));
        Assert.Equal("sk", config.GetStoredApiKey(ArrKind.Sonarr));
        Assert.Equal("rk", config.GetStoredApiKey(ArrKind.Radarr));
    }

    [Fact]
    public void ServiceRegistrator_SharesOneRegistrarWithTheHostAndBuildsEveryService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(ListLogger<>));
        services.AddSingleton(Substitute.For<ILibraryManager>());
        services.AddSingleton(Substitute.For<IUserManager>());

        new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());

        using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        Assert.Same(provider.GetRequiredService<BrowserScriptRegistrar>(), hosted);
        Assert.IsType<ArrClient>(provider.GetRequiredService<IArrClient>());
        Assert.NotNull(provider.GetRequiredService<CalendarService>());
        Assert.NotNull(provider.GetRequiredService<LibraryItemFinder>());
        Assert.NotNull(provider.GetRequiredService<ImageSigner>());
        Assert.NotNull(provider.GetRequiredService<ArrCalendarRateLimiter>());
        Assert.Same(provider.GetRequiredService<ArrSecretSource>(), provider.GetRequiredService<ArrSecretSource>());
    }
}
