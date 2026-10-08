using System.Text.Json;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;

namespace PlottR0._2.Tests;

/// <summary>
/// Locks the JSON wire contract for <see cref="UserRole"/>: PascalCase member-name
/// strings (never integers), via the converter attribute on the enum type itself.
/// Covers both the MVC/DTO path and the static <c>JsonSerializer.Serialize</c>
/// path used by <c>UserService</c>'s audit payloads.
/// </summary>
public sealed class UserRoleJsonTests
{
    [Fact]
    public void Serialize_UserResponseWithRole_EmitsPascalCaseString_NotInteger()
    {
        var user = new UserResponse
        {
            Id = Guid.NewGuid(),
            Name = "Alice",
            Email = "alice@example.com",
            Role = UserRole.FieldWorker,
        };

        var json = JsonSerializer.Serialize(user);

        json.Should().Contain("\"Role\":\"FieldWorker\"");
        json.Should().NotContain("\"Role\":3");
    }

    [Fact]
    public void Serialize_NullableRolePropertyInAnonymousObject_EmitsString()
    {
        // This is the exact static-path shape UserService serializes for the audit trail:
        // JsonSerializer.Serialize(new { email, name, role = entity.Role }).
        UserRole? role = UserRole.SystemAdmin;

        var json = JsonSerializer.Serialize(new { email = "a@b.c", name = "A", role });

        json.Should().Contain("\"role\":\"SystemAdmin\"");
        json.Should().NotContain("\"role\":0");
    }

    [Fact]
    public void Serialize_NullRole_EmitsNull()
    {
        UserRole? role = null;

        var json = JsonSerializer.Serialize(new { role });

        json.Should().Be("{\"role\":null}");
    }

    [Fact]
    public void Deserialize_KnownString_YieldsEnumMember()
    {
        var role = JsonSerializer.Deserialize<UserRole?>("\"TrialManager\"");

        role.Should().Be(UserRole.TrialManager);
    }

    [Fact]
    public void Deserialize_UnknownRoleString_ThrowsJsonException()
    {
        // Unknown role names must fail binding rather than silently coerce; in MVC
        // with [ApiController] this surfaces as HTTP 400.
        var act = () => JsonSerializer.Deserialize<UserRole?>("\"Agronomist\"");

        act.Should().Throw<JsonException>();
    }
}
