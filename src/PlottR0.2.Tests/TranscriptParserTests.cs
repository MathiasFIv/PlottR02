using PlottR0._2.Services.Implementations;

namespace PlottR0._2.Tests;

/// <summary>
/// Pure unit tests for <see cref="TranscriptParser"/> covering the constrained MVP grammar:
/// labeled digits, keyword-free VALUES-ONLY utterances (a bare transcript never yields a
/// plot/subsample), word forms with "oh"-merged numbers, the <c>sub</c> alias,
/// a zero value, partials, garbage, case-insensitivity and comma separators.
/// No database, no Docker, no fixture.
/// </summary>
public sealed class TranscriptParserTests
{
    [Theory]
    [InlineData("plot 101 subsample 3 value 7", 101, 3, 7, 0.95)]
    [InlineData("101 3 7", null, null, 101, 0.5)]                       // deleted triple form → VALUES ONLY
    [InlineData("plot one oh one, subsample three, value seven", 101, 3, 7, 0.75)]
    [InlineData("one oh one three seven", null, null, 101, 0.5)]        // keyword-free → values only
    [InlineData("two oh four, 1, 5", null, null, 204, 0.5)]             // keyword-free → values only
    [InlineData("plot 5 sub 2 value 10", 5, 2, 10, 0.95)]
    [InlineData("plot 8 subsample 1 value 0", 8, 1, 0, 0.95)]
    [InlineData("plot 101", 101, null, null, 0.5)]
    [InlineData("101", null, null, 1, 0.5)]                           // lone glued number → digit-split (see glue theory)
    [InlineData("hello world", null, null, null, 0.0)]
    [InlineData("plot 101 SUBSAMPLE 3 VALUE 7", 101, 3, 7, 0.95)]   // case-insensitive
    [InlineData("101, 3, 7", null, null, 101, 0.5)]                    // commas are separators
    [InlineData("one three seven", null, null, 1, 0.5)]   // word runs WITHOUT "oh" stay separate
    [InlineData("1 0 1", null, null, 1, 0.5)]             // bare digits NEVER merge: zero stays explicit
    [InlineData("PLUT-104-subsymbol-1-value-6.", 104, 1, 6, 0.95)]  // observed Whisper: phonetic aliases + separators
    [InlineData("plot-101-subsample-3-value-7", 101, 3, 7, 0.95)]   // hyphen-joined phrase
    [InlineData("plot104 subsample1 value6", 104, 1, 6, 0.95)]      // glued keyword+number
    [InlineData("plut 101 subsymbol 3 value 7", 101, 3, 7, 0.95)]   // aliases with whitespace
    [InlineData("plot 101 subamble 1 value 7", 101, 1, 7, 0.95)]    // "subamble" alias (owner utterance)
    [InlineData("plot 101 subsamble 1 value 7", 101, 1, 7, 0.95)]   // "subsamble" alias
    [InlineData("Sub 7104, plus 203, value 6.", 203, 7104, 6, 0.95)]  // observed Whisper: "plus" for "plot"
    [InlineData("plot 101 subsample 1 values 3, 6, 1", 101, 1, 3, 0.95)]  // multi-value labeled form
    [InlineData("plot 101 subsample 1 values 3 6 1", 101, 1, 3, 0.95)]    // multi-value labeled form, spaces
    [InlineData("plot 101 sub 3 value 7", 101, 3, 7, 0.95)]               // "sub" alias, labeled form
    [InlineData("101 3 7 6 1", null, null, 101, 0.5)]                     // keyword-free → ALL numbers are values
    [InlineData("3, 6, 1", null, null, 3, 0.5)]                           // run continuation: values only
    [InlineData("3 6 1", null, null, 3, 0.5)]                             // run continuation, no commas
    [InlineData("three six one", null, null, 3, 0.5)]                     // run continuation, word form
    [InlineData("7", null, null, 7, 0.5)]                                 // single value
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
            result.Values.Should().BeEmpty("the transcript stated no value → no values parsed");
        }
        result.Confidence.Should().Be(expectedConfidence);
        result.IsComplete.Should().Be(
            expectedPlot.HasValue && expectedSubsample.HasValue && expectedValue.HasValue);
    }

    [Theory]
    // Keyword-free word forms: every number is a value, plot/subsample stay null, and the
    // parse is INCOMPLETE — so it scores the partial band, not the word-form 0.75 band.
    [InlineData("one oh one three seven", new[] { 101, 3, 7 })]
    [InlineData("two oh four, 1, 5", new[] { 204, 1, 5 })]
    [InlineData("three six one", new[] { 3, 6, 1 })]
    public void Parse_KeywordFreeTranscript_IsValuesOnlyWithPartialConfidence(
        string transcript, int[] expectedValues)
    {
        // Act
        var result = TranscriptParser.Parse(transcript);

        // Assert — no keyword → no plot/subsample, ALL numbers land in Values
        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Values.Should().Equal(expectedValues);
        result.IsComplete.Should().BeFalse();
        result.Confidence.Should().Be(0.5);
    }

    [Theory]
    // The run-continuation utterance: the operator says only the numbers and the client's
    // default target supplies plot/subsample at sync time. Separator variants must agree.
    [InlineData("3, 6, 1")]
    [InlineData("3 6 1")]
    [InlineData("3-6-1")]
    public void Parse_KeywordFreeValues_ProduceIdenticalValues(string transcript)
    {
        // Act
        var result = TranscriptParser.Parse(transcript);

        // Assert
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
        // Act — "101 3 7" USED to mean plot 101 / subsample 3 / value 7 (confidence 0.85).
        // Owner decision (2026-10-08) deleted that form: a keyword-free transcript is
        // values-only, so 101 lands in Values where the sync service's 0..10 value gate
        // flags it — it can never be written as a plot.
        var result = TranscriptParser.Parse("101 3 7");

        // Assert
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
        // Act — "7" (continuation of a run with one remaining cell)
        var result = TranscriptParser.Parse("7");

        // Assert
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
        // Act — zero is legal write data (Rule 2) and must never be dropped or read as null
        var result = TranscriptParser.Parse("0");

        // Assert
        result.Values.Should().Equal(0);
        result.FirstValue.Should().Be(0);
        result.Confidence.Should().Be(0.5);
    }

    [Fact]
    public void Parse_LabeledForm_IsUnchangedByTheValuesOnlyBareRule()
    {
        // Act — the one-shot keyword rules and the contiguous-run collection still hold
        var result = TranscriptParser.Parse("plot 101 sub 3 value 7");

        // Assert
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(3);
        result.Values.Should().Equal(7);
        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.95);
    }

    // ── Glued-digit repair (owner decision 2026-10-08): Whisper drops the separators
    //    between spoken digits, so one value token carries several ratings ─────────

    [Fact]
    public void Parse_ValuesGlued398_SplitsToDigits()
    {
        // Act — the owner said "3, 9, 8"; Whisper glued it into the ONE token "398"
        var result = TranscriptParser.Parse("plot 101 subsample 1 values 398");

        // Assert — value position only: 398 is far above ValueCeiling, so it re-splits
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(1);
        result.Values.Should().Equal(3, 9, 8);
        result.FirstValue.Should().Be(3, "the reported first value agrees with the written cells");
        result.Repaired.Should().BeTrue();
    }

    [Theory]
    // In-range legal values are taken at face value: 10 is a real rating, 0 is a real
    // rating, and a lone digit has nothing to repair.
    [InlineData("values 10", new[] { 10 })]
    [InlineData("value 0", new[] { 0 })]
    [InlineData("values 7", new[] { 7 })]
    public void Parse_LegalValue_NeverSplit(string transcript, int[] expectedValues)
    {
        // Act
        var result = TranscriptParser.Parse(transcript);

        // Assert
        result.Values.Should().Equal(expectedValues);
        result.Repaired.Should().BeFalse();
    }

    [Theory]
    // Two-digit tokens are NEVER split, even out of range: ratings legitimately reach 10,
    // so "11"/"12" is genuinely ambiguous — writing two ratings would silently invent data.
    // Staying whole means value gate B flags it, which is the safe direction.
    [InlineData("values 11", new[] { 11 })]
    [InlineData("values 12", new[] { 12 })]
    public void Parse_TwoDigitOutOfRange_NeverSplit(string transcript, int[] expectedValues)
    {
        // Act
        var result = TranscriptParser.Parse(transcript);

        // Assert
        result.Values.Should().Equal(expectedValues);
        result.Repaired.Should().BeFalse();
    }

    [Fact]
    public void Parse_PlotAndSubsample_NeverSplit()
    {
        // Act — plot numbers are legitimately 3-digit and subsample numbers reach four
        // digits; both go through NextNumberAfter, which returns a single int and is
        // therefore structurally incapable of splitting.
        var result = TranscriptParser.Parse("plot 101 subsample 7104 value 7");

        // Assert
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(7104);
        result.Values.Should().Equal(7);
        result.Repaired.Should().BeFalse();
    }

    [Fact]
    public void Parse_BareSingleGlued398_Splits()
    {
        // Act — keyword-free, and EXACTLY ONE numeric token: the repair applies
        var result = TranscriptParser.Parse("398");

        // Assert
        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Values.Should().Equal(3, 9, 8);
        result.Repaired.Should().BeTrue();
        result.Confidence.Should().Be(0.5, "an incomplete parse stays at the partial band");
    }

    [Theory]
    // Multi-number bare runs pass through RAW: the repair only fires for a lone token,
    // so "101 3 7" (the deleted-triple regression guard) and "36 1" keep their literal
    // values and are still flagged by value gate B instead of being reinterpreted.
    [InlineData("101 3 7", new[] { 101, 3, 7 })]
    [InlineData("36 1", new[] { 36, 1 })]
    public void Parse_BareMultiNumbers_NeverSplit(string transcript, int[] expectedValues)
    {
        // Act
        var result = TranscriptParser.Parse(transcript);

        // Assert
        result.Values.Should().Equal(expectedValues);
        result.Repaired.Should().BeFalse();
    }

    [Fact]
    public void Parse_LeadingZero039_SplitsToZeroThreeNine()
    {
        // Act — the raw token text is what survives: an int round-trip of "039" is 39 and
        // would eat the leading zero, so the repair must read the text, not the number.
        var result = TranscriptParser.Parse("values 039");

        // Assert
        result.Values.Should().Equal(0, 3, 9);
        result.Repaired.Should().BeTrue();
    }

    [Fact]
    public void Parse_OverDigitCap_DoesNotSplit()
    {
        // Act — 10 digits is past MaxSplitDigits: refuse and leave it whole so gate B flags
        // it. The 9-digit cap also bounds the trait-column blast radius.
        var result = TranscriptParser.Parse("value 1234567890");

        // Assert
        result.Values.Should().Equal(1234567890);
        result.Repaired.Should().BeFalse();
    }

    [Theory]
    // The owner's verbatim field utterance. Both Whisper spellings of the subsample alias
    // are explicit dictionary entries, so neither drops the subsample field.
    [InlineData("Plot 101 subsamble 1.Values 398")]
    [InlineData("Plot 101 subamble 1.Values 398")]
    public void Parse_OwnerUtterance_ParsesSubambleAndSplitValues(string transcript)
    {
        // Act
        var result = TranscriptParser.Parse(transcript);

        // Assert — plot/subsample survive, the glued value splits, confidence is capped
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
        // Act — an otherwise perfect labeled parse (0.95) must still drop to 0.70 when the
        // value was repaired: 0.70 < ReviewConfidenceThreshold (0.75) ⇒ NeedsReview=true
        var result = TranscriptParser.Parse("plot 101 subsample 1 values 398");

        // Assert
        result.IsComplete.Should().BeTrue();
        result.Confidence.Should().Be(0.70);
        result.Repaired.Should().BeTrue();
    }

    [Fact]
    public void Parse_WordFormGluedValue_SplitsTheDigitTextItBuilt()
    {
        // Act — word forms follow the same predicate on the digit-text they assembled:
        // "one oh one" builds "101", which is above ValueCeiling and therefore splits.
        var result = TranscriptParser.Parse("plot 101 subsample 1 value one oh one");

        // Assert
        result.Values.Should().Equal(1, 0, 1);
        result.Repaired.Should().BeTrue();
        result.Confidence.Should().Be(0.70, "word-form 0.75 is still capped by the repair");
    }

    [Fact]
    public void Parse_ValueCeiling_MatchesRecordingSyncServiceMaxRating()
    {
        // Drift guard — the repair's ceiling is the same bound value gate B validates
        // against, so a value the parser takes at face value can never be one gate B rejects.
        TranscriptParser.ValueCeiling.Should().Be(RecordingSyncService.MaxRating);
    }

    [Theory]
    // Word-form numbers still cap a LABELED parse at 0.75 (below the digits-only 0.95).
    // Keyword-free word forms are covered by
    // Parse_KeywordFreeTranscript_IsValuesOnlyWithPartialConfidence above (0.5 — incomplete).
    [InlineData("plot one oh one subsample three value seven")]
    [InlineData("plot one oh one, subsample three, value seven")]
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