using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Api.Controllers;

[ApiController]
[Authorize(Roles = ReadAuditRoles)]
[Route("api/[controller]")]
public class AuditController : ControllerBase
{
    public const string ReadAuditRoles = "SystemAdmin,Researcher,TrialManager";

    private readonly IAuditService _auditService;

    public AuditController(IAuditService auditService) => _auditService = auditService;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PlottR0._2.Domain.AuditLog>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRecent([FromQuery] int count = 100, CancellationToken ct = default)
        => Ok(await _auditService.GetRecentAsync(count, ct));
}
