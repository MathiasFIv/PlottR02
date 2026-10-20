using System.Globalization;
using System.Text.RegularExpressions;

namespace PlottR0._2.Repository.Storage;

internal static class ObjectKeys
{
    public const string OutputMarker = "_out_";

    // Millisecond stamp: the per-trial write gate serialises writers in-process, so a
    // same-millisecond successor only has to bump past the copy written an instant earlier
    // (SaveCopyTo additionally publishes the name with a no-clobber move, so a second process cannot
    // take it either). With the base capped at MaxOutputBaseLength the whole name is
    // 100 + 5 + 19 + 5 = 129 chars, safely under the 255-char NAME_MAX that applies to a single path
    // component on both Windows (NTFS) and Linux (ext4), and well inside Windows' 260-char MAX_PATH.
    public const int MaxOutputBaseLength = 100;

    public const string OutputStampFormat = "yyyyMMdd_HHmmss_fff";

    // Both stamp shapes must keep parsing: names written before millisecond precision was
    // introduced (and never migrated) carry the second-precision form.
    public static readonly string[] OutputStampFormats = ["yyyyMMdd_HHmmss", OutputStampFormat];

    // Strips only a TRAILING run of generated stamps, so a user file literally named
    // "my_out_file.xlsx" keeps its name while "my_out_file_out_20260101_010203_out_…" strips back
    // to "my_out_file".
    private static readonly Regex OutputChainPattern = new(
        @"(_out_\d{8}_\d{6}(?:_\d{3})?)+$", RegexOptions.CultureInvariant);

    public static bool IsOutputCopy(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName).Contains(OutputMarker, StringComparison.Ordinal);

    public static string WorkbookSourceKey(Guid trialId, string fileName) => $"Workbooks/{trialId}/{fileName}";

    public static string WorkbookOutputKey(Guid trialId, string fileName) => $"Working/{trialId}/{fileName}";

    public static string WorkbookOutputPrefix(Guid trialId) => $"Working/{trialId}/";

    public static string RecordingKey(Guid trialId, string fileName) => $"recordings/{trialId}/{fileName}";

    /// <summary>
    /// Removes every generated "_out_&lt;stamp&gt;" segment a previous write appended, leaving the
    /// stable base a new output copy is named from.
    /// </summary>
    public static string StripOutputChain(string nameWithoutExtension)
        => OutputChainPattern.Replace(nameWithoutExtension, string.Empty);

    /// <summary>
    /// Builds the bounded, unique output-copy name "&lt;base&gt;_out_&lt;yyyyMMdd_HHmmss_fff&gt;.xlsx"
    /// from the source file name: any existing chain is stripped, the base is truncated to
    /// <see cref="MaxOutputBaseLength"/> and the millisecond stamp keeps the name length-safe.
    /// </summary>
    public static string BuildOutputFileName(string sourceNameWithoutExtension, DateTime stamp)
    {
        var baseName = StripOutputChain(sourceNameWithoutExtension);
        if (baseName.Length > MaxOutputBaseLength)
        {
            baseName = baseName[..MaxOutputBaseLength];
        }

        // A source whose whole stem is a generated stamp strips to nothing; give it a readable stem
        // back so the name still carries the "_out_" discriminator instead of becoming one.
        if (baseName.Length == 0)
        {
            baseName = "workbook";
        }

        return baseName
            + OutputMarker
            + stamp.ToString(OutputStampFormat, CultureInfo.InvariantCulture)
            + ".xlsx";
    }
}
