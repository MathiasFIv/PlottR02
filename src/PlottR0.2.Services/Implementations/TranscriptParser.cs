using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlottR0._2.Contracts.Transcription;

namespace PlottR0._2.Services.Implementations;

public static class TranscriptParser
{
    // Mirrors RecordingSyncService.MaxRating (a drift test pins the two together): the highest
    // value the rating sheet accepts.
    public const int ValueCeiling = 10;

    private const int MaxSplitDigits = 9;

    private enum TokenKind
    {
        Number,
        Plot,
        Subsample,
        Value,
    }

    private static readonly IReadOnlyDictionary<string, TokenKind> KeywordAliases =
        new Dictionary<string, TokenKind>(StringComparer.Ordinal)
        {
            ["plot"] = TokenKind.Plot,
            ["plut"] = TokenKind.Plot,
            ["plod"] = TokenKind.Plot,
            ["plus"] = TokenKind.Plot,

            ["plug"] = TokenKind.Plot,
            ["clot"] = TokenKind.Plot,
            ["pluts"] = TokenKind.Plot,
            ["blot"] = TokenKind.Plot,

            ["subsample"] = TokenKind.Subsample,
            ["subsymbol"] = TokenKind.Subsample,
            ["subsampel"] = TokenKind.Subsample,
            ["subsamble"] = TokenKind.Subsample,

            ["subamble"] = TokenKind.Subsample,
            ["sub"] = TokenKind.Subsample,

            ["value"] = TokenKind.Value,
            ["values"] = TokenKind.Value,
            ["valeu"] = TokenKind.Value,
        };

    /// <summary>
    /// Best-effort parse of a transcript. A repaired parse is capped at 0.70 confidence — below
    /// the sync's 0.75 review threshold — so a glued-digit guess always needs review.
    /// </summary>
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

    // Repair heuristic for glued digits ("42" → 4,2; "060" → 6,0): only 3+ digit tokens that are
    // out of range or carry a leading zero. Two-digit tokens are never split (ratings legitimately
    // reach 10) and the raw text is kept, so a leading zero is not lost.
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

    // Splits on non-alphanumerics and on letter/digit boundaries, so "plot-101-subsample-3" and
    // the glued "plot104" both tokenize cleanly. Without the boundary rules a hyphen-joined phrase
    // survives as one token, matches nothing, and is dropped as noise.
    private static readonly Regex TokenSplitRegex = new(
        @"[^a-z0-9]+|(?<=[a-z])(?=[0-9])|(?<=[0-9])(?=[a-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static List<string> Tokenize(string transcript) =>
        TokenSplitRegex.Split(transcript.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();

    private static List<(TokenKind Kind, int Value, bool WasWord, string Text)> Normalize(List<string> tokens)
    {
        var result = new List<(TokenKind, int, bool, string)>();

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

            i++;
        }

        return result;
    }

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