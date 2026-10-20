using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PlottR0._2.Api;
using PlottR0._2.Api.Controllers;
using PlottR0._2.Domain;

namespace PlottR0._2.Tests;

public sealed class AuditControllerAuthorizationTests
{
    private static AuthorizeAttribute ControllerAuthorize()
        => typeof(AuditController).GetCustomAttribute<AuthorizeAttribute>()!;

    [Fact]
    public void ReadAuditRoles_IsTheManagerSet_MatchingCreateTrialRoles()
    {
        AuditController.ReadAuditRoles.Should().Be("SystemAdmin,Researcher,TrialManager");
        AuditController.ReadAuditRoles.Should().Be(TrialsController.CreateTrialRoles,
            "audit reads use the same manager set as trial creation");
    }

    [Fact]
    public void Controller_SitsBehindAuthentication()
    {
        typeof(AuditController)
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull("the whole controller must sit behind the auth pipeline");
    }

    [Fact]
    public void Controller_CarriesTheManagerRoleGate()
    {
        typeof(AuditController)
            .GetMethod(nameof(AuditController.GetRecent))!
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull("the gate lives on the controller for this single action");

        ControllerAuthorize().Roles.Should().Be(AuditController.ReadAuditRoles);
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin, true)]
    [InlineData(UserRole.Researcher, true)]
    [InlineData(UserRole.TrialManager, true)]
    [InlineData(UserRole.FieldWorker, false)]
    [InlineData(null, false)]
    public void RoleGate_PermitsManagersOnly(UserRole? role, bool permitted)
    {
        var declaredRoles = ControllerAuthorize().Roles!.Split(',');
        var principal = PrincipalFor(role);

        declaredRoles.Any(principal.IsInRole).Should().Be(permitted,
            permitted
                ? $"role '{role}' must be permitted to read the audit log"
                : $"role '{role}' must be denied the audit log");
    }

    private static ClaimsPrincipal PrincipalFor(UserRole? role)
    {
        var identity = new ClaimsIdentity(
            authenticationType: "TestAuth",
            nameType: ClaimTypes.Name,
            roleType: JwtBearerConfiguration.RoleClaimName);

        if (role is not null)
            identity.AddClaim(new Claim(JwtBearerConfiguration.RoleClaimName, role.Value.ToString()));

        return new ClaimsPrincipal(identity);
    }
}
