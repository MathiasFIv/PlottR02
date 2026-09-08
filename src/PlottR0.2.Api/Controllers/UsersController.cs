using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Services.Abstractions;


namespace PlottR0._2.Api.Controllers;

/// <summary>
/// REST CRUD endpoints for the <see cref="PlottR0._2.Domain.User"/> resource.
/// Thin controller — all business logic is delegated to <see cref="IUserService"/>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;

    /// <summary>
    /// Creates a new <see cref="UsersController"/>.
    /// </summary>
    /// <param name="userService">The user service contract to operate against.</param>
    public UsersController(IUserService userService)
    {
        _userService = userService;
    }

    /// <summary>
    /// Gets all users.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of all users as DTOs.</returns>
    [HttpGet]
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
    /// Creates a new user.
    /// </summary>
    /// <param name="request">The create payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>201 with the created user and a Location header to fetch it.</returns>
    [HttpPost]
    [AllowAnonymous]
    public async Task<ActionResult<UserResponse>> Create([FromBody] CreateUserRequest request, CancellationToken ct = default)
    {
        var created = await _userService.CreateAsync(request, ct);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Updates an existing user by its id.
    /// </summary>
    /// <param name="id">The user's identity key.</param>
    /// <param name="request">The update payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>200 with the updated user DTO, or 404 if none exists.</returns>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserResponse>> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct = default)
    {
        var updated = await _userService.UpdateAsync(id, request, ct);
        if (updated is null)
        {
            return NotFound();
        }

        return Ok(updated);
    }

    /// <summary>
    /// Deletes a user by its id. Deleting a non-existent user is an idempotent no-op.
    /// </summary>
    /// <param name="id">The user's identity key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>204 No Content.</returns>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        await _userService.DeleteAsync(id, ct);

        return NoContent();
    }
}
