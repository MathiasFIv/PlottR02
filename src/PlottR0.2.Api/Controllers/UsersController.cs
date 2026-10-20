using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;

namespace PlottR0._2.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    public const string ManageUsersRoles = "SystemAdmin,TrialManager";

    private readonly IUserService _userService;
    private readonly IOptions<AuthOptions> _authOptions;

    public UsersController(IUserService userService, IOptions<AuthOptions> authOptions)
    {
        _userService = userService;
        _authOptions = authOptions;
    }

    [HttpGet]
    [Authorize(Roles = ManageUsersRoles + ",Researcher")]
    public async Task<ActionResult<IReadOnlyList<UserResponse>>> GetAll(CancellationToken ct = default)
    {
        var users = await _userService.GetAllResponsesAsync(ct);
        return Ok(users);
    }

    [HttpGet("me")]
    public async Task<ActionResult<UserResponse>> GetMe(CancellationToken ct = default)
    {
        var sub = User.FindFirstValue("sub");
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return Unauthorized();

        var user = await _userService.GetResponseAsync(userId, ct);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = ManageUsersRoles)]
    public async Task<ActionResult<UserResponse>> GetById(Guid id, CancellationToken ct = default)
    {
        var user = await _userService.GetResponseAsync(id, ct);
        if (user is null)
        {
            return NotFound();
        }

        return Ok(user);
    }

    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> Create([FromBody] CreateUserRequest request, CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated != true && !_authOptions.Value.AllowAnonymousSignup)
        {
            return Unauthorized();
        }

        try
        {
            var created = await _userService.CreateAsync(request, ct);

            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (ForbiddenAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = ManageUsersRoles)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserResponse>> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct = default)
    {
        try
        {
            var updated = await _userService.UpdateAsync(id, request, ct);
            if (updated is null)
            {
                return NotFound();
            }

            return Ok(updated);
        }
        catch (ForbiddenAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = ManageUsersRoles)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        await _userService.DeleteAsync(id, ct);

        return NoContent();
    }
}
