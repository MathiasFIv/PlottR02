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
    /// structural reads plus the User DTO workflows. Enforces the account creation/sharing
    /// matrix and the "no anonymous accounts" policy.
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

    /// <summary>
    /// The account-creation/assignment matrix: the roles each actor role may grant.
    /// A missing entry or an empty array means the actor may not create or assign roles.
    /// </summary>
    private static readonly IReadOnlyDictionary<UserRole, UserRole[]> AssignableRolesByActor =
        new Dictionary<UserRole, UserRole[]>
        {
            [UserRole.SystemAdmin] = [UserRole.SystemAdmin, UserRole.Researcher, UserRole.TrialManager, UserRole.FieldWorker],
            [UserRole.Researcher] = [UserRole.TrialManager, UserRole.FieldWorker],
            [UserRole.TrialManager] = [UserRole.FieldWorker],
            [UserRole.FieldWorker] = [],
        };

    /// <summary>
    /// Resolves the roles the current actor may assign, or throws when the actor is
    /// anonymous/role-less or has no assignable roles (e.g. a <see cref="UserRole.FieldWorker"/>).
    /// Shared by <see cref="CreateAsync"/> and <see cref="UpdateAsync"/> so both enforce
    /// exactly the same matrix.
    /// </summary>
    /// <exception cref="ForbiddenAccessException">When the actor may not create/assign users.</exception>
    private UserRole[] ResolveAssignableRoles()
    {
        var actorRole = _currentUser.Role;
        if (actorRole is null
            || !AssignableRolesByActor.TryGetValue(actorRole.Value, out var assignable)
            || assignable.Length == 0)
        {
            throw new ForbiddenAccessException("This account is not allowed to create users.");
        }

        return assignable;
    }

    /// <summary>
    /// Throws when <paramref name="requestedRole"/> is not one the current actor may assign.
    /// </summary>
    /// <param name="requestedRole">The role being granted or changed to.</param>
    /// <param name="assignableRoles">The roles resolved by <see cref="ResolveAssignableRoles"/>.</param>
    /// <exception cref="ForbiddenAccessException">When the requested role is outside the actor's matrix row.</exception>
    private void EnsureRoleIsAssignable(UserRole requestedRole, UserRole[] assignableRoles)
    {
        if (!assignableRoles.Contains(requestedRole))
        {
            throw new ForbiddenAccessException(
                $"Cannot assign role '{requestedRole}' as a '{_currentUser.Role}' account.");
        }
    }

    /// <inheritdoc />
    public async Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // No anonymous/no-role actor may create users, and a creator with no assignable
        // roles (FieldWorker) has nothing it is allowed to grant.
        var assignableRoles = ResolveAssignableRoles();

        // A role is mandatory — anonymous/no-role accounts are no longer permitted anywhere.
        if (request.Role is null)
        {
            throw new ArgumentException(
                "Role is required — anonymous/no-role accounts are no longer permitted.",
                nameof(request));
        }

        EnsureRoleIsAssignable(request.Role.Value, assignableRoles);

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

        if (request.Role is null)
        {
            // Nullifying the role would clear it. A legacy no-role user may keep its
            // role-less state (name/email edits only), but a real role may never be cleared.
            if (existing.Role is not null)
            {
                throw new ForbiddenAccessException(
                    "Clearing a user's role is not permitted — every account must keep a role.");
            }
        }
        else if (request.Role.Value == existing.Role)
        {
            // Echoing the unchanged role is not an assignment — no matrix check needed.
        }
        else
        {
            // A genuine role change is bound by the same matrix as account creation.
            var assignableRoles = ResolveAssignableRoles();
            EnsureRoleIsAssignable(request.Role.Value, assignableRoles);
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
