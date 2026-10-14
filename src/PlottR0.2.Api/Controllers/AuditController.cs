using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Api.Controllers;

/// <summary>
/// Read-only audit log endpoint. Thin controller — delegates to
/// <see cref="IAuditService"/> for data retrieval.
///
/// Reads are restricted to managers (system administrators, researchers and trial
/// managers) — the same role set as <c>TrialsController.CreateTrialRoles</c>. The audit
/// log records trial metadata (trial id, workbook name, source path) for trials the
/// caller may not otherwise see, so an authenticated non-manager (e.g. a field worker)
/// must not be able to enumerate it.
/// </summary>
[ApiController]
[Authorize(Roles = ReadAuditRoles)]
[Route("api/[controller]")]
public class AuditController : ControllerBase
{
    /// <summary>
    /// Roles permitted to read the audit log — system administrators, researchers and
    /// trial managers. Mirrors <c>TrialsController.CreateTrialRoles</c>; defined locally
    /// so this controller does not depend on another controller for its own gate.
    /// </summary>
    public const string ReadAuditRoles = "SystemAdmin,Researcher,TrialManager";

    private readonly IAuditService _auditService;

    /// <summary>Creates a new <see cref="AuditController"/>.</summary>
    /// <param name="auditService">The audit service contract to operate against.</param>
    public AuditController(IAuditService auditService) => _auditService = auditService;

    /// <summary>Returns the most recent audit log entries, newest first. Restricted to
    /// system administrators, researchers and trial managers.</summary>
    /// <param name="count">Max entries to return (default 100).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of recent audit log entries; 403 when the caller's role may not
    /// read the audit log.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PlottR0._2.Domain.AuditLog>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRecent([FromQuery] int count = 100, CancellationToken ct = default)
        => Ok(await _auditService.GetRecentAsync(count, ct));
}
