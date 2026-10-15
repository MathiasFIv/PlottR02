namespace PlottR0._2.Repository.Configuration;

/// <summary>
/// Binds the <c>Storage</c> configuration section that controls the local
/// write-through cache root used by the file-system stores. The durable copy of
/// every artifact lives in the S3-compatible object store (see
/// <see cref="ObjectStorageOptions"/>); the directory at <see cref="Root"/> is
/// only the on-box cache from which controllers stream files.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// The local cache root. The sentinel value <c>"Service"</c> (the default) means
    /// "walk up from the app base directory to the repo-root <c>Service/</c> directory",
    /// preserving the legacy behaviour used by tests and local development. Any other
    /// value is treated as an absolute or app-relative directory path.
    /// </summary>
    public string Root { get; set; } = "Service"; // "Service" is a sentinel = legacy repo-root Service/ walk
}
