using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

/// <summary>
/// Pure unit tests for <see cref="TranscriptParser"/> covering the constrained MVP grammar:
/// labeled digits, bare digits, word forms with "oh"-merged plots, the <c>sub</c> alias,
/// a zero value, partials, garbage, case-insensitivity and comma separators.
/// No database, no Docker, no fixture.
/// </summary>
public sealed class TranscriptParserTests
{
    [Theory]
    [InlineData("plot 101 subsample 3 value 7", 101, 3, 7, 0.95)]
    [InlineData("101 3 7", 101, 3, 7, 0.85)]
    [InlineData("plot one oh one, subsample three, value seven", 101, 3, 7, 0.75)]
    [InlineData("one oh one three seven", 101, 3, 7, 0.75)]
    [InlineData("two oh four, 1, 5", 204, 1, 5, 0.75)]
    [InlineData("plot 5 sub 2 value 10", 5, 2, 10, 0.95)]
    [InlineData("plot 8 subsample 1 value 0", 8, 1, 0, 0.95)]
    [InlineData("plot 101", 101, null, null, 0.5)]
    [InlineData("101", 101, null, null, 0.5)]
    [InlineData("hello world", null, null, null, 0.0)]
    [InlineData("plot 101 SUBSAMPLE 3 VALUE 7", 101, 3, 7, 0.95)]   // case-insensitive
    [InlineData("101, 3, 7", 101, 3, 7, 0.85)]                       // commas are separators
    [InlineData("one three seven", 1, 3, 7, 0.75)]  // word runs WITHOUT "oh" stay separate
    [InlineData("1 0 1", 1, 0, 1, 0.85)]            // bare digits NEVER merge: zero stays explicit
    public void Parse_ReturnsExpectedFieldsAndConfidence(
        string transcript,
        int? expectedPlot,
        int? expectedSubsample,
        int? expectedValue,
        double expectedConfidence)
    {
        // Act
        var result = TranscriptParser.Parse(transcript);

        // Assert
        result.Plot.Should().Be(expectedPlot);
        result.Subsample.Should().Be(expectedSubsample);
        result.Value.Should().Be(expectedValue);
        result.Confidence.Should().Be(expectedConfidence);
        result.IsComplete.Should().Be(
            expectedPlot.HasValue && expectedSubsample.HasValue && expectedValue.HasValue);
    }

    [Theory]
    [InlineData("plot one oh one subsample three value seven")]
    [InlineData("one oh one three seven")]
    [InlineData("two oh four, 1, 5")]
    public void Parse_WordFormNumbers_ReportsWordFormConfidence(string transcript)
    {
        // Act
        var result = TranscriptParser.Parse(transcript);

        // Assert — any word-form number caps confidence below the digits-only scores
        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.75);
    }

    [Fact]
    public void Parse_EmptyTranscript_ReturnsNoFieldsAndZeroConfidence()
    {
        // Act
        var result = TranscriptParser.Parse("");

        // Assert
        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Value.Should().BeNull();
        result.Confidence.Should().Be(0);
        result.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Parse_KeywordsWithNoNumbers_ReturnsNothing()
    {
        // Act — "plot" and "subsample" appear but no numeric tokens follow them
        var result = TranscriptParser.Parse("plot of subsample value");

        // Assert — no numbers parsed at all, zero confidence
        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Value.Should().BeNull();
        result.Confidence.Should().Be(0);
    }
}