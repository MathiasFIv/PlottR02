using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using PlottR0._2.Api.Controllers;

namespace PlottR0._2.Tests;

    /// <summary>
    /// Authorization contract for <see cref="UsersController"/>. Plan A14 forbids
    /// <c>WebApplicationFactory</c>, so — exactly like <c>TrialsControllerSheetTests</c> —
    /// the attributes are the observable contract asserted here rather than a pipeline 403.
    /// The behaviour behind <c>[Authorize(Roles = ...)]</c> is covered separately by
    /// <c>JwtBearerConfigurationTests</c> (mint → validate → <c>IsInRole</c>).
    /// <c>Create</c> carries <c>[AllowAnonymous]</c> for the config-gated self-signup path
    /// (<c>Auth:AllowAnonymousSignup</c>, default OFF — see
    /// <c>UsersControllerAnonymousSignupTests</c> for the 401/201 behaviour); every other
    /// action stays authenticated-only and the service-level matrix still governs who may
    /// grant which role.
    /// </summary>
public sealed class UsersControllerAuthorizationTests
{
    private static AuthorizeAttribute MethodAuthorize(string methodName)
    {
        var attr = typeof(UsersController)
            .GetMethod(methodName)!
            .GetCustomAttribute<AuthorizeAttribute>();

        attr.Should().NotBeNull($"`{methodName}` must declare [Authorize] metadata");
        return attr!;
    }

    [Fact]
    public void ManageUsersRoles_IsSystemAdminAndTrialManager()
    {
        UsersController.ManageUsersRoles.Should().Be("SystemAdmin,TrialManager");
    }

    [Theory]
    [InlineData(nameof(UsersController.GetById))]
    [InlineData(nameof(UsersController.Update))]
    [InlineData(nameof(UsersController.Delete))]
    public void AdminActions_RequireSystemAdminOrTrialManager(string methodName)
    {
        MethodAuthorize(methodName).Roles.Should().Be(UsersController.ManageUsersRoles);
    }

    [Fact]
    public void ListAction_AlsoAllowsResearchers_SoTheAssignmentPickerCanPopulate()
    {
        // The trial-assignment modal lists candidate users, so the list gate is widened
        // to researchers; every other user action keeps the narrower manager-only gate.
        MethodAuthorize(nameof(UsersController.GetAll)).Roles
            .Should().Be(UsersController.ManageUsersRoles + ",Researcher");
    }

    [Fact]
    public void GetMe_IsNotRoleRestricted_SoEveryRoleCanReadItsOwnProfile()
    {
        // GetMe has no method-level [Authorize]; only the class-level [Authorize]
        // (any authenticated user) applies — a researcher must reach their own profile.
        typeof(UsersController)
            .GetMethod(nameof(UsersController.GetMe))!
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull("GetMe relies on the class-level [Authorize] (any authenticated user)");
    }

    [Fact]
    public void Create_CarriesAllowAnonymous_ButStaysConfigGated()
    {
        var create = typeof(UsersController).GetMethod(nameof(UsersController.Create))!;

        // The self-signup path opts Create out of authentication metadata, but only the
        // Create action — and the flag in AuthOptions.AllowAnonymousSignup (default OFF)
        // decides whether an anonymous caller actually gets through (401 otherwise).
        create.GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().NotBeNull("Create is the single config-gated anonymous signup entry point");
        create.GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull("the class-level [Authorize] still covers Create; the matrix lives in the service");
    }

    [Fact]
    public void OnlyCreate_CarriesAllowAnonymous_OtherActionsStayAuthenticated()
    {
        // The class-level [Authorize] must keep protecting every other action: only
        // Create may opt out, and only because its service-layer/config gate backs it up.
        var actionNames = new[]
        {
            nameof(UsersController.GetAll),
            nameof(UsersController.GetMe),
            nameof(UsersController.GetById),
            nameof(UsersController.Update),
            nameof(UsersController.Delete),
        };

        foreach (var methodName in actionNames)
        {
            typeof(UsersController)
                .GetMethod(methodName)!
                .GetCustomAttribute<AllowAnonymousAttribute>()
                .Should().BeNull($"`{methodName}` must never become anonymous-reachable");
        }
    }

    [Fact]
    public void Controller_SitsBehindAuthentication()
    {
        typeof(UsersController)
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull("the whole controller must sit behind the auth pipeline");
    }
}
