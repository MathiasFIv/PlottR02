using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PlottR0._2.Api;
using PlottR0._2.Api.Controllers;
using PlottR0._2.Domain;

namespace PlottR0._2.Tests;

/// <summary>
/// Authorization contract for <see cref="AuditController"/>. Plan A14 forbids
/// <c>WebApplicationFactory</c>, so — as with <c>UsersControllerAuthorizationTests</c> —
/// the declared <see cref="AuthorizeAttribute"/> metadata is the observable contract. The
/// role gate is additionally evaluated against a real <see cref="ClaimsPrincipal"/> (with
/// the same literal <c>"role"</c> claim type the JWT handler uses) to pin permit/deny for
/// managers, a field worker and a role-less actor — the audit log carries trial metadata
/// for trials a non-manager may not otherwise see.
/// </summary>
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
        // The single read-only action inherits the controller-level role gate.
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
        // Mirror the RolesAuthorizationRequirement: permitted when the caller holds any
        // one of the declared roles. A null/permitted=false pair exercises the role-less actor.
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
