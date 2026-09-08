namespace PlottR0._2.Domain;

/// <summary>
/// Persisted opaque refresh token linked to a <see cref="User"/>. Enables session
/// renewal without re-entering credentials, and is a foundation for the future
/// offline/sync scenario. The <see cref="Token"/> value stored here is a SHA-256
/// hash of the plaintext token issued to the client — plaintext is never persisted.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public string Token { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }
}
