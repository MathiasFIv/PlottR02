namespace PlottR0._2.Repository.Storage;

/// <summary>
/// Builds the durable object-store keys for the artifacts the file-system stores
/// persist. Keys are stable, forward-slash separated logical paths that encode the
/// trial id and file name, independent of the local cache root. Source workbooks and
/// accumulated sync output copies live in separate namespaces so rehydration can
/// distinguish the pristine upload from chained outputs.
/// </summary>
internal static class ObjectKeys
{
    /// <summary>The marker embedded in the file name of every accumulated sync output copy.</summary>
    public const string OutputMarker = "_out_";

    /// <summary>True when <paramref name="fileName"/> names an accumulated sync output copy.</summary>
    public static bool IsOutputCopy(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName).Contains(OutputMarker, StringComparison.Ordinal);

    /// <summary>The durable key for a trial's pristine uploaded workbook.</summary>
    public static string WorkbookSourceKey(Guid trialId, string fileName) => $"Workbooks/{trialId}/{fileName}";

    /// <summary>The durable key for a trial's accumulated sync output copy.</summary>
    public static string WorkbookOutputKey(Guid trialId, string fileName) => $"Working/{trialId}/{fileName}";

    /// <summary>The durable key prefix covering every output copy of a trial.</summary>
    public static string WorkbookOutputPrefix(Guid trialId) => $"Working/{trialId}/";

    /// <summary>The durable key for a trial's uploaded voice recording blob.</summary>
    public static string RecordingKey(Guid trialId, string fileName) => $"recordings/{trialId}/{fileName}";
}
