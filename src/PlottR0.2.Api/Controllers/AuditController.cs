using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Api.Controllers;

/// <summary>
/// Read-only audit log endpoint. Thin controller — delegates to
/// <see cref="IAuditService"/> for data retrieval.
/// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class AuditController : ControllerBase
{
    private readonly IAuditService _auditService;

    /// <summary>Creates a new <see cref="AuditController"/>.</summary>
    /// <param name="auditService">The audit service contract to operate against.</param>
    public AuditController(IAuditService auditService) => _auditService = auditService;

    /// <summary>Returns the most recent audit log entries, newest first.</summary>
    /// <param name="count">Max entries to return (default 100).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of recent audit log entries.</returns>
    [HttpGet]
    public async Task<IActionResult> GetRecent([FromQuery] int count = 100, CancellationToken ct = default)
        => Ok(await _auditService.GetRecentAsync(count, ct));
}
