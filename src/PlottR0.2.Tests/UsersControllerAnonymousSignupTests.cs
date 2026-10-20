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

public sealed class UsersControllerAnonymousSignupTests
{
    private static readonly CreateUserRequest ValidRequest = new()
    {
        Name = "Signup Candidate",
        Email = "signup.candidate@plottr02.local",
        Password = "Sup3rS3cret!",
        Role = UserRole.SystemAdmin,
    };

    [Fact]
    public async Task Create_Anonymous_WhenFlagDisabled_Returns401_AndNeverReachesService()
    {
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
        var fake = new FakeUserService();
        var controller = ControllerFor(
            fake, allowAnonymousSignup, authenticated: true, role: UserRole.TrialManager);

        var result = await controller.Create(ValidRequest);

        result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status201Created);
        fake.CreatedRequests.Should().ContainSingle();
    }

    private static UsersController AnonymousController(FakeUserService fake, bool allowAnonymousSignup)
        => ControllerFor(fake, allowAnonymousSignup, authenticated: false, role: null);

    private static UsersController ControllerFor(
        FakeUserService fake, bool allowAnonymousSignup, bool authenticated, UserRole? role)
    {
        var controller = new UsersController(
            fake, Options.Create(new AuthOptions { AllowAnonymousSignup = allowAnonymousSignup }));

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
