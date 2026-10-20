using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using PlottR0._2.Api.Controllers;

namespace PlottR0._2.Tests;

public sealed class TrialsControllerAuthorizationTests
{
    private static AuthorizeAttribute MethodAuthorize(string methodName)
    {
        var attr = typeof(TrialsController)
            .GetMethod(methodName)!
            .GetCustomAttribute<AuthorizeAttribute>();

        attr.Should().NotBeNull($"`{methodName}` must declare [Authorize] metadata");
        return attr!;
    }

    [Fact]
    public void CreateTrialRoles_IsSystemAdminResearcherTrialManager()
    {
        TrialsController.CreateTrialRoles.Should().Be("SystemAdmin,Researcher,TrialManager");
    }

    [Fact]
    public void Create_RequiresAPrivilegedRole()
    {
        MethodAuthorize(nameof(TrialsController.Create)).Roles
            .Should().Be(TrialsController.CreateTrialRoles);
    }

    [Fact]
    public void AssignUsers_RequiresAPrivilegedRole()
    {
        MethodAuthorize(nameof(TrialsController.AssignUsers)).Roles
            .Should().Be(TrialsController.CreateTrialRoles);
    }

    [Fact]
    public void ListUsers_RequiresAPrivilegedRole()
    {
        MethodAuthorize(nameof(TrialsController.ListUsers)).Roles
            .Should().Be(TrialsController.CreateTrialRoles);
    }

    [Theory]
    [InlineData(nameof(TrialsController.List))]
    [InlineData(nameof(TrialsController.DownloadWorkbook))]
    [InlineData(nameof(TrialsController.GetSheet))]
    public void ReadActions_HaveNoMethodLevelRoleGate(string methodName)
    {
        typeof(TrialsController)
            .GetMethod(methodName)!
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull("access is enforced per-trial in TrialService, not by role metadata");
    }

    [Fact]
    public void Controller_SitsBehindAuthentication()
    {
        typeof(TrialsController)
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull("the whole controller must sit behind the auth pipeline");
    }
}
