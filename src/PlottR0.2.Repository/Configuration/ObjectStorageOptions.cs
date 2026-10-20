namespace PlottR0._2.Repository.Configuration;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    public bool Enabled { get; set; }

    public string Provider { get; set; } = "None";

    public string Bucket { get; set; } = "";

    public string Endpoint { get; set; } = "";

    public string Region { get; set; } = "";

    public string AccessKeyId { get; set; } = "";

    public string SecretAccessKey { get; set; } = "";

    public bool ForcePathStyle { get; set; }
}
