using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PlottR0._2.Api;
using PlottR0._2.Api.Controllers;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;

namespace PlottR0._2.Tests;

/// <summary>
/// Config-gated anonymous self-registration on <c>POST /api/Users</c>.
/// Plan A14 forbids <c>WebApplicationFactory</c>, so — exactly like
/// <c>TrialsControllerSheetTests</c> — the controller action is invoked directly with a
/// hand-rolled fake <see cref="IUserService"/> and a synthetic <see cref="ClaimsPrincipal"/>,
/// which makes the guard's observable outcome (401 short-circuit vs. delegate-to-service → 201)
/// the contract under test. Persistence of the created user is asserted against the real
/// Postgres-backed service in <c>UserServiceTests</c>; the flag's DI wiring is covered by
/// <c>Program.cs</c>'s fail-closed <c>AddOptions&lt;AuthOptions&gt;().Configure&lt;IConfiguration&gt;</c>
/// + the metadata tests in <c>UsersControllerAuthorizationTests</c>.
/// </summary>
public sealed class UsersControllerAnonymousSignupTests
{
    private static readonly CreateUserRequest ValidRequest = new()
    {
        Name = "Signup Candidate",
        Email = "signup.candidate@plottr02.local",
        Password = "Sup3rS3cret!",
        Role = UserRole.SystemAdmin, // the role the login-page form hard-codes
    };

    [Fact]
    public async Task Create_Anonymous_WhenFlagDisabled_Returns401_AndNeverReachesService()
    {
        // Flag absent/default OFF — anonymous callers must be rejected exactly as the
        // authenticated-only endpoint behaved before this feature existed.
        var fake = new FakeUserService();
        var controller = AnonymousController(fake, allowAnonymousSignup: false);

        var result = await controller.Create(ValidRequest);

        result.Result.Should().BeOfType<UnauthorizedResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        fake.CreatedRequests.Should().BeEmpty("the guard must short-circuit before the service");
    }

    [Fact]
    public async Task Create_Anonymous_WhenFlagIsMalformed_Returns401_AndNeverReachesService()
    {
        // Issue 1 regression: a malformed non-bool value ("notabool") must fail CLOSED
        // through the SAME parser the composition root binds with — so the guard returns 401
        // instead of the throwing strong binder surfacing a 500. The flag value is produced
        // exactly as Program.cs would, proving there is a single interpretation.
        var malformed = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AuthOptions.AllowAnonymousSignupKey] = "notabool",
            })
            .Build();

        var fake = new FakeUserService();
        var controller = ControllerFor(
            fake,
            allowAnonymousSignup: AuthOptions.ParseAllowAnonymousSignup(malformed),
            authenticated: false,
            role: null);

        var result = await controller.Create(ValidRequest);

        result.Result.Should().BeOfType<UnauthorizedResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        fake.CreatedRequests.Should().BeEmpty("the guard must short-circuit before the service");
    }

    [Fact]
    public async Task Create_Anonymous_WhenFlagEnabled_Returns201_AndForwardsRequestedRole()
    {
        var fake = new FakeUserService();
        var controller = AnonymousController(fake, allowAnonymousSignup: true);

        var result = await controller.Create(ValidRequest);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        created.ActionName.Should().Be(nameof(UsersController.GetById));

        // The controller forwards the caller's payload verbatim; on the anonymous path the
        // SERVICE coerces the persisted role to SystemAdmin (covered in UserServiceTests) —
        // the controller itself must not rewrite the request.
        fake.CreatedRequests.Should().ContainSingle();
        fake.CreatedRequests[0].Role.Should().Be(UserRole.SystemAdmin);
        fake.CreatedRequests[0].Email.Should().Be(ValidRequest.Email);
        created.Value.Should().BeSameAs(fake.ResponseToReturn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_Authenticated_IsUnaffectedByTheFlag_AlwaysDelegatesToService(bool allowAnonymousSignup)
    {
        // The flag only gates ANONYMOUS callers: an authenticated actor keeps its exact
        // pre-flag path (controller forwards; the service-level matrix decides the outcome).
        var fake = new FakeUserService();
        var controller = ControllerFor(
            fake, allowAnonymousSignup, authenticated: true, role: UserRole.TrialManager);

        var result = await controller.Create(ValidRequest);

        result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status201Created);
        fake.CreatedRequests.Should().ContainSingle();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Controller with an anonymous (unauthenticated) principal.</summary>
    private static UsersController AnonymousController(FakeUserService fake, bool allowAnonymousSignup)
        => ControllerFor(fake, allowAnonymousSignup, authenticated: false, role: null);

    private static UsersController ControllerFor(
        FakeUserService fake, bool allowAnonymousSignup, bool authenticated, UserRole? role)
    {
        var controller = new UsersController(
            fake, Options.Create(new AuthOptions { AllowAnonymousSignup = allowAnonymousSignup }));

        // A role claim only exists for authenticated actors; authenticationType is what
        // flips ClaimsIdentity.IsAuthenticated (and thus the controller's guard).
        var identity = authenticated
            ? new ClaimsIdentity(
                authenticationType: "TestAuth",
                nameType: ClaimTypes.Name,
                roleType: JwtBearerConfiguration.RoleClaimName)
            : new ClaimsIdentity();

        if (authenticated && role is not null)
            identity.AddClaim(new Claim(JwtBearerConfiguration.RoleClaimName, role.Value.ToString()));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        return controller;
    }

    /// <summary>
    /// Recording fake: captures every <see cref="CreateUserRequest"/> the controller
    /// hands over so tests can assert the guard either blocked (empty) or forwarded (single).
    /// </summary>
    private sealed class FakeUserService : IUserService
    {
        public List<CreateUserRequest> CreatedRequests { get; } = [];

        public UserResponse ResponseToReturn { get; } = new()
        {
            Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            Name = ValidRequest.Name,
            Email = ValidRequest.Email,
            Role = ValidRequest.Role,
        };

        public Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
        {
            CreatedRequests.Add(request);
            return Task.FromResult(ResponseToReturn);
        }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<UserResponse?> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<UserResponse?> GetResponseAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UserResponse>> GetAllResponsesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<UserResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
