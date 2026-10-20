using Microsoft.Extensions.DependencyInjection;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Tests.Support;

namespace PlottR0._2.Tests;

[Collection(PostgresCollection.Name)]
public sealed class UserServiceTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public UserServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _fixture.CurrentUser.ResetToDefaults();
        _fixture.SetAllowAnonymousSignup(false);
        return _fixture.ResetDatabaseAsync();
    }

    private IUserService Service(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IUserService>();

    private static readonly UserRole[] AllRoles =
    [
        UserRole.SystemAdmin,
        UserRole.Researcher,
        UserRole.TrialManager,
        UserRole.FieldWorker,
    ];

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

        var persisted = await scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>()
            .GetByEmailAsync(request.Email);
        persisted.Should().NotBeNull();
        persisted!.PasswordHash.Should().StartWith("$2")
            .And.NotBe(request.Password);
    }

    [Fact]
    public async Task CreateAsync_DuplicateEmail_ThrowsInvalidOperation()
    {
        var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        await service.CreateAsync(request);

        var duplicate = new CreateUserRequest
        {
            Name = request.Name,
            Email = request.Email,
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

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.Researcher)]
    [InlineData(UserRole.TrialManager)]
    [InlineData(UserRole.FieldWorker)]
    public async Task CreateAsync_AnonymousActor_WithFlagEnabled_CoercesRequestedRoleToSystemAdmin(UserRole requestedRole)
    {
        _fixture.CurrentUser.UserId = null;
        _fixture.CurrentUser.Name = null;
        _fixture.CurrentUser.Role = null;
        _fixture.SetAllowAnonymousSignup(true);

        var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
        request.Role = requestedRole;

        using var scope = _fixture.CreateScope();
        var created = await Service(scope).CreateAsync(request);

        created.Role.Should().Be(UserRole.SystemAdmin);

        var repo = scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>();
        var persisted = await repo.GetByEmailAsync(request.Email);
        persisted.Should().NotBeNull();
        persisted!.Role.Should().Be(UserRole.SystemAdmin);
        persisted.PasswordHash.Should().StartWith("$2").And.NotBe(request.Password);

        persisted.CreatedBy.Should().BeNull();

        var login = await Service(scope).LoginAsync(new LoginRequest
        {
            Email = request.Email,
            Password = request.Password,
        });
        login.Email.Should().Be(request.Email);
        login.Role.Should().Be(UserRole.SystemAdmin);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("notabool")]
    [InlineData(null)]
    public async Task CreateAsync_AnonymousActor_WithFlagOffOrMalformed_ThrowsForbidden(string? rawFlag)
    {
        _fixture.CurrentUser.UserId = null;
        _fixture.CurrentUser.Name = null;
        _fixture.CurrentUser.Role = null;
        _fixture.SetAllowAnonymousSignupRaw(rawFlag);

        foreach (var requestedRole in AllRoles)
        {
            var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
            request.Role = requestedRole;

            using var scope = _fixture.CreateScope();
            var act = () => Service(scope).CreateAsync(request);

            await act.Should().ThrowAsync<ForbiddenAccessException>()
                .WithMessage("*not allowed to create users*");
        }
    }

    [Fact]
    public async Task CreateAsync_MatrixUnchanged_ForAuthenticatedActor_WhenFlagEnabled()
    {
        _fixture.SetAllowAnonymousSignup(true);
        _fixture.CurrentUser.Role = UserRole.TrialManager;

        foreach (var requestedRole in AllRoles)
        {
            var shouldAllow = requestedRole == UserRole.FieldWorker;

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
    }

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

        var act = () => service.LoginAsync(new LoginRequest
        {
            Email = "nobody@plottr02.local",
            Password = "Whatever123!",
        });

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Invalid email or password.*");
    }

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
        result!.Id.Should().Be(created.Id);
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

    [Fact]
    public async Task DeleteAsync_ExistingUser_RemovesUser()
    {
        const string email = "delete.valid@plottr02.local";
        var created = await CreatePersistedUser(email, "Delete Me", "DeletePass123!");

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        await service.DeleteAsync(created.Id);

        var repo = scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>();
        (await repo.GetByEmailAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentId_IsIdempotentNoOp()
    {
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var act = () => service.DeleteAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_IsolatedFromActorIdentity_StillDeletes()
    {
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

        users.Should().BeAssignableTo<IReadOnlyList<User>>();
    }

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
        _ = created;

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var dtos = await service.GetAllResponsesAsync();

        var dtoPropertyNames = typeof(UserResponse).GetProperties().Select(p => p.Name).ToArray();
        dtoPropertyNames.Should().BeEquivalentTo(
            [nameof(UserResponse.Id), nameof(UserResponse.Name), nameof(UserResponse.Email), nameof(UserResponse.Role)]);
    }

    [Fact]
    public async Task CreateAndUpdateAsync_Role_RoundTripsThroughDatabaseAsEnum()
    {
        var request = UserFaker.CreateRequest(uniqueEmail: true).Generate();
        request.Role = UserRole.TrialManager;
        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var created = await service.CreateAsync(request);

        created.Role.Should().Be(UserRole.TrialManager);

        var repo = scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>();
        var persisted = await repo.GetByEmailAsync(request.Email);
        persisted!.Role.Should().Be(UserRole.TrialManager);

        var updated = await service.UpdateAsync(created.Id, new UpdateUserRequest
        {
            Name = created.Name,
            Email = created.Email,
            Role = UserRole.FieldWorker,
        });

        updated!.Role.Should().Be(UserRole.FieldWorker);
    }

    [Fact]
    public async Task UpdateAsync_RoleChange_ByTrialManager_AllowedOnlyForFieldWorker()
    {
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

        var repo = scope.ServiceProvider
            .GetRequiredService<PlottR0._2.Contracts.Repositories.IUserRepository>();
        var persisted = await repo.GetByIdAsync(created.Id);
        persisted!.Role.Should().Be(UserRole.FieldWorker);
    }

    [Fact]
    public async Task UpdateAsync_NullRole_OnNoRoleUser_IsAllowed()
    {
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
        var existing = await PersistUserAsync(
            "update.unchanged@plottr02.local", "System Admin User", "Pass123!", role: UserRole.SystemAdmin);
        _fixture.CurrentUser.Role = UserRole.TrialManager;

        using var scope = _fixture.CreateScope();
        var service = Service(scope);

        var result = await service.UpdateAsync(existing.Id, new UpdateUserRequest
        {
            Name = "Renamed",
            Email = "update.unchanged@plottr02.local",
            Role = UserRole.SystemAdmin,
        });

        result.Should().NotBeNull();
        result!.Name.Should().Be("Renamed");
        result.Role.Should().Be(UserRole.SystemAdmin);
    }

    private Task<User> CreatePersistedUser(string email, string name, string password)
        => PersistUserAsync(email, name, password, UserRole.FieldWorker);

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
