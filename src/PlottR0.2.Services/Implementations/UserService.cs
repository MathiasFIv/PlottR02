using BC = BCrypt.Net.BCrypt;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Mappers;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;

namespace PlottR0._2.Services.Implementations;

    public class UserService : IUserService
{
    private const int BcryptWorkFactor = 12;

    // bcrypt silently truncates the password at 72 bytes, so longer input is rejected up front.
    private const int BcryptMaxPasswordBytes = 72;

    private readonly IUserRepository _userRepository;
    private readonly IAuditService _auditService;
    private readonly ICurrentUser _currentUser;
    private readonly IOptions<AuthOptions> _authOptions;

    public UserService(
        IUserRepository userRepository,
        IAuditService auditService,
        ICurrentUser currentUser,
        IOptions<AuthOptions> authOptions)
    {
        _userRepository = userRepository;
        _auditService = auditService;
        _currentUser = currentUser;
        _authOptions = authOptions;
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _userRepository.GetByIdAsync(id, ct);

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
        => await _userRepository.GetAllAsync(null, null, ct);

    private static readonly IReadOnlyDictionary<UserRole, UserRole[]> AssignableRolesByActor =
        new Dictionary<UserRole, UserRole[]>
        {
            [UserRole.SystemAdmin] = [UserRole.SystemAdmin, UserRole.Researcher, UserRole.TrialManager, UserRole.FieldWorker],
            [UserRole.Researcher] = [UserRole.TrialManager, UserRole.FieldWorker],
            [UserRole.TrialManager] = [UserRole.FieldWorker],
            [UserRole.FieldWorker] = [],
        };

    private bool AllowAnonymousSignup => _authOptions.Value.AllowAnonymousSignup;

    private UserRole[] ResolveAssignableRoles(bool anonymousBootstrap = false)
    {
        if (anonymousBootstrap)
        {
            return AssignableRolesByActor[UserRole.SystemAdmin];
        }

        var actorRole = _currentUser.Role;
        if (actorRole is null)
        {
            throw new ForbiddenAccessException("This account is not allowed to create users.");
        }

        if (!AssignableRolesByActor.TryGetValue(actorRole.Value, out var assignable)
            || assignable.Length == 0)
        {
            throw new ForbiddenAccessException("This account is not allowed to create users.");
        }

        return assignable;
    }

    private void EnsureRoleIsAssignable(UserRole requestedRole, UserRole[] assignableRoles)
    {
        if (!assignableRoles.Contains(requestedRole))
        {
            throw new ForbiddenAccessException(
                $"Cannot assign role '{requestedRole}' as a '{_currentUser.Role}' account.");
        }
    }

    public async Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var anonymousBootstrap = _currentUser.Role is null && AllowAnonymousSignup;
        var role = anonymousBootstrap ? UserRole.SystemAdmin : request.Role;

        if (role is null)
        {
            throw new ArgumentException(
                "Role is required — anonymous/no-role accounts are no longer permitted.",
                nameof(request));
        }

        EnsureRoleIsAssignable(role.Value, ResolveAssignableRoles(anonymousBootstrap));

        ValidatePassword(request.Password);
        ValidateEmail(request.Email);

        if (await _userRepository.GetByEmailAsync(request.Email.Trim(), ct) is not null)
        {
            throw new InvalidOperationException(
                $"A user with email '{request.Email}' already exists.");
        }

        var entity = UserMapper.ToEntity(request);
        entity.Role = role.Value;
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
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        return UserMapper.ToResponse(user);
    }

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

    private static string HashPassword(string password)
        => BC.HashPassword(password, workFactor: BcryptWorkFactor);

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
            if (existing.Role is not null)
            {
                throw new ForbiddenAccessException(
                    "Clearing a user's role is not permitted — every account must keep a role.");
            }
        }
        else if (request.Role.Value == existing.Role)
        {
        }
        else
        {
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

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
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

    public async Task<UserResponse?> GetResponseAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _userRepository.GetByIdAsync(id, ct);
        return entity is null ? null : UserMapper.ToResponse(entity);
    }

    public async Task<IReadOnlyList<UserResponse>> GetAllResponsesAsync(CancellationToken ct = default)
    {
        var entities = await _userRepository.GetAllAsync(null, null, ct);
        return UserMapper.ToResponses(entities);
    }
}
