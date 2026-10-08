using System.Text.Json.Serialization;

namespace PlottR0._2.Domain;

/// <summary>
/// The four application roles a <see cref="User"/> may hold. The role is optional,
/// so the property type on entities/DTOs is <c>UserRole?</c> — <c>null</c> means
/// "no role assigned" and no <c>role</c> claim is emitted for such a user.
///
/// The <see cref="JsonStringEnumConverter"/> attribute fixes the JSON wire format
/// to PascalCase member names (<c>"SystemAdmin"</c>, <c>"FieldWorker"</c>) rather
/// than the System.Text.Json default of integers, on every serialization path —
/// MVC options, static <c>JsonSerializer.Serialize</c> calls, and direct
/// <c>JsonSerializer</c> usage alike. An unrecognised string on an incoming
/// request fails model binding and surfaces as HTTP 400.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserRole
{
    /// <summary>Full administrative access, including user management.</summary>
    SystemAdmin = 0,

    /// <summary>Designs and analyses trials.</summary>
    Researcher = 1,

    /// <summary>Manages individual trials.</summary>
    TrialManager = 2,

    /// <summary>Collects field data (recordings, ratings).</summary>
    FieldWorker = 3,
}
