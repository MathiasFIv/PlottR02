namespace PlottR0._2.Contracts.Dtos.Auth;

/// <summary>
/// New token pair returned after a successful refresh. The client replaces its
/// stored tokens with these values (rotation: the old refresh token is revoked).
/// </summary>
public class RefreshResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
}
