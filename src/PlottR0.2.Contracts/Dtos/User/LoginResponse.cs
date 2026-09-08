namespace PlottR0._2.Contracts.Dtos.User;

/// <summary>
/// Successful login result: the authenticated user's public profile plus the
/// initial JWT token pair (short-lived access token + long-lived refresh token).
/// Never carries the password hash.
/// </summary>
public class LoginResponse
{
    public UserResponse User { get; set; } = null!;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
}
