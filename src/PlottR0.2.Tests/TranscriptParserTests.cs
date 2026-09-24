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
    [InlineData("PLUT-104-subsymbol-1-value-6.", 104, 1, 6, 0.95)]  // observed Whisper: phonetic aliases + separators
    [InlineData("plot-101-subsample-3-value-7", 101, 3, 7, 0.95)]   // hyphen-joined phrase
    [InlineData("plot104 subsample1 value6", 104, 1, 6, 0.95)]      // glued keyword+number
    [InlineData("plut 101 subsymbol 3 value 7", 101, 3, 7, 0.95)]   // aliases with whitespace
    [InlineData("Sub 7104, plus 203, value 6.", 203, 7104, 6, 0.95)]  // observed Whisper: "plus" for "plot"
    [InlineData("plot 101 subsample 1 values 3, 6, 1", 101, 1, 3, 0.95)]  // multi-value labeled form
    [InlineData("101 3 7 6 1", 101, 3, 7, 0.85)]                        // multi-value bare form
    [InlineData("plot 5 subsample 2 value 10", 5, 2, 10, 0.95)]         // full-word subsample, single value
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
        result.FirstValue.Should().Be(expectedValue);
        if (expectedValue.HasValue)
        {
            // Every parse that states a value yields a non-empty list whose FIRST entry is the
            // asserted value; the exact multi-value lists are pinned by the dedicated Facts.
            result.Values.Should().NotBeEmpty();
            result.Values[0].Should().Be(expectedValue.Value);
        }
        else
        {
            result.Values.Should().BeEmpty("no value keyword / trailing number → no values parsed");
        }
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
        result.FirstValue.Should().BeNull();
        result.Values.Should().BeEmpty();
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
        result.FirstValue.Should().BeNull();
        result.Values.Should().BeEmpty();
        result.Confidence.Should().Be(0);
    }

    [Fact]
    public void Parse_PluralValuesKeyword_CollectsAllFollowingNumbers()
    {
        // Act — the plural keyword collects EVERY numeric token after it, not just one
        var result = TranscriptParser.Parse("plot 101 subsample 1 values 3, 6, 1");

        // Assert — plot/subsample as usual; all three numbers land in Values left-to-right
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(1);
        result.FirstValue.Should().Be(3);
        result.Values.Should().Equal(3, 6, 1);
        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.95);
    }

    [Fact]
    public void Parse_PluralValuesWithPhoneticAliases_ParsesFully()
    {
        // Act — observed Whisper aliases ("plus" for plot, "Sub" for subsample) with the
        // plural keyword: "Sub 7104, plus 203, values 6" → plot 203 / subsample 7104 / [6]
        var result = TranscriptParser.Parse("Sub 7104, plus 203, values 6");

        // Assert
        result.Plot.Should().Be(203);
        result.Subsample.Should().Be(7104);
        result.FirstValue.Should().Be(6);
        result.Values.Should().Equal(6);
        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.95);
    }

    [Fact]
    public void Parse_LabeledValues_StopsAtTheNextKeyword()
    {
        // Act — a second take follows the first in ONE transcript; the value collection must
        // stop at the next keyword ("plot" here) and never swallow the second take's numbers.
        var result = TranscriptParser.Parse("plot 101 subsample 1 values 3 6 1 plot 5 subsample 2 values 4");

        // Assert — only the CONTIGUOUS run after the first "values" (3 6 1) is collected;
        // the second take's 5, 2, 4 stay out of the first row's Values
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(1);
        result.FirstValue.Should().Be(3);
        result.Values.Should().Equal(3, 6, 1);
        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.95);
    }
}