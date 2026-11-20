using Jellyfin.Plugin.ArrCalendar.Browser;
using Jellyfin.Plugin.ArrCalendar.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ArrCalendar;

/// <summary>
/// Main Arr Calendar plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Plugin unique identifier.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("c3d840e2-b80d-4f3f-93c7-8b246d01ce0f");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Jellyfin XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Arr Calendar";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Adds a Calendar page to Jellyfin Web with upcoming episodes from Sonarr and movie releases from Radarr, including what is downloading right now.";

    /// <summary>
    /// Gets the active plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        // JavaScript Injector keeps plugin scripts in its own configuration and never prunes
        // them, so an uninstalled plugin would otherwise leave its loader behind for good.
        new JavaScriptInjectorBridge().Unregister(BrowserScriptRegistrar.ScriptId);
        base.OnUninstalling();
    }
}
