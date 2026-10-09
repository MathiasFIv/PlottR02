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
    /// <c>Create</c> is now authenticated-only and governed by the service-level matrix.
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
    [InlineData(nameof(UsersController.GetAll))]
    [InlineData(nameof(UsersController.GetById))]
    [InlineData(nameof(UsersController.Update))]
    [InlineData(nameof(UsersController.Delete))]
    public void AdminActions_RequireSystemAdminOrTrialManager(string methodName)
    {
        MethodAuthorize(methodName).Roles.Should().Be(UsersController.ManageUsersRoles);
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
    public void Create_RequiresAuthentication_NoLongerAnonymous()
    {
        var create = typeof(UsersController).GetMethod(nameof(UsersController.Create))!;

        // The login-screen self-registration path is gone: Create must NOT opt out of
        // authentication, and it declares no role gate of its own — the class-level
        // [Authorize] makes it authenticated-only, and the create/assign matrix is
        // enforced in UserService.
        create.GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().BeNull("Create is authenticated-only now");
        create.GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull("the class-level [Authorize] covers Create; the matrix lives in the service");
    }

    [Fact]
    public void Controller_SitsBehindAuthentication()
    {
        typeof(UsersController)
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull("the whole controller must sit behind the auth pipeline");
    }
}
