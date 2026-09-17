using System.Globalization;
using System.Text;
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
///   <item><description>Labeled digits — <c>"plot 101 subsample 3 value 7"</c>.</description></item>
///   <item><description>Bare digits — <c>"101 3 7"</c>.</description></item>
///   <item><description>Word digits — <c>"one oh one, three, seven"</c>.</description></item>
///   <item><description>Labeled word digits — <c>"plot one oh one, subsample three, value seven"</c>.</description></item>
/// </list>
/// <c>subsample</c> accepts the alias <c>sub</c> (as in <c>"plot 5 sub 2 value 10"</c>).
/// Parsing is FORMAT-ONLY: there is no trait parsing (exactly one trait per MVP) and no
/// value-range validation — the sync service validates plot existence and value range
/// against the workbook afterwards.
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
/// with any word-form number (the least reliable recognition signal — it overrides both
/// scores above); 0.5 partial (some fields parsed); 0 when nothing parsed (all fields
/// <see langword="null"/>).
/// </para>
/// </summary>
public static class TranscriptParser
{
    /// <summary>Normalized token kinds produced by the tokenizer/merging pass.</summary>
    private enum TokenKind
    {
        Number,
        Plot,
        Subsample,
        Value,
    }

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
        int? value = null;
        var hasKeywords = sequence.Any(t => t.Kind is not TokenKind.Number);

        if (hasKeywords)
        {
            // Labeled form: each keyword takes the next numeric token after it.
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
                else if (sequence[i].Kind == TokenKind.Value && value is null)
                {
                    value = NextNumberAfter(sequence, i);
                }
            }
        }
        else
        {
            // Bare form: the first up-to-3 numeric tokens in order are plot, subsample, value.
            var numbers = sequence.Where(t => t.Kind == TokenKind.Number).ToList();
            if (numbers.Count > 0) plot = numbers[0].Value;
            if (numbers.Count > 1) subsample = numbers[1].Value;
            if (numbers.Count > 2) value = numbers[2].Value;
        }

        var complete = plot.HasValue && subsample.HasValue && value.HasValue;
        var anyWordForm = sequence.Any(t => t.Kind == TokenKind.Number && t.WasWord);

        double confidence;
        if (complete)
        {
            confidence = anyWordForm ? 0.75 : (hasKeywords ? 0.95 : 0.85);
        }
        else if (plot.HasValue || subsample.HasValue || value.HasValue)
        {
            confidence = 0.5;
        }
        else
        {
            confidence = 0;
        }

        return new TranscriptParseResult
        {
            Plot = plot,
            Subsample = subsample,
            Value = value,
            Confidence = confidence,
        };
    }

    /// <summary>
    /// Lower-cases, strips commas (treated as plain separators) and splits on whitespace.
    /// </summary>
    private static List<string> Tokenize(string transcript) =>
        transcript.ToLowerInvariant()
            .Replace(',', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    /// <summary>
    /// Converts the raw token stream into a sequence of numbers and keywords, dropping
    /// noise. Consecutive digit-words bridged by an <c>"oh"</c> collapse left-to-right
    /// into one number (see type docs for the exact boundary).
    /// </summary>
    private static List<(TokenKind Kind, int Value, bool WasWord)> Normalize(List<string> tokens)
    {
        var result = new List<(TokenKind, int, bool)>();

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
                    result.Add((TokenKind.Number, int.Parse(digits.ToString(), CultureInfo.InvariantCulture), true));
                }
                else
                {
                    result.Add((TokenKind.Number, digit, true));
                    i++;
                }
                continue;
            }

            if (token == "plot")
            {
                result.Add((TokenKind.Plot, 0, false));
            }
            else if (token is "subsample" or "sub")
            {
                result.Add((TokenKind.Subsample, 0, false));
            }
            else if (token == "value")
            {
                result.Add((TokenKind.Value, 0, false));
            }
            else if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                result.Add((TokenKind.Number, number, false));
            }
            // Any other token is noise and dropped.

            i++;
        }

        return result;
    }

    /// <summary>
    /// Returns the value of the first numeric token at or after
    /// <paramref name="keywordIndex"/> + 1, or <see langword="null"/> when there is none.
    /// </summary>
    private static int? NextNumberAfter(List<(TokenKind Kind, int Value, bool WasWord)> sequence, int keywordIndex)
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