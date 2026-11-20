using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.ArrCalendar.Arr;

namespace Jellyfin.Plugin.ArrCalendar.Posters;

/// <summary>
/// Signs artwork URLs, so the image endpoint can stay anonymous without being enumerable.
/// </summary>
/// <remarks>
/// Browsers load artwork with <c>img</c> elements and CSS, which cannot carry Jellyfin's
/// authorization header, so the image endpoint cannot ask who is calling. Sonarr's and Radarr's
/// ids are small sequential numbers, though, and an open endpoint keyed by them would let anyone
/// walk the whole library. Each URL the calendar hands out therefore carries an HMAC of the
/// service and id under a key that never leaves this process: only someone who was shown the
/// entry on a calendar can load its picture.
/// <para>
/// The key is new at every start, so URLs from before a restart stop working; the page asks for
/// fresh ones whenever it loads a range.
/// </para>
/// </remarks>
public sealed class ImageSigner
{
    /// <summary>
    /// Base64url characters kept from the HMAC: 22 of them carry 132 bits.
    /// </summary>
    internal const int SignatureLength = 22;

    private readonly byte[] _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageSigner"/> class with a random key.
    /// </summary>
    public ImageSigner()
        : this(RandomNumberGenerator.GetBytes(32))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageSigner"/> class with a given key, for tests.
    /// </summary>
    /// <param name="key">The HMAC key.</param>
    internal ImageSigner(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 16)
        {
            throw new ArgumentException("The key must be at least 128 bits.", nameof(key));
        }

        _key = (byte[])key.Clone();
    }

    /// <summary>
    /// Signs one service's artwork for one id.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <param name="id">The series or movie id in that service.</param>
    /// <returns>The signature.</returns>
    public string Sign(ArrKind kind, int id)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(kind.Id() + ":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_')[..SignatureLength];
    }

    /// <summary>
    /// Checks a signature in constant time.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <param name="id">The series or movie id.</param>
    /// <param name="signature">The presented signature.</param>
    /// <returns><c>true</c> when it is this process's signature for exactly that artwork.</returns>
    public bool Verify(ArrKind kind, int id, string? signature)
    {
        if (signature is null || signature.Length != SignatureLength)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(kind, id)),
            Encoding.ASCII.GetBytes(signature));
    }
}
