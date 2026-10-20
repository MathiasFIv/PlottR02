using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using PlottR0._2.Api.Controllers;

namespace PlottR0._2.Tests;

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
        MethodAuthorize(nameof(UsersController.GetAll)).Roles
            .Should().Be(UsersController.ManageUsersRoles + ",Researcher");
    }

    [Fact]
    public void GetMe_IsNotRoleRestricted_SoEveryRoleCanReadItsOwnProfile()
    {
        typeof(UsersController)
            .GetMethod(nameof(UsersController.GetMe))!
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull("GetMe relies on the class-level [Authorize] (any authenticated user)");
    }

    [Fact]
    public void Create_CarriesAllowAnonymous_ButStaysConfigGated()
    {
        var create = typeof(UsersController).GetMethod(nameof(UsersController.Create))!;

        create.GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().NotBeNull("Create is the single config-gated anonymous signup entry point");
        create.GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull("the class-level [Authorize] still covers Create; the matrix lives in the service");
    }

    [Fact]
    public void OnlyCreate_CarriesAllowAnonymous_OtherActionsStayAuthenticated()
    {
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
