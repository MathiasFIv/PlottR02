namespace PlottR0._2.Contracts.Dtos.User;

/// <summary>
/// Login credentials — the email the user registered with and their plaintext
/// password. The password is only ever used to verify the stored BCrypt hash;
/// it is never persisted or returned.
/// </summary>
public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
