using Bogus;

namespace PlottR0._2.Tests.Support;

/// <summary>
/// Bogus factories for trial-related test data. Seeded so that a given test
/// is deterministic across runs (same seed → same generated values).
/// </summary>
public static class TrialFaker
{
    /// <summary>A fixed seed gives reproducible output each run.</summary>
    private const int Seed = 98765;

    /// <summary>
    /// Generates a sensible file name suitable for a trial workbook upload.
    /// Examples: "field trial 2.xlsx", "controlled assessment 7.xlsx".
    /// </summary>
    public static string GenerateFileName()
    {
        // The non-generic Faker is seeded via the static Randomizer.Seed.
        // (The generic Faker<T> uses UseSeed — see UserFaker.cs.)
        Randomizer.Seed = new Random(Seed);
        var faker = new Faker("en");

        var adjectives = new[] { "field", "controlled", "seasonal", "regional", "test" };
        var nouns = new[] { "trial", "assessment", "evaluation", "study", "analysis" };
        var adj = faker.PickRandom(adjectives);
        var noun = faker.PickRandom(nouns);
        var num = faker.Random.Int(1, 20);
        return $"{adj} {noun} {num}.xlsx";
    }

    /// <summary>
    /// Generates a list of distinct file names for multi-trial tests.
    /// </summary>
    public static IReadOnlyList<string> GenerateFileNames(int count)
    {
        Randomizer.Seed = new Random(Seed);
        var faker = new Faker("en");

        var adjectives = new[] { "field", "controlled", "seasonal", "regional", "test", "spring", "autumn", "winter", "summer" };
        var nouns = new[] { "trial", "assessment", "evaluation", "study", "analysis", "survey", "review" };

        var names = new HashSet<string>();
        var attempt = 0;
        while (names.Count < count)
        {
            var adj = faker.PickRandom(adjectives);
            var noun = faker.PickRandom(nouns);
            var num = faker.Random.Int(1, 100) + attempt;
            names.Add($"{adj} {noun} {num}.xlsx");
            attempt++;
        }

        return names.ToList();
    }
}
