using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ArrCalendar.Security;

/// <summary>
/// Renders untrusted values safe to write into a log line.
/// </summary>
/// <remarks>
/// Values that reach the log from a request or from Sonarr and Radarr pass through here, so a
/// value cannot start a forged second entry or reorder the one carrying it (CWE-117).
/// </remarks>
public static partial class LogSanitizer
{
    private const int MaxLoggedLength = 256;

    /// <summary>
    /// Returns a value that cannot alter the structure of the log line carrying it.
    /// </summary>
    /// <param name="value">The untrusted value.</param>
    /// <returns>The value with control and format characters replaced and its length bounded.</returns>
    public static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        var bounded = value.Length <= MaxLoggedLength ? value : value[..MaxLoggedLength] + "...";
        return UnsafeForLog().Replace(bounded, "?");
    }

    // Cc covers line breaks, Cf bidi overrides, and Zl/Zp the separators many viewers break on.
    // Not the whole C group: Cs would mangle every character outside the BMP.
    [GeneratedRegex(@"[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]")]
    private static partial Regex UnsafeForLog();
}
