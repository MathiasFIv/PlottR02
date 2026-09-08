using Bogus;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;

namespace PlottR0._2.Tests.Support;

/// <summary>
/// Bogus factories for <see cref="User"/>-related data. Seeded so that a given
/// test is deterministic across runs (same seed → same generated values), which is
/// the whole point of using Bogus for seedable test data.
///
/// Because a test collection shares one Postgres container, generated emails must be
/// unique per use — <see cref="CreateRequest"/> perturbs via <c>UniqueSuffix</c> when asked.
/// </summary>
public static class UserFaker
{
    /// <summary>A fixed seed gives reproducible output each run.</summary>
    private const int Seed = 12345;

    /// <summary>
    /// Generator for a valid <see cref="CreateUserRequest"/>. Pass
    /// <paramref name="uniqueEmail"/> = <c>true</c> to force a unique email per call
    /// (via random suffix) so tests never collide on the DB unique email constraint.
    /// </summary>
    public static Faker<CreateUserRequest> CreateRequest(bool uniqueEmail = false)
    {
        var faker = new Faker<CreateUserRequest>(locale: "en")
            .RuleFor(r => r.Name, f => f.Name.FullName())
            .RuleFor(r => r.Email, f =>
            {
                var email = f.Internet.Email();
                return uniqueEmail ? $"{email.ToLowerInvariant()}.{f.Random.Int(1000, 9999)}" : email;
            })
            .RuleFor(r => r.Role, f => f.PickRandom(new string?[] { "Administrator", "Agronomist", "Field Technician", null }))
            .RuleFor(r => r.Password, f => f.Internet.Password(12));

        // Seeds must be applied to the Faker<T> itself (via UseSeed) to be reproducible.
        faker.UseSeed(Seed);
        return faker;
    }

    /// <summary>Generator for a valid <see cref="UpdateUserRequest"/>.</summary>
    public static Faker<UpdateUserRequest> UpdateRequest()
    {
        var faker = new Faker<UpdateUserRequest>(locale: "en")
            .RuleFor(r => r.Name, f => f.Name.FullName())
            .RuleFor(r => r.Email, f => f.Internet.Email())
            .RuleFor(r => r.Role, f => f.PickRandom(new string?[] { "Administrator", "Agronomist", "Field Technician", null }));

        faker.UseSeed(Seed);
        return faker;
    }

    /// <summary>Generator for a valid <see cref="LoginRequest"/>.</summary>
    public static Faker<LoginRequest> LoginRequest()
    {
        var faker = new Faker<LoginRequest>(locale: "en")
            .RuleFor(r => r.Email, f => f.Internet.Email())
            .RuleFor(r => r.Password, f => f.Internet.Password(12));

        faker.UseSeed(Seed);
        return faker;
    }

    /// <summary>
    /// Builds a domain <see cref="User"/> with a known <see cref="BCrypt.Net.BCrypt"/>-hashed
    /// password, ready to be persisted. <paramref name="email"/> and <paramref name="password"/>
    /// let a test control the credentials (so Login can be verified deterministically).
    /// </summary>
    public static User Entity(string? email = null, string? password = null)
    {
        // The non-generic Faker is seeded via the static Randomizer.Seed.
        // (The generic Faker<T> uses UseSeed — see CreateRequest above.)
        Randomizer.Seed = new Random(Seed);
        var faker = new Faker(locale: "en");

        return new User
        {
            Id = Guid.NewGuid(),
            Name = faker.Name.FullName(),
            Email = email ?? faker.Internet.Email(),
            Role = faker.PickRandom(new string?[] { "Administrator", "Agronomist", "Field Technician", null }),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password ?? "ValidPass123!", workFactor: 12),
        };
    }
}
