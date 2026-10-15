namespace PlottR0._2.Repository.Configuration;

/// <summary>
/// Binds the <c>ObjectStorage</c> configuration section describing the durable
/// S3-compatible bucket that backs the local write-through cache. When
/// <see cref="Enabled"/> is <see langword="false"/> or <see cref="Provider"/> is
/// <c>"None"</c>, no object store is wired up and the file-system stores behave
/// exactly as before (local-only).
/// </summary>
public sealed class ObjectStorageOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "ObjectStorage";

    /// <summary>Whether durable object storage is active.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The provider label (e.g. <c>NeonObjectStorage</c>, <c>CloudflareR2</c>, <c>Tigris</c>).
    /// The value <c>"None"</c> (the default) disables durable storage even when
    /// <see cref="Enabled"/> is set.
    /// </summary>
    public string Provider { get; set; } = "None";

    /// <summary>The target bucket name.</summary>
    public string Bucket { get; set; } = "";

    /// <summary>The S3-compatible service endpoint URL.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>The signing region (may be a placeholder for path-style endpoints).</summary>
    public string Region { get; set; } = "";

    /// <summary>The S3 access key id.</summary>
    public string AccessKeyId { get; set; } = "";

    /// <summary>The S3 secret access key.</summary>
    public string SecretAccessKey { get; set; } = "";

    /// <summary>
    /// Whether to use path-style addressing (<c>endpoint/bucket/key</c>) rather than
    /// virtual-hosted-style (<c>bucket.endpoint/key</c>). Many S3-compatible providers require this.
    /// </summary>
    public bool ForcePathStyle { get; set; }
}
