// BCrypt.Net-Next's type (BCrypt) sits inside a namespace of the same name
// (BCrypt.Net), which .NET cannot resolve reliably when the namespace is declared
// after the using statements. Aliasing is the documented workaround.
using BC = BCrypt.Net.BCrypt;
using System.Text;
using System.Text.Json;
using PlottR0._2.Contracts;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Mappers;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Concrete <see cref="IUserService"/> backed by <see cref="IUserRepository"/>.
/// Talks only to the repository contract (never EF/Repository classes) and exposes
/// structural reads plus the User DTO workflows.
/// </summary>
public class UserService : IUserService
{
    /// <summary>
    /// BCrypt cost factor. 12 is the current recommended baseline; it yields a
    /// 60-character <c>$2a$12$</c>/<c>$2b$12$</c> hash and keeps hashing ~100-250ms
    /// on modern hardware.
    /// </summary>
    private const int BcryptWorkFactor = 12;

    /// <summary>
    /// BCrypt only uses the first 72 bytes of input; longer passwords are silently
    /// truncated, so reject them up front instead of weakening the stored credential.
    /// </summary>
    private const int BcryptMaxPasswordBytes = 72;

    private readonly IUserRepository _userRepository;
    private readonly IAuditService _auditService;
    private readonly ICurrentUser _currentUser;

    /// <summary>
    /// Creates a new <see cref="UserService"/>.
    /// </summary>
    /// <param name="userRepository">The user repository contract to operate against.</param>
    /// <param name="auditService">The audit service contract used to record the audit trail.</param>
    /// <param name="currentUser">The current user identity attributed to audit entries.</param>
    public UserService(IUserRepository userRepository, IAuditService auditService, ICurrentUser currentUser)
    {
        _userRepository = userRepository;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    /// <inheritdoc />
    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _userRepository.GetByIdAsync(id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
        => await _userRepository.GetAllAsync(null, null, ct);

    /// <inheritdoc />
    public async Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidatePassword(request.Password);
        ValidateEmail(request.Email);

        // Guard against duplicate emails — email is the login identifier.
        if (await _userRepository.GetByEmailAsync(request.Email.Trim(), ct) is not null)
        {
            throw new InvalidOperationException(
                $"A user with email '{request.Email}' already exists.");
        }

        var entity = UserMapper.ToEntity(request);
        entity.PasswordHash = HashPassword(request.Password);
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        entity.CreatedBy = _currentUser.UserId;
        entity.UpdatedBy = _currentUser.UserId;

        var created = await _userRepository.AddAsync(entity, ct);

        await _auditService.LogAsync(
            "User.Created",
            "User",
            created.Id.ToString(),
            _currentUser.UserId,
            _currentUser.Name,
            JsonSerializer.Serialize(new { email = created.Email, name = created.Name, role = created.Role }),
            ct);

        return UserMapper.ToResponse(created);
    }

    /// <inheritdoc />
    public async Task<UserResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            throw new UnauthorizedAccessException("Email and password are required.");
        }

        var user = await _userRepository.GetByEmailAsync(request.Email.Trim(), ct);
        if (user is null || !BC.Verify(request.Password, user.PasswordHash))
        {
            // Deliberately generic — we never reveal whether the email existed,
            // only that the credential pair is invalid.
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        return UserMapper.ToResponse(user);
    }

    /// <summary>
    /// Rejects empty or malformed emails before they reach persistence.
    /// </summary>
    /// <param name="email">The email address from the request.</param>
    /// <exception cref="ArgumentException">When the email is empty or lacks an @-sign.</exception>
    private static void ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email is required and must not be empty.", nameof(email));
        }

        if (!email.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException("Email must contain an '@' character.", nameof(email));
        }
    }

    /// <summary>
    /// Rejects empty or over-72-byte passwords. BCrypt truncates input at 72 bytes,
    /// so this guard guarantees no silently weakened credential is ever stored.
    /// </summary>
    /// <param name="password">The plaintext password from the create request.</param>
    /// <exception cref="ArgumentException">When the password is empty or exceeds 72 bytes.</exception>
    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password is required and must not be empty.", nameof(password));
        }

        if (Encoding.UTF8.GetByteCount(password) > BcryptMaxPasswordBytes)
        {
            throw new ArgumentException(
                $"Password must not exceed {BcryptMaxPasswordBytes} bytes (UTF-8); BCrypt truncates longer input.",
                nameof(password));
        }
    }

    /// <summary>
    /// Hashes a plaintext password with BCrypt using the configured work factor.
    /// The return value is the only credential material ever persisted — the
    /// plaintext is not stored anywhere.
    /// </summary>
    private static string HashPassword(string password)
        => BC.HashPassword(password, workFactor: BcryptWorkFactor);

    /// <inheritdoc />
    public async Task<UserResponse?> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = await _userRepository.GetByIdAsync(id, ct);
        if (existing is null)
        {
            return null;
        }

        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = _currentUser.UserId;

        UserMapper.Apply(request, existing);
        var updated = await _userRepository.UpdateAsync(existing, ct);

        await _auditService.LogAsync(
            "User.Updated",
            "User",
            updated.Id.ToString(),
            _currentUser.UserId,
            _currentUser.Name,
            details: null,
            ct);

        return UserMapper.ToResponse(updated);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Fetch the user first so we can attribute the deletion in the audit trail.
        // If the user no longer exists, deletion is an idempotent no-op — skip logging.
        var existing = await _userRepository.GetByIdAsync(id, ct);
        if (existing is null)
        {
            return;
        }

        await _userRepository.DeleteAsync(id, ct);

        await _auditService.LogAsync(
            "User.Deleted",
            "User",
            existing.Id.ToString(),
            _currentUser.UserId,
            _currentUser.Name,
            JsonSerializer.Serialize(new { email = existing.Email, name = existing.Name, role = existing.Role }),
            ct);
    }

    /// <inheritdoc />
    public async Task<UserResponse?> GetResponseAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _userRepository.GetByIdAsync(id, ct);
        return entity is null ? null : UserMapper.ToResponse(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserResponse>> GetAllResponsesAsync(CancellationToken ct = default)
    {
        var entities = await _userRepository.GetAllAsync(null, null, ct);
        return UserMapper.ToResponses(entities);
    }
}
