using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlottR0._2.Contracts.Transcription;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Pure, dependency-free parser for transcripts produced by the V2E (voice-to-excel)
/// MVP flow. It is deliberately stateless and synchronous so the parsing grammar stays
/// trivially unit-testable; the caller (the sync service) runs it against raw
/// transcription text.
/// <para>
/// Supported grammar (design.md, locked plan):
/// <list type="bullet">
///   <item><description>Labeled digits — <c>"plot 101 subsample 3 value 7"</c>, and the
///   multi-value form <c>"plot 101 subsample 1 values 3 6 1"</c> where the contiguous run
///   of numbers after <c>values</c> is collected (the first fills the recording's
///   traitOrdinal column, the rest fill the succeeding trait columns left-to-right).</description></item>
///   <item><description>Bare digits are VALUES ONLY — a keyword-free transcript puts EVERY
///   number in <c>Values</c> and leaves <c>Plot</c>/<c>Subsample</c> <see langword="null"/>
///   (<c>"3, 6, 1"</c> ⇒ values [3, 6, 1]; a single <c>"7"</c> ⇒ values [7]). Plot and
///   subsample REQUIRE their keywords. This is the run-continuation form: the operator
///   says only the numbers and the client's default target fills the target fields.
///   The former bare triple (<c>"101 3 7"</c> ⇒ plot 101 / subsample 3 / value 7) is
///   deliberately DELETED — it now parses as values [101, 3, 7], and a number above 10
///   can never be written: the sync service's 0..10 value gate flags it for review.</description></item>
///   <item><description>Word digits — <c>"one oh one, three, seven"</c>.</description></item>
///   <item><description>Labeled word digits — <c>"plot one oh one, subsample three, value seven"</c>.</description></item>
///   <item><description>Glued-digit repair — the ONE step that is not pure format, and it
///   applies in VALUE position ONLY (never plot, never subsample): when Whisper drops the
///   separators between spoken digits, a numeric token of <c>3..<see cref="MaxSplitDigits"/></c>
///   digits whose value is above <see cref="ValueCeiling"/> or whose raw text starts with
///   <c>0</c> is split into its decimal digits (<c>"values 398"</c> ⇒ [3, 9, 8],
///   <c>"values 039"</c> ⇒ [0, 3, 9] — the RAW text is used so a leading zero survives).
///   Two-digit tokens are NEVER split (<c>"values 11"</c> stays [11]): values legitimately
///   reach 10, so a two-digit out-of-range token is genuinely ambiguous and flagging it is
///   the safe direction. The repair is bounded by <see cref="MaxSplitDigits"/> — an over-cap
///   token (<c>"1234567890"</c>) is passed through whole so gate B flags it.</description></item>
/// </list>
/// <c>subsample</c> accepts the aliases <c>sub</c>, <c>subsymbol</c>, <c>subsamble</c> and
/// <c>subamble</c>; <c>plot</c> accepts <c>plut</c>, <c>plod</c> and <c>plus</c>
/// (<c>"plot 5 sub 2 value 10"</c>, the observed <c>"PLUT-104-subsymbol-1-value-6."</c>,
/// <c>"Sub 7104, plus 203, value 6."</c> and <c>"Plot 101 subsamble 1.Values 398"</c>).
/// Separators beyond whitespace are supported: hyphens/periods/commas split and a
/// letter↔digit transition splits <c>"plot104"</c>.
/// Parsing is FORMAT-ONLY apart from the ceiling-bounded glued-digit repair above: there is
/// no trait parsing (exactly one trait for MVP) and no value-range validation — the sync
/// service validates plot existence and value range against the workbook afterwards. The
/// repair does NOT decide legality either: it only re-splits digits it refuses to believe
/// is one number, and anything it leaves whole (a two-digit out-of-range token, an over-cap
/// token) still reaches the sync service's value gate B (<c>RecordingSyncService.cs:264</c>)
/// as defence-in-depth.
/// </para>
/// <para>
/// Multi-digit plot reconstruction: a consecutive run of digit-words bridged by an
/// <c>"oh"</c> collapses left-to-right into one number (<c>"one oh one"</c> ⇒ 101,
/// <c>"two oh four"</c> ⇒ 204). Documented boundary: only the words directly adjacent to
/// an <c>"oh"</c> merge (<c>"one oh one three seven"</c> ⇒ 101, 3, 7), a word-digit run
/// WITHOUT an <c>"oh"</c> stays separate numbers (<c>"one three seven"</c> ⇒ 1, 3, 7),
/// and literal numeric tokens are never merged (<c>"1 0 1"</c> is the three numbers
/// 1, 0, 1). Plots larger than ten therefore read best when spoken digit-by-digit around
/// an <c>"oh"</c>.
/// </para>
/// <para>
/// Confidence scoring: 0.95 complete + fully labeled; 0.85 complete + bare; 0.75 complete
/// with any word-form number (the least reliable recognition signal — it overrides the
/// scores above); 0.5 partial (some fields parsed); 0 when nothing parsed (all fields
/// <see langword="null"/>). Since bare transcripts are values-only, they never state
/// plot/subsample and are therefore ALWAYS incomplete: every keyword-free utterance
/// scores 0.5 (or 0), so the 0.85 band is currently unreachable but kept in the code.
/// A REPAIRED parse is additionally capped at 0.70 (<c>Repaired</c>): that sits below the
/// sync service's <c>ReviewConfidenceThreshold</c> (0.75), so glued-digit repair always
/// routes the recording through manual review while the write itself still proceeds from
/// the repaired values.
/// </para>
/// </summary>
public static class TranscriptParser
{
    /// <summary>
    /// Highest legal rating value. Mirrors <c>RecordingSyncService.MaxRating</c> (a drift
    /// guard test pins the two together). The parser stays format-only EXCEPT for the
    /// ceiling-bounded glued-digit repair, where this ceiling is the repair's upper bound —
    /// a value at or below it is taken at face value and never re-split. Deciding whether a
    /// value is LEGAL is deliberately not this class's job: range validation stays in the
    /// sync service's value gate B (<c>RecordingSyncService.cs:264</c>) as defence-in-depth,
    /// so a token the repair refuses to split is still caught there.
    /// </summary>
    public const int ValueCeiling = 10;

    /// <summary>
    /// Longest numeric token the glued-digit repair will split (the predicate is
    /// <c>3 &lt;= length &lt;= MaxSplitDigits</c> AND out-of-ceiling-or-leading-zero).
    /// The 9-digit cap bounds the repair's blast radius — trait columns span 1..9 — and it
    /// makes a longer token (<c>"1234567890"</c>) pass through whole so value gate B
    /// flags it instead of the parser guessing at its digits.
    /// </summary>
    private const int MaxSplitDigits = 9;

    /// <summary>Normalized token kinds produced by the tokenizer/merging pass.</summary>
    private enum TokenKind
    {
        Number,
        Plot,
        Subsample,
        Value,
    }

    /// <summary>
    /// Accepted spellings for the three keywords, including the phonetic variants Whisper
    /// actually emits with the MVP speakers: "plut" for "plot" and "subsymbol" for
    /// "subsample" were both observed in the field, and so was "plus" for "plot"
    /// (<c>"Sub 7104, plus 203, value 6."</c> — first observed in the 9712ac34 trial
    /// recordings, where the intended utterance was "plot 203") and "subamble" for
    /// "subsample" (<c>"Plot 101 subsamble 1.Values 398"</c> — the owner's own utterance,
    /// where the missing alias used to drop the subsample field entirely). The short forms
    /// ("sub") come from the original grammar. Lookups are ordinal (tokens are already
    /// lower-cased) and every entry is EXPLICIT — there is deliberately no fuzzy or
    /// prefix matching.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, TokenKind> KeywordAliases =
        new Dictionary<string, TokenKind>(StringComparer.Ordinal)
        {
            // plot
            ["plot"] = TokenKind.Plot,
            ["plut"] = TokenKind.Plot,
            ["plod"] = TokenKind.Plot,
            ["plus"] = TokenKind.Plot,
            // subsample
            ["subsample"] = TokenKind.Subsample,
            ["subsymbol"] = TokenKind.Subsample,
            ["subsampel"] = TokenKind.Subsample,
            ["subsamble"] = TokenKind.Subsample,
            // Observed verbatim field transcript: "Plot 101 subsamble 1.Values 398".
            // Explicit dictionary entry only — no fuzzy/prefix matching.
            ["subamble"] = TokenKind.Subsample,
            ["sub"] = TokenKind.Subsample,
            // value
            ["value"] = TokenKind.Value,
            ["values"] = TokenKind.Value,
            ["valeu"] = TokenKind.Value,
        };

    /// <summary>
    /// Parses one transcript against the constrained MVP grammar.
    /// </summary>
    /// <param name="transcript">The raw transcription text (may be empty).</param>
    /// <returns>The parsed fields and a 0..1 confidence score.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transcript"/> is <c>null</c>.</exception>
    public static TranscriptParseResult Parse(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var sequence = Normalize(Tokenize(transcript));

        int? plot = null;
        int? subsample = null;
        var values = new List<int>();
        var valuesTaken = false;
        var repaired = false;
        var hasKeywords = sequence.Any(t => t.Kind is not TokenKind.Number);

        if (hasKeywords)
        {
            // Labeled form: each keyword takes the next numeric token after it. The FIRST
            // value-kind keyword ("value"/"values") collects the CONTIGUOUS numeric run after
            // it — the multi-value form "plot 101 subsample 1 values 3 6 1"; the run STOPS at
            // the next keyword, so a second take in the same transcript ("... values 3 6 1
            // plot 5 subsample 2 values 4") never bleeds its numbers into the first row. A
            // repeated value keyword is ignored (one-shot like the other fields).
            //
            // The glued-digit repair happens ONLY inside this value-run collection: plot and
            // subsample still go through NextNumberAfter, which returns a single int and is
            // therefore structurally incapable of splitting ("plot 101" and "sub 7104" are
            // never touched — plot numbers are legitimately 3-digit).
            for (var i = 0; i < sequence.Count; i++)
            {
                if (sequence[i].Kind == TokenKind.Plot && plot is null)
                {
                    plot = NextNumberAfter(sequence, i);
                }
                else if (sequence[i].Kind == TokenKind.Subsample && subsample is null)
                {
                    subsample = NextNumberAfter(sequence, i);
                }
                else if (sequence[i].Kind == TokenKind.Value && !valuesTaken)
                {
                    foreach (var token in sequence.Skip(i + 1)
                                 .TakeWhile(t => t.Kind == TokenKind.Number))
                    {
                        repaired |= AddValue(token.Text, token.Value, values);
                    }

                    valuesTaken = true;
                }
            }
        }
        else
        {
            // Keyword-free (VALUES ONLY) form: EVERY number is a value; plot and subsample
            // stay null. "3, 6, 1" → values [3, 6, 1], a single "7" → values [7] — the
            // run-continuation utterance ("values 3, 6, 1" without repeating the keyword)
            // relies on this, with the client's default target supplying plot/subsample.
            // The old bare triple is deliberately gone: "101 3 7" now parses as values
            // [101, 3, 7], and 101 is out of the 0..10 range, so the sync service flags it
            // instead of ever writing it as a plot.
            //
            // The glued-digit repair applies ONLY when the transcript holds exactly ONE
            // numeric token — a lone glued number ("398") is unambiguously a run of digits,
            // while a multi-number bare run passes through RAW so "101 3 7" and "36 1"
            // keep their literal values and still flag via value gate B.
            var numbers = sequence.Where(t => t.Kind == TokenKind.Number).ToList();
            if (numbers.Count == 1)
            {
                repaired |= AddValue(numbers[0].Text, numbers[0].Value, values);
            }
            else
            {
                foreach (var number in numbers)
                {
                    values.Add(number.Value);
                }
            }
        }

        var complete = plot.HasValue && subsample.HasValue && values.Count > 0;
        var anyWordForm = sequence.Any(t => t.Kind == TokenKind.Number && t.WasWord);

        double confidence;
        if (complete)
        {
            confidence = anyWordForm ? 0.75 : (hasKeywords ? 0.95 : 0.85);
        }
        else if (plot.HasValue || subsample.HasValue || values.Count > 0)
        {
            confidence = 0.5;
        }
        else
        {
            confidence = 0;
        }

        // Owner decision: a glued-digit repair is a guess about what Whisper glued
        // together, so a repaired parse can never claim more than 0.70 — below the sync
        // service's ReviewConfidenceThreshold (0.75), which forces NeedsReview while the
        // write still proceeds from the repaired values.
        if (repaired)
        {
            confidence = Math.Min(confidence, 0.70);
        }

        return new TranscriptParseResult
        {
            Plot = plot,
            Subsample = subsample,
            Values = values,
            Confidence = confidence,
            Repaired = repaired,
        };
    }

    /// <summary>
    /// Appends one value-position token to <paramref name="values"/>, splitting it into its
    /// decimal digits when it matches the glued-digit predicate: a token of
    /// 3..<see cref="MaxSplitDigits"/> digits whose value is above <see cref="ValueCeiling"/>
    /// or whose RAW text starts with <c>0</c> (the raw text is what keeps
    /// <c>"039"</c> → [0, 3, 9] — an int round-trip would eat the leading zero).
    /// Two-digit tokens are never split: values legitimately reach <see cref="ValueCeiling"/>,
    /// so a two-digit out-of-range token is ambiguous and stays whole for value gate B to
    /// flag. Only ever called for tokens in VALUE position.
    /// </summary>
    /// <returns><see langword="true"/> when the token was split (the parse is repaired).</returns>
    private static bool AddValue(string text, int value, List<int> values)
    {
        if (text.Length >= 3 && text.Length <= MaxSplitDigits
            && (value > ValueCeiling || text[0] == '0'))
        {
            foreach (var ch in text)
            {
                values.Add(ch - '0');
            }

            return true;
        }

        values.Add(value);
        return false;
    }

    /// <summary>
    /// Lower-cases and splits the raw transcript into candidate tokens. Every run of
    /// non-alphanumeric characters is a separator (whitespace, commas, hyphens, periods,
    /// slashes, backslashes…), and a letter↔digit transition also splits — so Whisper's
    /// hyphen-joined utterances ("plot-101-subsample-3-value-7") and glued
    /// keyword+number ("plot104") both tokenize cleanly. Without this, a hyphen-joined
    /// phrase survives as ONE token that matches nothing and is dropped as noise.
    /// </summary>
    private static readonly Regex TokenSplitRegex = new(
        @"[^a-z0-9]+|(?<=[a-z])(?=[0-9])|(?<=[0-9])(?=[a-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static List<string> Tokenize(string transcript) =>
        TokenSplitRegex.Split(transcript.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();

    /// <summary>
    /// Converts the raw token stream into a sequence of numbers and keywords, dropping
    /// noise. Consecutive digit-words bridged by an <c>"oh"</c> collapse left-to-right
    /// into one number (see type docs for the exact boundary). Every number keeps the RAW
    /// TEXT it was built from (the literal token, or the digit-string an "oh" group
    /// assembled) so the value-position glued-digit repair can split the digits back apart
    /// and keep leading zeros.
    /// </summary>
    private static List<(TokenKind Kind, int Value, bool WasWord, string Text)> Normalize(List<string> tokens)
    {
        var result = new List<(TokenKind, int, bool, string)>();

        // Mark the words merged by an "oh": the "oh" itself plus its immediate digit-word
        // neighbours. Chains of multiple "oh"s expand transitively through these markers.
        var inOhGroup = new bool[tokens.Count];
        for (var p = 0; p < tokens.Count; p++)
        {
            if (tokens[p] != "oh") continue;
            inOhGroup[p] = true;
            if (p > 0 && TryWordDigit(tokens[p - 1], out _)) inOhGroup[p - 1] = true;
            if (p + 1 < tokens.Count && TryWordDigit(tokens[p + 1], out _)) inOhGroup[p + 1] = true;
        }

        for (var i = 0; i < tokens.Count;)
        {
            var token = tokens[i];

            if (TryWordDigit(token, out var digit))
            {
                if (inOhGroup[i])
                {
                    var digits = new StringBuilder();
                    while (i < tokens.Count && inOhGroup[i] && TryWordDigit(tokens[i], out var part))
                    {
                        digits.Append(part);
                        i++;
                    }

                    var digitText = digits.ToString();
                    result.Add((TokenKind.Number, int.Parse(digitText, CultureInfo.InvariantCulture), true, digitText));
                }
                else
                {
                    result.Add((TokenKind.Number, digit, true, digit.ToString(CultureInfo.InvariantCulture)));
                    i++;
                }
                continue;
            }

            if (KeywordAliases.TryGetValue(token, out var kind))
            {
                result.Add((kind, 0, false, string.Empty));
            }
            else if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                result.Add((TokenKind.Number, number, false, token));
            }
            // Any other token is noise and dropped.

            i++;
        }

        return result;
    }

    /// <summary>
    /// Returns the value of the first numeric token at or after
    /// <paramref name="keywordIndex"/> + 1, or <see langword="null"/> when there is none.
    /// Deliberately returns a single <c>int</c>: plot and subsample each take exactly ONE
    /// number, which also makes it structurally impossible for the glued-digit repair to
    /// ever apply to them.
    /// </summary>
    private static int? NextNumberAfter(
        List<(TokenKind Kind, int Value, bool WasWord, string Text)> sequence,
        int keywordIndex)
    {
        for (var j = keywordIndex + 1; j < sequence.Count; j++)
        {
            if (sequence[j].Kind == TokenKind.Number)
            {
                return sequence[j].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Maps a word token to its digit value: <c>zero..ten</c> plus <c>"oh"</c> (= 0).
    /// </summary>
    private static bool TryWordDigit(string token, out int value)
    {
        switch (token)
        {
            case "zero":
            case "oh":
                value = 0;
                return true;
            case "one":
                value = 1;
                return true;
            case "two":
                value = 2;
                return true;
            case "three":
                value = 3;
                return true;
            case "four":
                value = 4;
                return true;
            case "five":
                value = 5;
                return true;
            case "six":
                value = 6;
                return true;
            case "seven":
                value = 7;
                return true;
            case "eight":
                value = 8;
                return true;
            case "nine":
                value = 9;
                return true;
            case "ten":
                value = 10;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}