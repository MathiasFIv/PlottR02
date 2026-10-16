using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Configuration;


namespace PlottR0._2.Api.Controllers;

    /// <summary>
    /// REST CRUD endpoints for the <see cref="PlottR0._2.Domain.User"/> resource.
    /// Thin controller — all business logic is delegated to <see cref="IUserService"/>.
    /// Reading, updating and deleting users is restricted to system admins and
    /// trial managers (see <see cref="ManageUsersRoles"/>); listing additionally admits
    /// researchers so the trial-assignment picker can populate its candidate list.
    /// <c>GetMe</c> stays open to any authenticated user. <c>Create</c> carries
    /// <c>[AllowAnonymous]</c> so the login-page self-signup path can reach it, but that
    /// path is gated by <see cref="AuthOptions.AllowAnonymousSignup"/> (default OFF):
    /// with the flag off an anonymous caller is rejected with 401 right here in the
    /// action guard, and only authenticated actors — bound by the account-creation
    /// matrix enforced in <see cref="IUserService"/> — may create users.
    /// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    /// <summary>
    /// Roles permitted to list, read, update and delete users. Referenced by the
    /// <see cref="AuthorizeAttribute.Roles"/> gates on those actions and by the
    /// authorization metadata tests.
    /// </summary>
    public const string ManageUsersRoles = "SystemAdmin,TrialManager";

    private readonly IUserService _userService;
    private readonly IOptions<AuthOptions> _authOptions;

    /// <summary>
    /// Creates a new <see cref="UsersController"/>.
    /// </summary>
    /// <param name="userService">The user service contract to operate against.</param>
    /// <param name="authOptions">The bound <c>Auth</c> options carrying the anonymous-signup switch.</param>
    public UsersController(IUserService userService, IOptions<AuthOptions> authOptions)
    {
        _userService = userService;
        _authOptions = authOptions;
    }

    /// <summary>
    /// Gets all users. Also open to researchers (beyond <see cref="ManageUsersRoles"/>)
    /// so the trial-assignment picker can list candidate users; every other action keeps
    /// the narrower manager-only gate.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of all users as DTOs.</returns>
    [HttpGet]
    [Authorize(Roles = ManageUsersRoles + ",Researcher")]
    public async Task<ActionResult<IReadOnlyList<UserResponse>>> GetAll(CancellationToken ct = default)
    {
        var users = await _userService.GetAllResponsesAsync(ct);
        return Ok(users);
    }

    /// <summary>
    /// Gets the currently authenticated user's profile by reading the
    /// <c>sub</c> claim from the JWT access token.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching user DTO, or 401 if the token is missing/invalid, or 404 if the user no longer exists.</returns>
    [HttpGet("me")]
    public async Task<ActionResult<UserResponse>> GetMe(CancellationToken ct = default)
    {
        var sub = User.FindFirstValue("sub");
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return Unauthorized();

        var user = await _userService.GetResponseAsync(userId, ct);
        return user is null ? NotFound() : Ok(user);
    }

    /// <summary>
    /// Gets a single user by its id.
    /// </summary>
    /// <param name="id">The user's identity key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching user DTO, or 404 if none exists.</returns>
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

    /// <summary>
    /// Creates a new user. <c>[AllowAnonymous]</c> opts this single action out of the
    /// class-level <c>[Authorize]</c> so the login-page self-signup panel can call it —
    /// but anonymous access is config-gated by <see cref="AuthOptions.AllowAnonymousSignup"/>
    /// (default OFF). With the flag off, an anonymous caller is rejected with 401 by the
    /// guard below and never reaches the service, i.e. behaviour is identical to the
    /// authenticated-only endpoint that existed before the flag. Authenticated callers are
    /// unaffected by the flag: the caller must still supply a role inside their row of the
    /// account-creation/assignment matrix (enforced in <see cref="IUserService"/>, not here).
    /// </summary>
    /// <param name="request">The create payload; <c>Role</c> is required.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>201 with the created user and a Location header to fetch it, 401 when an
    /// anonymous caller hits a disabled signup flag, 400 for a missing/invalid role or
    /// payload, 403 when the caller may not assign the role, or 409 when the email already
    /// belongs to another account.</returns>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> Create([FromBody] CreateUserRequest request, CancellationToken ct = default)
    {
        // Config gate for anonymous self-registration. Authenticated callers skip this
        // entirely; anonymous callers only proceed when the feature is explicitly on,
        // so the default (flag absent/false) keeps this endpoint authenticated-only.
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
            // Duplicate email — the only InvalidOperationException CreateAsync throws.
            return Conflict(ex.Message);
        }
    }

    /// <summary>
    /// Updates an existing user by its id.
    /// </summary>
    /// <param name="id">The user's identity key.</param>
    /// <param name="request">The update payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the updated user DTO, or 404 if none exists.</returns>
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

    /// <summary>
    /// Deletes a user by its id. Deleting a non-existent user is an idempotent no-op.
    /// </summary>
    /// <param name="id">The user's identity key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>204 No Content.</returns>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = ManageUsersRoles)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        await _userService.DeleteAsync(id, ct);

        return NoContent();
    }
}
