using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

public sealed class TranscriptParserTests
{
    [Theory]
    [InlineData("plot 101 subsample 3 value 7", 101, 3, 7, 0.95)]
    [InlineData("101 3 7", null, null, 101, 0.5)]
    [InlineData("plot one oh one, subsample three, value seven", 101, 3, 7, 0.75)]
    [InlineData("one oh one three seven", null, null, 101, 0.5)]
    [InlineData("two oh four, 1, 5", null, null, 204, 0.5)]
    [InlineData("plot 5 sub 2 value 10", 5, 2, 10, 0.95)]
    [InlineData("plot 8 subsample 1 value 0", 8, 1, 0, 0.95)]
    [InlineData("plot 101", 101, null, null, 0.5)]
    [InlineData("101", null, null, 1, 0.5)]
    [InlineData("hello world", null, null, null, 0.0)]
    [InlineData("plot 101 SUBSAMPLE 3 VALUE 7", 101, 3, 7, 0.95)]
    [InlineData("101, 3, 7", null, null, 101, 0.5)]
    [InlineData("one three seven", null, null, 1, 0.5)]
    [InlineData("1 0 1", null, null, 1, 0.5)]
    [InlineData("PLUT-104-subsymbol-1-value-6.", 104, 1, 6, 0.95)]
    [InlineData("plot-101-subsample-3-value-7", 101, 3, 7, 0.95)]
    [InlineData("plot104 subsample1 value6", 104, 1, 6, 0.95)]
    [InlineData("plut 101 subsymbol 3 value 7", 101, 3, 7, 0.95)]
    [InlineData("plot 101 subamble 1 value 7", 101, 1, 7, 0.95)]
    [InlineData("plot 101 subsamble 1 value 7", 101, 1, 7, 0.95)]
    [InlineData("Sub 7104, plus 203, value 6.", 203, 7104, 6, 0.95)]
    [InlineData("plot 101 sub-symbol 1 value 7", 101, 1, 7, 0.95)]
    [InlineData("plot-101-sub-symbol-1-value-7", 101, 1, 7, 0.95)]
    [InlineData("plot 101 sub symbol 1 value 7", 101, 1, 7, 0.95)]
    [InlineData("plug 101 sub 1 value 7", 101, 1, 7, 0.95)]
    [InlineData("clot 101 sub 1 value 7", 101, 1, 7, 0.95)]
    [InlineData("pluts 101 sub 1 value 7", 101, 1, 7, 0.95)]
    [InlineData("blot 101 sub 1 value 7", 101, 1, 7, 0.95)]
    [InlineData("clotting 101 sub 1 value 7", null, 1, 7, 0.5)]
    [InlineData("plot 101 subsample 1 values 3, 6, 1", 101, 1, 3, 0.95)]
    [InlineData("plot 101 subsample 1 values 3 6 1", 101, 1, 3, 0.95)]
    [InlineData("plot 101 sub 3 value 7", 101, 3, 7, 0.95)]
    [InlineData("101 3 7 6 1", null, null, 101, 0.5)]
    [InlineData("3, 6, 1", null, null, 3, 0.5)]
    [InlineData("3 6 1", null, null, 3, 0.5)]
    [InlineData("three six one", null, null, 3, 0.5)]
    [InlineData("7", null, null, 7, 0.5)]
    [InlineData("plot 5 subsample 2 value 10", 5, 2, 10, 0.95)]
    public void Parse_ReturnsExpectedFieldsAndConfidence(
        string transcript,
        int? expectedPlot,
        int? expectedSubsample,
        int? expectedValue,
        double expectedConfidence)
    {
        var result = TranscriptParser.Parse(transcript);

        result.Plot.Should().Be(expectedPlot);
        result.Subsample.Should().Be(expectedSubsample);
        result.FirstValue.Should().Be(expectedValue);
        if (expectedValue.HasValue)
        {
            result.Values.Should().NotBeEmpty();
            result.Values[0].Should().Be(expectedValue.Value);
        }
        else
        {
            result.Values.Should().BeEmpty("the transcript stated no value → no values parsed");
        }
        result.Confidence.Should().Be(expectedConfidence);
        result.IsComplete.Should().Be(
            expectedPlot.HasValue && expectedSubsample.HasValue && expectedValue.HasValue);
    }

    [Theory]

    [InlineData("one oh one three seven", new[] { 101, 3, 7 })]
    [InlineData("two oh four, 1, 5", new[] { 204, 1, 5 })]
    [InlineData("three six one", new[] { 3, 6, 1 })]
    public void Parse_KeywordFreeTranscript_IsValuesOnlyWithPartialConfidence(
        string transcript, int[] expectedValues)
    {
        var result = TranscriptParser.Parse(transcript);

        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Values.Should().Equal(expectedValues);
        result.IsComplete.Should().BeFalse();
        result.Confidence.Should().Be(0.5);
    }

    [Theory]

    [InlineData("3, 6, 1")]
    [InlineData("3 6 1")]
    [InlineData("3-6-1")]
    public void Parse_KeywordFreeValues_ProduceIdenticalValues(string transcript)
    {
        var result = TranscriptParser.Parse(transcript);

        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Values.Should().Equal(3, 6, 1);
        result.FirstValue.Should().Be(3);
        result.IsComplete.Should().BeFalse();
        result.Confidence.Should().Be(0.5, "an incomplete parse never reaches the complete bands");
    }

    [Fact]
    public void Parse_DeletedBareTriple_NowParsesAsValuesOnly()
    {
        var result = TranscriptParser.Parse("101 3 7");

        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Values.Should().Equal(101, 3, 7);
        result.FirstValue.Should().Be(101);
        result.IsComplete.Should().BeFalse();
        result.Confidence.Should().Be(0.5);
    }

    [Fact]
    public void Parse_SingleBareNumber_YieldsExactlyOneValue()
    {
        var result = TranscriptParser.Parse("7");

        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Values.Should().Equal(7);
        result.FirstValue.Should().Be(7);
        result.IsComplete.Should().BeFalse();
        result.Confidence.Should().Be(0.5);
    }

    [Fact]
    public void Parse_BareZero_ParsesZeroAsALegalValue()
    {
        var result = TranscriptParser.Parse("0");

        result.Values.Should().Equal(0);
        result.FirstValue.Should().Be(0);
        result.Confidence.Should().Be(0.5);
    }

    [Fact]
    public void Parse_LabeledForm_IsUnchangedByTheValuesOnlyBareRule()
    {
        var result = TranscriptParser.Parse("plot 101 sub 3 value 7");

        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(3);
        result.Values.Should().Equal(7);
        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.95);
    }

    [Fact]
    public void Parse_ValuesGlued398_SplitsToDigits()
    {
        var result = TranscriptParser.Parse("plot 101 subsample 1 values 398");

        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(1);
        result.Values.Should().Equal(3, 9, 8);
        result.FirstValue.Should().Be(3, "the reported first value agrees with the written cells");
        result.Repaired.Should().BeTrue();
    }

    [Theory]

    [InlineData("values 10", new[] { 10 })]
    [InlineData("value 0", new[] { 0 })]
    [InlineData("values 7", new[] { 7 })]
    public void Parse_LegalValue_NeverSplit(string transcript, int[] expectedValues)
    {
        var result = TranscriptParser.Parse(transcript);

        result.Values.Should().Equal(expectedValues);
        result.Repaired.Should().BeFalse();
    }

    [Theory]

    [InlineData("values 11", new[] { 11 })]
    [InlineData("values 12", new[] { 12 })]
    public void Parse_TwoDigitOutOfRange_NeverSplit(string transcript, int[] expectedValues)
    {
        var result = TranscriptParser.Parse(transcript);

        result.Values.Should().Equal(expectedValues);
        result.Repaired.Should().BeFalse();
    }

    [Fact]
    public void Parse_PlotAndSubsample_NeverSplit()
    {
        var result = TranscriptParser.Parse("plot 101 subsample 7104 value 7");

        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(7104);
        result.Values.Should().Equal(7);
        result.Repaired.Should().BeFalse();
    }

    [Fact]
    public void Parse_BareSingleGlued398_Splits()
    {
        var result = TranscriptParser.Parse("398");

        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Values.Should().Equal(3, 9, 8);
        result.Repaired.Should().BeTrue();
        result.Confidence.Should().Be(0.5, "an incomplete parse stays at the partial band");
    }

    [Theory]

    [InlineData("101 3 7", new[] { 101, 3, 7 })]
    [InlineData("36 1", new[] { 36, 1 })]
    public void Parse_BareMultiNumbers_NeverSplit(string transcript, int[] expectedValues)
    {
        var result = TranscriptParser.Parse(transcript);

        result.Values.Should().Equal(expectedValues);
        result.Repaired.Should().BeFalse();
    }

    [Fact]
    public void Parse_LeadingZero039_SplitsToZeroThreeNine()
    {
        var result = TranscriptParser.Parse("values 039");

        result.Values.Should().Equal(0, 3, 9);
        result.Repaired.Should().BeTrue();
    }

    [Fact]
    public void Parse_OverDigitCap_DoesNotSplit()
    {
        var result = TranscriptParser.Parse("value 1234567890");

        result.Values.Should().Equal(1234567890);
        result.Repaired.Should().BeFalse();
    }

    [Theory]

    [InlineData("Plot 101 subsamble 1.Values 398")]
    [InlineData("Plot 101 subamble 1.Values 398")]
    public void Parse_OwnerUtterance_ParsesSubambleAndSplitValues(string transcript)
    {
        var result = TranscriptParser.Parse(transcript);

        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(1);
        result.Values.Should().Equal(3, 9, 8);
        result.FirstValue.Should().Be(3);
        result.Repaired.Should().BeTrue();
        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.70, "a repaired parse is capped below the 0.75 review threshold");
    }

    [Fact]
    public void Parse_RepairedLabeled_CapsConfidenceAt070()
    {
        var result = TranscriptParser.Parse("plot 101 subsample 1 values 398");

        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.70);
        result.Repaired.Should().BeTrue();
    }

    [Fact]
    public void Parse_WordFormGluedValue_SplitsTheDigitTextItBuilt()
    {
        var result = TranscriptParser.Parse("plot 101 subsample 1 value one oh one");

        result.Values.Should().Equal(1, 0, 1);
        result.Repaired.Should().BeTrue();
        result.Confidence.Should().Be(0.70, "word-form 0.75 is still capped by the repair");
    }

    [Fact]
    public void Parse_ValueCeiling_MatchesRecordingSyncServiceMaxRating()
    {
        TranscriptParser.ValueCeiling.Should().Be(RecordingSyncService.MaxRating);
    }

    [Theory]

    [InlineData("plot one oh one subsample three value seven")]
    [InlineData("plot one oh one, subsample three, value seven")]
    public void Parse_WordFormNumbers_ReportsWordFormConfidence(string transcript)
    {
        var result = TranscriptParser.Parse(transcript);

        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.75);
    }

    [Fact]
    public void Parse_EmptyTranscript_ReturnsNoFieldsAndZeroConfidence()
    {
        var result = TranscriptParser.Parse("");

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
        var result = TranscriptParser.Parse("plot of subsample value");

        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.FirstValue.Should().BeNull();
        result.Values.Should().BeEmpty();
        result.Confidence.Should().Be(0);
    }

    [Fact]
    public void Parse_PluralValuesKeyword_CollectsAllFollowingNumbers()
    {
        var result = TranscriptParser.Parse("plot 101 subsample 1 values 3, 6, 1");

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
        var result = TranscriptParser.Parse("Sub 7104, plus 203, values 6");

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
        var result = TranscriptParser.Parse("plot 101 subsample 1 values 3 6 1 plot 5 subsample 2 values 4");

        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(1);
        result.FirstValue.Should().Be(3);
        result.Values.Should().Equal(3, 6, 1);
        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.95);
    }
}