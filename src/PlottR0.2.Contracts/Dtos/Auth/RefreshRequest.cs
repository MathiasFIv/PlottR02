namespace PlottR0._2.Contracts.Dtos.Auth;

/// <summary>
/// Refresh token exchange payload. The client sends the opaque refresh token
/// (as returned at login or by a previous refresh) to receive a fresh token pair.
/// </summary>
public class RefreshRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}
