using System.Net;
using System.Net.Security;

namespace Jellyfin.Plugin.ArrCalendar.Arr;

/// <summary>
/// Decides which HTTPS certificates are verified: all of them, except on private and loopback
/// addresses, where self-signed certificates are normal.
/// </summary>
/// <remarks>
/// The same rule as Torrent Throttle. Sonarr and Radarr on a LAN or a Docker network commonly
/// serve HTTPS with a self-signed certificate, so an address that is a private, loopback or
/// link-local IP accepts any certificate. Redirects are refused, so requests never leave that
/// address. Host names are always verified, because a name implies a certificate issued for it,
/// and a name that resolves somewhere else would otherwise silently lose its protection.
/// </remarks>
internal static class LocalAddress
{
    private static readonly IPNetwork[] PrivateNetworks =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10")
    ];

    /// <summary>
    /// Gets a value indicating whether a URL names a private, loopback or link-local IP literal.
    /// </summary>
    /// <param name="uri">The URL.</param>
    /// <returns><c>true</c> for such an address; <c>false</c> for host names and public addresses.</returns>
    public static bool IsPrivateAddress(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return IsPrivateHost(uri.DnsSafeHost);
    }

    /// <summary>
    /// Gets a value indicating whether a host is a private, loopback or link-local IP literal.
    /// </summary>
    /// <param name="host">A host as written in a URL, with or without IPv6 brackets.</param>
    /// <returns><c>true</c> for such an address; <c>false</c> for names and public addresses.</returns>
    public static bool IsPrivateHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || !IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return PrivateNetworks.Any(network => network.Contains(address));
    }

    /// <summary>
    /// The certificate check the outbound handler applies to every TLS connection.
    /// </summary>
    /// <remarks>
    /// The shared handler serves Sonarr and Radarr at different addresses, so the decision is made
    /// per connection, from the host the connection was opened for. <see cref="SocketsHttpHandler"/>
    /// calls this with the <see cref="SslStream"/> as <paramref name="sender"/>, whose
    /// <see cref="SslStream.TargetHostName"/> is the URL's host. (Only a callback set through
    /// <c>HttpClientHandler</c> receives the request instead, which is accepted too.) Connections
    /// are pooled per host and port, so one accepted for a private address only ever carries
    /// requests to that address.
    /// </remarks>
    /// <param name="sender">The TLS stream, or the request that opened the connection.</param>
    /// <param name="certificate">The server certificate.</param>
    /// <param name="chain">The chain.</param>
    /// <param name="errors">What platform validation found.</param>
    /// <returns><c>true</c> to accept the connection.</returns>
    public static bool ValidateCertificate(
        object sender,
        System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
        System.Security.Cryptography.X509Certificates.X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        var host = sender switch
        {
            SslStream stream => stream.TargetHostName,
            HttpRequestMessage { RequestUri: { } uri } => uri.DnsSafeHost,
            _ => null
        };
        return IsPrivateHost(host);
    }
}
