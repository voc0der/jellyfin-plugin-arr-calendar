using System.Net;

namespace Jellyfin.Plugin.ArrCalendar.Arr;

/// <summary>
/// The configured address or key cannot be used, or the service answered in a way that only a
/// wrong address explains, such as a redirect.
/// </summary>
public sealed class ArrConfigurationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArrConfigurationException"/> class.
    /// </summary>
    /// <param name="message">A message safe to show an administrator.</param>
    public ArrConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The service could not be reached in time.
/// </summary>
public sealed class ArrConnectionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArrConnectionException"/> class.
    /// </summary>
    /// <param name="message">A message safe to show an administrator.</param>
    /// <param name="innerException">The transport failure, for the log only.</param>
    public ArrConnectionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The service answered, but not with something the calendar can use.
/// </summary>
public sealed class ArrApiException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArrApiException"/> class.
    /// </summary>
    /// <param name="statusCode">The status the service answered with, or 502 for an unusable body.</param>
    /// <param name="message">A message safe to show an administrator. Never carries an upstream body.</param>
    public ArrApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the status the service answered with.
    /// </summary>
    public HttpStatusCode StatusCode { get; }
}
