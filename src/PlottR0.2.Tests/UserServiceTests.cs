using Microsoft.Extensions.DependencyInjection;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Tests.Support;

namespace PlottR0._2.Tests;

/// <summary>
/// Integration tests for <see cref="IUserService"/> against a real PostgreSQL
/// (via Testcontainers) and the real EF <c>UserRepository</c> — no mocks.
/// Each method is covered with one valid, one invalid, and one edge scenario.
/// All tests run inside Docker.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UserServiceTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public UserServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Wipes the shared DB and restores the default test actor after each
    /// test, so neither rows nor actor identity leak into the next test.</summary>
    public Task DisposeAsync()
    {
        _fixture.CurrentUser.ResetToDefaults();
        return _fixture.ResetDatabaseAsync();
    }

    private IUserService Service(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IUserService>();

    /// <summary>The four valid <see cref="UserRole"/> values, for exhaustive matrix theories.</summary>
    private static readonly UserRole[] AllRoles =
    [
        UserRole.SystemAdmin,
        UserRole.Researcher,
        UserRole.TrialManager,
        UserRole.FieldWorker,
    ];

    // ── CreateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidRequest_CreatesUserAndReturnsResponse()
    {
        var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var result = await service.CreateAsync(request);

        result.Should().NotBeNull();
        result.Id.Should().NotBeEmpty();
        result.Name.Should().Be(request.Name);
        result.Email.Should().Be(request.Email);
        result.Role.Should().Be(request.Role);

        // The persisted entity must never hold plaintext — only a BCrypt hash.
        var persisted = await scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>()
            .GetByEmailAsync(request.Email);
        persisted.Should().NotBeNull();
        persisted!.PasswordHash.Should().StartWith("$2") // BCrypt hash prefix
            .And.NotBe(request.Password);
    }

    [Fact]
    public async Task CreateAsync_DuplicateEmail_ThrowsInvalidOperation()
    {
        var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        await service.CreateAsync(request); // first insert succeeds

        // Second create with the same email must be rejected as a duplicate.
        var duplicate = new CreateUserRequest
        {
            Name = request.Name,
            Email = request.Email, // same email
            Password = request.Password,
            Role = request.Role,
        };

        var act = () => service.CreateAsync(duplicate);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*already exists*");
    }

    [Fact]
    public async Task CreateAsync_NullRequest_ThrowsArgumentNull()
    {
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        CreateUserRequest? request = null;
        var act = () => service.CreateAsync(request!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── CreateAsync role-assignment matrix ──

    [Theory]
    [InlineData(UserRole.SystemAdmin, UserRole.SystemAdmin, true)]
    [InlineData(UserRole.SystemAdmin, UserRole.Researcher, true)]
    [InlineData(UserRole.SystemAdmin, UserRole.TrialManager, true)]
    [InlineData(UserRole.SystemAdmin, UserRole.FieldWorker, true)]
    [InlineData(UserRole.Researcher, UserRole.SystemAdmin, false)]
    [InlineData(UserRole.Researcher, UserRole.Researcher, false)]
    [InlineData(UserRole.Researcher, UserRole.TrialManager, true)]
    [InlineData(UserRole.Researcher, UserRole.FieldWorker, true)]
    [InlineData(UserRole.TrialManager, UserRole.SystemAdmin, false)]
    [InlineData(UserRole.TrialManager, UserRole.Researcher, false)]
    [InlineData(UserRole.TrialManager, UserRole.TrialManager, false)]
    [InlineData(UserRole.TrialManager, UserRole.FieldWorker, true)]
    public async Task CreateAsync_RoleAssignmentMatrix_AllowsOnlyAssignablePairs(UserRole actorRole, UserRole requestedRole, bool shouldAllow)
    {
        _fixture.CurrentUser.Role = actorRole;

        var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
        request.Role = requestedRole;

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        if (shouldAllow)
        {
            var result = await service.CreateAsync(request);
            result.Role.Should().Be(requestedRole);
        }
        else
        {
            var act = () => service.CreateAsync(request);
            await act.Should().ThrowAsync<ForbiddenAccessException>()
                .WithMessage("*Cannot assign role*");
        }
    }

    [Theory]
    [InlineData(UserRole.FieldWorker)]
    [InlineData(null)]
    public async Task CreateAsync_WithoutAssignableRights_DeniedForEveryRole(UserRole? actorRole)
    {
        // FieldWorkers and no-role/anonymous actors have no assignable rights and are
        // denied regardless of the requested role — the gate short-circuits before the
        // per-role matrix even runs.
        _fixture.CurrentUser.UserId = null;
        _fixture.CurrentUser.Role = actorRole;

        foreach (var requestedRole in AllRoles)
        {
            var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
            request.Role = requestedRole;

            using var scope = _fixture.CreateScope();
            var service = Service(scope);

            var act = () => service.CreateAsync(request);
            await act.Should().ThrowAsync<ForbiddenAccessException>()
                .WithMessage("*not allowed to create users*");
        }
    }

    [Fact]
    public async Task CreateAsync_NullRole_ThrowsArgumentException()
    {
        var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
        request.Role = null;
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var act = () => service.CreateAsync(request);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Role is required*");
    }

    // ── LoginAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsUser()
    {
        const string email = "login.valid@plottr02.local";
        const string password = "Sup3rS3cret!";
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        await service.CreateAsync(new CreateUserRequest
        {
            Name = "Valid Login User",
            Email = email,
            Password = password,
            Role = UserRole.Researcher,
        });

        var result = await service.LoginAsync(new LoginRequest { Email = email, Password = password });

        result.Should().NotBeNull();
        result.Email.Should().Be(email);
        // The response DTO simply has no password surface at all — verify the typed shape.
        result.Should().BeAssignableTo<UserResponse>();
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_ThrowsUnauthorized()
    {
        const string email = "login.wrong@plottr02.local";
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        await service.CreateAsync(new CreateUserRequest
        {
            Name = "Wrong Pass User",
            Email = email,
            Password = "CorrectPass123!",
            Role = UserRole.FieldWorker,
        });

        var act = () => service.LoginAsync(new LoginRequest { Email = email, Password = "WrongPass123!" });
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Invalid email or password.*");
    }

    [Fact]
    public async Task LoginAsync_UnknownEmail_ThrowsGenericUnauthorized()
    {
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        // The exception must be identical whether the email doesn't exist or the
        // password is wrong — never reveal which part failed (enumeration hardening).
        var act = () => service.LoginAsync(new LoginRequest
        {
            Email = "nobody@plottr02.local",
            Password = "Whatever123!",
        });

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Invalid email or password.*");
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ExistingUser_AppliesChanges()
    {
        const string email = "update.valid@plottr02.local";
        var created = await CreatePersistedUser(email, "Initial Name", "InitialPass123!");

        using var scope = _fixture.CreateScope();
        var service = Service(scope);
        var update = UserFaker.UpdateRequest().Generate();

        var result = await service.UpdateAsync(created.Id, update);

        result.Should().NotBeNull();
        result!.Id.Should().Be(created.Id); // identity preserved
        result.Name.Should().Be(update.Name);
        result.Email.Should().Be(update.Email);
        result.Role.Should().Be(update.Role);
    }

    [Fact]
    public async Task UpdateAsync_NonExistentId_ReturnsNull()
    {
        using var scope = _fixture.CreateScope();
        var service = Service(scope);
        var update = UserFaker.UpdateRequest().Generate();

        var result = await service.UpdateAsync(Guid.NewGuid(), update);

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_NullRequest_ThrowsArgumentNull()
    {
        const string email = "update.null@plottr02.local";
        var created = await CreatePersistedUser(email, "Null Req User", "InitPass123!");
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        UpdateUserRequest? update = null;
        var act = () => service.UpdateAsync(created.Id, update!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_ExistingUser_RemovesUser()
    {
        const string email = "delete.valid@plottr02.local";
        var created = await CreatePersistedUser(email, "Delete Me", "DeletePass123!");

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        await service.DeleteAsync(created.Id);

        // The user should now be gone from both the service view and the repo.
        var repo = scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>();
        (await repo.GetByEmailAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentId_IsIdempotentNoOp()
    {
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        // Deleting a user that never existed must not throw and must not error.
        var act = () => service.DeleteAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_IsolatedFromActorIdentity_StillDeletes()
    {
        // Edge: deletion works regardless of actor identity (empty/unknown actor).
        _fixture.CurrentUser.UserId = null;
        _fixture.CurrentUser.Name = null;

        const string email = "delete.edge@plottr02.local";
        var created = await CreatePersistedUser(email, "Edge Delete", "EdgePass123!");

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        await service.DeleteAsync(created.Id);

        var repo = scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>();
        (await repo.GetByEmailAsync(email)).Should().BeNull();
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_ExistingUser_ReturnsEntity()
    {
        const string email = "getbyid.valid@plottr02.local";
        var created = await CreatePersistedUser(email, "GetById User", "GetByIdPass123!");

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var found = await service.GetByIdAsync(created.Id);

        found.Should().NotBeNull();
        found!.Id.Should().Be(created.Id);
        found.Email.Should().Be(email);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var found = await service.GetByIdAsync(Guid.NewGuid());

        found.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_EmptyGuid_ReturnsNull()
    {
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var found = await service.GetByIdAsync(Guid.Empty);

        found.Should().BeNull();
    }

    // ── GetAllAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllAsync_WithUsers_ReturnsAll()
    {
        await CreatePersistedUser("all.one@plottr02.local", "Alpha", "AlphaPass123!");
        await CreatePersistedUser("all.two@plottr02.local", "Bravo", "BravoPass123!");

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var users = await service.GetAllAsync();

        users.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllAsync_EmptyDatabase_ReturnsEmpty()
    {
        // ResetDatabaseAsync (DisposeAsync) already emptied the table.
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var users = await service.GetAllAsync();

        users.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsImmutableReadOnlyList()
    {
        await CreatePersistedUser("all.immutable@plottr02.local", "Immutable", "ImmutePass123!");

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var users = await service.GetAllAsync();

        // The contract promises IReadOnlyList<T> — exercising the type confirms it.
        users.Should().BeAssignableTo<IReadOnlyList<User>>();
    }

    // ── GetResponseAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_ExistingUser_ReturnsDto()
    {
        const string email = "response.valid@plottr02.local";
        var created = await CreatePersistedUser(email, "Response User", "RespPass123!");

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var dto = await service.GetResponseAsync(created.Id);

        dto.Should().NotBeNull();
        dto!.Id.Should().Be(created.Id);
        dto.Email.Should().Be(email);
    }

    [Fact]
    public async Task GetResponseAsync_NonExistentId_ReturnsNull()
    {
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var dto = await service.GetResponseAsync(Guid.NewGuid());

        dto.Should().BeNull();
    }

    [Fact]
    public async Task GetResponseAsync_EmptyGuid_ReturnsNull()
    {
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var dto = await service.GetResponseAsync(Guid.Empty);

        dto.Should().BeNull();
    }

    // ── GetAllResponsesAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetAllResponsesAsync_WithUsers_ReturnsDtoList()
    {
        await CreatePersistedUser("allresp.one@plottr02.local", "Resp Alpha", "RAPass123!");
        await CreatePersistedUser("allresp.two@plottr02.local", "Resp Bravo", "RBPass123!");

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var dtos = await service.GetAllResponsesAsync();

        dtos.Should().HaveCount(2);
        dtos.Should().AllBeOfType<UserResponse>();
    }

    [Fact]
    public async Task GetAllResponsesAsync_EmptyDatabase_ReturnsEmpty()
    {
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var dtos = await service.GetAllResponsesAsync();

        dtos.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllResponsesAsync_NeverExposesPasswordHash()
    {
        var created = await CreatePersistedUser("allresp.nohash@plottr02.local", "No Hash", "NHPass123!");
        _ = created; // persisted via factory

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var dtos = await service.GetAllResponsesAsync();

        // DTO surface must not leak credential material — assert the typed shape has
        // no password/hash member at all.
        var dtoPropertyNames = typeof(UserResponse).GetProperties().Select(p => p.Name).ToArray();
        dtoPropertyNames.Should().BeEquivalentTo(
            [nameof(UserResponse.Id), nameof(UserResponse.Name), nameof(UserResponse.Email), nameof(UserResponse.Role)]);
    }

    // ── Role round-trip (typed enum contract) ─────────────────────────────────

    [Fact]
    public async Task CreateAndUpdateAsync_Role_RoundTripsThroughDatabaseAsEnum()
    {
        var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
        request.Role = UserRole.TrialManager;
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var created = await service.CreateAsync(request);

        created.Role.Should().Be(UserRole.TrialManager);

        // Persisted entity must materialise the enum from the string column.
        var repo = scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>();
        var persisted = await repo.GetByEmailAsync(request.Email);
        persisted!.Role.Should().Be(UserRole.TrialManager);

        // Update to a different member and confirm it survives the round-trip.
        var updated = await service.UpdateAsync(created.Id, new UpdateUserRequest
        {
            Name = created.Name,
            Email = created.Email,
            Role = UserRole.FieldWorker,
        });

        updated!.Role.Should().Be(UserRole.FieldWorker);
    }

    // ── UpdateAsync role policy ───────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_RoleChange_ByTrialManager_AllowedOnlyForFieldWorker()
    {
        // A TrialManager may only move a user to FieldWorker (its sole assignable role).
        _fixture.CurrentUser.Role = UserRole.SystemAdmin;
        var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
        request.Role = UserRole.TrialManager;

        using var scope = _fixture.CreateScope();
        var service = Service(scope);
        var trialManager = await service.CreateAsync(request);

        _fixture.CurrentUser.Role = UserRole.TrialManager;

        var toFieldWorker = await service.UpdateAsync(trialManager.Id, new UpdateUserRequest
        {
            Name = trialManager.Name,
            Email = trialManager.Email,
            Role = UserRole.FieldWorker,
        });
        toFieldWorker!.Role.Should().Be(UserRole.FieldWorker);

        var toSystemAdmin = () => service.UpdateAsync(trialManager.Id, new UpdateUserRequest
        {
            Name = trialManager.Name,
            Email = trialManager.Email,
            Role = UserRole.SystemAdmin,
        });
        await toSystemAdmin.Should().ThrowAsync<ForbiddenAccessException>()
            .WithMessage("*Cannot assign role*");

        var toResearcher = () => service.UpdateAsync(trialManager.Id, new UpdateUserRequest
        {
            Name = trialManager.Name,
            Email = trialManager.Email,
            Role = UserRole.Researcher,
        });
        await toResearcher.Should().ThrowAsync<ForbiddenAccessException>()
            .WithMessage("*Cannot assign role*");
    }

    [Fact]
    public async Task UpdateAsync_NullRole_OnRoleHoldingUser_ThrowsForbidden()
    {
        // A null role on a user that currently has a role would CLEAR it — forbidden.
        _fixture.CurrentUser.Role = UserRole.SystemAdmin;
        var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
        request.Role = UserRole.FieldWorker;

        using var scope = _fixture.CreateScope();
        var service = Service(scope);
        var created = await service.CreateAsync(request);

        var clearRole = () => service.UpdateAsync(created.Id, new UpdateUserRequest
        {
            Name = "Renamed",
            Email = created.Email,
            Role = null,
        });

        await clearRole.Should().ThrowAsync<ForbiddenAccessException>()
            .WithMessage("*every account must keep a role*");

        // The persisted role must be untouched — the clear must not have gone through.
        var repo = scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>();
        var persisted = await repo.GetByIdAsync(created.Id);
        persisted!.Role.Should().Be(UserRole.FieldWorker);
    }

    [Fact]
    public async Task UpdateAsync_NullRole_OnNoRoleUser_IsAllowed()
    {
        // Legacy no-role users keep their role-less state; name/email edits are allowed.
        var existing = await PersistUserAsync(
            "update.nullrole.norole@plottr02.local", "No Role User", "Pass123!", role: null);
        _fixture.CurrentUser.Role = UserRole.SystemAdmin;

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var result = await service.UpdateAsync(existing.Id, new UpdateUserRequest
        {
            Name = "Renamed",
            Email = "update.nullrole.norole@plottr02.local",
            Role = null,
        });

        result.Should().NotBeNull();
        result!.Name.Should().Be("Renamed");
        result.Role.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_UnchangedRole_IsNotAnAssignment()
    {
        // Echoing the current role is not a change — even an actor who could not grant
        // that role may update other fields while preserving it.
        var existing = await PersistUserAsync(
            "update.unchanged@plottr02.local", "System Admin User", "Pass123!", role: UserRole.SystemAdmin);
        _fixture.CurrentUser.Role = UserRole.TrialManager;

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var result = await service.UpdateAsync(existing.Id, new UpdateUserRequest
        {
            Name = "Renamed",
            Email = "update.unchanged@plottr02.local",
            Role = UserRole.SystemAdmin, // same value as persisted — not an assignment
        });

        result.Should().NotBeNull();
        result!.Name.Should().Be("Renamed");
        result.Role.Should().Be(UserRole.SystemAdmin);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Persists a user directly via the real repository and returns it.</summary>
    private Task<User> CreatePersistedUser(string email, string name, string password)
        => PersistUserAsync(email, name, password, UserRole.FieldWorker);

    /// <summary>
    /// Persists a user directly via the real repository with an explicit
    /// <paramref name="role"/> (use <c>null</c> to simulate a legacy no-role account),
    /// bypassing the create-time matrix so tests can set up arbitrary persisted state.
    /// </summary>
    private async Task<User> PersistUserAsync(string email, string name, string password, UserRole? role)
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>();

        var entity = UserFaker.Entity(email, password);
        entity.Name = name;
        entity.Role = role;
        return await repo.AddAsync(entity);
    }
}
