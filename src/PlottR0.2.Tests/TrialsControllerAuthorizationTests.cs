using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using PlottR0._2.Api.Controllers;

namespace PlottR0._2.Tests;

/// <summary>
/// Authorization contract for <see cref="TrialsController"/>. As with
/// <c>UsersControllerAuthorizationTests</c>, plan A14 forbids
/// <c>WebApplicationFactory</c>, so the declared attributes are the observable
/// contract. Creation and the crew-management actions (<c>AssignUsers</c>,
/// <c>ListUsers</c>) are gated to the three privileged roles via
/// <see cref="TrialsController.CreateTrialRoles"/>. The workbook and sheet reads carry no
/// method-level role gate: access to them is scoped per-trial in <c>TrialService</c>
/// (managers see every trial, everyone else only the trials they are assigned to), on top
/// of the class-level <c>[Authorize]</c> that requires an authenticated caller.
/// </summary>
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
        // PUT {trialId}/users — replaces the crew; same manager gate as Create.
        MethodAuthorize(nameof(TrialsController.AssignUsers)).Roles
            .Should().Be(TrialsController.CreateTrialRoles);
    }

    [Fact]
    public void ListUsers_RequiresAPrivilegedRole()
    {
        // GET {trialId}/users — reads the crew; same manager gate as Create.
        MethodAuthorize(nameof(TrialsController.ListUsers)).Roles
            .Should().Be(TrialsController.CreateTrialRoles);
    }

    [Theory]
    [InlineData(nameof(TrialsController.List))]
    [InlineData(nameof(TrialsController.DownloadWorkbook))]
    [InlineData(nameof(TrialsController.GetSheet))]
    public void ReadActions_HaveNoMethodLevelRoleGate(string methodName)
    {
        // List, workbook download and sheet reads are access-scoped per-trial inside
        // TrialService rather than role-gated: managers see every trial, everyone else
        // only the trials they are assigned to. Only the class-level [Authorize]
        // (any authenticated user) applies here.
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
