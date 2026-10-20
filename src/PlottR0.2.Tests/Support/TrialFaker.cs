using Bogus;

namespace PlottR0._2.Tests.Support;

public static class TrialFaker
{
    private const int Seed = 98765;

    public static string GenerateFileName()
    {
        Randomizer.Seed = new Random(Seed);
        var faker = new Faker("en");

        var adjectives = new[] { "field", "controlled", "seasonal", "regional", "test" };
        var nouns = new[] { "trial", "assessment", "evaluation", "study", "analysis" };
        var adj = faker.PickRandom(adjectives);
        var noun = faker.PickRandom(nouns);
        var num = faker.Random.Int(1, 20);
        return $"{adj} {noun} {num}.xlsx";
    }

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
