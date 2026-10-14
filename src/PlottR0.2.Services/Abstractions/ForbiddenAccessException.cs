namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Thrown by a service when the calling actor lacks the rights the operation
/// requires (e.g. a non-manager attempting to assign a privileged role).
/// Controllers map this to HTTP 403 Forbidden — deliberately distinct from the
/// 401 returned for missing/invalid credentials.
/// </summary>
public sealed class ForbiddenAccessException : Exception
{
    /// <summary>Creates a new <see cref="ForbiddenAccessException"/> with the given message.</summary>
    /// <param name="message">The reason the caller is not permitted.</param>
    public ForbiddenAccessException(string message) : base(message) { }
}