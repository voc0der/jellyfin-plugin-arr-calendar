namespace Jellyfin.Plugin.ArrCalendar;

/// <summary>
/// The Seerr Integration plugin, whose Requests entry the Calendar nests under on devices that
/// can hover.
/// </summary>
/// <remarks>
/// Only read to tell the administrator, on the settings page, where the Calendar entry will
/// sit. The browser decides placement from what Jellyfin Web actually shows. Kept apart from
/// <see cref="Plugin"/> so that file names exactly one GUID, its own, which is what
/// <c>scripts/verify-plugin-guid.sh</c> checks.
/// </remarks>
public static class SeerrIntegrationPlugin
{
    /// <summary>
    /// Seerr Integration's plugin identifier.
    /// </summary>
    public static readonly Guid Id = Guid.Parse("c937e280-0ac7-4250-a620-8c5892e628fe");
}
