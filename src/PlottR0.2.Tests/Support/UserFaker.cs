using Bogus;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Domain;

namespace PlottR0._2.Tests.Support;

public static class UserFaker
{
    private const int Seed = 12345;

    public static Faker<CreateUserRequest> CreateRequest(bool uniqueEmail = false)
    {
        var faker = new Faker<CreateUserRequest>(locale: "en")
            .RuleFor(r => r.Name, f => f.Name.FullName())
            .RuleFor(r => r.Email, f =>
            {
                var email = f.Internet.Email();
                return uniqueEmail ? $"{email.ToLowerInvariant()}.{f.Random.Int(1000, 9999)}" : email;
            })
            .RuleFor(r => r.Role, f => f.PickRandom(Roles()))
            .RuleFor(r => r.Password, f => f.Internet.Password(12));

        faker.UseSeed(Seed);
        return faker;
    }

    public static Faker<UpdateUserRequest> UpdateRequest()
    {
        var faker = new Faker<UpdateUserRequest>(locale: "en")
            .RuleFor(r => r.Name, f => f.Name.FullName())
            .RuleFor(r => r.Email, f => f.Internet.Email())
            .RuleFor(r => r.Role, f => f.PickRandom(Roles()));

        faker.UseSeed(Seed);
        return faker;
    }

    public static Faker<LoginRequest> LoginRequest()
    {
        var faker = new Faker<LoginRequest>(locale: "en")
            .RuleFor(r => r.Email, f => f.Internet.Email())
            .RuleFor(r => r.Password, f => f.Internet.Password(12));

        faker.UseSeed(Seed);
        return faker;
    }

    private static UserRole[] Roles() =>
    [
        UserRole.SystemAdmin,
        UserRole.Researcher,
        UserRole.TrialManager,
        UserRole.FieldWorker,
    ];

    public static User Entity(string? email = null, string? password = null)
    {
        Randomizer.Seed = new Random(Seed);
        var faker = new Faker(locale: "en");

        return new User
        {
            Id = Guid.NewGuid(),
            Name = faker.Name.FullName(),
            Email = email ?? faker.Internet.Email(),
            Role = faker.PickRandom(Roles()),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password ?? "ValidPass123!", workFactor: 12),
        };
    }
}
