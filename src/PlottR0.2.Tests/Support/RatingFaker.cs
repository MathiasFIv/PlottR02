using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests.Support;

/// <summary>
/// Bogus factories for <see cref="RecordSubsampleRatingsCommand"/>-related data.
/// Seeded so that a given test is deterministic across runs (same seed → same generated values),
/// which is the whole point of using Bogus for seedable test data.
///
/// The plot and subsample pools are consumed from <see cref="RatingWorkbookFactory"/> — the
/// single source of truth for "which plots/subsamples exist in the test workbooks" — so the
/// faker can never drift from what the tests actually create.
/// </summary>
public static class RatingFaker
{
    /// <summary>A fixed seed gives reproducible output each run.</summary>
    private const int Seed = 54321;

    /// <summary>
    /// Generator for a valid <see cref="RecordSubsampleRatingsCommand"/>.
    /// Plot from the factory's plot set, traitOrdinal 1..9, value 0..10,
    /// non-empty unique subsample indices ⊆ the factory's per-plot subsample set
    /// (<c>PickRandom</c> draws without replacement, so duplicates are impossible).
    /// </summary>
    public static Faker<RecordSubsampleRatingsCommand> RecordCommand()
    {
        var faker = new Faker<RecordSubsampleRatingsCommand>(locale: "en")
            .CustomInstantiator(f => new RecordSubsampleRatingsCommand
            {
                Plot = f.PickRandom(RatingWorkbookFactory.Plots),
                TraitOrdinal = f.Random.Int(1, 9),
                Value = f.Random.Int(0, 10),
                SubsampleIndices = f.PickRandom(
                        RatingWorkbookFactory.Subsamples,
                        f.Random.Int(1, RatingWorkbookFactory.Subsamples.Length))
                    .ToArray(),
            });

        // Seeds must be applied to the Faker<T> itself (via UseSeed) to be reproducible.
        faker.UseSeed(Seed);
        return faker;
    }
}