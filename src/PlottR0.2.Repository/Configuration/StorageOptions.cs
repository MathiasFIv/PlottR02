namespace PlottR0._2.Repository.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root for locally cached files; the "Service" sentinel means the service directory.</summary>
    public string Root { get; set; } = "Service";
}
